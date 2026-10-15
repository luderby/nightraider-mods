using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NightRaider.RareRefresh
{
    /// <summary>
    /// The ten-window random shop.
    ///
    /// Ten fixed card slots are laid out in a 5x2 grid. Each slot is bound to one kind of
    /// goods and shows a single randomly rolled offer: one item, one price (10x-25x its sell
    /// price) and one lot. Buying a window burns it out to "已售罄"; paying the refresh fee
    /// rolls that window a brand new item and a brand new price.
    ///
    /// Clicks are resolved by raycasting plus a rect test rather than by relying on Unity's
    /// Button events, which is the approach the item-shop mod already validates against this
    /// game. The panel has no text fields, so it does not need the IME handling.
    /// </summary>
    internal sealed class RareRefreshPanel : MonoBehaviour
    {
        // Layout constants ------------------------------------------------------------
        private static readonly int WindowCount = ShopCatalog.WindowCount;
        private const int Cols = 5;
        private const int Rows = 2;

        private const float CardW = 360f;
        private const float CardH = 412f;
        private const float CardGapX = 14f;
        private const float CardGapY = 16f;
        private const float GridTop = 84f;

        private static readonly string HeaderText = "刷新商店  —  "
            + ShopCatalog.WindowCount + " 个窗口 · 按稀有度定价 · 刷新重掷整页";

        // Palette ---------------------------------------------------------------------
        private static readonly Color ColBackdrop = new Color(0f, 0f, 0f, 0.80f);
        private static readonly Color ColWindow = new Color(0.07f, 0.085f, 0.105f, 0.99f);
        private static readonly Color ColCard = new Color(0.13f, 0.15f, 0.19f, 1f);
        private static readonly Color ColCardOut = new Color(0.10f, 0.10f, 0.11f, 1f);
        private static readonly Color ColHeader = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ColButton = new Color(0.19f, 0.23f, 0.29f, 1f);
        private static readonly Color ColButtonHot = new Color(0.27f, 0.36f, 0.46f, 1f);
        private static readonly Color ColBuy = new Color(0.16f, 0.40f, 0.24f, 1f);
        private static readonly Color ColBuyHot = new Color(0.22f, 0.55f, 0.32f, 1f);
        private static readonly Color ColRefresh = new Color(0.34f, 0.25f, 0.13f, 1f);
        private static readonly Color ColRefreshHot = new Color(0.48f, 0.35f, 0.17f, 1f);
        private static readonly Color ColDisabled = new Color(0.14f, 0.14f, 0.15f, 1f);
        private static readonly Color ColAccent = new Color(0.40f, 0.78f, 1f, 1f);
        private static readonly Color ColPrice = new Color(1f, 0.84f, 0.32f, 1f);
        private static readonly Color ColDeal = new Color(0.45f, 0.95f, 0.50f, 1f);
        private static readonly Color ColDim = new Color(0.65f, 0.67f, 0.72f, 1f);
        private static readonly Color ColOk = new Color(0.46f, 0.90f, 0.52f, 1f);
        private static readonly Color ColBad = new Color(1f, 0.44f, 0.40f, 1f);

        private sealed class Card
        {
            internal GameObject Root;
            internal Image Bg;
            internal Image Icon;
            internal Text Header;
            internal Text Name;
            internal Text Price;
            internal Text Deal;
            internal Text Info;
            internal Text BuyLabel;
            internal Image BuyBg;
            internal RectTransform BuyRect;
        }

        private sealed class Widget
        {
            internal RectTransform Rect;
            internal Image Image;
            internal Action OnClick;
            internal Color Normal;
            internal Color Hover;
        }

        private GameObject _root;
        private Font _font;
        private Text _gpText;
        private Text _statusText;
        private Text _hintText;
        private Text _refreshLabel;
        private bool _built;

        private readonly Card[] _cards = new Card[WindowCount];
        private readonly ShopStock[] _stock = new ShopStock[WindowCount];
        private readonly Dictionary<int, Widget> _widgets = new Dictionary<int, Widget>();
        private int _nextWidgetId;

        private string _status = string.Empty;
        private float _statusUntil;

        internal bool IsOpen { get { return _root != null && _root.activeSelf; } }

        internal static RareRefreshPanel Create()
        {
            GameObject go = new GameObject("NightRaider_RareRefreshPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<RareRefreshPanel>();
        }

        // ------------------------------------------------------------------ open/close

        internal void Toggle()
        {
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
                MountUnderGameCanvas();
                OnOpened();
                Cursor.visible = true;
            }
        }

        internal void Close()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        private void OnOpened()
        {
            // The tier table may have been edited since the last open, so rebuild the pools
            // and fill every window that is not currently showing a live offer. Windows that
            // are still stocked (or sold out) are left exactly as the player left them, so
            // closing the shop does not reroll anything for free.
            ItemPool.Build(true);

            int rolled = 0;
            for (int i = 0; i < WindowCount; i++)
            {
                if (_stock[i] == null)
                {
                    _stock[i] = ItemPool.Roll(OtherModels(i));
                    rolled++;
                }
            }

            if (rolled > 0)
            {
                RareRefreshPlugin.Log.LogInfo("已为 " + rolled + " 个空窗口生成了商品。");
            }

            RefreshAll();
            FlashStatus("点「购买」买下；点「刷新」花 " + RareRefreshPlugin.RefreshCost.ToString("N0")
                + " GP 一次性重掷全部 10 个窗口。", ColAccent);
        }

        private void MountUnderGameCanvas()
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
                RareRefreshPlugin.Log.LogWarning("挂载到游戏画布失败: " + ex.Message);
            }
        }

        // ----------------------------------------------------------------------- build

        private void Build()
        {
            _built = true;
            _font = UiFactory.FindGameFont();

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
                RareRefreshPlugin.Log.LogWarning("读取 CanvasUI 失败: " + ex.Message);
            }

            _root = UiFactory.CreateCanvasRoot("NightRaider_RareRefreshCanvas",
                mount != null ? mount : transform);
            UiFactory.MatchScaler(_root, gameCanvas);
            _root.SetActive(false);

            // Full-screen backdrop: it swallows clicks that miss a button, and gives the
            // window something opaque to sit on.
            GameObject backdrop = UiFactory.Panel("Backdrop", _root.transform, ColBackdrop);
            UiFactory.Stretch((RectTransform)backdrop.transform);
            RegisterClickable((RectTransform)backdrop.transform, null, ColBackdrop, ColBackdrop);

            GameObject window = UiFactory.Panel("Window", _root.transform, ColWindow);
            RectTransform winRect = (RectTransform)window.transform;
            UiFactory.AnchorCenter(winRect, 1920f, 1060f);

            BuildHeader(winRect);
            BuildGrid(winRect);
            BuildFooter(winRect);
        }

        private void BuildHeader(RectTransform win)
        {
            GameObject bar = UiFactory.Panel("TitleBar", win, ColHeader);
            RectTransform barRect = (RectTransform)bar.transform;
            UiFactory.AnchorTop(barRect, 1912f, 60f, 4f);

            Text title = UiFactory.Label("Title", barRect, HeaderText, _font, 24,
                TextAnchor.MiddleLeft, ColAccent);
            UiFactory.AnchorStretch(title.rectTransform, 18f, 0f, 300f, 0f);

            _gpText = UiFactory.Label("GP", barRect, "GP: --", _font, 22,
                TextAnchor.MiddleRight, ColPrice);
            UiFactory.AnchorStretch(_gpText.rectTransform, 0f, 0f, 74f, 0f);

            GameObject close = UiFactory.Panel("Close", barRect, ColButton);
            RectTransform closeRect = (RectTransform)close.transform;
            closeRect.anchorMin = new Vector2(1f, 0.5f);
            closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-8f, 0f);
            closeRect.sizeDelta = new Vector2(46f, 42f);
            UiFactory.Label("x", closeRect, "×", _font, 28, TextAnchor.MiddleCenter, Color.white);
            RegisterClickable(closeRect, Close, ColButton, ColButtonHot);
        }

        private void BuildGrid(RectTransform win)
        {
            for (int i = 0; i < WindowCount; i++)
            {
                int col = i % Cols;
                int row = i / Cols;

                GameObject cardGo = UiFactory.Panel("Card" + i, win, ColCard);
                RectTransform r = (RectTransform)cardGo.transform;
                r.anchorMin = new Vector2(0f, 1f);
                r.anchorMax = new Vector2(0f, 1f);
                r.pivot = new Vector2(0f, 1f);
                r.sizeDelta = new Vector2(CardW, CardH);
                r.anchoredPosition = new Vector2(
                    20f + col * (CardW + CardGapX),
                    -(GridTop + row * (CardH + CardGapY)));

                Card card = new Card();
                card.Root = cardGo;
                card.Bg = cardGo.GetComponent<Image>();
                _cards[i] = card;

                // Category band. Each card draws its own random category on every roll, so
                // the band is filled in per card by RefreshCard rather than fixed here.
                GameObject band = UiFactory.Panel("Band", r, ColHeader);
                RectTransform bandRect = (RectTransform)band.transform;
                bandRect.anchorMin = new Vector2(0f, 1f);
                bandRect.anchorMax = new Vector2(1f, 1f);
                bandRect.pivot = new Vector2(0.5f, 1f);
                bandRect.offsetMin = new Vector2(0f, 0f);
                bandRect.offsetMax = new Vector2(0f, 0f);
                bandRect.sizeDelta = new Vector2(0f, 38f);
                bandRect.anchoredPosition = Vector2.zero;

                card.Header = UiFactory.Label("H", bandRect, "#" + (i + 1),
                    _font, 19, TextAnchor.MiddleCenter, Color.white);
                UiFactory.Stretch(card.Header.rectTransform);

                // Item icon.
                GameObject iconGo = new GameObject("Icon");
                iconGo.transform.SetParent(r, false);
                RectTransform iconRect = iconGo.AddComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0f, 1f);
                iconRect.anchorMax = new Vector2(0f, 1f);
                iconRect.pivot = new Vector2(0f, 1f);
                iconRect.anchoredPosition = new Vector2(16f, -52f);
                iconRect.sizeDelta = new Vector2(84f, 84f);
                card.Icon = iconGo.AddComponent<Image>();
                card.Icon.preserveAspect = true;
                card.Icon.raycastTarget = false;

                // Item name (tinted by rarity), right of the icon: x 98..width, y -46..-124.
                card.Name = UiFactory.Label("Name", r, "--", _font, 20,
                    TextAnchor.UpperLeft, Color.white);
                RectTransform nameRect = card.Name.rectTransform;
                nameRect.anchorMin = new Vector2(0f, 1f);
                nameRect.anchorMax = new Vector2(1f, 1f);
                nameRect.pivot = new Vector2(0f, 1f);
                nameRect.sizeDelta = new Vector2(-110f, 84f);
                nameRect.anchoredPosition = new Vector2(110f, -52f);
                card.Name.horizontalOverflow = HorizontalWrapMode.Wrap;
                card.Name.verticalOverflow = VerticalWrapMode.Truncate;

                // Price, the loudest element on the card: full width, y -128..-170. The
                // 优惠 tag sits to its right for rolls in the cheap end of the band.
                card.Price = UiFactory.Label("Price", r, "--", _font, 34,
                    TextAnchor.MiddleCenter, ColPrice);
                RectTransform priceRect = card.Price.rectTransform;
                priceRect.anchorMin = new Vector2(0f, 1f);
                priceRect.anchorMax = new Vector2(1f, 1f);
                priceRect.pivot = new Vector2(0.5f, 1f);
                priceRect.sizeDelta = new Vector2(-20f, 48f);
                priceRect.anchoredPosition = new Vector2(0f, -146f);

                card.Deal = UiFactory.Label("Deal", r, string.Empty, _font, 15,
                    TextAnchor.MiddleCenter, ColDeal);
                RectTransform dealRect = card.Deal.rectTransform;
                dealRect.anchorMin = new Vector2(1f, 1f);
                dealRect.anchorMax = new Vector2(1f, 1f);
                dealRect.pivot = new Vector2(1f, 1f);
                dealRect.sizeDelta = new Vector2(46f, 22f);
                dealRect.anchoredPosition = new Vector2(-12f, -150f);
                card.Deal.raycastTarget = false;

                // Rarity + stack size + owned count: y -172..-260.
                card.Info = UiFactory.Label("Info", r, string.Empty, _font, 16,
                    TextAnchor.UpperCenter, ColDim);
                RectTransform infoRect = card.Info.rectTransform;
                infoRect.anchorMin = new Vector2(0f, 1f);
                infoRect.anchorMax = new Vector2(1f, 1f);
                infoRect.pivot = new Vector2(0.5f, 1f);
                infoRect.sizeDelta = new Vector2(-24f, 100f);
                infoRect.anchoredPosition = new Vector2(0f, -206f);
                card.Info.horizontalOverflow = HorizontalWrapMode.Wrap;
                card.Info.verticalOverflow = VerticalWrapMode.Truncate;

                // Buttons.
                // One full-width button: refreshing is a page-wide action now, so the button
                // lives in the footer instead of on every card.
                GameObject buy = UiFactory.Panel("Buy", r, ColBuy);
                card.BuyRect = (RectTransform)buy.transform;
                card.BuyRect.anchorMin = new Vector2(0f, 0f);
                card.BuyRect.anchorMax = new Vector2(1f, 0f);
                card.BuyRect.pivot = new Vector2(0.5f, 0f);
                card.BuyRect.sizeDelta = new Vector2(-32f, 46f);
                card.BuyRect.anchoredPosition = new Vector2(0f, 16f);
                card.BuyBg = buy.GetComponent<Image>();
                card.BuyLabel = UiFactory.Label("t", card.BuyRect, "购买", _font, 21,
                    TextAnchor.MiddleCenter, Color.white);
                UiFactory.Stretch(card.BuyLabel.rectTransform);

                int captured = i;
                RegisterClickable(card.BuyRect, delegate { OnBuyClicked(captured); }, ColBuy, ColBuyHot);
            }
        }

        private void BuildFooter(RectTransform win)
        {
            GameObject bar = UiFactory.Panel("Footer", win, ColHeader);
            RectTransform barRect = (RectTransform)bar.transform;
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(1f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.offsetMin = new Vector2(4f, 4f);
            barRect.offsetMax = new Vector2(-4f, 4f);
            barRect.sizeDelta = new Vector2(-8f, 60f);
            barRect.anchoredPosition = Vector2.zero;

            // [刷新] on the right, and the two text fields to its left.
            GameObject refresh = UiFactory.Panel("PageRefresh", barRect, ColRefresh);
            RectTransform refreshRect = (RectTransform)refresh.transform;
            refreshRect.anchorMin = new Vector2(1f, 0.5f);
            refreshRect.anchorMax = new Vector2(1f, 0.5f);
            refreshRect.pivot = new Vector2(1f, 0.5f);
            refreshRect.anchoredPosition = new Vector2(-14f, 0f);
            refreshRect.sizeDelta = new Vector2(300f, 48f);
            _refreshLabel = UiFactory.Label("t", refreshRect, RefreshButtonText(false), _font, 21,
                TextAnchor.MiddleCenter, Color.white);
            UiFactory.Stretch(_refreshLabel.rectTransform);
            RegisterClickable(refreshRect, OnPageRefreshClicked, ColRefresh, ColRefreshHot);

            _statusText = UiFactory.Label("Status", barRect, string.Empty, _font, 18,
                TextAnchor.MiddleRight, ColOk);
            UiFactory.AnchorStretch(_statusText.rectTransform, 0f, 0f, 280f, 0f);

            _hintText = UiFactory.Label("Hint", barRect, string.Empty, _font, 16,
                TextAnchor.MiddleLeft, ColDim);
            UiFactory.AnchorStretch(_hintText.rectTransform, 18f, 0f, 920f, 0f);
        }

        // ------------------------------------------------------------------- actions

        private void OnBuyClicked(int index)
        {
            ShopStock stock = _stock[index];
            if (stock == null)
            {
                FlashStatus("这个窗口暂时没有商品。", ColBad);
                return;
            }
            if (stock.SoldOut)
            {
                FlashStatus("这个窗口已经售罄，刷新后才能再买。", ColBad);
                PlayError();
                return;
            }

            DataHub data = GameRefs.Data;
            CurrentDataHub cur = GameRefs.Current;
            if (data == null || cur == null)
            {
                FlashStatus("读不到游戏数据，请先进入存档。", ColBad);
                return;
            }

            int total = stock.TotalPrice;
            int have = SafeGetGp();
            if (have < total)
            {
                FlashStatus("GP 不足：需要 " + total.ToString("N0") + "，当前 " + have.ToString("N0") + "。", ColBad);
                PlayError();
                return;
            }

            BagWriter.AddResult res = BagWriter.TryAdd(cur.myBag, stock.ModelId, stock.Count);
            bool toDepot = false;
            if (res == BagWriter.AddResult.NoSpace)
            {
                res = TryAddToAnyDepot(cur, stock.ModelId, stock.Count);
                toDepot = res == BagWriter.AddResult.Ok;
            }
            if (res != BagWriter.AddResult.Ok)
            {
                FlashStatus(res == BagWriter.AddResult.NoSpace
                    ? "背包和仓库都放不下了。"
                    : "购买失败（物品无效）。", ColBad);
                PlayError();
                return;
            }

            data.ChangeMyGP(-total);
            try
            {
                data.GetRecord(3, total);
            }
            catch (Exception ex)
            {
                RareRefreshPlugin.Log.LogWarning("记账失败: " + ex.Message);
            }

            stock.SoldOut = true;
            SaveAfterPurchase(toDepot);

            PlayUiSound(9017);
            FlashStatus("已买下 " + stock.DisplayName + " x" + stock.Count + "，花费 "
                + total.ToString("N0") + " GP" + (toDepot ? "（已放进仓库）" : "") + "。", ColOk);

            RefreshCard(index);
            RefreshGp();
            RefreshFooter();
        }

        /// <summary>
        /// Rerolls the whole page at once: every one of the ten windows gets a new item and a
        /// new price, and windows that were sold out come back into stock. This is the only
        /// refresh there is, so the price is charged once for the page rather than per window.
        /// </summary>
        private void OnPageRefreshClicked()
        {
            DataHub data = GameRefs.Data;
            if (data == null)
            {
                FlashStatus("读不到游戏数据，请先进入存档。", ColBad);
                return;
            }

            ItemPool.Build(true);

            int cost = RareRefreshPlugin.RefreshCost;
            int have = SafeGetGp();
            if (have < cost)
            {
                FlashStatus("GP 不足：刷新要 " + cost.ToString("N0")
                    + "，当前 " + have.ToString("N0") + "。", ColBad);
                PlayError();
                return;
            }

            // Roll the page first so a GP charge never happens without goods to show.
            ShopStock[] fresh = new ShopStock[WindowCount];
            int ok = 0;
            for (int i = 0; i < WindowCount; i++)
            {
                fresh[i] = ItemPool.Roll(PageModels(fresh, i));
                if (fresh[i] != null)
                {
                    ok++;
                }
            }
            if (ok == 0)
            {
                FlashStatus("这个分类暂时没有可用的商品，未扣费。", ColBad);
                PlayError();
                return;
            }

            data.ChangeMyGP(-cost);
            try
            {
                data.GetRecord(3, cost);
            }
            catch (Exception ex)
            {
                RareRefreshPlugin.Log.LogWarning("记账失败: " + ex.Message);
            }
            try
            {
                data.SaveRecord();
            }
            catch (Exception ex)
            {
                RareRefreshPlugin.Log.LogWarning("保存失败: " + ex.Message);
            }

            for (int i = 0; i < WindowCount; i++)
            {
                if (fresh[i] != null)
                {
                    _stock[i] = fresh[i];
                }
            }

            PlayUiSound(9012);
            FlashStatus("已刷新整页 " + ok + "/" + WindowCount + " 个窗口，花费 "                + cost.ToString("N0") + " GP。", ColAccent);

            RefreshAll();
        }

        /// <summary>
        /// Model ids already chosen for this page, so one refresh does not show the same item
        /// twice. Only the windows scanned so far are counted; <paramref name="self"/> and
        /// later windows are excluded.
        /// </summary>
        private static List<int> PageModels(ShopStock[] chosen, int self)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < self && i < chosen.Length; i++)
            {
                if (chosen[i] != null)
                {
                    result.Add(chosen[i].ModelId);
                }
            }
            return result;
        }

        /// <summary>Model ids currently offered by the other windows, to avoid duplicates.</summary>
        private List<int> OtherModels(int self)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < WindowCount; i++)
            {
                if (i == self || _stock[i] == null)
                {
                    continue;
                }
                result.Add(_stock[i].ModelId);
            }
            return result;
        }

        private static BagWriter.AddResult TryAddToAnyDepot(CurrentDataHub cur, int modelId, int count)
        {
            // Only depots A-F carry an unlock flag in the save file, so only those are safe.
            Dictionary<int, CurrentItemData>[] bags =
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
                if (r != BagWriter.AddResult.Ok)
                {
                    continue;
                }
                try
                {
                    GameRefs.Data.SaveDepot(i + 1);
                }
                catch (Exception ex)
                {
                    RareRefreshPlugin.Log.LogWarning("SaveDepot failed: " + ex.Message);
                }
                return r;
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
                RareRefreshPlugin.Log.LogError("购买后保存失败: " + ex);
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

        private void PlayError()
        {
            PlayUiSound(9002);
        }

        // -------------------------------------------------------------------- refresh

        private void RefreshAll()
        {
            for (int i = 0; i < WindowCount; i++)
            {
                RefreshCard(i);
            }
            RefreshGp();
            RefreshFooter();
        }

        private void RefreshGp()
        {
            if (_gpText != null)
            {
                _gpText.text = "GP: " + SafeGetGp().ToString("N0");
            }
        }

        private void RefreshFooter()
        {
            if (_hintText == null)
            {
                return;
            }
            int sold = 0;
            for (int i = 0; i < WindowCount; i++)
            {
                if (_stock[i] != null && _stock[i].SoldOut)
                {
                    sold++;
                }
            }
            _hintText.text = "已售罄 " + sold + "/" + WindowCount
                + "　·　按 " + RareRefreshPlugin.CfgToggleKey.Value + " 关闭";
            if (_refreshLabel != null)
            {
                _refreshLabel.text = RefreshButtonText(sold >= WindowCount);
            }
        }

        /// <summary>
        /// The refresh button states its own price, so the cost is never a guess:
        /// "刷新　50,000 GP".
        /// </summary>
        private static string RefreshButtonText(bool allSoldOut)
        {
            return (allSoldOut ? "刷新（已全售罄）　" : "刷新　")
                + RareRefreshPlugin.RefreshCost.ToString("N0") + " GP";
        }

        private void RefreshCard(int index)
        {
            Card card = _cards[index];
            if (card == null)
            {
                return;
            }

            ShopStock stock = _stock[index];
            if (stock == null)
            {
                card.Header.text = "#" + (index + 1);
                card.Name.text = "<color=#8A8A8A>暂无数据</color>";
                card.Price.text = "--";
                card.Deal.text = string.Empty;
                card.Info.text = GameRefs.IsSaveLoaded()
                    ? "这张卡片暂时没有可用商品"
                    : "请先进入存档";
                card.Icon.sprite = null;
                card.Icon.color = new Color(1f, 1f, 1f, 0.05f);
                card.Bg.color = ColCardOut;
                card.BuyBg.color = ColDisabled;
                card.BuyLabel.text = "购买";
                SetIcon(card, 0);
                return;
            }

            // The band shows this card's own category, which was drawn for this roll.
            card.Header.text = "#" + (index + 1) + "  " + ShopCatalog.NameOf(stock.Kind);

            // Name carries the rarity colour.
            card.Name.text = "<color=#" + ShopCatalog.TierHex(stock.Tier) + ">"
                + stock.DisplayName + "</color>";
            SetIcon(card, stock.ModelId);

            if (stock.SoldOut)
            {
                card.Price.text = "<color=#7A7A7A>已售罄</color>";
                card.Deal.text = string.Empty;
                card.Info.text = "本窗口商品已售罄\n点下方「刷新」花 "
                    + RareRefreshPlugin.RefreshCost.ToString("N0") + " GP 重掷整页";
                card.Bg.color = ColCardOut;
                card.Icon.color = new Color(1f, 1f, 1f, 0.30f);
                card.BuyBg.color = ColDisabled;
                card.BuyLabel.text = "已售罄";
                return;
            }

            card.Price.text = stock.TotalPrice.ToString("N0") + " GP";
            card.Deal.text = stock.IsDeal ? "优惠" : string.Empty;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("稀有度 ").Append(Stars(stock.Tier)).Append(' ').Append(ShopCatalog.TierName(stock.Tier));
            sb.Append('\n');
            sb.Append("数量 x ").Append(stock.Count);
            if (stock.Count > 1)
            {
                sb.Append("（堆叠上限）");
            }
            sb.Append('\n');
            sb.Append("已有 ").Append(stock.Owned);
            card.Info.text = sb.ToString();

            card.Bg.color = ItemTierTint(stock.Tier);
            card.Icon.color = Color.white;
            card.BuyBg.color = ColBuy;
            card.BuyLabel.text = "购买";
        }

        private static string Stars(int tier)
        {
            if (tier <= 0)
            {
                return "-----";
            }
            int t = Mathf.Clamp(tier, 1, RarityPalette.MaxTier);
            return new string('★', t) + new string('☆', RarityPalette.MaxTier - t);
        }

        /// <summary>Card background: the rarity colour, dimmed so white text stays readable.</summary>
        private static Color ItemTierTint(int tier)
        {
            if (tier <= 0)
            {
                return ColCard;
            }
            float b = RarityPalette.ShopBrightness;
            Color c = RarityPalette.RawColor(tier);
            return new Color(
                Mathf.Lerp(ColCard.r, c.r, b),
                Mathf.Lerp(ColCard.g, c.g, b),
                Mathf.Lerp(ColCard.b, c.b, b),
                1f);
        }

        private static void SetIcon(Card card, int modelId)
        {
            Sprite icon = null;
            if (modelId > 0)
            {
                try
                {
                    ResourcesHub hub = GameRefs.Resources;
                    if (hub != null && hub.imageDic != null)
                    {
                        hub.imageDic.TryGetValue(modelId, out icon);
                    }
                }
                catch
                {
                    icon = null;
                }
            }
            card.Icon.sprite = icon;
            if (icon == null)
            {
                card.Icon.color = new Color(1f, 1f, 1f, 0.06f);
            }
        }

        // ---------------------------------------------------------------------- status

        private void FlashStatus(string message, Color color)
        {
            _status = message;
            _statusUntil = Time.unscaledTime + 7f;
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

        // ----------------------------------------------------------------------- input

        private void Update()
        {
            if (_root == null || !_root.activeSelf)
            {
                return;
            }
            HandleClick();
            UpdateStatus();
        }

        private void RegisterClickable(RectTransform rect, Action onClick, Color normal, Color hover)
        {
            Widget w = new Widget();
            w.Rect = rect;
            w.Image = rect.GetComponent<Image>();
            w.OnClick = onClick;
            w.Normal = normal;
            w.Hover = hover;
            _widgets[_nextWidgetId++] = w;
        }

        private void HandleClick()
        {
            Vector2 mouse = Input.mousePosition;
            Widget hit = FindWidgetByRect(mouse);

            foreach (KeyValuePair<int, Widget> kv in _widgets)
            {
                Widget w = kv.Value;
                if (w.Image == null || w.OnClick == null)
                {
                    continue;   // leave the backdrop and labels alone
                }
                w.Image.color = (hit == w) ? w.Hover : w.Normal;
            }

            if (Input.GetMouseButtonDown(0) && hit != null && hit.OnClick != null)
            {
                hit.OnClick();
            }
        }

        /// <summary>
        /// Manual hit test. The game has an EventSystem, but resolving the click ourselves
        /// keeps the buttons working regardless of what the game does with focus or with its
        /// own raycasters. Deeper widgets win, so a button beats the card behind it.
        /// </summary>
        private Widget FindWidgetByRect(Vector2 screenPoint)
        {
            Widget best = null;
            int bestDepth = -1;
            foreach (KeyValuePair<int, Widget> kv in _widgets)
            {
                Widget w = kv.Value;
                if (w.Rect == null || w.OnClick == null)
                {
                    continue;
                }
                Canvas canvas = w.Rect.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    ? canvas.worldCamera : null;
                if (!RectTransformUtility.RectangleContainsScreenPoint(w.Rect, screenPoint, cam))
                {
                    continue;
                }
                int depth = Depth(w.Rect);
                if (best == null || depth > bestDepth)
                {
                    best = w;
                    bestDepth = depth;
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
