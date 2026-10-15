using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace NightRaider.RareTint
{
    /// <summary>
    /// Tier -> display name and background colour.
    ///
    /// Everything here is bound to BepInEx config so the palette can be retuned without
    /// rebuilding the plugin. Tier numbers themselves come from <see cref="ItemTiers"/>.
    ///
    /// Values are looked up by (section, key) every time they are read rather than cached in
    /// a ConfigEntry field. BepInEx reads the .cfg file only at startup, and the plugin calls
    /// ConfigFile.Reload() when the shop hotkey is pressed so edits apply live; whether
    /// Reload() updates the existing entries in place or replaces them is an internal detail,
    /// so a fresh lookup is the only thing that is correct either way.
    /// </summary>
    internal static class RarityPalette
    {
        internal const int MinTier = 1;
        internal const int MaxTier = 6;

        internal const string Section = "Tint";

        private static readonly string[] DefaultNames = { "", "普通", "优良", "稀有", "史诗", "传说", "绝世" };
        private static readonly string[] DefaultColors = { "", "C9C9C9", "5FBF5F", "4D8CE6", "A85CD9", "F2A33C", "FF3B30" };

        private static readonly Color[] Colors = new Color[MaxTier + 1];
        private static readonly string[] Names = new string[MaxTier + 1];

        private static ConfigFile _file;

        internal static string NameKey(int tier) { return "Tier" + tier + "Name"; }
        internal static string ColorKey(int tier) { return "Tier" + tier + "Color"; }

        internal static void Bind(BaseUnityPlugin plugin)
        {
            _file = plugin.Config;

            for (int t = MinTier; t <= MaxTier; t++)
            {
                int tier = t;
                plugin.Config.Bind(
                    Section, NameKey(tier), DefaultNames[tier],
                    "第 " + tier + " 档稀有度的显示名称。");

                plugin.Config.Bind(
                    Section, ColorKey(tier), DefaultColors[tier],
                    "第 " + tier + " 档稀有度的底色，十六进制 RRGGBB（可带 #）。\n" +
                    "默认：灰=普通 绿=优良 蓝=稀有 紫=史诗 橙=传说。");
            }

            plugin.Config.Bind(
                Section, "BagCellBrightness", 0.85f,
                new ConfigDescription(
                    "格子稀有度底色的强度。0 = 不着色（保持游戏原样），1 = 全亮。\n" +
                    "格子会被物品图标盖住，所以默认比较亮。\n" +
                    "改完这个值后重新打开一次背包（或按 F6）即可生效。",
                    new AcceptableValueRange<float>(0f, 1f)));

            Refresh();
        }

        /// <summary>
        /// Reads one setting fresh from the config file instead of a cached ConfigEntry, so
        /// the value is correct both before and after ConfigFile.Reload().
        /// </summary>
        private static T Lookup<T>(string key, T fallback)
        {
            if (_file == null)
            {
                return fallback;
            }
            try
            {
                ConfigEntry<T> e;
                if (_file.TryGetEntry(Section, key, out e) && e != null)
                {
                    return e.Value;
                }
            }
            catch
            {
                // fall through to the default
            }
            return fallback;
        }

        /// <summary>Re-reads the palette from the current config values.</summary>
        internal static void Refresh()
        {
            for (int t = MinTier; t <= MaxTier; t++)
            {
                string hex = Lookup(ColorKey(t), DefaultColors[t]);
                if (string.IsNullOrEmpty(hex))
                {
                    hex = DefaultColors[t];
                }
                hex = hex.Trim();
                if (hex.Length > 0 && hex[0] != '#')
                {
                    hex = "#" + hex;
                }

                Color c;
                if (!ColorUtility.TryParseHtmlString(hex, out c))
                {
                    ColorUtility.TryParseHtmlString("#" + DefaultColors[t], out c);
                }
                c.a = 1f;
                Colors[t] = c;

                string n = Lookup(NameKey(t), DefaultNames[t]);
                Names[t] = string.IsNullOrEmpty(n) ? DefaultNames[t] : n;
            }
        }

        private static int Clamp(int tier)
        {
            if (tier < MinTier)
            {
                return MinTier;
            }
            if (tier > MaxTier)
            {
                return MaxTier;
            }
            return tier;
        }

        /// <summary>The tier colour at full brightness.</summary>
        internal static Color RawColor(int tier)
        {
            return Colors[Clamp(tier)];
        }

        internal static string NameOf(int tier)
        {
            return Names[Clamp(tier)];
        }

        /// <summary>Tier colour as an RRGGBB hex string, for rich-text labels.</summary>
        internal static string HexOf(int tier)
        {
            return ColorUtility.ToHtmlStringRGB(RawColor(tier));
        }

        /// <summary>Configured brightness for shop rows (0..1).</summary>
        internal static float ShopBrightness
        {
            get { return Lookup("ShopRowBrightness", 0.30f); }
        }

        /// <summary>Configured brightness for bag cells (0..1).</summary>
        internal static float BagBrightness
        {
            get { return Lookup("BagCellBrightness", 0.85f); }
        }

        /// <summary>Background colour for a shop list row, scaled by the configured brightness.</summary>
        internal static Color ShopRowColor(int tier)
        {
            float b = ShopBrightness;
            Color c = RawColor(tier);
            return new Color(c.r * b, c.g * b, c.b * b, 1f);
        }

        /// <summary>Background colour for a bag cell, scaled by the configured brightness.</summary>
        internal static Color BagCellColor(int tier)
        {
            float b = BagBrightness;
            Color c = RawColor(tier);
            return new Color(c.r * b, c.g * b, c.b * b, 1f);
        }

        /// <summary>
        /// Colour for TEXT, at full brightness. Text must not be dimmed the way a background
        /// is: a row background at 0.55 brightness still reads fine, but text at 0.55 on the
        /// dark panel is muddy.
        /// </summary>
        internal static Color TextColor(int tier)
        {
            return RawColor(tier);
        }
    }
}
