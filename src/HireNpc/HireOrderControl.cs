using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 指令菜单 —— 交互方式照搬游戏自己的宠物指令（OrderControl.cs）：
    ///     按 X 打开指令菜单 → 按 1 / 2 / 3 选择 → Esc 关闭
    ///
    /// 与宠物的 Z→1..6 完全同构，符合用户"跟宠物的 z-1、z-2 有异曲同工之处"的要求。
    ///
    /// 三个指令：
    ///     1 = 跟随      （默认，跟随玩家 3-5m，有敌人自动交战）
    ///     2 = 原地待命  （停在原地不动，有敌人就地开火）
    ///     3 = 主动突击  （主动接近最近敌人）
    /// </summary>
    internal static class HireOrderControl
    {
        private static GameObject _root;
        private static Text _text;
        private static bool _open;
        private static float _openedAt;

        /// <summary>Esc 或选完指令后的短暂延迟关闭，避免同一帧既开又关。</summary>
        private const float MinOpenSeconds = 0.15f;

        internal static bool IsOpen { get { return _open; } }

        /// <summary>
        /// 刚选完指令的短暂窗口。
        ///
        /// 菜单会在选完的同一帧关闭，而游戏的快捷栏读取可能排在我们的选择处理之后；
        /// 只看 IsOpen 会在那一帧漏掉屏蔽，导致"按 1/2/3 仍切换快捷栏"。
        /// 这里额外维持约 0.25 秒的屏蔽窗口。
        /// </summary>
        private static float _selectedAt = -99f;

        internal static bool RecentlySelected
        {
            get { return Time.realtimeSinceStartup - _selectedAt < 0.25f; }
        }

        internal static void Build(Transform parent, Font font)
        {
            try
            {
                if (_root != null)
                {
                    return;
                }

                if (parent == null)
                {
                    HireNpcPlugin.Log.LogWarning("指令菜单构建失败：父节点为 null。");
                    return;
                }

                _root = new GameObject("NightRaider_HireOrderMenu");
                _root.transform.SetParent(parent, false);

                RectTransform r = _root.AddComponent<RectTransform>();
                r.anchorMin = new Vector2(0.5f, 0f);
                r.anchorMax = new Vector2(0.5f, 0f);
                r.pivot = new Vector2(0.5f, 0f);
                r.anchoredPosition = new Vector2(0f, 180f);
                r.sizeDelta = new Vector2(700f, 170f);
                r.localScale = Vector3.one;

                // 背板，和宠物指令一样是纯文字，但加一层半透明底更易读
                GameObject bg = new GameObject("Bg");
                bg.transform.SetParent(_root.transform, false);
                RectTransform br = bg.AddComponent<RectTransform>();
                br.anchorMin = Vector2.zero;
                br.anchorMax = Vector2.one;
                br.offsetMin = Vector2.zero;
                br.offsetMax = Vector2.zero;
                UnityEngine.UI.Image img = bg.AddComponent<UnityEngine.UI.Image>();
                img.color = new Color(0f, 0f, 0f, 0.72f);
                img.raycastTarget = false;

                _text = _root.AddComponent<Text>();
                _text.font = font;
                _text.fontSize = 22;
                _text.alignment = TextAnchor.MiddleCenter;
                _text.color = new Color(0.97f, 0.97f, 1f, 1f);
                _text.horizontalOverflow = HorizontalWrapMode.Wrap;
                _text.verticalOverflow = VerticalWrapMode.Overflow;
                _text.supportRichText = true;
                _text.raycastTarget = false;

                _text.text = BuildMenuText();
                _root.SetActive(false);

                HireNpcPlugin.Log.LogInfo("指令菜单已构建 [字体="
                    + (font != null ? font.name : "null") + "]");
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireOrderControl.Build 异常: " + ex);
            }
        }

        /// <summary>字体升级后应用新字体。</summary>
        internal static void ApplyFont()
        {
            try
            {
                if (_text == null)
                {
                    return;
                }

                Font f = GameRefs.FindUiFont();
                if (f != null)
                {
                    _text.font = f;
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("HireOrderControl.ApplyFont 异常: " + ex.Message);
            }
        }

        private static string BuildMenuText()
        {
            return MenuText;
        }

        internal static void Toggle()
        {
            HireNpcPlugin.Log.LogInfo("[指令菜单] Toggle 被调用，当前 _open=" + _open
                + " _root=" + (_root != null ? "已构建" : "null")
                + " 在场友军=" + AllyRegistry.Count);

            if (_open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        internal static void Open()
        {
            try
            {
                if (_root == null)
                {
                    HireNpcPlugin.Log.LogWarning("指令菜单不可用（未构建）。");
                    return;
                }

                // 没有在场友军时给出提示而不是开菜单
                if (AllyRegistry.Count == 0)
                {
                    HirePanel.Notify("当前没有在场友军");
                    HireNpcPlugin.Log.LogInfo("按 X：没有在场友军，未打开指令菜单。");
                    return;
                }

                _text.text = BuildMenuText();
                _root.SetActive(true);

                // 保证显示在最上层（与雇佣面板同一个 Canvas，调整兄弟顺序即可）
                _root.transform.SetAsLastSibling();

                _open = true;
                _openedAt = Time.realtimeSinceStartup;

                // 决定性诊断：把菜单实际所在的屏幕坐标、尺寸、文字前 40 字打出来，
                // 这样"打开了但看不见"与"根本没打开"能立刻区分。
                string diag = "?";
                try
                {
                    Vector3 wp = _root.transform.position;
                    RectTransform rt = _root.GetComponent<RectTransform>();
                    Vector2 size = rt != null ? rt.sizeDelta : Vector2.zero;
                    Vector2 lp = rt != null ? rt.localPosition : Vector2.zero;
                    diag = " 世界坐标=" + wp + " localPos=" + lp + " 尺寸=" + size
                         + " 父=" + (_root.transform.parent != null ? _root.transform.parent.name : "null")
                         + " 同层子物体数=" + (_root.transform.parent != null
                               ? _root.transform.parent.childCount : -1);
                }
                catch
                {
                    // 诊断失败无所谓
                }

                HireNpcPlugin.Log.LogInfo("指令菜单已打开（在场友军 " + AllyRegistry.Count
                    + " 名，activeSelf=" + _root.activeSelf + "）。" + diag);
                HireNpcPlugin.Log.LogInfo("菜单文字=" + BuildMenuText().Replace("\n", " / "));
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireOrderControl.Open 异常: " + ex);
            }
        }

        internal static void Close()
        {
            try
            {
                if (_root != null)
                {
                    _root.SetActive(false);
                }
            }
            finally
            {
                _open = false;
            }
        }

        /// <summary>
        /// IMGUI 渲染指令菜单。
        ///
        /// 为什么不用 Canvas/uGUI：本 MOD 的 Canvas 面板（雇佣面板）能正常显示，
        /// 但同样方式做的指令菜单始终不可见，多轮排查未定位到原因。
        /// IMGUI（OnGUI）不依赖 Canvas 层级、GraphicRaycaster、CanvasScaler 或 GraphicRegistry，
        /// 由 Unity 直接在屏幕空间绘制，**必定可见**，因此改用这个方案。
        ///
        /// OnGUI 只在菜单打开的少数帧执行，常态下没有任何开销。
        /// </summary>
        internal static void OnGUI()
        {
            if (!_open)
            {
                return;
            }

            try
            {
                EnsureGuiStyle();

                if (_style == null)
                {
                    return;
                }

                const float w = 520f;
                const float h = 132f;
                float x = (Screen.width - w) * 0.5f;
                // 用户要求更靠屏幕底部
                float y = Screen.height - h - 44f;

                // 背景框
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.80f);
                GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
                GUI.color = old;

                // 文字
                GUI.Label(new Rect(x + 12f, y + 8f, w - 24f, h - 16f), MenuText, _style);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireOrderControl.OnGUI 异常: " + ex);
            }
        }

        private static GUIStyle _style;

        private static void EnsureGuiStyle()
        {
            if (_style != null)
            {
                return;
            }

            _style = new GUIStyle(GUI.skin != null ? GUI.skin.label : null);
            _style.fontSize = 20;
            _style.alignment = TextAnchor.UpperLeft;
            _style.wordWrap = true;
            _style.normal.textColor = new Color(0.97f, 0.97f, 1f, 1f);

            Font f = GameRefs.FindUiFont();
            if (f != null)
            {
                _style.font = f;
                HireNpcPlugin.Log.LogInfo("[指令菜单] IMGUI 字体=" + f.name);
            }
        }

        /// <summary>X-4：闭麦 / 开麦。</summary>
        private static void ToggleMute()
        {
            HireTuning.AllyMuted = !HireTuning.AllyMuted;

            string msg = HireTuning.AllyMuted ? "护航语音：已闭麦" : "护航语音：已开麦";
            HireNpcPlugin.Log.LogInfo(msg);
            HirePanel.Notify(msg);

            Close();
        }

        private const string MenuText =
            "<color=#FFE08A>夜袭者护航俱乐部 · 指令</color>\n"
            + "[1] 跟随      在你附近活动，遇敌自动交战\n"
            + "[2] 驻守      停在原地，遇敌就地开火\n"
            + "[3] 闭麦      开关护航语音台词";

        /// <summary>每帧调用：处理 X 开合与 1/2/3 选择。</summary>
        internal static void Tick()
        {
            try
            {
                if (GameRefs.Game != null && GameRefs.Game.CheckMenu())
                {
                    return;
                }

                // X 的开合由 HireNpcPlugin 统一处理（需与无人机共用判定），这里只处理选择
                if (!_open)
                {
                    return;
                }

                // 刚打开的极短时间内不响应，避免同一次按键既开又选
                if (Time.realtimeSinceStartup - _openedAt < MinOpenSeconds)
                {
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Close();
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Alpha1))
                {
                    Apply(AllyMode.Follow, "跟随");
                }
                else if (Input.GetKeyDown(KeyCode.Alpha2))
                {
                    Apply(AllyMode.Hold, "驻守");
                }
                else if (Input.GetKeyDown(KeyCode.Alpha3))
                {
                    // X-3：闭麦 / 开麦（关闭护航台词）
                    ToggleMute();
                }

                // 菜单持续打开时，每 60 帧确认一次它确实还活着、还在可见位置
                if (Time.frameCount % 60 == 0 && _root != null)
                {
                    HireNpcPlugin.Log.LogInfo("[指令菜单] 存活确认: activeSelf=" + _root.activeSelf
                        + " 世界坐标=" + _root.transform.position
                        + " 文字长度=" + (_text != null && _text.text != null ? _text.text.Length : -1)
                        + " 字体=" + (_text != null && _text.font != null ? _text.font.name : "null")
                        + " 颜色=" + (_text != null ? _text.color.ToString() : "?"));
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireOrderControl.Tick 异常: " + ex);
            }
        }

        private static void Apply(AllyMode mode, string label)
        {
            try
            {
                _selectedAt = Time.realtimeSinceStartup;   // 开启快捷栏屏蔽窗口

                int n = AllyRegistry.Count;
                for (int i = 0; i < n; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null)
                    {
                        continue;
                    }

                    r.Mode = mode;
                }

                // 回读验证：确认每个友军的标记组件上真的写进去了。
                // 用户报告"有时对其中一个友军不生效"，这里能直接看出是哪个没写进去。
                for (int i = 0; i < n; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || r.Go == null)
                    {
                        HireNpcPlugin.Log.LogWarning("  → 友军[" + i + "] 对象为空，模式未设置");
                        continue;
                    }

                    AllyMarker mk = r.Marker;
                    HireNpcPlugin.Log.LogInfo("  → 友军[" + i + "] " + r.Go.name
                        + " 标记=" + (mk != null ? "有" : "无")
                        + " 标记模式=" + (mk != null ? HireNpcPlugin.ModeName(mk.Mode) : "?")
                        + " 记录模式=" + HireNpcPlugin.ModeName(r.Mode)
                        + " 存活=" + r.IsAlive);
                }

                HireNpcPlugin.Log.LogInfo("指令已下发：" + label + "（" + n + " 名友军）");
                HirePanel.Notify("友军指令：" + label);
                Close();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireOrderControl.Apply 异常: " + ex);
            }
        }
    }
}
