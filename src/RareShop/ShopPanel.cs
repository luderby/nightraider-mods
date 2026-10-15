using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Runtime-built shop window. Everything (canvas, panel, rows, input box) is
    /// created in code so the mod does not depend on any scene prefab.
    ///
    /// It deliberately does not rely on Unity's EventSystem for clicking, because
    /// the game drives most of its own UI through manual raycasts. Clicks are
    /// resolved by raycasting and text entry is captured from Input.inputString.
    /// </summary>
    internal sealed class ShopPanel : MonoBehaviour
    {
        // Layout constants
        private const float WinW = 1280f;
        private const float WinH = 700f;
        private const float RowH = 46f;
        private const float RowGap = 4f;
        private const int VisibleRows = 9;

        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color ColWindow = new Color(0.07f, 0.09f, 0.11f, 0.98f);
        private static readonly Color ColRow = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ColRowAlt = new Color(1f, 1f, 1f, 0.025f);
        private static readonly Color ColRowSel = new Color(0.20f, 0.55f, 0.85f, 0.35f);
        private static readonly Color ColButton = new Color(0.17f, 0.20f, 0.24f, 1f);
        private static readonly Color ColButtonHot = new Color(0.24f, 0.31f, 0.40f, 1f);
        private static readonly Color ColAccent = new Color(0.35f, 0.75f, 1f, 1f);
        private static readonly Color ColPrice = new Color(1f, 0.85f, 0.35f, 1f);
        private static readonly Color ColOk = new Color(0.45f, 0.9f, 0.5f, 1f);
        private static readonly Color ColBad = new Color(1f, 0.42f, 0.38f, 1f);

        private static readonly string[] TabNames = { "武器", "武器配件", "装备", "药剂", "食物", "材料", "全部" };
        private const int AllTabIndex = 6;

        private GameObject _root;
        private Text _titleText;
        private Text _gpText;
        private Text _statusText;
        private Text _multiplierText;
        private Text _pageText;
        private Text _detailText;
        private Image _multiplierBox;
        private Text _searchText;
        private Image _searchBox;
        private InputField _searchField;
        private InputField _multiplierField;
        private GameObject _lastSelected;
        private readonly List<InputField> _fields = new List<InputField>();
        private RectTransform _listContent;
        private Font _font;

        private readonly List<GameObject> _tabButtons = new List<GameObject>();
        private readonly List<Text> _tabLabels = new List<Text>();
        private readonly List<RowWidgets> _rows = new List<RowWidgets>();

        private readonly List<ShopEntry> _allItems = new List<ShopEntry>();
        private readonly List<ShopEntry> _filtered = new List<ShopEntry>();
        private readonly Dictionary<int, ButtonWidget> _widgets = new Dictionary<int, ButtonWidget>();

        private sealed class RowWidgets
        {
            internal GameObject Root;
            internal Image Bg;
            internal Image Icon;
            internal Text Name;
            internal Text Price;
            internal Text Stock;
            internal Text Amount;
            internal int EntryIndex = -1;
        }

        private sealed class ButtonWidget
        {
            internal RectTransform Rect;
            internal Image Image;
            internal Action OnClick;
            internal Color Normal;
            internal Color Hover;
        }

        private int _nextWidgetId;

        // State
        private bool _built;
        private ShopCategory _category = ShopCategory.Weapon;
        private bool _isAllTab;
        private float _multiplier = 1f;
        private string _multiplierRaw = "1";
        private string _search = string.Empty;
        private int _searchMatchedCategory = -1;
        private int _scroll;
        private int _selected = -1;
        private string _status = string.Empty;
        private float _statusUntil;

        internal bool IsOpen { get { return _root != null && _root.activeSelf; } }

        internal static ShopPanel Create()
        {
            GameObject go = new GameObject("NightRaider_RareShopPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<ShopPanel>();
        }

        internal void Toggle()
        {
            // Re-parent under the game's UI canvas the first time it is actually
            // available; the manager poll runs continuously so this converges.
            if (_built && _root != null)
            {
                TryMountUnderGameCanvas();
            }

            if (!_built)
            {
                Build();
            }
            if (_root == null)
            {
                return;
            }
            bool open = !_root.activeSelf;
            _root.SetActive(open);
            if (open)
            {
                TryMountUnderGameCanvas();
                _currentQuery = (_search ?? string.Empty).Trim();
                RebuildCatalog();
                RefreshAll();
                Cursor.visible = true;
            }
            else
            {
                BlurFields();
                RefreshSearchLabel();
                RefreshMultiplierLabel();
            }
        }

        private void TryMountUnderGameCanvas()
        {
            try
            {
                GameManager gm = GameRefs.Game;
                if (gm == null || gm.CanvasUI == null || _root == null)
                {
                    return;
                }
                if (_root.transform.parent == gm.CanvasUI.transform)
                {
                    return;
                }
                _root.transform.SetParent(gm.CanvasUI.transform, false);
                _root.transform.SetAsLastSibling();
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogWarning("TryMountUnderGameCanvas failed: " + ex.Message);
            }
        }

        internal void Close()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        // ---------------------------------------------------------------- build

        private void Build()
        {
            _built = true;
            _font = UiFactory.FindGameFont();
            _multiplier = Mathf.Max(0.01f, RareShopPlugin.CfgDefaultMultiplier.Value);
            _multiplierRaw = FormatMultiplier(_multiplier);

            // Mount inside the game's own UI canvas when it exists so the panel matches
            // its scaling; the nested canvas overrides sorting to stay on top.
            Transform mount = null;
            Canvas gameCanvas = null;
            try
            {
                GameManager gm = GameRefs.Game;
                if (gm != null && gm.CanvasUI != null)
                {
                    mount = gm.CanvasUI.transform;
                    gameCanvas = gm.CanvasUI.GetComponent<Canvas>();
                }
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogWarning("Could not access CanvasUI: " + ex.Message);
            }

            _root = UiFactory.CreateCanvasRoot("NightRaider_RareShopCanvas", mount != null ? mount : transform);
            UiFactory.MatchScaler(_root, gameCanvas);
            _root.SetActive(false);

            // Backdrop that also swallows clicks outside the window.
            GameObject backdrop = UiFactory.Panel("Backdrop", _root.transform, ColBackdrop);
            UiFactory.Stretch((RectTransform)backdrop.transform);
            RegisterClickable((RectTransform)backdrop.transform, null);

            GameObject window = UiFactory.Panel("Window", _root.transform, ColWindow);
            RectTransform winRect = (RectTransform)window.transform;
            UiFactory.AnchorCenter(winRect, WinW, WinH);

            BuildHeader(winRect);
            BuildTabs(winRect);
            BuildBody(winRect);
        }

        private void BuildHeader(RectTransform win)
        {
            GameObject title = UiFactory.Panel("TitleBar", win, new Color(1f, 1f, 1f, 0.04f));
            RectTransform titleRect = (RectTransform)title.transform;
            UiFactory.AnchorTop(titleRect, WinW - 24f, 46f, 8f);

            _titleText = UiFactory.Label("Title", titleRect, "物品商店  —  按 " + RareShopPlugin.CfgToggleKey.Value + " 关闭", _font, 20, TextAnchor.MiddleLeft, ColAccent);
            UiFactory.AnchorStretch(_titleText.rectTransform, 14f, 0f, 200f, 0f);

            _gpText = UiFactory.Label("GP", titleRect, "GP: 0", _font, 18, TextAnchor.MiddleRight, ColPrice);
            UiFactory.AnchorStretch(_gpText.rectTransform, 0f, 0f, 56f, 0f);

            // Close button
            GameObject closeGo = UiFactory.Panel("Close", titleRect, ColButton);
            RectTransform closeRect = (RectTransform)closeGo.transform;
            closeRect.anchorMin = new Vector2(1f, 0.5f);
            closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-6f, 0f);
            closeRect.sizeDelta = new Vector2(34f, 30f);
            UiFactory.Label("x", closeRect, "×", _font, 22, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(closeRect, CloseButton, ColButton, ColButtonHot);

            // Toolbar: search on the left, price multiplier on the right.
            GameObject bar = UiFactory.Panel("ToolBar", win, new Color(1f, 1f, 1f, 0.03f));
            RectTransform tools = (RectTransform)bar.transform;
            UiFactory.AnchorTop(tools, WinW - 24f, 44f, 60f);

            Text searchLabel = UiFactory.Label("SearchLabel", tools, "搜索物品：", _font, 16, TextAnchor.MiddleLeft, Color.white);
            UiFactory.AnchorStretch(searchLabel.rectTransform, 14f, 0f, WinW - 180f, 0f);

            _searchBox = UiFactory.Panel("SearchBox", tools, new Color(1f, 1f, 1f, 0.10f)).GetComponent<Image>();
            RectTransform searchRect = (RectTransform)_searchBox.transform;
            searchRect.anchorMin = new Vector2(0f, 0.5f);
            searchRect.anchorMax = new Vector2(0f, 0.5f);
            searchRect.pivot = new Vector2(0f, 0.5f);
            searchRect.anchoredPosition = new Vector2(90f, 0f);
            searchRect.sizeDelta = new Vector2(360f, 30f);
            _searchText = UiFactory.Label("SearchValue", searchRect, string.Empty, _font, 17, TextAnchor.MiddleLeft, Color.white);
            UiFactory.AnchorStretch(_searchText.rectTransform, 8f, 0f, 8f, 0f);
            _searchField = BuildInputField(_searchBox.gameObject, _searchText, _font, 17, TextAnchor.MiddleLeft, false, 32);
            _searchField.onValueChanged.AddListener(OnSearchFieldChanged);
            _searchField.onEndEdit.AddListener(OnSearchFieldEndEdit);
            RegisterClickable(searchRect, delegate { FocusField(_searchField); });

            GameObject clear = UiFactory.Panel("SearchClear", tools, ColButton);
            RectTransform clearRect = (RectTransform)clear.transform;
            clearRect.anchorMin = new Vector2(0f, 0.5f);
            clearRect.anchorMax = new Vector2(0f, 0.5f);
            clearRect.pivot = new Vector2(0f, 0.5f);
            clearRect.anchoredPosition = new Vector2(458f, 0f);
            clearRect.sizeDelta = new Vector2(56f, 30f);
            UiFactory.Label("t", clearRect, "清空", _font, 15, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(clearRect, delegate { SetSearch(string.Empty); if (_searchField != null) { _searchField.text = string.Empty; FocusField(_searchField); } }, ColButton, ColButtonHot);

            Text label = UiFactory.Label("MultLabel", tools, "价格倍率：", _font, 16, TextAnchor.MiddleRight, Color.white);
            UiFactory.AnchorStretch(label.rectTransform, 0f, 0f, 610f, 0f);

            // [-] box [+]
            _multiplierBox = UiFactory.Panel("MultBox", tools, new Color(1f, 1f, 1f, 0.10f)).GetComponent<Image>();
            RectTransform boxRect = (RectTransform)_multiplierBox.transform;
            boxRect.anchorMin = new Vector2(1f, 0.5f);
            boxRect.anchorMax = new Vector2(1f, 0.5f);
            boxRect.pivot = new Vector2(1f, 0.5f);
            boxRect.anchoredPosition = new Vector2(-232f, 0f);
            boxRect.sizeDelta = new Vector2(150f, 30f);
            _multiplierText = UiFactory.Label("MultValue", boxRect, _multiplierRaw, _font, 17, TextAnchor.MiddleCenter, Color.white);
            UiFactory.Stretch(_multiplierText.rectTransform);
            _multiplierField = BuildInputField(_multiplierBox.gameObject, _multiplierText, _font, 17, TextAnchor.MiddleCenter, false, 10);
            _multiplierField.text = _multiplierRaw;
            _multiplierField.onEndEdit.AddListener(OnMultiplierFieldEndEdit);
            RegisterClickable(boxRect, delegate { FocusField(_multiplierField); });

            GameObject minus = UiFactory.Panel("Minus", tools, ColButton);
            RectTransform minusRect = (RectTransform)minus.transform;
            PlaceRight(minusRect, -392f, 34f, 30f);
            UiFactory.Label("t", minusRect, "-", _font, 20, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(minusRect, delegate { StepMultiplier(-0.25f); }, ColButton, ColButtonHot);

            GameObject plus = UiFactory.Panel("Plus", tools, ColButton);
            RectTransform plusRect = (RectTransform)plus.transform;
            PlaceRight(plusRect, -74f, 34f, 30f);
            UiFactory.Label("t", plusRect, "+", _font, 20, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(plusRect, delegate { StepMultiplier(0.25f); }, ColButton, ColButtonHot);

            GameObject reset = UiFactory.Panel("Reset", tools, ColButton);
            RectTransform resetRect = (RectTransform)reset.transform;
            PlaceRight(resetRect, -4f, 62f, 30f);
            UiFactory.Label("t", resetRect, "重置", _font, 15, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(resetRect, delegate { SetMultiplier(1f); }, ColButton, ColButtonHot);
        }

        private void BuildTabs(RectTransform win)
        {
            GameObject bar = UiFactory.Panel("Tabs", win, new Color(1f, 1f, 1f, 0.02f));
            RectTransform barRect = (RectTransform)bar.transform;
            UiFactory.AnchorTop(barRect, WinW - 24f, 40f, 110f);

            float tabW = 118f;
            for (int i = 0; i < TabNames.Length; i++)
            {
                bool isAll = i == AllTabIndex;
                ShopCategory cat = isAll ? ShopCategory.Weapon : (ShopCategory)i;
                GameObject tab = UiFactory.Panel("Tab" + i, barRect, ColButton);
                RectTransform r = (RectTransform)tab.transform;
                r.anchorMin = new Vector2(0f, 0.5f);
                r.anchorMax = new Vector2(0f, 0.5f);
                r.pivot = new Vector2(0f, 0.5f);
                r.anchoredPosition = new Vector2(10f + i * (tabW + 6f), 0f);
                r.sizeDelta = new Vector2(tabW, 30f);
                Text label = UiFactory.Label("t", r, TabNames[i], _font, 16, TextAnchor.MiddleCenter, Color.white);
                UiFactory.Stretch(label.rectTransform);
                RegisterClickable(r, MakeTabAction(cat, isAll), ColButton, ColButtonHot);
                _tabButtons.Add(tab);
                _tabLabels.Add(label);
            }
        }

        private Action MakeTabAction(ShopCategory cat, bool isAll)
        {
            return delegate
            {
                _category = cat;
                _isAllTab = isAll;
                _scroll = 0;
                _selected = -1;
                ApplyFilter();
                RefreshAll();
            };
        }

        private void BuildBody(RectTransform win)
        {
            // List panel
            GameObject listPanel = UiFactory.Panel("ListPanel", win, new Color(0f, 0f, 0f, 0.35f));
            RectTransform listRect = (RectTransform)listPanel.transform;
            UiFactory.AnchorStretch(listRect, 12f, 12f, 40f + 320f, 12f);
            listRect.anchorMin = new Vector2(0f, 0f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.offsetMin = new Vector2(12f, 44f);
            listRect.offsetMax = new Vector2(-332f, -158f);

            GameObject content = new GameObject("Content");
            content.transform.SetParent(listRect, false);
            _listContent = content.AddComponent<RectTransform>();
            UiFactory.Stretch(_listContent);

            for (int i = 0; i < VisibleRows; i++)
            {
                _rows.Add(CreateRow(_listContent, i));
            }

            // Page controls
            GameObject pager = UiFactory.Panel("Pager", win, new Color(0f, 0f, 0f, 0.35f));
            RectTransform pagerRect = (RectTransform)pager.transform;
            pagerRect.anchorMin = new Vector2(0f, 0f);
            pagerRect.anchorMax = new Vector2(1f, 0f);
            pagerRect.pivot = new Vector2(0.5f, 0f);
            pagerRect.offsetMin = new Vector2(12f, 12f);
            pagerRect.offsetMax = new Vector2(-332f, 40f);
            pagerRect.anchoredPosition = new Vector2(0f, 0f);

            GameObject prev = UiFactory.Panel("Prev", pagerRect, ColButton);
            RectTransform prevRect = (RectTransform)prev.transform;
            prevRect.anchorMin = new Vector2(0f, 0.5f);
            prevRect.anchorMax = new Vector2(0f, 0.5f);
            prevRect.pivot = new Vector2(0f, 0.5f);
            prevRect.anchoredPosition = new Vector2(8f, 0f);
            prevRect.sizeDelta = new Vector2(90f, 24f);
            UiFactory.Label("t", prevRect, "上一页", _font, 15, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(prevRect, delegate { PageByFull(-1); }, ColButton, ColButtonHot);

            GameObject next = UiFactory.Panel("Next", pagerRect, ColButton);
            RectTransform nextRect = (RectTransform)next.transform;
            nextRect.anchorMin = new Vector2(0f, 0.5f);
            nextRect.anchorMax = new Vector2(0f, 0.5f);
            nextRect.pivot = new Vector2(0f, 0.5f);
            nextRect.anchoredPosition = new Vector2(104f, 0f);
            nextRect.sizeDelta = new Vector2(90f, 24f);
            UiFactory.Label("t", nextRect, "下一页", _font, 15, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(nextRect, delegate { PageByFull(1); }, ColButton, ColButtonHot);

            _pageText = UiFactory.Label("Page", pagerRect, "1 / 1", _font, 15, TextAnchor.MiddleLeft, Color.white);
            UiFactory.AnchorStretch(_pageText.rectTransform, 210f, 0f, 8f, 0f);

            _statusText = UiFactory.Label("Status", pagerRect, string.Empty, _font, 15, TextAnchor.MiddleRight, ColOk);
            UiFactory.AnchorStretch(_statusText.rectTransform, 0f, 0f, 12f, 0f);

            // Detail panel
            GameObject detail = UiFactory.Panel("Detail", win, new Color(0f, 0f, 0f, 0.35f));
            RectTransform detailRect = (RectTransform)detail.transform;
            detailRect.anchorMin = new Vector2(1f, 0f);
            detailRect.anchorMax = new Vector2(1f, 1f);
            detailRect.pivot = new Vector2(1f, 0.5f);
            detailRect.offsetMin = new Vector2(-324f, 12f);
            detailRect.offsetMax = new Vector2(-12f, -158f);

            _detailText = UiFactory.Label("DetailText", detailRect, string.Empty, _font, 16, TextAnchor.UpperLeft, Color.white);
            UiFactory.Stretch(_detailText.rectTransform);
            _detailText.rectTransform.offsetMin = new Vector2(12f, 12f);
            _detailText.rectTransform.offsetMax = new Vector2(-12f, -12f);
            _detailText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _detailText.verticalOverflow = VerticalWrapMode.Truncate;
        }

        private RowWidgets CreateRow(RectTransform parent, int index)
        {
            RowWidgets row = new RowWidgets();

            row.Root = UiFactory.Panel("Row" + index, parent, index % 2 == 0 ? ColRow : ColRowAlt);
            RectTransform r = (RectTransform)row.Root.transform;
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.offsetMin = new Vector2(6f, 0f);
            r.offsetMax = new Vector2(-6f, 0f);
            r.sizeDelta = new Vector2(-12f, RowH);
            r.anchoredPosition = new Vector2(0f, -6f - index * (RowH + RowGap));
            row.Bg = row.Root.GetComponent<Image>();

            // Icon
            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(r, false);
            RectTransform iconRect = iconGo.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(8f, 0f);
            iconRect.sizeDelta = new Vector2(36f, 36f);
            row.Icon = iconGo.AddComponent<Image>();
            row.Icon.preserveAspect = true;
            row.Icon.raycastTarget = false;

            row.Name = UiFactory.Label("Name", r, "...", _font, 17, TextAnchor.MiddleLeft, Color.white);
            UiFactory.AnchorStretch(row.Name.rectTransform, 52f, 0f, 520f, 0f);

            // Fixed-width columns so the labels can never run into each other.
            row.Stock = UiFactory.Label("Stock", r, "", _font, 14, TextAnchor.MiddleRight, new Color(0.7f, 0.7f, 0.7f, 1f));
            UiFactory.AnchorStretch(row.Stock.rectTransform, 175f, 0f, 560f, 0f);

            row.Price = UiFactory.Label("Price", r, "", _font, 16, TextAnchor.MiddleRight, ColPrice);
            UiFactory.AnchorStretch(row.Price.rectTransform, 560f, 0f, 262f, 0f);

            // Buy amount controls
            GameObject minus = UiFactory.Panel("AmtMinus", r, ColButton);
            RectTransform minusRect = (RectTransform)minus.transform;
            PlaceRight(minusRect, -232f, 26f, 26f);
            UiFactory.Label("t", minusRect, "-", _font, 18, TextAnchor.MiddleCenter, Color.white);

            row.Amount = UiFactory.Label("Amount", r, "1", _font, 16, TextAnchor.MiddleCenter, Color.white);
            RectTransform amtRect = row.Amount.rectTransform;
            amtRect.anchorMin = new Vector2(1f, 0.5f);
            amtRect.anchorMax = new Vector2(1f, 0.5f);
            amtRect.pivot = new Vector2(1f, 0.5f);
            amtRect.anchoredPosition = new Vector2(-198f, 0f);
            amtRect.sizeDelta = new Vector2(48f, 26f);
            row.Amount.text = "x1";

            GameObject plus = UiFactory.Panel("AmtPlus", r, ColButton);
            RectTransform plusRect = (RectTransform)plus.transform;
            PlaceRight(plusRect, -152f, 26f, 26f);
            UiFactory.Label("t", plusRect, "+", _font, 18, TextAnchor.MiddleCenter, Color.white);

            GameObject buy = UiFactory.Panel("Buy", r, ColButton);
            RectTransform buyRect = (RectTransform)buy.transform;
            PlaceRight(buyRect, -6f, 132f, 28f);
            UiFactory.Label("t", buyRect, "购买", _font, 16, TextAnchor.MiddleCenter, Color.white);

            int captured = index;
            RegisterClickable(minusRect, delegate { ChangeAmount(captured, -1); }, ColButton, ColButtonHot);
            RegisterClickable(plusRect, delegate { ChangeAmount(captured, 1); }, ColButton, ColButtonHot);
            RegisterClickable(buyRect, delegate { Buy(captured); }, ColButton, ColButtonHot);
            RegisterClickable(r, delegate { Select(captured); }, new Color(0f, 0f, 0f, 0f), new Color(1f, 1f, 1f, 0.06f));

            return row;
        }

        // ----------------------------------------------------------- behaviour

        private void RebuildCatalog()
        {
            _allItems.Clear();
            if (GameRefs.IsGameDataReady())
            {
                _allItems.AddRange(ItemCatalog.Build(1));
            }
            ApplyFilter();

            if (RareShopPlugin.CfgDebugLog != null && RareShopPlugin.CfgDebugLog.Value)
            {
                int[] perCategory = new int[TabNames.Length];
                for (int i = 0; i < _allItems.Count; i++)
                {
                    perCategory[(int)_allItems[i].Category]++;
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < perCategory.Length; i++)
                {
                    sb.Append(TabNames[i]).Append('=').Append(perCategory[i]).Append("  ");
                }
                RareShopPlugin.Log.LogInfo("Catalog built: total=" + _allItems.Count + "  " + sb);
            }
        }

        /// <summary>
        /// Applies the category tab and the search text. When the player is searching,
        /// matches from every category are shown regardless of the active tab, so a name
        /// lookup always succeeds without having to guess the right tab.
        /// </summary>
        private void ApplyFilter()
        {
            _filtered.Clear();
            _searchMatchedCategory = -1;
            string query = (_search ?? string.Empty).Trim();
            bool searching = query.Length > 0;

            for (int i = 0; i < _allItems.Count; i++)
            {
                ShopEntry e = _allItems[i];
                if (searching)
                {
                    if (!MatchesSearch(e, query))
                    {
                        continue;
                    }
                    if (_searchMatchedCategory < 0)
                    {
                        _searchMatchedCategory = (int)e.Category;
                    }
                    _filtered.Add(e);
                }
                else if (_isAllTab || e.Category == _category)
                {
                    _filtered.Add(e);
                }
            }

            if (searching)
            {
                _filtered.Sort(CompareSearchResults);
            }

            int maxScroll = Mathf.Max(0, _filtered.Count - VisibleRows);
            _scroll = Mathf.Clamp(_scroll, 0, maxScroll);
        }

        private static bool MatchesSearch(ShopEntry e, string query)
        {
            if (string.IsNullOrEmpty(e.DisplayName))
            {
                return false;
            }
            if (e.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(e.DisplayNameEn)
                && e.DisplayNameEn.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return e.ModelId.ToString().IndexOf(query, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Puts exact and prefix matches first, then the cheaper ones.</summary>
        private static int CompareSearchResults(ShopEntry a, ShopEntry b)
        {
            int ra = SearchRank(a);
            int rb = SearchRank(b);
            if (ra != rb)
            {
                return ra.CompareTo(rb);
            }
            int c = a.SellPrice.CompareTo(b.SellPrice);
            if (c != 0)
            {
                return c;
            }
            return a.ModelId.CompareTo(b.ModelId);
        }

        private static int SearchRank(ShopEntry e)
        {
            string q = _currentQuery;
            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(e.DisplayName))
            {
                return 3;
            }
            if (string.Equals(e.DisplayName, q, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (e.DisplayName.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (!string.IsNullOrEmpty(e.DisplayNameEn)
                && (string.Equals(e.DisplayNameEn, q, StringComparison.OrdinalIgnoreCase)
                    || e.DisplayNameEn.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
            {
                return 1;
            }
            return 2;
        }

        private static string _currentQuery = string.Empty;

        private void Update()
        {
            if (_root == null || !_root.activeSelf)
            {
                return;
            }

            HandleScroll();
            HandleClick();
            UpdateStatus();
        }

        /// <summary>Live filter as the player types in the search box.</summary>
        private void OnSearchFieldChanged(string value)
        {
            SetSearch(value);
        }

        private void OnSearchFieldEndEdit(string value)
        {
            SetSearch(value);
        }

        private void OnMultiplierFieldEndEdit(string value)
        {
            float parsed;
            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed) && parsed > 0f)
            {
                SetMultiplier(parsed);
            }
            else
            {
                SetMultiplier(_multiplier);
            }
            if (_multiplierField != null)
            {
                _multiplierField.text = _multiplierRaw;
            }
        }
        private void SetSearch(string value)
        {
            string incoming = value ?? string.Empty;
            bool unchanged = incoming == _search;
            _search = incoming;
            _currentQuery = _search.Trim();
            if (!unchanged)
            {
                _scroll = 0;
                _selected = -1;
                ApplyFilter();
                RefreshRows();
                RefreshDetail();
                RefreshPageLabel();
            }
            RefreshSearchLabel();
        }

        private void RefreshSearchLabel()
        {
            if (_searchText == null)
            {
                return;
            }
            if (_searchText.text != _search)
            {
                _searchText.text = _search;
            }
            _searchText.color = string.IsNullOrEmpty(_search)
                ? new Color(0.7f, 0.7f, 0.7f, 1f)
                : Color.white;
        }

        private void BeginEditMultiplier()
        {
            FocusField(_multiplierField);
            _multiplierRaw = string.Empty;
            RefreshMultiplierLabel();
            FlashStatus("输入倍率，回车确认（例如 2.5）", ColAccent);
        }

        private void StepMultiplier(float delta)
        {
            SetMultiplier(_multiplier + delta);
        }

        private void SetMultiplier(float value)
        {
            _multiplier = Mathf.Clamp(value, 0.01f, 1000f);
            _multiplierRaw = FormatMultiplier(_multiplier);
            RefreshMultiplierLabel();
            RefreshRowPrices();
            RefreshDetail();
        }

        private static string FormatMultiplier(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void RefreshMultiplierLabel()
        {
            if (_multiplierText == null)
            {
                return;
            }
            if (_multiplierText.text != _multiplierRaw)
            {
                _multiplierText.text = _multiplierRaw;
            }
            if (_multiplierField != null && _multiplierField.text != _multiplierRaw)
            {
                _multiplierField.text = _multiplierRaw;
            }
        }

        private void HandleScroll()
        {
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) < 0.001f)
            {
                return;
            }
            PageBy(wheel > 0f ? -1 : 1);
        }

        private void PageBy(int rows)
        {
            int maxScroll = Mathf.Max(0, _filtered.Count - VisibleRows);
            _scroll = Mathf.Clamp(_scroll + rows, 0, maxScroll);
            RefreshRows();
        }

        private void RefreshPageLabel()
        {
            if (_pageText == null)
            {
                return;
            }
            int pages = Mathf.Max(1, Mathf.CeilToInt(_filtered.Count / (float)VisibleRows));
            int page = _scroll / VisibleRows + 1;
            string what = (_search ?? string.Empty).Trim().Length > 0 ? "匹配" : "共";
            _pageText.text = string.Format("第 {0}/{1} 页   {2} {3} 项", page, pages, what, _filtered.Count);
        }

        private void PageByFull(int direction)
        {
            PageBy(direction * VisibleRows);
        }

        private void ChangeAmount(int rowIndex, int dir)
        {
            ShopEntry entry = EntryAt(rowIndex);
            if (entry == null)
            {
                return;
            }
            _selected = _scroll + rowIndex;
            int next = entry.BuyAmount + dir;
            if (next < 1)
            {
                next = 1;
            }
            if (next > entry.MaxStack)
            {
                next = entry.MaxStack;
            }
            entry.BuyAmount = next;
            RefreshRows();
            RefreshDetail();
        }

        private void Select(int rowIndex)
        {
            if (EntryAt(rowIndex) == null)
            {
                return;
            }
            _selected = _scroll + rowIndex;
            RefreshRows();
            RefreshDetail();
        }

        private ShopEntry EntryAt(int rowIndex)
        {
            int index = _scroll + rowIndex;
            if (index < 0 || index >= _filtered.Count)
            {
                return null;
            }
            return _filtered[index];
        }

        private void Buy(int rowIndex)
        {
            ShopEntry entry = EntryAt(rowIndex);
            if (entry == null)
            {
                return;
            }
            _selected = _scroll + rowIndex;

            int total = entry.TotalPrice(_multiplier);
            DataHub data = GameRefs.Data;
            CurrentDataHub cur = GameRefs.Current;
            if (data == null || cur == null)
            {
                FlashStatus("读不到游戏数据，请先进入存档。", ColBad);
                return;
            }

            if (data.CheckMyGP() < total)
            {
                FlashStatus("GP 不足，需要 " + total + "，当前 " + data.CheckMyGP() + "。", ColBad);
                PlayError();
                return;
            }

            BagWriter.AddResult res = BagWriter.TryAdd(cur.myBag, entry.ModelId, entry.BuyAmount);
            bool toDepot = false;
            if (res == BagWriter.AddResult.NoSpace)
            {
                // Fall back to the warehouse so the purchase is not lost.
                res = TryAddToAnyDepot(cur, entry.ModelId, entry.BuyAmount);
                toDepot = res == BagWriter.AddResult.Ok;
            }

            if (res != BagWriter.AddResult.Ok)
            {
                FlashStatus(res == BagWriter.AddResult.NoSpace ? "背包和仓库都放不下了。" : "购买失败（物品无效）。", ColBad);
                PlayError();
                return;
            }

            data.ChangeMyGP(-total);
            data.GetRecord(3, total);
            SaveAfterPurchase(toDepot);

            PlayBuy();
            FlashStatus("已购买 " + entry.DisplayName + " x" + entry.BuyAmount + "，花费 " + total + " GP" + (toDepot ? "（已放入仓库）" : "") + "。", ColOk);
            RefreshAll();
        }

        private void CloseButton()
        {
            Close();
        }

        /// <summary>Anchors a control to the right edge of its parent.</summary>
        private static void PlaceRight(RectTransform rect, float rightOffset, float width, float height)
        {
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(rightOffset, 0f);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static BagWriter.AddResult TryAddToAnyDepot(CurrentDataHub cur, int modelId, int count)
        {
            // Only depots A-F carry an unlock flag in the save file, so those are
            // the ones that can be used safely.
            int[] unlock = new int[]
            {
                cur.depotUnlockA, cur.depotUnlockB, cur.depotUnlockC,
                cur.depotUnlockD, cur.depotUnlockE, cur.depotUnlockF
            };
            Dictionary<int, CurrentItemData>[] bags = new Dictionary<int, CurrentItemData>[]
            {
                cur.depotA_Bag, cur.depotB_Bag, cur.depotC_Bag,
                cur.depotD_Bag, cur.depotE_Bag, cur.depotF_Bag
            };
            for (int i = 0; i < bags.Length; i++)
            {
                if (bags[i] == null)
                {
                    continue;
                }
                BagWriter.AddResult r = BagWriter.TryAdd(bags[i], modelId, count);
                if (r == BagWriter.AddResult.Ok)
                {
                    try
                    {
                        GameRefs.Data.SaveDepot(i + 1);
                    }
                    catch (Exception ex)
                    {
                        RareShopPlugin.Log.LogWarning("SaveDepot failed: " + ex.Message);
                    }
                    return r;
                }
            }
            return BagWriter.AddResult.NoSpace;
        }

        private static void SaveAfterPurchase(bool depotOnly)
        {
            DataHub data = GameRefs.Data;
            if (data == null)
            {
                return;
            }
            try
            {
                if (!depotOnly)
                {
                    data.SaveBag();
                }
                data.SaveRecord();
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogError("SaveAfterPurchase failed: " + ex);
            }
        }

        private static void PlayUiSound(int id)
        {
            try
            {
                AudioManager audio = UnityEngine.Object.FindObjectOfType<AudioManager>();
                if (audio != null)
                {
                    audio.PlayUIAudio(id);
                }
            }
            catch
            {
            }
        }

        private void PlayBuy()
        {
            PlayUiSound(9017);
        }

        private void PlayError()
        {
            PlayUiSound(9002);
        }

        private void FlashStatus(string message, Color color)
        {
            _status = message;
            _statusUntil = Time.unscaledTime + 6f;
            if (_statusText != null)
            {
                _statusText.text = message;
                _statusText.color = color;
            }
        }

        private void UpdateStatus()
        {
            if (_statusText == null)
            {
                return;
            }
            if (_status.Length > 0 && Time.unscaledTime > _statusUntil)
            {
                _status = string.Empty;
            }
            if (_status.Length == 0 && _statusText.text.Length > 0)
            {
                _statusText.text = string.Empty;
            }
        }

        private void RefreshAll()
        {
            RefreshTabs();
            RefreshSearchLabel();
            RefreshMultiplierLabel();
            RefreshGp();
            RefreshRows();
            RefreshDetail();
        }

        /// <summary>
        /// Reads the player's GP. The game's CheckMyGP indexes recordDataDic directly,
        /// which throws until a save has been loaded, so that is guarded here.
        /// </summary>
        private static int SafeGetGp()
        {
            try
            {
                DataHub data = GameRefs.Data;
                return data != null ? data.CheckMyGP() : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void RefreshGp()
        {
            if (_gpText == null)
            {
                return;
            }
            _gpText.text = "GP: " + SafeGetGp().ToString("N0");
        }

        private void RefreshTabs()
        {
            for (int i = 0; i < _tabLabels.Count; i++)
            {
                bool active = (i == AllTabIndex) ? _isAllTab : (!_isAllTab && (ShopCategory)i == _category);
                _tabLabels[i].color = active ? ColAccent : new Color(0.75f, 0.75f, 0.75f, 1f);
                Image img = _tabButtons[i].GetComponent<Image>();
                if (img != null)
                {
                    img.color = active ? new Color(0.16f, 0.32f, 0.48f, 1f) : ColButton;
                }
            }
        }

        private void RefreshRowPrices()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                ShopEntry e = EntryAt(i);
                if (e == null || _rows[i].Price == null)
                {
                    continue;
                }
                _rows[i].Price.text = e.TotalPrice(_multiplier).ToString("N0") + " GP";
            }
        }

        private void RefreshRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                RowWidgets row = _rows[i];
                ShopEntry e = EntryAt(i);
                if (e == null)
                {
                    row.Root.SetActive(false);
                    row.EntryIndex = -1;
                    continue;
                }
                row.Root.SetActive(true);
                row.EntryIndex = _scroll + i;

                row.Name.text = e.DisplayName;
                row.Price.text = e.TotalPrice(_multiplier).ToString("N0") + " GP";
                row.Amount.text = "x" + e.BuyAmount;

                int owned = 0;
                if (GameRefs.IsSaveLoaded())
                {
                    try
                    {
                        owned = GameRefs.Current.CheckAllDepotHaveSomeThing(e.ModelId);
                    }
                    catch
                    {
                        owned = 0;
                    }
                }
                row.Stock.text = "售价 " + e.SellPrice + "   已有 " + owned;

                bool sel = _selected == (_scroll + i);
                row.Bg.color = RowBackground(e, i, sel);

                Sprite icon = null;
                try
                {
                    ResourcesHub hub = GameRefs.Resources;
                    if (hub != null && hub.imageDic != null)
                    {
                        hub.imageDic.TryGetValue(e.ModelId, out icon);
                    }
                }
                catch
                {
                    icon = null;
                }
                row.Icon.sprite = icon;
                row.Icon.color = icon != null ? Color.white : new Color(1f, 1f, 1f, 0.06f);
            }

            RefreshPageLabel();
        }

        /// <summary>
        /// Background of one shop row.
        ///
        /// With rarity colouring on, the tier colour is the base and selection tints it, so
        /// "which row is selected" and "how rare is this item" stay readable at the same
        /// time. A row the tier table does not know -- or the feature switched off -- falls
        /// back to the original subtle zebra striping.
        /// </summary>
        private static Color RowBackground(ShopEntry e, int visualIndex, bool selected)
        {
            int tier = 0;
            if (RareShopPlugin.CfgRarityInShop != null && RareShopPlugin.CfgRarityInShop.Value)
            {
                tier = ItemTiers.TierOf(e.ModelId);
            }

            if (tier <= 0)
            {
                return selected ? ColRowSel : (visualIndex % 2 == 0 ? ColRow : ColRowAlt);
            }

            Color bg = RarityPalette.ShopRowColor(tier);
            if (visualIndex % 2 == 1)
            {
                // Keep a faint stripe between neighbouring rows of the same tier.
                bg = new Color(bg.r * 0.82f, bg.g * 0.82f, bg.b * 0.82f, 1f);
            }
            if (selected)
            {
                bg = Color.Lerp(bg, ColRowSel, 0.6f);
                bg.a = 1f;
            }
            return bg;
        }

        private void RefreshDetail()

        {
            if (_detailText == null)
            {
                return;
            }
            ShopEntry e = (_selected >= 0 && _selected < _filtered.Count) ? _filtered[_selected] : null;
            if (e == null)
            {
                string searching = (_search ?? string.Empty).Trim();
                if (searching.Length > 0 && _filtered.Count == 0)
                {
                    _detailText.text = "<b>没有找到匹配的物品</b>\n\n"
                        + "当前搜索词：<color=#FFD95A>" + searching + "</color>\n\n"
                        + "· 可以只输入名字的一部分，例如「药」或「9mm」\n"
                        + "· 支持中文名、英文名和物品 ID\n"
                        + "· 点「清空」按钮清除搜索\n"
                        + "· 搜索时不区分页签，会从全部物品里找";
                    return;
                }
                _detailText.text = "<b>操作说明</b>\n\n"
                    + "· 上方搜索框输入名字即可查找物品，支持中/英文和 ID\n"
                    + "· 点击左侧物品行选中\n"
                    + "· [-] / [+] 调整购买数量\n"
                    + "· [购买] 花费 GP 买下，优先放入背包，背包满则放入仓库\n"
                    + "· 点击倍率框后直接键入数字，回车确认\n"
                    + "· 鼠标滚轮翻页\n\n"
                    + "最终价格 = 物品售价 × 倍率。";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(e.DisplayName).Append("</b>\n\n");
            sb.Append("物品 ID：").Append(e.ModelId).Append('\n');
            sb.Append("分类：").Append(TabNames[(int)e.Category]).Append('\n');

            int rarityTier = (RareShopPlugin.CfgRarityInShop != null && RareShopPlugin.CfgRarityInShop.Value)
                ? ItemTiers.TierOf(e.ModelId)
                : 0;
            if (rarityTier > 0)
            {
                sb.Append("稀有度：<color=#").Append(RarityPalette.HexOf(rarityTier)).Append('>')
                    .Append(RarityPalette.NameOf(rarityTier)).Append("</color>（").Append(rarityTier).Append(" 档）\n");
            }
            sb.Append("售价：").Append(e.SellPrice).Append(" GP\n");
            sb.Append("单价：").Append(e.UnitPrice(_multiplier).ToString("N0")).Append(" GP（×").Append(FormatMultiplier(_multiplier)).Append("）\n");
            sb.Append("数量：").Append(e.BuyAmount).Append(" / 堆叠上限 ").Append(e.MaxStack).Append('\n');
            sb.Append("总价：<color=#FFD95A>").Append(e.TotalPrice(_multiplier).ToString("N0")).Append(" GP</color>\n");

            string desc = null;
            try
            {
                TextManager tm = GameRefs.Text;
                if (tm != null)
                {
                    desc = tm.GetTextByItems(e.ModelId + 50000);
                }
            }
            catch
            {
                desc = null;
            }
            if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 0 && desc.Trim() != " ")
            {
                sb.Append('\n').Append(desc.Replace("<color=#00FF00>", "<color=#7FE87F>"));
            }

            _detailText.text = sb.ToString();
        }

        // --------------------------------------------------------------- input

        /// <summary>
        /// Builds a real UnityEngine.UI.InputField on top of a panel. A real InputField is
        /// required for CJK text: it switches Input.imeCompositionMode to On and reads
        /// Input.compositionString, which is what makes the IME candidate window appear.
        /// Reading Input.inputString directly only ever yields ASCII. The field only
        /// processes keys while EventSystem.currentSelectedGameObject is its GameObject,
        /// so clicking it selects it.
        /// </summary>
        private InputField BuildInputField(GameObject host, Text textBox, Font font, int fontSize,
            TextAnchor anchor, bool multiline, int characterLimit)
        {
            InputField field = host.GetComponent<InputField>();
            if (field == null)
            {
                field = host.AddComponent<InputField>();
            }

            textBox.supportRichText = false;
            textBox.horizontalOverflow = HorizontalWrapMode.Overflow;
            textBox.verticalOverflow = VerticalWrapMode.Truncate;
            textBox.alignment = anchor;
            textBox.raycastTarget = false;
            textBox.font = font;
            textBox.fontSize = fontSize;

            field.textComponent = textBox;
            field.text = string.Empty;
            field.characterLimit = characterLimit;
            field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            field.contentType = InputField.ContentType.Standard;
            field.caretColor = ColAccent;
            field.selectionColor = new Color(0.25f, 0.5f, 0.8f, 0.6f);
            field.customCaretColor = true;
            _fields.Add(field);
            return field;
        }

        /// <summary>
        /// Makes the given field the one that receives keyboard input, and remembers what
        /// was selected before so it can be restored when the shop closes.
        /// </summary>
        private void FocusField(InputField field)
        {
            if (field == null)
            {
                return;
            }
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                RareShopPlugin.Log.LogWarning("No EventSystem: text input cannot be focused.");
                return;
            }
            if (es.currentSelectedGameObject != field.gameObject)
            {
                if (_lastSelected == null)
                {
                    _lastSelected = es.currentSelectedGameObject;
                }
                es.SetSelectedGameObject(field.gameObject);
            }
            field.ActivateInputField();
            field.Select();
        }

        /// <summary>Releases focus and stops the IME composition.</summary>
        private void BlurFields()
        {
            try
            {
                Input.imeCompositionMode = IMECompositionMode.Auto;
            }
            catch
            {
            }
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                return;
            }
            InputField focused = null;
            for (int i = 0; i < _fields.Count; i++)
            {
                if (es.currentSelectedGameObject == _fields[i].gameObject)
                {
                    focused = _fields[i];
                    break;
                }
            }
            if (focused != null)
            {
                focused.DeactivateInputField();
                es.SetSelectedGameObject(_lastSelected);
                _lastSelected = null;
            }
        }

        private void RegisterClickable(RectTransform rect, Action onClick)
        {
            RegisterClickable(rect, onClick, new Color(0f, 0f, 0f, 0f), new Color(1f, 1f, 1f, 0.06f));
        }

        private void RegisterClickable(RectTransform rect, Action onClick, Color normal, Color hover)
        {
            ButtonWidget w = new ButtonWidget();
            w.Rect = rect;
            w.Image = rect.GetComponent<Image>();
            w.OnClick = onClick;
            w.Normal = normal;
            w.Hover = hover;
            _widgets[_nextWidgetId++] = w;
        }

        private void HandleClick()
        {
            // Resolve which control the cursor is over. The game has an EventSystem, but
            // fall back to a rect test if it is unavailable or does not report the mod's
            // graphics, so the buttons always work.
            ButtonWidget hit = null;
            Vector2 mouse = Input.mousePosition;
            EventSystem es = EventSystem.current;
            if (es != null)
            {
                PointerEventData ped = new PointerEventData(es);
                ped.position = mouse;
                List<RaycastResult> hits = new List<RaycastResult>();
                es.RaycastAll(ped, hits);

                for (int i = 0; i < hits.Count; i++)
                {
                    ButtonWidget w;
                    if (TryFindWidget(hits[i].gameObject.transform as RectTransform, out w))
                    {
                        hit = w;
                        break;
                    }
                }
            }
            if (hit == null)
            {
                hit = FindWidgetByRect(mouse);
            }

            // Hover feedback
            foreach (KeyValuePair<int, ButtonWidget> kv in _widgets)
            {
                ButtonWidget w = kv.Value;
                if (w.Image == null)
                {
                    continue;
                }
                bool isHit = hit == w;
                w.Image.color = isHit ? w.Hover : w.Normal;
            }

            if (Input.GetMouseButtonDown(0))
            {
                bool clickedMultiplierBox = _multiplierBox != null && hit != null && hit.Rect == _multiplierBox.transform;
                bool clickedSearchBox = _searchBox != null && hit != null && hit.Rect == _searchBox.transform;
                if (hit != null && hit.OnClick != null)
                {
                    hit.OnClick();
                }
                else if (!clickedMultiplierBox && !clickedSearchBox)
                {
                    // Clicking anywhere else releases the text fields (and the IME).
                    BlurFields();
                }
            }
        }

        private ButtonWidget FindWidget(Component c)
        {
            if (c == null)
            {
                return null;
            }
            foreach (KeyValuePair<int, ButtonWidget> kv in _widgets)
            {
                if (kv.Value.Rect == c.transform)
                {
                    return kv.Value;
                }
            }
            return null;
        }

        private bool TryFindWidget(RectTransform rect, out ButtonWidget widget)
        {
            widget = null;
            Transform t = rect;
            while (t != null)
            {
                foreach (KeyValuePair<int, ButtonWidget> kv in _widgets)
                {
                    if (kv.Value.Rect == t)
                    {
                        widget = kv.Value;
                        return true;
                    }
                }
                t = t.parent;
            }
            return false;
        }

        private ButtonWidget FindWidgetByRect(Vector2 screenPoint)
        {
            ButtonWidget best = null;
            int bestDepth = -1;
            foreach (KeyValuePair<int, ButtonWidget> kv in _widgets)
            {
                ButtonWidget w = kv.Value;
                if (w.Rect == null)
                {
                    continue;
                }
                Canvas canvas = w.Rect.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(w.Rect, screenPoint, cam))
                {
                    // Deeper widgets sit on top of shallower ones, so they win.
                    int depth = Depth(w.Rect);
                    if (best == null || depth > bestDepth)
                    {
                        best = w;
                        bestDepth = depth;
                    }
                }
            }
            return best;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null)
            {
                d++;
                t = t.parent;
            }
            return d;
        }
    }
}
