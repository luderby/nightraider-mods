using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 友军 AI 大脑。挂在本 MOD 自己的 GameObject 上，每帧驱动所有友军。
    ///
    /// 为什么必须自己写：
    ///  1. 索敌：LookArea.cs:90 与 LookAreaCld.cs:61 都显式排除 party 0，
    ///     所以友军不会自动索敌，必须由 MOD 主动喂目标。
    ///  2. 移动：CharaControl.MoveToTarget 依赖私有 tempPos 寻路路径
    ///     （CharaControl.cs:2382-2396），party 0 单位经常拿不到有效路径 → 站着不动。
    ///     实测只有部分单位会移动，因此跟随必须自研。
    /// </summary>
    internal sealed class AllyBrain : MonoBehaviour
    {
        private static FieldInfo _fCloseTgt;
        private static FieldInfo _fFinalSpeed;
        private static FieldInfo _fIsFar;
        private static FieldInfo _fIsIdle;

        private int _frame;

        private void Awake()
        {
            _fCloseTgt = AccessTools.Field(typeof(CharaControl), "closeTgt");
            _fFinalSpeed = AccessTools.Field(typeof(CharaControl), "finalSpeed");
            _fIsFar = AccessTools.Field(typeof(CharaControl), "isFar");
            _fIsIdle = AccessTools.Field(typeof(CharaControl), "isIdle");
        }

        private void Update()
        {
            try
            {
                _frame++;

                if (AllyRegistry.Count == 0)
                {
                    return;
                }

                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null)
                {
                    return;
                }

                // 只在本 MOD 自己的单位上工作；营地(非战斗)地图不驱动。
                TerrainManager ter = GameRefs.Terrain;
                if (ter != null && !ter.GetCombatState())
                {
                    return;
                }

                GameObject player = cur.nowChara;
                _hostiles = ScanHostiles();

                for (int i = AllyRegistry.Count - 1; i >= 0; i--)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || r.Go == null || !r.IsAlive)
                    {
                        continue;
                    }

                    DriveTargeting(r, player, _hostiles);
                    TickRecovery(r, player, _hostiles);
                    MaybeTalk(r, GetCloseTgt(r.Control) != null, IsFiring(r.Control));
                }

                // 周期性行为日志：直接反映"指令是否驱动了移动"
                if (HireTuning.Verbose && Time.frameCount % 120 == 0 && AllyRegistry.Count > 0)
                {
                    for (int i = 0; i < AllyRegistry.Count; i++)
                    {
                        AllyRecord r = AllyRegistry.At(i);
                        if (r == null || r.Go == null)
                        {
                            continue;
                        }

                        float d = Vector3.Distance(r.Go.transform.position, player.transform.position);
                        CharaControl cc = r.Control;
                        GameObject tgt = cc != null ? GetCloseTgt(cc) : null;

                        HireNpcPlugin.Log.LogInfo("[行为] 友军[" + i + "] 模式=" + HireNpcPlugin.ModeName(r.Mode)
                            + " 位置=" + r.Go.transform.position
                            + " 与玩家距离=" + d.ToString("F2")
                            + " 当前目标=" + (tgt != null ? tgt.name : "无"));
                    }
                }

                AllyRegistry.Prune();
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("AllyBrain.Update 异常: " + ex);
            }
        }

        /// <summary>
        /// 位置控制放在 LateUpdate。
        ///
        /// 原因：游戏未设置脚本执行顺序（全工程无 DefaultExecutionOrder），
        /// 而 CharaControl 自带的 MoveToTarget / MoveNoTarget（CharaControl.cs:2266 / 2481）
        /// 也会在它自己的 Update 里改 transform.position。若本 MOD 也在 Update 里改，
        /// 谁后执行是不确定的 —— 表现为"指令切换了但没效果"。
        ///
        /// Unity 保证 LateUpdate 在所有 Update 之后执行，因此这里拥有位置的最终决定权，
        /// 既能让三个指令稳定生效，又不必去补丁游戏的私有移动方法。
        /// </summary>
        private void LateUpdate()
        {
            try
            {
                if (AllyRegistry.Count == 0)
                {
                    return;
                }

                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null)
                {
                    return;
                }

                TerrainManager ter = GameRefs.Terrain;
                if (ter != null && !ter.GetCombatState())
                {
                    return;
                }

                GameObject player = cur.nowChara;
                List<GameObject> hostiles = _hostiles ?? ScanHostiles();

                for (int i = 0; i < AllyRegistry.Count; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || r.Go == null || !r.IsAlive)
                    {
                        continue;
                    }

                    ApplyMovement(r, player, hostiles);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("AllyBrain.LateUpdate 异常: " + ex);
            }
        }

        private List<GameObject> _hostiles;

        // ------------------------------------------------------------------
        // 索敌
        // ------------------------------------------------------------------

        /// <summary>
        /// 扫描敌对单位。只认 party != 0 且 != 7，并且**显式排除带 AllyMarker 的单位**
        /// —— 否则友军可能把同伴当成敌人（"NPC 互相开火"缺陷）。
        /// </summary>
        private static List<GameObject> ScanHostiles()
        {
            List<GameObject> list = new List<GameObject>();
            try
            {
                GameManager gm = GameRefs.Game;
                if (gm == null || gm.pool == null)
                {
                    return list;
                }

                Transform pool = gm.pool.transform;
                // 1 = 普通敌人池，5 = 波次池
                int[] idxs = { 1, 5 };
                for (int k = 0; k < idxs.Length; k++)
                {
                    int idx = idxs[k];
                    if (pool.childCount <= idx)
                    {
                        continue;
                    }

                    Transform parent = pool.GetChild(idx);
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform c = parent.GetChild(i);
                        if (c == null)
                        {
                            continue;
                        }

                        // 自己的友军永远不算敌人
                        if (c.GetComponent<AllyMarker>() != null)
                        {
                            continue;
                        }

                        CharaState st = c.GetComponent<CharaState>();
                        if (st == null || !st.IsAlive())
                        {
                            continue;
                        }

                        int party = st.CheckParty();
                        if (party != 0 && party != 7)
                        {
                            list.Add(c.gameObject);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("ScanHostiles 异常: " + ex.Message);
            }
            return list;
        }

        private static GameObject Nearest(List<GameObject> list, Vector3 from, float maxRange)
        {
            GameObject best = null;
            float bestSqr = maxRange * maxRange;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null)
                {
                    continue;
                }

                float sqr = (list[i].transform.position - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = list[i];
                }
            }
            return best;
        }

        private static GameObject GetCloseTgt(CharaControl c)
        {
            if (c == null || _fCloseTgt == null)
            {
                return null;
            }
            return _fCloseTgt.GetValue(c) as GameObject;
        }

        // ------------------------------------------------------------------
        // 主驱动
        // ------------------------------------------------------------------

        /// <summary>
        /// Update 阶段：阵营守护 + 清除错误目标 + 喂目标（索敌）。
        /// 移动不在这里做 —— 见 ApplyMovement 的注释。
        /// </summary>
        private static void DriveTargeting(AllyRecord r, GameObject player, List<GameObject> hostiles)
        {
            try
            {
                CharaControl ctrl = r.Control;
                CharaState state = r.State;
                if (ctrl == null || state == null)
                {
                    return;
                }

                // ── 阵营守护（纵深防御；根因级拦截在 Harmony Prefix 上） ──────────
                if (state.CheckParty() != 0)
                {
                    if (!r.WarnedFactionFlip)
                    {
                        r.WarnedFactionFlip = true;
                        HireNpcPlugin.Log.LogWarning("友军 " + r.Go.name + " 阵营被改成 party="
                                                     + state.CheckParty() + "，已强制拉回 party 0。");
                    }
                    state.SetParty(0);
                }

                // ── 清除指向本方（玩家或友军）的错误目标 ────────────────────────
                GameObject currentTgt = GetCloseTgt(ctrl);
                if (currentTgt != null && IsFriendlyTarget(currentTgt, player))
                {
                    ctrl.LostTgt();
                    ctrl.ClearTargetList();
                    if (!r.WarnedWrongTarget)
                    {
                        r.WarnedWrongTarget = true;
                        HireNpcPlugin.Log.LogInfo("友军 " + r.Go.name
                                                  + " 目标指向本方(" + currentTgt.name + ")，已清除。");
                    }
                }

                // ── 索敌（节流） ────────────────────────────────────────────────
                // 只在玩家的活动半径内锁敌：
                //   按"与玩家的距离"排序，友军不会为追远处敌人而离开玩家太远。
                AllyMode mode = r.Mode;
                float wantRange = HireTuning.FollowNear;

                if (Time.frameCount >= r.NextRetargetFrame)
                {
                    r.NextRetargetFrame = Time.frameCount + HireTuning.RetargetFrames;

                    GameObject target = NearestToPlayer(hostiles, player, wantRange);
                    if (target != null)
                    {
                        ctrl.isIdle = false;
                        ctrl.GetTargetListObj(target);
                        ctrl.GetCloseTgt(target);
                    }
                }

                // 维持 AI 更新：清掉远距离剔除标记，否则单位会"僵住"
                ClearCullFlags(ctrl);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("DriveTargeting 异常: " + ex);
            }
        }

        /// <summary>
        /// 位置控制（LateUpdate）。
        ///
        /// 【重要设计变更】不再由本 MOD 直接改 transform.position。
        ///
        /// 原因（用户实测反馈）：直接平移 transform.position 会导致
        ///   * 走路没有腿部动画（看起来是"滑行/平移"）
        ///   * 与游戏自身的移动叠加时产生小距离抖动、被拖拽感
        ///   * 绕过游戏的碰撞与寻路
        ///
        /// 改为**驱动游戏自身的移动系统**：
        ///   * 用 GetSuspectPos() 设置"想去的位置"（CharaControl.cs:366）
        ///   * 游戏自己的 MoveToSuspectPos（CharaControl.cs:2432）会沿 tempPos 寻路走过去，
        ///     并且带 finalSpeed / isRun 动画 —— 与普通 AI 完全一致的行走表现
        ///   * 用 SetForcedTime(0) 确保不被"被击退压制"状态挡住
        ///
        /// 本 MOD 只负责决定"去哪里"，不再亲自搬运单位。
        /// </summary>
        private static FieldInfo _fAtkRateTime;

        private static readonly string[] CombatTalk = { };
        private static readonly string[] IdleTalk = { };
        private static readonly string[] HurtTalk = { };

        // ── 台词类别（每类各自独立冷却）─────────────────────────────────────
        private const int TalkCatIdle = 1;
        private const int TalkCatFollow = 2;
        private const int TalkCatCombat = 3;
        private const int TalkCatReload = 4;
        private const int TalkCatKillSelf = 5;
        private const int TalkCatKillOther = 6;
        private const int TalkCatSelfHurt = 7;
        private const int TalkCatPlayerHurt = 8;
        private const int TalkCatPlayerDown = 9;
        private const int TalkCatAllyDown = 10;
        private const int TalkCatLowHp = 11;

        // 各类别的冷却帧数（60 帧 ≈ 1 秒）
        private const int CdHurt = 180;        // 受伤类：3 秒内连续受伤只喊 1 次
        private const int CdKill = 240;
        private const int CdReload = 300;
        private const int CdEvent = 180;
        private const int CdChat = 420;        // 闲聊类
        private const int CdLowHp = 600;

        /// <summary>同一条待说事件最多保留多少帧（避免陈旧台词过很久才冒出来）。</summary>
        private const int PendingExpire = 150;

        /// <summary>任意两条台词之间的最小间隔（帧），只用于防止文字重叠。</summary>
        private const int MinOverlapGap = 24;   // 约 0.4 秒

        private static int _lastSpokenFrame;

        /// <summary>
        /// 随机台词。
        ///
        /// 【设计要点（按用户要求）】
        ///   1. **每个类别独立冷却** —— 不再是全局共用一把冷却，
        ///      因此"老板受伤"与"换弹"可以各自触发自己的台词。
        ///   2. 同类事件短时间内重复触发只算一次
        ///      （例如老板 3 秒内连续受伤，只会喊 1 次）。
        ///   3. 事件一律先**入队**，说出口才清除 ——
        ///      避免"标记被消费但没说出来"导致台词丢失
        ///      （这正是用户实测"受伤无喊话"的原因）。
        /// </summary>
        private static void MaybeTalk(AllyRecord r, bool inCombat, bool firing)
        {
            try
            {
                if (!HireTuning.AllyTalk || HireTuning.AllyMuted)
                {
                    return;
                }

                FXManager fx = GameRefs.Fx;
                if (fx == null || r.Go == null)
                {
                    return;
                }

                int now = Time.frameCount;

                // ── 1. 把本帧发生的事件入队 ─────────────────────────────────
                if (r.TalkPlayerDead)
                {
                    r.TalkPlayerDead = false;
                    r.Enqueue(TalkCatPlayerDown, now);
                }

                if (r.TalkPlayerHurt)
                {
                    r.TalkPlayerHurt = false;
                    r.Enqueue(TalkCatPlayerHurt, now);     // 同类重复入队会被忽略
                }

                if (r.TalkSelfHurt)
                {
                    r.TalkSelfHurt = false;
                    r.Enqueue(TalkCatSelfHurt, now);
                }

                if (r.TalkAllyDown)
                {
                    r.TalkAllyDown = false;
                    r.Enqueue(TalkCatAllyDown, now);
                }

                if (r.TalkKillOther)
                {
                    r.TalkKillOther = false;
                    r.Enqueue(TalkCatKillOther, now);
                }

                if (r.TalkKillSelf)
                {
                    r.TalkKillSelf = false;
                    r.KillCounter++;
                    if (r.KillCounter >= TalkFile.KillsPerLine)
                    {
                        r.KillCounter = 0;
                        r.Enqueue(TalkCatKillSelf, now);
                    }
                }

                // 换弹：进入换弹状态那一帧入队
                bool reloading = IsReloading(r.Control);
                if (reloading && !r.WasReloading)
                {
                    r.Enqueue(TalkCatReload, now);
                }
                r.WasReloading = reloading;

                // 闲聊：到点才入队（随机 8~18 秒）
                if (r.NextTalkFrame == 0)
                {
                    r.NextTalkFrame = now + UnityEngine.Random.Range(480, 1080);
                }
                else if (now >= r.NextTalkFrame)
                {
                    r.NextTalkFrame = now + UnityEngine.Random.Range(480, 1080);

                    CharaData cd = r.State != null ? r.State.GetCurrentData() : null;
                    bool low = cd != null && cd.currentMaxHp > 0
                               && (float)cd.currentHP / cd.currentMaxHp < 0.3f;

                    r.Enqueue(low ? TalkCatLowHp : (inCombat ? TalkCatCombat : TalkCatIdle), now);
                }

                // ── 2. 从队列里挑一条能说的 ─────────────────────────────────
                if (r.Pending.Count == 0)
                {
                    return;
                }

                // 防止文字重叠：任意两条台词至少间隔 0.4 秒
                if (now - _lastSpokenFrame < MinOverlapGap)
                {
                    return;
                }

                for (int i = 0; i < r.Pending.Count; i++)
                {
                    int cat = r.Pending[i].Key;
                    int born = r.Pending[i].Value;

                    // 太陈旧就丢弃
                    if (now - born > PendingExpire)
                    {
                        r.Pending.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (!r.Ready(cat, now))
                    {
                        continue;      // 该类别还在冷却
                    }

                    string[] pool = PoolFor(cat);
                    if (pool == null || pool.Length == 0)
                    {
                        r.Pending.RemoveAt(i);
                        i--;
                        continue;
                    }

                    fx.ShowJump(r.Go, pool[UnityEngine.Random.Range(0, pool.Length)]);

                    r.MarkSpoken(cat, now, CooldownFor(cat));
                    _lastSpokenFrame = now;
                    r.Pending.RemoveAt(i);
                    return;            // 一帧最多说一句
                }
            }
            catch
            {
                // 台词只是锦上添花，出错不影响战斗逻辑
            }
        }

        private static string[] PoolFor(int cat)
        {
            switch (cat)
            {
                case TalkCatIdle: return AllyTalk.Idle;
                case TalkCatFollow: return AllyTalk.Follow;
                case TalkCatCombat: return AllyTalk.Combat;
                case TalkCatReload: return AllyTalk.Reload;
                case TalkCatKillSelf: return AllyTalk.KillSelf;
                case TalkCatKillOther: return AllyTalk.KillOther;
                case TalkCatSelfHurt: return AllyTalk.SelfHurt;
                case TalkCatPlayerHurt: return AllyTalk.PlayerHurt;
                case TalkCatPlayerDown: return AllyTalk.PlayerDown;
                case TalkCatAllyDown: return AllyTalk.AllyDown;
                case TalkCatLowHp: return AllyTalk.LowHp;
                default: return null;
            }
        }

        private static int CooldownFor(int cat)
        {
            switch (cat)
            {
                case TalkCatSelfHurt:
                case TalkCatPlayerHurt:
                    return CdHurt;
                case TalkCatKillSelf:
                case TalkCatKillOther:
                    return CdKill;
                case TalkCatReload:
                    return CdReload;
                case TalkCatLowHp:
                    return CdLowHp;
                case TalkCatIdle:
                case TalkCatCombat:
                    return CdChat;
                default:
                    return CdEvent;
            }
        }

        private static FieldInfo _fWeaponForReload;

        /// <summary>
        /// 是否处于"需要换弹"的状态。
        ///
        /// 敌人 AI 的弹匣在 CharaControl.cs:1442-1446 自动补满；
        /// 这里用 enemyWeapon.nowAmmoCount 判断 —— 为 0 即正在/即将换弹。
        /// </summary>
        private static bool IsReloading(CharaControl ctrl)
        {
            try
            {
                if (ctrl == null)
                {
                    return false;
                }

                if (_fWeaponForReload == null)
                {
                    _fWeaponForReload = AccessTools.Field(typeof(CharaControl), "enemyWeapon");
                }

                if (_fWeaponForReload == null)
                {
                    return false;
                }

                CurrentItemData w = _fWeaponForReload.GetValue(ctrl) as CurrentItemData;
                return w != null && w.nowAmmoCount <= 0;
            }
            catch
            {
                return false;
            }
        }

        private static FieldInfo _fDamageShield;

        /// <summary>
        /// 脱离战斗后的恢复（用户要求）：
        ///   · 周围没有敌人满 15 秒 → 清除护航身上的异常状态
        ///   · 同时把血量与护甲补满
        ///
        /// 实现要点：
        ///   异常状态用游戏自带的 CharaState.ClearStates()（CharaState.cs:3099），
        ///   它会把 isBleeding / isBurning / isPoisoning / isStun / 各类骨折等
        ///   三十多个状态计数一次性清零 —— 不需要我们自己反射改私有字段。
        ///
        ///   护甲（HUD 上的"护甲"）= CharaState.damageShield，上限 = CharaData.def，
        ///   所以补满就是把 damageShield 置为 def。
        /// </summary>
        private static void TickRecovery(AllyRecord r, GameObject player, List<GameObject> hostiles)
        {
            try
            {
                if (!HireTuning.AutoRecover)
                {
                    return;
                }

                int now = Time.frameCount;
                Vector3 me = r.Go.transform.position;

                // 附近有敌人 → 记录战斗帧，并重置恢复状态
                GameObject near = Nearest(hostiles, me, HireTuning.RecoverSafeRadius);
                if (near != null)
                {
                    r.LastCombatFrame = now;
                    r.HasBeenInCombat = true;
                    return;
                }

                // 从未进入过战斗就不需要恢复
                if (!r.HasBeenInCombat)
                {
                    return;
                }

                // 脱离战斗还不到设定时长
                int need = Mathf.RoundToInt(HireTuning.RecoverSeconds * 50f);
                if (now - r.LastCombatFrame < need)
                {
                    return;
                }

                CharaState st = r.State;
                if (st == null)
                {
                    return;
                }

                // ── 1. 清除异常状态 ────────────────────────────────────────
                // 注意：按用户要求，**只清除异常状态，不再补满血量与护甲**。
                if (IsStateDirty(st))
                {
                    st.ClearStates();
                    HireNpcPlugin.Log.LogInfo("[恢复] 已清除「" + TierNameOf(r) + "」的异常状态。");
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("TickRecovery 异常: " + ex.Message);
            }
        }

        private static string TierNameOf(AllyRecord r)
        {
            HireTier t = HireData.Find(r.TierKey);
            return t != null ? t.Name : r.TierKey;
        }

        /// <summary>身上是否还有异常状态（避免无谓地反复调用 ClearStates）。</summary>
        private static bool IsStateDirty(CharaState st)
        {
            try
            {
                return st.CheckIsBleeding() > 0
                       || st.CheckIsBurning() > 0
                       || st.CheckIsPoisoning() > 0
                       || st.CheckIsStun() > 0
                       || st.CheckIsDizzy() > 0
                       || st.CheckIsBlurredVision() > 0
                       || st.CheckIsVisceralBleeding() > 0
                       || st.CheckIsSoppy() > 0
                       || st.CheckIsDiarrhea() > 0
                       || st.CheckIsHyperthermal() > 0
                       || st.CheckIsFreezing() > 0
                       || st.CheckIsEmesis() > 0
                       || st.CheckIsDebility() > 0
                       || st.CheckIsArmBoneFracture() > 0
                       || st.CheckIsLegBoneFracture() > 0
                       || st.CheckIsRibFracture() > 0
                       || st.CheckIsCardiacInjure() > 0
                       || st.CheckIsLungInjure() > 0
                       || st.CheckIsGastricInjure() > 0
                       || st.CheckIsLiverKidneyInjure() > 0
                       || st.CheckIsOcularInjure() > 0
                       || st.CheckIsSlowing() > 0
                       || st.CheckIsAching() > 0
                       || st.CheckIsPanic() > 0
                       || st.CheckIsPalsy() > 0
                       || st.CheckIsClumsy() > 0
                       || st.CheckIsBlinding() > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 该友军是否正在开火（用于"开枪时不移动"）。
        ///
        /// atkRateTime 是游戏自己的攻击冷却（CharaControl.cs:118）。
        /// 玩家在开枪后一段时间内是不能移动的，这里让友军也遵循同样规则 ——
        /// 否则会出现"边走边打"的滑步观感。
        /// </summary>
        private static bool IsFiring(CharaControl ctrl)
        {
            try
            {
                if (_fAtkRateTime == null)
                {
                    _fAtkRateTime = AccessTools.Field(typeof(CharaControl), "atkRateTime");
                }

                if (_fAtkRateTime == null)
                {
                    return false;
                }

                object v = _fAtkRateTime.GetValue(ctrl);
                return v is int && (int)v > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyMovement(AllyRecord r, GameObject player, List<GameObject> hostiles)
        {
            try
            {
                CharaControl ctrl = r.Control;
                if (ctrl == null || r.Go == null)
                {
                    return;
                }

                AllyMode mode = r.Mode;
                GameObject close = GetCloseTgt(ctrl);
                bool inCombat = close != null;
                float distToPlayer = Dist(r.Go, player);

                Vector3 me = r.Go.transform.position;

                // ── 换弹中：远离敌人 ───────────────────────────────────────────
                // 用户反馈"换弹时最好远离敌人"。此时敌人也会开枪，
                // 因此优先朝远离最近敌人的方向退，同时不要跑出活动半径。
                if (IsReloading(ctrl))
                {
                    GameObject threat = Nearest(hostiles, me, 20f);
                    Vector3 away;

                    if (threat != null)
                    {
                        away = (me - threat.transform.position);
                        if (away.sqrMagnitude < 0.0001f)
                        {
                            away = Vector3.right;
                        }
                        away = away.normalized;
                    }
                    else
                    {
                        away = (me - player.transform.position).normalized;   // 无威胁就别乱动
                    }

                    // 但不能退到活动半径之外：如果已经在半径边缘，改为朝玩家靠
                    float radiusNow = HireTuning.FollowNear;

                    Vector3 retreat = me + away * 3f;
                    if (Vector3.Distance(retreat, player.transform.position) > radiusNow)
                    {
                        retreat = player.transform.position;
                    }

                    ctrl.GetSuspectPos(retreat);
                    r.LastSuspect = retreat;
                    r.HasSuspect = true;
                    r.StuckFrames = 0;
                    return;
                }

                // ── 开枪定身 ───────────────────────────────────────────────────
                // 玩家在开火后的冷却期内无法移动；让友军遵循同样规则，
                // 解决"一边移动一边射击"的滑步问题。
                if (IsFiring(ctrl))
                {
                    ctrl.GetSuspectPos(me);      // 想去的位置 = 脚下 → 原地站定
                    r.HasSuspect = true;
                    r.LastSuspect = me;
                    r.StuckFrames = 0;
                    return;
                }

                // ── 停留：完全不动 ─────────────────────────────────────────────
                if (mode == AllyMode.Hold)
                {
                    ctrl.GetSuspectPos(me);
                    r.HasSuspect = true;
                    r.LastSuspect = me;
                    r.StuckFrames = 0;
                    return;
                }

                // ── 瞬移归队：只在真正脱队时触发，落到玩家 3 米内 ────────────────
                if (distToPlayer > HireTuning.FollowFar)
                {
                    TeleportNearPlayer(r, player, 3.0f);
                    r.HasSuspect = false;
                    return;
                }

                // ── 决定"想去的位置" ───────────────────────────────────────────
                Vector3 want;
                bool haveWant = true;

                if (inCombat)
                {
                    // 有交战目标：让游戏自身的 MoveToTarget 处理接近与走位，
                    // 我们只在友军离玩家太远时把它拉回来（避免打断正常交战机动）。
                    float radiusNow = HireTuning.FollowNear;

                    if (distToPlayer > radiusNow)
                    {
                        Vector3 dirIn = (player.transform.position - me);
                        if (dirIn.sqrMagnitude < 0.0001f)
                        {
                            dirIn = Vector3.right;
                        }
                        want = player.transform.position - dirIn.normalized * Mathf.Max(1f, radiusNow - 2f);
                    }
                    else
                    {
                        haveWant = false;      // 半径内：交给游戏 AI 自行交战走位
                        want = me;
                    }
                }
                else
                {
                    // 跟随：目标点落在玩家活动半径内，且保持稳定
                    want = GetStableRoamPoint(r, player, HireTuning.FollowNear);
                }

                if (!haveWant)
                {
                    r.StuckFrames = 0;
                    return;
                }

                // ── 节流设置 suspectPos ────────────────────────────────────────
                // 【朝向抖动的原因】CharaControl.GetSuspectPos（CharaControl.cs:366-370）
                // 在设置目标点的同时会把 lookDir 转向该点。如果目标点频繁变化，
                // 角色就会不停转身 —— 这就是"朝向变化较快"的根因。
                //
                // 因此这里把更新条件收得很紧：
                //   · 只有在"确实需要移动"（目标点离当前位置 > 1.2 米）时才下发
                //   · 且目标点相对上次变化超过 4 米，或已过 90 帧
                // 到达漫游点后会先休息 1.5~3 秒（RoamRestFrames），期间不改变朝向。
                float distToWant = Vector3.Distance(me, want);

                bool wantChanged = !r.HasSuspect || (want - r.LastSuspect).sqrMagnitude > 16f;
                bool timeUp = Time.frameCount >= r.NextSuspectFrame;
                bool needMove = distToWant > 1.2f;

                if (!needMove)
                {
                    // 已经到位/无需移动 —— 不要下发新目标，避免无意义转身
                    r.StuckFrames = 0;
                    return;
                }

                if (wantChanged || timeUp)
                {
                    ctrl.GetSuspectPos(want);
                    r.LastSuspect = want;
                    r.HasSuspect = true;
                    r.NextSuspectFrame = Time.frameCount + 90;
                }

                r.StuckFrames = 0;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("ApplyMovement 异常: " + ex);
            }
        }

        /// <summary>
        /// 取一个**稳定**的漫游点（玩家 radius 米范围内）。
        ///
        /// 关键：漫游点算出来就固定不动，只有满足以下任一条件才重算 ——
        ///   · 友军已经走到漫游点附近（1.5 米内）
        ///   · 玩家离开了原漫游点所依附的位置超过 4 米
        /// 这样友军会"走过去 → 停下 → 再选下一个点"，而不是绕着圈追一个不断跳变的目标
        /// （这正是之前"抽搐成陀螺"的原因）。
        /// </summary>
        private static Vector3 GetStableRoamPoint(AllyRecord r, GameObject player, float radius)
        {
            Vector3 pp = player.transform.position;

            // 到达点位后先停留一段时间再选下一个点。
            // 否则"一到点就换方向"会让友军在原地快速左右晃动。
            if (r.RoamRestFrames > 0)
            {
                r.RoamRestFrames--;
                if (r.HasRoamPoint)
                {
                    return r.RoamPoint;
                }
            }

            bool reached = r.HasRoamPoint
                           && Vector3.Distance(r.Go.transform.position, r.RoamPoint) < 1.2f;

            if (reached)
            {
                // 到达后休息 1.5~3 秒再选下一个点。
                // 这段时间不下发新的 suspectPos，因此角色朝向保持稳定（不抖动）。
                r.RoamRestFrames = UnityEngine.Random.Range(90, 180);
            }

            bool playerMoved = Vector3.Distance(pp, r.RoamAnchor) > 4f;

            bool needNew = !r.HasRoamPoint || (reached && r.RoamRestFrames <= 0) || playerMoved;

            if (needNew)
            {
                // 用每个友军自己的随机角度，避免所有人在同一方向
                float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float rr = radius * UnityEngine.Random.Range(0.35f, 0.90f);

                r.RoamPoint = new Vector3(pp.x + Mathf.Cos(ang) * rr,
                                          pp.y + Mathf.Sin(ang) * rr, 0f);
                r.RoamAnchor = pp;
                r.HasRoamPoint = true;
            }

            return r.RoamPoint;
        }

        private static void FreezeAt(AllyRecord r)
        {
            if (!r.Frozen)
            {
                r.Frozen = true;
                r.FrozenPos = r.Go.transform.position;
            }

            r.Go.transform.position = r.FrozenPos;
            r.StuckFrames = 0;
            r.WasTryingToMove = false;
        }

        /// <summary>
        /// 找"离玩家最近、且距离玩家不超过 maxFromPlayer"的敌人。
        ///
        /// 用户要求：进攻状态只索敌离玩家最近的敌人，且距离不超过玩家 25 米。
        /// 注意排序依据是【与玩家的距离】，而不是与友军自己的距离 ——
        /// 这样友军不会为了追一个远处的敌人而离开玩家太远。
        /// </summary>
        private static GameObject NearestToPlayer(List<GameObject> list, GameObject player, float maxFromPlayer)
        {
            GameObject best = null;
            float bestSqr = maxFromPlayer * maxFromPlayer;
            Vector3 pp = player.transform.position;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null)
                {
                    continue;
                }

                float sqr = (list[i].transform.position - pp).sqrMagnitude;
                if (sqr <= bestSqr && (best == null || sqr < bestSqr))
                {
                    bestSqr = sqr;
                    best = list[i];
                }
            }

            return best;
        }

        /// <summary>
        /// 卡住脱困。
        ///
        /// 友军是直接改 transform.position 的（绕开游戏自身的碰撞推挤逻辑），
        /// 所以容易钻进场景物件里出不来。这里检测"想动但几乎没动"，
        /// 持续卡住时就沿朝向目标的方向做几次小幅位移把它推出来。
        /// </summary>
        private static void TickStuckDetection(AllyRecord r, Vector3 desired)
        {
            try
            {
                float moved = Vector3.Distance(r.Go.transform.position, r.LastPos);

                if (r.WasTryingToMove && moved < 0.02f)
                {
                    r.StuckFrames++;
                }
                else
                {
                    r.StuckFrames = 0;
                }

                r.LastPos = r.Go.transform.position;
                r.WasTryingToMove = true;

                // 约 0.5 秒（30 帧）都没挪动 → 判定卡住
                if (r.StuckFrames >= 30)
                {
                    r.StuckFrames = 0;

                    Vector3 dir = (desired - r.Go.transform.position);
                    if (dir.sqrMagnitude < 0.0001f)
                    {
                        // 目标就在脚下：向随机方向脱困
                        float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                        dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                    }
                    else
                    {
                        dir = dir.normalized;
                    }

                    // 尝试多个方向，优先朝目标推
                    float[] offsets = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        Vector3 d = Rotate(dir, offsets[i]);
                        Vector3 candidate = r.Go.transform.position + d * 1.2f;

                        // 候选点不撞墙才采用
                        RaycastHit2D hit = Physics2D.Raycast(
                            r.Go.transform.position + new Vector3(0f, 0.3f, 0f),
                            d,
                            1.2f,
                            LayerMask.GetMask("Wall"));

                        if (!hit || hit.transform.tag != "Building")
                        {
                            r.Go.transform.position = candidate;
                            if (HireTuning.Verbose)
                            {
                                HireNpcPlugin.Log.LogInfo("[脱困] 友军 " + r.Go.name
                                    + " 卡住，已平移 " + offsets[i] + "° 脱出。");
                            }
                            return;
                        }
                    }

                    // 八个方向都堵住：直接朝目标做一次无视碰撞的小位移，避免永久卡死
                    r.Go.transform.position = Vector3.MoveTowards(
                        r.Go.transform.position, desired, 0.8f);

                    if (HireTuning.Verbose)
                    {
                        HireNpcPlugin.Log.LogInfo("[脱困] 友军 " + r.Go.name
                            + " 被完全围住，已强制朝目标平移。");
                    }
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("TickStuckDetection 异常: " + ex.Message);
            }
        }

        private static Vector3 Rotate(Vector3 v, float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector3(v.x * cos - v.y * sin, v.x * sin + v.y * cos, 0f);
        }

        /// <summary>
        /// 目标位置归位（旧方案遗留的入口，现已由 AllyRecord.Frozen/FrozenPos 取代）。
        /// 保留为无副作用实现，避免旧引用残留。
        /// </summary>
        internal static bool TryIsPositionLocked(GameObject go, out Vector3 lockedPos)
        {
            lockedPos = Vector3.zero;

            try
            {
                AllyRecord r = AllyRegistry.Find(go);
                if (r == null || !r.Frozen)
                {
                    return false;
                }

                lockedPos = r.FrozenPos;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>目标是玩家或任意友军都算"本方"。</summary>
        private static bool IsFriendlyTarget(GameObject tgt, GameObject player)
        {
            if (tgt == null)
            {
                return false;
            }

            if (tgt == player)
            {
                return true;
            }

            if (tgt.GetComponent<AllyMarker>() != null)
            {
                return true;
            }

            CharaState st = tgt.GetComponent<CharaState>();
            return st != null && st.CheckParty() == 0;
        }

        private static float Dist(GameObject a, GameObject b)
        {
            return Vector3.Distance(a.transform.position, b.transform.position);
        }

        /// <summary>
        /// CharaControl.cs:1265 的更新总门槛含 !isFar。远距离剔除会把 isFar 置 true
        /// （CharaControl.cs:1217-1263），导致 AI 停止工作。这里强制清掉。
        /// </summary>
        private static void ClearCullFlags(CharaControl ctrl)
        {
            try
            {
                if (_fIsFar != null && (bool)_fIsFar.GetValue(ctrl))
                {
                    _fIsFar.SetValue(ctrl, false);
                }

                if (_fIsIdle != null && (bool)_fIsIdle.GetValue(ctrl))
                {
                    _fIsIdle.SetValue(ctrl, false);
                }
            }
            catch
            {
                // 反射失败不影响主流程
            }
        }

        /// <summary>
        /// 自研移动：优先直线，遇墙用游戏自己的 TerrainManager.FindWay 绕行。
        /// 完全绕开 CharaControl.MoveToTarget（它依赖 party 0 单位拿不到的私有 tempPos）。
        /// </summary>
        /// <summary>
        /// 瞬移归队 —— 与宠物 ComeBack 一致的做法（PetControl.ComeBack 直接把
        /// transform.position 设为玩家位置，见 PetControl.cs:347-357）。
        ///
        /// 用瞬移而不是"慢速拖拽"：友军直接改 transform.position 会穿过墙体，
        /// 若用缓慢拖拽，目标在墙后时会被卡住、看起来像被"拉扯"。瞬移一次到位更干净。
        ///
        /// 落点：以玩家为中心、半径 FollowNear×0.6 的环上（保证在 5 米范围内），
        /// 并逐个候选点检查是否在墙内，尽量选一个不撞墙的位置。
        /// </summary>
        private static void TeleportNearPlayer(AllyRecord r, GameObject player, float withinMeters)
        {
            try
            {
                Vector3 center = player.transform.position;
                float radius = Mathf.Clamp(withinMeters * 0.7f, 1.0f, withinMeters);

                // 先在 8 个方向里找不撞墙的落点
                for (int i = 0; i < 8; i++)
                {
                    float ang = (45f * i) * Mathf.Deg2Rad;
                    Vector3 candidate = center + new Vector3(
                        Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius, 0f);

                    RaycastHit2D hit = Physics2D.Raycast(
                        center + new Vector3(0f, 0.3f, 0f),
                        new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f),
                        radius,
                        LayerMask.GetMask("Wall"));

                    if (!hit || hit.transform.tag != "Building")
                    {
                        r.Go.transform.position = candidate;
                        // 让游戏自身的移动系统从新位置重新出发，避免残留旧路径
                        CharaControl cc = r.Control;
                        if (cc != null)
                        {
                            cc.GetSuspectPos(candidate);
                        }
                        AfterTeleport(r);
                        return;
                    }
                }

                // 八个方向都撞墙：直接落到玩家脚下
                r.Go.transform.position = center;
                AfterTeleport(r);

                if (HireTuning.Verbose)
                {
                    HireNpcPlugin.Log.LogInfo("[归队] 友军 " + r.Go.name
                        + " 周围均被阻挡，已瞬移到玩家位置。");
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("TeleportNearPlayer 异常: " + ex.Message);
            }
        }

        private static void AfterTeleport(AllyRecord r)
        {
            r.StuckFrames = 0;
            r.WasTryingToMove = false;
            r.LastPos = r.Go.transform.position;      // 瞬移后必须重置，否则会被误判为"卡住"
            r.Frozen = false;

            if (HireTuning.Verbose)
            {
                HireNpcPlugin.Log.LogInfo("[归队] 友军 " + r.Go.name + " 已瞬移归队 → "
                    + r.Go.transform.position);
            }
        }

        private static void MoveToward(AllyRecord r, CharaControl ctrl, Vector3 desired)
        {
            try
            {
                Vector3 pos = r.Go.transform.position;
                float speed = GetSpeed(ctrl);

                // 与目标之间是否有墙
                bool blocked = false;
                RaycastHit2D hit = Physics2D.Raycast(
                    pos + new Vector3(0f, 0.3f, 0f),
                    (desired + new Vector3(0f, 0.3f, 0f)) - (pos + new Vector3(0f, 0.3f, 0f)),
                    Vector3.Distance(desired, pos),
                    LayerMask.GetMask("Wall"));
                if (hit && hit.transform.tag == "Building")
                {
                    blocked = true;
                }

                if (blocked)
                {
                    TerrainManager ter = GameRefs.Terrain;
                    if (ter != null)
                    {
                        List<Node> path = ter.FindWay(pos, desired, 2000);
                        if (path != null && path.Count > 0)
                        {
                            Vector3 step = path[0].scenePos + new Vector3(0.5f, 0.5f, 0f);
                            r.Go.transform.position = Vector3.MoveTowards(
                                pos, step, Time.deltaTime * speed);
                            return;
                        }
                    }
                }

                r.Go.transform.position = Vector3.MoveTowards(pos, desired, Time.deltaTime * speed);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("MoveToward 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 速度口径与游戏一致：finalSpeed = BaseSpeed * movSpd * 0.01 * 0.01
        /// （参考 CharaControl.cs:1291 玩家与 AI 共用的公式）。
        /// 若反射能读到 finalSpeed 就沿用游戏算好的值。
        /// </summary>
        private static float GetSpeed(CharaControl ctrl)
        {
            try
            {
                if (_fFinalSpeed != null)
                {
                    object v = _fFinalSpeed.GetValue(ctrl);
                    if (v is float && (float)v > 0.01f)
                    {
                        // 游戏内部乘以 2.5 才是每帧位移；这里换算成"每秒"尺度
                        return Mathf.Max(1.5f, (float)v * 2.5f * 60f * Time.deltaTime);
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return 3.0f;   // 兜底：约 3 单位/秒
        }
    }
}
