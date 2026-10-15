using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.RareRefresh
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class RareRefreshPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nightraider.rarerefresh";
        public const string PluginName = "Night Raider Refresh Shop";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigFile ConfigRef;

        internal static ConfigEntry<KeyCode> CfgToggleKey;
        internal static ConfigEntry<bool> CfgSuppressGameInput;
        internal static ConfigEntry<int> CfgRefreshCost;
        internal static ConfigEntry<bool> CfgDebugLog;

        internal static ConfigEntry<float> CfgWeight2;
        internal static ConfigEntry<float> CfgWeight3;
        internal static ConfigEntry<float> CfgWeight4;
        internal static ConfigEntry<float> CfgWeight6;
        internal static ConfigEntry<float> CfgWeight5;

        internal static ConfigEntry<float> CfgBandTier2;
        internal static ConfigEntry<float> CfgBandTier3;
        internal static ConfigEntry<float> CfgBandTier4;
        internal static ConfigEntry<float> CfgBandTier6;
        internal static ConfigEntry<float> CfgBandTier5;

        private static RareRefreshPanel _panel;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            ConfigRef = Config;

            CfgToggleKey = Config.Bind(
                "General", "ToggleKey", KeyCode.F7,
                "打开/关闭「刷新商店」的按键。\n" +
                "游戏默认键位里 F7 未被占用（F10 的说明请以游戏内为准）。");

            CfgSuppressGameInput = Config.Bind(
                "General", "SuppressGameInputWhileOpen", true,
                "商店打开时屏蔽游戏自身的鼠标/键盘输入，避免点击商店按钮时角色同时开枪或移动。");

            CfgDebugLog = Config.Bind(
                "General", "DebugLog", false,
                "输出额外调试日志（每个类别的可选物品数量）。");

            CfgRefreshCost = Config.Bind(
                "Shop", "PageRefreshCostGP", 50000,
                "刷新（把 10 个窗口一起重掷）要花的 GP。\n" +
                "刷新会重新随机全部 10 个窗口的商品和价格，已售罄的窗口也会重新上货。");

            // --- rarity weights ----------------------------------------------------
            // The game has no rarity field; tiers come from the same editable CSV the
            // item-shop mod uses (see ItemTiers).
            //
            // The tier is drawn FIRST using these weights and the item is then picked from
            // that tier, so these percentages are what the player actually sees. That also
            // means no per-category tier floor is needed: the weights alone decide the mix,
            // and a tier a category has nothing in is simply re-drawn.
            //
            // Tier 1 is pinned to 0: an "ordinary" item has no business in a rare-goods shop.
            // Tier 6 (绝世) is the top tier the boss defined; its default weight of 1 keeps it
            // genuinely rare, and the other weights are scaled slightly so all six still sum
            // to 100 (39.6 / 29.7 / 19.8 / 9.9 / 1.0).
            CfgWeight6 = Config.Bind(
                "RarityWeights", "Tier6Weight", 1f,
                "绝世（红）出现概率权重。默认 1 => 1%。\n" +
                "这是最高档：单格价值极高、或来源极其特殊的物品。\n" +
                "每张卡片各抽一次，所以 10 张卡片一页里至少出现 1 件红色的概率约为 9.6%。");

            CfgWeight5 = Config.Bind(
                "RarityWeights", "Tier5Weight", 9.9f,
                "传说（橙）出现概率权重。默认 9.9 => 9.9%。");

            CfgWeight4 = Config.Bind(
                "RarityWeights", "Tier4Weight", 19.8f,
                "史诗（紫）出现概率权重。默认 19.8 => 19.8%。");

            CfgWeight3 = Config.Bind(
                "RarityWeights", "Tier3Weight", 29.7f,
                "稀有（蓝）出现概率权重。默认 29.7 => 29.7%。");

            CfgWeight2 = Config.Bind(
                "RarityWeights", "Tier2Weight", 39.6f,
                "优良（绿）出现概率权重。默认 39.6 => 39.6%。\n" +
                "五个权重是相对值，不必凑够 100，程序会自动归一化。\n" +
                "第 1 档（普通/灰）固定不出现。");

            // --- price bands -------------------------------------------------------
            // Each rarity has its own 10x-wide multiplier band applied to the item's sell
            // price. The lower 3x of each band is the 优惠 window.
            CfgBandTier6 = Config.Bind(
                "PriceBands", "Tier6BandMin", 35f,
                "绝世（红）价格倍率下限。默认 35 => 区间 35~45 倍，其中 35~38 为「优惠」。");

            CfgBandTier5 = Config.Bind(
                "PriceBands", "Tier5BandMin", 30f,
                "传说（橙）价格倍率下限。默认 30 => 区间 30~40 倍，其中 30~33 为「优惠」。");

            CfgBandTier4 = Config.Bind(
                "PriceBands", "Tier4BandMin", 25f,
                "史诗（紫）价格倍率下限。默认 25 => 区间 25~35 倍，其中 25~28 为「优惠」。");

            CfgBandTier3 = Config.Bind(
                "PriceBands", "Tier3BandMin", 20f,
                "稀有（蓝）价格倍率下限。默认 20 => 区间 20~30 倍，其中 20~23 为「优惠」。");

            CfgBandTier2 = Config.Bind(
                "PriceBands", "Tier2BandMin", 15f,
                "优良（绿）价格倍率下限。默认 15 => 区间 15~25 倍，其中 15~18 为「优惠」。\n" +
                "每个档位的区间宽度固定 10 倍，下限往下 3 倍算作「优惠」区间。");

            RarityPalette.Bind(this);
            ItemTiers.Load();

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(RareRefreshPlugin).Assembly);
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony 补丁失败: " + ex);
            }

            Logger.LogInfo(PluginName + " v" + PluginVersion + " 已加载。开关按键: " + CfgToggleKey.Value
                + "；刷新费用: " + CfgRefreshCost.Value + " GP；稀有度权重(绿/蓝/紫/橙): "
                + TierWeight(2) + "/" + TierWeight(3) + "/" + TierWeight(4) + "/" + TierWeight(5)
                + "；稀有度表: "
                + (ItemTiers.Count > 0
                    ? ItemTiers.Count + " 个物品 (" + ItemTiers.SourcePath + ")"
                    : "未载入"));
        }

        private void Update()
        {
            try
            {
                if (CfgToggleKey != null && Input.GetKeyDown(CfgToggleKey.Value))
                {
                    TogglePanel();
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Update 异常: " + ex);
            }
        }

        internal static void TogglePanel()
        {
            // BepInEx reads the .cfg once at startup, so pull it back from disk here:
            // edit the cfg, press the hotkey, done — no restart.
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
                Log.LogWarning("重新读取配置失败，沿用当前值：" + ex.Message);
            }

            if (_panel == null)
            {
                _panel = RareRefreshPanel.Create();
            }

            if (!GameRefs.IsSaveLoaded())
            {
                Log.LogWarning("尚未载入存档，商店暂时无法购买。");
            }

            _panel.Toggle();
        }

        internal static bool IsPanelOpen()
        {
            return _panel != null && _panel.IsOpen;
        }

        /// <summary>True while the game's own input must be silenced (our UI is up).</summary>
        internal static bool ShouldBlockGameInput()
        {
            return CfgSuppressGameInput != null
                && CfgSuppressGameInput.Value
                && IsPanelOpen();
        }

        internal static int RefreshCost
        {
            get { return CfgRefreshCost != null ? Mathf.Max(0, CfgRefreshCost.Value) : 50000; }
        }

        /// <summary>
        /// Relative draw weight of one rarity tier. Tier 1 is pinned to zero so an "ordinary"
        /// item can never appear in a rare-goods shop; the remaining tiers default to the
        /// requested 绿40 / 蓝30 / 紫20 / 橙10.
        /// </summary>
        internal static float TierWeight(int tier)
        {
            switch (tier)
            {
                case 2: return Mathf.Max(0f, CfgWeight2 != null ? CfgWeight2.Value : 39.6f);
                case 3: return Mathf.Max(0f, CfgWeight3 != null ? CfgWeight3.Value : 29.7f);
                case 4: return Mathf.Max(0f, CfgWeight4 != null ? CfgWeight4.Value : 19.8f);
                case 5: return Mathf.Max(0f, CfgWeight5 != null ? CfgWeight5.Value : 9.9f);
                case 6: return Mathf.Max(0f, CfgWeight6 != null ? CfgWeight6.Value : 1f);
                default: return 0f;
            }
        }

        /// <summary>
        /// Lower bound of a rarity's multiplier band. Green 15, blue 20, purple 25, orange 30,
        /// 绝世 35: each band is 10x wide and its lower 3x is the discount (优惠) window.
        /// </summary>
        internal static float BandMin(int tier)
        {
            switch (tier)
            {
                case 2: return Mathf.Max(1f, CfgBandTier2 != null ? CfgBandTier2.Value : 15f);
                case 3: return Mathf.Max(1f, CfgBandTier3 != null ? CfgBandTier3.Value : 20f);
                case 4: return Mathf.Max(1f, CfgBandTier4 != null ? CfgBandTier4.Value : 25f);
                case 5: return Mathf.Max(1f, CfgBandTier5 != null ? CfgBandTier5.Value : 30f);
                case 6: return Mathf.Max(1f, CfgBandTier6 != null ? CfgBandTier6.Value : 35f);
                default: return 1f;
            }
        }

        internal static int TierFloor(ShopKind kind)
        {
            // No per-category floor any more: the rarity weights decide the mix. Kept as a
            // single place to reintroduce a floor later if it is ever wanted again.
            return 0;
        }
    }

    /// <summary>
    /// Makes the game treat the panel as an open window, the same way the item-shop mod
    /// does, so gameplay code that checks for open windows behaves.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "CheckWindowOpen")]
    internal static class GameManagerCheckWindowOpenPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (RareRefreshPlugin.IsPanelOpen())
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// While the panel is on screen the game must not also react to the mouse/keyboard,
    /// otherwise clicking [购买] would fire the weapon and clicking [刷新] would make the
    /// character move. The game polls Input directly and never consults
    /// EventSystem.currentSelectedGameObject, so the input is filtered at the source.
    ///
    /// The panel itself is unaffected: it reads Input.mousePosition (never blocked) and is
    /// driven entirely by mouse buttons, which it reads through its own components rather
    /// than through a polled key. Only the game's polling is silenced.
    /// </summary>
    internal static class InputSuppression
    {
        [HarmonyPatch(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) })]
        [HarmonyPrefix]
        private static bool GetKeyDownKeyCode(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetKeyDown", new Type[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool GetKeyDownString(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetKey", new Type[] { typeof(KeyCode) })]
        [HarmonyPrefix]
        private static bool GetKeyKeyCode(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetKey", new Type[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool GetKeyString(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetKeyUp", new Type[] { typeof(KeyCode) })]
        [HarmonyPrefix]
        private static bool GetKeyUpKeyCode(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetMouseButton")]
        [HarmonyPrefix]
        private static bool GetMouseButton(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetMouseButtonDown")]
        [HarmonyPrefix]
        private static bool GetMouseButtonDown(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetMouseButtonUp")]
        [HarmonyPrefix]
        private static bool GetMouseButtonUp(ref bool __result)
        {
            if (RareRefreshPlugin.ShouldBlockGameInput())
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
