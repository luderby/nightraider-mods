using System.Collections.Generic;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 本 MOD 生成的单位标记。
    ///
    /// 为什么必须要它：敌对人类单位与我们的友军都来自同一个预制体
    /// （ResourcesHub.prefabsDic_Enemy[modelID]），因此 **GameObject 名字完全相同**
    /// （例如都是 "627(Clone)"）。仅凭名字无法区分"游戏原生敌人"与"我们的友军"。
    /// 组件引用不受名字冲突影响，也不会因对象池复用而误判。
    /// </summary>
    public class AllyMarker : MonoBehaviour
    {
        public int ModelId;

        /// <summary>当前指令模式（每个友军独立）。</summary>
        public AllyMode Mode = AllyMode.Follow;

        /// <summary>该友军的档位 Key，用于界面显示。</summary>
        public string TierKey;
    }

    /// <summary>单个友军的运行期状态。</summary>
    internal sealed class AllyRecord
    {
        internal GameObject Go;
        internal int ModelId;
        internal string TierKey;

        /// <summary>最近一次目标重算的帧号（节流用）。</summary>
        internal int NextRetargetFrame;

        /// <summary>是否已经就"目标指向本方"告警过一次（避免刷屏）。</summary>
        internal bool WarnedWrongTarget;

        /// <summary>是否已经就"阵营被改动"告警过一次。</summary>
        internal bool WarnedFactionFlip;

        /// <summary>卡住检测：上一次采样位置与"当时是否在尝试移动"。</summary>
        internal Vector3 LastPos;
        internal bool WasTryingToMove;
        internal int StuckFrames;

        /// <summary>位置是否被本 MOD 冻结（原地待命 / 已进入近身范围）。</summary>
        internal bool Frozen;

        /// <summary>冻结时应该保持的世界坐标。</summary>
        internal Vector3 FrozenPos;

        /// <summary>上一次下发的"想去的位置"（用于节流，避免每帧重置寻路）。</summary>
        internal bool HasSuspect;
        internal Vector3 LastSuspect;
        internal int NextSuspectFrame;

        /// <summary>
        /// 漫游点。
        ///
        /// 必须固定，否则会"抽搐成陀螺"——之前用 Time.frameCount 当角度，
        /// 每 30 帧目标点就跳到圆周另一处，友军便绕着圈追这个跳变目标。
        /// 现在改为：漫游点算出来就固定，直到友军走到它附近、或玩家移动较远时才重算。
        /// </summary>
        internal bool HasRoamPoint;
        internal Vector3 RoamPoint;

        /// <summary>选漫游点时所依附的玩家位置（玩家走远后需重算）。</summary>
        internal Vector3 RoamAnchor;

        /// <summary>
        /// 到达漫游点后的停留帧数。
        /// 没有这个停留，友军一到点就立刻换新方向 → 表现为"快速左右晃动"。
        /// </summary>
        internal int RoamRestFrames;

        /// <summary>下一句台词的最早帧号（用于随机台词）。</summary>
        internal int NextTalkFrame;

        /// <summary>上一帧是否在开火（用于检测"开火"这一瞬间）。</summary>
        internal bool WasFiring;

        /// <summary>受击后置位，下一帧由 MaybeTalk 弹出一句受击台词。</summary>
        internal bool NeedsTalk;

        // ── 台词触发标记（由外部事件置位，AllyBrain 消费）──────────────────
        internal bool TalkSelfHurt;
        internal bool TalkPlayerHurt;
        internal bool TalkKillSelf;
        internal bool TalkKillOther;
        internal bool TalkAllyDown;
        internal bool TalkPlayerDead;
        internal bool TalkReload;

        /// <summary>上一帧是否处于换弹状态（用于检测"开始换弹"这一瞬间）。</summary>
        internal bool WasReloading;

        /// <summary>累积击杀数（达到阈值才说一句击杀台词，避免刷屏）。</summary>
        internal int KillCounter;

        // ── 脱离战斗后的恢复计时器 ──────────────────────────────────────────
        //
        // 用户要求：周围没有敌人时，
        //   · 15 秒后清除护航身上的异常状态
        //   · 15 秒后把血量与护甲补满
        // 这里记录"最后一次检测到附近有敌人的帧号"，据此计算脱离战斗时长。
        internal int LastCombatFrame;
        internal bool HasBeenInCombat;

        /// <summary>本 MOD 是否曾经给该队员补满过护甲（用于日志去重）。</summary>
        internal bool RestoredLogged;

        // ── 台词系统：按类别独立冷却 ────────────────────────────────────────
        //
        // 用户要求"不同类型的动作冷却不共用"：
        //   老板受伤  → 触发受伤台词（即使同时在换弹，换弹台词也能各自触发）
        //   老板 3 秒内连续受伤 → 只触发 1 次（同类冷却）
        //
        // 因此每个类别有各自的冷却帧号；另有统一的"待说队列"，
        // 保证标记不会被冷却吞掉（说出口才清除）。

        /// <summary>类别 → 下一次可说的帧号（按类别独立冷却）。</summary>
        internal readonly System.Collections.Generic.Dictionary<int, int> CategoryNext =
            new System.Collections.Generic.Dictionary<int, int>();

        /// <summary>待说队列（类别 → 该事件发生的帧号）。</summary>
        internal readonly System.Collections.Generic.List<KeyValuePair<int, int>> Pending =
            new System.Collections.Generic.List<KeyValuePair<int, int>>();

        /// <summary>登记一条"待说"事件（同类别只保留最早的一条）。</summary>
        internal void Enqueue(int category, int frame)
        {
            for (int i = 0; i < Pending.Count; i++)
            {
                if (Pending[i].Key == category)
                {
                    return;      // 已有同类待说，不重复入队
                }
            }

            Pending.Add(new KeyValuePair<int, int>(category, frame));
        }

        /// <summary>类别是否已过冷却。</summary>
        internal bool Ready(int category, int frame)
        {
            int next;
            return !CategoryNext.TryGetValue(category, out next) || frame >= next;
        }

        /// <summary>标记某类别刚说过。</summary>
        internal void MarkSpoken(int category, int frame, int cooldown)
        {
            CategoryNext[category] = frame + cooldown;
        }

        internal AllyMarker Marker
        {
            get { return Go != null ? Go.GetComponent<AllyMarker>() : null; }
        }

        internal CharaControl Control
        {
            get { return Go != null ? Go.GetComponent<CharaControl>() : null; }
        }

        internal CharaState State
        {
            get { return Go != null ? Go.GetComponent<CharaState>() : null; }
        }

        internal AllyMode Mode
        {
            get
            {
                AllyMarker m = Marker;
                return m != null ? m.Mode : AllyMode.Follow;
            }
            set
            {
                AllyMarker m = Marker;
                if (m != null)
                {
                    m.Mode = value;
                }
            }
        }

        internal bool IsAlive
        {
            get
            {
                CharaState s = State;
                return Go != null && s != null && s.IsAlive();
            }
        }
    }

    /// <summary>
    /// 友军总表。所有"这是不是我们的友军"的判断都必须走这里，
    /// 不能依赖 GameObject 名字。
    /// </summary>
    internal static class AllyRegistry
    {
        private static readonly List<AllyRecord> _allies = new List<AllyRecord>();

        internal static int Count { get { return _allies.Count; } }

        internal static AllyRecord At(int i) { return _allies[i]; }

        /// <summary>当前在场的存活友军数量。</summary>
        internal static int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _allies.Count; i++)
                {
                    if (_allies[i].IsAlive)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// 在生成前登记占位记录。必须在 MonsterManager.CreateEnemey 之前调用，
        /// 因为该方法内部会在返回前调用 GetCloseTgt(nowChara) 强制把玩家设为目标，
        /// 若登记晚于它，拦截器就来不及拦（这正是"生成瞬间朝玩家开枪"的成因）。
        /// </summary>
        internal static AllyRecord PreRegister(int modelId, string tierKey, int frame)
        {
            AllyRecord r = new AllyRecord
            {
                Go = null,
                ModelId = modelId,
                TierKey = tierKey,
                NextRetargetFrame = frame
            };
            _allies.Add(r);
            return r;
        }

        /// <summary>生成返回后回填对象，并打上标记。</summary>
        internal static void Complete(AllyRecord r, GameObject go)
        {
            if (r == null || go == null)
            {
                return;
            }

            r.Go = go;
            r.LastPos = go.transform.position;

            AllyMarker marker = go.GetComponent<AllyMarker>();
            if (marker == null)
            {
                marker = go.AddComponent<AllyMarker>();
            }
            marker.ModelId = r.ModelId;
            marker.TierKey = r.TierKey;
            marker.Mode = AllyMode.Follow;
        }

        internal static bool IsAlly(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            return go.GetComponent<AllyMarker>() != null;
        }

        internal static AllyRecord Find(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            for (int i = 0; i < _allies.Count; i++)
            {
                if (_allies[i].Go == go)
                {
                    return _allies[i];
                }
            }
            return null;
        }

        /// <summary>移除已死亡/已销毁的记录。</summary>
        internal static void Prune()
        {
            for (int i = _allies.Count - 1; i >= 0; i--)
            {
                AllyRecord r = _allies[i];
                if (r.Go == null || !r.IsAlive)
                {
                    _allies.RemoveAt(i);
                }
            }
        }

        internal static void Clear()
        {
            _allies.Clear();
        }

        /// <summary>
        /// 按给定的 GameObject 列表重建注册表（保留它们原有的模式等信息）。
        /// 用于选择性结单后同步场上队员。
        /// </summary>
        internal static void RebuildFrom(List<GameObject> keep)
        {
            List<AllyRecord> rebuilt = new List<AllyRecord>();

            if (keep != null)
            {
                for (int i = 0; i < keep.Count; i++)
                {
                    AllyRecord r = Find(keep[i]);
                    if (r != null)
                    {
                        rebuilt.Add(r);
                    }
                }
            }

            _allies.Clear();
            _allies.AddRange(rebuilt);
        }

        /// <summary>把当前在场的友军 GameObject 列表取出来（用于回收）。</summary>
        internal static List<GameObject> Snapshot()
        {
            List<GameObject> list = new List<GameObject>();
            for (int i = 0; i < _allies.Count; i++)
            {
                if (_allies[i].Go != null)
                {
                    list.Add(_allies[i].Go);
                }
            }
            return list;
        }
    }
}
