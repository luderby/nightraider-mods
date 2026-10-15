using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRaider.RareRefresh
{
    /// <summary>
    /// The kinds of goods a window can offer. The game has no single category field, so a kind
    /// is derived from (modelID range, ItemData.itemTyep) combined with the game's own
    /// functional item lists — ammoList, medicTypeList, Item_UseBookList, cardTypeList,
    /// fileTypeList — plus the sell-only treasure set.
    /// See <see cref="ItemPool.TryClassify"/> for the exact order and evidence.
    /// </summary>
    internal enum ShopKind
    {
        Weapon = 0,
        WeaponPart = 1,
        Material = 2,
        Gear = 3,
        Ammo = 4,
        Medicine = 5,
        Food = 6,
        Throwable = 7,
        Book = 8,
        KeyItem = 9,
        Collectible = 10
    }

    /// <summary>
    /// One window's current offer: one item, one price, one lot size, and whether it has
    /// already been bought out.
    /// </summary>
    internal sealed class ShopStock
    {
        internal int ModelId;
        internal string DisplayName;
        internal ShopKind Kind;

        internal int Tier;              // rarity tier from the CSV, 1..5
        internal int SellPrice;         // the item's own sell price (price / 2)

        internal float Multiplier;      // rolled inside this tier's price band
        internal int UnitPrice;         // SellPrice * Multiplier, rounded up
        internal int Count;             // lot size: maxStack for stackables, else 1
        internal bool SoldOut;

        /// <summary>
        /// True when the roll landed in the cheaper end of its tier's band. The band is always
        /// 10x wide and its lower 3x is the discount window (green 15-18, blue 20-23, ...).
        /// </summary>
        internal bool IsDeal
        {
            get { return Multiplier < ItemPool.DealMax(Tier); }
        }

        internal int TotalPrice { get { return UnitPrice * Mathf.Max(1, Count); } }

        /// <summary>How many of this item the player already owns, for the card footer.</summary>
        internal int Owned
        {
            get
            {
                if (!GameRefs.IsSaveLoaded())
                {
                    return 0;
                }
                try
                {
                    return GameRefs.Current.CheckAllDepotHaveSomeThing(ModelId);
                }
                catch
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>
    /// Builds the tier-filtered pools a window can draw from, and rolls the random offers.
    ///
    /// Rarity is not in the game at all (no rarity/quality/grade member exists on ItemData
    /// or CurrentItemData), so it comes from the editable CSV shared with the item-shop mod:
    ///
    ///     BepInEx/config/NightRaider_item_tiers.csv
    ///
    /// If that table is missing every item reads as tier 0 and the pools fall back to
    /// "everything in the category", which still produces a working shop.
    /// </summary>
    internal static class ItemPool
    {
        private static readonly System.Random Rng = new System.Random();

        // ---------------------------------------------------------------- price bands
        // Each rarity tier has its own 10x-wide multiplier band, and the lower 3x of every
        // band is the discount window (green 15-18, blue 20-23, purple 25-28, orange 30-33).
        // Both halves are editable in the config.
        internal const float BandWidth = 10f;
        internal const float DealWidth = 3f;

        /// <summary>
        /// Candidate items per kind per rarity tier: Pools[kind][tier]. Keeping the tiers
        /// separate is what lets a roll honour the configured rarity weights exactly, instead
        /// of picking uniformly and hoping the resulting mix looks right.
        /// </summary>
        private static readonly List<int>[][] Pools = NewPools();

        private static bool _built;

        private static List<int>[][] NewPools()
        {
            List<int>[][] pools = new List<int>[ShopCatalog.KindCount][];
            for (int k = 0; k < pools.Length; k++)
            {
                pools[k] = new List<int>[RarityPalette.MaxTier + 1];
                for (int t = 1; t <= RarityPalette.MaxTier; t++)
                {
                    pools[k][t] = new List<int>();
                }
            }
            return pools;
        }

        private static List<int> PoolFor(ShopKind kind, int tier)
        {
            int k = (int)kind;
            if (k < 0 || k >= Pools.Length || tier < 1 || tier > RarityPalette.MaxTier)
            {
                return null;
            }
            return Pools[k][tier];
        }

        /// <summary>
        /// How many items one category currently holds at one tier. Read-only; used by the
        /// standalone diagnostic probe to report the pool make-up.
        /// </summary>
        internal static int CountIn(ShopKind kind, int tier)
        {
            List<int> pool = PoolFor(kind, tier);
            return pool != null ? pool.Count : 0;
        }

        /// <summary>Which tiers this kind actually has items in, given the configured floor.</summary>
        private static List<int> AvailableTiers(ShopKind kind)
        {
            List<int> tiers = new List<int>();
            for (int t = 1; t <= RarityPalette.MaxTier; t++)
            {
                List<int> pool = PoolFor(kind, t);
                if (pool != null && pool.Count > 0)
                {
                    tiers.Add(t);
                }
            }
            return tiers;
        }

        internal static void Build(bool force)
        {
            if (_built && !force)
            {
                return;
            }
            _built = true;

            for (int k = 0; k < ShopCatalog.KindCount; k++)
            {
                for (int t = 1; t <= RarityPalette.MaxTier; t++)
                {
                    Pools[k][t].Clear();
                }
            }

            DataHub data = GameRefs.Data;
            if (data == null || data.itemDataDic == null)
            {
                return;
            }

            foreach (KeyValuePair<int, ItemData> kv in data.itemDataDic)
            {
                int id = kv.Key;
                ItemData model = kv.Value;
                if (model == null || id <= 1)
                {
                    continue;
                }

                ShopKind kind;
                if (!TryClassify(id, model, out kind))
                {
                    continue;
                }
                if (SellPriceFor(model) <= 0)
                {
                    continue;
                }

                int tier = Mathf.Clamp(ItemTiers.TierOf(id), 0, RarityPalette.MaxTier);
                if (tier < 1)
                {
                    continue;   // unassigned in the table; not eligible for a rarity weight
                }

                Pools[(int)kind][tier].Add(id);
            }

            // A kind with nothing at all would show an empty window; say so in the log.
            for (int k = 0; k < ShopCatalog.KindCount; k++)
            {
                ShopKind kind = (ShopKind)k;
                if (AvailableTiers(kind).Count == 0)
                {
                    RareRefreshPlugin.Log.LogWarning(
                        ShopCatalog.NameOf(kind) + "：没有任何可用物品，该分类的窗口会空着。");
                }
            }

            if (RareRefreshPlugin.CfgDebugLog != null && RareRefreshPlugin.CfgDebugLog.Value)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder("候选池：");
                for (int k = 0; k < ShopCatalog.KindCount; k++)
                {
                    ShopKind kind = (ShopKind)k;
                    sb.Append(ShopCatalog.NameOf(kind)).Append('=');
                    for (int t = 1; t <= RarityPalette.MaxTier; t++)
                    {
                        int n = Pools[k][t].Count;
                        if (n > 0)
                        {
                            sb.Append('t').Append(t).Append(':').Append(n).Append(' ');
                        }
                    }
                    sb.Append("  ");
                }
                RareRefreshPlugin.Log.LogInfo(sb.ToString());
            }
        }

        /// <summary>
        /// Rolls a brand new offer for one window: the category itself is drawn at random,
        /// then the rarity tier by weight, then an item from that tier.
        ///
        /// Drawing the category per card rather than mapping it to a fixed window is what lets
        /// the category pool be larger than the ten windows — every card independently picks
        /// one of <see cref="ShopCatalog.KindCount"/> kinds, so one page can show a category
        /// twice or not at all.
        ///
        /// The rarity tier is drawn first (green 40%, blue 30%, purple 20%, orange 10% by
        /// default) because drawing it before picking the item is what makes the advertised
        /// percentages hold regardless of how many items happen to sit in each tier. A tier
        /// the drawn category has nothing in is re-drawn across the tiers it does have.
        /// </summary>
        internal static ShopStock Roll(List<int> exclude)
        {
            Build(false);

            DataHub data = GameRefs.Data;
            if (data == null)
            {
                return null;
            }

            // Two attempts at the category: if the drawn one somehow has no stock at all, a
            // second, different draw is tried before giving up on this card.
            for (int kindAttempt = 0; kindAttempt < 2; kindAttempt++)
            {
                ShopKind kind = (ShopKind)Rng.Next(ShopCatalog.KindCount);
                ShopStock stock = RollInKind(kind, data, exclude);
                if (stock != null)
                {
                    return stock;
                }
            }
            return null;
        }

        /// <summary>Rolls an offer from one specific category.</summary>
        private static ShopStock RollInKind(ShopKind kind, DataHub data, List<int> exclude)
        {
            List<int> tiers = AvailableTiers(kind);
            if (tiers.Count == 0)
            {
                return null;
            }

            int modelId = 0;
            int chosenTier = 0;
            for (int attempt = 0; attempt < 24 && modelId <= 0; attempt++)
            {
                int tier = RollTier(tiers);
                List<int> pool = PoolFor(kind, tier);
                if (pool == null || pool.Count == 0)
                {
                    continue;
                }
                int pick = PickModel(pool, exclude);
                if (pick > 0)
                {
                    modelId = pick;
                    chosenTier = tier;
                }
            }
            if (modelId <= 0)
            {
                return null;
            }

            ItemData model;
            if (!data.itemDataDic.TryGetValue(modelId, out model) || model == null)
            {
                return null;
            }

            int sell = SellPriceFor(model);
            if (sell <= 0)
            {
                return null;
            }

            ShopStock stock = new ShopStock();
            stock.ModelId = modelId;
            stock.Kind = kind;
            // The tier the draw landed on is authoritative: the price band must match the
            // colour the player sees, so this is not re-read from the table.
            stock.Tier = chosenTier;
            stock.DisplayName = LookupName(modelId);
            stock.SellPrice = sell;
            stock.Multiplier = RollMultiplier(chosenTier);
            stock.UnitPrice = Mathf.Max(1, Mathf.CeilToInt(sell * stock.Multiplier));
            stock.Count = LotSize(model);
            stock.SoldOut = false;
            return stock;
        }

        /// <summary>
        /// Draws a rarity tier using the configured weights, restricted to the tiers the
        /// category actually has stock in. Weights of tiers that are unavailable are dropped
        /// and the rest renormalised, so 40/30/20/10 comes out as advertised while every
        /// category still gets a full spread.
        /// </summary>
        private static int RollTier(List<int> available)
        {
            float total = 0f;
            for (int i = 0; i < available.Count; i++)
            {
                total += RareRefreshPlugin.TierWeight(available[i]);
            }

            if (total <= 0f)
            {
                // Every available tier is weighted 0: fall back to a flat draw rather than
                // refusing to sell anything.
                return available[Rng.Next(available.Count)];
            }

            double pick = Rng.NextDouble() * total;
            double acc = 0.0;
            for (int i = 0; i < available.Count; i++)
            {
                acc += RareRefreshPlugin.TierWeight(available[i]);
                if (pick < acc)
                {
                    return available[i];
                }
            }
            return available[available.Count - 1];
        }

        /// <summary>Picks a model id, preferring one no other window is currently offering.</summary>
        private static int PickModel(List<int> pool, List<int> exclude)
        {
            if (pool.Count == 0)
            {
                return 0;
            }
            if (exclude == null || exclude.Count == 0)
            {
                return pool[Rng.Next(pool.Count)];
            }

            // Bounded rejection sampling: with ~50+ candidates in the tightest pool a few
            // dozen tries almost always finds a distinct item, and the fallback guarantees
            // termination either way.
            for (int attempt = 0; attempt < 48; attempt++)
            {
                int candidate = pool[Rng.Next(pool.Count)];
                if (!exclude.Contains(candidate))
                {
                    return candidate;
                }
            }
            return pool[Rng.Next(pool.Count)];
        }

        /// <summary>Lower bound of a tier's multiplier band (green 15, blue 20, purple 25, orange 30).</summary>
        internal static float BandMin(int tier)
        {
            return RareRefreshPlugin.BandMin(tier);
        }

        /// <summary>Upper bound of a tier's multiplier band, exclusive in practice.</summary>
        internal static float BandMax(int tier)
        {
            return BandMin(tier) + BandWidth;
        }

        /// <summary>Upper bound of the discount (优惠) window: the band's lower 3x.</summary>
        internal static float DealMax(int tier)
        {
            return BandMin(tier) + DealWidth;
        }

        /// <summary>
        /// Rolls the multiplier inside the given tier's band, with a fractional part so prices
        /// differ between rolls.
        /// </summary>
        private static float RollMultiplier(int tier)
        {
            float lo = BandMin(tier);
            float value = lo + (float)Rng.NextDouble() * BandWidth;
            return (float)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// How many units one purchase is: one full stack, i.e. the game's own ItemData.maxStack
        /// for that item (ammo 200/120/70/..., consumables by their stack size). Items that do
        /// not stack have maxStack 1, so they are simply bought one at a time.
        /// </summary>
        private static int LotSize(ItemData model)
        {
            return Mathf.Max(1, model.maxStack);
        }

        /// <summary>
        /// The price the game itself shows when the player sells the item:
        /// CurrentItemData.GetSellPrice() is ItemData.price, and the trade UI hands over half
        /// of it (MyBagControl.cs:6356 — GetSellPrice() * 0.5f).
        /// </summary>
        internal static int SellPriceFor(ItemData model)
        {
            if (model == null)
            {
                return 0;
            }
            return Mathf.Max(0, Mathf.CeilToInt(model.price * 0.5f));
        }

        /// <summary>Display name in the active language, or "#id" when the table has none.</summary>
        internal static string LookupName(int modelId)
        {
            try
            {
                TextManager text = GameRefs.Text;
                if (text != null)
                {
                    string name = text.GetTextByItems(modelId);
                    if (!string.IsNullOrEmpty(name) && name.Trim().Length > 0)
                    {
                        return name.Trim();
                    }
                }
            }
            catch
            {
                // fall through to the id
            }
            return "#" + modelId;
        }

        /// <summary>
        /// Which kind an item belongs to, or false when it must not be sold here.
        ///
        /// The test order matters: the game's own functional lists overlap heavily (a
        /// bandage is in both medicTypeList and toolTypeList; the drone is in both toolTypeList
        /// and throwList; 硝酸甘油 is in both medicTypeList and toolTypeList). The order below
        /// goes from most specific to least, matching how a player would name the item:
        ///
        ///   1. 任务文件  — blocked outright (see IsQuestPaper)
        ///   2. 武器 1000..1999 itemTyep 1
        ///   3. 配件 2000..2999 itemTyep 3
        ///   4. 装备 5000..5999 itemTyep 2
        ///   5. 子弹   ammoList
        ///   6. 书籍   Item_UseBookList (skill books, manuals, blueprints — all tier 4-5)
        ///   7. 药品   medicTypeList
        ///   8. 食物   foodTypeList
        ///   9. 投掷物 throwList (grenades, launchers, drones)
        ///  10. 钥匙证件 cardTypeList / fileTypeList (keys, door cards, bank cards, IDs)
        ///  11. 收藏品 the sell-only treasure set
        ///  12. 材料   everything else in the itemTyep 4 block
        /// </summary>
        internal static bool TryClassify(int id, ItemData model, out ShopKind kind)
        {
            kind = ShopKind.Material;

            // Quest papers must never be purchasable: see IsQuestPaper.
            if (IsQuestPaper(id))
            {
                return false;
            }

            if (model.itemTyep == 1 && id >= 1000 && id <= 1999)
            {
                kind = ShopKind.Weapon;
                return true;
            }
            if (model.itemTyep == 3 && id >= 2000 && id <= 2999)
            {
                kind = ShopKind.WeaponPart;
                return true;
            }
            if (model.itemTyep == 2 && id >= 5000 && id <= 5999)
            {
                kind = ShopKind.Gear;
                return true;
            }

            if (model.itemTyep == 4 && id >= 3000 && id <= 4999)
            {
                if (InList(GameList.Ammo, id))
                {
                    kind = ShopKind.Ammo;
                    return true;
                }
                if (InList(GameList.Book, id))
                {
                    kind = ShopKind.Book;
                    return true;
                }
                if (InList(GameList.Medicine, id))
                {
                    kind = ShopKind.Medicine;
                    return true;
                }
                if (InList(GameList.Food, id))
                {
                    kind = ShopKind.Food;
                    return true;
                }
                if (InList(GameList.Throwable, id))
                {
                    kind = ShopKind.Throwable;
                    return true;
                }
                if (InList(GameList.KeyItem, id))
                {
                    kind = ShopKind.KeyItem;
                    return true;
                }
                if (IsCollectible(id))
                {
                    kind = ShopKind.Collectible;
                    return true;
                }
                kind = ShopKind.Material;
                return true;
            }

            return false;
        }

        /// <summary>Which of the game's own functional lists to look at.</summary>
        internal enum GameList
        {
            Ammo,
            Medicine,
            Food,
            Throwable,
            Book,
            KeyItem
        }

        /// <summary>
        /// Reads one of the game's own item lists (DataHub), each with a documented id-range
        /// fallback for the case where the manager is not readable yet.
        ///
        /// The lists that carry real weight here: ammoList (56 rounds), medicTypeList (129),
        /// foodTypeList (98), Item_UseBookList (25 skill books/manuals/blueprints, all
        /// tier 4-5), throwList (20), and cardTypeList + fileTypeList (keys, door cards, bank
        /// cards, IDs).
        /// </summary>
        private static bool InList(GameList which, int id)
        {
            DataHub data = null;
            try
            {
                data = GameRefs.Data;
            }
            catch
            {
                data = null;
            }

            if (data != null)
            {
                try
                {
                    switch (which)
                    {
                        case GameList.Ammo:
                            if (data.ammoList != null) return data.ammoList.Contains(id);
                            break;
                        case GameList.Medicine:
                            if (data.medicTypeList != null) return data.medicTypeList.Contains(id);
                            break;
                        case GameList.Food:
                            if (data.foodTypeList != null) return data.foodTypeList.Contains(id);
                            break;
                        case GameList.Throwable:
                            if (data.throwList != null) return data.throwList.Contains(id);
                            break;
                        case GameList.Book:
                            if (data.Item_UseBookList != null) return data.Item_UseBookList.Contains(id);
                            break;
                        case GameList.KeyItem:
                            bool c = data.cardTypeList != null && data.cardTypeList.Contains(id);
                            bool f = data.fileTypeList != null && data.fileTypeList.Contains(id);
                            return c || f;
                    }
                }
                catch
                {
                    // fall through to the range test
                }
            }

            // Fallbacks. Only reached when the manager is unreadable; they are deliberately
            // conservative so a missing list can never invent a category for a random item.
            switch (which)
            {
                case GameList.Ammo:
                    // Both ammo blocks are verified to contain nothing but ammunition.
                    return (id >= 4000 && id <= 4033) || (id >= 4680 && id <= 4701);
                case GameList.Medicine:
                    return id >= 3000 && id <= 3054;
                case GameList.Food:
                    return id >= 3070 && id <= 3175;
                case GameList.Throwable:
                    return id >= 3055 && id <= 3062;
                case GameList.Book:
                    return id >= 4645 && id <= 4670;
                case GameList.KeyItem:
                    return id >= 4034 && id <= 4056;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Mission papers, which are deliberately kept out of the shop.
        ///
        /// The game has no "quest item" flag, so these are named explicitly. They are the
        /// documents and credentials the mission system hands in — the 机密文件 / 情报文件
        /// family plus the supply voucher — all of which sit in both DataHub.cardTypeList and
        /// DataHub.fileTypeList. Ordinary keys, door cards and bank cards are intentionally NOT
        /// blocked: they are useful purchases and are not turned in.
        /// </summary>
        private static readonly int[] QuestPaperIds =
        {
            4554,   // 企业机密文件
            4555,   // 军事机密文件
            4556,   // 幸存者基地情报文件
            4557,   // D堡研究所绝密文件
            4679,   // 监狱情报文件
            4735    // 物资凭证
        };

        internal static bool IsQuestPaper(int id)
        {
            for (int i = 0; i < QuestPaperIds.Length; i++)
            {
                if (QuestPaperIds[i] == id)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The 收藏品 set: items that exist only to be sold on. Mirrors the game's own
        /// treasure-dealer lists (Goods_Luxury_Low/Hight, Goods_Decorate_Low/High, Goods_Figma)
        /// — jewellery, bullion, watches, statues, trophies, medals, figurines, artwork and the
        /// high-end anniversary pieces. 61 items.
        /// </summary>
        private static readonly int[] CollectibleIds =
        {
            4382, 4383, 4384, 4385, 4386, 4387, 4388, 4389, 4390, 4391,
            4392, 4393, 4394, 4395, 4397, 4398, 4399, 4400, 4401, 4402,
            4403, 4404, 4405, 4406, 4407, 4408, 4484, 4485, 4486, 4487,
            4488, 4489, 4505, 4506, 4631, 4632, 4633, 4636, 4637, 4639,
            4640, 4702, 4703, 4704, 4705, 4706, 4707, 4708, 4709, 4710,
            4711, 4712, 4713, 4714, 4715, 4716, 4733, 4750, 4752, 4753,
            4765
        };

        internal static bool IsCollectible(int id)
        {
            for (int i = 0; i < CollectibleIds.Length; i++)
            {
                if (CollectibleIds[i] == id)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when the id is in the game's own medicine list (DataHub.medicTypeList), which
        /// also carries the medical lab equipment and chemical reagents. Falls back to the
        /// 3000..3054 block when the list cannot be read (those ids are all medicine).
        /// </summary>
        internal static bool IsMedicine(int id)
        {
            try
            {
                DataHub data = GameRefs.Data;
                if (data != null && data.medicTypeList != null)
                {
                    return data.medicTypeList.Contains(id);
                }
            }
            catch
            {
                // fall through to the id-range test below
            }
            return id >= 3000 && id <= 3054;
        }

        /// <summary>True when the id is in the game's own ammo list (DataHub.ammoList).</summary>
        internal static bool IsAmmo(int id)
        {
            try
            {
                DataHub data = GameRefs.Data;
                if (data != null && data.ammoList != null)
                {
                    return data.ammoList.Contains(id);
                }
            }
            catch
            {
                // fall through to the id-range test below
            }

            // Only reached when ammoList could not be read. Both blocks are verified to
            // contain nothing but ammunition, so the fallback never disagrees with
            // ammoList: 4000..4033 is the main block (9mm through RPG rounds) and
            // 4680..4701 is the advanced block (RIP/HPB/FMJ/HAP, 8.6mm, 14.5mm, 20mm, 40mm
            // fragment). Missing the second block would silently file 22 advanced rounds
            // under 高级材料 instead of 高级子弹.
            return (id >= 4000 && id <= 4033) || (id >= 4680 && id <= 4701);
        }
    }

    /// <summary>Small shared helpers for the kinds.</summary>
    internal static class ShopCatalog
    {
        internal const int KindCount = 11;
        internal const int WindowCount = 10;

        private static readonly string[] Names =
        {
            "武器", "配件", "材料", "装备", "子弹", "药品",
            "食物", "投掷物", "书籍", "钥匙证件", "收藏品"
        };

        private static readonly string[] Short =
        {
            "武器", "配件", "材料", "装备", "子弹", "药品",
            "食物", "投掷", "书籍", "钥匙", "收藏"
        };

        internal static string NameOf(ShopKind kind)
        {
            int i = (int)kind;
            return (i >= 0 && i < Names.Length) ? Names[i] : "未知";
        }

        internal static string ShortOf(ShopKind kind)
        {
            int i = (int)kind;
            return (i >= 0 && i < Short.Length) ? Short[i] : "?";
        }

        /// <summary>
        /// Legacy helper kept only for the diagnostic probe: reports the kind a window would
        /// sell if categories were fixed. The panel no longer uses a fixed mapping — each card
        /// draws its own category on every roll (see ItemPool.Roll), which is what lets the
        /// category pool be larger than the ten windows.
        /// </summary>
        internal static ShopKind KindForWindow(int index)
        {
            int i = ((index % KindCount) + KindCount) % KindCount;
            return (ShopKind)i;
        }

        internal static string TierName(int tier)
        {
            return tier > 0 ? RarityPalette.NameOf(tier) : "未分级";
        }

        internal static string TierHex(int tier)
        {
            return tier > 0 ? RarityPalette.HexOf(tier) : "C9C9C9";
        }
    }
}
