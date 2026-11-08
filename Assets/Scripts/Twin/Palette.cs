using System.Collections.Generic;
using UnityEngine;

namespace CbreTwin
{
    public static class Palette
    {
        public static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;
        }

        public static readonly Color Indigo = Hex("#5B5BFF");
        public static readonly Color Coral = Hex("#FF7A59");
        public static readonly Color Mint = Hex("#2EC4B6");
        public static readonly Color Sun = Hex("#FFD166");
        public static readonly Color Pink = Hex("#FF5DA2");
        public static readonly Color Sky = Hex("#4DA3FF");
        public static readonly Color Ink = Hex("#1F2A44");
        public static readonly Color Muted = Hex("#6B7A99");
        public static readonly Color NvidiaGreen = Hex("#76B900");

        public static readonly Color Ok = Hex("#34C47C");
        public static readonly Color Watch = Hex("#FFB020");
        public static readonly Color Critical = Hex("#FF4D5E");

        public static readonly Color Sky3D = Hex("#EAF3FF");
        public static readonly Color Lawn = Hex("#CDEBC4");
        public static readonly Color BlueprintBg = Hex("#1E4E8C");
        public static readonly Color BlueprintLine = Hex("#DDEBFF");

        static readonly Dictionary<string, string> RoomColors = new()
        {
            ["living"] = "#FFE3C2", ["kitchen"] = "#FFF1A8", ["dining"] = "#FFD3DE", ["bedroom"] = "#D4E6FF",
            ["primary"] = "#C8D9FF", ["bath"] = "#C6F1EA", ["hall"] = "#F2ECE3", ["laundry"] = "#E5DEFA",
            ["garage"] = "#DFE3EA", ["office"] = "#DDF4CC", ["game"] = "#FFD6F0", ["media"] = "#D9D3FF",
            ["stair"] = "#ECE5DA", ["closet"] = "#EFE9E0", ["pantry"] = "#FBEBD2", ["mudroom"] = "#F0E7D8",
            ["patio"] = "#E6DACA", ["deck"] = "#D8B896", ["pool"] = "#6EC6FF",
        };

        public static Color Room(string kind) => RoomColors.TryGetValue(kind, out var h) ? Hex(h) : Hex("#EEEEEE");

        public static Color Severity(string s) => s switch
        {
            "critical" => Critical,
            "watch" => Watch,
            _ => Ok,
        };

        public static Color Tier(string tier) => tier switch
        {
            "severe" => Hex("#FF4D6D"),
            "high" => Hex("#FF8C42"),
            "elevated" => Hex("#FFC233"),
            _ => Mint,
        };

        public static Color Agent(string agent) => agent switch
        {
            "structural" => Hex("#FF7A59"),
            "operations" => Hex("#FF5DA2"),
            "market" => Hex("#2EC4B6"),
            "user" => Indigo,
            _ => NvidiaGreen,
        };

        public static string KindIcon(string kind) => kind switch
        {
            "roof" => "RF",
            "gutters" => "DR",
            "foundation" => "FD",
            "plumbing" => "PL",
            "sewer" => "SW",
            "electrical" => "EL",
            "hvac" => "HV",
            "water_heater" => "WH",
            "windows" => "WN",
            "attic_insulation" => "IN",
            "drywall" => "FN",
            "mold" => "IA",
            _ => "··",
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
            "mold" => "Mold / IAQ",
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
