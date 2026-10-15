using System;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Safe access to the game's manager singletons.
    ///
    /// ManagerBase&lt;T&gt;.Instance does not return null when a manager does not exist
    /// yet: it creates a brand new GameObject with that component and DontDestroyOnLoad
    /// (ManagerBase.cs:11-15). Calling it before the scene has set the game up leaves a
    /// junk manager behind whose fields are all null. So every lookup in this mod goes
    /// through FindObjectOfType first and only touches .Instance afterwards.
    /// </summary>
    internal static class GameRefs
    {
        private static GameManager _game;
        private static DataHub _data;
        private static CurrentDataHub _current;
        private static TextManager _text;
        private static ResourcesHub _resources;

        internal static GameManager Game { get { return Resolve(ref _game); } }
        internal static DataHub Data { get { return Resolve(ref _data); } }
        internal static CurrentDataHub Current { get { return Resolve(ref _current); } }
        internal static TextManager Text { get { return Resolve(ref _text); } }
        internal static ResourcesHub Resources { get { return Resolve(ref _resources); } }

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
                RareShopPlugin.Log.LogWarning("FindObjectOfType<" + typeof(T).Name + "> failed: " + ex.Message);
                cache = null;
            }
            return cache;
        }

        /// <summary>True once the game has created the data managers (i.e. a save is loaded).</summary>
        internal static bool IsGameDataReady()
        {
            return Data != null && Current != null;
        }

        /// <summary>
        /// True when the player's save data is actually loaded. The managers exist
        /// before a save is opened, but recordDataDic (which holds GP) is empty until
        /// then, so purchases must wait for this.
        /// </summary>
        internal static bool IsSaveLoaded()
        {
            if (!IsGameDataReady())
            {
                return false;
            }
            try
            {
                DataHub data = Data;
                if (data.recordDataDic == null || !data.recordDataDic.ContainsKey(0))
                {
                    return false;
                }
                CurrentDataHub cur = Current;
                if (cur.myBag == null || cur.myBag.Count == 0)
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void Invalidate()
        {
            _game = null;
            _data = null;
            _current = null;
            _text = null;
            _resources = null;
        }

        /// <summary>
        /// Finds the font the running game uses. The mod builds its UI from code, so it
        /// cannot reference the serialized font asset; borrowing a live Text component's
        /// font is the only reliable way to get the embedded CJK-capable font (LanaPixel).
        /// </summary>
        internal static Font FindUiFont()
        {
            try
            {
                Text[] texts = UnityEngine.Object.FindObjectsOfType<Text>();
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].font != null)
                    {
                        return texts[i].font;
                    }
                }
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogWarning("FindUiFont (live Text) failed: " + ex.Message);
            }

            try
            {
                UnityEngine.Object[] fonts = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(Font));
                for (int i = 0; i < fonts.Length; i++)
                {
                    Font f = fonts[i] as Font;
                    if (f != null)
                    {
                        return f;
                    }
                }
            }
            catch (Exception ex)
            {
                RareShopPlugin.Log.LogWarning("FindUiFont (all fonts) failed: " + ex.Message);
            }
            return null;
        }
    }
}
