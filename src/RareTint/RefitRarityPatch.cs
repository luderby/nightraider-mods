using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareTint
{
    /// <summary>
    /// Colours the weapon-modification ("改装") panel by rarity.
    ///
    /// Two surfaces, both living under RefitControl:
    ///
    ///   1. the seven attachment slots
    ///        refitFrame.GetChild(3).GetChild(0..6)   (RefitControl.cs:200, :1417)
    ///      Slot ps (0-based) shows equipmentBag[weaponIndex] via
    ///      CurrentDataHub.GetEquipmentPartsByIndex(dt, ps) (CurrentDataHub.cs:5273), whose
    ///      mapping is 0=partsA 1=partsB 2=partsC 3=partsD 4=partsE 5=partsF 6=partsG.
    ///      ClearParts (RefitControl.cs:50) confirms the same order for the per-slot buttons.
    ///
    ///   2. the parts-chooser popup while installing
    ///        RefitControl.partsListWindow children
    ///      The game already colours each entry by *availability*
    ///      (RefitControl.cs:656-665: white = installable, red = not). Those roots do have an
    ///      Image, so they are tinted directly — but "not installable" is shown as a dull grey
    ///      rather than the rarity colour, so the warning still reads.
    ///
    /// The seven slots have no Image of their own per the decompiled game code (nothing ever
    /// sets their colour), so the same 3px rarity strip used by the quick bar is added along
    /// the bottom edge, which never covers the icon. Whether the slot root really lacks an
    /// Image cannot be told from source alone, so it is probed at run time and the strip is
    /// only used when there is no background to tint.
    ///
    /// Why a poll instead of Harmony patches: RefreshWeaponAllData, OpenPartsChange and
    /// RefreshMessage are all private, and the panel only repaints when the player swaps or
    /// clears an attachment. Repainting is therefore driven by a cheap signature of the seven
    /// attachment ids, compared once per frame while the panel is open, so nothing is redrawn
    /// unless the loadout actually changed.
    ///
    /// Hover tooltips for attachments already work: the game calls
    /// EXMessageShow.ResetData(partsID, 1, null), which reaches the tooltip hook that colours
    /// the name by ItemTiers.TierOf(modelID).
    /// </summary>
    internal static class RefitRarity
    {
        private const string StripName = "RareTintStrip";

        private static FieldInfo _partsListField;
        private static bool _partsListLookupFailed;
        private static bool _errorLogged;

        /// <summary>Null = not probed yet; true = slots own an Image, false = add a strip.</summary>
        private static bool? _slotsHaveImage;

        /// <summary>
        /// True while the modification panel is on screen. CheckOpen() reads
        /// transform.GetChild(0).activeSelf (RefitControl.cs:24-27).
        /// </summary>
        internal static bool IsPanelOpen(RefitControl refit)
        {
            try
            {
                return refit != null && refit.CheckOpen();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Repaints attachments and the item slot. Hooked to the game's own redraw
        /// (RefreshWeaponAllData) so the colours land in the same frame.
        /// </summary>
        internal static void PaintPanel(RefitControl refit)
        {
            try
            {
                if (!Ready(refit))
                {
                    return;
                }
                CurrentDataHub cur = GameRefs.Current;
                CurrentItemData item = CurrentWeapon(refit);
                // Diagnose before any early exit: if the equipment panel shows no colour, the
                // first thing to rule out is that the hook ran but bailed out here.
                DumpPanel(refit, cur, item);
                if (cur == null || item == null || item.modelID <= 1)
                {
                    return;
                }
                // Weapons and wearable equipment share this panel: armour takes a ballistic
                // plate in partsE and helmets take a head lamp / night sight in partsE, all
                // through the same screen. Both are painted.
                PaintSlots(cur, item);
                if (IsWeapon(item))
                {
                    // Weapons get a strip on the display slot. Equipment cannot take a strip:
                    // its display slot holds the model hierarchy PutDataToWeaponImage_UI indexes
                    // by child number (MyBagControl.cs:10173+), so an extra child would shift
                    // those indices. It gets a full-size backing overlay instead, which sits
                    // behind the icon and adds no child to the model hierarchy.
                    PaintWeaponSlot(item);
                }
                else
                {
                    PaintDisplaySlot(item);
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        /// <summary>
        /// Repaints the attachment chooser. Hooked straight to the game's OpenPartsChange,
        /// which recycles and re-creates every entry; a per-frame poll used to arrive a frame
        /// late, so the game's own white/red leaked through and the colours flickered.
        /// </summary>
        internal static void PaintChooser(RefitControl refit)
        {
            try
            {
                if (!Ready(refit))
                {
                    return;
                }
                PaintPartsList(GameRefs.Current);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static bool Ready(RefitControl refit)
        {
            return refit != null
                && RareTintPlugin.IsEquipmentTintOn()
                && ItemTiers.Count > 0;
        }

        /// <summary>
        /// Confirms once per session that a hook actually fired. BepInEx does not report
        /// individual patches, and these two target private game methods, so without this a
        /// silently-failed patch would look exactly like "the colours don't work".
        /// </summary>
        internal static void LogHookedOnce(ref bool logged, string message)
        {
            if (logged)
            {
                return;
            }
            logged = true;
            if (RareTintPlugin.Log != null)
            {
                RareTintPlugin.Log.LogInfo(message);
            }
        }

        private static bool IsWeapon(CurrentItemData dt)
        {
            return dt != null && dt.modelID >= 1000 && dt.modelID < 2000;
        }

        // ---------------------------------------------------------------- surfaces

        /// <summary>
        /// The seven attachment slots. <paramref name="item"/> may be a weapon or a wearable —
        /// the same panel serves both — and its partsA..G hold the fitted attachment ids.
        /// </summary>
        private static void PaintSlots(CurrentDataHub cur, CurrentItemData item)
        {
            GameObject frame = GetRefitFrame();
            if (frame == null)
            {
                return;
            }
            Transform slotRoot = frame.transform.childCount > 3 ? frame.transform.GetChild(3) : null;
            if (slotRoot == null)
            {
                return;
            }

            if (_slotsHaveImage == null)
            {
                // Probe once: does the slot root carry its own background Image?
                _slotsHaveImage = slotRoot.childCount == 0
                    || slotRoot.GetChild(0).GetComponent<Image>() != null;
            }

            int slots = Mathf.Min(7, slotRoot.childCount);
            DumpOnce(SigOf(cur, item), cur, item, slotRoot);
            for (int ui = 0; ui < slots; ui++)
            {
                int partId = cur.GetEquipmentPartsByIndex(item, PartIndexForUiSlot(ui));
                Transform slot = slotRoot.GetChild(ui);

                int tier = partId >= 2000 ? ItemTiers.TierOf(partId) : 0;
                if (tier > 0)
                {
                    // Colour the icon the slot is showing; nothing is added to the hierarchy, so
                    // no sibling index the game reads can shift.
                    EquipmentRarity.TintSlotIcon(slot, tier);
                }
                else
                {
                    // Cleared slot: reset it, because nothing else repaints it and the previous
                    // attachment's colour would otherwise stay behind.
                    EquipmentRarity.TintSlotIcon(slot, 0);
                }
                SetStrip(slot, false, Color.clear);
            }
        }

        /// <summary>
        /// Reports what the refit hook actually sees, every time the panel repaints. Unlike the
        /// per-loadout dump this one is not de-duplicated, because the interesting case is
        /// exactly the one where nothing appears: it shows whether newDT was readable, what the
        /// model id was, and whether the display slot exists.
        /// </summary>
        private static void DumpPanel(RefitControl refit, CurrentDataHub cur, CurrentItemData item)
        {
            if (RareTintPlugin.Log == null)
            {
                return;
            }
            try
            {
                GameObject frame = GetRefitFrame();
                string newDt = item == null ? "null" : item.modelID.ToString();
                string kind = item == null ? "?" : (item.modelID >= 1000 && item.modelID < 2000 ? "武器" : "装备/其它");
                string frameInfo;
                if (frame == null)
                {
                    frameInfo = "refitFrame=null";
                }
                else
                {
                    Transform slotRoot = frame.transform.childCount > 3 ? frame.transform.GetChild(3) : null;
                    frameInfo = "refitFrame=ok children=" + frame.transform.childCount
                        + " slotRoot=" + (slotRoot == null ? "null" : "children=" + slotRoot.childCount);
                }
                RareTintPlugin.Log.LogInfo(
                    "[refit] hook ran: newDT.modelID=" + newDt + " (" + kind + ") " + frameInfo
                    + " tiers=" + ItemTiers.Count
                    + " equipTintOn=" + RareTintPlugin.IsEquipmentTintOn());
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        /// <summary>A stable string for the current loadout, used to de-duplicate log dumps.</summary>
        private static string SigOf(CurrentDataHub cur, CurrentItemData item)
        {
            if (cur == null || item == null)
            {
                return "";
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(item.modelID);
            for (int ps = 0; ps < 7; ps++)
            {
                sb.Append(':').Append(cur.GetEquipmentPartsByIndex(item, ps));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The display slot (refitFrame child 2) for wearable equipment. Nothing is added to it:
        /// PutDataToWeaponImage_UI indexes that hierarchy by child number, so an extra object
        /// would shift the model indices. The slot's own Image is tinted instead.
        /// </summary>
        private static void PaintDisplaySlot(CurrentItemData item)
        {
            GameObject frame = GetRefitFrame();
            if (frame == null || frame.transform.childCount <= 2 || item == null)
            {
                return;
            }
            Image background = frame.transform.GetChild(2).GetComponent<Image>();
            if (background == null)
            {
                return;
            }
            int tier = ItemTiers.TierOf(item.modelID);
            Color want = tier > 0 ? RefitCellColor(tier) : Color.white;
            if (background.color != want)
            {
                background.color = want;
            }
        }

        /// <summary>
        /// The display slot (refitFrame child 2) for a weapon. Unlike equipment it can take a
        /// child, so it gets the same 3px rarity strip as the attachment slots.
        /// </summary>
        private static void PaintWeaponSlot(CurrentItemData weapon)
        {
            GameObject frame = GetRefitFrame();
            if (frame == null || frame.transform.childCount <= 2 || weapon == null)
            {
                return;
            }
            Transform slot = frame.transform.GetChild(2);
            int tier = ItemTiers.TierOf(weapon.modelID);
            if (tier > 0)
            {
                SetStrip(slot, true, RefitCellColor(tier));
            }
            else
            {
                SetStrip(slot, false, Color.clear);
            }
        }

        /// <summary>
        /// The parts chooser. Entries are pooled, so nothing is cached here: the game owns
        /// these objects' lifetime. Installable entries take the rarity colour, uninstallable
        /// ones take the dull grey from BrokenSlotColor so the warning survives.
        /// </summary>
        private static void PaintPartsList(CurrentDataHub cur)
        {
            Transform list = GetPartsListWindow();
            if (list == null)
            {
                return;
            }
            Color unavailable = RareTintPlugin.BrokenSlotColor();
            for (int i = 0; i < list.childCount; i++)
            {
                Transform entry = list.GetChild(i);
                if (entry == null)
                {
                    continue;
                }
                Image img = entry.GetComponent<Image>();
                if (img == null)
                {
                    continue;
                }
                Button button = entry.GetComponent<Button>();
                if (button != null && !button.interactable)
                {
                    if (img.color != unavailable)
                    {
                        img.color = unavailable;
                    }
                    continue;
                }
                // Which attachment is this entry showing? PartsButton stores it privately, and
                // the entry's own EXMessageShow holds the same id, so read it from there.
                int partId = PartIdOf(entry);
                if (partId < 2000)
                {
                    continue;
                }
                int tier = ItemTiers.TierOf(partId);
                if (tier <= 0)
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

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// The weapon being modified. RefitControl.newDT is the panel's working copy of it
        /// (created in Find, filled by GetSourceData), so this works no matter where the weapon
        /// currently lives — equipped, in the bag, in the depot or in a loot container.
        /// </summary>
        private static CurrentItemData CurrentWeapon(RefitControl refit)
        {
            if (refit == null)
            {
                return null;
            }
            if (_newDtField == null && !_newDtLookupFailed)
            {
                _newDtField = AccessTools.Field(typeof(RefitControl), "newDT");
                if (_newDtField == null)
                {
                    _newDtLookupFailed = true;
                    LogReflection("找不到 RefitControl.newDT，改装界面底色已停用。");
                    return null;
                }
            }
            if (_newDtField == null)
            {
                return null;
            }
            try
            {
                return _newDtField.GetValue(refit) as CurrentItemData;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo _newDtField;
        private static bool _newDtLookupFailed;

        /// <summary>Reads the attachment id an entry shows. EXMessageShow.myID is public.</summary>
        private static int PartIdOf(Transform entry)
        {
            try
            {
                EXMessageShow show = entry.GetComponent<EXMessageShow>();
                if (show != null)
                {
                    return show.myID;
                }
            }
            catch
            {
            }
            return 0;
        }

        private static GameObject GetRefitFrame()
        {
            RefitControl refit = GameRefs.Refit;
            if (refit == null)
            {
                return null;
            }
            FieldInfo f = _refitFrameField ?? (_refitFrameField = AccessTools.Field(typeof(RefitControl), "refitFrame"));
            if (f == null)
            {
                LogReflection("找不到 RefitControl.refitFrame，改装界面底色已停用。");
                return null;
            }
            return f.GetValue(refit) as GameObject;
        }

        private static FieldInfo _refitFrameField;

        private static Transform GetPartsListWindow()
        {
            if (_partsListLookupFailed)
            {
                return null;
            }
            RefitControl refit = GameRefs.Refit;
            if (refit == null)
            {
                return null;
            }
            if (_partsListField == null)
            {
                _partsListField = AccessTools.Field(typeof(RefitControl), "partsListWindow");
                if (_partsListField == null)
                {
                    _partsListLookupFailed = true;
                    LogReflection("找不到 RefitControl.partsListWindow，配件列表底色已停用。");
                    return null;
                }
            }
            GameObject go = _partsListField.GetValue(refit) as GameObject;
            return go != null ? go.transform : null;
        }

        /// <summary>
        /// Applies the rarity colour to a slot, and makes sure it actually sticks: a shallow
        /// Image only tints the sprite that is already there, so a slot whose background is a
        /// dim template would swallow the colour. When the tint does not take (or there is no
        /// sprite to tint), a plain filled overlay is created instead.
        /// </summary>
        private static void ForceSlotColour(Transform slot, Color colour)
        {
            if (slot == null)
            {
                return;
            }
            Image img = slot.GetComponent<Image>();
            if (img != null)
            {
                try
                {
                    if (img.sprite != null)
                    {
                        img.color = colour;
                        if (img.color == colour)
                        {
                            return;         // tint took
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            }
            ShowOverlay(slot, colour);
        }

        /// <summary>
        /// The sprite the game's own bag cells use. A bag cell renders colour x sprite, while
        /// the overlay is a flat block rendering colour alone, so the overlay reads brighter
        /// than the matching bag cell unless it multiplies by the same sprite. Sampling one real
        /// cell once keeps the two identical.
        /// </summary>
        private static Sprite BagCellSprite()
        {
            if (_bagCellSprite != null || _bagCellSpriteLookedUp)
            {
                return _bagCellSprite;
            }
            _bagCellSpriteLookedUp = true;
            try
            {
                MyBagControl bag = UnityEngine.Object.FindObjectOfType<MyBagControl>();
                if (bag == null)
                {
                    return null;
                }
                FieldInfo f = AccessTools.Field(typeof(MyBagControl), "myBag_GOBJ");
                GameObject host = f != null ? f.GetValue(bag) as GameObject : null;
                if (host == null || host.transform.childCount == 0)
                {
                    return null;
                }
                Image cell = host.transform.GetChild(0).GetComponent<Image>();
                if (cell != null)
                {
                    _bagCellSprite = cell.sprite;
                }
            }
            catch
            {
                _bagCellSprite = null;
            }
            return _bagCellSprite;
        }

        private static Sprite _bagCellSprite;
        private static bool _bagCellSpriteLookedUp;

        /// <summary>
        /// The colour to show on a refit slot. It is the same value a bag cell uses, so that the
        /// two read the same; when no bag cell sprite could be sampled the flat block is dimmed
        /// a little to compensate for the missing sprite multiplication.
        /// </summary>
        internal static Color RefitCellColor(int tier)
        {
            Color c = RarityPalette.BagCellColor(tier);
            if (BagCellSprite() != null)
            {
                return c;
            }
            float k = FlatBlockCompensation;
            return new Color(c.r * k, c.g * k, c.b * k, 1f);
        }

        /// <summary>
        /// Fallback dimming for a flat colour block, applied only when the game's cell sprite
        /// could not be sampled. Cell sprites are not pure white, so an unadjusted block of the
        /// same colour reads brighter than the cell it is meant to match.
        /// </summary>
        private const float FlatBlockCompensation = 0.85f;

        /// <summary>
        /// Attaches the overlay to a slot's icon child and stretches it to the slot's full size.
        ///
        /// Why the icon child and not the slot itself: these slots are addressed by position —
        /// the empty-state placeholder is GetChild(0) and the installed-part icon is GetChild(1)
        /// (MyBagControl.cs:7473-7486, RefitControl.cs:204-540) — so appending to the slot is
        /// safe but renders on top of the icon, and inserting at index 0 corrupts the indices.
        /// The icon child has no such convention, so a child there is unambiguously behind the
        /// icon while staying inside the slot's own rendering layer. A child Canvas was tried
        /// first but sank the overlay below the whole panel, making it invisible.
        ///
        /// It also gives a natural lifetime: the game deactivates the icon child for an empty
        /// slot, so the colour hides itself exactly when there is nothing to show.
        /// </summary>
        private static Transform OverlayParent(Transform slot)
        {
            if (slot != null && slot.childCount > 1)
            {
                Transform icon = slot.GetChild(1);
                if (icon != null)
                {
                    return icon;
                }
            }
            return slot;
        }

        /// <summary>
        /// Full-size backing image used when a slot's own Image cannot be tinted.
        ///
        /// <paramref name="colour"/> must be the *final* colour to show, not a multiplier — use
        /// RefitCellColor for that. The overlay also borrows the game's own cell sprite when one
        /// could be sampled, so it multiplies exactly like a bag cell does.
        /// </summary>
        internal static void ShowOverlay(Transform slot, Color colour)
        {
            Image overlay;
            if (!_overlays.TryGetValue(slot, out overlay) || overlay == null)
            {
                try
                {
                    GameObject go = new GameObject(OverlayName, typeof(RectTransform), typeof(Image));
                    go.transform.SetParent(OverlayParent(slot), false);
                    // Appended, never inserted: these slots are addressed by position. The game
                    // keeps its empty-state placeholder at GetChild(0) and the installed-part
                    // icon at GetChild(1) (MyBagControl.cs:7473-7486 for equipment,
                    // RefitControl.cs:204-540 for weapons), so inserting at index 0 shifted both
                    // and made every slot show the wrong attachment's icon — including a stale
                    // one after clearing the slots. Appending leaves indices 0 and 1 untouched.
                    overlay = go.GetComponent<Image>();
                    overlay.raycastTarget = false;
                    // Borrow the game's own cell sprite so the block multiplies identically to
                    // a bag cell; fall back to a flat block when it could not be sampled (in
                    // which case the caller has already dimmed the colour to compensate).
                    overlay.sprite = BagCellSprite();

                    RectTransform rt = overlay.rectTransform;
                    // The overlay lives under the icon child but must cover the whole slot, so it
                    // is not anchored to that child: its four edges are computed so the block
                    // lands exactly on the slot rectangle.
                    RectTransform slotRt = slot as RectTransform;
                    RectTransform parentRt = rt.parent as RectTransform;
                    if (slotRt != null && parentRt != null && !Mathf.Approximately(parentRt.rect.width, 0f))
                    {
                        float sx = (slotRt.rect.width > 0f && parentRt.rect.width > 0f)
                            ? slotRt.rect.width / parentRt.rect.width : 1f;
                        float sy = (slotRt.rect.height > 0f && parentRt.rect.height > 0f)
                            ? slotRt.rect.height / parentRt.rect.height : 1f;
                        Vector2 local = parentRt.InverseTransformPoint(slotRt.position);
                        rt.anchorMin = new Vector2(0.5f, 0.5f);
                        rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.sizeDelta = new Vector2(slotRt.rect.width, slotRt.rect.height);
                        rt.anchoredPosition = local;
                        rt.localScale = new Vector3(sx, sy, 1f);
                    }
                    else
                    {
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        rt.localScale = Vector3.one;
                    }
                    _overlays[slot] = overlay;
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    return;
                }
            }
            if (!overlay.enabled)
            {
                overlay.enabled = true;
            }
            if (overlay.color != colour)
            {
                overlay.color = colour;
            }
        }

        internal static void HideOverlay(Transform slot)
        {
            if (slot == null)
            {
                return;
            }
            Image overlay;
            if (_overlays.TryGetValue(slot, out overlay) && overlay != null && overlay.enabled)
            {
                overlay.enabled = false;
            }
        }

        /// <summary>
        /// Dumps what the panel actually looks like, once per distinct loadout. This is the
        /// only way to tell "the hook never ran" apart from "the colour was applied and then
        /// overwritten" without a debugger.
        /// </summary>
        private static void DumpOnce(string sig, CurrentDataHub cur, CurrentItemData item, Transform slotRoot)
        {
            if (sig == _lastDumpSig)
            {
                return;
            }
            _lastDumpSig = sig;
            if (RareTintPlugin.Log == null || cur == null)
            {
                return;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[refit] item=").Append(item.modelID).Append(" parts A..G=");
            for (int ps = 0; ps < 7; ps++)
            {
                sb.Append(cur.GetEquipmentPartsByIndex(item, ps)).Append(ps < 6 ? "," : "");
            }
            sb.Append(" | slots:");
            for (int ui = 0; ui < slotRoot.childCount && ui < 7; ui++)
            {
                Transform slot = slotRoot.GetChild(ui);
                sb.Append(" [ui=").Append(ui)
                  .Append(" pid=").Append(cur.GetEquipmentPartsByIndex(item, PartIndexForUiSlot(ui)));
                // The two children the game toggles: 0 is the empty-state placeholder, 1 is the
                // installed-part icon. Reporting active/enabled plus the sprite name is what
                // tells a stale icon apart from a deliberate placeholder.
                for (int c = 0; c < 2 && c < slot.childCount; c++)
                {
                    Transform ch = slot.GetChild(c);
                    Image ci = ch.GetComponent<Image>();
                    sb.Append(" c").Append(c)
                      .Append(ch.gameObject.activeSelf ? "=on" : "=off");
                    if (ci != null)
                    {
                        sb.Append(ci.enabled ? "/en" : "/dis")
                          .Append("/").Append(ci.sprite != null ? ci.sprite.name : "null");
                    }
                    else
                    {
                        sb.Append("/noimg");
                    }
                }
                sb.Append("]");
                // Anything beyond the two the game uses, plus how many children the slot has.
                // A leftover object from an earlier build would show up here, and an extra child
                // is exactly the kind of thing that shifts the game's positional lookups.
                if (slot.childCount > 2)
                {
                    sb.Append("(extra=").Append(slot.childCount - 2);
                    for (int c = 2; c < slot.childCount; c++)
                    {
                        sb.Append('/').Append(slot.GetChild(c).name);
                    }
                    sb.Append(')');
                }
            }
            RareTintPlugin.Log.LogInfo(sb.ToString());
        }

        private static string ColorStr(Color c)
        {
            return ((int)(c.r * 255)) + "/" + ((int)(c.g * 255)) + "/" + ((int)(c.b * 255));
        }

        private static string _lastDumpSig;
        private static readonly Dictionary<Transform, Image> _overlays = new Dictionary<Transform, Image>();
        private const string OverlayName = "RareTintOverlay";

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
        /// Shows or hides the 3px rarity strip along the bottom of a cell. Cached per Transform
        /// for the same reason as the quick bar: a name-based lookup can miss an object created
        /// in the same frame and then rebuild it on every repaint.
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
                    return;
                }
                GameObject go = new GameObject(StripName, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(slot, false);
                strip = go.GetComponent<Image>();
                strip.raycastTarget = false;

                RectTransform rt = strip.rectTransform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.offsetMin = new Vector2(0f, 0f);
                rt.offsetMax = new Vector2(0f, StripHeight);
                rt.localScale = Vector3.one;
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

        /// <summary>
        /// Maps a UI attachment slot to the index GetEquipmentPartsByIndex expects.
        ///
        /// The two orders are NOT the same, which is easy to get wrong: the API is a plain
        /// switch returning partsA..partsG in order (CurrentDataHub.cs:5277), but the panel
        /// lays its seven slots out as A, B, C, F, D, E, G. Four independent places in the game
        /// agree on that layout, so it is not a decompiler artefact:
        ///
        ///   ClearParts          slot 1..7 -> partsA partsB partsC partsF partsD partsE partsG
        ///                       (RefitControl.cs:55-82)
        ///   ButtonParts         same order (RefitControl.cs:932-959)
        ///   RefreshWeaponAllData  GetChild(3).GetChild(0..6) -> partsA B C F D E G
        ///                       (RefitControl.cs:200-540)
        ///   OpenPartsChange     the seven cases compare sourceDT in the same order
        ///                       (RefitControl.cs:652-898)
        ///
        /// Feeding the API index straight through therefore shifted every attachment from slot 3
        /// onwards: the scope's colour appeared on the magazine slot, and so on.
        /// </summary>
        private static readonly int[] UiSlotToPartIndex = { 0, 1, 2, 5, 3, 4, 6 };

        private static int PartIndexForUiSlot(int uiSlot)
        {
            if (uiSlot < 0 || uiSlot >= UiSlotToPartIndex.Length)
            {
                return 0;
            }
            return UiSlotToPartIndex[uiSlot];
        }

        private const float StripHeight = 3f;

        private static readonly Dictionary<Transform, Image> _strips = new Dictionary<Transform, Image>();

        /// <summary>
        /// Drops strips whose slot has been destroyed, so the cache cannot grow without bound as
        /// weapons are opened over a session. A Unity-destroyed Transform reads back as null.
        /// </summary>
        internal static void PruneCache()
        {
            if (_strips.Count >= 64)
            {
                PruneOne(_strips);
            }
            if (_overlays.Count >= 64)
            {
                PruneOne(_overlays);
            }
        }

        private static void PruneOne(Dictionary<Transform, Image> map)
        {
            var dead = new List<Transform>();
            foreach (var kv in map)
            {
                if (kv.Key == null || kv.Value == null)
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (Transform t in dead)
            {
                if (t != null)
                {
                    map.Remove(t);
                }
            }
        }

        internal static void ResetCache()
        {
            _strips.Clear();
            _slotsHaveImage = null;
        }

        private static void LogReflection(string message)
        {
            RareTintPlugin.Log.LogWarning(message);
        }

        internal static void LogError(Exception ex)
        {
            if (_errorLogged)
            {
                return;
            }
            _errorLogged = true;
            RareTintPlugin.Log.LogWarning("改装界面底色失败（只报一次）：" + ex);
        }
    }

    /// <summary>
    /// Postfix on MyBagControl.RefreshRefitPartsFrame_Equipment — the equipment side of the
    /// refit window, which is a completely separate UI from the weapon side.
    ///
    /// SwitchTheRefitWindow picks between the two by model id (MyBagControl.cs:6660):
    ///   modelID &lt; 5000  -> refitWindow child 4, RefreshRefitPartsFrame_Weapon
    ///   modelID &gt;= 5000 -> refitWindow child 5, RefreshRefitPartsFrame_Equipment
    ///
    /// The equipment side shows the worn item's icon at
    /// refitWindow.GetChild(2).GetChild(0).GetChild(0) (MyBagControl.cs:6701) and four
    /// attachment slots in refitPartsFrameEquipment (registered at MyBagControl.cs:9880).
    /// Those four are partsA, partsB, partsC, partsD in order — unlike the weapon panel there
    /// is no F/D/E shuffle, so the API index is used directly.
    /// </summary>
    [HarmonyPatch(typeof(MyBagControl), "RefreshRefitPartsFrame_Equipment")]
    internal static class RefitEquipmentRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyBagControl __instance, CurrentItemData dt)
        {
            try
            {
                if (dt == null || !RareTintPlugin.IsEquipmentTintOn() || ItemTiers.Count == 0)
                {
                    return;
                }

                // The worn item's own display slot.
                //
                // refitMainFrame is refitWindow child 2 (MyBagControl.cs:9877), and the game
                // indexes its children by position:
                //   child 0      wrapper whose GetChild(0) is the item image
                //   child 1      ammo count text
                //   children 2,3 weapon model slots
                // RefreshRefitMainFrame also walks the item image's own children and deactivates
                // them every repaint (MyBagControl.cs:6692-6698).
                //
                // So nothing may be inserted into either parent: doing that shifted the indices
                // the game relies on, which is why the icon and the attachment slots vanished the
                // second time the panel was opened. The wrapper's own Image is tinted instead,
                // which changes no structure at all.
                GameObject window = EquipmentRarity.RefitWindow(__instance);
                if (window != null && window.transform.childCount > 2)
                {
                    Image background = window.transform.GetChild(2).GetComponent<Image>();
                    if (background != null)
                    {
                        int iconTier = ItemTiers.TierOf(dt.modelID);
                        background.color = iconTier > 0
                            ? RarityPalette.BagCellColor(iconTier)
                            : Color.white;
                    }
                }

                // The four attachment slots.
                Transform slots = EquipmentRarity.EquipmentPartsFrame(__instance);
                if (slots == null)
                {
                    return;
                }
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null)
                {
                    return;
                }
                int count = Mathf.Min(4, slots.childCount);
                for (int i = 0; i < count; i++)
                {
                    Transform slot = slots.GetChild(i);
                    int partId = cur.GetEquipmentPartsByIndex(dt, i);
                    int tier = partId >= 2000 ? ItemTiers.TierOf(partId) : 0;

                    // Colour the slot rectangle and the icon the game is actually showing.
                    // Nothing is added to the hierarchy, so no sibling index the game reads can
                    // shift — see TintSlotIcon for why both earlier approaches failed.
                    EquipmentRarity.TintSlotIcon(slot, tier);
                }
            }
            catch (Exception ex)
            {
                RareTintPlugin.Log.LogWarning("装备改装界面底色失败（只报一次）：" + ex);
            }
        }
    }

    /// <summary>
    /// Postfix on RefitControl.RefreshWeaponAllData — the game's own redraw of the seven
    /// attachment slots and the weapon display. Private in the game, so the patch is applied by
    /// name (Harmony can patch private methods; only abstract/extern ones are off limits).
    /// </summary>
    [HarmonyPatch(typeof(RefitControl), "RefreshWeaponAllData")]
    internal static class RefitSlotsRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RefitControl __instance)
        {
            RefitRarity.PruneCache();
            RefitRarity.PaintPanel(__instance);
            RefitRarity.LogHookedOnce(ref _slotsLogged, "改装槽位钩子已生效（RefreshWeaponAllData）");
        }

        private static bool _slotsLogged;
    }

    /// <summary>
    /// Postfix on RefitControl.OpenPartsChange — the chooser recycles every entry and rebuilds
    /// them, so the colours must be applied after that, in the same frame.
    /// </summary>
    [HarmonyPatch(typeof(RefitControl), "OpenPartsChange")]
    internal static class RefitChooserRarityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RefitControl __instance)
        {
            RefitRarity.PaintChooser(__instance);
            RefitRarity.LogHookedOnce(ref _chooserLogged, "配件选择列表钩子已生效（OpenPartsChange）");
        }

        private static bool _chooserLogged;
    }
}
