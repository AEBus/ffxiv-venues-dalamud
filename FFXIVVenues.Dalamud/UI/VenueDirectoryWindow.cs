using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using FFXIVVenues.Dalamud.UI.Abstractions;
using FFXIVVenues.Dalamud.Utils;
using FFXIVVenues.VenueModels;
using TimeZoneConverter;

namespace FFXIVVenues.Dalamud.UI;

internal class VenueDirectoryWindow : Window
{
    private const float BannerMaxWidth = 520f;
    private const float MinPanelWidth = 320f;
    private static readonly string[] Regions = { "North America", "Europe", "Oceania", "Japan" };
    private static readonly Vector4 DefaultSectionBackground = new(0.20f, 0.20f, 0.23f, 0.95f);
    private static readonly Vector4 HighlightSectionBackground = new(0.12f, 0.12f, 0.12f, 0.95f);
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLinkRegex = new(@"\[(.*?)\]\((.*?)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownStrongRegex = new(@"(\*\*|__|~~)(.*?)\1", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownEmRegex = new(@"(\*|_)(.*?)\1", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OnlyPunctuationLineRegex = new(@"^[=\-_*`~|+\\/]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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

    private readonly HttpClient _httpClient;
    private readonly VenueService _venueService;

    private Task<Venue[]?>? _venuesTask;
    private Venue[]? _venues;
    private string? _loadError;
    private DateTimeOffset _lastRefresh;

    private string? _selectedVenueId;
    private string _searchText = string.Empty;
    private string _tagFilter = string.Empty;
    private string? _selectedRegion;
    private string? _selectedDataCenter;
    private string? _selectedWorld;
    private bool _onlyOpen = true;
    private bool _withinWeek;
    private bool _sfwOnly;
    private bool _nsfwOnly;
    private bool _sizeSmall = true;
    private bool _sizeMedium = true;
    private bool _sizeLarge = true;

    private readonly List<string> _dataCenters = new();
    private readonly List<string> _regions = new();
    private readonly List<string> _worlds = new();
    private float _splitRatio = 0.42f;
    private float _rightPaneWidth;

    public VenueDirectoryWindow(UiBuilder uiBuilder, HttpClient httpClient, VenueService venueService)
        : base(uiBuilder)
    {
        _httpClient = httpClient;
        _venueService = venueService;

        InitialSize = new Vector2(1200, 800);
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue);
        Title = "FFXIV Venues Directory";
    }

    public override void Render()
    {
        EnsureVenuesRequested();
        TryConsumeVenueTask();

        if (_venues == null)
        {
            DrawLoadingState();
            return;
        }

        var filteredVenues = ApplyFilters(_venues).ToList();
        EnsureSelection(filteredVenues);

        DrawToolbar(filteredVenues.Count);
        ImGui.Separator();

        var region = ImGui.GetContentRegionAvail();
        var splitterWidth = Math.Max(4f, ImGui.GetStyle().ItemSpacing.X);
        var usableWidth = Math.Max(region.X - splitterWidth, MinPanelWidth * 2);
        var leftWidth = Math.Clamp(usableWidth * _splitRatio, MinPanelWidth, usableWidth - MinPanelWidth);
        var rightWidth = usableWidth - leftWidth;
        if (rightWidth > BannerMaxWidth)
        {
            rightWidth = BannerMaxWidth;
            leftWidth = usableWidth - rightWidth;
            _splitRatio = leftWidth / usableWidth;
        }

        _rightPaneWidth = rightWidth;

        ImGui.BeginChild("VenueListPane", new Vector2(leftWidth, 0), true);
        DrawFilters();
        ImGui.Separator();
        DrawVenueTable(filteredVenues);
        ImGui.EndChild();

        ImGui.SameLine(0f, 0f);
        ImGui.InvisibleButton("##VenueSplitter", new Vector2(splitterWidth, region.Y));
        var splitterMin = ImGui.GetItemRectMin();
        var splitterMax = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(splitterMin, splitterMax, ImGui.GetColorU32(ImGuiCol.Border));
        if (ImGui.IsItemActive())
        {
            var newLeft = Math.Clamp(leftWidth + ImGui.GetIO().MouseDelta.X, MinPanelWidth, usableWidth - MinPanelWidth);
            _splitRatio = Math.Clamp(newLeft / usableWidth, 0.15f, 0.85f);
        }

        ImGui.SameLine(0f, 0f);
        ImGui.BeginChild("VenueDetailPane", new Vector2(rightWidth, 0), true);
        var selected = filteredVenues.FirstOrDefault(v => v.Id == _selectedVenueId);
        if (selected == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Select a venue from the list to see its details.");
        }
        else
        {
            DrawVenueDetails(selected);
        }

        ImGui.EndChild();
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
        _venuesTask = _httpClient.GetFromJsonAsync<Venue[]>("venue?approved=true");
        _loadError = null;
        _venues = null;
    }

    private void TryConsumeVenueTask()
    {
        if (_venuesTask == null || !_venuesTask.IsCompleted)
        {
            return;
        }

        if (_venuesTask.IsFaulted)
        {
            _loadError = _venuesTask.Exception?.GetBaseException().Message ?? "Failed to load venues.";
            _venuesTask = null;
            return;
        }

        _venues = _venuesTask.Result ?? Array.Empty<Venue>();
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
        _selectedVenueId = _venues.FirstOrDefault()?.Id;
    }

    private void DrawLoadingState()
    {
        if (!string.IsNullOrEmpty(_loadError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, _loadError);
            if (ImGui.Button("Retry"))
            {
                TriggerRefresh();
            }

            return;
        }

        ImGui.Text("Fetching venues from api.ffxivvenues.com...");
    }

    private void DrawToolbar(int visibleCount)
    {
        if (ImGui.Button("Refresh"))
        {
            TriggerRefresh();
        }

        ImGui.SameLine();
        ImGui.Text($"{visibleCount} / {_venues?.Length ?? 0} venues");

        if (_lastRefresh != default)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Updated {FormatRelativeTime(_lastRefresh)}");
        }

        if (!string.IsNullOrEmpty(_loadError))
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudRed, _loadError);
        }
    }

    private void DrawFilters()
    {
        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##VenueSearch", "Search by name, description or tag...", ref _searchText, 160);
        ImGui.PopItemWidth();

        if (ImGui.BeginTable("FilterLayout", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginCombo("Region", _selectedRegion ?? "Any"))
            {
                if (ImGui.Selectable("Any", _selectedRegion == null))
                {
                    SetRegion(null);
                }

                ImGui.Separator();
                foreach (var region in _regions)
                {
                    if (ImGui.Selectable(region, string.Equals(region, _selectedRegion, StringComparison.OrdinalIgnoreCase)))
                    {
                        SetRegion(region);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            if (ImGui.BeginCombo("Data Center", _selectedDataCenter ?? "Any"))
            {
                if (ImGui.Selectable("Any", _selectedDataCenter == null))
                {
                    SetDataCenter(null);
                }

                ImGui.Separator();
                foreach (var dc in GetRegionDataCenters())
                {
                    if (ImGui.Selectable(dc, string.Equals(dc, _selectedDataCenter, StringComparison.OrdinalIgnoreCase)))
                    {
                        SetDataCenter(dc);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.BeginCombo("World", _selectedWorld ?? "Any"))
            {
                if (ImGui.Selectable("Any", _selectedWorld == null))
                {
                    _selectedWorld = null;
                }

                ImGui.Separator();
                foreach (var world in _worlds)
                {
                    if (ImGui.Selectable(world, string.Equals(world, _selectedWorld, StringComparison.OrdinalIgnoreCase)))
                    {
                        _selectedWorld = world;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.TableNextColumn();
            ImGui.InputTextWithHint("Tags", "Comma separated tags", ref _tagFilter, 128);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawSizeFilter();
            ImGui.TableNextColumn();
            DrawFilterToggles();

            ImGui.EndTable();
        }
    }

    private void DrawSizeFilter()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("House size");
        ImGui.SameLine(0, 20);

        DrawSizeToggle("Small", ref _sizeSmall);
        ImGui.SameLine();
        DrawSizeToggle("Medium", ref _sizeMedium);
        ImGui.SameLine();
        DrawSizeToggle("Large", ref _sizeLarge);
    }

    private void DrawSizeToggle(string label, ref bool flag)
    {
        if (ImGui.Checkbox(label, ref flag) && !_sizeSmall && !_sizeMedium && !_sizeLarge)
        {
            flag = true;
        }
    }

    private void DrawFilterToggles()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Filters");
        ImGui.SameLine(0, 20);

        ImGui.Checkbox("Open now", ref _onlyOpen);
        ImGui.SameLine();
        ImGui.Checkbox("Next week", ref _withinWeek);
        ImGui.SameLine();
        if (ImGui.Checkbox("SFW only", ref _sfwOnly) && _sfwOnly)
        {
            _nsfwOnly = false;
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("NSFW only", ref _nsfwOnly) && _nsfwOnly)
        {
            _sfwOnly = false;
        }
    }

    private void DrawVenueTable(IReadOnlyList<Venue> venues)
    {
        var flags = ImGuiTableFlags.BordersInner | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable;
        var size = ImGui.GetContentRegionAvail();
        if (ImGui.BeginTable("VenueSummaryTable", 3, flags, size))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Venue", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 0.35f);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthStretch, 0.40f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.25f);
            ImGui.TableHeadersRow();

            var sorted = SortVenues(venues);
            foreach (var venue in sorted)
            {
                var open = venue.Resolution?.IsNow == true;
                var nameColor = open ? ImGuiColors.DalamudViolet : ImGuiColors.ParsedGrey;
                var statusText = FormatStatusLine(venue);
                var isSelected = string.Equals(_selectedVenueId, venue.Id, StringComparison.Ordinal);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(venue.Id);
                var displayName = string.IsNullOrWhiteSpace(venue.Name) ? "Unnamed venue" : NormalizeFancyText(venue.Name);
                if (ImGui.Selectable(displayName, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedVenueId = venue.Id;
                }

                ImGui.PopID();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(FormatAddress(venue.Location));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(nameColor, statusText);
            }

            ImGui.EndTable();
        }
    }

    private void DrawVenueDetails(Venue venue)
    {
        var banner = _venueService.GetVenueBanner(venue.Id, venue.BannerUri);
        if (banner != null)
        {
            var padding = ImGui.GetStyle().WindowPadding.X * 2f;
            var maxWidth = MathF.Max(0f, _rightPaneWidth - padding);
            var width = MathF.Min(maxWidth, BannerMaxWidth);
            var aspect = banner.Width == 0 ? 0.5f : banner.Height / (float)banner.Width;
            var size = new Vector2(width, MathF.Max(120f, width * aspect));
            ImGui.Image(banner.Handle, size);
        }

        ImGui.SetWindowFontScale(1.5f);
        var headerName = string.IsNullOrWhiteSpace(venue.Name) ? "Unnamed Venue" : NormalizeFancyText(venue.Name);
        var headerSize = ImGui.CalcTextSize(headerName);
        var headerPos = ImGui.GetCursorScreenPos();
        var headerDrawList = ImGui.GetWindowDrawList();
        var headerShadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        var headerColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        headerDrawList.AddText(new Vector2(headerPos.X - 1f, headerPos.Y), headerShadow, headerName);
        headerDrawList.AddText(new Vector2(headerPos.X + 1f, headerPos.Y), headerShadow, headerName);
        headerDrawList.AddText(new Vector2(headerPos.X, headerPos.Y - 1f), headerShadow, headerName);
        headerDrawList.AddText(new Vector2(headerPos.X, headerPos.Y + 1f), headerShadow, headerName);
        headerDrawList.AddText(headerPos, headerColor, headerName);
        ImGui.Dummy(new Vector2(headerSize.X, headerSize.Y));
        ImGui.SetWindowFontScale(1f);

        var location = FormatAddressDetailed(venue.Location);
        ImGui.TextDisabled(location);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        if (ImGui.Button("Copy address"))
        {
            ImGui.SetClipboardText(location);
        }

        ImGui.SameLine();
        if (ImGui.Button("Visit (Lifestream)"))
        {
            var command = FormatLifestreamCommand(venue.Location);
            if (!string.IsNullOrEmpty(command))
            {
                if (!PluginService.CommandManager.ProcessCommand(command))
                {
                    PluginService.ChatGui.PrintError($"Failed to execute {command}");
                }
            }
        }

        if (venue.Website != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Website"))
            {
                OpenBrowser(venue.Website.ToString());
            }
        }

        if (venue.Discord != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Discord"))
            {
                OpenBrowser(venue.Discord.ToString());
            }
        }

        ImGui.PopStyleVar(2);

        if (!venue.Sfw)
        {
            DrawSection("NsfwWarningCard", HighlightSectionBackground, () =>
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "Warning:");
                ImGui.SameLine();
                ImGui.TextWrapped("This venue has indicated they are openly NSFW. You must not visit this venue if you are under 18 years of age or the legal age of consent in your country, and by visiting you declare you are not. Be prepared to verify your age.");
            });
        }

        if (venue.Description?.Count > 0)
        {
            DrawSection("DescriptionCard", () =>
            {
                foreach (var para in venue.Description)
                {
                    if (string.IsNullOrWhiteSpace(para))
                    {
                        continue;
                    }

                    var sanitized = SanitizeDescription(para);
                    if (string.IsNullOrWhiteSpace(sanitized))
                    {
                        continue;
                    }

                    ImGui.TextWrapped(sanitized);
                }
            });
        }

        DrawSection("ScheduleCard", HighlightSectionBackground, () =>
        {
            if (venue.Resolution != null)
            {
                var resolution = venue.Resolution;
                var label = resolution.IsNow
                    ? $"Open now until {FormatShortTime(resolution.End)}!"
                    : $"Next open {resolution.Start.ToLocalTime().ToString("dddd", CultureInfo.InvariantCulture)} at {FormatShortTime(resolution.Start)}";
                ImGui.TextColored(ImGuiColors.DalamudViolet, label);
            }

            if (venue.Schedule?.Count > 0)
            {
                var tableFlags = ImGuiTableFlags.SizingStretchProp |
                                 ImGuiTableFlags.NoHostExtendX |
                                 ImGuiTableFlags.BordersOuterH |
                                 ImGuiTableFlags.BordersInnerH;
                if (ImGui.BeginTable("VenueScheduleTable", 2, tableFlags))
                {
                    ImGui.TableSetupColumn("Day", ImGuiTableColumnFlags.WidthStretch, 0.62f);
                    ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.38f);

                    foreach (var schedule in venue.Schedule.OrderBy(s => s.Day).ThenBy(s => s.Start.Hour))
                    {
                        var (start, end, _, localDay) = FormatScheduleTimes(schedule, DateTime.Now.DayOfWeek);
                        var label = FormatScheduleLabel(schedule, localDay);
                        var isActive = schedule.Resolution?.IsNow == true;
                        var labelColor = isActive ? ImGuiColors.DalamudViolet : ImGuiColors.DalamudWhite;

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextColored(labelColor, label);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextColored(labelColor, $"{start} - {end}");
                    }

                    ImGui.EndTable();
                }

                ImGui.TextColored(ImGuiColors.DalamudGrey, "All times are in your timezone.");
            }
        });

        if (venue.Tags?.Count > 0)
        {
            DrawSection("TagsCard", () => DrawTagChips(venue.Tags));
        }
    }

    private void DrawSection(string id, Action content) =>
        DrawSection(id, null, content);

    private void DrawSection(string id, Vector4? backgroundOverride, Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var contentWidth = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var bg = ImGui.GetColorU32(backgroundOverride ?? DefaultSectionBackground);
        var border = ImGui.GetColorU32(ImGuiCol.Border);
        var padding = new Vector2(12f, 10f);
        ImGui.PushID(id);
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(0, padding.Y));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding.X);
        ImGui.BeginGroup();
        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudWhite);
        content();
        ImGui.PopStyleColor();
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, padding.Y));
        ImGui.EndGroup();
        drawList.ChannelsSetCurrent(0);
        ImGui.PopID();

        var min = startPos;
        var max = ImGui.GetItemRectMax();
        max = new Vector2(min.X + contentWidth, max.Y);
        drawList.AddRectFilled(min, max, bg, 6f);
        drawList.AddRect(min, max, border, 6f);
        drawList.ChannelsMerge();

        ImGui.Spacing();
    }

    private static void DrawTagChips(IEnumerable<string> tags)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var framePadding = new Vector2(8f, 4f);
        var startPosX = ImGui.GetCursorPosX();
        var maxWidth = ImGui.GetContentRegionAvail().X;
        var x = startPosX;
        var y = ImGui.GetCursorPosY();
        var rowHeight = 0f;
        var startScreen = ImGui.GetCursorScreenPos();
        var rightEdge = startScreen.X + maxWidth;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, framePadding);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);

        foreach (var tag in tags)
        {
            var size = ImGui.CalcTextSize(tag);
            var chipWidth = size.X + framePadding.X * 2f;
            var chipHeight = size.Y + framePadding.Y * 2f;

            var screenX = startScreen.X + (x - startPosX);
            if (screenX + chipWidth > rightEdge && x > startPosX)
            {
                x = startPosX;
                y += rowHeight + spacing;
                rowHeight = 0f;
            }

            ImGui.SetCursorPos(new Vector2(x, y));
            var min = ImGui.GetCursorScreenPos();
            var max = new Vector2(min.X + chipWidth, min.Y + chipHeight);
            var bg = ImGui.GetColorU32(new Vector4(0.24f, 0.24f, 0.28f, 1f));
            var border = ImGui.GetColorU32(ImGuiCol.Border);
            drawList.AddRectFilled(min, max, bg, 6f);
            drawList.AddRect(min, max, border, 6f);
            drawList.AddText(new Vector2(min.X + framePadding.X, min.Y + framePadding.Y), ImGui.GetColorU32(ImGuiCol.Text), tag);
            ImGui.Dummy(new Vector2(chipWidth, chipHeight));
            x += chipWidth + spacing;
            rowHeight = MathF.Max(rowHeight, chipHeight);
        }

        ImGui.SetCursorPos(new Vector2(startPosX, y + rowHeight + spacing));
        ImGui.PopStyleVar(2);
    }

    private static string SanitizeDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = NormalizeFancyText(value);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = MarkdownLinkRegex.Replace(text, "$1");
        text = MarkdownStrongRegex.Replace(text, "$2");
        text = MarkdownEmRegex.Replace(text, "$2");
        text = text.Replace("`", string.Empty);
        text = text.Replace("\r\n", "\n");

        var lines = text.Split('\n');
        var cleanedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                cleanedLines.Add(string.Empty);
                continue;
            }

            var buffer = new char[trimmed.Length];
            var count = 0;
            foreach (var ch in trimmed)
            {
                if (ch >= '\u2500' && ch <= '\u259F')
                {
                    continue;
                }

                if (char.IsControl(ch) && ch != '\t')
                {
                    continue;
                }

                buffer[count++] = ch;
            }

            var cleaned = new string(buffer, 0, count).Trim();
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (cleaned.Length > 6 && OnlyPunctuationLineRegex.IsMatch(cleaned))
            {
                continue;
            }

            cleanedLines.Add(cleaned);
        }

