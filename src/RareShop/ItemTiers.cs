using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Reads the external modelID -> tier table.
    ///
    /// The game has no rarity data at all: neither ItemData nor CurrentItemData declares
    /// a rarity/quality/grade member (verified against the decompiled Assembly-CSharp).
    /// So the tier cannot be "read" from the game and has to come from a table instead.
    ///
    /// The table is a CSV the player owns:
    ///
    ///     BepInEx/config/NightRaider_item_tiers.csv
    ///
    /// Only two columns are required, <c>modelId</c> and <c>tier</c>; they are located by
    /// header name so the column order does not matter and any extra columns (name, price,
    /// the derivation evidence, notes for yourself) are ignored. Tiers are 1..5.
    ///
    /// A missing file is not an error: the mod keeps the game's own colours and logs why.
    /// </summary>
    internal static class ItemTiers
    {
        private const string FileName = "NightRaider_item_tiers.csv";

        private static readonly Dictionary<int, int> Map = new Dictionary<int, int>();
        private static string _sourcePath;
        private static bool _warnedMissing;

        /// <summary>Number of model ids currently mapped.</summary>
        internal static int Count { get { return Map.Count; } }

        /// <summary>Path the table was loaded from, or null when nothing was loaded.</summary>
        internal static string SourcePath { get { return _sourcePath; } }

        internal static string ConfigPath
        {
            get { return Path.Combine(Paths.ConfigPath, FileName); }
        }

        /// <summary>Drops the cached table and reads it again. Safe to call at any time.</summary>
        internal static void Reload()
        {
            Map.Clear();
            _sourcePath = null;
            Load();
        }

        internal static void Load()
        {
            try
            {
                string path = ResolvePath();
                if (path == null)
                {
                    WarnMissingOnce();
                    return;
                }

                int bad = 0;
                int pending = 0;
                int rows = ParseFile(path, ref bad, ref pending);
                _sourcePath = path;

                RareShopPlugin.Log.LogInfo(
                    "稀有度表已载入：" + path + "（" + rows + " 行，映射 " + Map.Count + " 个物品"
                    + (bad > 0 ? "，跳过 " + bad + " 行无效数据" : "")
                    + (pending > 0 ? "，" + pending + " 个物品档位为 0（待定），保持游戏原色" : "")
                    + "）");
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogError("稀有度表载入失败，将使用游戏原色：" + ex);
            }
        }

        /// <summary>
        /// Finds the table: the config copy first, then a template shipped next to the plugin
        /// dll (which is copied into config so it becomes editable).
        /// </summary>
        private static string ResolvePath()
        {
            string configPath = ConfigPath;
            if (File.Exists(configPath))
            {
                return configPath;
            }

            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    string template = Path.Combine(pluginDir, FileName);
                    if (File.Exists(template))
                    {
                        Directory.CreateDirectory(Paths.ConfigPath);
                        File.Copy(template, configPath, false);
                        RareShopPlugin.Log.LogInfo("已把稀有度表模板复制到 " + configPath + "，可直接编辑。");
                        return configPath;
                    }
                }
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogWarning("复制稀有度表模板失败：" + ex.Message);
            }

            return null;
        }

        private static void WarnMissingOnce()
        {
            if (_warnedMissing)
            {
                return;
            }
            _warnedMissing = true;
            RareShopPlugin.Log.LogWarning(
                "未找到稀有度表 " + ConfigPath + "，稀有度底色不生效（游戏原色保持不变）。");
        }

        /// <summary>Parses the CSV. Returns the number of data rows seen.</summary>
        private static int ParseFile(string path, ref int bad, ref int pending)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0)
            {
                return 0;
            }

            int idCol = -1;
            int tierCol = -1;
            int seen = 0;
            bool headerFound = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null || line.Trim().Length == 0)
                {
                    continue;
                }

                List<string> cells = SplitCsv(line);

                if (!headerFound)
                {
                    for (int c = 0; c < cells.Count; c++)
                    {
                        string h = cells[c].Trim().ToLowerInvariant();
                        if (h == "modelid" || h == "id")
                        {
                            idCol = c;
                        }
                        else if (h == "tier" || h == "rarity")
                        {
                            tierCol = c;
                        }
                    }

                    if (idCol < 0 || tierCol < 0)
                    {
                        RareShopPlugin.Log.LogError(
                            "稀有度表缺少表头 modelId / tier 列：" + path);
                        return 0;
                    }
                    headerFound = true;
                    continue;
                }

                if (idCol >= cells.Count || tierCol >= cells.Count)
                {
                    bad++;
                    continue;
                }

                int id;
                int tier;
                if (!int.TryParse(cells[idCol].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    bad++;
                    continue;
                }
                if (!int.TryParse(cells[tierCol].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tier))
                {
                    bad++;
                    continue;
                }

                seen++;
                if (id <= 0)
                {
                    bad++;
                    continue;
                }

                if (tier <= 0)
                {
                    // tier <= 0 means "not assigned yet" in the table (the hand-assignment
                    // list). Skip the row so TierOf returns 0 and the caller leaves the game's
                    // own colour alone, instead of clamping to tier 1 and painting it common.
                    pending++;
                    continue;
                }

                if (tier > RarityPalette.MaxTier)
                {
                    tier = RarityPalette.MaxTier;
                }

                Map[id] = tier;
            }

            return seen;
        }

        /// <summary>
        /// Minimal RFC4180-ish CSV split: handles quoted fields, doubled quotes inside them
        /// and commas inside quotes. The shipped table has item names with both.
        /// </summary>
        private static List<string> SplitCsv(string line)
        {
            List<string> result = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                else if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    result.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(ch);
                }
            }

            result.Add(sb.ToString());
            return result;
        }

        /// <summary>
        /// Tier of an item, or 0 when the table does not know it. Callers must treat 0 as
        /// "leave the game's own colour alone" rather than painting a default.
        /// </summary>
        internal static int TierOf(int modelId)
        {
            int tier;
            if (Map.TryGetValue(modelId, out tier))
            {
                return tier;
            }
            return 0;
        }
    }
}
