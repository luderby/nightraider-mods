using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRaider.HireNpc
{
    /// <summary>
    /// 友军标识（IMGUI 绘制）。
    ///
    /// 为什么用 IMGUI：本 MOD 的 uGUI 指令菜单多次排查仍不可见，而 IMGUI（OnGUI）
    /// 已实测可用。标识需要每帧跟随友军的世界坐标，IMGUI 的
    /// Camera.WorldToScreenPoint + GUI.Label 组合简单可靠。
    ///
    /// 提供三种标识，可用配置项组合开关：
    ///   1. 名称标签（档位名，如"步枪兵"）—— 敌人没有名字标签，这是最直接的区分方式
    ///   2. 血条            —— 实时显示友军 HP
    ///   3. 距离            —— 显示与玩家的距离，便于判断 15 米瞬移阈值
    ///
    /// 全部绘制在友军头顶上方，随镜头移动自动跟随。
    /// </summary>
    internal static class AllyHud
    {
        private static GUIStyle _nameStyle;
        private static GUIStyle _infoStyle;
        private static Texture2D _barBg;
        private static Texture2D _barFg;
        private static Texture2D _barBorder;

        /// <summary>每帧由驱动器的 OnGUI 调用。</summary>
        internal static void Draw()
        {
            try
            {
                if (AllyRegistry.Count == 0)
                {
                    return;
                }

                // 只在关卡内绘制（营地不显示）
                if (!HireNpcPlugin.InMission)
                {
                    return;
                }

                // 打开背包 / 设置 / 地图等游戏界面时隐藏，避免压在那些界面上。
                // 注意 HirePanel 打开时我们会强制 CheckWindowOpen 返回 true，
                // 所以要把自己的面板排除掉。
                GameManager gm = GameRefs.Game;
                if (gm != null && gm.CheckWindowOpen() && !HirePanel.IsOpen)
                {
                    return;
                }

                // M 大地图打开时也隐藏（地图上另有专用标记）
                if (GameRefs.MiniMap != null && GameRefs.MiniMap.CheckOpen())
                {
                    return;
                }

                Camera cam = Camera.main;
                if (cam == null)
                {
                    return;
                }

                CurrentDataHub cur = GameRefs.Current;
                GameObject player = cur != null ? cur.nowChara : null;

                EnsureStyles();
                if (_nameStyle == null)
                {
                    return;
                }

                float scale = Screen.height / 1080f;
                if (scale < 0.6f) scale = 0.6f;

                for (int i = 0; i < AllyRegistry.Count; i++)
                {
                    AllyRecord r = AllyRegistry.At(i);
                    if (r == null || r.Go == null || !r.IsAlive)
                    {
                        continue;
                    }

                    Vector3 sp = cam.WorldToScreenPoint(r.Go.transform.position + new Vector3(0f, 0.9f, 0f));
                    if (sp.z < 0f)
                    {
                        continue;      // 在镜头背后
                    }

                    // WorldToScreenPoint 原点在左下，OnGUI 在左上 → 翻转 Y
                    float sx = sp.x;
                    float sy = Screen.height - sp.y;

                    DrawNameAndBar(r, sx, sy, scale, player);
                }
            }
            catch (Exception ex)
            {
                HireNpcPlugin.Log.LogError("AllyHud.Draw 异常: " + ex);
            }
        }

        private static void DrawNameAndBar(AllyRecord r, float sx, float sy, float scale, GameObject player)
        {
            bool showName = HireNpcPlugin.CfgAllyName == null || HireNpcPlugin.CfgAllyName.Value;
            bool showBar = HireNpcPlugin.CfgAllyHpBar == null || HireNpcPlugin.CfgAllyHpBar.Value;
            bool showDist = HireNpcPlugin.CfgAllyDistance != null && HireNpcPlugin.CfgAllyDistance.Value;

            float y = sy;

            // ── 名称/状态 ──────────────────────────────────────────────────────
            // 用户要求：地图中友军只显示状态，不再显示「金牌护/魔王护」这类等级名。
            if (showName)
            {
                // 直接显示当前状态
                string label = HireNpcPlugin.ModeName(r.Mode);

                float w = 260f * scale;
                float h = 26f * scale;
                GUI.Label(new Rect(sx - w * 0.5f, y - h, w, h), label, _nameStyle);
                y -= h;
            }

            // ── 血条 + 护盾条 ───────────────────────────────────────────────────
            if (showBar)
            {
                CharaState st = r.State;
                CharaData cd = st != null ? st.GetCurrentData() : null;

                float bw = 64f * scale;
                float bh = 7f * scale;
                float bx = sx - bw * 0.5f;
                float by = y - bh - 2f * scale;

                // 护盾（黄色）画在血条上方，与游戏的护甲显示口径一致：
                // 当前值 = CharaState.damageShield，上限 = CharaData.def
                float shieldRatio = 0f;
                if (st != null && cd != null && cd.def > 0)
                {
                    shieldRatio = Mathf.Clamp01((float)st.GetDamageShied() / cd.def);
                }

                if (shieldRatio > 0f)
                {
                    float sh = 6f * scale;
                    float sy2 = by - sh - 2f * scale;

                    GUI.DrawTexture(new Rect(bx - 1f, sy2 - 1f, bw + 2f, sh + 2f), _barBorder);
                    GUI.DrawTexture(new Rect(bx, sy2, bw, sh), _barBg);

                    Color oldS = GUI.color;
                    GUI.color = new Color(1f, 0.85f, 0.20f, 1f);      // 黄色护盾
                    GUI.DrawTexture(new Rect(bx, sy2, bw * shieldRatio, sh), _barFg);
                    GUI.color = oldS;

                    y = sy2;
                }

                if (cd != null && cd.currentMaxHp > 0)
                {
                    float ratio = Mathf.Clamp01((float)cd.currentHP / (float)cd.currentMaxHp);

                    // 外框 + 底色
                    GUI.DrawTexture(new Rect(bx - 1f, by - 1f, bw + 2f, bh + 2f), _barBorder);
                    GUI.DrawTexture(new Rect(bx, by, bw, bh), _barBg);

                    // 血量（绿→黄→红）
                    Color c = ratio > 0.5f
                        ? Color.Lerp(new Color(0.95f, 0.85f, 0.25f, 1f), new Color(0.35f, 0.90f, 0.40f, 1f),
                                     (ratio - 0.5f) * 2f)
                        : Color.Lerp(new Color(0.95f, 0.30f, 0.25f, 1f), new Color(0.95f, 0.85f, 0.25f, 1f),
                                     ratio * 2f);

                    Color old = GUI.color;
                    GUI.color = c;
                    GUI.DrawTexture(new Rect(bx, by, bw * ratio, bh), _barFg);
                    GUI.color = old;

                    y = by;
                }
            }

            // ── 与玩家距离 ──────────────────────────────────────────────────────
            if (showDist && player != null)
            {
                float d = Vector3.Distance(r.Go.transform.position, player.transform.position);
                float w = 120f * scale;
                float h = 24f * scale;
                GUI.Label(new Rect(sx - w * 0.5f, y - h, w, h),
                          d.ToString("F0") + "m", _infoStyle);
            }
        }

        private static void EnsureStyles()
        {
            if (_nameStyle != null)
            {
                return;
            }

            Font f = GameRefs.FindUiFont();

            _nameStyle = new GUIStyle(GUI.skin != null ? GUI.skin.label : null);
            _nameStyle.font = f;
            _nameStyle.fontSize = 20;
            _nameStyle.alignment = TextAnchor.LowerCenter;
            _nameStyle.normal.textColor = new Color(0.45f, 1f, 0.55f, 1f);   // 亮绿：一眼区分敌人
            _nameStyle.wordWrap = false;

            _infoStyle = new GUIStyle(_nameStyle);
            _infoStyle.fontSize = 16;
            _infoStyle.normal.textColor = new Color(0.85f, 0.88f, 0.95f, 1f);

            _barBg = MakeTex(new Color(0.08f, 0.08f, 0.10f, 0.85f));
            _barFg = MakeTex(Color.white);
            _barBorder = MakeTex(new Color(0f, 0f, 0f, 0.9f));
        }

        private static Texture2D MakeTex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
