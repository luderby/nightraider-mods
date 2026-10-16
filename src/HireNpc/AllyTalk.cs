using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 护航队员的台词库。
    ///
    /// 所有文本都绑到 BepInEx 配置文件（BepInEx/config/nightraider.hirenpc.cfg）的
    /// [Talk] 段，用户可以直接编辑 cfg 修改，不需要改代码。
    ///
    /// 填写格式：用逗号分隔多个候选，运行时随机取一句。
    /// 例如：  CombatTalk = 掩护我！, 目标已锁定, 换弹中…
    /// 留空则该类台词不出现。
    /// </summary>
    internal static class AllyTalk
    {
        public const string Section = "Talk";

        // 配置项（在 HireNpcPlugin 初始化时绑定）
        internal static ConfigEntry<string> CfgIdle;
        internal static ConfigEntry<string> CfgFollow;
        internal static ConfigEntry<string> CfgCombat;
        internal static ConfigEntry<string> CfgReload;
        internal static ConfigEntry<string> CfgKillSelf;
        internal static ConfigEntry<string> CfgKillOther;
        internal static ConfigEntry<string> CfgSelfHurt;
        internal static ConfigEntry<string> CfgPlayerHurt;
        internal static ConfigEntry<string> CfgPlayerDown;
        internal static ConfigEntry<string> CfgAllyDown;
        internal static ConfigEntry<string> CfgAllyRevive;
        internal static ConfigEntry<string> CfgLowHp;
        internal static ConfigEntry<string> CfgEnemyMany;
        internal static ConfigEntry<string> CfgEnemyCleared;
        internal static ConfigEntry<string> CfgFoundLoot;
        internal static ConfigEntry<string> CfgFlee;

        // ── 默认文本 ────────────────────────────────────────────────────────
        private const string DefaultIdle =
            "周围安静了, 检查弹药…, 有点饿了, 这地方真够呛, 继续走吧, 注意脚下, 我跟着你, 随时待命, 烟还有吗, 这地图真大";

        private const string DefaultFollow =
            "跟上你了, 慢点，我跟不上, 就在你后面, 我在";

        private const string DefaultCombat =
            "掩护我！, 目标已锁定, 清除威胁！, 左翼安全, 发现敌人, 压制火力！, 别过来！, 还剩几个？, 找掩体！, 注意侧翼！";

        private const string DefaultReload =
            "换弹中…, 掩护我换弹！, 弹匣空了, 给我两秒！, 退后，我换弹, 弹没了！";

        private const string DefaultKillSelf =
            "干掉了！, 干净利落, 解决, 下一个, 正中靶心";

        private const string DefaultKillOther =
            "打得好！, 漂亮, 记你一功, 继续, 好枪法";

        private const string DefaultSelfHurt =
            "中弹了！, 需要掩护！, 撑住…, 该死！, 我受伤了！, 挨了一下！";

        private const string DefaultPlayerHurt =
            "老大中弹了！, 掩护队长！, 你没事吧？, 止血！, 撑住，我在！, 队长受伤了！";

        private const string DefaultPlayerDown =
            "队长！不！, 撑住啊！, 快救人！, 别死！";

        private const string DefaultAllyDown =
            "有人倒下了！, 快救人！, 别死啊！, 撤！, 少一个人了…";

        private const string DefaultAllyRevive =
            "欢迎回来, 站稳了？, 别再倒下了";

        private const string DefaultLowHp =
            "我快撑不住了…, 需要治疗！, 血量危险！, 我快不行了, 掩护我撤！";

        private const string DefaultEnemyMany =
            "数量太多了！, 被包围了！, 稳住阵型！";

        private const string DefaultEnemyCleared =
            "清干净了, 安全了, 继续推进";

        private const string DefaultFoundLoot =
            "有物资！, 这边有箱子, 过去看看";

        private const string DefaultFlee =
            "撤！上直升机！, 走！, 任务完成，撤！";

        // ── 解析结果缓存 ────────────────────────────────────────────────────
        private static string[] _idle;
        private static string[] _follow;
        private static string[] _combat;
        private static string[] _reload;
        private static string[] _killSelf;
        private static string[] _killOther;
        private static string[] _selfHurt;
        private static string[] _playerHurt;
        private static string[] _playerDown;
        private static string[] _allyDown;
        private static string[] _allyRevive;
        private static string[] _lowHp;
        private static string[] _enemyMany;
        private static string[] _enemyCleared;
        private static string[] _foundLoot;
        private static string[] _flee;

        internal static void Bind(ConfigFile cfg)
        {
            CfgIdle = cfg.Bind(Section, "IdleTalk", DefaultIdle,
                "平时闲聊（非战斗状态随机出现）。用逗号分隔多个候选，留空=不出现。");
            CfgFollow = cfg.Bind(Section, "FollowTalk", DefaultFollow,
                "跟随移动中偶尔说的话。");
            CfgCombat = cfg.Bind(Section, "CombatTalk", DefaultCombat,
                "战斗中随机台词（有敌人在场时的闲聊）。");
            CfgReload = cfg.Bind(Section, "ReloadTalk", DefaultReload,
                "开始换弹时说的话。");
            CfgKillSelf = cfg.Bind(Section, "KillSelfTalk", DefaultKillSelf,
                "自己击杀敌人时说的话（累积 3 杀才说一句）。");
            CfgKillOther = cfg.Bind(Section, "KillOtherTalk", DefaultKillOther,
                "同伴或玩家击杀敌人时说的话。");
            CfgSelfHurt = cfg.Bind(Section, "SelfHurtTalk", DefaultSelfHurt,
                "自己受伤时说的话。");
            CfgPlayerHurt = cfg.Bind(Section, "PlayerHurtTalk", DefaultPlayerHurt,
                "玩家受伤时说的话。");
            CfgPlayerDown = cfg.Bind(Section, "PlayerDownTalk", DefaultPlayerDown,
                "玩家倒地/阵亡时说的话。");
            CfgAllyDown = cfg.Bind(Section, "AllyDownTalk", DefaultAllyDown,
                "有护航同伴倒下时说的话。");
            CfgAllyRevive = cfg.Bind(Section, "AllyReviveTalk", DefaultAllyRevive,
                "同伴被救起时说的话（预留）。");
            CfgLowHp = cfg.Bind(Section, "LowHpTalk", DefaultLowHp,
                "自己血量低于 30% 时说的话。");
            CfgEnemyMany = cfg.Bind(Section, "EnemyManyTalk", DefaultEnemyMany,
                "周围敌人很多时说的话（预留）。");
            CfgEnemyCleared = cfg.Bind(Section, "EnemyClearedTalk", DefaultEnemyCleared,
                "附近敌人被清空时说的话（预留）。");
            CfgFoundLoot = cfg.Bind(Section, "FoundLootTalk", DefaultFoundLoot,
                "发现物资时说的话（预留）。");
            CfgFlee = cfg.Bind(Section, "FleeTalk", DefaultFlee,
                "撤离阶段说的话（预留）。");
        }

        /// <summary>配置改动后调用，让缓存失效。</summary>
        internal static void Invalidate()
        {
            _idle = _follow = _combat = _reload = null;
            _killSelf = _killOther = _selfHurt = _playerHurt = null;
            _playerDown = _allyDown = _allyRevive = _lowHp = null;
            _enemyMany = _enemyCleared = _foundLoot = _flee = null;
        }

        internal static string[] Idle { get { return _idle ?? (_idle = Parse(CfgIdle)); } }
        internal static string[] Follow { get { return _follow ?? (_follow = Parse(CfgFollow)); } }
        internal static string[] Combat { get { return _combat ?? (_combat = Parse(CfgCombat)); } }
        internal static string[] Reload { get { return _reload ?? (_reload = Parse(CfgReload)); } }
        internal static string[] KillSelf { get { return _killSelf ?? (_killSelf = Parse(CfgKillSelf)); } }
        internal static string[] KillOther { get { return _killOther ?? (_killOther = Parse(CfgKillOther)); } }
        internal static string[] SelfHurt { get { return _selfHurt ?? (_selfHurt = Parse(CfgSelfHurt)); } }
        internal static string[] PlayerHurt { get { return _playerHurt ?? (_playerHurt = Parse(CfgPlayerHurt)); } }
        internal static string[] PlayerDown { get { return _playerDown ?? (_playerDown = Parse(CfgPlayerDown)); } }
        internal static string[] AllyDown { get { return _allyDown ?? (_allyDown = Parse(CfgAllyDown)); } }
        internal static string[] AllyRevive { get { return _allyRevive ?? (_allyRevive = Parse(CfgAllyRevive)); } }
        internal static string[] LowHp { get { return _lowHp ?? (_lowHp = Parse(CfgLowHp)); } }
        internal static string[] EnemyMany { get { return _enemyMany ?? (_enemyMany = Parse(CfgEnemyMany)); } }
        internal static string[] EnemyCleared { get { return _enemyCleared ?? (_enemyCleared = Parse(CfgEnemyCleared)); } }
        internal static string[] FoundLoot { get { return _foundLoot ?? (_foundLoot = Parse(CfgFoundLoot)); } }
        internal static string[] Flee { get { return _flee ?? (_flee = Parse(CfgFlee)); } }

        /// <summary>从"逗号 / 顿号 / 分号 / 竖线分隔"的文本解析出候选列表。</summary>
        private static string[] Parse(ConfigEntry<string> entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Value))
            {
                return new string[0];
            }

            string raw = entry.Value.Replace('，', ',')
                                    .Replace('、', ',')
                                    .Replace('；', ',')
                                    .Replace(';', ',')
                                    .Replace('|', ',');

            string[] parts = raw.Split(',');
            List<string> list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length > 0)
                {
                    list.Add(s);
                }
            }

            return list.ToArray();
        }
    }
}
