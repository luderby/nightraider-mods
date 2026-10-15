using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Safe wrapper around the game's bag/depot container APIs.
    ///
    /// The game stores every container as Dictionary&lt;int, CurrentItemData&gt; keyed by slot
    /// index. A slot whose modelID is 0 is "empty"; slots with modelID == -1 are grid
    /// occupancy markers. Multi-cell items occupy several indices via
    /// CurrentDataHub.AutoOccupyBagGrid.
    /// </summary>
    internal static class BagWriter
    {
        /// <summary>
        /// Result of a purchase attempt.
        /// </summary>
        internal enum AddResult
        {
            Ok,
            NoSpace,
            InvalidItem,
            Error
        }

        /// <summary>
        /// Adds <paramref name="count"/> of <paramref name="modelId"/> into the given container,
        /// stacking into existing stacks first when the item is stackable, then filling
        /// empty slots. Returns NoSpace without side effects if it cannot fit everything.
        /// </summary>
        internal static AddResult TryAdd(Dictionary<int, CurrentItemData> container, int modelId, int count)
        {
            if (container == null)
            {
                return AddResult.Error;
            }
            if (count <= 0)
            {
                return AddResult.InvalidItem;
            }

            DataHub data = ManagerBase<DataHub>.Instance;
            CurrentDataHub cur = ManagerBase<CurrentDataHub>.Instance;
            if (data == null || cur == null || data.itemDataDic == null)
            {
                return AddResult.Error;
            }

            ItemData model;
            if (!data.itemDataDic.TryGetValue(modelId, out model) || model == null)
            {
                return AddResult.InvalidItem;
            }

            int maxStack = Mathf.Max(1, model.maxStack);
            int remaining = count;

            // Plan the placement first so a failed add never leaves partial state.
            List<int> stackTargets = new List<int>();
            List<int> stackAmounts = new List<int>();

            if (maxStack > 1)
            {
                foreach (KeyValuePair<int, CurrentItemData> kv in container)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }
                    CurrentItemData slot = kv.Value;
                    if (slot == null || slot.modelID != modelId)
                    {
                        continue;
                    }
                    int room = maxStack - slot.nowStack;
                    if (room <= 0)
                    {
                        continue;
                    }
                    int take = Mathf.Min(room, remaining);
                    stackTargets.Add(kv.Key);
                    stackAmounts.Add(take);
                    remaining -= take;
                }
            }

            // Remaining amount goes into fresh empty slots.
            List<int> freshSlots = new List<int>();
            int slotsNeeded = 0;
            if (remaining > 0)
            {
                slotsNeeded = Mathf.CeilToInt((float)remaining / maxStack);
                foreach (KeyValuePair<int, CurrentItemData> kv in container)
                {
                    if (freshSlots.Count >= slotsNeeded)
                    {
                        break;
                    }
                    CurrentItemData slot = kv.Value;
                    if (slot == null || slot.modelID != 0)
                    {
                        continue;
                    }
                    if (!cur.CheckSpaceByBag(container, kv.Key, model, false))
                    {
                        continue;
                    }
                    freshSlots.Add(kv.Key);
                }
                if (freshSlots.Count < slotsNeeded)
                {
                    return AddResult.NoSpace;
                }
            }

            // Commit.
            try
            {
                for (int i = 0; i < stackTargets.Count; i++)
                {
                    CurrentItemData slot = container[stackTargets[i]];
                    slot.nowStack += stackAmounts[i];
                    int cap = Mathf.Max(1, slot.GetData().maxStack);
                    if (slot.nowStack > cap)
                    {
                        slot.nowStack = cap;
                    }
                }

                int pending = remaining;
                for (int i = 0; i < freshSlots.Count && pending > 0; i++)
                {
                    int give = Mathf.Min(maxStack, pending);
                    int pos = freshSlots[i];

                    CurrentItemData created;
                    if (modelId >= 1000 && modelId <= 1999)
                    {
                        // Firearms come pre-assembled with the game's default parts/ammo setup.
                        created = cur.CreateCompWeapon(modelId);
                    }
                    else
                    {
                        created = cur.CreateNewItem(modelId, give);
                    }
                    if (created == null)
                    {
                        return AddResult.Error;
                    }
                    created.nowStack = give;
                    container[pos] = created;
                    cur.AutoOccupyBagGrid(cur.BagVectorTransToIndex(cur.GetGoodsOccupy(container, pos)), pos, container);
                    pending -= give;
                }
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogError("BagWriter.TryAdd failed for model " + modelId + ": " + ex);
                return AddResult.Error;
            }

            return AddResult.Ok;
        }

        /// <summary>
        /// Counts how many of <paramref name="modelId"/> currently sit in the container.
        /// </summary>
        internal static int CountOf(Dictionary<int, CurrentItemData> container, int modelId)
        {
            if (container == null)
            {
                return 0;
            }
            CurrentDataHub cur = ManagerBase<CurrentDataHub>.Instance;
            if (cur == null)
            {
                return 0;
            }
            try
            {
                return cur.CheckBagItemCount(container, modelId);
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogError("BagWriter.CountOf failed: " + ex.Message);
                return 0;
            }
        }
    }
}
