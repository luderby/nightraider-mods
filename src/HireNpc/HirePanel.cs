using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 雇佣面板。
    ///
    /// UI 构建要点（沿用已跑通的 RareShop 方案）：
    ///   * 挂到 GameManager.CanvasUI 下，嵌套 Canvas 用 overrideSorting + sortingOrder=32000
    ///   * **必须自带 GraphicRaycaster**：GraphicRegistry 按最近的 Canvas 注册图形，
    ///     父 Canvas 的 raycaster 看不到本面板的控件
    ///   * 复制游戏 Canvas 的 CanvasScaler，避免硬编码分辨率
    ///   * 中文字体必须从活着的 Text 上"借"（游戏字体 LanaPixel 只能运行时获取）
    ///   * 点击处理自己算（游戏不查 EventSystem.currentSelectedGameObject，手测最稳）
    /// </summary>
    internal static class HirePanel
    {
        private static GameObject _root;
        private static Font _font;
        private static bool _built;
        private static int _cursorDiagFrame;

        private static Text _gpText;
        private static Text _hiredText;

        private static readonly Color ColHead = new Color(0.78f, 0.83f, 0.92f, 1f);
        private static readonly Color ColHelp = new Color(0.72f, 0.76f, 0.82f, 1f);

        private static readonly List<ButtonEntry> _buttons = new List<ButtonEntry>();

        /// <summary>订单区：每笔订单的动态「结单」按钮（每次刷新时重建）。</summary>
        private static Transform _orderSlotParent;
        private static float _orderSlotX;
        private static float _orderSlotTopY;
        private static float _orderSlotStep;
        private static readonly List<GameObject> _orderSlots = new List<GameObject>();

        /// <summary>操作说明标签（位置随订单行数动态下移）。</summary>
        private static Text _helpLabel;
        private static float _orderBlockTopY;      // 订单区顶部（供 LayoutHelp 计算）

        private static int MaxOrderLines = 6;

        private const string HelpText =
            "操作说明\n"
            + "  · 点「点单」雇佣护航队员（上限 3 人，进入关卡时生成）。\n"
            + "  · 关卡内按 [X] 下达指令，再按 1 / 2 / 3 选择：\n"
            + "       [1] 跟随  [2] 驻守  [3] 闭麦\n"
            + "  · 每笔订单含 3 次成功撤离；[续单] 可 +3 次，[提前结单] 按比例退款。";

        /// <summary>
        /// 操作说明随订单内容**自动向下扩展**。
        ///
        /// 订单正文高度 = 实际行数 × 21，说明紧跟在正文下方 20px 处；
        /// 因此点单再多也不会与说明重叠（这是"自动向下扩展"的做法）。
        /// 同时订单正文最多显示 5 行，保证说明不会被推出窗口。
        /// </summary>
        private static void LayoutHelp()
        {
            if (_helpLabel == null)
            {
                return;
            }

            int lines = _orderLineCount;
            if (lines < 1)
            {
                lines = 1;
            }

            if (lines > MaxOrderLines)
            {
                lines = MaxOrderLines;
            }

            // 正文用 UpperLeft 锚点，从 _orderSlotTopY 之下开始
            float bodyBottom = _orderBodyTopY - lines * 21f;
            float helpCenter = bodyBottom - 20f - 63f;   // 63 = 126/2

            RectTransform rt = _helpLabel.transform as RectTransform;
            if (rt != null)
            {
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, helpCenter);
            }
        }

        private static int _orderLineCount = 1;

        /// <summary>订单正文的顶部 Y（UpperLeft 锚点）。</summary>
        private static float _orderBodyTopY;

        private sealed class ButtonEntry
        {
            internal RectTransform Rect;
            internal Image Image;
            internal Color Normal;
            internal Color Hover;
            internal Action OnClick;
        }

        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ColWindow = new Color(0.10f, 0.11f, 0.14f, 0.98f);
        private static readonly Color ColBtn = new Color(0.20f, 0.24f, 0.32f, 1f);
        private static readonly Color ColBtnHover = new Color(0.30f, 0.38f, 0.52f, 1f);
        private static readonly Color ColGood = new Color(0.55f, 0.90f, 0.60f, 1f);
        private static readonly Color ColBad = new Color(0.95f, 0.50f, 0.50f, 1f);
        private static readonly Color ColText = new Color(0.92f, 0.93f, 0.96f, 1f);
        private static readonly Color ColDim = new Color(0.70f, 0.73f, 0.78f, 1f);

        private const float WinW = 900f;
        private const float WinH = 820f;

        internal static bool IsOpen
        {
            get { return _root != null && _root.activeSelf; }
        }

        // ------------------------------------------------------------------
        // 开关
        // ------------------------------------------------------------------

        /// <summary>字体升级后，把新字体应用到本面板所有文字上。</summary>
        internal static void ApplyFont()
        {
            try
            {
                Font f = GameRefs.FindUiFont();
                if (f == null)
                {
                    return;
                }

                _font = f;
                if (_root == null)
                {
                    return;
                }

                Text[] texts = _root.GetComponentsInChildren<Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    texts[i].font = f;
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("HirePanel.ApplyFont 异常: " + ex.Message);
            }
        }

        internal static void EnsureBuilt()
        {
            if (!_built)
            {
                Build();
            }
        }

        internal static void Toggle()
        {
            try
            {
                EnsureBuilt();

                if (_root == null)
                {
                    return;
                }

                bool open = !_root.activeSelf;
                _root.SetActive(open);

                if (open)
                {
                    MountUnderGameCanvas();
                    Refresh();
                    AcquireCursor();
                }
                else
                {
                    Refresh();
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HirePanel.Toggle 异常: " + ex);
            }
        }

        private static void MountUnderGameCanvas()
        {
            try
            {
                GameManager gm = GameRefs.Game;
                if (gm == null || gm.CanvasUI == null || _root == null)
                {
                    return;
                }

                if (_root.transform.parent != gm.CanvasUI.transform)
                {
                    _root.transform.SetParent(gm.CanvasUI.transform, false);
                }

                _root.transform.SetAsLastSibling();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("MountUnderGameCanvas 异常: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 光标 / 输入焦点
        // （具体实现见文件后部的 AcquireCursor —— 与 RareShop 保持一致的做法）
        // ------------------------------------------------------------------

        /// <summary>在游戏内弹出一条浮动提示（借游戏自己的 JumpShow）。</summary>
        internal static void Notify(string text)
        {
            try
            {
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null || string.IsNullOrEmpty(text))
                {
                    return;
                }

                FXManager fx = UnityEngine.Object.FindObjectOfType<FXManager>();
                if (fx != null)
                {
                    fx.ShowJump(cur.nowChara, text);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("Notify 异常: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        private static void Build()
        {
            if (_built)
            {
                return;
            }
            _built = true;

            try
            {
                _font = GameRefs.FindUiFont();

                GameObject root = new GameObject("NightRaider_HireNpcCanvas");
                UnityEngine.Object.DontDestroyOnLoad(root);

                Canvas canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32000;

                // 必须自带：否则本面板的控件收不到点击
                root.AddComponent<GraphicRaycaster>();

                CanvasScaler scaler = root.AddComponent<CanvasScaler>();
                GameManager gm = GameRefs.Game;
                CanvasScaler src = (gm != null && gm.CanvasUI != null)
                    ? gm.CanvasUI.GetComponent<CanvasScaler>()
                    : null;
                if (src != null)
                {
                    scaler.uiScaleMode = src.uiScaleMode;
                    scaler.referenceResolution = src.referenceResolution;
                    scaler.screenMatchMode = src.screenMatchMode;
                    scaler.matchWidthOrHeight = src.matchWidthOrHeight;
                    scaler.referencePixelsPerUnit = src.referencePixelsPerUnit;
                }
                else
                {
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(1920f, 1080f);
                    scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                    scaler.matchWidthOrHeight = 0.5f;
                }

                _root = root;

                // 背板（同时拦截面板外的点击）
                GameObject backdrop = Panel("Backdrop", root.transform, ColBackdrop);
                Stretch((RectTransform)backdrop.transform);

                // 主窗
                GameObject win = Panel("Window", root.transform, ColWindow);
                RectTransform wr = (RectTransform)win.transform;
                wr.anchorMin = new Vector2(0.5f, 0.5f);
                wr.anchorMax = new Vector2(0.5f, 0.5f);
                wr.pivot = new Vector2(0.5f, 0.5f);
                wr.anchoredPosition = Vector2.zero;
                wr.sizeDelta = new Vector2(WinW, WinH);

                float y = WinH * 0.5f - 40f;

                // ── 标题 ────────────────────────────────────────────────────────
                Label(win.transform, "夜袭者护航俱乐部", 32, TextAnchor.MiddleCenter,
                      new Vector2(0f, y), new Vector2(WinW - 60f, 46f), new Color(1f, 0.90f, 0.55f, 1f));

                _gpText = Label(win.transform, "", 21, TextAnchor.MiddleCenter,
                                new Vector2(0f, y - 42f), new Vector2(WinW - 60f, 28f), ColDim);

                // ── 名册（真正的列布局）────────────────────────────────────────
                // 注意：中文字符是全角等宽，用 PadRight 拼字符串**必然错位**，
                // 因此表头与数据行必须使用相同的列坐标 + 左对齐。
                const float colRank = -350f;   // 护航等级
                const float colRole = -170f;   // 说明
                const float colCost = 150f;    // 花费（右边界约 280）
                const float colBtn = 355f;     // 点单按钮
                const float rowH = 44f;

                float headerY = y - 84f;
                LabelL(win.transform, "护航等级", colRank, headerY, 140f, ColHead, 34f);
                LabelL(win.transform, "说明", colRole, headerY, 300f, ColHead, 34f);
                LabelL(win.transform, "花费", colCost, headerY, 90f, ColHead, 34f);

                float rowY = headerY - 40f;

                for (int i = 0; i < HireData.Tiers.Length; i++)
                {
                    HireTier tier = HireData.Tiers[i];
                    float ry = rowY - i * rowH;

                    LabelL(win.transform, tier.Name, colRank, ry, 140f, 22, ColText, 34f);
                    LabelL(win.transform, tier.Role, colRole, ry, 300f, 20, ColDim, 34f);
                    LabelL(win.transform, CostText(tier), colCost, ry, 130f, 20,
                           new Color(1f, 0.85f, 0.45f, 1f), 34f);

                    int captured = i;
                    MakeButton(win.transform, "点单", new Vector2(colBtn, ry),
                               new Vector2(90f, 38f), delegate { OnHireClicked(captured); });
                }

                // ── 我的订单 ────────────────────────────────────────────────────
                float hy = rowY - HireData.Tiers.Length * rowH - 24f;
                _orderBlockTopY = hy;
                LabelL(win.transform, "我的订单", -350f, hy, 200f,
                       new Color(0.75f, 0.90f, 1f, 1f));

                // 订单区（全部左对齐锚点，panel 坐标 0 在中心，右边界 +450）
                //   订单文字：x -340 宽 520 → 右边界 180
                //   续单按钮：x 200  宽 64  → 200..264
                //   结单按钮：x 272  宽 104 → 272..376   （376 < 450，不会越界）
                _orderBodyTopY = hy - 22f;
                _hiredText = LabelL(win.transform, "", -340f, _orderBodyTopY - 63f,
                                    520f, 17, ColDim, 126f);
                _hiredText.alignment = TextAnchor.UpperLeft;

                // ── 操作说明（位置由 LayoutHelp 随订单行数**自动向下扩展**）──────
                _helpLabel = LabelL(win.transform, HelpText, -350f, 0f, WinW - 90f, 17, ColHelp, 126f);
                LayoutHelp();

                // ── 订单区右侧：每笔订单一个「结单」按钮 ────────────────────────
                // 与订单正文**逐行对齐**：正文首行中心 = _orderBodyTopY - 10.5，行距 21
                _orderSlotParent = win.transform;
                _orderSlotX = 200f;
                _orderSlotTopY = _orderBodyTopY - 10.5f;
                _orderSlotStep = 21f;

                // ── 底部按钮（"提前结单"在每笔订单行上；指令状态按钮已按要求移除）──
                float btnY = -WinH * 0.5f + 46f;
                MakeButton(win.transform, "关闭", new Vector2(0f, btnY),
                           new Vector2(130f, 40f), delegate { Toggle(); });

                _root.SetActive(false);
                HashSetButtonsOnBackdrop(backdrop);

                // 指令菜单挂在同一个 Canvas 下（平时隐藏，按 X 才显示）
                HireOrderControl.Build(root.transform, _font);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HirePanel.Build 异常: " + ex);
            }
        }

        private static void HashSetButtonsOnBackdrop(GameObject backdrop)
        {
            // 背板本身就吞掉外部点击（不需要回调）
            AddClickable((RectTransform)backdrop.transform, delegate { }, ColBackdrop, ColBackdrop);
        }

        /// <summary>花费文本：GP，或"GP + BP"（明星护这类双货币档位）。</summary>
        internal static string CostText(HireTier t)
        {
            if (t == null)
            {
                return "?";
            }

            string s = FormatMoney(t.Cost) + " GP";
            if (t.BpCost > 0)
            {
                s += " + " + t.BpCost + " BP";
            }

            return s;
        }

        /// <summary>把大数字格式化成"20万"这种便于阅读的形式。</summary>
        private static string FormatMoney(int v)
        {
            if (v >= 100000000)
            {
                return (v / 100000000f).ToString("0.#") + "亿";
            }

            if (v >= 10000)
            {
                return (v / 10000f).ToString("0.#") + "万";
            }

            return v.ToString();
        }

        /// <summary>
        /// 左对齐的独立列标签。
        /// 中文字符是全角等宽，用空格/PadRight 拼接**无法对齐**，
        /// 必须让表头与数据行使用同一列坐标 + 左对齐锚点。
        /// </summary>
        private static Text LabelL(Transform parent, string text, float x, float y,
                                   float width, Color color)
        {
            return LabelL(parent, text, x, y, width, 18, color, 30f);
        }

        private static Text LabelL(Transform parent, string text, float x, float y,
                                   float width, int size, Color color)
        {
            return LabelL(parent, text, x, y, width, size, color, 30f);
        }

        private static Text LabelL(Transform parent, string text, float x, float y,
                                   float width, Color color, float height)
        {
            return LabelL(parent, text, x, y, width, 18, color, height);
        }

        private static Text LabelL(Transform parent, string text, float x, float y,
                                   float width, int size, Color color, float height)
        {
            GameObject go = new GameObject("Col");
            go.transform.SetParent(parent, false);
            RectTransform r = go.AddComponent<RectTransform>();
            r.localScale = Vector3.one;
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0f, 0.5f);        // 左对齐锚点
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(width, height);

            Text t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            return t;
        }

        private static GameObject Panel(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform r = go.AddComponent<RectTransform>();
            r.localScale = Vector3.one;
            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            return go;
        }

        private static Text Label(Transform parent, string text, int size, TextAnchor anchor,
                                  Vector2 pos, Vector2 sizeDelta, Color color)
        {
            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            RectTransform r = go.AddComponent<RectTransform>();
            r.localScale = Vector3.one;
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = sizeDelta;

            Text t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.text = text;
            t.alignment = anchor;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            return t;
        }

        private static void MakeButton(Transform parent, string caption, Vector2 pos, Vector2 size, Action onClick)
        {
            GameObject go = Panel("Btn_" + caption, parent, ColBtn);
            RectTransform r = (RectTransform)go.transform;
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;

            Label(go.transform, caption, 20, TextAnchor.MiddleCenter, Vector2.zero, size, ColText);
            AddClickable(r, onClick, ColBtn, ColBtnHover);
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        private static void AddClickable(RectTransform rect, Action onClick, Color normal, Color hover)
        {
            ButtonEntry e = new ButtonEntry
            {
                Rect = rect,
                Image = rect.GetComponent<Image>(),
                Normal = normal,
                Hover = hover,
                OnClick = onClick
            };
            _buttons.Add(e);
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        private static void OnHireClicked(int tierIndex)
        {
            try
            {
                HireTier tier = HireData.Tiers[tierIndex];
                string msg;
                if (AllySpawner.TryHire(tier, out msg))
                {
                    Status(msg, ColGood);
                }
                else
                {
                    Status(msg, ColBad);
                }
                Refresh();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnHireClicked 异常: " + ex);
            }
        }

        private static void OnFireClicked()
        {
            try
            {
                string msg;
                int last = HireNpcPlugin.Hired.Count - 1;
                if (AllySpawner.TryFire(last, out msg))
                {
                    Status(msg, ColGood);
                }
                else
                {
                    Status(msg, ColBad);
                }
                Refresh();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnFireClicked 异常: " + ex);
            }
        }

        private static void OnCycleModeClicked()
        {
            try
            {
                int n = AllyRegistry.Count;
                if (n == 0)
                {
                    Status("当前没有在场友军（需进入关卡后才会生成）。", ColBad);
                    return;
                }

                AllyMode cur = AllyRegistry.At(0).Mode;
                AllyMode next = (AllyMode)(((int)cur + 1) % 3);
                for (int i = 0; i < n; i++)
                {
                    AllyRegistry.At(i).Mode = next;
                }

                Status("指令已切换为：" + HireNpcPlugin.ModeName(next), ColGood);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnCycleModeClicked 异常: " + ex);
            }
        }

        private static void Status(string msg, Color color)
        {
            // 面板上不再保留状态行（用户要求去掉），
            // 改为把结果打到日志 + 游戏内浮动提示，避免遮挡布局。
            if (!string.IsNullOrEmpty(msg))
            {
                HireNpcPlugin.Log.LogInfo(msg);
                HirePanel.Notify(msg);
            }
        }

        private static void Refresh()
        {
            try
            {
                // 每次刷新都从游戏数据重新取一次 HP/ATK。
                // 不再依赖启动时的一次性自检（那时存档可能还没载入，会显示 0）。
                HireData.RefreshStatsFromGame();

                if (_gpText != null)
                {
                    int gp = 0;
                    try
                    {
                        DataHub d = GameRefs.Data;
                        if (d != null && GameRefs.IsSaveLoaded())
                        {
                            gp = d.CheckMyGP();
                        }
                    }
                    catch
                    {
                        // 忽略
                    }

                    _gpText.text = "当前 GP: " + gp
                                   + "    上限: " + HireTuning.MaxHired + " 人"
                                   + "    [面板键: " + HireNpcPlugin.CfgPanelKey.Value + "]";
                }

                if (_hiredText != null)
                {
                    List<HireOrder> orders = HireNpcPlugin.Hired;
                    if (orders.Count == 0)
                    {
                        _hiredText.text = "（暂无订单）点「点单」即可雇佣护航队员。";
                        _orderLineCount = 1;
                    }
                    else
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.Append("当前 ").Append(orders.Count).Append(" 笔订单（每笔 3 次撤离合约）");

                        int shown = 0;
                        for (int i = 0; i < orders.Count && shown < MaxOrderLines; i++, shown++)
                        {
                            HireOrder o = orders[i];
                            HireTier t = HireData.Find(o.TierKey);
                            string nm = t != null ? t.Name : o.TierKey;

                            sb.Append("\n  ").Append(nm)
                              .Append("     剩余 ").Append(o.Remaining).Append("/").Append(o.Total).Append(" 次");
                        }

                        if (orders.Count > MaxOrderLines)
                        {
                            sb.Append("\n  …等共 ").Append(orders.Count).Append(" 笔");
                            shown++;
                        }

                        if (AllyRegistry.AliveCount > 0)
                        {
                            sb.Append("\n在场队员：").Append(AllyRegistry.AliveCount).Append(" 人");
                            shown++;
                        }

                        _hiredText.text = sb.ToString();
                        _orderLineCount = shown;
                    }

                    // 订单行数变了 → 重新摆放操作说明，保证永不重叠
                    LayoutHelp();
                }

                RebuildOrderSlots();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("Refresh 异常: " + ex);
            }
        }

        /// <summary>
        /// 为每一笔订单生成一个「结单」按钮，实现"选择性结单"。
        /// 每次刷新时销毁重建，保证与订单列表同步。
        /// </summary>
        private static void RebuildOrderSlots()
        {
            try
            {
                // 先清掉旧的
                for (int i = 0; i < _orderSlots.Count; i++)
                {
                    if (_orderSlots[i] != null)
                    {
                        // 同时移除其点击项，避免残留可点区域
                        RectTransform rt = _orderSlots[i].transform as RectTransform;
                        for (int k = _buttons.Count - 1; k >= 0; k--)
                        {
                            if (_buttons[k].Rect == rt)
                            {
                                _buttons.RemoveAt(k);
                            }
                        }

                        UnityEngine.Object.Destroy(_orderSlots[i]);
                    }
                }
                _orderSlots.Clear();

                if (_orderSlotParent == null)
                {
                    return;
                }

                List<HireOrder> orders = HireNpcPlugin.Hired;
                for (int idx = 0; idx < orders.Count; idx++)
                {
                    HireOrder order = orders[idx];
                    HireTier t = HireData.Find(order.TierKey);
                    int captured = idx;

                    float y = _orderSlotTopY - idx * _orderSlotStep;

                    // 每行两个按钮：续单 + 提前结单（用小字号以适应按钮尺寸）
                    GameObject renew = Panel("OrderRenew" + idx, _orderSlotParent,
                                             new Color(0.24f, 0.34f, 0.26f, 1f));
                    RectTransform rr = (RectTransform)renew.transform;
                    rr.anchorMin = new Vector2(0.5f, 0.5f);
                    rr.anchorMax = new Vector2(0.5f, 0.5f);
                    rr.pivot = new Vector2(0f, 0.5f);
                    rr.anchoredPosition = new Vector2(_orderSlotX, y);
                    rr.sizeDelta = new Vector2(64f, 20f);

                    Label(renew.transform, "续单", 14, TextAnchor.MiddleCenter,
                          Vector2.zero, new Vector2(64f, 20f), new Color(0.82f, 1f, 0.86f, 1f));

                    AddClickable(rr, delegate { OnRenewOne(captured); },
                                 new Color(0.24f, 0.34f, 0.26f, 1f),
                                 new Color(0.34f, 0.50f, 0.38f, 1f));

                    _orderSlots.Add(renew);

                    // 提前结单
                    GameObject go = Panel("OrderSlot" + idx, _orderSlotParent,
                                          new Color(0.36f, 0.24f, 0.24f, 1f));
                    RectTransform r = (RectTransform)go.transform;
                    r.anchorMin = new Vector2(0.5f, 0.5f);
                    r.anchorMax = new Vector2(0.5f, 0.5f);
                    r.pivot = new Vector2(0f, 0.5f);
                    r.anchoredPosition = new Vector2(_orderSlotX + 72f, y);
                    r.sizeDelta = new Vector2(104f, 20f);

                    Label(go.transform, "提前结单", 14, TextAnchor.MiddleCenter,
                          Vector2.zero, new Vector2(104f, 20f), new Color(1f, 0.85f, 0.85f, 1f));

                    AddClickable(r, delegate { OnFireOne(captured); },
                                 new Color(0.36f, 0.24f, 0.24f, 1f),
                                 new Color(0.52f, 0.32f, 0.32f, 1f));

                    _orderSlots.Add(go);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("RebuildOrderSlots 异常: " + ex.Message);
            }
        }

        /// <summary>续单：为这笔订单续 1 单（+3 次，原价×20%）。</summary>
        private static void OnRenewOne(int index)
        {
            try
            {
                string msg;
                if (AllySpawner.TryRenew(index, 1, out msg))
                {
                    Status(msg, ColGood);
                }
                else
                {
                    Status(msg, ColBad);
                }

                Refresh();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnRenewOne 异常: " + ex);
            }
        }

        /// <summary>结掉指定的一笔订单（可选择性结单，并按等级退款）。</summary>
        private static void OnFireOne(int index)
        {
            try
            {
                string msg;
                if (AllySpawner.TryFire(index, out msg))
                {
                    Status(msg, ColGood);
                }
                else
                {
                    Status(msg, ColBad);
                }

                // 结单后场上若还有该等级的队员，一并撤离，避免"订单没了人还在"
                AllySpawner.SyncFieldWithOrders();

                Refresh();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnFireOne 异常: " + ex);
            }
        }

        private static string AliveModeText()
        {
            try
            {
                if (AllyRegistry.Count == 0)
                {
                    return null;
                }
                return HireNpcPlugin.ModeName(AllyRegistry.At(0).Mode);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>由常驻驱动器每帧调用。</summary>
        internal static void Tick()
        {
            try
            {
                if (_root == null || !_root.activeSelf)
                {
                    return;
                }

                // 与 RareShop 一致：只保证 Cursor.visible = true，绝不碰 SetCursor。
                if (!Cursor.visible)
                {
                    Cursor.visible = true;
                }

                CursorDiag();
                HandleClick();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HirePanel.Tick 异常: " + ex);
            }
        }

        /// <summary>
        /// 光标处理 —— 与已跑通的 RareShop(F8 面板) 保持一致。
        ///
        /// RareShop 只做了一件事：打开面板时 Cursor.visible = true（ShopPanel.cs:136），
        /// **从不调用 Cursor.SetCursor**。而本 MOD 之前额外调用了
        /// Cursor.SetCursor(GameManager.HandleIcon, ...)，这正是问题所在：
        /// Unity 对光标纹理有严格要求（可读性/格式/热区），纹理不合格时 SetCursor 会
        /// 静默失败，结果连系统光标也不显示了 —— 表现为"面板能点、但看不到指针，
        /// 必须按 ESC 才能看到鼠标"。现在改为完全照搬 RareShop 的做法。
        /// </summary>
        private static void AcquireCursor()
        {
            try
            {
                Cursor.visible = true;

                if (EventSystem.current == null)
                {
                    HireNpcPlugin.Log.LogWarning("场景中没有 EventSystem，面板点击可能无效。");
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("AcquireCursor 异常: " + ex.Message);
            }
        }

        /// <summary>每秒一次的光标诊断，便于确认光标状态。</summary>
        private static void CursorDiag()
        {
            try
            {
                if (Time.frameCount - _cursorDiagFrame < 60)
                {
                    return;
                }

                _cursorDiagFrame = Time.frameCount;
                GameManager gm = GameRefs.Game;
                HireNpcPlugin.Log.LogInfo("[光标] Cursor.visible=" + Cursor.visible
                    + " lockState=" + Cursor.lockState
                    + " HandleIcon=" + (gm != null && gm.HandleIcon != null
                        ? (gm.HandleIcon.width + "x" + gm.HandleIcon.height
                           + " fmt=" + gm.HandleIcon.format
                           + " readable=" + gm.HandleIcon.isReadable)
                        : "null")
                    + " 屏幕模式=" + Screen.fullScreenMode);
            }
            catch
            {
                // 诊断失败无所谓
            }
        }

        private static void HandleClick()
        {
            Vector2 mouse = Input.mousePosition;
            ButtonEntry hit = null;

            EventSystem es = EventSystem.current;
            if (es != null)
            {
                PointerEventData ped = new PointerEventData(es) { position = mouse };
                List<RaycastResult> results = new List<RaycastResult>();
                es.RaycastAll(ped, results);
                for (int i = 0; i < results.Count; i++)
                {
                    ButtonEntry e = FindByRect(results[i].gameObject.transform as RectTransform);
                    if (e != null)
                    {
                        hit = e;
                        break;
                    }
                }
            }

            if (hit == null)
            {
                hit = FindByPoint(mouse);
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                ButtonEntry e = _buttons[i];
                if (e.Image != null)
                {
                    e.Image.color = (e == hit) ? e.Hover : e.Normal;
                }
            }

            if (Input.GetMouseButtonDown(0) && hit != null && hit.OnClick != null)
            {
                hit.OnClick();
            }
        }

        private static ButtonEntry FindByRect(RectTransform r)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].Rect == r)
                {
                    return _buttons[i];
                }
            }
            return null;
        }

        private static ButtonEntry FindByPoint(Vector2 screenPoint)
        {
            ButtonEntry best = null;
            int bestDepth = -1;
            for (int i = 0; i < _buttons.Count; i++)
            {
                ButtonEntry e = _buttons[i];
                if (e.Rect == null)
                {
                    continue;
                }

                Canvas canvas = e.Rect.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    ? canvas.worldCamera
                    : null;

                if (RectTransformUtility.RectangleContainsScreenPoint(e.Rect, screenPoint, cam))
                {
                    int depth = Depth(e.Rect);
                    if (best == null || depth > bestDepth)
                    {
                        best = e;
                        bestDepth = depth;
                    }
                }
            }
            return best;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t != null)
            {
                d++;
                t = t.parent;
            }
            return d;
        }
    }
}
