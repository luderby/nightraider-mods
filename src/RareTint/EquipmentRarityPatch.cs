using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareTint
{
    /// <summary>
    /// Colours the worn equipment slots and the quick bars by item rarity.
    ///
    /// Where the equipment lives: everything the character wears sits in
    /// CurrentDataHub.equipmentBag, a Dictionary&lt;int, CurrentItemData&gt;:
    ///
    ///   0 backpack   1 helmet   2 outfit   3 pants   4 armor   5 boots
    ///   6 primary weapon   7 secondary weapon   8 melee weapon
    ///   9..14 quick bar
    ///
    /// Two different UI objects show that data:
    ///
    ///   bag panel   MyBagControl.myEquipment_GOBJ
    ///                 child 0..8   = the nine equipment slots (own background Image)
    ///                 child 9      = the quick bar, 6 sub-slots (no background Image)
    ///               drawn by RefreshTheEquipmentMessage (MyBagControl.cs:9309)
    ///
    ///   HUD         BattleMessage.shotCutFrame
    ///                 child 0..5   = the quick bar, 6 sub-slots (no background Image)
    ///                 child 6      = label, child 7 = selection highlight
    ///               drawn by RefreshShotCut (BattleMessage.cs:1470)
    ///
    /// Slots that own a background Image (the nine equipment slots) get the rarity colour
    /// directly. Slots that do not (both quick bars) get a thin rarity strip laid along the
    /// bottom edge instead, so the item icon stays fully readable. The strip is created once
    /// and reused; it is the FIRST child so it renders behind the icon and the stack count.
    ///
    /// Worn-out items (nowDurability &lt;= 0 with durability to lose) are not given a rarity
    /// colour at all:
    ///   * equipment slots get a dull grey (BrokenSlotColor) instead of the game's solid red;
    ///   * quick bar strips go grey too;
    ///   * the hover tooltip says 【已损坏】.
    ///
    /// F1 hides the quick bar with shotCutFrame.SetActive(false). That only deactivates the
    /// GameObject, so the strips hide and reappear with it, and because BattleMessage.cs:111
    /// calls RefreshShotCut() when the bar comes back, the colours are re-applied on show.
    /// </summary>
    internal static class EquipmentRarity
    {
        private const string StripName = "RareTintStrip";

        private static FieldInfo _equipField;
        private static FieldInfo _shotCutField;
        private static bool _reflectionFailed;
        private static bool _errorLogged;

        /// <summary>
        /// One strip per quick-bar slot, keyed on the slot Transform. See SetStrip for why
        /// this cache exists instead of looking the strip up by name.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<Transform, Image> _strips
            = new System.Collections.Generic.Dictionary<Transform, Image>();

        // ---------------------------------------------------------------- shared helpers

        /// <summary>
        /// The refit window root (MyBagControl.refitWindow, registered at MyBagControl.cs:9879).
        /// Exposed for the equipment refit panel, which lives in a different container than the
        /// weapon one.
        /// </summary>
        internal static GameObject RefitWindow(MyBagControl bag)
        {
            if (bag == null)
            {
                return null;
            }
            if (_refitWindowField == null && !_refitWindowLookupFailed)
            {
                _refitWindowField = AccessTools.Field(typeof(MyBagControl), "refitWindow");
                if (_refitWindowField == null)
                {
                    _refitWindowLookupFailed = true;
                    RareTintPlugin.Log.LogWarning(
                        "找不到 MyBagControl.refitWindow，装备改装界面底色已停用。");
                    return null;
                }
            }
            if (_refitWindowField == null)
            {
                return null;
            }
            try
            {
                return _refitWindowField.GetValue(bag) as GameObject;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo _refitWindowField;
        private static bool _refitWindowLookupFailed;
        private static FieldInfo _equipPartsFrameField;
        private static bool _equipPartsFrameLookupFailed;

        /// <summary>The four attachment slots of the equipment refit panel.</summary>
        internal static Transform EquipmentPartsFrame(MyBagControl bag)
        {
            if (bag == null)
            {
                return null;
            }
            if (_equipPartsFrameField == null && !_equipPartsFrameLookupFailed)
            {
                _equipPartsFrameField = AccessTools.Field(typeof(MyBagControl), "refitPartsFrameEquipment");
                if (_equipPartsFrameField == null)
                {
                    _equipPartsFrameLookupFailed = true;
                    RareTintPlugin.Log.LogWarning(
                        "找不到 MyBagControl.refitPartsFrameEquipment，装备配件底色已停用。");
                    return null;
                }
            }
            if (_equipPartsFrameField == null)
            {
                return null;
            }
            try
            {
                GameObject go = _equipPartsFrameField.GetValue(bag) as GameObject;
                return go != null ? go.transform : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- painting

        /// <summary>MyBagControl.RefreshTheEquipmentMessage postfix.</summary>
        internal static void PaintEquipment(MyBagControl bag)
        {
            try
            {
                if (!RareTintPlugin.IsEquipmentTintOn() || ItemTiers.Count == 0)
                {
                    return;
                }
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.equipmentBag == null)
                {
                    return;
                }
                if (!ResolveEquipField())
                {
                    return;
                }

                GameObject host = _equipField.GetValue(bag) as GameObject;
                if (host == null)
                {
                    return;
                }

                Transform tr = host.transform;
                int slots = Mathf.Min(9, tr.childCount);
                for (int i = 0; i < slots; i++)
                {
                    CurrentItemData data;
                    if (!cur.equipmentBag.TryGetValue(i, out data) || data == null)
                    {
                        continue;
                    }

                    int modelId = RealModelId(data);
                    if (modelId <= 1)
                    {
                        continue;       // empty slot
                    }

                    if (IsWornOut(data))
                    {
                        PaintRaw(tr.GetChild(i), RareTintPlugin.BrokenSlotColor());
                        continue;
                    }

                    int tier = ItemTiers.TierOf(modelId);
                    if (tier > 0)
                    {
                        BagRarity.PaintCellBackground(tr.GetChild(i), tier);
                    }
                }

                // The bag panel carries its own copy of the quick bar under child 9. That copy
                // maps container index i to equipmentBag[i + 9] (MyBagControl.cs:9358).
                if (tr.childCount > 9)
                {
                    PaintQuickBar(tr.GetChild(9), cur);
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        /// <summary>BattleMessage.RefreshShotCut postfix — the quick bar on the HUD.</summary>
        internal static void PaintHudHotbar(BattleMessage msg)
        {
            try
            {
                if (!RareTintPlugin.IsHudHotbarStripOn() || ItemTiers.Count == 0)
                {
                    return;
                }
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.equipmentBag == null)
                {
                    return;
                }

                if (_shotCutField == null)
                {
                    _shotCutField = AccessTools.Field(typeof(BattleMessage), "shotCutFrame");
                    if (_shotCutField == null)
                    {
                        LogReflection("找不到 BattleMessage.shotCutFrame，HUD 快捷栏色条已停用。");
                        return;
                    }
                }

                GameObject host = _shotCutField.GetValue(msg) as GameObject;
                if (host == null || !host.activeInHierarchy)
                {
                    return;
                }
                PaintQuickBar(host.transform, cur);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        /// <summary>
        /// Paints six quick-bar sub-slots. Sub-slot i maps to equipmentBag[i + 9] in both
        /// places, even though the two containers nest differently:
        ///   HUD        shotCutFrame child 0..5        (BattleMessage.cs:1488)
        ///   bag panel  myEquipment_GOBJ child 9 child 0..5  (MyBagControl.cs:9358, i = 9..14)
        ///
        /// Slots with no item, or an item the tier table does not know, get no strip.
        /// </summary>
        private static void PaintQuickBar(Transform bar, CurrentDataHub cur)
        {
            if (bar == null)
            {
                return;
            }
            int slots = Mathf.Min(6, bar.childCount);
            for (int i = 0; i < slots; i++)
            {
                CurrentItemData data;
                if (!cur.equipmentBag.TryGetValue(i + 9, out data) || data == null)
                {
                    continue;
                }
                int modelId = RealModelId(data);
                Transform slot = bar.GetChild(i);
                if (modelId <= 1)
                {
                    SetStrip(slot, false, Color.clear);
                    continue;
                }
                if (IsWornOut(data))
                {
                    SetStrip(slot, true, RareTintPlugin.BrokenSlotColor());
                    continue;
                }
                int tier = ItemTiers.TierOf(modelId);
                if (tier > 0)
                {
                    SetStrip(slot, true, RarityPalette.RawColor(tier));
                }
                else
                {
                    SetStrip(slot, false, Color.clear);
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Resolves the model id behind a slot. A backpack with its expansion grid open leaves
        /// modelID at 1 and keeps the real model id in exBagDic (CurrentDataHub.cs:4375), the
        /// same convention a covered bag cell uses.
        /// </summary>
        private static int RealModelId(CurrentItemData data)
        {
            if (data == null)
            {
                return 0;
            }
            int modelId = data.modelID;
            if (modelId != 1)
            {
                return modelId;
            }
            CurrentItemData root;
            if (data.exBagDic == null || !data.exBagDic.TryGetValue(0, out root) || root == null)
            {
                return 0;
            }
            return root.modelID;
        }

        /// <summary>
        /// True when the item is worn out. Only items that actually have durability can be worn
        /// out, which is the same rule the game uses to paint the slot red
        /// (MyBagControl.cs:9383).
        /// </summary>
        private static bool IsWornOut(CurrentItemData data)
        {
            if (data == null)
            {
                return false;
            }
            ItemData def = data.GetData();
            return def != null && def.maxDurability > 0 && data.nowDurability <= 0;
        }

        private static void PaintRaw(Transform cell, Color colour)
        {
            if (cell == null)
            {
                return;
            }
            Image img = cell.GetComponent<Image>();
            if (img != null && img.color != colour)
            {
                img.color = colour;
            }
        }

        /// <summary>
        /// Shows or hides a rarity strip along the bottom of a quick-bar slot.
        ///
        /// The strip is cached per slot Transform rather than looked up by name. This matters:
        /// Unity's Transform.Find can miss an object created in the same frame, so a name-based
        /// lookup created a fresh strip on every repaint. RefreshShotCut runs repeatedly (game
        /// start, weapon switches, SettingManager.cs:796), so the child list grew without bound
        /// and the game locked up — that was the reported freeze.
        ///
        /// Getting this right is the whole point: the cache makes the routine idempotent, so
        /// calling it any number of times per frame cannot add a second strip. The key is the
        /// slot Transform itself, and a Unity-destroyed slot reads back as a null key value.
        ///
        /// The strip is appended as the last child instead of being moved to the front: sibling
        /// reordering during a UI refresh forces layout rebuilds for no benefit here, because
        /// the strip only covers the bottom 3px while the icon is centred.
        /// </summary>
        private static void SetStrip(Transform slot, bool on, Color colour)
        {
            if (slot == null)
            {
                return;
            }

            Image strip;
            if (!_strips.TryGetValue(slot, out strip) || strip == null)
            {
                if (!on)
                {
                    return;                 // nothing to show and nothing cached yet
                }
                strip = CreateStrip(slot);
                if (strip == null)
                {
                    return;
                }
                _strips[slot] = strip;
            }

            if (!on)
            {
                if (strip.enabled)
                {
                    strip.enabled = false;
                }
                return;
            }
            if (!strip.enabled)
            {
                strip.enabled = true;
            }
            if (strip.color != colour)
            {
                strip.color = colour;
            }
        }

        /// <summary>Creates the strip object for one slot. At most one per slot, ever.</summary>
        private static Image CreateStrip(Transform slot)
        {
            try
            {
                GameObject go = new GameObject(StripName, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(slot, false);

                Image strip = go.GetComponent<Image>();
                strip.raycastTarget = false;    // never eats a click; the quick bar must stay usable

                // Stretch across the slot's width and sit on its bottom edge, so the strip
                // scales with whatever size the slot has instead of relying on a fixed pixel
                // height. 3px keeps it visible even in a small quick bar.
                RectTransform rt = strip.rectTransform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.offsetMin = new Vector2(0f, 0f);
                rt.offsetMax = new Vector2(0f, StripHeight);
                rt.localScale = Vector3.one;
                return strip;
            }
            catch (Exception ex)
            {
                LogError(ex);
                return null;
            }
        }

        /// <summary>Thickness of the rarity strip in UI pixels.</summary>
        private const float StripHeight = 3f;

        private static bool ResolveEquipField()
        {
            if (_equipField != null)
            {
                return true;
            }
            if (_reflectionFailed)
            {
                return false;
            }
            _equipField = AccessTools.Field(typeof(MyBagControl), "myEquipment_GOBJ");
            if (_equipField == null)
            {
                LogReflection("找不到 MyBagControl.myEquipment_GOBJ，装备栏稀有度底色已停用。");
                return false;
            }
            return true;
        }

        private static void LogReflection(string message)
        {
            if (_reflectionFailed)
            {
                return;
            }
            _reflectionFailed = true;
            RareTintPlugin.Log.LogWarning(message);
        }

        /// <summary>
        /// Puts the rarity colour behind a refit attachment slot's icon, without adding any
        /// object to the hierarchy, and hides the bogus empty-state placeholder.
        ///
        /// Two separate things are handled here.
        ///
        /// 1. The rarity colour. It goes on the slot rectangle, which is the fill the game draws
        ///    behind the icon. The icon artwork is deliberately left in its own colours: tinting
        ///    it too made the part art change colour, which hurt readability.
        ///
        /// 2. A game quirk. For an empty slot the weapon panel activates child 0 as the
        ///    "placeholder" (RefitControl.cs:244-247), but that child's sprite is not a generic
        ///    empty icon — it is the icon of the part that belongs in that slot. A logged
        ///    example: slot 1 held part 2125 with child 1 showing partsIcon_125, and after
        ///    clearing it child 0 was activated still wearing partsIcon_124, its slot's own part
        ///    icon. So a cleared slot kept showing a part that was not installed. Disabling that
        ///    Image (not the GameObject, so the game's own active state is untouched) leaves the
        ///    slot visually empty as intended.
        /// </summary>
        internal static void TintSlotIcon(Transform slot, int tier)
        {
            if (slot == null)
            {
                return;
            }
            Color want = tier > 0 ? RarityPalette.BagCellColor(tier) : Color.white;

            // The slot rectangle behind the icon. This is the rarity background.
            Image background = slot.GetComponent<Image>();
            if (background != null && background.color != want)
            {
                background.color = want;
            }

            int n = Mathf.Min(2, slot.childCount);
            for (int i = 0; i < n; i++)
            {
                Transform child = slot.GetChild(i);
                if (child == null)
                {
                    continue;
                }
                Image img = child.GetComponent<Image>();
                if (img == null)
                {
                    continue;
                }

                // The icon art keeps its own colours. The game leaves these Images white, so
                // this only undoes a tint an earlier build of this mod may have left behind.
                if (img.color != Color.white)
                {
                    img.color = Color.white;
                }

                // An empty slot must not show the placeholder, because that placeholder carries
                // a part icon. Only the slot's own background should remain.
                if (tier <= 0 && i == 0 && img.enabled)
                {
                    img.enabled = false;
                }
            }
        }

        /// <summary>Creates or updates the full-size rarity backing layer on a cell.</summary>
        internal static void ShowOverlay(Transform cell, Color colour)
        {
            RefitRarity.ShowOverlay(cell, colour);
        }

        /// <summary>Hides the backing layer on a cell, if it has one.</summary>
        internal static void HideOverlay(Transform cell)
        {
            RefitRarity.HideOverlay(cell);
        }

        internal static void LogError(Exception ex)
        {
            if (_errorLogged)
            {
                return;
            }
            _errorLogged = true;
            RareTintPlugin.Log.LogWarning("装备栏/快捷栏上色失败（只报一次）：" + ex);
        }
    }

    /// <summary>
    /// Postfix on MyBagControl.RefreshTheEquipmentMessage. That method needs no arguments, so
    /// the instance is taken from the patch's __instance.
    /// </summary>
    [HarmonyPatch(typeof(MyBagControl), "RefreshTheEquipmentMessage")]
    internal static class EquipmentRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyBagControl __instance)
        {
            EquipmentRarity.PaintEquipment(__instance);
        }
    }

    /// <summary>
    /// Postfix on BattleMessage.RefreshShotCut — the HUD quick bar. RefreshShotCut returns
    /// early while the bar is hidden (BattleMessage.cs:1472), so this simply does not run then;
    /// showing the bar calls RefreshShotCut again, which re-applies the colours.
    /// </summary>
    [HarmonyPatch(typeof(BattleMessage), "RefreshShotCut")]
    internal static class HotbarRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(BattleMessage __instance)
        {
            EquipmentRarity.PaintHudHotbar(__instance);
        }
    }
}
