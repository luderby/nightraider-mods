using System;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 安全访问游戏的 Manager 单例 + MOD 自己的持久化。
    ///
    /// 重要：绝对不要直接访问 ManagerBase&lt;T&gt;.Instance。它在不存在时会
    /// new GameObject 并 DontDestroyOnLoad（ManagerBase.cs:11-15），而
    /// GameManager.Awake 立刻访问 CanvasSystem（GameManager.cs:396），会留下
    /// 字段全 null 的僵尸管理器并抛 NRE。一律先 FindObjectOfType。
    /// </summary>
    internal static class GameRefs
    {
        private static GameManager _game;
        private static DataHub _data;
        private static CurrentDataHub _current;
        private static TextManager _text;
        private static ResourcesHub _resources;
        private static TerrainManager _terrain;
        private static FXManager _fx;

        /// <summary>特效/浮动文字管理器（ShowJump 会用到）。</summary>
        internal static FXManager Fx
        {
            get
            {
                if (_fx == null)
                {
                    _fx = UnityEngine.Object.FindObjectOfType<FXManager>();
                }

                return _fx;
            }
        }

        private static MiniMapNew _miniMap;

        /// <summary>M 键大地图（MiniMapNew 是 ManagerBase 自动创建的常驻单例）。</summary>
        internal static MiniMapNew MiniMap
        {
            get
            {
                if (_miniMap == null)
                {
                    _miniMap = UnityEngine.Object.FindObjectOfType<MiniMapNew>();
                }

                return _miniMap;
            }
        }

        private static MonsterManager _monster;
        private static ObjectPoolManager _pool;

        internal static GameManager Game { get { return Resolve(ref _game); } }
        internal static DataHub Data { get { return Resolve(ref _data); } }
        internal static CurrentDataHub Current { get { return Resolve(ref _current); } }
        internal static TextManager Text { get { return Resolve(ref _text); } }
        internal static ResourcesHub Resources { get { return Resolve(ref _resources); } }
        internal static TerrainManager Terrain { get { return Resolve(ref _terrain); } }
        internal static MonsterManager Monster { get { return Resolve(ref _monster); } }
        internal static ObjectPoolManager Pool { get { return Resolve(ref _pool); } }

        private static T Resolve<T>(ref T cache) where T : Component
        {
            if (cache != null)
            {
                return cache;
            }

            try
            {
                cache = UnityEngine.Object.FindObjectOfType<T>();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("FindObjectOfType<" + typeof(T).Name + "> 失败: " + ex.Message);
                cache = null;
            }

            return cache;
        }

        /// <summary>存档数据是否已载入（GP 存在 recordDataDic[0]）。</summary>
        internal static bool IsSaveLoaded()
        {
            try
            {
                DataHub d = Data;
                if (d == null || d.recordDataDic == null || !d.recordDataDic.ContainsKey(0))
                {
                    return false;
                }

                CurrentDataHub c = Current;
                return c != null && c.nowCharaData != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>是否处于"关卡内"（战斗地图）。</summary>
        internal static bool IsInMission()
        {
            TerrainManager t = Terrain;
            CurrentDataHub c = Current;
            return t != null && c != null && c.nowChara != null && t.GetCombatState();
        }

        internal static void Invalidate()
        {
            _game = null;
            _data = null;
            _current = null;
            _text = null;
            _resources = null;
            _terrain = null;
            _monster = null;
            _pool = null;
        }

        /// <summary>
        /// 字体是否"合格"（含中文字形）。Unity 内置的 Arial/LegacyRuntime 不合格，
        /// 用它渲染中文会全空白。
        /// </summary>
        internal static bool IsFontUsable(Font f)
        {
            if (f == null || string.IsNullOrEmpty(f.name))
            {
                return false;
            }

            string n = f.name;
            if (n.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("LegacyRuntime", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>丢弃缓存，强制下次重新查找（用于拿到更好字体后升级）。</summary>
        internal static void InvalidateFont()
        {
            _cachedFont = null;
        }

        private static Font _cachedFont;

        /// <summary>
        /// 借游戏正在使用的字体。
        ///
        /// 关键：必须拿到游戏自己的字体（LanaPixel，含中日韩字形）。
        /// Unity 内置的 Arial / LegacyRuntime **没有中文字形**，用它渲染中文会显示为空白——
        /// 这正是"指令菜单打开了却看不到文字"的原因（实测日志显示字体=LegacyRuntime）。
        ///
        /// 所以这里优先按名称匹配 LanaPixel，并**排除本 MOD 自己创建的 Text**
        /// （本 MOD 的 UI 建出来后，FindObjectsOfType 很可能先命中它）。
        /// </summary>
        internal static Font FindUiFont()
        {
            if (_cachedFont != null)
            {
                return _cachedFont;
            }

            // 1) 在已加载的字体资源里找 LanaPixel
            try
            {
                UnityEngine.Object[] fonts = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(Font));
                for (int i = 0; i < fonts.Length; i++)
                {
                    Font f = fonts[i] as Font;
                    if (f != null && f.name != null
                        && f.name.IndexOf("Lana", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cachedFont = f;
                        HireNpcPlugin.Log.LogInfo("[字体] 使用游戏字体: " + f.name);
                        return _cachedFont;
                    }
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("FindUiFont(LanaPixel) 失败: " + ex.Message);
            }

            // 2) 从"不属于本 MOD"的活 Text 上借
            try
            {
                Text[] texts = UnityEngine.Object.FindObjectsOfType<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    Text t = texts[i];
                    if (t == null || t.font == null)
                    {
                        continue;
                    }

                    Transform root = t.transform.root;
                    if (root != null && root.name != null
                        && root.name.IndexOf("NightRaider_Hire", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;      // 跳过本 MOD 自己的 UI
                    }

                    _cachedFont = t.font;
                    HireNpcPlugin.Log.LogInfo("[字体] 借用游戏文本字体: " + t.font.name
                        + "（来自 " + (root != null ? root.name : "?") + "）");
                    return _cachedFont;
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("FindUiFont(live Text) 失败: " + ex.Message);
            }

            // 3) 兜底：任意字体（可能没有中文，但不至于崩）
            try
            {
                UnityEngine.Object[] fonts = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(Font));
                for (int i = 0; i < fonts.Length; i++)
                {
                    Font f = fonts[i] as Font;
                    if (f != null)
                    {
                        _cachedFont = f;
                        HireNpcPlugin.Log.LogWarning("[字体] 未找到游戏字体，兜底: " + f.name
                            + "（中文可能显示为空白）");
                        return _cachedFont;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            HireNpcPlugin.Log.LogError("[字体] 找不到任何字体，UI 文字将不可见。");
            return null;
        }
    }

    /// <summary>
    /// MOD 自己的持久化。**绝不写入游戏存档**。
    ///
    /// 原因（已实测确认）：游戏存档是明文 JSON，但所有 Save 方法都从内存字典
    /// 全量重建并 new StreamWriter 截断文件（JsonManager.cs:3254-3332 等），
    /// 而且读档路径没有 try/catch（JsonManager.cs:2763-2769），塞进去的字段
    /// 既会被静默抹掉，又可能让玩家存档读不出来。
    ///
    /// 因此状态写到 BepInEx/config/ 下，按存档位（archiveFileID 1/2/3）分文件，
    /// 并用"临时文件 + 替换"避免半写损坏。
    /// </summary>
    internal static class HireStore
    {
        private static string FilePath(int slot)
        {
            return Path.Combine(Paths.ConfigPath, "NightRaider.HireNpc.slot" + slot + ".txt");
        }

        private static int CurrentSlot()
        {
            try
            {
                GameManager g = GameRefs.Game;
                return g != null ? g.archiveFileID : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 读取该存档位的护航订单。
        ///
        /// 存档格式（每行一笔）：
        ///     TierKey|已用撤离次数|总次数
        /// 兼容旧格式（旧版每行只有一个 TierKey），此时视为"未使用过、共 3 次"。
        /// </summary>
        internal static System.Collections.Generic.List<HireOrder> LoadOrders()
        {
            System.Collections.Generic.List<HireOrder> result =
                new System.Collections.Generic.List<HireOrder>();

            try
            {
                int slot = CurrentSlot();
                if (slot <= 0)
                {
                    return result;
                }

                string p = FilePath(slot);
                if (!File.Exists(p))
                {
                    return result;
                }

                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s.StartsWith("#"))
                    {
                        continue;
                    }

                    string[] parts = s.Split('|');
                    string key = parts[0].Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    HireOrder o = new HireOrder
                    {
                        TierKey = key,
                        Total = HireData.ContractDepartures,
                        Used = 0
                    };

                    if (parts.Length >= 2)
                    {
                        int used;
                        if (int.TryParse(parts[1].Trim(), out used))
                        {
                            o.Used = used;
                        }
                    }

                    if (parts.Length >= 3)
                    {
                        int total;
                        if (int.TryParse(parts[2].Trim(), out total) && total > 0)
                        {
                            o.Total = total;
                        }
                    }

                    if (parts.Length >= 4)
                    {
                        int paid;
                        if (int.TryParse(parts[3].Trim(), out paid))
                        {
                            o.PaidGp = paid;
                        }
                    }

                    if (parts.Length >= 5)
                    {
                        int paidBp;
                        if (int.TryParse(parts[4].Trim(), out paidBp))
                        {
                            o.PaidBp = paidBp;
                        }
                    }

                    // 旧格式没有实付记录 → 按"当时的原价"补齐，保证退款不会算成 0
                    if (o.PaidGp <= 0)
                    {
                        HireTier t = HireData.Find(o.TierKey);
                        if (t != null)
                        {
                            int renewUnits = (o.Total - HireData.ContractDepartures)
                                             / HireData.RenewDepartures;
                            if (renewUnits < 0)
                            {
                                renewUnits = 0;
                            }

                            o.PaidGp = t.Cost + HireData.RenewUnitGp(t) * renewUnits;
                            o.PaidBp = t.BpCost + HireData.RenewUnitBp(t) * renewUnits;
                        }
                    }

                    result.Add(o);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("HireStore.LoadOrders 失败: " + ex.Message);
            }

            return result;
        }

        internal static void SaveOrders(System.Collections.Generic.List<HireOrder> orders)
        {
            try
            {
                int slot = CurrentSlot();
                if (slot <= 0)
                {
                    return;
                }

                string p = FilePath(slot);
                string tmp = p + ".tmp";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# NightRaider HireNpc 护航订单");
                sb.AppendLine("# 格式: 档位Key|已用撤离次数|总次数|实付GP|实付BP");
                for (int i = 0; i < orders.Count; i++)
                {
                    HireOrder o = orders[i];
                    if (o == null)
                    {
                        continue;
                    }

                    sb.Append(o.TierKey).Append('|').Append(o.Used).Append('|').Append(o.Total)
                      .Append('|').Append(o.PaidGp).Append('|').Append(o.PaidBp)
                      .AppendLine();
                }

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
                File.Move(tmp, p);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("HireStore.SaveOrders 失败: " + ex);
            }
        }

        // ---- 旧接口（保留以免遗漏引用）--------------------------------------

        /// <summary>读取该存档位的已雇佣档位 Key 列表。</summary>
        internal static System.Collections.Generic.List<string> LoadHired()
        {
            System.Collections.Generic.List<string> result = new System.Collections.Generic.List<string>();
            System.Collections.Generic.List<HireOrder> orders = LoadOrders();
            for (int i = 0; i < orders.Count; i++)
            {
                result.Add(orders[i].TierKey);
            }

            return result;
        }

        internal static void SaveHired(System.Collections.Generic.List<string> keys)
        {
            System.Collections.Generic.List<HireOrder> orders =
                new System.Collections.Generic.List<HireOrder>();
            for (int i = 0; i < keys.Count; i++)
            {
                orders.Add(new HireOrder { TierKey = keys[i] });
            }

            SaveOrders(orders);
        }
    }
}