        return string.Join("\n", cleanedLines).Trim();
    }

    private static string NormalizeFancyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var hadSmallCaps = false;

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
                hadSmallCaps = true;
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

        var normalized = builder.ToString();
        if (hadSmallCaps)
        {
            normalized = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
        }

        return normalized;
    }

    private IEnumerable<Venue> SortVenues(IEnumerable<Venue> venues)
    {
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount == 0)
        {
            return venues.OrderBy(v => v.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        IOrderedEnumerable<Venue>? ordered = null;
        for (var i = 0; i < sortSpecs.SpecsCount; i++)
        {
            var spec = sortSpecs.Specs[i];
            if (spec.SortDirection == ImGuiSortDirection.None)
            {
                continue;
            }

            Func<Venue, IComparable?> keySelector = spec.ColumnIndex switch
            {
                0 => v => v.Name ?? string.Empty,
                1 => v => GetLocationKey(v),
                2 => v => v.Resolution?.Start ?? DateTimeOffset.MinValue,
                _ => v => v.Name ?? string.Empty
            };

            ordered = ordered == null
                ? spec.SortDirection == ImGuiSortDirection.Ascending
                    ? venues.OrderBy(keySelector)
                    : venues.OrderByDescending(keySelector)
                : spec.SortDirection == ImGuiSortDirection.Ascending
                    ? ordered.ThenBy(keySelector)
                    : ordered.ThenByDescending(keySelector);
        }

        sortSpecs.SpecsDirty = false;
        return ordered ?? venues;
    }

    private IEnumerable<Venue> ApplyFilters(IEnumerable<Venue> venues)
    {
        var query = venues;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var search = _searchText.Trim();
            query = query.Where(v =>
                Contains(v.Name, search) ||
                (v.Description?.Any(d => Contains(d, search)) ?? false) ||
                (v.Tags?.Any(t => Contains(t, search)) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(_tagFilter))
        {
            var tags = _tagFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(v => v.Tags != null &&
                                     tags.All(tag => v.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))));
        }

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

        if (!string.IsNullOrEmpty(_selectedWorld))
        {
            query = query.Where(v => string.Equals(v.Location?.World, _selectedWorld, StringComparison.OrdinalIgnoreCase));
        }

        if (_onlyOpen)
        {
            query = query.Where(v => v.Resolution?.IsNow == true);
        }

        if (_withinWeek)
        {
            query = query.Where(v => v.Resolution?.IsWithinWeek != false);
        }

        if (_sfwOnly)
        {
            query = query.Where(v => v.Sfw);
        }
        else if (_nsfwOnly)
        {
            query = query.Where(v => !v.Sfw);
        }

        var filterBySize = !(_sizeSmall && _sizeMedium && _sizeLarge);
        if (filterBySize)
        {
            query = query.Where(v =>
                HousingPlotSizeResolver.TryGetSize(v.Location, out var size) &&
                ((size == PlotSize.Small && _sizeSmall) ||
                 (size == PlotSize.Medium && _sizeMedium) ||
                 (size == PlotSize.Large && _sizeLarge)));
        }

        return query;
    }

    private void EnsureSelection(IReadOnlyList<Venue> venues)
    {
        if (venues.Count == 0)
        {
            _selectedVenueId = null;
            return;
        }

        if (_selectedVenueId == null || venues.All(v => v.Id != _selectedVenueId))
        {
            _selectedVenueId = venues[0].Id;
        }
    }

    private void SetRegion(string? region)
    {
        _selectedRegion = region;
        _selectedDataCenter = null;
        _selectedWorld = null;
        UpdateWorldOptions();
    }

    private void SetDataCenter(string? dataCenter)
    {
        _selectedDataCenter = dataCenter;
        _selectedWorld = null;
        UpdateWorldOptions();
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

    private string FormatStatusLine(Venue venue)
    {
        if (venue.Resolution == null)
        {
            return "No scheduled openings";
        }

        var startLocal = venue.Resolution.Start.ToLocalTime();
        var endLocal = venue.Resolution.End.ToLocalTime();
        var start = $"{startLocal.ToString("ddd", CultureInfo.InvariantCulture)} {FormatShortTime(startLocal)}";
        var end = FormatShortTime(endLocal);
        return venue.Resolution.IsNow
            ? $"Open until {end}"
            : $"Opens {start}";
    }

    private static string FormatAddress(Location? location)
    {
        if (location == null)
        {
            return "Location unknown";
        }

        if (!string.IsNullOrWhiteSpace(location.Override))
        {
            return location.Override;
        }

        return $"{location.DataCenter}, {location.World}, {location.District}, Ward {location.Ward}, Plot {location.Plot}";
    }

    private static string FormatAddressDetailed(Location? location)
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
        parts.Add($"Plot {location.Plot}");
        if (location.Apartment > 0)
        {
            parts.Add($"Apartment {location.Apartment}");
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

    private static string FormatLifestreamCommand(Location? location)
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
        parts.Add($"Plot {location.Plot}");
        if (location.Apartment > 0)
        {
            parts.Add($"Apartment {location.Apartment}");
        }

        if (location.Room > 0)
        {
            parts.Add($"Room {location.Room}");
        }

        var destination = string.Join(", ", parts);
        return $"/li {destination}";
    }

    private static string GetLocationKey(Venue venue) =>
        $"{venue.Location?.DataCenter}-{venue.Location?.World}-{venue.Location?.District}-{venue.Location?.Ward}-{venue.Location?.Plot}";

    private static string FormatScheduleLabel(Schedule schedule, DayOfWeek? localDay)
    {
        var interval = FormatInterval(schedule.Interval);
        if (string.Equals(interval, "Daily", StringComparison.OrdinalIgnoreCase))
        {
            return "Daily";
        }

        var dayLabel = localDay.HasValue ? PluralizeDay(localDay.Value) : PluralizeDay(schedule.Day);
        return $"{interval} on {dayLabel}";
    }

    private static (string Start, string End, bool IsToday, DayOfWeek? LocalDay) FormatScheduleTimes(
        Schedule schedule,
        DayOfWeek currentDay)
    {
        if (TryFormatLocalTime(schedule.Start, schedule.Day, out var start, out var startLocal) &&
            TryFormatLocalTime(schedule.End, schedule.Day, out var end, out _))
        {
            return (start, end, startLocal.DayOfWeek == currentDay, startLocal.DayOfWeek);
        }

        return (FormatTime(schedule.Start), FormatTime(schedule.End),
            schedule.Day.ToString().Equals(currentDay.ToString(), StringComparison.OrdinalIgnoreCase), null);
    }

    private static string FormatShortTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

    private static bool TryFormatLocalTime(Time? time, Day day, out string formatted, out DateTime localTime)
    {
        formatted = string.Empty;
        localTime = DateTime.MinValue;
        if (time == null)
        {
            return false;
        }

        if (!TryGetTimeZoneInfo(time.TimeZone, out var sourceTimeZone))
        {
            return false;
        }

        if (!TryParseDayOfWeek(day, out var targetDay))
        {
            targetDay = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sourceTimeZone).DayOfWeek;
        }

        var sourceNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sourceTimeZone);
        var sourceDate = GetNextDateForDay(sourceNow, targetDay);
        var sourceTime = sourceDate.AddHours(time.Hour).AddMinutes(time.Minute);
        if (time.NextDay)
        {
            sourceTime = sourceTime.AddDays(1);
        }

        var unspecified = DateTime.SpecifyKind(sourceTime, DateTimeKind.Unspecified);
        localTime = TimeZoneInfo.ConvertTime(unspecified, sourceTimeZone, TimeZoneInfo.Local);
        formatted = localTime.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);
        return true;
    }

    private static DateTime GetNextDateForDay(DateTime reference, DayOfWeek target)
    {
        var diff = ((int)target - (int)reference.DayOfWeek + 7) % 7;
        return reference.Date.AddDays(diff);
    }

    private static bool TryParseDayOfWeek(Day day, out DayOfWeek dayOfWeek) =>
        Enum.TryParse(day.ToString(), true, out dayOfWeek);

    private static string PluralizeDay(Day day) => PluralizeDay(day.ToString());

    private static string PluralizeDay(DayOfWeek day) => PluralizeDay(day.ToString());

    private static string PluralizeDay(string name)
    {
        if (name.EndsWith("day", StringComparison.OrdinalIgnoreCase))
        {
            return name + "s";
        }

        return name;
    }

    private static string FormatTime(Time? time)
    {
        if (time == null)
        {
            return "--";
        }

        var suffix = time.NextDay ? " (+1)" : string.Empty;
        var abbreviation = GetTimeZoneAbbreviation(time.TimeZone, DateTime.UtcNow);
        return $"{time.Hour:00}:{time.Minute:00} {abbreviation}{suffix}";
    }

    private static bool TryGetTimeZoneInfo(string timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        var trimmed = timeZoneId.Trim();
        try
        {
            var windowsId = TZConvert.IanaToWindows(trimmed);
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            return true;
        }
        catch
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetTimeZoneAbbreviation(string timeZoneId, DateTime referenceUtc)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return "UTC";
        }

        var trimmed = timeZoneId.Trim();
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

    private static string FormatInterval(object? interval)
    {
        if (interval == null)
        {
            return "Unknown";
        }

        var intervalType = interval.GetType().GetProperty("IntervalType")?.GetValue(interval)?.ToString();
        var intervalArgument = interval.GetType().GetProperty("IntervalArgument")?.GetValue(interval);
        var argument = intervalArgument switch
        {
            byte value => value,
            short value => value,
            int value => value,
            long value => (int)value,
            _ => int.TryParse(intervalArgument?.ToString(), out var parsed) ? parsed : 0
        };

        return intervalType switch
        {
            "EveryXWeeks" => argument <= 1 ? "Weekly" : $"Every {argument} weeks",
            "EveryXDays" => argument <= 1 ? "Daily" : $"Every {argument} days",
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

        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(source, search, CompareOptions.IgnoreCase) >= 0;
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(url);
        }
        catch
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw;
            }
        }
    }
}
