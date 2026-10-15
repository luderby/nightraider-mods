using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Real item category. The values must line up with the TabNames table in ShopPanel,
    /// and there is deliberately no "All" member: the All tab is a UI-only filter that
    /// shows every category (it is TabNames.Length - 1), so the two can never drift apart.
    ///
    /// Categories follow the game's own modelID layout:
    ///   1000-1999 itemTyep 1 = weapon
    ///   2000-2999 itemTyep 3 = weapon attachment / mod
    ///   5000-5999 itemTyep 2 = wearable gear (helmet / armour / boots / suits)
    ///   3000-3999 itemTyep 4, itemPos = subtype (1 medicine, 2 throwable, 3 food, 4 tool)
    ///   4000-4999 itemTyep 4 = material, ammo, tool, key, document
    /// </summary>
    internal enum ShopCategory
    {
        Weapon = 0,
        WeaponPart = 1,
        Gear = 2,
        Medicine = 3,
        Food = 4,
        Material = 5
    }

    /// <summary>
    /// One purchasable entry in the shop.
    /// </summary>
    internal sealed class ShopEntry
    {
        internal int ModelId;
        internal string DisplayName;
        internal string DisplayNameEn;   // used by the search box so English names work too
        internal int SellPrice;      // the price the game shows when the player sells this item
        internal int MaxStack;
        internal int GridSize;
        internal ShopCategory Category;
        internal int BuyAmount;      // how many the player currently wants to buy

        /// <summary>Price for a single unit at the given multiplier.</summary>
        internal int UnitPrice(float multiplier)
        {
            float v = SellPrice * multiplier;
            if (v < 1f)
            {
                v = 1f;
            }
            if (v > 999999999f)
            {
                v = 999999999f;
            }
            return (int)Math.Ceiling(v);
        }

        /// <summary>Total price for the current buy amount.</summary>
        internal int TotalPrice(float multiplier)
        {
            long total = (long)UnitPrice(multiplier) * Mathf.Max(1, BuyAmount);
            if (total > 999999999L)
            {
                total = 999999999L;
            }
            return (int)total;
        }
    }

    /// <summary>
    /// Builds the purchasable item list out of the game's own item table and
    /// classifies each entry, filtering out items that have no use other than
    /// being sold for GP.
    /// </summary>
    internal static class ItemCatalog
    {
        /// <summary>
        /// Builds the full shop catalog. Every item in the game's item table that has a
        /// name and a price is listed, including the ones whose only use is selling for
        /// GP; the user asked for the complete list and can find what they want with the
        /// search box.
        /// </summary>
        internal static List<ShopEntry> Build(int defaultBuyAmount)
        {
            List<ShopEntry> result = new List<ShopEntry>();

            DataHub data = GameRefs.Data;
            if (data == null || data.itemDataDic == null)
            {
                return result;
            }

            TextManager text = GameRefs.Text;

            foreach (KeyValuePair<int, ItemData> kv in data.itemDataDic)
            {
                int id = kv.Key;
                ItemData model = kv.Value;
                if (model == null || id <= 1)
                {
                    continue;
                }

                ShopCategory category;
                if (!TryClassify(id, model, out category))
                {
                    continue;
                }

                string name;
                string nameEn = null;
                try
                {
                    name = text != null ? text.GetTextByItems(id) : null;
                    nameEn = LookupAlternateName(text, id);
                }
                catch
                {
                    name = null;
                }
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                name = name.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                int sellPrice = SellPriceFor(model);
                if (sellPrice <= 0)
                {
                    continue;
                }

                ShopEntry entry = new ShopEntry();
                entry.ModelId = id;
                entry.DisplayName = name;
                entry.DisplayNameEn = nameEn;
                entry.SellPrice = sellPrice;
                entry.MaxStack = Mathf.Max(1, model.maxStack);
                entry.GridSize = Mathf.Max(1, model.gridX) * Mathf.Max(1, model.gridY);
                entry.Category = category;
                entry.BuyAmount = Mathf.Clamp(defaultBuyAmount, 1, entry.MaxStack);
                result.Add(entry);
            }

            result.Sort(CompareEntries);
            return result;
        }

        private static int CompareEntries(ShopEntry a, ShopEntry b)
        {
            int c = a.Category.CompareTo(b.Category);
            if (c != 0)
            {
                return c;
            }
            c = b.SellPrice.CompareTo(a.SellPrice);
            if (c != 0)
            {
                return c;
            }
            return a.ModelId.CompareTo(b.ModelId);
        }

        /// <summary>
        /// Returns the item name in English regardless of the active game language, so the
        /// search box can match English names too. The game's name tables are private and
        /// selected by GameManager.language, so this flips the language for one lookup and
        /// restores it immediately. Runs on the main thread during catalog rebuild.
        /// </summary>
        private static string LookupAlternateName(TextManager text, int id)
        {
            if (text == null)
            {
                return null;
            }
            GameManager gm = GameRefs.Game;
            if (gm == null)
            {
                return null;
            }
            int original = gm.language;
            if (original == 1)
            {
                return null;   // already English, nothing extra to match
            }
            string result = null;
            try
            {
                gm.language = 1;
                string en = text.GetTextByItems(id);
                if (!string.IsNullOrEmpty(en) && en.Trim().Length > 0 && en != " ")
                {
                    result = en.Trim();
                }
            }
            catch
            {
                result = null;
            }
            finally
            {
                gm.language = original;
            }
            return result;
        }

        /// <summary>
        /// Reproduces the game's own sell price so the shop multiplier applies to
        /// the same number the player sees at the recycling dealer:
        /// CurrentItemData.GetSellPrice() returns the item's price (plus fitted parts
        /// and loaded ammo for firearms), and the trade UI shows 50% of it
        /// (MyBagControl.cs:6356 - GetSellPrice() * 0.5f).
        /// </summary>
        internal static int SellPriceFor(ItemData model)
        {
            if (model == null)
            {
                return 0;
            }
            return Mathf.Max(1, Mathf.CeilToInt(model.price * 0.5f));
        }

        private static bool TryClassify(int id, ItemData model, out ShopCategory category)
        {
            if (id >= 1000 && id <= 1999)
            {
                category = ShopCategory.Weapon;
                return true;
            }
            if (id >= 2000 && id <= 2999)
            {
                category = ShopCategory.WeaponPart;
                return true;
            }
            if (id >= 3000 && id <= 3999)
            {
                // In this block itemPos is the subtype:
                // 1 = medicine, 2 = throwable/support, 3 = food/drink, 4 = tool/uav.
                switch (model.itemPos)
                {
                    case 1:
                        category = ShopCategory.Medicine;
                        return true;
                    case 3:
                        category = ShopCategory.Food;
                        return true;
                    default:
                        category = ShopCategory.Material;
                        return true;
                }
            }
            if (id >= 4000 && id <= 4999)
            {
                category = ShopCategory.Material;
                return true;
            }

            if (id >= 5000 && id <= 5999)
            {
                // Wearable gear: helmet / armour / leggings / boots / gloves / suits.
                category = ShopCategory.Gear;
                return true;
            }

            category = ShopCategory.Material;
            return false;
        }
    }
}
