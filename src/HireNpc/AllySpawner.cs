using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 友军生成 / 回收。
    ///
    /// 生成流程（顺序不能改）：
    ///   1. 前置校验 modelID 同时存在于 monsterDataDic 与 prefabsDic_Enemy
    ///      （CreateNewMonster 取值处 CurrentDataHub.cs:6716 没有 TryGetValue 保护，
    ///       缺键会直接抛 KeyNotFoundException）
    ///   2. **先登记占位**（AllyRegistry.PreRegister）
    ///   3. 调用 MonsterManager.CreateEnemey(0, modelId)
    ///      —— 该方法内部会在返回前 GetCloseTgt(nowChara)，此时拦截器已能认出友军
    ///   4. PostCreateEnemey → OnSpawned 回填对象并打 AllyMarker
    ///   5. 兜底：清掉可能残留的"指向玩家"的目标
    /// </summary>
    internal static class AllySpawner
    {
        private static MethodInfo _miInstantiate;
        private static MethodInfo _miRecycle;
        private static FieldInfo _fCloseTgt;
        private static FieldInfo _fDamageShield;

        /// <summary>
        /// 该等级下一个应该出场的模型。
        ///
        /// 多模型等级（如明星护 = 507/508）：点单一次上场一个，
        /// 再点一次就把另一个也带出来；两个都已在场时返回第一个（允许重复雇佣同名）。
        /// </summary>
        internal static int PickModelForTier(HireTier tier)
        {
            if (tier == null || tier.ModelIds == null || tier.ModelIds.Length == 0)
            {
                return 0;
            }

            if (tier.ModelIds.Length == 1)
            {
                return tier.ModelIds[0];
            }

            // 找出"该等级里尚未在场"的模型
            for (int i = 0; i < tier.ModelIds.Length; i++)
            {
                int id = tier.ModelIds[i];
                if (!IsModelAlreadyOut(tier.Key, id))
                {
                    return id;
                }
            }

            return tier.ModelIds[0];
        }

        private static bool IsModelAlreadyOut(string tierKey, int modelId)
        {
            for (int i = 0; i < AllyRegistry.Count; i++)
            {
                AllyRecord r = AllyRegistry.At(i);
                if (r != null && r.ModelId == modelId
                    && r.TierKey == tierKey && r.IsAlive)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>等待回填的占位记录（PostCreateEnemey 会消费它）。</summary>
        private static AllyRecord _pending;
        private static int _pendingModelId;

        // ------------------------------------------------------------------
        // 对象池（这两个方法是 internal，必须反射调用）
        // ------------------------------------------------------------------

        internal static GameObject PoolInstantiate(GameObject prefab)
        {
            try
            {
                ObjectPoolManager pm = GameRefs.Pool;
                if (pm == null)
                {
                    return null;
                }

                if (_miInstantiate == null)
                {
                    _miInstantiate = AccessTools.Method(typeof(ObjectPoolManager), "InstantiateGameObject");
                }

                if (_miInstantiate != null)
                {
                    return _miInstantiate.Invoke(pm, new object[] { prefab }) as GameObject;
                }

                return UnityEngine.Object.Instantiate(prefab);
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("PoolInstantiate 异常: " + ex);
                return null;
            }
        }

        internal static void PoolRecycle(GameObject go)
        {
            try
            {
                if (go == null)
                {
                    return;
                }

                ObjectPoolManager pm = GameRefs.Pool;
                if (pm == null)
                {
                    return;
                }

                if (_miRecycle == null)
                {
                    _miRecycle = AccessTools.Method(typeof(ObjectPoolManager), "RecycleGameObject");
                }

                if (_miRecycle != null)
                {
                    _miRecycle.Invoke(pm, new object[] { go });
                }
                else
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("PoolRecycle 异常: " + ex);
            }
        }

        // ------------------------------------------------------------------
        // 生成
        // ------------------------------------------------------------------

        /// <summary>进图时按雇佣名单生成友军。</summary>
        internal static void SpawnHiredForMission()
        {
            try
            {
                // 用 CreateWorld 的 mapTP 作为权威判定（营地 mapTP=0，关卡 mapTP=1）。
                // GetCombatState() 在营地也可能为 true，不能作为依据。
                if (!HireNpcPlugin.InMission)
                {
                    HireNpcPlugin.Log.LogInfo("当前不在关卡内（InMission=false），跳过生成。");
                    return;
                }

                List<HireOrder> orders = HireNpcPlugin.Hired;
                if (orders == null || orders.Count == 0)
                {
                    HireNpcPlugin.Log.LogInfo("本局没有护航订单。");
                    return;
                }

                int limit = HireTuning.MaxHired > 0
                    ? Mathf.Min(orders.Count, HireTuning.MaxHired)
                    : orders.Count;      // 0 = 不限制（bug 修复：原来会算成 0 名）

                HireNpcPlugin.Log.LogInfo("进图：按名单生成 " + limit + " 名护航队员"
                    + (HireTuning.MaxHired > 0 ? "（上限 " + HireTuning.MaxHired + "）" : "（无上限）"));

                for (int i = 0; i < limit; i++)
                {
                    HireOrder order = orders[i];
                    HireTier tier = HireData.Find(order.TierKey);
                    if (tier == null)
                    {
                        HireNpcPlugin.Log.LogWarning("未知档位 Key：" + order.TierKey + "，跳过。");
                        continue;
                    }

                    Spawn(tier, i);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("SpawnHiredForMission 异常: " + ex);
            }
        }

        /// <summary>生成一个友军。index 用于在玩家周围错开位置。</summary>
        internal static GameObject Spawn(HireTier tier, int index)
        {
            try
            {
                if (tier == null)
                {
                    return null;
                }

                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null)
                {
                    return null;
                }

                DataHub dh = GameRefs.Data;
                ResourcesHub rh = GameRefs.Resources;
                MonsterManager mm = GameRefs.Monster;
                if (dh == null || rh == null || mm == null)
                {
                    HireNpcPlugin.Log.LogWarning("生成跳过：管理器不可用。");
                    return null;
                }

                // ── 1. 选模型 + 双重校验，避免 KeyNotFoundException ──────────────
                // 一个等级可能有多个模型（如明星护 = 507/508），这里挑一个尚未在场的。
                int modelId = PickModelForTier(tier);

                if (dh.monsterDataDic == null || !dh.monsterDataDic.ContainsKey(modelId))
                {
                    HireNpcPlugin.Log.LogWarning("模型 " + modelId + " 不在 monsterDataDic，跳过。");
                    return null;
                }

                if (rh.prefabsDic_Enemy == null || !rh.prefabsDic_Enemy.ContainsKey(modelId))
                {
                    HireNpcPlugin.Log.LogWarning("模型 " + modelId + " 没有敌人预制体，跳过。");
                    return null;
                }

                // ── 2. 先登记占位（时序关键） ───────────────────────────────────
                _pendingModelId = modelId;
                _pending = AllyRegistry.PreRegister(modelId, tier.Key, Time.frameCount);

                // ── 3. 生成（party 0 = 玩家阵营） ───────────────────────────────
                GameObject go = mm.CreateEnemey(0, modelId);

                // 若 Postfix 因故没跑到，这里兜底
                if (_pending != null)
                {
                    AllyRegistry.Complete(_pending, go);
                    _pending = null;
                }

                if (go == null)
                {
                    HireNpcPlugin.Log.LogWarning("CreateEnemey 返回 null（model " + modelId + "）。");
                    return null;
                }

                // ── 4. 位置：紧贴玩家（落在 3-5m 跟随带内） ─────────────────────
                float angle = Mathf.Deg2Rad * (45f + 90f * index);
                float radius = 1.0f + 0.75f * index;
                go.transform.position = cur.nowChara.transform.position
                                        + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);

                CharaControl ctrl = go.GetComponent<CharaControl>();
                if (ctrl != null)
                {
                    ctrl.isIdle = false;

                    // ── 5. 兜底：清掉任何指向玩家的残留目标 ─────────────────────
                    if (ClearPlayerTarget(ctrl))
                    {
                        HireNpcPlugin.Log.LogInfo("生成后兜底清除了指向玩家的目标。");
                    }
                }

                // ── 6. 按护航等级调整数值 ──────────────────────────────────────
                ApplyTierScaling(go, tier);

                // ── 7. 按护航等级配发武器 ──────────────────────────────────────
                ApplyTierWeapon(go, tier);

                CharaState state = go.GetComponent<CharaState>();
                HireNpcPlugin.Log.LogInfo("已生成友军: " + tier.Name + " (model " + tier.ModelId
                                          + ", party=" + (state != null ? state.CheckParty().ToString() : "?")
                                          + ") pos=" + go.transform.position);

                // 在游戏内给出可见反馈：直接在友军头顶弹字，并顺带诊断可见性。
                NotifySpawned(go, tier);
                DiagnoseVisibility(go, tier);

                return go;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("Spawn 异常: " + ex);
                return null;
            }
        }

        /// <summary>在友军头顶弹字，让玩家立刻看到"友军到了"。</summary>
        private static void NotifySpawned(GameObject ally, HireTier tier)
        {
            try
            {
                FXManager fx = UnityEngine.Object.FindObjectOfType<FXManager>();
                if (fx != null)
                {
                    fx.ShowJump(ally, "友军加入：" + tier.Name);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("NotifySpawned 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 可见性诊断。对象池复用可能让渲染器停留在 disabled 状态，
        /// 那样单位"存在但看不见"。这里把关键状态打进日志，便于定位。
        /// </summary>
        private static void DiagnoseVisibility(GameObject ally, HireTier tier)
        {
            try
            {
                int total = 0;
                int enabled = 0;
                string detail = "";

                Transform t = ally.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    SpriteRenderer sr = t.GetChild(i).GetComponent<SpriteRenderer>();
                    if (sr != null)
                    {
                        total++;
                        if (sr.enabled)
                        {
                            enabled++;
                        }

                        if (detail.Length == 0)
                        {
                            detail = " 首个渲染器[enabled=" + sr.enabled
                                     + " sprite=" + (sr.sprite != null ? sr.sprite.name : "null")
                                     + " color.a=" + sr.color.a.ToString("F2")
                                     + " sortingOrder=" + sr.sortingOrder
                                     + " layer=" + sr.sortingLayerName
                                     + " goLayer=" + sr.gameObject.layer + "]";
                        }
                    }
                }

                // 父节点变换 —— 若父节点有缩放/偏移，世界坐标会与预期不符
                Transform par = ally.transform.parent;
                string parentInfo = par != null
                    ? ("父=" + par.name + " 父世界pos=" + par.position
                       + " 父lossyScale=" + par.lossyScale + " 父下共" + par.childCount + "个子物体")
                    : "父=null";

                CurrentDataHub cur = GameRefs.Current;
                float dist = (cur != null && cur.nowChara != null)
                    ? Vector3.Distance(ally.transform.position, cur.nowChara.transform.position)
                    : -1f;

                HireNpcPlugin.Log.LogInfo("[可见性] " + tier.Name
                    + " activeSelf=" + ally.activeSelf
                    + " 渲染器=" + enabled + "/" + total
                    + " 与玩家距离=" + dist.ToString("F2")
                    + " 自身世界pos=" + ally.transform.position
                    + " | " + parentInfo
                    + " |" + detail);

                if (cur != null && cur.nowChara != null)
                {
                    HireNpcPlugin.Log.LogInfo("[可见性] 玩家世界pos=" + cur.nowChara.transform.position
                        + " 玩家父=" + (cur.nowChara.transform.parent != null
                            ? cur.nowChara.transform.parent.name : "null"));
                }

                if (!ally.activeSelf || (total > 0 && enabled == 0))
                {
                    HireNpcPlugin.Log.LogWarning("[可见性] " + tier.Name
                        + " 可能不可见！试着手动启用渲染器。");
                    for (int i = 0; i < t.childCount; i++)
                    {
                        SpriteRenderer sr = t.GetChild(i).GetComponent<SpriteRenderer>();
                        if (sr != null)
                        {
                            sr.enabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("DiagnoseVisibility 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 按护航等级调整 HP / ATK。
        ///
        /// 时机说明：必须在 CreateEnemey 之后调用 —— 因为 CharaState.ResetData 会把
        /// currentHP / currentMaxHp 重置为 maxHP（CharaState.cs:2996-2999），
        /// 若在之前改会被覆盖。
        /// </summary>
        private static void ApplyTierScaling(GameObject go, HireTier tier)
        {
            try
            {
                if (go == null || tier == null)
                {
                    return;
                }

                if (Mathf.Approximately(tier.HpScale, 1f) && Mathf.Approximately(tier.AtkScale, 1f))
                {
                    return;      // 倍率都是 1，无需处理
                }

                CharaState st = go.GetComponent<CharaState>();
                CharaData cd = st != null ? st.GetCurrentData() : null;
                if (cd == null)
                {
                    return;
                }

                int newMaxHp = Mathf.Max(1, Mathf.RoundToInt(cd.maxHP * tier.HpScale));
                cd.maxHP = newMaxHp;
                cd.currentMaxHp = newMaxHp;
                cd.currentHP = newMaxHp;
                cd.atk = Mathf.Max(0, Mathf.RoundToInt(cd.atk * tier.AtkScale));

                // ── 护盾 ────────────────────────────────────────────────────────
                // 游戏里 HUD 的"护甲"= CharaState.damageShield，上限 = CharaData.def。
                // 因此要给出指定的护盾值，必须同时改写 def（决定上限与回复量）
                // 和 damageShield（当前值）。
                if (tier.ShieldValue > 0)
                {
                    cd.def = tier.ShieldValue;

                    if (_fDamageShield == null)
                    {
                        _fDamageShield = AccessTools.Field(typeof(CharaState), "damageShield");
                    }

                    if (_fDamageShield != null)
                    {
                        _fDamageShield.SetValue(st, tier.ShieldValue);
                    }
                }

                HireNpcPlugin.Log.LogInfo("已按「" + tier.Name + "」调整数值: HP="
                    + newMaxHp + " ATK=" + cd.atk
                    + " 护盾=" + (tier.ShieldValue * 0.1f).ToString("F0")
                    + "（倍率 HP×" + tier.HpScale + " ATK×" + tier.AtkScale + "）");
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("ApplyTierScaling 异常: " + ex.Message);
            }
        }

        private static FieldInfo _fEnemyWeapon;

        /// <summary>
        /// 按护航等级配发指定武器。
        ///
        /// 为什么必须换枪：这些人类单位的 atk 字段是 0，
        /// 伤害完全来自 CharaControl.enemyWeapon（见 CharaControl.cs:1848）。
        /// 所以"给更好的枪"是提升护航战力最直接的途径。
        ///
        /// 时机：必须在 CreateEnemey 返回之后 —— 那时 CharaControl 已经用
        /// GetAIWeaponByHardcode 随机生成过一把武器了，我们直接替换掉。
        /// </summary>
        private static void ApplyTierWeapon(GameObject go, HireTier tier)
        {
            try
            {
                if (go == null || tier == null || tier.WeaponId <= 0)
                {
                    return;
                }

                CharaControl ctrl = go.GetComponent<CharaControl>();
                CurrentDataHub cur = GameRefs.Current;
                DataHub dh = GameRefs.Data;
                if (ctrl == null || cur == null || dh == null)
                {
                    return;
                }

                // 武器 ID 必须存在，否则 CreateWeaponFullParts 可能抛异常
                if (dh.itemDataDic != null && !dh.itemDataDic.ContainsKey(tier.WeaponId))
                {
                    HireNpcPlugin.Log.LogWarning("武器 " + tier.WeaponId
                        + " 不在 itemDataDic，跳过配发（" + tier.Name + "）。");
                    return;
                }

                if (_fEnemyWeapon == null)
                {
                    _fEnemyWeapon = AccessTools.Field(typeof(CharaControl), "enemyWeapon");
                }

                if (_fEnemyWeapon == null)
                {
                    HireNpcPlugin.Log.LogWarning("找不到 CharaControl.enemyWeapon 字段，无法配发武器。");
                    return;
                }

                CurrentItemData weapon = cur.CreateWeaponFullParts(tier.WeaponId, 1);
                if (weapon == null)
                {
                    HireNpcPlugin.Log.LogWarning("CreateWeaponFullParts(" + tier.WeaponId + ") 返回 null。");
                    return;
                }

                // 耐久给足，避免刚上场枪就坏
                ItemData wd = weapon.GetData();
                if (wd != null && wd.maxDurability > 0)
                {
                    weapon.nowDurability = wd.maxDurability;
                }

                _fEnemyWeapon.SetValue(ctrl, weapon);

                // 武器名从游戏的文本表取（GetTextByItems 用物品 ID）
                string wname = tier.WeaponId.ToString();
                try
                {
                    TextManager tm = GameRefs.Text;
                    if (tm != null)
                    {
                        string n = tm.GetTextByItems(tier.WeaponId);
                        if (!string.IsNullOrEmpty(n))
                        {
                            wname = n;
                        }
                    }
                }
                catch
                {
                    // 名字取不到就用 ID
                }

                HireNpcPlugin.Log.LogInfo("已为「" + tier.Name + "」配发武器："
                    + wname + "（ID " + tier.WeaponId + "）");
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("ApplyTierWeapon 异常: " + ex.Message);
            }
        }

        /// <summary>Harmony Postfix 回调：把生成出来的对象回填进占位记录。</summary>
        internal static void OnSpawned(GameObject go)
        {
            try
            {
                if (_pending == null || go == null)
                {
                    return;
                }

                AllyRegistry.Complete(_pending, go);
                _pending = null;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("OnSpawned 异常: " + ex);
            }
        }

        private static bool ClearPlayerTarget(CharaControl ctrl)
        {
            try
            {
                CurrentDataHub cur = GameRefs.Current;
                if (cur == null || cur.nowChara == null || ctrl == null)
                {
                    return false;
                }

                if (_fCloseTgt == null)
                {
                    _fCloseTgt = AccessTools.Field(typeof(CharaControl), "closeTgt");
                }

                if (_fCloseTgt == null)
                {
                    return false;
                }

                GameObject tgt = _fCloseTgt.GetValue(ctrl) as GameObject;
                if (tgt != null && tgt == cur.nowChara)
                {
                    ctrl.LostTgt();
                    ctrl.ClearTargetList();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("ClearPlayerTarget 异常: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 回收
        // ------------------------------------------------------------------

        internal static void RecallAll(string reason)
        {
            try
            {
                List<GameObject> list = AllyRegistry.Snapshot();
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                    {
                        PoolRecycle(list[i]);
                        n++;
                    }
                }

                AllyRegistry.Clear();
                _pending = null;

                if (n > 0)
                {
                    HireNpcPlugin.Log.LogInfo("已回收 " + n + " 名友军（" + reason + "）。");
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("RecallAll 异常: " + ex);
            }
        }

        /// <summary>
        /// 让场上队员与订单列表保持一致：撤掉"已经没有对应订单"的队员。
        /// 用于选择性结单之后 —— 否则会出现"订单已结、人还在场"。
        /// </summary>
        internal static void SyncFieldWithOrders()
        {
            try
            {
                List<AllyRecord> toRemove = new List<AllyRecord>();
                for (int i = 0; i < AllyRegistry.Count; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || r.Go == null)
                    {
                        continue;
                    }

                    bool stillOrdered = false;
                    for (int k = 0; k < HireNpcPlugin.Hired.Count; k++)
                    {
                        if (HireNpcPlugin.Hired[k].TierKey == r.TierKey)
                        {
                            stillOrdered = true;
                            break;
                        }
                    }

                    if (!stillOrdered)
                    {
                        toRemove.Add(r);
                    }
                }

                // 一个等级可能有多个订单/多个模型：订单数少于在场数时也要撤掉多余的
                for (int tierIdx = 0; tierIdx < HireData.Tiers.Length; tierIdx++)
                {
                    HireTier tier = HireData.Tiers[tierIdx];
                    int ordered = 0;
                    for (int k = 0; k < HireNpcPlugin.Hired.Count; k++)
                    {
                        if (HireNpcPlugin.Hired[k].TierKey == tier.Key)
                        {
                            ordered++;
                        }
                    }

                    int alive = 0;
                    for (int i = 0; i < AllyRegistry.Count; i++)
                    {
                        AllyRecord r = AllyRegistry.At(i);
                        if (r != null && r.Go != null && r.TierKey == tier.Key && r.IsAlive)
                        {
                            alive++;
                        }
                    }

                    if (alive > ordered)
                    {
                        int excess = alive - ordered;
                        for (int i = AllyRegistry.Count - 1; i >= 0 && excess > 0; i--)
                        {
                            AllyRecord r = AllyRegistry.At(i);
                            if (r != null && r.Go != null && r.TierKey == tier.Key && r.IsAlive
                                && !toRemove.Contains(r))
                            {
                                toRemove.Add(r);
                                excess--;
                            }
                        }
                    }
                }

                int n = 0;
                for (int i = 0; i < toRemove.Count; i++)
                {
                    if (toRemove[i] != null && toRemove[i].Go != null)
                    {
                        PoolRecycle(toRemove[i].Go);
                        n++;
                    }
                }

                // 重建注册表（去掉已回收的）
                List<GameObject> survivors = new List<GameObject>();
                for (int i = 0; i < AllyRegistry.Count; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    bool removed = false;
                    for (int k = 0; k < toRemove.Count; k++)
                    {
                        if (toRemove[k] == r)
                        {
                            removed = true;
                            break;
                        }
                    }

                    if (!removed && r != null && r.Go != null && r.IsAlive)
                    {
                        survivors.Add(r.Go);
                    }
                }

                AllyRegistry.RebuildFrom(survivors);

                if (n > 0)
                {
                    HireNpcPlugin.Log.LogInfo("结单同步：已撤离 " + n + " 名不再有订单的队员。");
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("SyncFieldWithOrders 异常: " + ex);
            }
        }

        // ------------------------------------------------------------------
        // 雇佣 / 解雇（清单 + GP 结算）
        // ------------------------------------------------------------------

        internal static bool TryHire(HireTier tier, out string message)
        {
            message = null;
            try
            {
                if (tier == null)
                {
                    message = "无效档位。";
                    return false;
                }

                if (!GameRefs.IsSaveLoaded())
                {
                    message = "存档数据尚未载入。";
                    return false;
                }

                if (HireTuning.MaxHired > 0 && HireNpcPlugin.Hired.Count >= HireTuning.MaxHired)
                {
                    message = "已达上限（最多 " + HireTuning.MaxHired + " 人）。";
                    return false;
                }

                DataHub dh = GameRefs.Data;
                CurrentDataHub cur = GameRefs.Current;

                int gp = dh.CheckMyGP();
                if (gp < tier.Cost)
                {
                    message = "GP 不足：需要 " + tier.Cost + "，当前 " + gp + "。";
                    return false;
                }

                // BP（点券）= CheckPC(102)，用 AddBP 增减（CurrentDataHub.cs:1948）
                int bp = cur != null ? cur.CheckPC(102) : 0;
                if (tier.BpCost > 0)
                {
                    if (cur == null)
                    {
                        message = "无法读取 BP（CurrentDataHub 未就绪）。";
                        return false;
                    }

                    if (bp < tier.BpCost)
                    {
                        message = "BP 不足：需要 " + tier.BpCost + "，当前 " + bp + "。";
                        return false;
                    }
                }

                // 同步扣费
                dh.ChangeMyGP(-tier.Cost);
                if (tier.BpCost > 0)
                {
                    cur.AddBP(-tier.BpCost);
                }

                HireOrder order = new HireOrder
                {
                    TierKey = tier.Key,
                    Total = HireData.ContractDepartures,
                    Used = 0,
                    PaidGp = tier.Cost,          // 记录实付，退款以此为准
                    PaidBp = tier.BpCost
                };

                HireNpcPlugin.Hired.Add(order);
                HireStore.SaveOrders(HireNpcPlugin.Hired);

                message = "已点单「" + tier.Name + "」，花费 " + tier.Cost + " GP"
                          + (tier.BpCost > 0 ? " + " + tier.BpCost + " BP" : "")
                          + "。合约 " + order.Total + " 次护航"
                          + "（当前共 " + HireNpcPlugin.Hired.Count + " 笔订单）";
                HireNpcPlugin.Log.LogInfo(message);

                // 说明：按需求已取消"点单后立即生成"。
                // 护航队员只会在**进入关卡地图**时统一生成（见 PostCreateWorld），
                // 在营地/地图界面点单只登记订单，不会立刻出现。
                HireNpcPlugin.Log.LogInfo("订单已登记；护航队员将在进入关卡时生成。");

                return true;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("TryHire 异常: " + ex);
                message = "雇佣失败（详见日志）。";
                return false;
            }
        }

        /// <summary>
        /// 续单：为指定订单追加 N 单（每单 +3 次成功撤离），
        /// 每单价格 = 原价 × 20%。
        /// </summary>
        internal static bool TryRenew(int index, int units, out string message)
        {
            message = null;
            try
            {
                if (index < 0 || index >= HireNpcPlugin.Hired.Count)
                {
                    message = "无效下标。";
                    return false;
                }

                if (units < 1)
                {
                    message = "续单数量至少为 1。";
                    return false;
                }

                HireOrder order = HireNpcPlugin.Hired[index];
                HireTier tier = HireData.Find(order.TierKey);
                if (tier == null)
                {
                    message = "找不到该订单对应的护航等级。";
                    return false;
                }

                DataHub dh = GameRefs.Data;
                CurrentDataHub cur = GameRefs.Current;

                int unitGp = HireData.RenewUnitGp(tier);
                int unitBp = HireData.RenewUnitBp(tier);

                int totalGp = unitGp * units;
                int totalBp = unitBp * units;

                int gp = dh.CheckMyGP();
                if (gp < totalGp)
                {
                    message = "续单失败：GP 不足（需要 " + totalGp + "，当前 " + gp + "）。";
                    return false;
                }

                if (totalBp > 0)
                {
                    if (cur == null)
                    {
                        message = "续单失败：无法读取 BP。";
                        return false;
                    }

                    int bp = cur.CheckPC(102);
                    if (bp < totalBp)
                    {
                        message = "续单失败：BP 不足（需要 " + totalBp + "，当前 " + bp + "）。";
                        return false;
                    }
                }

                // 扣费
                dh.ChangeMyGP(-totalGp);
                if (totalBp > 0)
                {
                    cur.AddBP(-totalBp);
                }

                // 记入实付（退款以此为准）
                order.PaidGp += totalGp;
                order.PaidBp += totalBp;

                // 追加次数
                int added = HireData.RenewDepartures * units;
                order.Total += added;
                HireStore.SaveOrders(HireNpcPlugin.Hired);

                message = "已续单「" + tier.Name + "」× " + units + "，花费 " + totalGp + " GP"
                          + (totalBp > 0 ? " + " + totalBp + " BP" : "")
                          + "（每单 " + unitGp + " GP）。新增 " + added
                          + " 次，合约共 " + order.Total + " 次，剩余 " + order.Remaining + " 次。";

                HireNpcPlugin.Log.LogInfo(message);
                return true;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("TryRenew 异常: " + ex);
                message = "续单失败（详见日志）。";
                return false;
            }
        }

        /// <summary>
        /// 提前结单（可选择性结掉某一笔订单），按已使用的撤离次数退款。
        ///
        /// 退款规则（用户最终确认）：
        ///     退款 = 实付总额 × (0.6 − 已用次数 × 0.2)，下限 0
        /// </summary>
        internal static bool TryFire(int index, out string message)
        {
            message = null;
            try
            {
                if (index < 0 || index >= HireNpcPlugin.Hired.Count)
                {
                    message = "无效下标。";
                    return false;
                }

                HireOrder order = HireNpcPlugin.Hired[index];
                HireTier tier = HireData.Find(order.TierKey);

                int refund = HireData.RefundAmount(order);
                int refundBp = HireData.RefundBpAmount(order);
                float ratio = HireData.RefundRatio(order.Used);

                HireNpcPlugin.Hired.RemoveAt(index);
                HireStore.SaveOrders(HireNpcPlugin.Hired);

                string name = tier != null ? tier.Name : order.TierKey;
                string bpPart = refundBp > 0 ? " + " + refundBp + " BP" : "";

                if (GameRefs.IsSaveLoaded() && (refund > 0 || refundBp > 0))
                {
                    if (refund > 0)
                    {
                        GameRefs.Data.ChangeMyGP(refund);
                    }

                    if (refundBp > 0)
                    {
                        CurrentDataHub cur = GameRefs.Current;
                        if (cur != null)
                        {
                            cur.AddBP(refundBp);
                        }
                    }

                    message = "已提前结单「" + name + "」"
                              + "（已用 " + order.Used + "/" + order.Total + " 次）"
                              + "，按 " + Mathf.RoundToInt(ratio * 100f) + "% 退还 "
                              + refund + " GP" + bpPart + "。";
                }
                else if (refund <= 0 && refundBp <= 0)
                {
                    message = "已提前结单「" + name + "」"
                              + "（已用 " + order.Used + "/" + order.Total + " 次）"
                              + "，按 0% 退还，不退款。";
                }
                else
                {
                    message = "已从名单移除。";
                }

                HireNpcPlugin.Log.LogInfo(message);
                return true;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("TryFire 异常: " + ex);
                message = "结单失败（详见日志）。";
                return false;
            }
        }
    }
}
