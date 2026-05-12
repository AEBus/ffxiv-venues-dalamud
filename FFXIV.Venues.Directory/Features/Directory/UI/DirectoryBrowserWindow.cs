using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFXIV.Venues.Directory.Features.Directory.Filters;
using FFXIV.Venues.Directory.Features.Directory.Media;
using FFXIV.Venues.Directory.Infrastructure;
using FFXIV.Venues.Directory.Integrations;
using FFXIV.Venues.Directory.Features.Directory.Domain;
using TimeZoneConverter;

namespace FFXIV.Venues.Directory.Features.Directory.Ui;

internal sealed partial class DirectoryBrowserWindow : Window, IDisposable
{
    private enum PostPreparationActivationStage
    {
        None,
        FinalizeState,
        ListWarmup,
        DetailWarmup
    }

    private enum DescriptionLineKind
    {
        Paragraph,
        Heading,
        Metadata,
        Lineup,
        Link,
        Banner
    }

    private sealed record VenueRouteOption(string DisplayText, string CopyText, string? LifestreamArguments);
    private sealed record PreparedDescriptionSegment(string Text, string? Url);
    private sealed record PreparedDescriptionLine(PreparedDescriptionSegment[] Segments, bool IsBlank);
    private sealed record PreparedScheduleRow(string Label, string TimeRange, bool IsActive);
    private sealed record PreparedVenue(
        DirectoryVenue Source,
        string Id,
        string DisplayName,
        string SearchText,
        HashSet<string> Tags,
        string? Region,
        string? DataCenter,
        string? World,
        bool IsOpen,
        bool IsNsfw,
        HousingPlotSize? PlotSize,
        bool IsApartment,
        string VenueTypeLabel,
        int VenueTypeSortKey,
        string LocationSortKey,
        string TableAddress,
        string DetailedAddress,
        string StatusLine,
        DateTimeOffset StatusSortKey,
        VenueRouteOption[] RouteOptions,
        PreparedDescriptionLine[] DescriptionLines,
        PreparedScheduleRow[] ScheduleRows,
        string? ResolutionSummary,
        string? WarningText);
    private sealed record PreparedVenueDetails(
        VenueRouteOption[] RouteOptions,
        PreparedDescriptionLine[] DescriptionLines,
        PreparedScheduleRow[] ScheduleRows,
        string? ResolutionSummary,
        bool SchedulePending);
    private readonly record struct ScheduleTimeZoneContext(string Id, TimeZoneInfo TimeZone, DateTime SourceNow);
    private readonly record struct SortSpecSnapshot(int ColumnIndex, ImGuiSortDirection Direction);

