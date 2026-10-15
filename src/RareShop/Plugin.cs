using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.RareShop
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class RareShopPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nightraider.rareshopplugin";
        public const string PluginName = "Night Raider Rare Shop";
        public const string PluginVersion = "1.3.5";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyCode> CfgToggleKey;
        internal static ConfigEntry<float> CfgDefaultMultiplier;
        internal static ConfigEntry<KeyCode> CfgScreenshotKey;
        internal static ConfigEntry<bool> CfgSuppressGameInput;
        internal static ConfigEntry<bool> CfgDebugLog;

        internal static ConfigEntry<bool> CfgRarityInShop;
        internal static ConfigEntry<bool> CfgRarityInBag;
        internal static ConfigEntry<bool> CfgRarityTooltip;
        internal static ConfigEntry<bool> CfgReloadTierTableOnOpen;

        /// <summary>
        /// The plugin's ConfigFile. BaseUnityPlugin.Config is an instance member, but the
        /// hotkey path (TogglePanel) is static and needs the file to reload settings from
        /// disk. Captured in Awake.
        /// </summary>
        internal static ConfigFile ConfigRef;

        private static ShopPanel _panel;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            ConfigRef = Config;

            CfgToggleKey = Config.Bind(
                "General", "ToggleKey", KeyCode.F8,
                "打开/关闭稀有物品商店的按键。游戏默认键位里 F8 未被占用。");

            CfgScreenshotKey = Config.Bind(
                "General", "ScreenshotKey", KeyCode.F9,
                "按此键把当前画面截图保存到 BepInEx/Screenshots，用于反馈界面问题时附上截图。");

            CfgSuppressGameInput = Config.Bind(
                "General", "SuppressGameInputWhileOpen", true,
                "商店打开时屏蔽游戏自身的键盘/鼠标输入，避免在倍率输入框里打字时角色乱动。");

            CfgDefaultMultiplier = Config.Bind(
                "Shop", "DefaultPriceMultiplier", 1.0f,
                "打开商店时的默认价格倍率。最终价格 = 物品售价 × 倍率。");

            CfgDebugLog = Config.Bind(
                "General", "DebugLog", false,
                "输出额外调试日志（商品分类统计等）。");

            // --- rarity colouring -------------------------------------------------
            // The game has no rarity field, so the tiers come from an editable CSV
            // (BepInEx/config/NightRaider_item_tiers.csv). These switches decide where
            // that tier is painted.
            CfgRarityInShop = Config.Bind(
                "Rarity", "ColorShopRows", true,
                "按稀有度给商店列表行上底色。");

            CfgRarityInBag = Config.Bind(
                "Rarity", "ColorBagCells", true,
                "按稀有度给游戏自带格子（背包、A~L 仓库、箱子/尸体、商店与回收列表、交易窗口）上底色。\n" +
                "已锁定（红）和已标记（金）的格子会保留游戏原色，不覆盖。");

            CfgRarityTooltip = Config.Bind(
                "Rarity", "ColorTooltipName", true,
                "把鼠标悬停说明里的物品名也染成对应稀有度颜色。\n" +
                "带武器皮肤的物品会保留游戏自己的皮肤颜色，不会被覆盖。");

            CfgReloadTierTableOnOpen = Config.Bind(
                "Rarity", "ReloadTableOnOpen", true,
                "每次打开商店时重新读取稀有度表，这样改完 CSV 不用重启游戏。");

            RarityPalette.Bind(this);
            ItemTiers.Load();

            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(RareShopPlugin).Assembly);
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony 补丁失败: " + ex);
            }

            Logger.LogInfo(PluginName + " v" + PluginVersion + " 已加载。开关按键: " + CfgToggleKey.Value
                + "；稀有度表: " + (ItemTiers.Count > 0
                    ? ItemTiers.Count + " 个物品 (" + ItemTiers.SourcePath + ")"
                    : "未载入，稀有度底色不生效"));
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(CfgToggleKey.Value))
                {
                    TogglePanel();
                }

                if (CfgScreenshotKey.Value != KeyCode.None && Input.GetKeyDown(CfgScreenshotKey.Value))
                {
                    CaptureScreenshot();
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Update 异常: " + ex);
            }
        }

        /// <summary>Opens/closes the shop. This is what the hotkey invokes.</summary>
        internal static void TogglePanel()
        {
            // BepInEx reads the .cfg file once, at startup: ConfigEntry.Value returns the
            // in-memory value, so a setting edited in the .cfg while the game is running
            // would never take effect. Reload from disk here instead, so the palette (and
            // every other setting) can be tuned live: edit the cfg, press F8, done.
            try
            {
                if (ConfigRef != null)
                {
                    ConfigRef.Reload();
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("重新读取配置文件失败，沿用当前值：" + ex.Message);
            }

            // Re-read the tier table too, so the CSV can also be edited while running.
            try
            {
                RarityPalette.Refresh();
                if (CfgReloadTierTableOnOpen != null && CfgReloadTierTableOnOpen.Value)
                {
                    ItemTiers.Reload();
                }

                // One line per open that states the values actually in force. Without this
                // there is no way to tell "my edit was ignored" from "my edit applied but
                // I cannot see the difference".
                Log.LogInfo("稀有度配色：商店行强度=" + RarityPalette.ShopBrightness.ToString("0.00")
                    + "，背包格强度=" + RarityPalette.BagBrightness.ToString("0.00")
                    + "，商店行=" + (CfgRarityInShop != null && CfgRarityInShop.Value ? "开" : "关")
                    + "，背包格=" + (CfgRarityInBag != null && CfgRarityInBag.Value ? "开" : "关")
                    + "，档位表=" + ItemTiers.Count + " 项");
            }
            catch (Exception ex)
            {
                Log.LogWarning("刷新稀有度配置失败，沿用现有数据：" + ex.Message);
            }

            if (_panel == null)
            {
                _panel = ShopPanel.Create();
            }

            if (!GameRefs.IsSaveLoaded())
            {
                Log.LogWarning("尚未载入存档，商店暂时没有可购买的商品。");
            }

            _panel.Toggle();
        }

        internal static bool IsShopOpen()
        {
            return _panel != null && _panel.IsOpen;
        }

        /// <summary>
        /// Saves the current frame next to the BepInEx folder so interface problems
        /// can be reported with a screenshot.
        /// </summary>
        internal static void CaptureScreenshot()
        {
            try
            {
                string dir = System.IO.Path.Combine(Paths.BepInExRootPath, "Screenshots");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                Texture2D tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
                System.IO.File.WriteAllBytes(file, png);
                Log.LogInfo("截图已保存: " + file);
            }
            catch (Exception ex)
            {
                Log.LogError("截图失败: " + ex);
            }
        }
    }

    /// <summary>
    /// Makes the game treat the shop as an open window so that gameplay code which
    /// checks for open windows behaves.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "CheckWindowOpen")]
    internal static class GameManagerCheckWindowOpenPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (RareShopPlugin.IsShopOpen())
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// While the shop is on screen the game must not also react to the keyboard,
    /// otherwise typing a multiplier would move the character or fire the weapon.
    /// The game polls Input.GetKeyDown/GetKey/GetAxis directly and never consults
    /// EventSystem.currentSelectedGameObject, so the input is filtered at the source.
    /// The mod's own code is unaffected because it reads Input directly too.
    /// </summary>
    internal static class InputSuppression
    {
        private static bool Silent()
        {
            return RareShopPlugin.CfgSuppressGameInput != null
                && RareShopPlugin.CfgSuppressGameInput.Value
                && RareShopPlugin.IsShopOpen();
        }

        [HarmonyPatch(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) })]
        [HarmonyPrefix]
        private static bool GetKeyDownKeyCode(ref bool __result)
        {
            if (Silent())
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
            if (Silent())
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
            if (Silent())
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
            if (Silent())
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
            if (Silent())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetButtonDown")]
        [HarmonyPrefix]
        private static bool GetButtonDown(ref bool __result)
        {
            if (Silent())
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetAxis")]
        [HarmonyPrefix]
        private static bool GetAxis(ref float __result)
        {
            if (Silent())
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetAxisRaw")]
        [HarmonyPrefix]
        private static bool GetAxisRaw(ref float __result)
        {
            if (Silent())
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetMouseButton")]
        [HarmonyPrefix]
        private static bool GetMouseButton(ref bool __result)
        {
            if (Silent())
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
            if (Silent())
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
            if (Silent())
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
