using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.RareTint
{
    /// <summary>
    /// 「物品稀有度底色」独立插件。
    ///
    /// 游戏本身没有稀有度字段，档位来自和「物品商店 / 刷新商店」共用的那张 CSV
    /// （BepInEx/config/NightRaider_item_tiers.csv）。本插件只做一件事：把这个档位画成
    /// 底色 / 名字颜色，让玩家在背包、仓库、箱子、商店等处一眼看出哪件是好东西。
    ///
    /// 它不添加任何窗口、不占用任何按键、不改动物品数值，可以单独安装，也可以和
    /// 「刷新商店」一起安装。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class RareTintPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nightraider.raretint";
        public const string PluginName = "Night Raider Item Rarity Tint";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigFile ConfigRef;

        internal static ConfigEntry<bool> CfgColorBagCells;
        internal static ConfigEntry<bool> CfgColorEquipment;
        internal static ConfigEntry<bool> CfgColorTooltip;
        internal static ConfigEntry<bool> CfgLockMarkText;
        internal static ConfigEntry<string> CfgLockMarkColor;
        internal static ConfigEntry<string> CfgBrokenColor;
        internal static ConfigEntry<string> CfgBrokenSlotColor;
        internal static ConfigEntry<bool> CfgHotbarStrip;
        internal static ConfigEntry<bool> CfgHudHotbarStrip;
        internal static ConfigEntry<bool> CfgReloadTableOnHotkey;
        internal static ConfigEntry<bool> CfgDebugLog;

        private Harmony _harmony;
        private bool _reloadedOnce;

        private void Awake()
        {
            Log = Logger;
            ConfigRef = Config;

            CfgColorBagCells = Config.Bind(
                "Tint", "ColorBagCells", true,
                "给游戏自带的格子按稀有度上底色。\n" +
                "覆盖：背包、A~L 仓库、箱子与尸体、商店/回收/黑市列表、交易窗口。\n" +
                "已锁定和已标记的物品也会显示稀有度底色（它们的状态改由悬停文字表示）。");

            CfgColorEquipment = Config.Bind(
                "Tint", "ColorEquipmentSlots", true,
                "给穿在身上的装备格子按稀有度上底色。\n" +
                "覆盖：背包界面里人物身上的 9 个格子（背包/头盔/衣服/护腿/护甲/靴子/主武器/副武器/近战）。\n" +
                "耐久归零的装备不上色，改为悬停显示【已损坏】。");

            CfgColorTooltip = Config.Bind(
                "Tint", "ColorTooltipName", true,
                "把鼠标悬停说明里的物品名也染成对应稀有度颜色。\n" +
                "带武器皮肤的物品会保留游戏自己的皮肤颜色，不会被覆盖。");

            CfgLockMarkText = Config.Bind(
                "Tint", "LockMarkInTooltip", true,
                "用悬停说明里的文字表示「已锁定 / 已标记」，而不是改格子底色。\n" +
                "游戏原本会把锁定的格子涂成大红、标记的涂成金色，这会盖住稀有度底色。\n" +
                "开启后：格子恢复稀有度底色，鼠标移上去在说明末尾显示【已锁定】/【已标记】；\n" +
                "两个状态同时存在时两个都显示。取消锁定/标记后文字自动消失。");

            CfgLockMarkColor = Config.Bind(
                "Tint", "LockMarkTextColor", "FF8A3D",
                "锁定/标记文字的颜色，十六进制 RRGGBB（可带 #）。\n" +
                "默认 FF8A3D（橙金），与游戏的锁定红、标记金都区分得开。");

            CfgBrokenColor = Config.Bind(
                "Tint", "BrokenTextColor", "FF5A5A",
                "【已损坏】文字的颜色，十六进制 RRGGBB（可带 #）。\n" +
                "默认 FF5A5A（警示红），和锁定/标记的橙金区分开。");

            CfgBrokenSlotColor = Config.Bind(
                "Tint", "BrokenSlotColor", "4A4A4A",
                "耐久归零的装备/快捷栏格子使用的颜色，十六进制 RRGGBB（可带 #）。\n" +
                "默认 4A4A4A（暗灰）。刻意选了不艳丽的颜色：\n" +
                "既和游戏原本的纯红区分开，也不会和任何一档稀有度撞色。\n" +
                "配合悬停说明里的【已损坏】，一眼就能看出哪件坏了。");

            CfgHotbarStrip = Config.Bind(
                "Tint", "HotbarRarityStrip", true,
                "给快捷栏格子加一条稀有度色条（贴在格子底边，不盖住物品图标）。\n" +
                "覆盖：HUD 上的快捷栏 6 格 + 背包界面里的快捷栏 6 格。\n" +
                "按 F1 隐藏快捷栏时色条会一起隐藏，重新显示时会自动恢复。\n" +
                "如果开这个之后出现卡顿，先把它关掉再反馈。");

            CfgHudHotbarStrip = Config.Bind(
                "Tint", "HudHotbarStrip", true,
                "只控制 HUD 上那条快捷栏的色条（背包界面里的那份由上面那项控制）。\n" +
                "留作排查用：万一只有 HUD 那条出问题，可以单独关掉它。");

            CfgReloadTableOnHotkey = Config.Bind(
                "Tint", "ReloadTableOnDemand", true,
                "按 F6 重新读取稀有度表（改完 CSV 不用重启游戏）。\n" +
                "本插件平时不需要按键，F6 只是为了改表后能立即看到效果。");

            CfgDebugLog = Config.Bind(
                "Tint", "DebugLog", false,
                "输出额外调试日志（稀有度表载入情况等）。");

            RarityPalette.Bind(this);
            ItemTiers.Load();

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(RareTintPlugin).Assembly);
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony 补丁失败: " + ex);
            }

            Logger.LogInfo(PluginName + " v" + PluginVersion + " 已加载。"
                + "背包格底色: " + (CfgColorBagCells.Value ? "开" : "关")
                + "；装备栏底色: " + (CfgColorEquipment.Value ? "开" : "关")
                + "；快捷栏色条: " + (CfgHotbarStrip.Value ? "开" : "关")
                + "(HUD:" + (CfgHudHotbarStrip.Value ? "开" : "关") + ")"
                + "；悬停名字颜色: " + (CfgColorTooltip.Value ? "开" : "关")
                + "；锁定/标记/损坏文字: " + (CfgLockMarkText.Value ? "开" : "关")
                + "；损坏格颜色: #" + BrokenSlotColourHex()
                + "；稀有度表: " + (ItemTiers.Count > 0
                    ? ItemTiers.Count + " 个物品 (" + ItemTiers.SourcePath + ")"
                    : "未载入 —— 底色不会生效"));
        }

        private void Update()
        {
            try
            {
                // No UI of its own: the only key exists so a freshly edited tier table can be
                // picked up without restarting the game.
                if (CfgReloadTableOnHotkey != null && CfgReloadTableOnHotkey.Value
                    && Input.GetKeyDown(KeyCode.F6))
                {
                    Reload();
                }

                // Read and apply cfg edits once, lazily, so the switches can be flipped
                // without restarting either.
                if (!_reloadedOnce && Time.unscaledTime > 5f)
                {
                    _reloadedOnce = true;
                    ReloadSettings();
                }

                // The weapon-modification panel is handled by Harmony postfixes on
                // RefitControl.RefreshWeaponAllData and OpenPartsChange (see RefitRarityPatch),
                // so nothing needs polling here.
            }
            catch (Exception ex)
            {
                Log.LogError("Update 异常: " + ex);
            }
        }

        private void ReloadSettings()
        {
            try
            {
                if (ConfigRef != null)
                {
                    ConfigRef.Reload();
                }
                RarityPalette.Refresh();
            }
            catch (Exception ex)
            {
                Log.LogWarning("重新读取配置失败，沿用当前值：" + ex.Message);
            }
        }

        /// <summary>Re-reads the config and the tier table, then reports what is in force.</summary>
        internal static void Reload()
        {
            try
            {
                if (ConfigRef != null)
                {
                    ConfigRef.Reload();
                }
                RarityPalette.Refresh();
                ItemTiers.Reload();
            }
            catch (Exception ex)
            {
                Log.LogWarning("重新读取失败：" + ex.Message);
            }

            Log.LogInfo("稀有度底色已刷新：格子=" + (IsBagTintOn() ? "开" : "关")
                + "，装备栏=" + (IsEquipmentTintOn() ? "开" : "关")
                + "，悬停名字=" + (IsTooltipTintOn() ? "开" : "关")
                + "，锁定/标记/损坏文字=" + (IsLockMarkTextOn() ? "开" : "关")
                + "，档位表=" + ItemTiers.Count + " 项"
                + "，格底色强度=" + RarityPalette.BagBrightness.ToString("0.00"));
        }

        internal static bool IsBagTintOn()
        {
            return CfgColorBagCells != null && CfgColorBagCells.Value;
        }

        internal static bool IsEquipmentTintOn()
        {
            return CfgColorEquipment != null && CfgColorEquipment.Value;
        }

        internal static bool IsHotbarStripOn()
        {
            return CfgHotbarStrip == null || CfgHotbarStrip.Value;
        }

        /// <summary>Separate switch for the HUD copy of the quick bar, for troubleshooting.</summary>
        internal static bool IsHudHotbarStripOn()
        {
            return IsHotbarStripOn() && (CfgHudHotbarStrip == null || CfgHudHotbarStrip.Value);
        }

        /// <summary>Dull colour used for a worn-out item's slot and hotbar strip.</summary>
        internal static Color BrokenSlotColor()
        {
            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + ColourHex(CfgBrokenSlotColor, "4A4A4A"), out c))
            {
                c = new Color(0.290f, 0.290f, 0.290f, 1f);     // 4A4A4A
            }
            return c;
        }

        internal static bool IsTooltipTintOn()
        {
            return CfgColorTooltip != null && CfgColorTooltip.Value;
        }

        internal static bool IsLockMarkTextOn()
        {
            return CfgLockMarkText == null || CfgLockMarkText.Value;
        }

        /// <summary>RRGGBB (no '#') used for the 【已锁定】/【已标记】 labels.</summary>
        internal static string LockMarkColourHex()
        {
            return ColourHex(CfgLockMarkColor, "FF8A3D");
        }

        /// <summary>RRGGBB (no '#') used for the 【已损坏】 label.</summary>
        internal static string BrokenColourHex()
        {
            return ColourHex(CfgBrokenColor, "FF5A5A");
        }

        /// <summary>RRGGBB (no '#') used for a worn-out item's slot and quick-bar strip.</summary>
        internal static string BrokenSlotColourHex()
        {
            return ColourHex(CfgBrokenSlotColor, "4A4A4A");
        }

        private static string ColourHex(ConfigEntry<string> entry, string fallback)
        {
            string hex = entry != null ? entry.Value : null;
            if (string.IsNullOrEmpty(hex))
            {
                return fallback;
            }
            hex = hex.Trim();
            if (hex.Length > 0 && hex[0] == '#')
            {
                hex = hex.Substring(1);
            }
            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + hex, out c))
            {
                return fallback;
            }
            return hex;
        }
    }
}
