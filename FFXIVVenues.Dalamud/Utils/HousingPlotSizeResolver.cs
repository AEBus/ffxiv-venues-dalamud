using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFXIVVenues.VenueModels;

namespace FFXIVVenues.Dalamud.Utils;

internal static class HousingPlotSizeResolver
{
    private static readonly PlotSize[] Mist =
        Parse("1,2,0,1,2,1,1,0,0,0,0,0,0,1,2,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,2,0,1,2,1,1,0,0,0,0,0,0,1,2,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1");

    private static readonly PlotSize[] LavenderBeds =
        Parse("1,0,2,0,1,2,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,0,1,2,0,1,1,0,2,0,1,2,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,0,1,2,0,1");

    private static readonly PlotSize[] Goblet =
        Parse("0,0,0,1,2,1,0,1,0,0,1,1,2,0,0,0,0,0,1,0,0,0,0,0,1,0,0,0,0,2,0,0,0,1,2,1,0,1,0,0,1,1,2,0,0,0,0,0,1,0,0,0,0,0,1,0,0,0,0,2");

    private static readonly PlotSize[] Shirogane =
        Parse("1,0,0,0,0,0,2,1,0,0,0,0,1,0,1,2,0,0,1,0,0,0,0,1,0,0,0,1,0,2,1,0,0,0,0,0,2,1,0,0,0,0,1,0,1,2,0,0,1,0,0,0,0,1,0,0,0,1,0,2");

    private static readonly PlotSize[] Empyreum =
        Parse("0,1,0,0,0,0,1,1,0,0,0,2,0,0,0,0,1,1,0,0,1,2,0,0,0,1,0,0,0,2,0,1,0,0,0,0,1,1,0,0,0,2,0,0,0,0,1,1,0,0,1,2,0,0,0,1,0,0,0,2");

    private static readonly Dictionary<string, PlotSize[]> DistrictMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mist"] = Mist,
        ["lavender beds"] = LavenderBeds,
        ["the lavender beds"] = LavenderBeds,
        ["goblet"] = Goblet,
        ["the goblet"] = Goblet,
        ["shirogane"] = Shirogane,
        ["empyreum"] = Empyreum,
    };

    public static bool TryGetSize(Location? location, out PlotSize size)
    {
        size = default;
        if (location == null || location.Plot <= 0)
            return false;

        var district = NormalizeDistrict(location.District);
        if (district == null || !DistrictMapping.TryGetValue(district, out var sizes))
            return false;

        var plotIndex = location.Plot - 1;
        if (plotIndex < 0 || plotIndex >= sizes.Length)
            return false;

        size = sizes[plotIndex];
        return true;
    }

    private static string? NormalizeDistrict(string? district)
    {
        if (string.IsNullOrWhiteSpace(district))
            return null;

        var normalized = district.Trim();
        if (normalized.StartsWith("the ", true, CultureInfo.InvariantCulture))
            normalized = normalized[4..];

        return normalized.ToLowerInvariant();
    }

    private static PlotSize[] Parse(string csv)
    {
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => (PlotSize)int.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();
    }
}

internal enum PlotSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}
