using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 从外部文本文件加载护航台词文案，覆盖 cfg 中的默认值。
    ///
    /// 文件格式（见 _moddev/护航台词文案.txt）：
    ///     [IdleTalk]
    ///     - 周围安静了
    ///     - 检查弹药…
    ///
    /// 搜索顺序（找到第一个就生效）：
    ///     1. BepInEx/config/NightRaider.HireNpc.Talk.txt
    ///     2. 游戏根目录/护航台词文案.txt
    ///     3. 若都不存在 → 自动按内置默认值写出一份，方便用户编辑
    ///
    /// 用户编辑后重新启动游戏即可生效；也可以在内游戏里按 X-4 临时闭麦。
    /// </summary>
    internal static class TalkFile
    {
        internal const string FileName = "NightRaider.HireNpc.Talk.txt";

        /// <summary>
        /// 两条台词之间的最小间隔（帧）。60 帧 ≈ 1 秒。
        /// 用于解决"怪多的时候台词冒得太频繁"。
        /// </summary>
        internal const int MinGapFrames = 180;      // 约 3 秒

        /// <summary>累积多少次击杀才说一句击杀台词（避免刷屏）。</summary>
        internal const int KillsPerLine = 3;

        /// <summary>分类名 → 该分类下的候选文本。</summary>
        private static readonly Dictionary<string, List<string>> _sections =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static string _loadedFrom;

        /// <summary>尝试加载；成功则把内容写入对应的 cfg 项。</summary>
        internal static void LoadAndApply()
        {
            try
            {
                _sections.Clear();

                string path = FindFile();
                if (path == null)
                {
                    // 没有就导出一份默认模板，方便用户直接改
                    path = ExportTemplate();
                    if (path == null)
                    {
                        return;
                    }
                }

                if (!Parse(path))
                {
                    return;
                }

                _loadedFrom = path;

                // 注意：**文件里没有出现的分类一律清空**（用户的意图就是"删掉即失效"）。
                // 因此这里列出全部已知分类，逐个按文件内容覆盖（缺失→空）。
                int applied = 0;
                applied += Apply("IdleTalk", AllyTalk.CfgIdle);
                applied += Apply("FollowTalk", AllyTalk.CfgFollow);
                applied += Apply("CombatTalk", AllyTalk.CfgCombat);
                applied += Apply("ReloadTalk", AllyTalk.CfgReload);
                applied += Apply("KillSelfTalk", AllyTalk.CfgKillSelf);
                applied += Apply("KillOtherTalk", AllyTalk.CfgKillOther);
                applied += Apply("SelfHurtTalk", AllyTalk.CfgSelfHurt);
                applied += Apply("PlayerHurtTalk", AllyTalk.CfgPlayerHurt);
                applied += Apply("PlayerDownTalk", AllyTalk.CfgPlayerDown);
                applied += Apply("AllyDownTalk", AllyTalk.CfgAllyDown);
                applied += Apply("AllyReviveTalk", AllyTalk.CfgAllyRevive);
                applied += Apply("LowHpTalk", AllyTalk.CfgLowHp);
                applied += Apply("EnemyManyTalk", AllyTalk.CfgEnemyMany);
                applied += Apply("EnemyClearedTalk", AllyTalk.CfgEnemyCleared);
                applied += Apply("FoundLootTalk", AllyTalk.CfgFoundLoot);
                applied += Apply("FleeTalk", AllyTalk.CfgFlee);

                AllyTalk.Invalidate();

                HireNpcPlugin.Log.LogInfo("[台词] 已从 " + path + " 载入 " + applied + " 类文案。");
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("[台词] 载入失败: " + ex.Message);
            }
        }

        private static string FindFile()
        {
            string[] candidates =
            {
                Path.Combine(Paths.ConfigPath, FileName),
                Path.Combine(Paths.GameRootPath, FileName),
                Path.Combine(Paths.GameRootPath, "护航台词文案.txt"),
                Path.Combine(Paths.BepInExRootPath, FileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    if (File.Exists(candidates[i]))
                    {
                        return candidates[i];
                    }
                }
                catch
                {
                    // 忽略不可访问的路径
                }
            }

            return null;
        }

        /// <summary>把当前（默认）文案导出成模板文件。</summary>
        private static string ExportTemplate()
        {
            try
            {
                string path = Path.Combine(Paths.ConfigPath, FileName);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 夜袭者护航俱乐部 —— 台词文案（自动生成）");
                sb.AppendLine("# 每行一句，以「- 」开头；# 开头是注释。");
                sb.AppendLine("# 某个分类下不写任何行 → 该类台词不出现。");
                sb.AppendLine("# 改完保存并重启游戏即可生效（或在游戏里按 X-4 临时闭麦）。");

                WriteSection(sb, "IdleTalk", "平时闲聊（非战斗，每 8~18 秒随机）", AllyTalk.CfgIdle);
                WriteSection(sb, "FollowTalk", "跟随移动中偶尔", AllyTalk.CfgFollow);
                WriteSection(sb, "CombatTalk", "战斗中闲聊", AllyTalk.CfgCombat);
                WriteSection(sb, "ReloadTalk", "开始换弹的瞬间", AllyTalk.CfgReload);
                WriteSection(sb, "KillSelfTalk", "自己击杀敌人（累积 3 杀才说）", AllyTalk.CfgKillSelf);
                WriteSection(sb, "KillOtherTalk", "同伴或玩家击杀敌人", AllyTalk.CfgKillOther);
                WriteSection(sb, "SelfHurtTalk", "自己被打中", AllyTalk.CfgSelfHurt);
                WriteSection(sb, "PlayerHurtTalk", "玩家被打中", AllyTalk.CfgPlayerHurt);
                WriteSection(sb, "PlayerDownTalk", "玩家倒地/阵亡", AllyTalk.CfgPlayerDown);
                WriteSection(sb, "AllyDownTalk", "同伴倒下", AllyTalk.CfgAllyDown);
                WriteSection(sb, "AllyReviveTalk", "同伴被救起（预留）", AllyTalk.CfgAllyRevive);
                WriteSection(sb, "LowHpTalk", "自己血量低于 30%", AllyTalk.CfgLowHp);
                WriteSection(sb, "EnemyManyTalk", "周围敌人很多（预留）", AllyTalk.CfgEnemyMany);
                WriteSection(sb, "EnemyClearedTalk", "附近敌人清空（预留）", AllyTalk.CfgEnemyCleared);
                WriteSection(sb, "FoundLootTalk", "发现物资（预留）", AllyTalk.CfgFoundLoot);
                WriteSection(sb, "FleeTalk", "撤离阶段（预留）", AllyTalk.CfgFlee);

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                HireNpcPlugin.Log.LogInfo("[台词] 已导出默认文案模板: " + path);
                return path;
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogWarning("[台词] 导出模板失败: " + ex.Message);
                return null;
            }
        }

        private static void WriteSection(StringBuilder sb, string name, string desc,
                                         BepInEx.Configuration.ConfigEntry<string> entry)
        {
            sb.AppendLine();
            sb.AppendLine("[" + name + "]        # " + desc);
            if (entry != null && !string.IsNullOrEmpty(entry.Value))
            {
                string[] parts = entry.Value.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string s = parts[i].Trim();
                    if (s.Length > 0)
                    {
                        sb.Append("- ").AppendLine(s);
                    }
                }
            }
        }

        /// <summary>解析文件（返回是否有任何有效分类）。</summary>
        private static bool Parse(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            string current = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("#"))
                {
                    continue;
                }

                if (s.StartsWith("[") && s.EndsWith("]"))
                {
                    current = s.Substring(1, s.Length - 2).Trim();
                    if (!_sections.ContainsKey(current))
                    {
                        _sections[current] = new List<string>();
                    }
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                // 支持 "- 文本" 或直接写文本
                if (s.StartsWith("-"))
                {
                    s = s.Substring(1).Trim();
                }

                if (s.Length > 0)
                {
                    _sections[current].Add(s);
                }
            }

            return _sections.Count > 0;
        }

        private static int Apply(string section, BepInEx.Configuration.ConfigEntry<string> entry)
        {
            if (entry == null)
            {
                return 0;
            }

            List<string> list;
            if (!_sections.TryGetValue(section, out list))
            {
                // 文件里没有这个分类 → 清空（用户"删掉即失效"的意图）
                entry.Value = "";
                _cleared++;
                return 1;
            }

            // 文件里显式写了这个分类（哪怕是空的）→ 以文件为准
            entry.Value = string.Join(", ", list.ToArray());
            if (list.Count == 0)
            {
                _cleared++;
            }

            return 1;
        }

        private static int _cleared;
    }
}
