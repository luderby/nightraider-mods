using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Repaints the game's own bag cells by item rarity.
    ///
    /// MyBagControl.RefreshTheGrid (MyBagControl.cs:8405) already colours every cell, but
    /// the colours encode interaction state and broad category, not rarity:
    ///
    ///   white        plain
    ///   grey 0.5     drag origin
    ///   dark 0.2     ...
    ///   dark red     invalid drop target
    ///   gold         item is in CurrentDataHub.markList   (marked)
    ///   blue         modelID 3000-3999                    (consumables)
    ///   purple       modelID 2000-2999                    (weapon parts)
    ///   red          item is in CurrentDataHub.lockList   (locked)
    ///
    /// A Harmony postfix runs after all of that and overrides only the cells where rarity is
    /// strictly more useful:
    ///
    ///   * a drag in progress is left completely alone, so placement highlighting still works;
    ///   * locked (red) and marked (gold) cells keep the game's colour, because those are
    ///     player-applied states and losing them would be a regression, not a feature;
    ///   * items the tier table does not know keep the game's colour.
    ///
    /// Everything else -- including the category blue/purple -- becomes the rarity colour.
    /// </summary>
    internal static class BagRarity
    {
        private static FieldInfo _myBagField;
        private static FieldInfo _targetBagField;
        private static bool _reflectionFailed;
        private static bool _paintErrorLogged;

        internal static void ResetReflectionCache()
        {
            _myBagField = null;
            _targetBagField = null;
            _reflectionFailed = false;
        }

        /// <summary>
        /// Shared guard + reflection setup. Returns the game object to paint, or null when
        /// painting should be skipped entirely.
        /// </summary>
        private static GameObject Prepare(MyBagControl bag, CurrentDataHub cur, bool wantTarget)
        {
            if (bag == null || cur == null)
            {
                return null;
            }
            if (RareShopPlugin.CfgRarityInBag == null || !RareShopPlugin.CfgRarityInBag.Value)
            {
                return null;
            }
            if (ItemTiers.Count == 0)
            {
                return null;
            }
            // Never fight the drag / drop-target highlighting.
            if (bag.isTouch)
            {
                return null;
            }
            if (_reflectionFailed)
            {
                return null;
            }

            if (_myBagField == null)
            {
                _myBagField = AccessTools.Field(typeof(MyBagControl), "myBag_GOBJ");
                _targetBagField = AccessTools.Field(typeof(MyBagControl), "targetBag_GOBJ");
                if (_myBagField == null || _targetBagField == null)
                {
                    _reflectionFailed = true;
                    RareShopPlugin.Log.LogWarning(
                        "找不到 MyBagControl.myBag_GOBJ / targetBag_GOBJ，背包稀有度底色已停用。");
                    return null;
                }
            }

            return (wantTarget ? _targetBagField : _myBagField).GetValue(bag) as GameObject;
        }

        /// <summary>
        /// Paints the player's own bag. Hooked to MyBagControl.RefreshTheGrid.
        /// </summary>
        internal static void PaintMyBag(MyBagControl bag)
        {
            CurrentDataHub cur = GameRefs.Current;
            GameObject host = Prepare(bag, cur, false);
            if (host == null)
            {
                return;
            }
            PaintCells(host, cur.myBag, cur);
        }

        /// <summary>
        /// Paints the "target" grid. That single grid is reused by the game for every
        /// container-like window: loot containers and corpses, the A-L depot (仓库), the
        /// shop / recycle / black-market lists and the trade window. They all go through
        /// CurrentDataHub.targetBag and MyBagControl.RefreshTheTargetGrid.
        ///
        /// This needs its own hook: opening the depot runs SwitchDepot -> ChangeTargetBag ->
        /// RefreshTheTargetGrid, which never calls RefreshTheGrid, so a hook on RefreshTheGrid
        /// alone left the depot and every other target window unpainted.
        /// </summary>
        internal static void PaintTargetBag(MyBagControl bag)
        {
            CurrentDataHub cur = GameRefs.Current;
            GameObject host = Prepare(bag, cur, true);
            if (host == null)
            {
                return;
            }
            PaintCells(host, cur.targetBag, cur);
        }

        private static void PaintCells(GameObject host, Dictionary<int, CurrentItemData> items, CurrentDataHub cur)
        {
            if (host == null || items == null)
            {
                return;
            }

            Transform tr = host.transform;
            int cells = tr.childCount;

            for (int i = 0; i < cells; i++)
            {
                CurrentItemData cell;
                if (!items.TryGetValue(i, out cell) || cell == null)
                {
                    continue;
                }

                int modelId = cell.modelID;

                // Grid occupancy, as the game models it:
                //   modelID == 0  empty cell            (DataHub.nullItemData, GetNull() sets 0)
                //   modelID == 1  cell covered by a multi-cell item
                //   modelID >  1  the item itself, sitting on its origin cell
                // AutoOccupyBagGrid (CurrentDataHub.cs:5659) writes the ORIGIN CELL INDEX into
                // the placeholder's id, so the item behind a covered cell can be resolved from
                // there. Without this, only the single origin cell got the tier colour and a
                // 2x3 item looked like a 1x1 item with stray grey around it.
                if (modelId == 1)
                {
                    if (cell.id < 0 || cell.id == i)
                    {
                        continue;   // padding outside the bag grid, or a self-reference
                    }
                    CurrentItemData origin;
                    if (!items.TryGetValue(cell.id, out origin) || origin == null)
                    {
                        continue;
                    }
                    modelId = origin.modelID;
                }
                if (modelId <= 1)
                {
                    continue;       // empty cell
                }

                if (cur.lockList != null && cur.lockList.Contains(modelId))
                {
                    continue;   // the game paints these red; keep it
                }
                if (cur.markList != null && cur.markList.Contains(modelId))
                {
                    continue;   // the game paints these gold; keep it
                }

                int tier = ItemTiers.TierOf(modelId);
                if (tier <= 0)
                {
                    continue;   // unknown item; keep the game's colour
                }

                Transform child = tr.GetChild(i);
                if (child == null)
                {
                    continue;
                }
                Image img = child.GetComponent<Image>();
                if (img == null)
                {
                    continue;
                }

                Color want = RarityPalette.BagCellColor(tier);
                if (img.color != want)
                {
                    img.color = want;
                }
            }
        }

        internal static void LogPaintError(Exception ex)
        {
            if (_paintErrorLogged)
            {
                return;
            }
            _paintErrorLogged = true;
            RareShopPlugin.Log.LogError("背包稀有度底色失败（只报一次）：" + ex);
        }
    }

    /// <summary>Paints the player's own bag grid.</summary>
    [HarmonyPatch(typeof(MyBagControl), "RefreshTheGrid")]
    internal static class MyBagRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyBagControl __instance)
        {
            try
            {
                BagRarity.PaintMyBag(__instance);
            }
            catch (Exception ex)
            {
                BagRarity.LogPaintError(ex);
            }
        }
    }

    /// <summary>
    /// Paints the target grid, which the game reuses for the A-L depot (仓库), loot
    /// containers and corpses, the shop / recycle / black-market lists and the trade window.
    /// Without this hook the depot and every other target window stayed uncoloured, because
    /// SwitchDepot goes straight to RefreshTheTargetGrid and never touches RefreshTheGrid.
    /// </summary>
    [HarmonyPatch(typeof(MyBagControl), "RefreshTheTargetGrid")]
    internal static class TargetGridRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyBagControl __instance)
        {
            try
            {
                BagRarity.PaintTargetBag(__instance);
            }
            catch (Exception ex)
            {
                BagRarity.LogPaintError(ex);
            }
        }
    }
}