    private const float BannerMaxWidth = 520f;
    private const float NarrowLayoutBreakpointWidth = 1280f;
    private const float NarrowDetailDrawerWidth = 624f;
    private const float NarrowDetailBannerWidth = 600f;
    private const float NarrowDetailDrawerTopInset = 48f;
    private const float NarrowSelectedVenueActionOffsetX = 328f;
    private const float NarrowSelectedVenueActionWidth = 126f;
    private const float NarrowSelectedVenueActionYOffset = -29f;
    private const float MinPanelWidth = 320f;
    private const float FilterSidebarFixedWidth = 352f;
    private const string PluginTimeFormat = "HH:mm";
    private static readonly TimeSpan PreparedVenueBuildTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] Regions = { "North America", "Europe", "Oceania", "Japan" };
    private static readonly Vector4 DefaultSectionBackground = new(0.20f, 0.20f, 0.23f, 0.95f);
    private static readonly Vector4 HighlightSectionBackground = new(0.12f, 0.12f, 0.12f, 0.95f);
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLinkRegex = new(@"\[(.*?)\]\((.*?)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownStrongRegex = new(@"(\*\*|__|~~)(.*?)\1", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownEmRegex = new(@"(\*|_)(.*?)\1", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ColonEmojiRegex = new(@":[A-Za-z0-9_+\-]+:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlRegex = new(@"https?://[^\s\)\]\}<>\""]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex OnlyPunctuationLineRegex = new(@"^[\p{P}\p{S}]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeadingEqualsRegex = new(@"^\s*=+\s*(.*?)\s*=+\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeadingSingleEqualsRegex = new(@"^\s*=\s*(.*?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingBulletRegex = new(@"^\s*[-*•▪◦·]+\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RawUrlOnlyLineRegex = new(@"^\s*https?://[^\s]+\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LongDiscordChannelUrlRegex = new(@"discord(?:app)?\.com/channels/", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DateLineRegex = new(@"^(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b|^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TimeAnnouncementRegex = new(@"^(?:Starts?|Open(?:ing)?(?:\s+hours?)?|Doors?)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LocationLineRegex = new(@"\b(?:Ward\s*\d+.*(?:Plot|Apartment|Apt)\s*\d+|Plot\s*\d+\b|Apartment\s*\d+\b|Empyreum|Lavender Beds|Goblet|Mist|Shirogane)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LineupLineRegex = new(@"^(?:slot\s*\d+|dj\s*(?:set|lineup|line-up)|lineup)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SectionHeadingRegex = new(@"^[A-Za-z0-9 '&/+.-]{2,40}(?:[:?!])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RecurringDayRegex = new(@"\bevery\s+(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|weekday|weekdays|weekend|weekends|daily|nightly)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ExternalActionLeadRegex = new(@"^(?:discord|website|site|carrd|partake|promo(?:tional)?\s+video|video|trailer|twitch|youtube|linktree|socials?)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PromoCtaLeadRegex = new(@"^(?:join|visit|come|grab|check|watch|read|follow|book|discover|explore|experience|learn|find|want|step|enter|dive)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ShortTaglineLeadRegex = new(@"^(?:welcome(?:\s+to)?|feel|enjoy|celebrate|party|dance|relax|indulge)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingDecorationRegex = new(@"^[\p{P}\p{S}\s]+(?=[\p{L}\p{N}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingDecorationRegex = new(@"(?<=[\p{L}\p{N}])[\p{P}\p{S}\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OverrideRouteRegex = new(
        @"(?:(?<label>[A-Za-z0-9'&()\- ]{2,40}):\s*)?(?<address>[A-Za-z][A-Za-z0-9'’\- ]+(?:,\s*[A-Za-z][A-Za-z0-9'’\- ]+){1,2},\s*Ward\s*\d+(?:\s*(?:Sub|Subdivision))?\s*,\s*(?:Plot|Apartment|Apt)\s*\d+(?:,\s*(?:Sub|Subdivision))?(?:,\s*Room\s*\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<int, char> SmallCapsMap = new()
    {
        { 0x1D00, 'A' },
        { 0x0299, 'B' },
        { 0x1D04, 'C' },
        { 0x1D05, 'D' },
        { 0x1D07, 'E' },
        { 0xA730, 'F' },
        { 0x0262, 'G' },
        { 0x029C, 'H' },
        { 0x026A, 'I' },
        { 0x1D0A, 'J' },
        { 0x1D0B, 'K' },
        { 0x029F, 'L' },
        { 0x1D0D, 'M' },
        { 0x0274, 'N' },
        { 0x1D0F, 'O' },
        { 0x1D18, 'P' },
        { 0x01EB, 'Q' },
        { 0x0280, 'R' },
        { 0x1D1B, 'T' },
        { 0x1D1C, 'U' },
        { 0x1D20, 'V' },
        { 0x1D21, 'W' },
        { 0x028F, 'Y' },
        { 0x1D22, 'Z' },
    };
    private static readonly Dictionary<int, char> SpecialMap = new()
    {
        { 0x210E, 'h' },
        { 0x2113, 'l' },
        { 0x1D70A, 'o' },
        { 0x1D70B, 'o' },
        { 0x1D710, 'u' },
    };
    private static readonly Dictionary<string, string> TimeZoneAbbreviationMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Coordinated Universal Time", "UTC" },
            { "Greenwich Mean Time", "GMT" },
            { "Eastern Standard Time", "EST" },
            { "Eastern Daylight Time", "EDT" },
            { "Central Standard Time", "CST" },
            { "Central Daylight Time", "CDT" },
            { "Mountain Standard Time", "MST" },
            { "Mountain Daylight Time", "MDT" },
            { "Pacific Standard Time", "PST" },
            { "Pacific Daylight Time", "PDT" },
            { "Alaskan Standard Time", "AKST" },
            { "Alaskan Daylight Time", "AKDT" },
            { "Hawaiian Standard Time", "HST" },
            { "Atlantic Standard Time", "AST" },
            { "Atlantic Daylight Time", "ADT" },
            { "Greenwich Standard Time", "GMT" },
            { "GMT Standard Time", "GMT" },
            { "GMT Daylight Time", "GMT" },
            { "Central European Standard Time", "CET" },
            { "Central European Summer Time", "CEST" },
            { "W. Europe Standard Time", "CET" },
            { "W. Europe Daylight Time", "CEST" },
            { "E. Europe Standard Time", "EET" },
            { "E. Europe Daylight Time", "EEST" },
            { "Russian Standard Time", "MSK" },
            { "Japan Standard Time", "JST" },
            { "AUS Eastern Standard Time", "AEST" },
            { "AUS Eastern Daylight Time", "AEDT" },
            { "Cen. Australia Standard Time", "ACST" },
            { "Cen. Australia Daylight Time", "ACDT" },
            { "W. Australia Standard Time", "AWST" },
            { "New Zealand Standard Time", "NZST" },
            { "New Zealand Daylight Time", "NZDT" }
        };
    private static readonly Dictionary<int, string> ProblemSymbolMap = new()
    {
        { 0x2728, "*" }, // ✨
        { 0x1F48B, "•" }, // 💋
        { 0x1F3B6, "♪" }, // 🎶
        { 0x1F3A7, "♪" }, // 🎧
        { 0x1F378, "*" }, // 🍸
        { 0x1F376, "*" }, // 🍶
        { 0x1F3B2, "•" }, // 🎲
        { 0x1F3AD, "*" }, // 🎭
        { 0x1F389, "*" }, // 🎉
        { 0x1F525, "*" }, // 🔥
        { 0x1F319, "*" }, // 🌙
        { 0x1F31F, "*" }, // 🌟
        { 0x2B50, "*" }, // ⭐
        { 0x2605, "*" }, // ★
        { 0x2606, "*" }, // ☆
        { 0x1F338, "*" }, // 🌸
        { 0x1F940, "*" }, // 🥀
        { 0x2740, "*" }, // ❀
        { 0x1F49C, "♥" }, // 💜
        { 0x1F49B, "♥" }, // 💛
        { 0x1F5A4, "♥" }, // 🖤
        { 0x2764, "♥" }, // ❤
        { 0x2661, "♥" }, // ♡
        { 0x1F4CD, "•" }, // 📍
        { 0x1F517, "->" }, // 🔗
        { 0x1F4AC, "" }, // 💬
        { 0xFE0F, "" }, // VS16
        { 0x200D, "" }, // ZWJ
        { 0x2060, "" } // WORD JOINER
    };
    private static readonly Dictionary<string, TimeZoneInfo?> TimeZoneInfoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TimeZoneInfoCacheLock = new();

    private readonly HttpClient _httpClient;
    private readonly VenueBannerCache _venueService;
    private readonly Configuration _configuration;
    private readonly LifestreamNavigator _lifestreamIpc;
    private readonly PlotSizeLookup _housingPlotSizeResolver;
    private readonly CancellationTokenSource _disposeCts = new();

    private Task<DirectoryVenue[]?>? _venuesTask;
    private Task<PreparedVenue[]>? _preparedVenuesTask;
    private DirectoryVenue[]? _venues;
    private PreparedVenue[]? _preparedVenues;
    private string? _loadError;
    private DateTimeOffset _lastRefresh;
    private DateTimeOffset _preparedVenueTaskStartedAtUtc;
    private int _venueRefreshVersion;
    private int _preparedVenueTaskVersion;

    private string? _selectedVenueId;
    private string _searchText = string.Empty;
    private string _tagFilter = string.Empty;
    private string? _selectedRegion;
    private string? _selectedDataCenter;
    private string? _selectedWorld;
    private bool _onlyOpen = true;
    private bool _favoritesOnly;
    private bool _visitedOnly;
    private bool _sfwOnly;
    private bool _nsfwOnly;
    private bool _sizeSmall = true;
    private bool _sizeMedium = true;
    private bool _sizeLarge = true;
    private bool _sizeApartment = true;

    private readonly List<string> _dataCenters = new();
    private readonly List<string> _regions = new();
    private readonly List<string> _worlds = new();
    private readonly HashSet<string> _favoriteVenueIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visitedVenueIds = new(StringComparer.Ordinal);
    private readonly List<PreparedVenue> _filteredVenues = new();
    private readonly List<PreparedVenue> _sortedVenues = new();
    private readonly Dictionary<string, PreparedVenueDetails> _preparedVenueDetailsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<PreparedVenueDetails>> _preparedVenueDetailTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<PreparedScheduleRow[]>> _preparedVenueScheduleTasks = new(StringComparer.Ordinal);
    private readonly List<SortSpecSnapshot> _sortSpecSnapshots = new();
    private readonly List<float> _sortedVenueRowHeights = new();
    private readonly List<float> _sortedVenueRowOffsets = new();
    private float _splitRatio = 0.42f;
    private float _rightPaneWidth;
    private float _sortedVenueColumnMetricsWrapWidth = -1f;
    private float _sortedVenueRowMetricsWrapWidth = -1f;
    private float _sortedVenueTotalHeight;
    private bool _showNarrowDetailPane;
    private bool _isNarrowDetailDrawerActive;
    private bool _wasNarrowLayoutLastDraw;
    private bool _filteredVenuesDirty = true;
    private bool _sortedVenuesDirty = true;
    private bool _sortedVenueRowMetricsDirty = true;
    private readonly Dictionary<string, int> _selectedRouteIndices = new(StringComparer.Ordinal);
    private PostPreparationActivationStage _postPreparationActivationStage;
    private bool _disposed;

    public DirectoryBrowserWindow(
        HttpClient httpClient,
        VenueBannerCache venueService,
        Configuration configuration,
        LifestreamNavigator lifestreamIpc,
        PlotSizeLookup housingPlotSizeResolver)
        : base("FFXIV Venues Directory")
    {
        _httpClient = httpClient;
        _venueService = venueService;
        _configuration = configuration;
        _lifestreamIpc = lifestreamIpc;
        _housingPlotSizeResolver = housingPlotSizeResolver;
        EnsurePreferenceCollectionsInitialized();
        SyncPreferredVenueLookups();

        Size = new Vector2(1280f, 720f);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1280f, 720f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _venuesTask = null;
        _preparedVenuesTask = null;
        _preparedVenueDetailTasks.Clear();
        _preparedVenueScheduleTasks.Clear();
        _disposeCts.Dispose();
    }

    public override void Draw()
    {
        if (_disposed)
        {
            return;
        }

        var activationStage = _postPreparationActivationStage;
        try
        {
            EnsureVenuesRequested();
            TryConsumeVenueTask();
            var consumedPreparedTask = TryConsumePreparedVenueTask();
            if (_venues == null || _preparedVenues == null)
            {
                DrawLoadingState();
                return;
            }

            if (consumedPreparedTask)
            {
                DrawActivationPlaceholder();
                return;
            }

            RefreshFilteredVenuesIfNeeded();
            EnsureSelection(_filteredVenues);
            var selectedVenue = GetSelectedPreparedVenue();

            if (activationStage == PostPreparationActivationStage.FinalizeState)
            {
                DrawActivationPlaceholder();
                return;
            }

            var region = ImGui.GetContentRegionAvail();
            var isNarrowLayout = region.X <= Scale(NarrowLayoutBreakpointWidth);
            if (isNarrowLayout && !_wasNarrowLayoutLastDraw)
            {
                _showNarrowDetailPane = false;
            }

            _wasNarrowLayoutLastDraw = isNarrowLayout;
            if (selectedVenue == null)
            {
                _showNarrowDetailPane = false;
            }

            var splitterWidth = Math.Max(Scale(4f), ImGui.GetStyle().ItemSpacing.X);
            var minPanelWidth = Scale(MinPanelWidth);
            var maxBannerWidth = Scale(BannerMaxWidth);
            var narrowDrawerWidth = Scale(NarrowDetailDrawerWidth);
            var splitterCount = isNarrowLayout ? 0f : 1f;
            var minimumContentWidth = isNarrowLayout
                ? minPanelWidth
                : minPanelWidth * 2f;
            var usableWidth = Math.Max(region.X - (splitterWidth * splitterCount), minimumContentWidth);
            var preferredSidebarWidth = Scale(FilterSidebarFixedWidth);
            var sidebarMaxWidth = MathF.Max(0f, usableWidth - minimumContentWidth);
            var sidebarWidth = MathF.Min(preferredSidebarWidth, sidebarMaxWidth);
            var contentWidth = MathF.Max(minimumContentWidth, usableWidth - sidebarWidth);
            var leftWidth = contentWidth;
            var rightWidth = isNarrowLayout && _showNarrowDetailPane ? narrowDrawerWidth : 0f;
            if (!isNarrowLayout)
            {
                leftWidth = Math.Clamp(contentWidth * _splitRatio, minPanelWidth, contentWidth - minPanelWidth);
                rightWidth = contentWidth - leftWidth;
                if (rightWidth > maxBannerWidth)
                {
                    rightWidth = maxBannerWidth;
                    leftWidth = contentWidth - rightWidth;
                    _splitRatio = leftWidth / contentWidth;
                }
            }

            _isNarrowDetailDrawerActive = isNarrowLayout && _showNarrowDetailPane;
            _rightPaneWidth = _isNarrowDetailDrawerActive ? rightWidth : (!isNarrowLayout ? rightWidth : 0f);

            using (var filterPane = ImRaii.Child("VenueFilterPane"u8, new Vector2(sidebarWidth, 0f), true))
            {
                if (filterPane)
                {
                    DrawToolbar(_filteredVenues.Count);
                    if (activationStage != PostPreparationActivationStage.None)
                    {
                        DrawActivationPlaceholder();
                    }
                    else
                    {
                        DrawFilters();
                    }
                }
            }

            RefreshFilteredVenuesIfNeeded();

            ImGui.SameLine(0f, 0f);
            Vector2 listPaneMin = default;
            Vector2 listPaneMax = default;
            using (var listPane = ImRaii.Child(
                       "VenueListPane"u8,
                       new Vector2(leftWidth, 0f),
                       true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (listPane)
                {
                    if (isNarrowLayout && selectedVenue != null)
                    {
                        DrawNarrowSelectedVenueStrip(selectedVenue);
                        ImGui.Separator();
                    }

                    DrawVenueTable(_filteredVenues);
                    if (isNarrowLayout &&
                        selectedVenue != null &&
                        !_showNarrowDetailPane &&
                        ImGui.IsWindowFocused() &&
                        ImGui.IsKeyPressed(ImGuiKey.Enter, false))
                    {
                        OpenNarrowDetailPane(selectedVenue.Id);
                    }
                }
            }

            listPaneMin = ImGui.GetItemRectMin();
            listPaneMax = ImGui.GetItemRectMax();

            selectedVenue = GetSelectedPreparedVenue();
            if (isNarrowLayout && _showNarrowDetailPane)
            {
                var overlayTopInset = Scale(NarrowDetailDrawerTopInset);
                var overlayHeight = MathF.Max(0f, listPaneMax.Y - (listPaneMin.Y + overlayTopInset));
                var overlayPos = new Vector2(listPaneMax.X - rightWidth, listPaneMin.Y + overlayTopInset);
                var restoreCursor = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(overlayPos);
                using var overlayColors = ImRaii.PushColor(ImGuiCol.ChildBg, UiStyle.DrawerOverlayBackground);
                using (var detailDrawer = ImRaii.Child("VenueDetailDrawer"u8, new Vector2(rightWidth, overlayHeight), true))
                {
                    if (detailDrawer)
                    {
                        if (selectedVenue == null)
                        {
                            DrawText(GetEmptySelectionMessage(), UiStyle.WarningText);
                        }
                        else if (activationStage != PostPreparationActivationStage.None)
                        {
                            ImGui.TextDisabled("Loading venue details...");
                        }
                        else
                        {
                            DrawVenueDetails(selectedVenue, activationStage == PostPreparationActivationStage.None);
                        }
                    }
                }

                ImGui.SetCursorScreenPos(restoreCursor);
            }
            else if (!isNarrowLayout)
            {
                ImGui.SameLine(0f, 0f);
                ImGui.InvisibleButton("##VenueListSplitter", new Vector2(splitterWidth, region.Y));
                var listSplitterMin = ImGui.GetItemRectMin();
                var listSplitterMax = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddRectFilled(listSplitterMin, listSplitterMax, ImGui.GetColorU32(ImGuiCol.Border));
                if (ImGui.IsItemActive())
                {
                    var newLeft = Math.Clamp(leftWidth + ImGui.GetIO().MouseDelta.X, minPanelWidth, contentWidth - minPanelWidth);
                    _splitRatio = Math.Clamp(newLeft / contentWidth, 0.15f, 0.85f);
                }

                ImGui.SameLine(0f, 0f);
                using (var detailPane = ImRaii.Child("VenueDetailPane"u8, new Vector2(rightWidth, 0f), true))
                {
                    if (detailPane)
                    {
                        if (selectedVenue == null)
                        {
                            DrawText(GetEmptySelectionMessage(), UiStyle.WarningText);
                        }
                        else if (activationStage != PostPreparationActivationStage.None)
                        {
                            ImGui.TextDisabled("Loading venue details...");
                        }
                        else
                        {
                            DrawVenueDetails(selectedVenue, activationStage == PostPreparationActivationStage.None);
                        }
                    }
                }
            }
        }
        finally
        {
            if (activationStage != PostPreparationActivationStage.None)
            {
                AdvancePostPreparationActivationStage();
            }
        }
    }

    private void OpenNarrowDetailPane(string venueId)
    {
        _selectedVenueId = venueId;
        _showNarrowDetailPane = true;
    }

    private void DrawNarrowSelectedVenueStrip(PreparedVenue venue)
    {
        var actionIcon = _showNarrowDetailPane ? FontAwesomeIcon.Times : FontAwesomeIcon.Eye;
        var actionLabel = _showNarrowDetailPane ? "Close details" : "Open details";
        var actionTone = _showNarrowDetailPane ? UiButtonTone.Secondary : UiButtonTone.Primary;
        var actionSize = MeasureActionButtonSize(actionIcon, actionLabel);
        var fullAddress = GetNarrowSelectedVenueFullAddress(venue);

        using (ImRaii.Group())
        {
            DrawSectionLabel("Selected venue");
            ImGuiHelpers.ScaledDummy(0f, 2f);

            var titleStart = ImGui.GetCursorPos();
            var actionX = titleStart.X + Scale(NarrowSelectedVenueActionOffsetX);
            var titleWrapX = MathF.Max(titleStart.X, actionX - UiStyle.InlineGroupSpacing);
            DrawDisplayTitleText(venue.DisplayName, titleWrapX);

            var titleBottomY = ImGui.GetCursorPosY();
            ImGui.SetCursorPos(new Vector2(actionX, titleStart.Y + Scale(NarrowSelectedVenueActionYOffset)));
            if (_showNarrowDetailPane)
            {
                if (DrawActionButton(actionIcon, actionLabel, actionTone, Scale(NarrowSelectedVenueActionWidth), Scale(2f)))
                {
                    _showNarrowDetailPane = false;
                }
            }
            else if (DrawActionButton(actionIcon, actionLabel, actionTone, Scale(NarrowSelectedVenueActionWidth)))
            {
                OpenNarrowDetailPane(venue.Id);
            }

            ImGui.SetCursorPosY(MathF.Max(titleBottomY, titleStart.Y + actionSize.Y));
            if (!string.IsNullOrWhiteSpace(fullAddress))
            {
                ImGuiHelpers.ScaledDummy(0f, 2f);
                DrawTextWrapped(fullAddress, UiStyle.BodyMutedText);
            }

            if (!string.IsNullOrWhiteSpace(venue.VenueTypeLabel) || venue.IsNsfw)
            {
                ImGuiHelpers.ScaledDummy(0f, 2f);
                DrawNarrowSelectedVenueMetaChips(venue);
            }
        }
    }

    private static string GetNarrowSelectedVenueFullAddress(PreparedVenue venue) =>
        !string.IsNullOrWhiteSpace(venue.DetailedAddress)
            ? venue.DetailedAddress
            : venue.TableAddress;

    private static void DrawNarrowSelectedVenueMetaChips(PreparedVenue venue)
    {
        var hasPreviousChip = false;
        if (!string.IsNullOrWhiteSpace(venue.VenueTypeLabel))
        {
            DrawSizeBadge(venue.VenueTypeLabel, $"selected_strip_size_{venue.Id}");
            hasPreviousChip = true;
        }

        if (!venue.IsNsfw)
        {
            return;
        }

        if (hasPreviousChip)
        {
            ImGui.SameLine(0f, UiStyle.InlineSpacing);
        }

        var nsfwText = "NSFW";
        var nsfwSize = new Vector2(
            ImGui.CalcTextSize(nsfwText).X + UiStyle.ChipPadding.X * 2f,
            UiStyle.BadgeSide);
        DrawStaticChip($"selected_strip_nsfw_{venue.Id}", nsfwText, UiChipTone.Warning, nsfwSize, centerText: true);
    }

    private static void DrawActivationPlaceholder() => ImGui.Text("Activating venue list...");

    private void AdvancePostPreparationActivationStage()
    {
        _postPreparationActivationStage = _postPreparationActivationStage switch
        {
            PostPreparationActivationStage.FinalizeState => PostPreparationActivationStage.ListWarmup,
            PostPreparationActivationStage.ListWarmup => PostPreparationActivationStage.DetailWarmup,
            PostPreparationActivationStage.DetailWarmup => PostPreparationActivationStage.None,
            _ => PostPreparationActivationStage.None
        };
    }

    private void EnsureVenuesRequested()
    {
        if (_venuesTask == null && _venues == null && _loadError == null)
        {
            TriggerRefresh();
        }
    }

    private void TriggerRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _venueRefreshVersion++;
        _venuesTask = _httpClient.GetFromJsonAsync<DirectoryVenue[]>("venue?approved=true", _disposeCts.Token);
        _preparedVenuesTask = null;
        _loadError = null;
        _venues = null;
        _preparedVenues = null;
        _preparedVenueTaskStartedAtUtc = default;
        _filteredVenues.Clear();
        _sortedVenues.Clear();
        _preparedVenueDetailsCache.Clear();
        _preparedVenueDetailTasks.Clear();
        _preparedVenueScheduleTasks.Clear();
        _selectedVenueId = null;
        _selectedRouteIndices.Clear();
        _postPreparationActivationStage = PostPreparationActivationStage.None;
        InvalidateFilteredVenues();
    }

    private void TryConsumeVenueTask()
    {
        if (_venuesTask == null || !_venuesTask.IsCompleted)
        {
            return;
        }

        if (_venuesTask.IsCanceled)
        {
            _venuesTask = null;
            return;
        }

        if (_venuesTask.IsFaulted)
        {
            _loadError = _venuesTask.Exception?.GetBaseException().Message ?? "Failed to load venues.";
            _venuesTask = null;
            return;
        }

        _venues = _venuesTask.Result ?? Array.Empty<DirectoryVenue>();
        _venuesTask = null;
        _loadError = null;
        _lastRefresh = DateTimeOffset.UtcNow;

        _dataCenters.Clear();
        _dataCenters.AddRange(_venues
            .Select(v => v.Location?.DataCenter)
            .Where(dc => !string.IsNullOrWhiteSpace(dc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dc => dc, StringComparer.OrdinalIgnoreCase)!);

        _regions.Clear();
        _regions.AddRange(Regions);

        UpdateWorldOptions();
        _housingPlotSizeResolver.WarmUp();

        var venues = _venues;
        var refreshVersion = _venueRefreshVersion;
        _preparedVenueTaskVersion = refreshVersion;
        _preparedVenueTaskStartedAtUtc = DateTimeOffset.UtcNow;
        var cancellationToken = _disposeCts.Token;
        _preparedVenuesTask = Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = BuildPreparedVenues(venues);
                cancellationToken.ThrowIfCancellationRequested();
                return prepared;
            },
            cancellationToken,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private bool TryConsumePreparedVenueTask()
    {
        if (_preparedVenuesTask == null || !_preparedVenuesTask.IsCompleted)
        {
            if (_preparedVenuesTask != null &&
                _venues != null &&
                _preparedVenueTaskStartedAtUtc != default &&
                DateTimeOffset.UtcNow - _preparedVenueTaskStartedAtUtc >= PreparedVenueBuildTimeout)
            {
                DalamudServices.PluginLog.Warning(
                    "[DirectoryBrowserWindow] Venue preparation exceeded {TimeoutSeconds}s. Falling back to lightweight prepared venue data.",
                    PreparedVenueBuildTimeout.TotalSeconds);

                _preparedVenues = BuildPreparedVenuesLightweight(_venues);
                _preparedVenuesTask = null;
                _preparedVenueTaskStartedAtUtc = default;
                _selectedVenueId = _preparedVenues.FirstOrDefault()?.Id;
                _postPreparationActivationStage = PostPreparationActivationStage.FinalizeState;
                InvalidateFilteredVenues();
                return true;
            }

            return false;
        }

        if (_preparedVenuesTask.IsCanceled)
        {
            _preparedVenuesTask = null;
            _preparedVenueTaskStartedAtUtc = default;
            return false;
        }

        if (_preparedVenuesTask.IsFaulted)
        {
            _loadError = _preparedVenuesTask.Exception?.GetBaseException().Message ?? "Failed to prepare venues.";
            _preparedVenuesTask = null;
            _preparedVenueTaskStartedAtUtc = default;
            return false;
        }

        if (_preparedVenueTaskVersion != _venueRefreshVersion)
        {
            _preparedVenuesTask = null;
            _preparedVenueTaskStartedAtUtc = default;
            return false;
        }

        _preparedVenues = _preparedVenuesTask.Result ?? Array.Empty<PreparedVenue>();
        _preparedVenuesTask = null;
        _preparedVenueTaskStartedAtUtc = default;
        _selectedVenueId = _preparedVenues.FirstOrDefault()?.Id;
        _postPreparationActivationStage = PostPreparationActivationStage.FinalizeState;
        InvalidateFilteredVenues();

        return true;
    }

    private void DrawLoadingState()
    {
        if (!string.IsNullOrEmpty(_loadError))
        {
            DrawText(_loadError, UiStyle.ErrorText);
            if (ImGui.Button("Retry"))
            {
                TriggerRefresh();
            }

            return;
        }

        if (_preparedVenuesTask != null)
        {
            ImGui.Text("Preparing venues...");
            return;
        }

        ImGui.Text("Loading venues...");
    }

    private bool TryGetPreparedVenueDetails(
        PreparedVenue venue,
        out PreparedVenueDetails details,
        out bool cacheHit,
        out bool buildPending)
    {
        if (_preparedVenueDetailsCache.TryGetValue(venue.Id, out var cachedDetails))
        {
            if (TryConsumePreparedVenueScheduleTask(venue.Id, cachedDetails, out var completedDetails))
            {
                cachedDetails = completedDetails;
                _preparedVenueDetailsCache[venue.Id] = cachedDetails;
            }
            else if (cachedDetails.SchedulePending && !_preparedVenueScheduleTasks.ContainsKey(venue.Id))
            {
                EnsurePreparedVenueScheduleBuildStarted(venue);
            }

            details = cachedDetails;
            cacheHit = true;
            buildPending = false;
            return true;
        }

        if (_preparedVenueDetailTasks.TryGetValue(venue.Id, out var task))
        {
            if (!task.IsCompleted)
            {
                details = default!;
                cacheHit = false;
                buildPending = true;
                return false;
            }

            _preparedVenueDetailTasks.Remove(venue.Id);
            if (task.IsFaulted || task.IsCanceled)
            {
                DalamudServices.PluginLog.Warning(
                    task.Exception?.GetBaseException(),
                    "[DirectoryBrowserWindow] Failed to build prepared venue details for {VenueId}. Using fallback detail view.",
                    venue.Id);

                details = CreateFallbackPreparedVenueDetails(venue);
                _preparedVenueDetailsCache[venue.Id] = details;
                cacheHit = false;
                buildPending = false;
                return true;
            }

            details = task.Result;
            _preparedVenueDetailsCache[venue.Id] = details;
            cacheHit = false;
            buildPending = false;
            return true;
        }

        EnsurePreparedVenueDetailBuildStarted(venue);
        details = default!;
        cacheHit = false;
        buildPending = true;
        return false;
    }

    private bool TryConsumePreparedVenueScheduleTask(
        string venueId,
        PreparedVenueDetails details,
        out PreparedVenueDetails updatedDetails)
    {
        updatedDetails = details;
        if (!_preparedVenueScheduleTasks.TryGetValue(venueId, out var task) || !task.IsCompleted)
        {
            return false;
        }

        _preparedVenueScheduleTasks.Remove(venueId);
        if (task.IsFaulted || task.IsCanceled)
        {
            DalamudServices.PluginLog.Warning(
                task.Exception?.GetBaseException(),
                "[DirectoryBrowserWindow] Failed to build prepared venue schedule for {VenueId}.",
                venueId);

            updatedDetails = details with
            {
                ScheduleRows = Array.Empty<PreparedScheduleRow>(),
                SchedulePending = false
            };
            return true;
        }

        updatedDetails = details with
        {
            ScheduleRows = task.Result,
            SchedulePending = false
        };
        return true;
    }

    private void EnsurePreparedVenueDetailBuildStarted(PreparedVenue venue)
    {
        if (_preparedVenueDetailsCache.ContainsKey(venue.Id) || _preparedVenueDetailTasks.ContainsKey(venue.Id))
        {
            return;
        }

        var cancellationToken = _disposeCts.Token;
        _preparedVenueDetailTasks[venue.Id] = Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch
                {
                    // Best effort only; some hosts may reject thread priority changes.
                }

                var details = BuildPreparedVenueDetails(venue);
                cancellationToken.ThrowIfCancellationRequested();
                return details;
            },
            cancellationToken,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void EnsurePreparedVenueScheduleBuildStarted(PreparedVenue venue)
    {
        if (_preparedVenueScheduleTasks.ContainsKey(venue.Id))
        {
            return;
        }

        var cancellationToken = _disposeCts.Token;
        _preparedVenueScheduleTasks[venue.Id] = Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch
                {
                    // Best effort only; some hosts may reject thread priority changes.
                }

                var rows = BuildPreparedScheduleRows(venue.Source.Schedule);
                cancellationToken.ThrowIfCancellationRequested();
                return rows;
            },
            cancellationToken,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static PreparedVenueDetails BuildPreparedVenueDetails(PreparedVenue venue)
    {
        VenueRouteOption[] routeOptions = Array.Empty<VenueRouteOption>();
        string descriptionText = string.Empty;
        string sanitizedDescription = string.Empty;
        string compactedDescription = string.Empty;
        PreparedDescriptionLine[] descriptionLines = Array.Empty<PreparedDescriptionLine>();
        string? resolutionSummary = null;
        var hasDeferredSchedule = HasScheduleEntries(venue.Source.Schedule);

        routeOptions = BuildRouteOptions(venue.Source).ToArray();
        descriptionText = JoinNonEmptyLines(venue.Source.Description);
        sanitizedDescription = SanitizeDescription(descriptionText);
        compactedDescription = CompactDescriptionForDalamud(sanitizedDescription);
        descriptionLines = string.IsNullOrWhiteSpace(compactedDescription)
            ? Array.Empty<PreparedDescriptionLine>()
            : PrepareDescriptionLines(compactedDescription);
        resolutionSummary = BuildResolutionSummary(venue.Source.Resolution);

        return new PreparedVenueDetails(
            routeOptions,
            descriptionLines,
            Array.Empty<PreparedScheduleRow>(),
            resolutionSummary,
            hasDeferredSchedule);
    }

    private static PreparedVenueDetails CreateFallbackPreparedVenueDetails(PreparedVenue venue) =>
        new(
            GetImmediateRouteOptions(venue),
            Array.Empty<PreparedDescriptionLine>(),
            Array.Empty<PreparedScheduleRow>(),
            BuildResolutionSummary(venue.Source.Resolution),
            false);

    private static VenueRouteOption[] GetImmediateRouteOptions(PreparedVenue venue)
    {
        var lifestreamArgs = FormatLifestreamArguments(venue.Source.Location);
        return
        [
            new VenueRouteOption(
                venue.DetailedAddress,
                venue.DetailedAddress,
                string.IsNullOrWhiteSpace(lifestreamArgs) ? null : lifestreamArgs)
        ];
    }

    private static string SanitizeDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = NormalizeDisplayText(value);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = MarkdownStrongRegex.Replace(text, "$2");
        text = MarkdownEmRegex.Replace(text, "$2");
        text = ColonEmojiRegex.Replace(text, " ");
        text = text.Replace("`", string.Empty);
        text = text.Replace("\r\n", "\n");

        var lines = text.Split('\n');
        var normalizedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var normalized = MultiWhitespaceRegex.Replace(line, " ").Trim();
            if (normalized.Length == 0)
            {
                if (normalizedLines.Count > 0 && normalizedLines[^1].Length > 0)
                {
                    normalizedLines.Add(string.Empty);
                }

                continue;
            }

            normalized = NormalizeDescriptionLine(normalized, out var hadListMarker);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (IsDescriptionDecorationOnly(normalized))
            {
                continue;
            }

            if (hadListMarker)
            {
                normalized = "• " + normalized;
            }

            normalizedLines.Add(normalized);
        }

        var merged = new List<string>(normalizedLines.Count);
        foreach (var line in normalizedLines)
        {
            if (line.Length == 0)
            {
                if (merged.Count > 0 && merged[^1].Length > 0)
                {
                    merged.Add(string.Empty);
                }

                continue;
            }

            if (merged.Count == 0 || merged[^1].Length == 0)
            {
                merged.Add(line);
                continue;
            }

            if (ShouldJoinDescriptionLines(merged[^1], line))
            {
                merged[^1] += " " + line;
            }
            else
            {
                merged.Add(line);
            }
        }

        return string.Join("\n", merged).Trim();
    }

    private static string CompactDescriptionForDalamud(string sanitized)
    {
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        var lines = sanitized.Split('\n');
        var compacted = new List<string>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = CompactDescriptionLine(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (ShouldDropCompactDescriptionLine(line))
            {
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (compacted.Count > 0 &&
                compacted[^1].Length == 0 &&
                line.Length == 0)
            {
                continue;
            }

            compacted.Add(line);
        }

        return string.Join("\n", compacted).Trim();
    }

    private static string NormalizeDescriptionLine(string value, out bool hadListMarker)
    {
        var normalized = value;
        var headingMatch = HeadingEqualsRegex.Match(normalized);
        if (headingMatch.Success)
        {
            normalized = headingMatch.Groups[1].Value.Trim();
        }
        else
        {
            var singleHeading = HeadingSingleEqualsRegex.Match(normalized);
            if (singleHeading.Success)
            {
                normalized = singleHeading.Groups[1].Value.Trim();
            }
        }

        var markdownMatch = MarkdownLinkRegex.Match(normalized);
        if (markdownMatch.Success && markdownMatch.Index > 0)
        {
            var prefix = normalized[..markdownMatch.Index];
            if (IsDecorationPrefix(prefix))
            {
                normalized = normalized[markdownMatch.Index..];
                markdownMatch = MarkdownLinkRegex.Match(normalized);
            }
        }

        var hasLinkSyntax = markdownMatch.Success || UrlRegex.IsMatch(normalized);
        if (!(markdownMatch.Success && normalized.Length > 0 && normalized[0] == '['))
        {
            normalized = LeadingDecorationRegex.Replace(normalized, string.Empty);
        }

        if (!hasLinkSyntax)
        {
            normalized = TrailingDecorationRegex.Replace(normalized, string.Empty);
        }

        hadListMarker = LeadingBulletRegex.IsMatch(normalized);
        normalized = LeadingBulletRegex.Replace(normalized, string.Empty).Trim();
        return normalized;
    }

    private static bool IsDescriptionDecorationOnly(string line) =>
        line.Length == 0 ||
        (line.Length > 6 && OnlyPunctuationLineRegex.IsMatch(line));

    private static bool IsDecorationPrefix(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsPunctuation(c) && !char.IsSymbol(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDescriptionLinkDumpLine(string line)
    {
        if (RawUrlOnlyLineRegex.IsMatch(line) || LongDiscordChannelUrlRegex.IsMatch(line))
        {
            return true;
        }

        var semanticText = GetDescriptionSemanticText(line);
        return ExternalActionLeadRegex.IsMatch(semanticText) &&
               (semanticText.Length <= 40 || semanticText.Contains(':'));
    }

    private static bool ShouldDropCompactDescriptionLine(string line) =>
        line.Length == 0 || IsDescriptionDecorationOnly(line);

    private static bool IsDescriptionBannerLine(string line)
    {
        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length is < 6 or > 32)
        {
            return false;
        }

        var upperCount = letters.Count(char.IsUpper);
        return upperCount * 4 >= letters.Length * 3;
    }

    private static bool IsDescriptionRedundantMetadataLine(string line)
    {
        var semanticText = GetDescriptionSemanticText(line);
        if (DateLineRegex.IsMatch(semanticText) ||
            TimeAnnouncementRegex.IsMatch(semanticText) ||
            LocationLineRegex.IsMatch(semanticText) ||
            RecurringDayRegex.IsMatch(semanticText))
        {
            return true;
        }

        return false;
    }

    private static bool IsDescriptionPromoFluffLine(string line)
    {
        if (line.StartsWith("• ", StringComparison.Ordinal))
        {
            return false;
        }

        var semanticText = GetDescriptionSemanticText(line);
        if (semanticText.Length == 0 ||
            semanticText.Any(char.IsDigit) ||
            UrlRegex.IsMatch(semanticText) ||
            IsDescriptionStandaloneHeading(line))
        {
            return false;
        }

        var wordCount = CountDescriptionWords(semanticText);
        if (wordCount is 0 or > 14)
        {
            return false;
        }

        if (semanticText.Contains('!') && semanticText.Length <= 80)
        {
            return true;
        }

        if (PromoCtaLeadRegex.IsMatch(semanticText) && semanticText.Length <= 90)
        {
            return true;
        }

        if (ShortTaglineLeadRegex.IsMatch(semanticText) && semanticText.Length <= 100)
        {
            return true;
        }

        return semanticText.Length <= 100 &&
               wordCount <= 12 &&
               (semanticText.Contains('•') || semanticText.Contains(" - ", StringComparison.Ordinal));
    }

    private static bool IsDescriptionStandaloneHeading(string line)
    {
        var semanticText = GetDescriptionSemanticText(line);

        if (semanticText.Length > 40 || !SectionHeadingRegex.IsMatch(semanticText))
        {
            return false;
        }

        var wordCount = CountDescriptionWords(semanticText);
        if (wordCount <= 4 &&
            (semanticText.EndsWith(':') || semanticText.EndsWith('.') || semanticText.EndsWith('?') || semanticText.EndsWith('!')))
        {
            return true;
        }

        if (wordCount is 0 or > 5)
        {
            return false;
        }

        var words = semanticText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0)
            .ToArray();
        var titleishWords = 0;
        foreach (var word in words)
        {
            var trimmedWord = word.Trim('.', ':', '!', '?', ',', ';', '-', '(', ')', '[', ']');
            if (trimmedWord.Length == 0 || IsDescriptionConnectorWord(trimmedWord))
            {
                continue;
            }

            if (char.IsUpper(trimmedWord[0]) || trimmedWord.All(char.IsUpper))
            {
                titleishWords++;
            }
        }

        return titleishWords >= Math.Max(1, words.Count(w => !IsDescriptionConnectorWord(w.Trim('.', ':', '!', '?', ',', ';', '-', '(', ')', '[', ']'))) - 1);
    }

    private static bool IsDescriptionLineupLine(string line) =>
        LineupLineRegex.IsMatch(GetDescriptionSemanticText(line)) || line.StartsWith("• Slot ", StringComparison.Ordinal);

    private static string CompactDescriptionLine(string value)
    {
        var line = MultiWhitespaceRegex.Replace(value, " ").Trim();
        if (line.Length == 0)
        {
            return string.Empty;
        }

        line = Regex.Replace(line, @"\s*(?:→|->|–|—)\s*", " - ", RegexOptions.CultureInvariant);
        var slotMatch = Regex.Match(line, @"^Slot\s*(\d+)\s*\|\s*(.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (slotMatch.Success)
        {
            line = $"• Slot {slotMatch.Groups[1].Value}: {slotMatch.Groups[2].Value.Trim()}";
        }

        line = Regex.Replace(line, @"\s*\|\s*", ": ", RegexOptions.CultureInvariant);
        var hadBullet = line.StartsWith("• ", StringComparison.Ordinal);
        line = hadBullet ? line.Trim() : line.Trim(' ', '-');
        if (line.Length == 0)
        {
            return string.Empty;
        }

        if (LineupLineRegex.IsMatch(line) && !line.StartsWith("• ", StringComparison.Ordinal))
        {
            line = "• " + line;
        }

        return MultiWhitespaceRegex.Replace(line, " ").Trim();
    }

    private static string GetDescriptionComparisonKey(string value)
    {
        var normalized = NormalizeForSearch(value);
        normalized = normalized.Replace("•", " ", StringComparison.Ordinal);
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim().ToLowerInvariant();
        return normalized;
    }

    private static string GetDescriptionSemanticText(string value) =>
        value.StartsWith("• ", StringComparison.Ordinal) ? value[2..].TrimStart() : value;

    private static int CountDescriptionWords(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static bool IsDescriptionConnectorWord(string value) =>
        value.Equals("and", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("or", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("of", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("the", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("&", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("to", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("for", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("in", StringComparison.OrdinalIgnoreCase);

    private static PreparedDescriptionLine[] PrepareDescriptionLines(string sanitized)
    {
        var lines = sanitized.Split('\n');
        var preparedLines = new List<PreparedDescriptionLine>(lines.Length);
        string? previousContentLine = null;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                if (preparedLines.Count == 0 || preparedLines[^1].IsBlank)
                {
                    continue;
                }

                preparedLines.Add(new PreparedDescriptionLine(Array.Empty<PreparedDescriptionSegment>(), true));
                previousContentLine = null;
                continue;
            }

            if (previousContentLine is not null && ShouldInsertDescriptionBreak(previousContentLine, line))
            {
                preparedLines.Add(new PreparedDescriptionLine(Array.Empty<PreparedDescriptionSegment>(), true));
            }

            var segments = TokenizeDescriptionSegments(line);

            preparedLines.Add(new PreparedDescriptionLine(segments.ToArray(), false));
            previousContentLine = line;
        }

        return preparedLines.ToArray();
    }

    private static List<PreparedDescriptionSegment> TokenizeDescriptionSegments(string line)
    {
        var segments = new List<PreparedDescriptionSegment>();
        var markdownMatches = MarkdownLinkRegex.Matches(line);
        if (markdownMatches.Count == 0)
        {
            AppendTextSegmentsWithUrls(segments, line);
            return segments;
        }

        var cursor = 0;
        foreach (Match match in markdownMatches)
        {
            if (match.Index > cursor)
            {
                AppendTextSegmentsWithUrls(segments, line.Substring(cursor, match.Index - cursor));
            }

            var label = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (label.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                segments.Add(new PreparedDescriptionSegment(label, url));
            }
            else if (label.Length > 0)
            {
                segments.Add(new PreparedDescriptionSegment(label, null));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < line.Length)
        {
            AppendTextSegmentsWithUrls(segments, line[cursor..]);
        }

        return segments;
    }

    private static void AppendTextSegmentsWithUrls(List<PreparedDescriptionSegment> segments, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var matches = UrlRegex.Matches(text);
        if (matches.Count == 0)
        {
            segments.Add(new PreparedDescriptionSegment(text, null));
            return;
        }

        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index > cursor)
            {
                var prefix = text.Substring(cursor, match.Index - cursor);
                if (!string.IsNullOrEmpty(prefix))
                {
                    segments.Add(new PreparedDescriptionSegment(prefix, null));
                }
            }

            var rawUrl = match.Value;
            var url = rawUrl.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            var trailing = rawUrl.Substring(url.Length);

            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                segments.Add(new PreparedDescriptionSegment(GetCompactUrlLabel(url), url));
            }
            else
            {
                segments.Add(new PreparedDescriptionSegment(rawUrl, null));
            }

            if (!string.IsNullOrEmpty(trailing))
            {
                segments.Add(new PreparedDescriptionSegment(trailing, null));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            segments.Add(new PreparedDescriptionSegment(text[cursor..], null));
        }
    }

    private static string GetCompactUrlLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (TryGetSemanticUrlLabel(uri, out var semanticLabel))
        {
            return semanticLabel;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return host;
    }

    private static string NormalizeFancyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0xA9C1 || value == 0xA9C2)
            {
                continue;
            }

            if (SmallCapsMap.TryGetValue(value, out var smallCap))
            {
                builder.Append(smallCap);
                continue;
            }

            if (SpecialMap.TryGetValue(value, out var mapped))
            {
                builder.Append(mapped);
                continue;
            }

            if (value is >= 0x1D400 and <= 0x1D419)
            {
                builder.Append((char)('A' + (value - 0x1D400)));
                continue;
            }

            if (value is >= 0x1D41A and <= 0x1D433)
            {
                builder.Append((char)('a' + (value - 0x1D41A)));
                continue;
            }

            if (value is >= 0x1D434 and <= 0x1D44D)
            {
                builder.Append((char)('A' + (value - 0x1D434)));
                continue;
            }

            if (value is >= 0x1D44E and <= 0x1D467)
            {
                builder.Append((char)('a' + (value - 0x1D44E)));
                continue;
            }

            if (value is >= 0x1D63C and <= 0x1D655)
            {
                builder.Append((char)('A' + (value - 0x1D63C)));
                continue;
            }

            if (value is >= 0x1D656 and <= 0x1D66F)
            {
                builder.Append((char)('a' + (value - 0x1D656)));
                continue;
            }

            if (value is >= 0x1D608 and <= 0x1D621)
            {
                builder.Append((char)('A' + (value - 0x1D608)));
                continue;
            }

            if (value is >= 0x1D622 and <= 0x1D63B)
            {
                builder.Append((char)('a' + (value - 0x1D622)));
                continue;
            }

            if (value is >= 0x1D5D4 and <= 0x1D5ED)
            {
                builder.Append((char)('A' + (value - 0x1D5D4)));
                continue;
            }

            if (value is >= 0x1D5EE and <= 0x1D607)
            {
                builder.Append((char)('a' + (value - 0x1D5EE)));
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static string NormalizeDisplayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeFancyText(text);
        normalized = ReplaceProblemSymbols(normalized);
        normalized = normalized.Replace("\r\n", "\n").Trim();
        return normalized;
    }

    private static string NormalizeForSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeDisplayText(text);
        normalized = HtmlTagRegex.Replace(normalized, " ");
        normalized = MarkdownLinkRegex.Replace(normalized, "$1");
        normalized = MarkdownStrongRegex.Replace(normalized, "$2");
        normalized = MarkdownEmRegex.Replace(normalized, "$2");
        normalized = normalized.Replace("`", string.Empty);
        normalized = MultiWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    private static string ReplaceProblemSymbols(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (ProblemSymbolMap.TryGetValue(rune.Value, out var replacement))
            {
                builder.Append(replacement);
                continue;
            }

            if (IsLikelyEmojiRune(rune.Value))
            {
                builder.Append('•');
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static bool IsLikelyEmojiRune(int value) =>
        value is >= 0x1F000 and <= 0x1FAFF;

    private static bool ShouldJoinDescriptionLines(string previous, string current)
    {
        if (previous.EndsWith(':') ||
            previous.Length < 40 ||
            IsLikelyPromotionalShortLine(previous) ||
            IsLikelyPromotionalShortLine(current) ||
            IsDescriptionStandaloneHeading(previous) ||
            current.StartsWith("• ", StringComparison.Ordinal) ||
            IsDescriptionStandaloneHeading(current) ||
            IsDescriptionLineupLine(current) ||
            IsDescriptionRedundantMetadataLine(current) ||
            IsDescriptionLinkDumpLine(current))
        {
            return false;
        }

        var last = previous[^1];
        if (last is '.' or '!' or '?' or ';' or ':')
        {
            return false;
        }

        var currentText = GetDescriptionSemanticText(current);
        return currentText.Length > 0 && char.IsLower(currentText[0]);
    }

    private static bool IsLikelyPromotionalShortLine(string line)
    {
        var semanticText = GetDescriptionSemanticText(line);
        if (semanticText.Length == 0 ||
            semanticText.Length > 100 ||
            semanticText.Any(char.IsDigit) ||
            UrlRegex.IsMatch(semanticText))
        {
            return false;
        }

        return PromoCtaLeadRegex.IsMatch(semanticText) ||
               ShortTaglineLeadRegex.IsMatch(semanticText) ||
               (CountDescriptionWords(semanticText) <= 12 &&
                (semanticText.Contains('!') || semanticText.Contains('•') || semanticText.Contains(" - ", StringComparison.Ordinal)));
    }

    private static bool ShouldInsertDescriptionBreak(string previous, string current)
    {
        var previousKind = GetDescriptionLineKind(previous);
        var currentKind = GetDescriptionLineKind(current);

        if (currentKind == DescriptionLineKind.Heading && previousKind != DescriptionLineKind.Heading)
        {
            return true;
        }

        if (previousKind == DescriptionLineKind.Banner && currentKind != DescriptionLineKind.Banner)
        {
            return true;
        }

        if (previousKind != currentKind &&
            (previousKind is DescriptionLineKind.Metadata or DescriptionLineKind.Lineup or DescriptionLineKind.Link ||
             currentKind is DescriptionLineKind.Metadata or DescriptionLineKind.Lineup or DescriptionLineKind.Link))
        {
            return true;
        }

        return false;
    }

    private static DescriptionLineKind GetDescriptionLineKind(string line)
    {
        if (IsDescriptionStandaloneHeading(line))
        {
            return DescriptionLineKind.Heading;
        }

        if (IsDescriptionRedundantMetadataLine(line))
        {
            return DescriptionLineKind.Metadata;
        }

        if (IsDescriptionLineupLine(line))
        {
            return DescriptionLineKind.Lineup;
        }

        if (IsDescriptionLinkOnlyLine(line) || IsDescriptionLinkDumpLine(line))
        {
            return DescriptionLineKind.Link;
        }

        if (IsDescriptionBannerLine(line))
        {
            return DescriptionLineKind.Banner;
        }

        return DescriptionLineKind.Paragraph;
    }

    private static bool IsDescriptionLinkOnlyLine(string line)
    {
        var strippedMarkdown = MarkdownLinkRegex.Replace(line, string.Empty).Trim();
        if (strippedMarkdown.Length == 0 && MarkdownLinkRegex.IsMatch(line))
        {
            return true;
        }

        return RawUrlOnlyLineRegex.IsMatch(line);
    }

    private static bool TryGetSemanticUrlLabel(Uri uri, out string label)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        var path = uri.AbsolutePath.Trim('/');

        if (host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase) ||
            (host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("invite/", StringComparison.OrdinalIgnoreCase)))
        {
            label = "Discord invite";
            return true;
        }

        if (host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("channels/", StringComparison.OrdinalIgnoreCase))
        {
            label = "Discord channel";
            return true;
        }

        if (host.Equals("partake.gg", StringComparison.OrdinalIgnoreCase))
        {
            label = "Partake event";
            return true;
        }

        if (host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            label = "Promo video";
            return true;
        }

        if (host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            label = "Twitch stream";
            return true;
        }

        if (host.EndsWith("carrd.co", StringComparison.OrdinalIgnoreCase))
        {
            label = "Website";
            return true;
        }

        label = string.Empty;
        return false;
    }

    private void RefreshFilteredVenuesIfNeeded()
    {
        if (!_filteredVenuesDirty)
        {
            return;
        }

        _filteredVenues.Clear();
        if (_preparedVenues == null)
        {
            _filteredVenuesDirty = false;
            _sortedVenuesDirty = true;
            return;
        }

        var normalizedSearch = NormalizeForSearch(_searchText.Trim());
        var normalizedTags = ParseRequiredTags(_tagFilter);
        foreach (var venue in _preparedVenues)
        {
            if (MatchesCurrentFilters(venue, normalizedSearch, normalizedTags))
            {
                _filteredVenues.Add(venue);
            }
        }

        _filteredVenuesDirty = false;
        _sortedVenuesDirty = true;
        _sortedVenueRowMetricsDirty = true;
    }

    private bool MatchesCurrentFilters(PreparedVenue venue, string normalizedSearch, string[] normalizedTags)
    {
        if (normalizedSearch.Length > 0 &&
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(venue.SearchText, normalizedSearch, CompareOptions.IgnoreCase) < 0)
        {
            return false;
        }

        if (normalizedTags.Length > 0)
        {
            foreach (var tag in normalizedTags)
            {
                if (!VenueHasMatchingTag(venue, tag))
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrEmpty(_selectedRegion) &&
            !string.Equals(venue.Region, _selectedRegion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_selectedDataCenter) &&
            !string.Equals(venue.DataCenter, _selectedDataCenter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_selectedWorld) &&
            !string.Equals(venue.World, _selectedWorld, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_onlyOpen && !venue.IsOpen)
        {
            return false;
        }

        if (_favoritesOnly && !IsPreferredVenue(_favoriteVenueIds, venue.Id))
        {
            return false;
        }

        if (_visitedOnly && !IsPreferredVenue(_visitedVenueIds, venue.Id))
        {
            return false;
        }

        if (_sfwOnly && venue.IsNsfw)
        {
            return false;
        }

        if (_nsfwOnly && !venue.IsNsfw)
        {
            return false;
        }

        if (_sizeApartment && _sizeSmall && _sizeMedium && _sizeLarge)
        {
            return true;
        }

        if (venue.IsApartment)
        {
            return _sizeApartment;
        }

        return venue.PlotSize switch
        {
            HousingPlotSize.Small => _sizeSmall,
            HousingPlotSize.Medium => _sizeMedium,
            HousingPlotSize.Large => _sizeLarge,
            _ => false
        };
    }

    private static bool VenueHasMatchingTag(PreparedVenue venue, string normalizedTagFilter)
    {
        foreach (var venueTag in venue.Tags)
        {
            var normalizedVenueTag = NormalizeForSearch(venueTag);
            if (normalizedVenueTag.Length > 0 &&
                CultureInfo.CurrentCulture.CompareInfo.IndexOf(normalizedVenueTag, normalizedTagFilter, CompareOptions.IgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureSortedVenues(ImGuiTableSortSpecsPtr sortSpecs)
    {
        var sortSpecsChanged = HaveSortSpecsChanged(sortSpecs);
        if (!_sortedVenuesDirty && !sortSpecsChanged)
        {
            return;
        }

        _sortedVenues.Clear();
        _sortedVenues.AddRange(_filteredVenues);
        _sortedVenues.Sort((left, right) => ComparePreparedVenues(left, right, _sortSpecSnapshots));
        _sortedVenuesDirty = false;
        _sortedVenueRowMetricsDirty = true;

        if (sortSpecs.SpecsCount > 0)
        {
            sortSpecs.SpecsDirty = false;
        }

    }

    private bool HaveSortSpecsChanged(ImGuiTableSortSpecsPtr sortSpecs)
    {
        var nextSortSpecs = new List<SortSpecSnapshot>(sortSpecs.SpecsCount);
        for (var i = 0; i < sortSpecs.SpecsCount; i++)
        {
            var spec = sortSpecs.Specs[i];
            if (spec.SortDirection == ImGuiSortDirection.None)
            {
                continue;
            }

            nextSortSpecs.Add(new SortSpecSnapshot(spec.ColumnIndex, spec.SortDirection));
        }

        var changed = nextSortSpecs.Count != _sortSpecSnapshots.Count;
        if (!changed)
        {
            for (var i = 0; i < nextSortSpecs.Count; i++)
            {
                if (nextSortSpecs[i] != _sortSpecSnapshots[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            _sortSpecSnapshots.Clear();
            _sortSpecSnapshots.AddRange(nextSortSpecs);
        }

        return _sortedVenuesDirty || sortSpecs.SpecsDirty || changed;
    }

    private static int ComparePreparedVenues(
        PreparedVenue left,
        PreparedVenue right,
        IReadOnlyList<SortSpecSnapshot> sortSpecs)
    {
        if (sortSpecs.Count == 0)
        {
            return ComparePreparedVenueColumn(left, right, 0);
        }

        foreach (var sortSpec in sortSpecs)
        {
            var comparison = ComparePreparedVenueColumn(left, right, sortSpec.ColumnIndex);
            if (comparison == 0)
            {
                continue;
            }

            return sortSpec.Direction == ImGuiSortDirection.Descending
                ? -comparison
                : comparison;
        }

        return ComparePreparedVenueColumn(left, right, 0);
    }

    private static int ComparePreparedVenueColumn(PreparedVenue left, PreparedVenue right, int columnIndex) =>
        columnIndex switch
        {
            0 => CompareText(left.DisplayName, right.DisplayName),
            1 => CompareText(left.LocationSortKey, right.LocationSortKey),
            2 => left.VenueTypeSortKey.CompareTo(right.VenueTypeSortKey),
            3 => left.StatusSortKey.CompareTo(right.StatusSortKey),
            _ => CompareText(left.DisplayName, right.DisplayName)
        };

    private static int CompareText(string? left, string? right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);

    private static string[] ParseRequiredTags(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void EnsurePreferenceCollectionsInitialized()
    {
        _configuration.FavoriteVenueIds ??= new List<string>();
        _configuration.VisitedVenueIds ??= new List<string>();
    }

    private void SyncPreferredVenueLookups()
    {
        _favoriteVenueIds.Clear();
        _visitedVenueIds.Clear();

        foreach (var id in _configuration.FavoriteVenueIds ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _favoriteVenueIds.Add(id);
            }
        }

        foreach (var id in _configuration.VisitedVenueIds ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _visitedVenueIds.Add(id);
            }
        }
    }

    private static bool IsPreferredVenue(HashSet<string> venueIds, string? venueId) =>
        !string.IsNullOrWhiteSpace(venueId) && venueIds.Contains(venueId);

    private void SetPreferredVenue(List<string>? venueIds, HashSet<string> lookup, string? venueId, bool enabled)
    {
        if (venueIds == null || string.IsNullOrWhiteSpace(venueId))
        {
            return;
        }

        var changed = false;
        if (enabled)
        {
            if (lookup.Add(venueId))
            {
                venueIds.Add(venueId);
                changed = true;
            }
        }
        else if (lookup.Remove(venueId))
        {
            venueIds.RemoveAll(id => string.Equals(id, venueId, StringComparison.Ordinal));
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _configuration.Save(DalamudServices.PluginInterface);
        if (_favoritesOnly || _visitedOnly)
        {
            InvalidateFilteredVenues();
        }
    }

    private void EnsureSelection(IReadOnlyList<PreparedVenue> venues)
    {
        if (venues.Count == 0)
        {
            _selectedVenueId = null;
            return;
        }

        if (_selectedVenueId == null || venues.All(v => !string.Equals(v.Id, _selectedVenueId, StringComparison.Ordinal)))
        {
            _selectedVenueId = venues[0].Id;
        }
    }

    private PreparedVenue? GetSelectedPreparedVenue() =>
        _filteredVenues.FirstOrDefault(v => string.Equals(v.Id, _selectedVenueId, StringComparison.Ordinal));

    private void SetRegion(string? region)
    {
        _selectedRegion = region;
        _selectedDataCenter = null;
        _selectedWorld = null;
        UpdateWorldOptions();
        InvalidateFilteredVenues();
    }

    private void SetDataCenter(string? dataCenter)
    {
        _selectedDataCenter = dataCenter;
        _selectedWorld = null;
        UpdateWorldOptions();
        InvalidateFilteredVenues();
    }

    private void InvalidateFilteredVenues()
    {
        _filteredVenuesDirty = true;
        _sortedVenuesDirty = true;
        _sortedVenueRowMetricsDirty = true;
        _sortedVenueRowMetricsWrapWidth = -1f;
    }

    private void UpdateWorldOptions()
    {
        _worlds.Clear();
        if (_venues == null)
        {
            return;
        }

        var query = _venues.AsEnumerable();
        if (!string.IsNullOrEmpty(_selectedRegion))
        {
            query = query.Where(v =>
                string.Equals(ResolveRegion(v.Location?.DataCenter), _selectedRegion, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_selectedDataCenter))
        {
            query = query.Where(v =>
                string.Equals(v.Location?.DataCenter, _selectedDataCenter, StringComparison.OrdinalIgnoreCase));
        }

        _worlds.AddRange(query
            .Select(v => v.Location?.World)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)!);
    }

    private IEnumerable<string> GetRegionDataCenters()
    {
        if (_selectedRegion == null)
        {
            return _dataCenters;
        }

        return _dataCenters
            .Where(dc => string.Equals(ResolveRegion(dc), _selectedRegion, StringComparison.OrdinalIgnoreCase))
            .OrderBy(dc => dc, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ResolveRegion(string? dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            return null;
        }

        return dataCenter.Trim() switch
        {
            "Aether" => "North America",
            "Crystal" => "North America",
            "Dynamis" => "North America",
            "Primal" => "North America",
            "Chaos" => "Europe",
            "Light" => "Europe",
            "Materia" => "Oceania",
            "Elemental" => "Japan",
            "Gaia" => "Japan",
            "Mana" => "Japan",
            "Meteor" => "Japan",
            _ => null
        };
    }

    private string FormatStatusLine(DirectoryVenue venue)
    {
        if (venue.Resolution == null)
        {
            return "No opening set";
        }

        var startLocal = venue.Resolution.Start.ToLocalTime();
        var endLocal = venue.Resolution.End.ToLocalTime();
        var start = $"{startLocal.ToString("ddd", CultureInfo.InvariantCulture)} {FormatShortTime(startLocal)}";
        var end = FormatShortTime(endLocal);
        return venue.Resolution.IsNow
            ? $"Open until {end}"
            : $"Opens {start}";
    }

    private static string FormatAddress(DirectoryLocation? location)
    {
        if (location == null)
        {
            return "Location unknown";
        }

        if (!string.IsNullOrWhiteSpace(location.Override))
        {
            return location.Override;
        }

        if (location.Apartment > 0)
        {
            var subdivision = location.Subdivision ? ", Subdivision" : string.Empty;
            return $"{location.DataCenter}, {location.World}, {location.District}, Ward {location.Ward}{subdivision}, Apartment {location.Apartment}";
        }

        return $"{location.DataCenter}, {location.World}, {location.District}, Ward {location.Ward}, Plot {location.Plot}";
    }

    private static string FormatAddressForTable(DirectoryVenue venue)
    {
        var location = venue.Location;
        if (location == null)
        {
            return "Location unknown";
        }

        if (!string.IsNullOrWhiteSpace(location.Override))
        {
            var routes = ParseOverrideRouteOptions(location.Override)
                .Select(r => r.DisplayText)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (routes.Count > 1)
            {
                return string.Join("\n", routes);
            }
        }

        return FormatAddress(location);
    }

    private static string FormatAddressDetailed(DirectoryLocation? location)
    {
        if (location == null)
        {
            return "Location unknown";
        }

        if (!string.IsNullOrWhiteSpace(location.Override))
        {
            return location.Override;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(location.DataCenter))
        {
            parts.Add(location.DataCenter);
        }

        if (!string.IsNullOrWhiteSpace(location.World))
        {
            parts.Add(location.World);
        }

        if (!string.IsNullOrWhiteSpace(location.District))
        {
            parts.Add(location.District);
        }

        parts.Add($"Ward {location.Ward}" + (location.Subdivision ? " (Subdivision)" : string.Empty));
        if (location.Apartment > 0)
        {
            parts.Add($"Apartment {location.Apartment}");
        }
        else
        {
            parts.Add($"Plot {location.Plot}");
        }

        if (location.Room > 0)
        {
            parts.Add($"Room {location.Room}");
        }

        if (!string.IsNullOrWhiteSpace(location.Shard))
        {
            parts.Add($"Shard {location.Shard}");
        }

        return string.Join(", ", parts);
    }

    private int GetSelectedRouteIndex(string venueId, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        if (!_selectedRouteIndices.TryGetValue(venueId, out var index))
        {
            _selectedRouteIndices[venueId] = 0;
            return 0;
        }

        if (index >= 0 && index < count)
        {
            return index;
        }

        _selectedRouteIndices[venueId] = 0;
        return 0;
    }

    private static List<VenueRouteOption> BuildRouteOptions(DirectoryVenue venue)
    {
        var options = new List<VenueRouteOption>();
        var location = venue.Location;
        if (location == null)
        {
            options.Add(new VenueRouteOption("Location unknown", "Location unknown", null));
            return options;
        }

        if (!string.IsNullOrWhiteSpace(location.Override))
        {
            options.AddRange(ParseOverrideRouteOptions(location.Override));
            if (options.Count > 0)
            {
                return options;
            }
        }

        var detailed = FormatAddressDetailed(location);
        var lifestreamArgs = FormatLifestreamArguments(location);
        options.Add(new VenueRouteOption(detailed, detailed, string.IsNullOrWhiteSpace(lifestreamArgs) ? null : lifestreamArgs));
        return options;
    }

    private static IEnumerable<VenueRouteOption> ParseOverrideRouteOptions(string overrideText)
    {
        var text = Regex.Replace(overrideText, @"\s+", " ").Trim();
        if (text.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentLabel = null;
        foreach (Match match in OverrideRouteRegex.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            var label = match.Groups["label"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(label))
            {
                currentLabel = label;
            }

            var address = NormalizeRouteAddress(match.Groups["address"].Value);
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            var display = string.IsNullOrWhiteSpace(currentLabel)
                ? address
                : $"{currentLabel}: {address}";
            if (!seen.Add(display))
            {
                continue;
            }

            var lifestreamArgs = FormatLifestreamArguments(address);
            yield return new VenueRouteOption(display, address, string.IsNullOrWhiteSpace(lifestreamArgs) ? null : lifestreamArgs);
        }
    }

    private static string NormalizeRouteAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim(), @"\s+", " ").Trim().TrimEnd('.');
    }

    private PreparedVenue[] BuildPreparedVenues(IReadOnlyList<DirectoryVenue> venues)
    {
        var prepared = new PreparedVenue[venues.Count];
        for (var i = 0; i < venues.Count; i++)
        {
            var sourceVenue = venues[i];
            try
            {
                prepared[i] = CreatePreparedVenue(sourceVenue);
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Warning(
                    ex,
                    "[DirectoryBrowserWindow] Failed to prepare venue at index {Index} (Id: {VenueId}, Name: {VenueName}). Using fallback prepared venue.",
                    i,
                    sourceVenue.Id,
                    sourceVenue.Name ?? string.Empty);

                prepared[i] = CreateFallbackPreparedVenue(sourceVenue, i);
            }
        }

        return prepared;
    }

    private PreparedVenue[] BuildPreparedVenuesLightweight(IReadOnlyList<DirectoryVenue> venues)
    {
        var prepared = new PreparedVenue[venues.Count];
        for (var i = 0; i < venues.Count; i++)
        {
            var sourceVenue = venues[i];
            try
            {
                prepared[i] = CreatePreparedVenue(sourceVenue, resolvePlotSize: false);
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Warning(
                    ex,
                    "[DirectoryBrowserWindow] Failed to build lightweight prepared venue at index {Index} (Id: {VenueId}, Name: {VenueName}). Using fallback prepared venue.",
                    i,
                    sourceVenue.Id,
                    sourceVenue.Name ?? string.Empty);

                prepared[i] = CreateFallbackPreparedVenue(sourceVenue, i);
            }
        }

        return prepared;
    }

    private static PreparedVenue CreateFallbackPreparedVenue(DirectoryVenue venue, int index)
    {
        var displayName = string.IsNullOrWhiteSpace(venue.Name)
            ? $"Venue #{index + 1}"
            : venue.Name.Trim();
        var fallbackId = BuildFallbackPreparedVenueId(venue);
        var location = venue.Location;
        var region = location?.DataCenter;
        var tableAddress = BuildFallbackAddress(location);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new PreparedVenue(
            venue,
            fallbackId,
            displayName,
            NormalizeForSearch(displayName),
            tags,
            ResolveRegion(region),
            region,
            location?.World,
            venue.Resolution?.IsNow == true,
            !venue.Sfw,
            null,
            location?.Apartment > 0,
            "?",
            int.MaxValue,
            $"{region ?? string.Empty}|{location?.World ?? string.Empty}|{displayName}",
            tableAddress,
            tableAddress,
            "Status unavailable",
            DateTimeOffset.MinValue,
            Array.Empty<VenueRouteOption>(),
            Array.Empty<PreparedDescriptionLine>(),
            Array.Empty<PreparedScheduleRow>(),
            null,
            "This venue could not be fully prepared.");
    }

    private static string BuildFallbackPreparedVenueId(DirectoryVenue venue)
    {
        if (!string.IsNullOrWhiteSpace(venue.Id))
        {
            return venue.Id;
        }

        var location = venue.Location;
        var fallbackKey = string.Join("|",
            venue.Name?.Trim() ?? string.Empty,
            location?.DataCenter?.Trim() ?? string.Empty,
            location?.World?.Trim() ?? string.Empty,
            location?.District?.Trim() ?? string.Empty,
            location?.Ward.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            location?.Plot.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            location?.Apartment.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            location?.Room.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            venue.Website?.ToString() ?? string.Empty,
            venue.Discord?.ToString() ?? string.Empty);

        return "generated:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallbackKey)));
    }

    private static string BuildFallbackAddress(DirectoryLocation? location)
    {
        if (location == null)
        {
            return "Unknown location";
        }

        return string.Join(", ",
            new[]
            {
                location.World,
                location.District,
                location.Ward > 0 ? $"Ward {location.Ward}" : null,
                location.Apartment > 0 ? $"Apartment {location.Apartment}" : (location.Plot > 0 ? $"Plot {location.Plot}" : null),
                location.Room > 0 ? $"Room {location.Room}" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private PreparedVenue CreatePreparedVenue(DirectoryVenue venue, bool resolvePlotSize = true)
    {
        var id = GetPreparedVenueId(venue);
        var displayName = string.IsNullOrWhiteSpace(venue.Name) ? "Unnamed venue" : NormalizeDisplayText(venue.Name);
        var descriptionText = JoinNonEmptyLines(venue.Description);
        var normalizedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (venue.Tags != null)
        {
            foreach (var tag in venue.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    normalizedTags.Add(tag);
                }
            }
        }

        var searchText = BuildPreparedSearchText(displayName, descriptionText, normalizedTags);
        var region = ResolveRegion(venue.Location?.DataCenter);
        var plotSize = resolvePlotSize ? TryGetPlotSize(venue.Location) : null;
        var isApartment = IsApartmentLocation(venue.Location);
        var venueTypeLabel = GetPreparedVenueTypeLabel(isApartment, plotSize);
        var venueTypeSortKey = GetPreparedVenueTypeSortKey(isApartment, plotSize);
        var tableAddress = FormatAddressForTable(venue);
        var detailedAddress = FormatAddressDetailed(venue.Location);
        var warningText = GetVenueWarningText(IsOpenlyNsfwVenue(venue), HasAdultServicesTag(venue));
        var statusLine = FormatStatusLine(venue);
        var statusSortKey = GetStatusSortKey(venue.Resolution);
        return new PreparedVenue(
            venue,
            id,
            displayName,
            searchText,
            normalizedTags,
            region,
            venue.Location?.DataCenter,
            venue.Location?.World,
            venue.Resolution?.IsNow == true,
            IsVenueNsfw(venue),
            plotSize,
            isApartment,
            venueTypeLabel,
            venueTypeSortKey,
            GetLocationKey(venue),
            tableAddress,
            detailedAddress,
            statusLine,
            statusSortKey,
            Array.Empty<VenueRouteOption>(),
            Array.Empty<PreparedDescriptionLine>(),
            Array.Empty<PreparedScheduleRow>(),
            null,
            warningText);
    }

    private HousingPlotSize? TryGetPlotSize(DirectoryLocation? location) =>
        _housingPlotSizeResolver.TryGetSize(location, out var size) ? size : null;

    private static string BuildPreparedSearchText(string displayName, string description, IEnumerable<string> tags)
    {
        var builder = new StringBuilder(displayName.Length + description.Length + 64);
        builder.Append(displayName);
        if (!string.IsNullOrWhiteSpace(description))
        {
            builder.Append('\n');
            builder.Append(description);
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            builder.Append('\n');
            builder.Append(tag);
        }

        return NormalizeForSearch(builder.ToString());
    }

    private static string JoinNonEmptyLines(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static string GetPreparedVenueId(DirectoryVenue venue)
    {
        if (!string.IsNullOrWhiteSpace(venue.Id))
        {
            return venue.Id;
        }

        var fallbackKey = string.Join("|",
            NormalizeForSearch(venue.Name),
            NormalizeForSearch(FormatAddressDetailed(venue.Location)),
            NormalizeForSearch(JoinNonEmptyLines(venue.Description)),
            venue.Website?.ToString() ?? string.Empty,
            venue.Discord?.ToString() ?? string.Empty);
        return "generated:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallbackKey)));
    }

    private static string GetPreparedVenueTypeLabel(bool isApartment, HousingPlotSize? size)
    {
        if (isApartment)
        {
            return "A";
        }

        return size switch
        {
            HousingPlotSize.Small => "S",
            HousingPlotSize.Medium => "M",
            HousingPlotSize.Large => "L",
            _ => "?"
        };
    }

    private static int GetPreparedVenueTypeSortKey(bool isApartment, HousingPlotSize? size)
    {
        if (isApartment)
        {
            return 0;
        }

        return size switch
        {
            HousingPlotSize.Small => 1,
            HousingPlotSize.Medium => 2,
            HousingPlotSize.Large => 3,
            _ => 4
        };
    }

    private static PreparedScheduleRow[] BuildPreparedScheduleRows(IEnumerable<DirectorySchedule>? schedules)
    {
        if (schedules == null)
        {
            return Array.Empty<PreparedScheduleRow>();
        }

        DirectorySchedule[] ordered = Array.Empty<DirectorySchedule>();
        ordered = schedules switch
        {
            DirectorySchedule[] array => array.Length == 0 ? Array.Empty<DirectorySchedule>() : array.ToArray(),
            List<DirectorySchedule> list => list.Count == 0 ? Array.Empty<DirectorySchedule>() : list.ToArray(),
            _ => schedules.ToArray()
        };

        if (ordered.Length == 0)
        {
            return Array.Empty<PreparedScheduleRow>();
        }

        Array.Sort(ordered, CompareDirectorySchedules);
        var referenceUtc = DateTime.UtcNow;
        var shortTimePattern = PluginTimeFormat;
        var localTimeZone = TimeZoneInfo.Local;
        var timeZoneContexts = new Dictionary<string, ScheduleTimeZoneContext>(StringComparer.OrdinalIgnoreCase);
        var rows = new PreparedScheduleRow[ordered.Length];

        for (var i = 0; i < ordered.Length; i++)
        {
            var schedule = ordered[i];
            if (!TryFormatLocalScheduleRange(
                    schedule,
                    referenceUtc,
                    shortTimePattern,
                    localTimeZone,
                    timeZoneContexts,
                    out var start,
                    out var end))
            {
                start = FormatTime(schedule.Start, referenceUtc);
                end = FormatTime(schedule.End, referenceUtc);
            }

            var label = FormatScheduleLabel(schedule);
            var timeRange = string.Concat(start, " - ", end);
            rows[i] = new PreparedScheduleRow(
                label,
                timeRange,
                schedule.Resolution?.IsNow == true);
        }

        return rows;
    }

    private static string? BuildResolutionSummary(DirectoryResolution? resolution)
    {
        if (resolution == null)
        {
            return null;
        }

        return resolution.IsNow
            ? $"Open now until {FormatShortTime(resolution.End)}!"
            : $"Next open {resolution.Start.ToLocalTime().ToString("dddd", CultureInfo.InvariantCulture)} at {FormatShortTime(resolution.Start)}";
    }

    private static DateTimeOffset GetStatusSortKey(DirectoryResolution? resolution)
    {
        if (resolution == null)
        {
            return DateTimeOffset.MaxValue;
        }

        return resolution.IsNow ? resolution.End : resolution.Start;
    }

    private static string FormatLifestreamArguments(DirectoryLocation? location)
    {
        if (location == null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(location.DataCenter))
        {
            parts.Add(location.DataCenter);
        }

        if (!string.IsNullOrWhiteSpace(location.World))
        {
            parts.Add(location.World);
        }

        if (!string.IsNullOrWhiteSpace(location.District))
        {
            var cleaned = location.District.Replace("(Subdivision)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                parts.Add(cleaned);
            }
        }

        parts.Add($"Ward {location.Ward}");
        if (location.Apartment > 0)
        {
            parts.Add($"Apartment {location.Apartment}");
            if (location.Subdivision)
            {
                parts.Add("Subdivision");
            }
        }
        else
        {
            parts.Add($"Plot {location.Plot}");

            if (location.Room > 0)
            {
                parts.Add($"Room {location.Room}");
            }
        }

        return string.Join(", ", parts);
    }

    private static string? FormatLifestreamArguments(string routeText)
    {
        if (string.IsNullOrWhiteSpace(routeText))
        {
            return null;
        }

        var tokens = routeText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().TrimEnd('.'))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var wardToken = tokens.FirstOrDefault(t => t.StartsWith("Ward ", StringComparison.OrdinalIgnoreCase));
        if (wardToken == null)
        {
            return null;
        }

        var wardIndex = tokens.FindIndex(t => string.Equals(t, wardToken, StringComparison.OrdinalIgnoreCase));
        if (wardIndex < 2)
        {
            return null;
        }

        var plotToken = tokens.FirstOrDefault(t => t.StartsWith("Plot ", StringComparison.OrdinalIgnoreCase));
        var apartmentToken = tokens.FirstOrDefault(t =>
            t.StartsWith("Apartment ", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Apt ", StringComparison.OrdinalIgnoreCase));
        if (plotToken == null && apartmentToken == null)
        {
            return null;
        }

        var head = tokens.Take(wardIndex).ToList();
        var parts = new List<string>(head.Count + 4);
        parts.AddRange(head);
        parts.Add(NormalizeWardOrPlotToken(wardToken));
        if (apartmentToken != null)
        {
            parts.Add(NormalizeWardOrPlotToken(apartmentToken));
            if (wardToken.Contains("sub", StringComparison.OrdinalIgnoreCase) ||
                tokens.Any(t => t.Contains("subdivision", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t, "sub", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t, "s", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add("Subdivision");
            }
        }
        else
        {
            parts.Add(NormalizeWardOrPlotToken(plotToken!));
        }

        var room = tokens.FirstOrDefault(t => t.StartsWith("Room ", StringComparison.OrdinalIgnoreCase));
        if (room != null)
        {
            parts.Add(room);
        }

        return string.Join(", ", parts);
    }

    private static string NormalizeWardOrPlotToken(string token)
    {
        var cleaned = Regex.Replace(token, @"\s+", " ").Trim();
        if (cleaned.StartsWith("Ward", StringComparison.OrdinalIgnoreCase))
        {
            return "Ward " + cleaned.Substring(4).Trim();
        }

        if (cleaned.StartsWith("Apartment", StringComparison.OrdinalIgnoreCase))
        {
            return "Apartment " + cleaned.Substring("Apartment".Length).Trim();
        }

        if (cleaned.StartsWith("Apt", StringComparison.OrdinalIgnoreCase))
        {
            return "Apartment " + cleaned.Substring(3).Trim();
        }

        if (cleaned.StartsWith("Plot", StringComparison.OrdinalIgnoreCase))
        {
            return "Plot " + cleaned.Substring(4).Trim();
        }

        return cleaned;
    }

    private static bool IsApartmentLocation(DirectoryLocation? location) =>
        location is { Apartment: > 0 };

    private static bool IsVenueNsfw(DirectoryVenue venue)
    {
        return IsOpenlyNsfwVenue(venue);
    }

    private static bool IsOpenlyNsfwVenue(DirectoryVenue venue) => venue.Sfw == false;

    private static bool HasAdultServicesTag(DirectoryVenue venue) =>
        venue.Tags?.Any(tag => !string.IsNullOrWhiteSpace(tag) &&
                               tag.Contains("courtesan", StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetVenueWarningText(bool openlyNsfw, bool hasAdultServices)
    {
        if (hasAdultServices && openlyNsfw)
        {
            return "This venue has indicated they are openly NSFW and offer adult services. You must not visit this venue if you are under 18 years of age or the legal age of consent in your country, and by visiting you declare you are not. Be prepared to verify your age.";
        }

        if (hasAdultServices && !openlyNsfw)
        {
            return "This venue has indicated they offer adult services. You must not partake in these services if you are under 18 years of age or the legal age of consent in your country, and by partaking in these services you declare you are not. Be prepared to verify your age.";
        }

        if (!hasAdultServices && openlyNsfw)
        {
            return "This venue has indicated they are openly NSFW. You must not visit this venue if you are under 18 years of age or the legal age of consent in your country, and by visiting you declare you are not. Be prepared to verify your age.";
        }

        return null;
    }

    private string GetVenueTypeLabel(DirectoryVenue venue)
    {
        if (IsApartmentLocation(venue.Location))
        {
            return "A";
        }

        if (_housingPlotSizeResolver.TryGetSize(venue.Location, out var size))
        {
            return size switch
            {
                HousingPlotSize.Small => "S",
                HousingPlotSize.Medium => "M",
                HousingPlotSize.Large => "L",
                _ => "?"
            };
        }

        return "?";
    }

    private int GetVenueTypeSortKey(DirectoryVenue venue)
    {
        if (IsApartmentLocation(venue.Location))
        {
            return 0;
        }

        if (_housingPlotSizeResolver.TryGetSize(venue.Location, out var size))
        {
            return size switch
            {
                HousingPlotSize.Small => 1,
                HousingPlotSize.Medium => 2,
                HousingPlotSize.Large => 3,
                _ => 4
            };
        }

        return 4;
    }

    private static string GetLocationKey(DirectoryVenue venue) =>
        $"{venue.Location?.DataCenter}-{venue.Location?.World}-{venue.Location?.District}-{venue.Location?.Ward}-{venue.Location?.Plot}";

    private static int CompareDirectorySchedules(DirectorySchedule? left, DirectorySchedule? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return 1;
        }

        if (right == null)
        {
            return -1;
        }

        var comparison = left.Day.CompareTo(right.Day);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = (left.Start?.Hour ?? 0).CompareTo(right.Start?.Hour ?? 0);
        if (comparison != 0)
        {
            return comparison;
        }

        return (left.Start?.Minute ?? 0).CompareTo(right.Start?.Minute ?? 0);
    }

    private static string FormatScheduleLabel(DirectorySchedule schedule)
    {
        var interval = FormatInterval(schedule.Interval);
        if (string.Equals(interval, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            return "Daily";
        }

        // Weekly ownership must stay anchored to the source schedule day.
        return $"{interval} on {PluralizeDay(GetScheduleOwningDay(schedule.Day))}";
    }

    private static bool TryFormatLocalScheduleRange(
        DirectorySchedule schedule,
        DateTime referenceUtc,
        string shortTimePattern,
        TimeZoneInfo localTimeZone,
        Dictionary<string, ScheduleTimeZoneContext> timeZoneContexts,
        out string start,
        out string end)
    {
        start = string.Empty;
        end = string.Empty;

        var owningDay = GetScheduleOwningDay(schedule.Day);

        var hasStartContext = TryGetScheduleTimeZoneContext(
            schedule.Start?.TimeZone ?? string.Empty,
            referenceUtc,
            timeZoneContexts,
            out var startContext);
        if (!hasStartContext)
        {
            return false;
        }

        var reuseEndContext = schedule.End != null &&
                              string.Equals(
                                  NormalizeTimeZoneCacheKey(schedule.Start?.TimeZone),
                                  NormalizeTimeZoneCacheKey(schedule.End.TimeZone),
                                  StringComparison.OrdinalIgnoreCase);

        var endContext = startContext;
        if (!reuseEndContext)
        {
            reuseEndContext = TryGetScheduleTimeZoneContext(
                schedule.End?.TimeZone ?? string.Empty,
                referenceUtc,
                timeZoneContexts,
                out endContext);
            if (!reuseEndContext)
            {
                return false;
            }
        }

        if (!TryFormatLocalTime(schedule.Start, owningDay, shortTimePattern, localTimeZone, startContext, out start) ||
            !TryFormatLocalTime(schedule.End, owningDay, shortTimePattern, localTimeZone, endContext, out end))
        {
            return false;
        }

        return true;
    }

    private static string FormatShortTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString(PluginTimeFormat, CultureInfo.CurrentCulture);

    private static bool TryFormatLocalTime(
        DirectoryTime? time,
        DayOfWeek owningDay,
        string shortTimePattern,
        TimeZoneInfo localTimeZone,
        ScheduleTimeZoneContext timeZoneContext,
        out string formatted)
    {
        formatted = string.Empty;
        if (time == null)
        {
            return false;
        }

        var sourceTime = GetNextDateForDay(timeZoneContext.SourceNow, owningDay)
            .AddHours(time.Hour)
            .AddMinutes(time.Minute);
        if (time.NextDay)
        {
            sourceTime = sourceTime.AddDays(1);
        }

        var unspecified = DateTime.SpecifyKind(sourceTime, DateTimeKind.Unspecified);
        var localTime = TimeZoneInfo.ConvertTime(unspecified, timeZoneContext.TimeZone, localTimeZone);
        formatted = localTime.ToString(shortTimePattern, CultureInfo.CurrentCulture);
        return true;
    }

    private static DateTime GetNextDateForDay(DateTime reference, DayOfWeek target)
    {
        var diff = ((int)target - (int)reference.DayOfWeek + 7) % 7;
        return reference.Date.AddDays(diff);
    }

    private static DayOfWeek GetScheduleOwningDay(DirectoryDay day) => day switch
    {
        DirectoryDay.Monday => DayOfWeek.Monday,
        DirectoryDay.Tuesday => DayOfWeek.Tuesday,
        DirectoryDay.Wednesday => DayOfWeek.Wednesday,
        DirectoryDay.Thursday => DayOfWeek.Thursday,
        DirectoryDay.Friday => DayOfWeek.Friday,
        DirectoryDay.Saturday => DayOfWeek.Saturday,
        DirectoryDay.Sunday => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Unknown schedule day value.")
    };

    private static string PluralizeDay(DayOfWeek day) => PluralizeDay(day.ToString());

    private static string PluralizeDay(string name)
    {
        if (name.EndsWith("day", StringComparison.OrdinalIgnoreCase))
        {
            return name + "s";
        }

        return name;
    }

    private static string FormatTime(DirectoryTime? time, DateTime referenceUtc)
    {
        if (time == null)
        {
            return "--";
        }

        var suffix = time.NextDay ? " (+1)" : string.Empty;
        var abbreviation = GetTimeZoneAbbreviation(time.TimeZone, referenceUtc);
        return $"{time.Hour:00}:{time.Minute:00} {abbreviation}{suffix}";
    }

    private static bool TryGetScheduleTimeZoneContext(
        string timeZoneId,
        DateTime referenceUtc,
        Dictionary<string, ScheduleTimeZoneContext> timeZoneContexts,
        out ScheduleTimeZoneContext context)
    {
        var cacheKey = NormalizeTimeZoneCacheKey(timeZoneId);
        if (cacheKey.Length == 0)
        {
            context = default;
            return false;
        }

        if (timeZoneContexts.TryGetValue(cacheKey, out context))
        {
            return true;
        }

        if (!TryGetTimeZoneInfo(cacheKey, out var timeZone))
        {
            context = default;
            return false;
        }

        context = new ScheduleTimeZoneContext(
            cacheKey,
            timeZone,
            TimeZoneInfo.ConvertTimeFromUtc(referenceUtc, timeZone));
        timeZoneContexts[cacheKey] = context;
        return true;
    }

    private static bool TryGetTimeZoneInfo(string timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        var trimmed = NormalizeTimeZoneCacheKey(timeZoneId);
        if (trimmed.Length == 0)
        {
            return false;
        }

        lock (TimeZoneInfoCacheLock)
        {
            if (TimeZoneInfoCache.TryGetValue(trimmed, out var cachedTimeZone))
            {
                if (cachedTimeZone == null)
                {
                    return false;
                }

                timeZone = cachedTimeZone;
                return true;
            }
        }

        TimeZoneInfo? resolvedTimeZone = null;
        try
        {
            resolvedTimeZone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch
        {
            try
            {
                resolvedTimeZone = TZConvert.GetTimeZoneInfo(trimmed);
            }
            catch
            {
                resolvedTimeZone = null;
            }
        }

        lock (TimeZoneInfoCacheLock)
        {
            TimeZoneInfoCache[trimmed] = resolvedTimeZone;
        }

        if (resolvedTimeZone == null)
        {
            return false;
        }

        timeZone = resolvedTimeZone;
        return true;
    }

    private static string GetTimeZoneAbbreviation(string timeZoneId, DateTime referenceUtc)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return "UTC";
        }

        var trimmed = NormalizeTimeZoneCacheKey(timeZoneId);
        if (string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return "UTC";
        }

        if (string.Equals(trimmed, "GMT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            return "GMT";
        }

        if (!TryGetTimeZoneInfo(trimmed, out var timeZone))
        {
            return trimmed;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(referenceUtc, timeZone);
        var isDst = timeZone.IsDaylightSavingTime(local);
        var name = isDst ? timeZone.DaylightName : timeZone.StandardName;
        if (TimeZoneAbbreviationMap.TryGetValue(name, out var abbreviation) &&
            !string.IsNullOrWhiteSpace(abbreviation))
        {
            return abbreviation;
        }

        return AbbreviateName(name);
    }

    private static string NormalizeTimeZoneCacheKey(string? timeZoneId) =>
        string.IsNullOrWhiteSpace(timeZoneId) ? string.Empty : timeZoneId.Trim();

    private static string AbbreviateName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = new List<char>(parts.Length);
        foreach (var part in parts)
        {
            if (string.Equals(part, "Standard", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Daylight", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Summer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Time", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            letters.Add(char.ToUpperInvariant(part[0]));
        }

        return letters.Count == 0 ? name : new string(letters.ToArray());
    }

    private static string FormatInterval(DirectoryInterval? interval)
    {
        if (interval == null)
        {
            return "Unknown";
        }

        var intervalType = interval.IntervalType switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var n) => n.ToString(CultureInfo.InvariantCulture),
            JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
            _ => interval.IntervalType.ToString()
        };

        var argument = interval.IntervalArgument switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), out var n) => n,
            _ => int.TryParse(interval.IntervalArgument.ToString(), out var parsed) ? parsed : 0
        };

        return intervalType switch
        {
            "EveryXWeeks" or "0" => argument <= 1 ? "Weekly" : $"Every {argument} weeks",
            "EveryXDays" or "1" => Math.Abs(argument) <= 1 ? "Daily" : $"Every {Math.Abs(argument)} days",
            "EveryXMonths" => argument <= 1 ? "Monthly" : $"Every {argument} months",
            "EveryXHours" => argument <= 1 ? "Hourly" : $"Every {argument} hours",
            "EveryXMinutes" => argument <= 1 ? "Every minute" : $"Every {argument} minutes",
            "Once" => "One-time",
            null => "Unknown",
            _ => argument > 0 ? $"{intervalType} ({argument})" : intervalType
        };
    }

    private static string FormatRelativeTime(DateTimeOffset value)
    {
        var span = DateTimeOffset.UtcNow - value;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 1.5) return "a minute ago";
        if (span.TotalHours < 1) return $"{Math.Round(span.TotalMinutes)} minutes ago";
        if (span.TotalHours < 1.5) return "an hour ago";
        if (span.TotalHours < 24) return $"{Math.Round(span.TotalHours)} hours ago";
        if (span.TotalDays < 2) return "yesterday";
        return $"{Math.Round(span.TotalDays)} days ago";
    }

    private static bool Contains(string? source, string search)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var normalizedSource = NormalizeForSearch(source);
        if (normalizedSource.Length == 0)
        {
            return false;
        }

        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(normalizedSource, search, CompareOptions.IgnoreCase) >= 0;
    }

    private static float Scale(float value) => value * ImGuiHelpers.GlobalScale;
}
