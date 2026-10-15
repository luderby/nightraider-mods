using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Colours the hover tooltip's item name by rarity, so the name matches the cell
    /// background the player is hovering.
    ///
    /// How the tooltip works: every bag cell carries an EXMessageShow component
    /// (EXMessageShow.cs:86) whose OnPointerEnter starts a delay and then calls
    /// EXMessageControl.BeOpened(modelID, CurrentItemData) (EXMessageShow.cs:56). That method
    /// builds the panel: proTitle is the item name and proDes the stat block
    /// (EXMessageControl.cs:10-23, :216-219).
    ///
    /// BeOpened resets proTitle.color to white and then, for weapons that have a skin, sets
    /// it to the skin's colour instead (EXMessageControl.cs:265 and :322). A postfix runs
    /// after all of that, so it only applies the rarity colour when the game left the title
    /// at plain white -- otherwise the existing weapon-skin name colouring would be lost.
    /// </summary>
    internal static class TooltipRarity
    {
        private static FieldInfo _titleField;
        private static bool _reflectionFailed;
        private static bool _errorLogged;

        internal static void ResetReflectionCache()
        {
            _titleField = null;
            _reflectionFailed = false;
        }

        internal static void Colour(EXMessageControl control, int modelId)
        {
            if (control == null)
            {
                return;
            }
            if (RareShopPlugin.CfgRarityTooltip == null || !RareShopPlugin.CfgRarityTooltip.Value)
            {
                return;
            }
            if (ItemTiers.Count == 0 || _reflectionFailed)
            {
                return;
            }

            int tier = ItemTiers.TierOf(modelId);
            if (tier <= 0)
            {
                return;     // not in the table (system text, shop label, ...): leave as-is
            }

            if (_titleField == null)
            {
                _titleField = AccessTools.Field(typeof(EXMessageControl), "proTitle");
                if (_titleField == null)
                {
                    _reflectionFailed = true;
                    RareShopPlugin.Log.LogWarning(
                        "找不到 EXMessageControl.proTitle，悬停说明的稀有度颜色已停用。");
                    return;
                }
            }

            Text title = _titleField.GetValue(control) as Text;
            if (title == null)
            {
                return;
            }

            // The game colours the name itself when the weapon has a skin. Keep that.
            if (title.color != Color.white)
            {
                return;
            }

            title.color = RarityPalette.TextColor(tier);
        }

        internal static void LogError(Exception ex)
        {
            if (_errorLogged)
            {
                return;
            }
            _errorLogged = true;
            RareShopPlugin.Log.LogError("悬停说明稀有度颜色失败（只报一次）：" + ex);
        }
    }

    /// <summary>
    /// Postfix on EXMessageControl.BeOpened(int idd, CurrentItemData dt). The first parameter
    /// is the item's modelID, which is what the rarity table is keyed by.
    /// </summary>
    [HarmonyPatch(typeof(EXMessageControl), "BeOpened")]
    internal static class TooltipRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EXMessageControl __instance, int idd)
        {
            try
            {
                TooltipRarity.Colour(__instance, idd);
            }
            catch (Exception ex)
            {
                TooltipRarity.LogError(ex);
            }
        }
    }
}
