using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareTint
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
    ///   * cells that actually hold an item take its rarity colour, including while that item
    ///     is being dragged, so the colour no longer disappears mid-drag;
    ///   * the drop-validity colours are still respected: empty cells (the drop preview) and
    ///     invalid drop targets carry no resolvable item, so they are never touched;
    ///   * locked (red) and marked (gold) cells take the rarity colour too — their state is
    ///     reported as 【已锁定】/【已标记】 in the hover tooltip instead;
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
            if (!RareTintPlugin.IsBagTintOn())
            {
                return null;
            }
            if (ItemTiers.Count == 0)
            {
                return null;
            }
            // Dragging used to be skipped entirely ("never fight the drag highlighting"), but
            // the game re-paints its own lock/mark colours during a drag (MyBagControl.Refresh-
            // TheGrid case 7 = locked red, case 4 = marked gold, using the dragged item's real
            // modelID), so the rarity colour vanished from the cell under the cursor until the
            // item was dropped. The paint loop below only touches cells that hold a resolvable
            // item with a known tier, which leaves every drop-validity colour alone:
            //   modelID 0  (empty cells, i.e. the drop preview)  -> skipped
            //   modelID -1 (invalid drop target)                 -> skipped
            //   modelID 1  (drag origin / covered cell)          -> resolved to the real item
            // So the drag preview keeps the game's white / grey / dark-red signalling while the
            // cell carrying the item shows its rarity.
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
                    RareTintPlugin.Log.LogWarning(
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
                try
                {
                    PaintOneCell(tr, items, i);
                }
                catch (Exception ex)
                {
                    LogCellError(i, ex);
                }
            }
        }

        /// <summary>Paints a single bag cell, or leaves it alone when it holds nothing usable.</summary>
        private static void PaintOneCell(Transform tr, Dictionary<int, CurrentItemData> items, int i)
        {
            {
                CurrentItemData cell;
                if (!items.TryGetValue(i, out cell) || cell == null)
                {
                    return;
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
                        return;   // padding outside the bag grid, or a self-reference
                    }
                    CurrentItemData origin;
                    if (!items.TryGetValue(cell.id, out origin) || origin == null)
                    {
                        return;
                    }
                    modelId = origin.modelID;
                }
                if (modelId <= 1)
                {
                    return;       // empty cell
                }

                // Locked / marked items used to be skipped so the game's red (locked) and
                // gold (marked) cells survived. That is no longer wanted: the state is now
                // reported as text in the hover tooltip (see LockMarkTooltip), so these cells
                // take the rarity colour like every other cell. Cells whose item has no tier
                // still keep the game's colour, which is how an unassigned item stays visible.
                int tier = ItemTiers.TierOf(modelId);
                if (tier <= 0)
                {
                    return;   // unknown item; keep the game's colour
                }

                if (i >= tr.childCount)
                {
                    return;   // dictionary and cell count disagree; leave the game's colour
                }
                Transform child = tr.GetChild(i);
                if (child == null)
                {
                    return;
                }
                PaintCellBackground(child, tier);
            }
        }

        /// <summary>
        /// Applies the rarity colour to one cell's own background Image. Shared with the
        /// equipment-slot patch (see EquipmentRarityPatch), which has the same shape: a cell
        /// object whose root Image is the background and whose child 0 is the item icon.
        /// </summary>
        internal static void PaintCellBackground(Transform cell, int tier)
        {
            if (cell == null || tier <= 0)
            {
                return;
            }
            Image img = cell.GetComponent<Image>();
            if (img == null)
            {
                return;
            }
            Color want = RarityPalette.BagCellColor(tier);
            if (img.color != want)
            {
                img.color = want;
            }
        }

        /// <summary>
        /// Reports a per-cell paint failure. The loop now also runs mid-drag, where the grid
        /// dictionaries are being reshuffled, so a throw here is swallowed and reported once
        /// rather than being allowed to break the game's own refresh.
        /// </summary>
        internal static void LogCellError(int cellIndex, Exception ex)
        {
            if (_cellErrorLogged)
            {
                return;
            }
            _cellErrorLogged = true;
            RareTintPlugin.Log.LogWarning(
                "背包格子上色失败（格 " + cellIndex + "，只报一次，其余格子继续）：" + ex);
        }

        private static bool _cellErrorLogged;

        internal static void LogPaintError(Exception ex)
        {
            if (_paintErrorLogged)
            {
                return;
            }
            _paintErrorLogged = true;
            RareTintPlugin.Log.LogError("背包稀有度底色失败（只报一次）：" + ex);
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
