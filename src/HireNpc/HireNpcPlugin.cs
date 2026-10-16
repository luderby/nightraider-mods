using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 夜袭者「雇佣友方 NPC」MOD。
    ///
    /// 设计要点（全部来自阶段 0 实测，详见 _moddev/docs/）：
    ///   1. 友军用 party 0（玩家阵营）生成 → 引擎自带的阵营判定保证互相不伤害
    ///      （伤害门槛：FXControl.cs:1164 / CharaControl.cs:1715 要求 party 不同）
    ///   2. 生成必须"先登记后生成"：MonsterManager.CreateEnemey 内部在返回前调用
    ///      GetCloseTgt(nowChara)（MonsterManager.cs:39-42），若登记晚了，拦截器来不及
    ///      生效，友军就会一出生朝玩家开枪
    ///   3. 索敌必须主动驱动：LookArea.cs:90 排除 party 0
    ///   4. 移动必须自研：CharaControl.MoveToTarget 依赖 party 0 拿不到的 tempPos
    ///   5. 阵营守护：拦截 CharaState.SetParty，拒绝任何把友军改成非 0 阵营的调用
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class HireNpcPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "nightraider.hirenpc";
        public const string PluginName = "Night Raider Hire NPC";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyCode> CfgPanelKey;
        internal static ConfigEntry<KeyCode> CfgModeKey;
        internal static ConfigEntry<int> CfgMaxHired;
        internal static ConfigEntry<bool> CfgFollowFar;
        internal static ConfigEntry<float> CfgFollowFarDist;
        internal static ConfigEntry<float> CfgFollowNearDist;
        internal static ConfigEntry<bool> CfgVerbose;
        internal static ConfigEntry<bool> CfgUseGameCursor;
        internal static ConfigEntry<bool> CfgAllyName;
        internal static ConfigEntry<bool> CfgAllyHpBar;
        internal static ConfigEntry<bool> CfgAllyDistance;
        internal static ConfigEntry<bool> CfgAllyTalk;
        internal static ConfigEntry<bool> CfgAutoRecover;
        internal static ConfigEntry<float> CfgRecoverSeconds;
        internal static ConfigEntry<float> CfgRecoverRadius;

        internal static ConfigFile ConfigRef;

        private Harmony _harmony;
        private GameObject _brainGo;
        private static bool _wasInMission;
        private static int _emptyFrames;
        private bool _statsVerified;
        private bool _hiredLoaded;

        /// <summary>已雇佣的档位 Key（上场名单）。</summary>
        internal static readonly List<HireOrder> Hired = new List<HireOrder>();

        private void Awake()
        {
            Log = Logger;
            ConfigRef = Config;

            // ── 热键：避开游戏占用（F1截图/F2更新日志/F5笔记/F9丢物品）与其它MOD（F6/F7/F8）──
            CfgPanelKey = Config.Bind("General", "TogglePanelKey", KeyCode.F3,
                "打开/关闭『雇佣 NPC』面板。");
            CfgModeKey = Config.Bind("General", "CycleModeKey", KeyCode.X,
                "打开/关闭友军指令菜单（菜单内用 1/2/3 选择指令）。\n" +
                "X 同时是游戏的『无人机切换』键（GameManager.cs:551，生效条件为 nowUAV != null）。\n" +
                "本 MOD 只在『当前没有可用无人机』时响应 X；正在飞无人机时 X 完全归游戏使用。\n" +
                "交互方式与宠物的 Z→1..6 指令菜单一致（OrderControl.cs）。");

            // ── 友军标识（IMGUI 绘制在友军头顶）─────────────────────────────────
            CfgAllyName = Config.Bind("AllyMark", "ShowName", true,
                "在友军头顶显示名称与当前指令（如「[跟随] 步枪兵」）。\n" +
                "敌人没有该标签，是最直接的区分方式。");
            CfgAllyHpBar = Config.Bind("AllyMark", "ShowHpBar", true,
                "在友军头顶显示血条。");
            CfgAllyDistance = Config.Bind("AllyMark", "ShowDistance", false,
                "在友军头顶显示与你的距离，便于观察 25 米瞬移阈值。");

            CfgAllyTalk = Config.Bind("AllyMark", "AllyTalk", true,
                "让护航队员时不时冒出随机台词（受伤、开火、闲聊）。");
            CfgMaxHired = Config.Bind("General", "MaxHired", 3,
                "最多可雇佣的护航队员数量。\n" +
                "0 = 不限制。默认 3。");

            // ── 行为半径（按用户设定）──────────────────────────────────────────
            // 跟随：在玩家 8 米范围内移动漫游（用户指定，从 5 米改回 8 米）
            CfgFollowNearDist = Config.Bind("Behavior", "FollowRoamRadius", 8.0f,
                "『跟随』模式下友军的活动半径（米）。\n" +
                "友军会在此半径内自由移动/漫游，超出会被平滑拉回边界，但不会瞬移。");
            // 停留：不动（无半径概念）
            // 瞬移阈值
            CfgFollowFarDist = Config.Bind("Behavior", "TeleportBackDistance", 25.0f,
                "超过此距离时，友军瞬移归队（照搬宠物 ComeBack 的做法）。\n" +
                "此值应大于跟随的活动半径，否则会在边界反复触发（默认 25 米）。");

            CfgVerbose = Config.Bind("General", "VerboseLog", true,
                "输出详细调试日志（含友军每 2 秒的行为快照）。排查问题时建议保持开启。");

            // ── 脱离战斗后的自动恢复 ───────────────────────────────────────
            CfgAutoRecover = Config.Bind("Recover", "AutoRecover", true,
                "周围没有敌人满一定时间后，自动为护航清除异常状态（中毒/流血等）。\n" +
                "注意：按需求已取消「补满血量与护甲」的功能。");
            CfgRecoverSeconds = Config.Bind("Recover", "RecoverSeconds", 15f,
                "脱离战斗多少秒后触发恢复（秒）。");
            CfgRecoverRadius = Config.Bind("Recover", "SafeRadius", 20f,
                "判定「周围安全」的半径（米）。此半径内没有敌人即视为脱离战斗。");

            CfgUseGameCursor = Config.Bind("General", "UseGameCrosshairCursor", true,
                "开启面板时使用游戏的动作光标（GameManager.HandleIcon）。\n" +
                "若面板能点击但看不到鼠标指针，把此项设为 false 可改用系统默认光标。");

            HireData.ApplyConfig(Config);

            // 0 = 不限制（用户要求先解除限制以便测试）
            HireTuning.MaxHired = Mathf.Max(0, CfgMaxHired.Value);
            HireTuning.FollowFar = Mathf.Max(5f, CfgFollowFarDist.Value);          // 瞬移阈值
            // 说明：AssaultRange（"警惕"模式的半径）已随该指令一并移除。
            HireTuning.FollowNear = Mathf.Max(1f, CfgFollowNearDist.Value);        // 跟随漫游半径
            HireTuning.Verbose = CfgVerbose.Value;

            // 台词文本（可在 cfg 的 [Talk] 段直接修改）
            AllyTalk.Bind(Config);

            // 若存在外部台词文件，则以文件内容覆盖 cfg（见 TalkFile 的搜索顺序）
            TalkFile.LoadAndApply();
            HireTuning.AllyTalk = CfgAllyTalk == null || CfgAllyTalk.Value;

            // 脱离战斗后的自动恢复
            HireTuning.AutoRecover = CfgAutoRecover == null || CfgAutoRecover.Value;
            HireTuning.RecoverSeconds = CfgRecoverSeconds != null
                ? Mathf.Max(1f, CfgRecoverSeconds.Value) : 15f;
            HireTuning.RecoverSafeRadius = CfgRecoverRadius != null
                ? Mathf.Max(3f, CfgRecoverRadius.Value) : 20f;

            Log.LogInfo("恢复设置: 自动恢复=" + HireTuning.AutoRecover
                        + "，脱离战斗 " + HireTuning.RecoverSeconds + " 秒后触发"
                        + "，安全半径 " + HireTuning.RecoverSafeRadius + "m");

            Log.LogInfo("行为参数: 跟随漫游半径=" + HireTuning.FollowNear
                        + "m，瞬移阈值=" + HireTuning.FollowFar + "m");

            // 确保面板与指令菜单都被构建出来（含指令菜单文本）
            try
            {
                HirePanel.EnsureBuilt();
            }
            catch (Exception ex)
            {
                Log.LogWarning("HirePanel.EnsureBuilt 异常: " + ex.Message);
            }

            try
            {
                _harmony = new Harmony(PluginGuid);
                InstallPatches();
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony 初始化失败: " + ex);
            }

            // 常驻大脑（驱动索敌与自研移动）
            try
            {
                _brainGo = new GameObject("NightRaider_HireNpcBrain");
                UnityEngine.Object.DontDestroyOnLoad(_brainGo);
                _brainGo.AddComponent<AllyBrain>();
                _brainGo.AddComponent<HirePanelDriver>();
            }
            catch (Exception ex)
            {
                Log.LogError("创建大脑对象失败: " + ex);
            }

            Hired.Clear();
            Hired.AddRange(HireStore.LoadOrders());

            Log.LogInfo(PluginName + " v" + PluginVersion + " 已加载。");
            Log.LogInfo("热键: 雇佣面板=" + CfgPanelKey.Value
                        + "；关卡内指令菜单=" + CfgModeKey.Value
                        + "；护航上限=" + HireTuning.MaxHired + " 人");
            Log.LogInfo("已载入雇佣名单: " + DescribeOrders());
        }

        /// <summary>
        /// 撤离成功的钩子（TerrainManager.EscapeBattleOver）。
        ///
        /// 这是玩家"撤离回营地"真正走的路径；CreateWorld 是它在 1.6 秒后才触发的后续步骤，
        /// 只挂后者会漏计数。这里就地消耗一次护航次数。
        /// </summary>
        internal static void PostEscapeBattleOver()
        {
            try
            {
                Log.LogInfo("检测到撤离跳转（EscapeBattleOver），在回营地时结算护航次数。");
                _evacuationPending = true;
            }
            catch (Exception ex)
            {
                Log.LogError("PostEscapeBattleOver 异常: " + ex);
            }
        }

        /// <summary>
        /// 任务达成/全歼后的结算（TerrainManager.BattleOver，TerrainManager.cs:1529）。
        /// 这条路径同样算作"一次成功的护航"。
        /// </summary>
        internal static void PostBattleOver()
        {
            try
            {
                Log.LogInfo("检测到任务结束（BattleOver），在回营地时结算护航次数。");
                _evacuationPending = true;
            }
            catch (Exception ex)
            {
                Log.LogError("PostBattleOver 异常: " + ex);
            }
        }

        /// <summary>标记"本局以撤离/达成结束"，实际计数在回到营地时进行。</summary>
        private static bool _evacuationPending;

        /// <summary>
        /// 结算本局结果。
        ///
        /// 用户要求：**死亡算撤离失败，不扣护航次数**。
        /// 因此只有"活着撤离/任务达成"才消耗一次合约。
        /// </summary>
        private static void SettleMissionResult()
        {
            try
            {
                if (!_evacuationPending)
                {
                    return;
                }

                _evacuationPending = false;

                if (_playerDiedThisMission)
                {
                    Log.LogInfo("本局撤离失败（玩家阵亡），不消耗护航次数。");
                    HirePanel.Notify("撤离失败：护航次数未消耗");
                    return;
                }

                Log.LogInfo("本局撤离成功，记账一次护航。");
                OnMissionEvacuated();
            }
            catch (Exception ex)
            {
                Log.LogError("SettleMissionResult 异常: " + ex);
            }
        }

        /// <summary>CharaState 死亡方法的候选名（不同版本命名不同，逐个尝试）。</summary>
        private static readonly string[] DeathMethodNames = { "Die", "Dead", "Down", "SetDie" };

        /// <summary>
        /// 敌人死亡钩子（击杀台词）。
        ///
        /// CharaState 的死亡方法名在不同版本可能是 Die / Dead / Down，
        /// 因此这里按候选名逐个尝试挂补丁。
        /// </summary>
        internal static void PostEnemyDied(CharaState __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                // 玩家阵亡 → 本局记为失败。
                // 必须在这里记，不能事后判断：回营地时 CreatePlayer + ResetData(isP:true)
                // 会把玩家血量重置为满，IsAlive() 就永远是 true 了
                //（用户实测"阵亡撤离仍扣次数"就是这个原因）。
                if (__instance.CheckParty() == 0)
                {
                    // party 0 且不是我们的护航 → 玩家本人
                    if (__instance.GetComponent<AllyMarker>() == null)
                    {
                        _playerDiedThisMission = true;
                        Log.LogInfo("检测到玩家阵亡，本局将判定为撤离失败。");

                        // 让所有在场护航喊一句"玩家倒下"
                        for (int i = 0; i < AllyRegistry.Count; i++)
                        {
                            AllyRecord pr = AllyRegistry.At(i);
                            if (pr != null)
                            {
                                pr.TalkPlayerDead = true;
                            }
                        }
                    }

                    return;
                }

                // 只关心"敌人被打死" → 触发击杀台词
                if (__instance.CheckParty() == 7)
                {
                    return;
                }

                // 击杀归属：CharaState.finalDamageSource 记录了最后一击的来源。
                // 若来源是我们的护航 → 他自己说"击杀台词"；
                // 其余同伴（以及玩家击杀的情况）说"他人击杀台词"。
                GameObject killer = null;
                try
                {
                    if (_fFinalDamageSource == null)
                    {
                        _fFinalDamageSource = AccessTools.Field(typeof(CharaState), "finalDamageSource");
                    }

                    if (_fFinalDamageSource != null)
                    {
                        killer = _fFinalDamageSource.GetValue(__instance) as GameObject;
                    }
                }
                catch
                {
                    // 取不到就按"他人击杀"处理
                }

                AllyRecord killerRec = killer != null ? AllyRegistry.Find(killer) : null;

                for (int i = 0; i < AllyRegistry.Count; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || !r.IsAlive)
                    {
                        continue;
                    }

                    if (killerRec != null && ReferenceEquals(r, killerRec))
                    {
                        r.TalkKillSelf = true;
                    }
                    else
                    {
                        r.TalkKillOther = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PostEnemyDied 异常: " + ex);
            }
        }

        private static FieldInfo _fFinalDamageSource;

        /// <summary>本局玩家是否阵亡过（进入关卡时清零）。</summary>
        private static bool _playerDiedThisMission;

        /// <summary>把订单列表拼成一行日志文本。</summary>
        internal static string DescribeOrders()
        {
            if (Hired.Count == 0)
            {
                return "(空)";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Hired.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                HireOrder o = Hired[i];
                HireTier t = HireData.Find(o.TierKey);
                sb.Append(t != null ? t.Name : o.TierKey)
                  .Append("(").Append(o.Used).Append("/").Append(o.Total).Append(")");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 一次成功的护航 = 一次成功撤离。
        ///
        /// 由 TerrainManager.EscapeBattleOver / BattleOver 的 Postfix 触发
        /// （那才是"回营地"的真正入口）。注意**不能**放在 PostCreateWorld 里，
        /// 否则 BattleOver 与 EscapeBattleOver 都会各触发一次 CreateWorld，导致重复计数。
        /// </summary>
        internal static void OnMissionEvacuated()
        {
            try
            {
                if (Hired.Count == 0)
                {
                    return;
                }

                List<HireOrder> finished = new List<HireOrder>();

                for (int i = 0; i < Hired.Count; i++)
                {
                    HireOrder o = Hired[i];
                    o.Used++;

                    HireTier t = HireData.Find(o.TierKey);
                    string name = t != null ? t.Name : o.TierKey;

                    Log.LogInfo("护航撤离成功：「" + name + "」已使用 "
                                + o.Used + "/" + o.Total + " 次。");

                    if (!o.Active)
                    {
                        finished.Add(o);
                        Log.LogInfo("「" + name + "」合约完成（共 " + o.Total + " 次撤离），已结束护航。");
                    }
                }

                for (int i = 0; i < finished.Count; i++)
                {
                    Hired.Remove(finished[i]);
                }

                HireStore.SaveOrders(Hired);

                if (finished.Count > 0)
                {
                    HirePanel.Notify("有 " + finished.Count + " 名护航合约到期，已结束服务。");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("OnMissionEvacuated 异常: " + ex);
            }
        }

        private void Update()
        {
            try
            {
                // 字体升级：首次构建时游戏字体可能尚未加载（实测拿到的是没有中文字形的
                // LegacyRuntime，导致中文全空白）。这里持续检查，一旦能拿到游戏字体就替换。
                TryUpgradeFont();

                // 热键
                if (Input.GetKeyDown(CfgPanelKey.Value))
                {
                    HirePanel.Toggle();
                }

                // X：打开/关闭友军指令菜单（与游戏的无人机切换键按情境共用）。
                // 菜单打开后由 1/2/3 选择指令，交互方式与宠物的 Z→1..6 一致。
                if (Input.GetKeyDown(CfgModeKey.Value) && !IsUavHotkeyContext())
                {
                    HireOrderControl.Toggle();
                }

                // 说明：测试用热键（F10 手动回收 / F12 生成测试丧尸 / F11 导出模型清单）
                // 已全部移除。护航队员会在进入关卡地图时自动生成、离开时自动回收。

                // 进图 / 回营地检测。
                // 权威判定来自 TerrainManager.CreateWorld 的 mapTP（见 PostCreateWorld）；
                // 这里的轮询只作为 Harmony 失效时的兜底，且优先信任 mapTP 标记。
                bool inMission = InMission;

                if (inMission && !_wasInMission)
                {
                    _wasInMission = true;
                    _allowAutoRespawn = true;
                    AllySpawner.SpawnHiredForMission();
                }
                else if (!inMission && _wasInMission)
                {
                    _wasInMission = false;
                    AllySpawner.RecallAll("离开关卡");
                    _allowAutoRespawn = true;
                }

                // 场上友军全没了（换图/全员阵亡）时允许重新生成。
                // 但手动 F10 回收后不重刷，否则会出现"清空即刷新"。
                if (inMission && _wasInMission && _allowAutoRespawn
                    && AllyRegistry.Count == 0 && HireNpcPlugin.Hired.Count > 0)
                {
                    _emptyFrames++;
                    if (_emptyFrames > 240)
                    {
                        _emptyFrames = 0;
                        HireNpcPlugin.Log.LogInfo("场上已无友军，重新生成。");
                        AllySpawner.SpawnHiredForMission();
                    }
                }
                else
                {
                    _emptyFrames = 0;
                }

                // 一次性自检：等游戏数据管理器就绪后，校验档位模型并用真实数值刷新展示。
                if (!_statsVerified && GameRefs.Data != null && GameRefs.Resources != null)
                {
                    _statsVerified = true;
                    HireData.VerifyAndRefreshStats();
                }

                // 存档位就绪后重新载入雇佣名单。
                // 启动时 archiveFileID 仍是 0，按槽位分文件的名单那时读不出来，
                // 所以必须等存档真正载入后再读一次。
                if (!_hiredLoaded && GameRefs.IsSaveLoaded())
                {
                    _hiredLoaded = true;
                    List<HireOrder> reloaded = HireStore.LoadOrders();

                    // 过滤掉已不存在的旧档位 Key（例如改版前的 Rifleman/Elite），
                    // 以及合约已用完的订单（Used >= Total）。
                    List<HireOrder> valid = new List<HireOrder>();
                    for (int i = 0; i < reloaded.Count; i++)
                    {
                        HireOrder o = reloaded[i];
                        if (HireData.Find(o.TierKey) == null)
                        {
                            Log.LogInfo("已忽略失效的旧档位 Key：" + o.TierKey);
                            continue;
                        }

                        if (!o.Active)
                        {
                            Log.LogInfo("订单合约已完成，自动结束：" + o.TierKey
                                        + "（" + o.Used + "/" + o.Total + "）");
                            continue;
                        }

                        valid.Add(o);
                    }

                    Hired.Clear();
                    Hired.AddRange(valid);

                    if (valid.Count != reloaded.Count)
                    {
                        HireStore.SaveOrders(Hired);
                    }

                    Log.LogInfo("存档已载入，护航订单(" + GameRefs.Game.archiveFileID + "): "
                                + DescribeOrders());
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Update 异常: " + ex);
            }
        }

        /// <summary>
        /// X 键的归属判定。
        ///
        /// 游戏在 GameManager.cs:551 用 X 切换无人机控制，生效条件是：
        ///     nowChara != null &amp;&amp; nowUAV != null &amp;&amp; !TalkSystem.CheckOpen()
        /// 因此"当前正在操控无人机"或"存在可用无人机"时，X 应完全让给游戏。
        /// 其余情况（绝大多数时间）X 用于切换友军指令。
        /// </summary>
        private static bool IsUavHotkeyContext()
        {
            try
            {
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null)
                {
                    return false;
                }

                // 正在操控无人机 → X 归游戏（用于退出操控）
                if (cur.isUAVControl)
                {
                    return true;
                }

                // 存在无人机 → 游戏的 X 会尝试进入操控，让给游戏避免歧义
                if (cur.nowUAV != null)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 持续尝试把 UI 字体升级为游戏字体（含中文字形）。
        /// 不做成一次性：构建时机可能早于游戏字体加载完成。
        /// </summary>
        private void TryUpgradeFont()
        {
            if (_fontUpgraded)
            {
                return;
            }

            try
            {
                // 每 30 帧查一次，避免每帧遍历字体资源
                if (Time.frameCount % 30 != 0)
                {
                    return;
                }

                if (GameRefs.IsFontUsable(GameRefs.FindUiFont()))
                {
                    _fontUpgraded = true;
                    HirePanel.ApplyFont();
                    HireOrderControl.ApplyFont();
                    Log.LogInfo("[字体] 已升级为游戏字体，UI 文字应可正常显示中文。");
                }            }
            catch (Exception ex)
            {
                Log.LogWarning("TryUpgradeFont 异常: " + ex.Message);
            }
        }

        private static bool _fontUpgraded;

        private void CycleModeForAll()
        {
            try
            {
                int n = AllyRegistry.Count;
                if (n == 0)
                {
                    Log.LogInfo("当前没有友军，无法切换指令。");
                    return;
                }

                // 以第一个友军的模式为基准，全体统一切到下一个模式
                AllyMode cur = AllyRegistry.At(0).Mode;
                AllyMode next = (AllyMode)(((int)cur + 1) % 3);

                for (int i = 0; i < n; i++)
                {
                    AllyRegistry.At(i).Mode = next;
                }

                Log.LogInfo("所有友军指令切换为: " + ModeName(next) + "（" + n + " 人）");
                HirePanel.Notify("友军指令：" + ModeName(next));
            }
            catch (Exception ex)
            {
                Log.LogError("CycleModeForAll 异常: " + ex);
            }
        }

        internal static string ModeName(AllyMode m)
        {
            switch (m)
            {
                case AllyMode.Hold: return "驻守中";
                default: return "跟随中";
            }
        }

        /// <summary>指令菜单 / 界面提示里用的动作名（不含"中"）。（保留给界面文案使用）</summary>
        internal static string ModeActionName(AllyMode m)
        {
            switch (m)
            {
                case AllyMode.Hold: return "驻守";
                default: return "跟随";
            }
        }

        // ------------------------------------------------------------------
        // Harmony 补丁（全部显式安装并自检；不要用 PatchAll —— 它在实测中静默失效）
        // ------------------------------------------------------------------

        private void InstallPatches()
        {
            // Patch(type, method, postfixName, prefixName)
            Patch("CharaState", "SetParty", null, nameof(PreventAllyFactionChange));

            // GetCloseTgt 与 GetTargetListObj 都是公开的目标设置入口，两个都要堵。
            Patch("CharaControl", "GetCloseTgt", null, nameof(PreventAllyTargetingPlayer));
            Patch("CharaControl", "GetTargetListObj", null, nameof(PreventAllyTargetingPlayer));

            // 时序修复：CreateEnemey 返回前会调用 GetCloseTgt(nowChara)，
            // 所以必须在它执行期间就能认出友军（Prefix 登记 + Postfix 回填）。
            Patch("MonsterManager", "CreateEnemey", nameof(PostCreateEnemey), nameof(PreCreateEnemey));

            Patch("TerrainManager", "CreateWorld", nameof(PostCreateWorld), null);
            Patch("GameManager", "CheckWindowOpen", null, nameof(ForceWindowOpen));

            // 指令菜单打开时屏蔽 Alpha1/2/3，避免顺带切换快捷栏。
            //
            // 不用补丁 UnityEngine.Input（它有多重重载，AccessTools 按类型匹配会失败，实测报
            // "Input.GetKeyDown 未找到"）。改为拦截游戏自己的键位查询：
            // 快捷栏切换走 KeyCustomizationManager.GetKeyCode("ALP1"..)（CharaControl.cs:599-647），
            // 菜单打开期间让这三个键返回 KeyCode.None，游戏就收不到按键了。
            // 这完全不触碰 Unity API，也不影响其它按键。
            Patch("KeyCustomizationManager", "GetKeyCode", nameof(SuppressHotbarKeysWhileMenuOpen), null);

            // 阻止宠物把友军当敌人。
            // 三层拦截，覆盖"发现 → 采纳 → 追击"整条链：
            //   1. PetSearch.OnTriggerEnter2D —— 索敌触发器（PetSearch.cs:37 只判 tag=="Enemy"）
            //   2. PetControl.GetTarget        —— 采纳目标
            //   3. PetControl.GoToTarget       —— 追击执行
            Patch("PetControl", "OnTriggerStay2D", null, nameof(PreventPetAttackInTrigger));
            Patch("PetSearch", "OnTriggerEnter2D", null, nameof(PreventPetSearchAlly));
            Patch("PetControl", "GetTarget", null, nameof(PreventPetGetAllyTarget));
            Patch("PetControl", "GoToTarget", null, nameof(PreventPetChaseAlly));
            Patch("CharaState", "BeHit", null, nameof(PreventPetDamagingAlly));

            // 武器类型限制：明星护/魔王护 只配发步枪
            Patch("DataHub", "GetAIWeaponByHardcode", nameof(RestrictToRifle), null);

            // 撤离成功 = 一次护航消耗。
            // 撤离走的是 TerrainManager.EscapeBattleOver（TerrainManager.cs:1572），
            // 而它内部才去 CreateWorldWait(0, 1.6f)。只挂 CreateWorld 会漏掉计数
            // （用户实测表现为"撤离没有被正确计数"），因此直接挂撤离跳转本身。
            Patch("TerrainManager", "EscapeBattleOver", nameof(PostEscapeBattleOver), null);
            Patch("TerrainManager", "BattleOver", nameof(PostBattleOver), null);

            // 敌人死亡 → 击杀台词（方法名按候选逐个尝试）
            for (int i = 0; i < DeathMethodNames.Length; i++)
            {
                Patch("CharaState", DeathMethodNames[i], nameof(PostEnemyDied), null);
            }

            Verify("CharaState", "SetParty", "阵营守护");
            Verify("CharaControl", "GetCloseTgt", "禁止锁定玩家(A)");
            Verify("CharaControl", "GetTargetListObj", "禁止锁定玩家(B)");
            Verify("MonsterManager", "CreateEnemey", "生成前预登记(时序)");
            Verify("TerrainManager", "CreateWorld", "进图/回营地钩子");
            Verify("GameManager", "CheckWindowOpen", "面板模态化(光标/快捷栏)");
            Verify("KeyCustomizationManager", "GetKeyCode", "指令菜单屏蔽快捷栏键");
            Verify("PetControl", "OnTriggerStay2D", "宠物不攻击友军(攻击)");
            Verify("PetSearch", "OnTriggerEnter2D", "宠物不索敌友军");
            Verify("PetControl", "GetTarget", "宠物不采纳友军目标");
            Verify("PetControl", "GoToTarget", "宠物不追击友军");
            Verify("CharaState", "BeHit", "宠物不伤友军(兜底)");
            Verify("TerrainManager", "EscapeBattleOver", "撤离成功→消耗护航次数");
            Verify("TerrainManager", "BattleOver", "任务结束→消耗护航次数");
            Verify("CharaState", "Die", "击杀台词");
        }

        private void Patch(string typeName, string methodName, string postfix, string prefix,
                           Type[] argTypes = null)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null)
                {
                    Log.LogWarning("找不到类型 " + typeName + "，跳过补丁 " + methodName);
                    return;
                }

                MethodInfo mi = argTypes != null
                    ? AccessTools.Method(t, methodName, argTypes)
                    : AccessTools.Method(t, methodName);
                if (mi == null)
                {
                    Log.LogWarning("找不到方法 " + typeName + "." + methodName + "，跳过");
                    return;
                }

                HarmonyMethod pre = prefix == null
                    ? null
                    : new HarmonyMethod(AccessTools.Method(typeof(HireNpcPlugin), prefix));
                HarmonyMethod post = postfix == null
                    ? null
                    : new HarmonyMethod(AccessTools.Method(typeof(HireNpcPlugin), postfix));

                _harmony.Patch(mi, pre, post);
            }
            catch (Exception ex)
            {
                Log.LogWarning("补丁 " + typeName + "." + methodName + " 失败: " + ex.Message);
            }
        }

        private void Verify(string typeName, string methodName, string what)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                MethodInfo mi = t == null ? null : AccessTools.Method(t, methodName);
                if (mi == null)
                {
                    Log.LogWarning("[VERIFY] " + typeName + "." + methodName + " 未找到：" + what);
                    return;
                }

                Patches info = Harmony.GetPatchInfo(mi);
                int pre = info != null && info.Prefixes != null ? info.Prefixes.Count : 0;
                int post = info != null && info.Postfixes != null ? info.Postfixes.Count : 0;
                Log.LogInfo("[VERIFY] " + typeName + "." + methodName + " (" + what + ") -> Prefix="
                            + pre + " Postfix=" + post + (pre + post > 0 ? "  ✅" : "  ❌未生效"));
            }
            catch (Exception ex)
            {
                Log.LogWarning("[VERIFY] 校验失败 " + typeName + "." + methodName + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 补丁实现
        // ------------------------------------------------------------------

        /// <summary>根因级：拒绝任何把友军改成非 0 阵营的调用。</summary>
        internal static bool PreventAllyFactionChange(CharaState __instance, int pt)
        {
            try
            {
                if (__instance == null || pt == 0)
                {
                    return true;
                }

                if (AllyRegistry.IsAlly(__instance.gameObject))
                {
                    if (HireTuning.Verbose)
                    {
                        Log.LogWarning("拦截策反: " + __instance.gameObject.name
                                       + " 被要求 SetParty(" + pt + ")，已拒绝。");
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PreventAllyFactionChange 异常: " + ex);
            }

            return true;
        }

        /// <summary>
        /// 禁止友军把玩家设为目标。同时覆盖 GetCloseTgt 与 GetTargetListObj
        /// （两者都是公开的目标设置入口，且 GetCloseTgt 会被 MonsterManager 在生成时强调）。
        /// </summary>
        /// <summary>
        /// 禁止友军把玩家设为目标。同时覆盖 GetCloseTgt 与 GetTargetListObj
        /// （两者都是公开的目标设置入口，且 GetCloseTgt 会被 MonsterManager 在生成时强制调用）。
        /// </summary>
        internal static bool PreventAllyTargetingPlayer(CharaControl __instance, GameObject tgt)
        {
            try
            {
                if (__instance == null || tgt == null)
                {
                    return true;
                }

                if (!AllyRegistry.IsAlly(__instance.gameObject))
                {
                    return true;
                }

                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null)
                {
                    return true;
                }

                if (tgt != cur.nowChara)
                {
                    return true;
                }

                if (HireTuning.Verbose)
                {
                    Log.LogWarning("拦截: 友军 " + __instance.gameObject.name
                                   + " 被要求锁定玩家，已拒绝。");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.LogError("PreventAllyTargetingPlayer 异常: " + ex);
                return true;
            }
        }

        /// <summary>
        /// 指令菜单打开时屏蔽数字键 1/2/3。
        ///
        /// 游戏用 KeyCode.Alpha1..9 切换快捷栏（CharaControl.cs:599-647 通过
        /// KeyCustomizationManager.GetKeyCode("ALP1"..)）。所以玩家按 1/2/3 选指令时
        /// 会顺带切快捷栏 —— 这个补丁就是为消除该副作用。
        ///
        /// 只屏蔽 Alpha1/2/3，且仅在菜单打开期间生效，对游戏其它输入零影响。
        /// </summary>
        private static readonly string[] BlockedHotbarActions = { "ALP1", "ALP2", "ALP3" };

        private static int _hotbarBlockCount;

        internal static void SuppressHotbarKeysWhileMenuOpen(string keyName, ref KeyCode __result)
        {
            try
            {
                if (keyName == null)
                {
                    return;
                }

                // 只在"指令菜单打开"或"刚选完指令的短暂窗口内"屏蔽。
                // 加窗口是因为菜单会在选完的同一帧关闭，而游戏的快捷栏读取可能排在我们的
                // 选择处理之后 —— 若只看 IsOpen，那一帧就漏掉了（用户报告的"仍会切换"）。
                bool menuActive = HireOrderControl.IsOpen || HireOrderControl.RecentlySelected;

                if (!menuActive)
                {
                    return;
                }

                for (int i = 0; i < BlockedHotbarActions.Length; i++)
                {
                    if (keyName == BlockedHotbarActions[i])
                    {
                        __result = KeyCode.None;

                        _hotbarBlockCount++;
                        if (_hotbarBlockCount <= 3)
                        {
                            Log.LogInfo("[快捷栏屏蔽] 已拦截 " + keyName
                                        + "（第 " + _hotbarBlockCount + " 次）");
                        }
                        return;
                    }
                }
            }
            catch
            {
                // 忽略，放行
            }
        }

        /// <summary>
        /// 时序修复的前半段：在 CreateEnemey 执行前登记占位。
        /// 这样当该方法内部调用 GetCloseTgt(nowChara) 时，拦截器已经能认出这是友军。
        /// </summary>
        internal static void PreCreateEnemey(int pt, int iddd)
        {
            // 占位记录由 AllySpawner 在调用前创建；这里只需确认标记有效。
            // （保留此方法是为了让 Harmony 有一对 Prefix/Postfix 用于时序观测。）
        }

        internal static void PostCreateEnemey(GameObject __result)
        {
            try
            {
                AllySpawner.OnSpawned(__result);
            }
            catch (Exception ex)
            {
                Log.LogError("PostCreateEnemey 异常: " + ex);
            }
        }

        /// <summary>
        /// mapTP == 0 → 回营地；mapTP == 1 → 进入关卡。
        ///
        /// 用这个钩子做"是否在关卡内"的权威判定，比 GetCombatState() 可靠：
        /// 实测 GetCombatState() 在营地也可能为 true，导致友军被生成在营地里
        /// （用户报告："进图时雇佣的 NPC 会加载到营地，不跟着进图"）。
        /// </summary>
        internal static bool InMission;

        internal static void PostCreateWorld(int mapTP)
        {
            try
            {
                InMission = mapTP == 1;

                if (mapTP == 0)
                {
                    // 回到营地：结算本局结果（撤离成功才扣次数；阵亡不扣）
                    SettleMissionResult();

                    AllySpawner.RecallAll("回到营地");
                    _allowAutoRespawn = true;
                    _wasInMission = false;
                    return;
                }

                // ── 每次进入关卡地图都重新生成 ──────────────────────────────────
                _wasInMission = true;
                _allowAutoRespawn = true;
                _emptyFrames = 0;
                _playerDiedThisMission = false;     // 新的一局，重新统计

                // 换图时旧对象通常已被游戏清掉，这里先清注册表避免残留指向已销毁对象
                AllyRegistry.Prune();
                if (AllyRegistry.Count == 0)
                {
                    Log.LogInfo("进入关卡地图（mapTP=1），开始生成护航队员。");
                    AllySpawner.SpawnHiredForMission();
                }
                else
                {
                    Log.LogInfo("进入关卡地图（mapTP=1），已有 " + AllyRegistry.Count + " 名在场，跳过生成。");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PostCreateWorld 异常: " + ex);
            }
        }

        /// <summary>手动 F10 回收后置 false，避免"一清空就立刻刷新"。</summary>
        private static bool _allowAutoRespawn = true;

        /// <summary>本 MOD 是否有"面板"打开（会阻止玩家操作的那一类）。</summary>
        internal static bool IsAnyPanelOpen
        {
            get { return HirePanel.IsOpen; }
        }

        /// <summary>
        /// 让游戏把「雇佣面板」当成一个模态窗口。
        ///
        /// 必须用 return false 跳过原方法，而不是"只改结果"——只写 __result 的话原方法
        /// 仍会执行并在结束时覆盖我们的赋值（这个错误此前造成两个症状：
        /// 快捷栏照旧被 1/2/3 切换、鼠标指针不显示）。
        ///
        /// 注意：**只包含雇佣面板，不包含 X 指令菜单**。
        /// 游戏 CheckWindowOpen() 的清单是"会挡住玩家操作的窗口"（背包/选关/设置/图鉴/
        /// 任务/对话…，GameManager.cs:1023-1071），而宠物指令菜单 OrderControl 不在其中，
        /// 因此它不阻止玩家移动。我们照此处理：X 菜单打开时玩家仍可移动。
        /// </summary>
        internal static bool ForceWindowOpen(ref bool __result)
        {
            try
            {
                if (IsAnyPanelOpen)
                {
                    __result = true;
                    return false;      // 跳过原方法，避免结果被覆盖
                }
            }
            catch (Exception ex)
            {
                Log.LogError("ForceWindowOpen 异常: " + ex);
            }

            return true;
        }

        /// <summary>
        /// 阻止宠物伤害我们的友军。
        ///
        /// 宠物攻击走 PetControl.cs:248-251 —— 条件只判断 `collision.tag == "Enemy"`，
        /// **完全没有阵营校验**。而我们的人形友军正是 Enemy 标签，所以会被宠物攻击。
        /// 这里在伤害落点（CharaState.BeHit）上拦截。
        /// </summary>
        private static bool _petBlockLogged;
        private static bool _playerHurtLogged;
        private static bool _partyLogged;
        private static int _beHitDiagCount;
        private static bool _petTriggerLogged;

        /// <summary>
        /// 宠物攻击判定的源头拦截。
        ///
        /// PetControl.OnTriggerStay2D（PetControl.cs:240-266）判断条件只有
        ///     (actionMode == 1 || actionMode == 2) &amp;&amp; collision.tag == "Enemy" &amp;&amp; attckCD &lt;= 0
        /// **完全没有阵营校验**。我们的人形友军正是 Enemy 标签，所以会被宠物攻击。
        ///
        /// 在源头返回 false 直接跳过整个方法，比在伤害落点拦更彻底
        /// （连吸血/眩晕/攻击动画一起挡掉）。
        /// </summary>
        internal static bool PreventPetAttackInTrigger(object __0)
        {
            try
            {
                if (__0 == null)
                {
                    return true;
                }

                Collider2D collision = __0 as Collider2D;
                if (collision == null)
                {
                    return true;
                }

                if (collision.GetComponent<AllyMarker>() == null)
                {
                    return true;      // 撞到的不是我们的友军，放行
                }

                if (!_petTriggerLogged)
                {
                    _petTriggerLogged = true;
                    Log.LogInfo("已在宠物攻击判定中跳过友军：" + collision.gameObject.name);
                }

                return false;         // 跳过 PetControl.OnTriggerStay2D
            }
            catch (Exception ex)
            {
                Log.LogError("PreventPetAttackInTrigger 异常: " + ex);
                return true;
            }
        }

        /// <summary>
        /// 武器类型限制：明星护 / 魔王护 只配发步枪。
        ///
        /// 做法：CharaControl.InitData 用
        ///     ToolManager.GetSingleRandomCount(DataHub.GetAIWeaponByHardcode(modelID))
        /// 从列表里随机取一把（CharaControl.cs:1848）。所以只要把"步枪"移到列表首位，
        /// 再让取随机数变成取第一把即可 —— 这里直接把返回值替换成只含步枪的列表。
        ///
        /// 步枪的判定：itemDataDic[weaponId].itemPos == 4
        /// （与 CharaControl.cs:2349-2363 的"交战距离"分类一致，itemPos 4 即步枪）。
        /// </summary>
        internal static void RestrictToRifle(int aiID, ref List<int> __result)
        {
            try
            {
                if (__result == null || __result.Count == 0)
                {
                    return;
                }

                HireTier tier = HireData.FindByModel(aiID);
                if (tier == null || !tier.RifleOnly)
                {
                    return;
                }

                DataHub dh = GameRefs.Data;
                if (dh == null || dh.itemDataDic == null)
                {
                    return;
                }

                List<int> rifles = new List<int>();
                for (int i = 0; i < __result.Count; i++)
                {
                    int id = __result[i];
                    ItemData item;
                    if (dh.itemDataDic.TryGetValue(id, out item) && item != null && item.itemPos == 4)
                    {
                        rifles.Add(id);
                    }
                }

                if (rifles.Count > 0)
                {
                    __result = rifles;
                }
                else if (HireTuning.Verbose)
                {
                    Log.LogWarning("档位「" + tier.Name + "」在模型 " + aiID
                                   + " 的武器池里找不到步枪（itemPos=4），保持原池。");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("RestrictToRifle 异常: " + ex);
            }
        }

        private static bool _petSearchLogged;

        /// <summary>
        /// 宠物索敌触发器拦截。
        ///
        /// PetSearch.OnTriggerEnter2D（PetSearch.cs:33-52）的判定只有
        ///     if (type == 1) { if (collision.tag == "Enemy") { ... target = collision.gameObject; } }
        /// **没有阵营校验**，而友军正是 Enemy 标签 ⇒ 宠物会把友军当成敌人并一直贴上去。
        /// 这里在源头跳过友军。
        /// </summary>
        internal static bool PreventPetSearchAlly(object __0)
        {
            try
            {
                Collider2D collision = __0 as Collider2D;
                if (collision == null)
                {
                    return true;
                }

                if (collision.GetComponent<AllyMarker>() == null)
                {
                    return true;
                }

                if (!_petSearchLogged)
                {
                    _petSearchLogged = true;
                    Log.LogInfo("已在宠物索敌中跳过友军：" + collision.gameObject.name);
                }

                return false;      // 跳过 PetSearch.OnTriggerEnter2D
            }
            catch (Exception ex)
            {
                Log.LogError("PreventPetSearchAlly 异常: " + ex);
                return true;
            }
        }

        /// <summary>宠物采纳目标时跳过友军（PetControl.cs:57 GetTarget）。</summary>
        internal static bool PreventPetGetAllyTarget(object __0)
        {
            try
            {
                GameObject obj = __0 as GameObject;
                if (obj == null)
                {
                    return true;
                }

                if (obj.GetComponent<AllyMarker>() != null)
                {
                    return false;      // 不采纳友军作为目标
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PreventPetGetAllyTarget 异常: " + ex);
            }

            return true;
        }

        /// <summary>宠物追击时若目标竟是友军则放弃（PetControl.cs:373 GoToTarget）。</summary>
        internal static bool PreventPetChaseAlly(PetControl __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }

                // target 是私有字段，用反射读取
                if (_fPetTarget == null)
                {
                    _fPetTarget = AccessTools.Field(typeof(PetControl), "target");
                }

                if (_fPetTarget == null)
                {
                    return true;
                }

                GameObject tgt = _fPetTarget.GetValue(__instance) as GameObject;
                if (tgt != null && tgt.GetComponent<AllyMarker>() != null)
                {
                    _fPetTarget.SetValue(__instance, null);   // 清掉错误目标
                    return false;                              // 跳过本帧追击
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PreventPetChaseAlly 异常: " + ex);
            }

            return true;
        }

        private static FieldInfo _fPetTarget;

        internal static bool PreventPetDamagingAlly(CharaState __instance, GameObject srs)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }

                int party = __instance.CheckParty();
                bool isAlly = AllyRegistry.IsAlly(__instance.gameObject);

                // ── 诊断：记录前几次 BeHit，确认"玩家受伤"到底走不走这里 ──────
                if (_beHitDiagCount < 8)
                {
                    _beHitDiagCount++;
                    Log.LogInfo("[受伤诊断] 目标=" + __instance.gameObject.name
                                + " party=" + party
                                + " 是护航=" + isAlly
                                + " 来源=" + (srs != null ? srs.name : "null"));
                }

                // ── 受伤台词（对所有单位都要判断，不能提前 return）────────────
                if (isAlly)
                {
                    AllyRecord ar = AllyRegistry.Find(__instance.gameObject);
                    if (ar != null)
                    {
                        ar.TalkSelfHurt = true;
                    }
                }
                else if (party == 0)
                {
                    // party 0 且不是我们的护航 → 玩家本人受伤
                    for (int i = 0; i < AllyRegistry.Count; i++)
                    {
                        AllyRecord ar2 = AllyRegistry.At(i);
                        if (ar2 != null)
                        {
                            ar2.TalkPlayerHurt = true;
                        }
                    }

                    if (!_playerHurtLogged)
                    {
                        _playerHurtLogged = true;
                        Log.LogInfo("已识别到玩家受伤（party=" + party
                                    + "），已让 " + AllyRegistry.Count + " 名护航准备喊话。");
                    }
                }

                // ── 拦截逻辑：只针对我们的护航队员 ─────────────────────────
                if (!isAlly || srs == null)
                {
                    return true;      // 不是友军 → 不干预游戏本体逻辑
                }

                // 攻击者是宠物 → 拦截
                if (srs.GetComponent<PetControl>() != null)
                {
                    if (!_petBlockLogged)
                    {
                        _petBlockLogged = true;
                        Log.LogInfo("已阻止宠物攻击友军：" + __instance.gameObject.name
                                    + "（来源 " + srs.name + "）");
                    }
                    return false;
                }

                // 任何 party 0 单位都不得伤害 party 0 的友军。
                // 引擎本应已按阵营拦住，这里是针对"绕过阵营判定的伤害路径"（如宠物）
                // 的第二道保险。
                CharaState src = srs.GetComponent<CharaState>();
                if (src != null && src.CheckParty() == 0)
                {
                    if (!_petBlockLogged)
                    {
                        _petBlockLogged = true;
                        Log.LogInfo("已阻止同阵营伤害友军：" + srs.name + " → "
                                    + __instance.gameObject.name);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("PreventPetDamagingAlly 异常: " + ex);
            }

            return true;
        }
    }

    /// <summary>
    /// 阻止游戏自身的移动逻辑挪动被本 MOD 接管的友军。
    ///
    /// CharaControl 自带的移动方法会在它自己的 Update 里直接改 transform.position：
    ///   MoveToTarget(CharaControl.cs:2266) / MoveNoTarget(:2481) / MoveToSuspectPos(:2432)
    /// 即使我们在 LateUpdate 里把位置摆好了，下一帧又会被它挪走 ——
    /// 这就是"用停留指令友军仍会移动"和"近距离被持续拖拽"的根因。
    ///
    /// 因此当 AllyBrain 判定某友军的位置由本 MOD 接管时，这里直接跳过这些方法，
    /// 并把它按回锁定位（双保险：既跳过、也对齐）。
    /// </summary>
    internal static class CharaControlPatch
    {
        private static readonly string[] MoveMethods =
        {
            "MoveToTarget", "MoveNoTarget", "MoveToSuspectPos"
        };

        internal static void Install(Harmony harmony, Action<string, string, string, string> patch,
                                     Action<string, string, string> verify)
        {
            for (int i = 0; i < MoveMethods.Length; i++)
            {
                // 签名是 (typeName, methodName, postfixName, prefixName)
                patch("CharaControl", MoveMethods[i], null, nameof(BlockMovement));
                verify("CharaControl", MoveMethods[i], "接管友军位置");
            }
        }

        internal static bool BlockMovement(CharaControl __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return true;
                }

                Vector3 locked;
                if (!AllyBrain.TryIsPositionLocked(__instance.gameObject, out locked))
                {
                    return true;      // 未被接管 → 放行游戏自身逻辑
                }

                // 跳过原方法，并把位置对齐到锁定位（防止其它路径挪动）
                if (locked != Vector3.zero)
                {
                    __instance.transform.position = locked;
                }

                return false;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("BlockMovement 异常: " + ex);
                return true;
            }
        }
    }

    /// <summary>
    /// 面板驱动：把 UI 的 Update 挂在常驻对象上（面板本身是静态工具类）。
    /// </summary>
    internal sealed class HirePanelDriver : MonoBehaviour
    {
        private void Update()
        {
            HirePanel.Tick();
            HireOrderControl.Tick();
        }

        /// <summary>指令菜单与友军标识都走 IMGUI 渲染（不依赖 Canvas，必定可见）。</summary>
        private void OnGUI()
        {
            HireOrderControl.OnGUI();
            AllyHud.Draw();
            // 说明：地图上的友军标记（AllyMapMarkers）已按用户要求移除。
            // 原因是地图内容层是独立 Canvas，坐标无法可靠换算到屏幕，
            // 多次尝试（uGUI 子物体 / 相机换算 / 内容层换算 / 以玩家为中心）
            // 均无法与实际地图对齐。
        }
    }
}
