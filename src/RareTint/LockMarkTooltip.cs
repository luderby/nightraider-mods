using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareTint
{
    /// <summary>
    /// Reports the "locked" and "marked" states as text in the hover tooltip, replacing the
    /// coloured cell that used to carry them.
    ///
    /// Why this exists: the game paints a locked item's cell solid red
    /// (MyBagControl.RefreshTheGrid case 7) and a marked item's cell gold (case 4), which
    /// collides with this mod's rarity colouring and hides the rarity. The player still needs
    /// to be able to tell the two states apart, so instead of the cell colour the state is
    /// appended to the tooltip's description block.
    ///
    /// How the state is stored: CurrentDataHub.lockList / markList hold *model ids*, not slots
    /// (see MyBagControl.cs:8496/8500 and CurrentDataHub.LockSomeThing). So the state is a
    /// property of the item type — locking an M4 shows 【已锁定】 on every M4, which is exactly
    /// what the old red colour did too.
    ///
    /// Where it is written: EXMessageControl.BeOpened(int idd, CurrentItemData dt) receives the
    /// model id as its first argument and fills in proDes. A postfix appends the labels after
    /// the game has finished, so nothing the game wrote is disturbed. proDes is never given a
    /// colour by the game (only its .text is assigned), so appending cannot clash with the
    /// existing tooltip styling.
    /// </summary>
    internal static class LockMarkTooltip
    {
        private static FieldInfo _desField;
        private static bool _reflectionFailed;
        private static bool _errorLogged;

        internal static void ResetReflectionCache()
        {
            _desField = null;
            _reflectionFailed = false;
        }

        internal static void Append(EXMessageControl control, int modelId, CurrentItemData dt)
        {
            if (control == null || _reflectionFailed)
            {
                return;
            }
            if (!RareTintPlugin.IsLockMarkTextOn())
            {
                return;
            }
            if (modelId <= 1)
            {
                return;
            }

            CurrentDataHub cur = GameRefs.Current;
            if (cur == null)
            {
                return;
            }

            bool locked = false;
            bool marked = false;
            try
            {
                locked = cur.lockList != null && cur.lockList.Contains(modelId);
                marked = cur.markList != null && cur.markList.Contains(modelId);
            }
            catch
            {
                return;
            }

            // Durability is a property of the individual instance, not of the model, so it has
            // to come from the CurrentItemData the game passed in (EXMessageShow.myData). An
            // item counts as worn out only when it actually has durability to lose, which is
            // the same rule the game uses to paint the slot red (MyBagControl.cs:9383).
            bool broken = false;
            try
            {
                if (dt != null && dt.modelID > 1)
                {
                    ItemData def = dt.GetData();
                    if (def != null && def.maxDurability > 0 && dt.nowDurability <= 0)
                    {
                        broken = true;
                    }
                }
            }
            catch
            {
                broken = false;
            }

            if (!locked && !marked && !broken)
            {
                return;     // nothing to say; leave the tooltip exactly as the game made it
            }

            if (_desField == null)
            {
                _desField = AccessTools.Field(typeof(EXMessageControl), "proDes");
                if (_desField == null)
                {
                    _reflectionFailed = true;
                    RareTintPlugin.Log.LogWarning(
                        "找不到 EXMessageControl.proDes，锁定/标记/损坏文字已停用。");
                    return;
                }
            }

            Text des = _desField.GetValue(control) as Text;
            if (des == null)
            {
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (des.text != null)
            {
                sb.Append(des.text);
            }

            // Lock / mark share one label colour; a worn-out item gets its own, so it reads as
            // the warning it is rather than as another player-applied state.
            string stateColour = RareTintPlugin.LockMarkColourHex();
            sb.Append("\n<color=#").Append(stateColour).Append('>');
            bool first = true;
            if (locked)
            {
                sb.Append("【已锁定】");
                first = false;
            }
            if (marked)
            {
                if (!first)
                {
                    sb.Append(' ');
                }
                sb.Append("【已标记】");
                first = false;
            }
            sb.Append("</color>");
            if (broken)
            {
                sb.Append("<color=#").Append(RareTintPlugin.BrokenColourHex()).Append(">【已损坏】</color>");
            }

            string want = sb.ToString();
            if (des.text != want)
            {
                des.text = want;
            }
        }

        internal static void LogError(Exception ex)
        {
            if (_errorLogged)
            {
                return;
            }
            _errorLogged = true;
            RareTintPlugin.Log.LogError("锁定/标记/损坏文字失败（只报一次）：" + ex);
        }
    }

    /// <summary>
    /// Postfix on EXMessageControl.BeOpened(int idd, CurrentItemData dt). The first parameter
    /// is the item's model id, which is what lockList / markList are keyed by; the second is
    /// the hovered instance, which is where durability lives.
    /// </summary>
    [HarmonyPatch(typeof(EXMessageControl), "BeOpened")]
    internal static class LockMarkTooltipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EXMessageControl __instance, int idd, CurrentItemData dt)
        {
            try
            {
                LockMarkTooltip.Append(__instance, idd, dt);
            }
            catch (Exception ex)
            {
                LockMarkTooltip.LogError(ex);
            }
        }
    }
}
