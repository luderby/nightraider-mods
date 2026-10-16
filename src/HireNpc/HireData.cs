using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>友军的三种指令模式。</summary>
    public enum AllyMode
    {
        /// <summary>跟随：在玩家活动半径内随行并自动交战。</summary>
        Follow = 0,

        /// <summary>驻守：停在原地不移动；有敌人进入射程则就地开火。</summary>
        Hold = 1,
    }

    /// <summary>
    /// 可雇佣兵种。
    ///
    /// 数据来自游戏自身：
    ///   * 归属判定：DataHub.humanEnemyList（人类单位）
    ///   * 数值：DataHub.cs:1735-1770 的数组（maxHP/atk/def/crit/atkRate/movSpd）
    ///   * 武器：CharaControl.InitData 会按 modelID 自动配发（DataHub.GetAIWeaponByHardcode）
    ///
    /// 排除的模型（重要）：
    ///   * 508        —— 会被 CharaControl.cs:1199 改成 party 2（我们有 SetParty 拦截，但暂不纳入）
    ///   * 510        —— 带任务剧本硬编码行为（CharaControl.cs:1155-1200）
    ///   * 629        —— 不在 monsterDataDic 注册范围风险，故不使用
    /// </summary>
    /// <summary>
    /// 一笔护航订单（夜袭者护航俱乐部）。
    ///
    /// 合约规则（按用户设定）：
    ///   · 每次点单包含 **3 次**地图成功的护航（即 3 次成功撤离）
    ///   · 3 次用完后该护航自动结束，不再随行
    ///   · 提前结单按"已使用的撤离次数"退款：
    ///         已用 0 次 → 退还 50% 点单 GP
    ///         已用 1 次 → 退还 20%
    ///         已用 2 次 → 不退款（合约接近完成）
    /// </summary>
    public sealed class HireOrder
    {
        public string TierKey;

        /// <summary>本合约总共包含的护航次数。</summary>
        /// <summary>本合约总共包含的护航次数。</summary>
        public int Total = 3;

        /// <summary>已经成功撤离的次数。</summary>
        public int Used;

        // ── 实际支付记录（退款以此为准）────────────────────────────────────
        public int PaidGp;
        public int PaidBp;

        public bool Active { get { return Used < Total; } }

        /// <summary>剩余护航次数。</summary>
        public int Remaining { get { int r = Total - Used; return r < 0 ? 0 : r; } }

        public HireOrder Clone()
        {
            return new HireOrder
            {
                TierKey = TierKey, Total = Total, Used = Used,
                PaidGp = PaidGp, PaidBp = PaidBp
            };
        }
    }

    public sealed class HireTier
    {
        public string Key;
        public string Name;

        /// <summary>
        /// 该等级可用的模型集合。
        /// 一个等级可以有多个模型：点单一次占用一个名额（从其中选一个尚未在场的），
        /// 点单两次则两个都出场。
        /// </summary>
        public int[] ModelIds;

        /// <summary>UI/日志展示用的主模型（取 ModelIds[0]）。</summary>
        public int ModelId { get { return ModelIds != null && ModelIds.Length > 0 ? ModelIds[0] : 0; } }

        /// <summary>雇佣花费（GP）。</summary>
        public int Cost;

        /// <summary>
        /// 额外需要的 BP（点券）。0 = 不需要 BP。
        /// 仅"明星护"使用（用户要求用 GP + BP 同步消耗点单）。
        /// </summary>
        public int BpCost;

        /// <summary>展示用 HP（内部值 ×0.1）。</summary>
        public int DisplayHp;

        public int DisplayAtk;

        /// <summary>展示用护盾（内部值 ×0.1）。</summary>
        public int DisplayShield;

        /// <summary>玩家可见的一句话定位。</summary>
        public string Role;

        /// <summary>数值倍率：1.0 = 游戏原始值。</summary>
        public float HpScale = 1f;
        public float AtkScale = 1f;

        /// <summary>
        /// 护盾值（内部值，×10）。会写入 def 与 damageShield。
        /// 游戏里 HUD 显示的"护甲"就是 CharaState.damageShield，上限 = CharaData.def。
        /// </summary>
        public int ShieldValue;

        /// <summary>是否限定为步枪类武器。</summary>
        public bool RifleOnly;

        /// <summary>
        /// 指定配发的武器 ID（0 = 不改）。
        ///
        /// 说明：这些人类单位的 atk 字段是 0（伤害完全来自武器），
        /// 所以"给更好的枪"是提升护航战力最有效的办法。
        /// 实现方式见 AllySpawner.ApplyTierWeapon：在 CreateEnemey 返回后
        /// 直接用 CurrentDataHub.CreateWeaponFullParts 换掉 CharaControl.enemyWeapon。
        /// </summary>
        public int WeaponId;
    }

    /// <summary>档位表 + 全部可调参数。价格可通过 cfg 覆盖，无需重新编译。</summary>
    internal static class HireData
    {
        /// <summary>
        /// 护航档位表（夜袭者护航俱乐部）。
        ///
        /// 模型分配由用户指定：
        ///     明星护 = 507 / 508（两个都可用，点单一次上一个）
        ///     魔王护 = 627
        ///     金牌护 = 625
        ///     高级护 = 624
        ///     普通护 = 623
        ///
        /// 护盾值（用户指定，界面显示值）：明星 220 / 魔王 180 / 金牌 120 / 高级 80 / 普通 40
        /// 内部按 ×10 存储（游戏口径，见 BattleMessage.cs:185 显示时 ×0.1）。
        /// </summary>
        internal static readonly HireTier[] Tiers =
        {
            // 普通护 —— 623（20 万 GP）
            new HireTier { Key = "Common", Name = "普通护", ModelIds = new[] { 623 }, Cost = 200000,
                           Role = "休闲娱乐", HpScale = 1.0f, AtkScale = 1.0f, ShieldValue = 400 },

            // 高级护 —— 624（40 万 GP）
            new HireTier { Key = "Senior", Name = "高级护", ModelIds = new[] { 624 }, Cost = 400000,
                           Role = "美美撤离", HpScale = 2.0f, AtkScale = 2.0f, ShieldValue = 800 },

            // 金牌护 —— 644（60 万 GP）
            new HireTier { Key = "Gold", Name = "金牌护", ModelIds = new[] { 644 }, Cost = 600000,
                           Role = "板板坐好", HpScale = 3.0f, AtkScale = 2.5f, ShieldValue = 1200 },

            // 魔王护 —— 627 / 646（80 万 GP）
            new HireTier { Key = "Demon", Name = "魔王护", ModelIds = new[] { 627, 646 }, Cost = 800000,
                           Role = "无需多言！", HpScale = 4.0f, AtkScale = 3.0f, ShieldValue = 1800 },

            // 明星护 —— 507 / 508（正式价：100 万 GP + 68 BP）
            new HireTier { Key = "Star", Name = "明星护", ModelIds = new[] { 507, 508 }, Cost = 1000000,
                           BpCost = 68,
                           Role = "鸭梨！", HpScale = 5.0f, AtkScale = 4.0f, ShieldValue = 2200 },
        };

        /// <summary>
        /// 价格**以代码为准**，不从 cfg 读取。
        ///
        /// 【为什么改掉】原先每个档位都绑定了 [HireCost] 配置项并执行
        ///     t.Cost = e.Value;
        /// 而 BepInEx 的配置一旦写入就永久保留。早期版本写入过 100，
        /// 于是后来把代码改成 20 万也**被配置里的 100 覆盖回去** ——
        /// 表现为"改了价格但游戏里还是 100"。
        ///
        /// 现在价格只由本文件的 Tiers 定义决定；[HireCost] 段即便存在也不再生效。
        /// </summary>
        internal static void ApplyConfig(ConfigFile cfg)
        {
            // 故意留空：价格不走配置，避免被历史值覆盖。
        }

        internal static HireTier Find(string key)
        {
            for (int i = 0; i < Tiers.Length; i++)
            {
                if (Tiers[i].Key == key)
                {
                    return Tiers[i];
                }
            }
            return null;
        }

        /// <summary>
        /// 退款比例（用户最终确认的规则）。
        ///
        ///     退款 = 实付总额 × (0.6 − 已用次数 × 0.2)，下限 0
        ///
        /// 即：从未使用扣 40%（服务费），之后每多用一次多扣 20%，3 次扣完。
        /// 以"原价 20 万、未续单"为例：
        ///     已用 0 → 退 12 万    已用 1 → 退 8 万
        ///     已用 2 → 退 4 万     已用 3 → 退 0
        ///
        /// 之所以用"实付总额"而不是"原价"作为基数：续单会增加实付，
        /// 若基数恒为原价，则续单越多可退比例越低（续 10 单后只能退 20%）。
        /// 用实付为基数时，续单的钱也能按同一比例退回，且不会出现套利。
        /// </summary>
        public static float RefundRatio(int used)
        {
            float r = 0.6f - used * 0.2f;
            return r < 0f ? 0f : r;
        }

        /// <summary>提前结单可退回的 GP（以实付为基准）。</summary>
        public static int RefundAmount(HireOrder order)
        {
            if (order == null)
            {
                return 0;
            }

            return Mathf.RoundToInt(order.PaidGp * RefundRatio(order.Used));
        }

        /// <summary>提前结单可退回的 BP（以实付为基准）。</summary>
        public static int RefundBpAmount(HireOrder order)
        {
            if (order == null || order.PaidBp <= 0)
            {
                return 0;
            }

            return Mathf.RoundToInt(order.PaidBp * RefundRatio(order.Used));
        }

        /// <summary>本合约总共包含的护航次数（每次点单 3 次成功撤离）。</summary>
        public const int ContractDepartures = 3;

        /// <summary>续单时追加的护航次数（每续 1 单 = +3 次）。</summary>
        public const int RenewDepartures = 3;

        /// <summary>
        /// 每续 1 单的价格 = 原价 × 20%（用户设定）。
        /// 以普通护 20 万为例：续 1 单 = 4 万，+3 次护航。
        /// </summary>
        public const float RenewUnitRatio = 0.20f;

        /// <summary>续 1 单需要的 GP。</summary>
        public static int RenewUnitGp(HireTier t)
        {
            return t == null ? 0 : Mathf.RoundToInt(t.Cost * RenewUnitRatio);
        }

        /// <summary>续 1 单需要的 BP（双货币档位）。</summary>
        public static int RenewUnitBp(HireTier t)
        {
            return t == null || t.BpCost <= 0
                ? 0
                : Mathf.RoundToInt(t.BpCost * RenewUnitRatio);
        }

        /// <summary>续单价格显示文本。</summary>
        public static string RenewUnitText(HireTier t)
        {
            if (t == null)
            {
                return "?";
            }

            int gp = RenewUnitGp(t);
            int bp = RenewUnitBp(t);
            return gp + " GP" + (bp > 0 ? " + " + bp + " BP" : "") + " / 单";
        }

        /// <summary>
        /// 从游戏数据刷新展示用的 HP/ATK。
        ///
        /// 与 VerifyAndRefreshStats 的区别：这个可以被随时调用（面板每次刷新都会调），
        /// 不依赖"启动时一次性自检"的时机 —— 否则存档尚未载入时会一直显示 0。
        /// </summary>
        internal static void RefreshStatsFromGame()
        {
            try
            {
                DataHub dh = GameRefs.Data;
                if (dh == null || dh.monsterDataDic == null)
                {
                    return;
                }

                for (int i = 0; i < Tiers.Length; i++)
                {
                    HireTier t = Tiers[i];
                    CharaData cd;
                    if (dh.monsterDataDic.TryGetValue(t.ModelId, out cd) && cd != null)
                    {
                        // 展示"该护航等级实际生效的数值"= 游戏原始值 × 档位倍率
                        t.DisplayHp = Mathf.RoundToInt(cd.maxHP * t.HpScale * 0.1f);
                        t.DisplayAtk = Mathf.RoundToInt(cd.atk * t.AtkScale * 0.1f);
                    }

                    t.DisplayShield = Mathf.RoundToInt(t.ShieldValue * 0.1f);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("RefreshStatsFromGame 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 启动自检：确认每个档位的 modelID 都真正可用，并用游戏自身的数值刷新展示字段。
        /// 这一步很重要 —— CreateNewMonster 取值处（CurrentDataHub.cs:6716）没有
        /// TryGetValue 保护，模型缺失会直接抛 KeyNotFoundException。
        /// </summary>
        internal static void VerifyAndRefreshStats()
        {
            try
            {
                DataHub dh = GameRefs.Data;
                ResourcesHub rh = GameRefs.Resources;
                if (dh == null || rh == null)
                {
                    HireNpcPlugin.Log.LogWarning("档位自检跳过：DataHub/ResourcesHub 未就绪。");
                    return;
                }

                for (int i = 0; i < Tiers.Length; i++)
                {
                    HireTier t = Tiers[i];
                    bool inMonster = dh.monsterDataDic != null && dh.monsterDataDic.ContainsKey(t.ModelId);
                    bool inPrefab = rh.prefabsDic_Enemy != null && rh.prefabsDic_Enemy.ContainsKey(t.ModelId);

                    if (inMonster)
                    {
                        CharaData cd = dh.monsterDataDic[t.ModelId];
                        t.DisplayHp = Mathf.RoundToInt(cd.maxHP * t.HpScale * 0.1f);
                        t.DisplayAtk = Mathf.RoundToInt(cd.atk * t.AtkScale * 0.1f);
                    }

                    t.DisplayShield = Mathf.RoundToInt(t.ShieldValue * 0.1f);

                    if (!inMonster || !inPrefab)
                    {
                        HireNpcPlugin.Log.LogWarning("档位「" + t.Name + "」模型 " + t.ModelId
                            + " 不可用（monsterDataDic=" + inMonster
                            + ", prefabsDic_Enemy=" + inPrefab + "）—— 该档位会在生成时被跳过。");
                    }
                    else if (HireTuning.Verbose)
                    {
                        HireNpcPlugin.Log.LogInfo("档位「" + t.Name + "」model=" + t.ModelId
                            + " HP=" + t.DisplayHp + " ATK=" + t.DisplayAtk + " 花费=" + t.Cost + " GP");
                    }
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("档位自检异常: " + ex.Message);
            }
        }

        /// <summary>把 modelID 反查成档位（用于日志与 UI）。</summary>
        internal static HireTier FindByModel(int modelId)
        {
            for (int i = 0; i < Tiers.Length; i++)
            {
                if (Tiers[i].ModelId == modelId)
                {
                    return Tiers[i];
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 运行期参数。集中放这里，避免散落的魔法数字。
    /// </summary>
    internal static class HireTuning
    {
        /// <summary>雇佣上限（用户要求 2）。</summary>
        internal static int MaxHired = 2;

        /// <summary>跟随：超过这个距离就朝玩家移动。</summary>
        internal static float FollowFar = 5.0f;

        /// <summary>跟随：近于这个距离就停（避免挤在玩家身上）。</summary>
        internal static float FollowNear = 3.0f;

        /// <summary>主动突击的最大追击距离。</summary>
        // 注：AssaultRange（"警惕"模式的索敌半径）已随该指令一并移除。

        /// <summary>索敌半径。</summary>
        internal static float AcquireRadius = 22.0f;

        /// <summary>目标切换节流（帧）。</summary>
        internal static int RetargetFrames = 30;

        /// <summary>AI 移动速度系数（与游戏 CharaControl 内部口径一致）。</summary>
        internal static float SpeedScale = 0.01f;

        /// <summary>自己驱动移动时使用的基础速度。</summary>
        internal static float BaseSpeed = 90.0f;

        /// <summary>是否输出详细日志。</summary>
        internal static bool Verbose = false;

        /// <summary>是否让护航队员随机冒出台词（可用配置关闭）。</summary>
        internal static bool AllyTalk = true;

        /// <summary>X-4 闭麦：临时关闭所有护航台词（不改配置文件）。</summary>
        internal static bool AllyMuted = false;

        /// <summary>是否启用"脱离战斗后自动恢复"（清异常状态 + 补满血量护甲）。</summary>
        internal static bool AutoRecover = true;

        /// <summary>判定"周围安全"的半径（米）。此半径内无敌人即视为脱离战斗。</summary>
        internal static float RecoverSafeRadius = 20f;

        /// <summary>脱离战斗多少秒后触发恢复。</summary>
        internal static float RecoverSeconds = 15f;
    }
}
