using System.Collections.Generic;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>
    /// Restrained, architectural palette: warm paper neutrals, one deep green accent,
    /// a clay secondary, and muted status colours.
    /// </summary>
    public static class Palette
    {
        public static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;
        }

        // Interface
        public static readonly Color Paper = Hex("#F4F2EE");
        public static readonly Color Surface = Hex("#FFFFFF");
        public static readonly Color Line = Hex("#E4E0D8");
        public static readonly Color Ink = Hex("#1B1C1E");
        public static readonly Color Ink2 = Hex("#55585E");
        public static readonly Color Muted = Hex("#8A8D93");
        public static readonly Color Accent = Hex("#2F5D50");
        public static readonly Color AccentSoft = Hex("#E3ECE8");
        public static readonly Color Clay = Hex("#B9714A");
        public static readonly Color Slate = Hex("#5F7391");
        public static readonly Color Sand = Hex("#C8A96A");

        // Status
        public static readonly Color Ok = Hex("#3C7A57");
        public static readonly Color Watch = Hex("#B7791F");
        public static readonly Color Critical = Hex("#B4443A");

        // Scene
        public static readonly Color Sky3D = Hex("#ECEDEA");
        public static readonly Color Ground = Hex("#D9DFCF");
        public static readonly Color PlanPaper = Hex("#F7F6F2");
        public static readonly Color PlanInk = Hex("#2B2D31");
        public static readonly Color WallInterior = Hex("#FAF9F6");
        public static readonly Color WallExterior = Hex("#EFEBE4");
        public static readonly Color Glass = Hex("#C4D8DF");
        public static readonly Color Frame = Hex("#2E3034");

        static readonly Dictionary<string, string> FloorFinish = new()
        {
            ["living"] = "#DCCBB2", ["dining"] = "#DCCBB2", ["bedroom"] = "#D9CDBB", ["primary"] = "#D6C6AE",
            ["office"] = "#D9CDBB", ["game"] = "#D3C4AD", ["media"] = "#C9C1B6", ["hall"] = "#DCCBB2",
            ["stair"] = "#CDBBA0", ["kitchen"] = "#E9E6E0", ["bath"] = "#ECEBE7", ["laundry"] = "#E6E3DD",
            ["mudroom"] = "#E1DDD5", ["pantry"] = "#E6E3DD", ["closet"] = "#D9CDBB", ["garage"] = "#D3D2CE",
            ["patio"] = "#D6D1C7", ["deck"] = "#B79C7E", ["pool"] = "#7FB3C4",
        };

        /// <summary>Material finish for a room's floor (oak, tile, concrete…).</summary>
        public static Color Floor(string kind) => FloorFinish.TryGetValue(kind, out var h) ? Hex(h) : Hex("#DDD6CA");

        public static Color Severity(string s) => s switch
        {
            "critical" => Critical,
            "watch" => Watch,
            _ => Ok,
        };

        public static Color Tier(string tier) => tier switch
        {
            "severe" => Hex("#B4443A"),
            "high" => Hex("#C2683A"),
            "elevated" => Hex("#C49A3A"),
            _ => Hex("#5E8C76"),
        };

        public static Color Agent(string agent) => agent switch
        {
            "structural" => Clay,
            "operations" => Hex("#8E5A7A"),
            "market" => Slate,
            "user" => Ink,
            _ => Accent,
        };

        public static string KindLabel(string kind) => kind switch
        {
            "roof" => "Roof",
            "gutters" => "Drainage",
            "foundation" => "Foundation",
            "plumbing" => "Plumbing",
            "sewer" => "Sewer",
            "electrical" => "Electrical",
            "hvac" => "HVAC",
            "water_heater" => "Water heater",
            "windows" => "Windows",
            "attic_insulation" => "Insulation",
            "drywall" => "Finishes",
            "mold" => "Air & moisture",
            _ => kind,
        };

        public static string Money(double v)
        {
            var a = System.Math.Abs(v);
            var s = a >= 1_000_000 ? $"${a / 1_000_000:0.00}M" : a >= 10_000 ? $"${a / 1000:0}k" : $"${a:#,0}";
            return v < 0 ? "−" + s : s;
        }

        public static string MoneyFull(double v) => (v < 0 ? "−" : "") + "$" + System.Math.Abs(v).ToString("#,0");
        public static string Pct(double v) => $"{v * 100:0}%";
    }
}
