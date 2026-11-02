// Smooth Frames - BepInEx 5 plugin for Apocalypter
// Removes the stutter caused by mods that search the whole scene (FindObjectsOfType) on timers, by sharing one
// short-lived answer between them, and turns on rigidbody interpolation for the player, the vehicle and its trailer.

using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyTitle("Smooth Frames")]

namespace SmoothFrames
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class SmoothFramesPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.gelatti.apocalypter.smoothframes";
        public const string NAME = "Smooth Frames";
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Log;

        internal ConfigEntry<bool> InterpEnabled, InterpVehicle, InterpTrailer;
        internal ConfigEntry<bool> FixFindCache;
        internal ConfigEntry<float> FindCacheSeconds;
        internal ConfigEntry<string> FindCacheExclude;

        // seconds between looks for the player, vehicle and trailer
        private const float RefreshInterval = 1f;
        // time per frame spent altering the other mods at the start, and how long the cache waits after a scene load
        private const float PatchBudgetMs = 8f, PatchBudgetInGameMs = 1f, FindCacheLoadPause = 20f;

        private readonly Interpolation _interp = new Interpolation();
        private FindCachePatcher _patcher;
        private int _lastFrame = -1;
        private float _nextRefresh;
        private int _errors;

        private void Awake()
        {
            Log = Logger;
            try
            {
                Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu.");

                FixFindCache = Config.Bind("Fixes", "FindCache", true,
                    "Mods that search the whole scene (FindObjectsOfType, ~10 ms per search in a loaded world) share one search, " +
                    "reused for a few seconds. Applies to every mod in BepInEx\\plugins. Turning it off takes effect at once; " +
                    "turning it back on needs a game restart if the game was started with it off.");
                FindCacheSeconds = Config.Bind("Fixes", "FindCacheRefresh", 4f,
                    new ConfigDescription("Seconds between one scene-wide script search and the next. Searches that include templates and " +
                        "disabled objects (FindObjectsOfTypeAll) last three times as long.", new AcceptableValueRange<float>(0.2f, 10f)));
                FindCacheExclude = Config.Bind("Fixes", "FindCacheExclude", "",
                    "DLL names (without .dll, comma separated) the cache must leave alone, if a mod misbehaves. Takes effect after a game restart.");

                InterpEnabled = Config.Bind("Interpolation", "Enabled", true,
                    "Sets RigidbodyInterpolation.Interpolate on the player's rigidbody. The original value comes back when turned off.");
                InterpVehicle = Config.Bind("Interpolation", "IncludeVehicle", true, "Also interpolates the vehicle the player is driving.");
                InterpTrailer = Config.Bind("Interpolation", "IncludeTrailer", true, "Also interpolates the trailer hitched to that vehicle.");
                EventHandler refresh = (s, e) => _nextRefresh = 0f;
                InterpEnabled.SettingChanged += refresh;
                InterpVehicle.SettingChanged += refresh;
                InterpTrailer.SettingChanged += refresh;

                FindCache.Enabled = FixFindCache.Value;
                FindCache.Seconds = FindCacheSeconds.Value;
                if (FixFindCache.Value) _patcher = new FindCachePatcher(FindCacheExclude.Value);

                // the game removes the plugin's own object, so the per-frame work runs from this callback instead of Update
                Application.onBeforeRender += OnBeforeRender;
                SceneManager.sceneLoaded += OnSceneLoaded;
                Log.LogInfo(NAME + " " + VERSION + " loaded.");
            }
            catch (Exception e) { Log.LogError("Awake: " + e); }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                _nextRefresh = 0f;
                PlayerFinder.ClearCache();
                FindCache.Clear();
                FindCache.PausedUntil = Time.realtimeSinceStartup + FindCacheLoadPause;
            }
            catch (Exception e) { Log.LogError("sceneLoaded: " + e); }
        }

        // Once per frame, just before rendering.
        private void OnBeforeRender()
        {
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            try
            {
                FindCache.Enabled = FixFindCache.Value;
                FindCache.Seconds = FindCacheSeconds.Value;
                if (_patcher != null && !_patcher.Done)
                {
                    // full budget only in the main menu; whatever is left after loading a save finishes slowly
                    float budget = SceneManager.GetActiveScene().name == "MainMenu" ? PatchBudgetMs : PatchBudgetInGameMs;
                    _patcher.Step((long)(budget * Stopwatch.Frequency / 1000.0));
                }
                float t = Time.realtimeSinceStartup;
                if (t >= _nextRefresh)
                {
                    _nextRefresh = t + RefreshInterval;
                    _interp.Refresh(InterpEnabled.Value, InterpVehicle.Value, InterpTrailer.Value);
                }
            }
            catch (Exception e)
            {
                if (_errors++ < 10) Log.LogError("Frame: " + e);
            }
        }
    }
}
