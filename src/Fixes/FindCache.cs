using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SmoothFrames
{
    // Shared, short-lived answers to FindObjectsOfType / FindObjectsOfTypeAll for the mods patched by FindCachePatcher.
    // A search for any script type walks every script in the scene (~10 ms in a loaded world), whatever the type, so
    // one sweep for MonoBehaviour answers all script types at once. Objects the patched methods create are added as
    // they are made (Track), so a mod finds what it has just created without a new search.
    // Callers always get their own array, without destroyed objects.
    internal static class FindCache
    {
        private sealed class Entry
        {
            public Type Type;
            public bool Scripts;    // a MonoBehaviour type: filled by the shared sweep
            public readonly List<Object> Items = new List<Object>();
            public float Expires;
        }

        private static readonly Dictionary<Type, Entry> Active = new Dictionary<Type, Entry>();
        private static readonly Dictionary<Type, Entry> All = new Dictionary<Type, Entry>();
        private static readonly List<Entry> ScriptEntries = new List<Entry>();
        // concrete type -> entries it belongs to (sweep: script entries; Track: every entry); rebuilt when entries are added
        private static readonly Dictionary<Type, Entry[]> SweepTargets = new Dictionary<Type, Entry[]>();
        private static readonly Dictionary<Type, Entry[]> TrackActive = new Dictionary<Type, Entry[]>();
        private static readonly Dictionary<Type, Entry[]> TrackAll = new Dictionary<Type, Entry[]>();
        private static readonly List<Component> TrackBuffer = new List<Component>();
        private static readonly List<Entry> Scratch = new List<Entry>();

        public static bool Enabled;
        public static float Seconds = 4f;
        // no caching until then (scene loads spawn and look for things all at once)
        public static float PausedUntil;

        private static int _lastSearchFrame = -1;

        public static void Clear()
        {
            Active.Clear();
            All.Clear();
            ScriptEntries.Clear();
            ForgetTargets();
        }

        private static bool Usable { get { return Enabled && Time.realtimeSinceStartup >= PausedUntil; } }

        // ---- replacements for the calls in the patched mods (same signatures as Unity's)

        public static T[] Find<T>() where T : Object
        {
            if (!Usable) return Object.FindObjectsOfType<T>();
            return Copy<T>(Get(Active, typeof(T)));
        }

        // objects that are switched off are not cached: the mods use that for saving and restoring
        public static T[] Find<T>(bool includeInactive) where T : Object
        {
            return includeInactive ? Object.FindObjectsOfType<T>(true) : Find<T>();
        }

        public static T[] FindAll<T>() where T : Object
        {
            if (!Usable) return Resources.FindObjectsOfTypeAll<T>();
            return Copy<T>(Get(All, typeof(T)));
        }

        public static Object[] Find(Type type)
        {
            if (!Usable) return Object.FindObjectsOfType(type);
            return Copy<Object>(Get(Active, type));
        }

        public static Object[] FindAll(Type type)
        {
            if (!Usable) return Resources.FindObjectsOfTypeAll(type);
            return Copy<Object>(Get(All, type));
        }

        // Called right after a patched method creates an object (Instantiate) or a component (AddComponent):
        // what it made goes straight into the cached answers.
        public static void Track(Object made)
        {
            if (!Enabled || made == null || (Active.Count == 0 && All.Count == 0)) return;
            var c = made as Component;
            var go = c != null ? c.gameObject : made as GameObject;
            if (go == null) return;
            if (c != null) { Add(c); return; }
            go.GetComponentsInChildren(true, TrackBuffer);
            for (int i = 0; i < TrackBuffer.Count; i++) Add(TrackBuffer[i]);
            TrackBuffer.Clear();
        }

        // ---- cache

        private static Entry Get(Dictionary<Type, Entry> map, Type type)
        {
            Entry e;
            bool known = map.TryGetValue(type, out e);
            float now = Time.realtimeSinceStartup;
            if (known)
            {
                if (now < e.Expires) return e;
                // at most one real search per frame; the rest wait for a later frame
                if (Time.frameCount == _lastSearchFrame) return e;
            }
            else
            {
                e = new Entry { Type = type, Scripts = map == Active && typeof(MonoBehaviour).IsAssignableFrom(type) };
                map[type] = e;
                if (e.Scripts) ScriptEntries.Add(e);
                ForgetTargets();
            }
            if (e.Scripts) Sweep(now);
            else Search(map, e, now);
            _lastSearchFrame = Time.frameCount;
            return e;
        }

        // One search for all scripts, shared out to every script type that has been asked for.
        private static void Sweep(float now)
        {
            MonoBehaviour[] scripts = Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var e in ScriptEntries) e.Items.Clear();
            for (int i = 0; i < scripts.Length; i++)
            {
                var s = scripts[i];
                if (s == null) continue;
                foreach (var e in Targets(SweepTargets, s.GetType(), ScriptEntries)) e.Items.Add(s);
            }
            float expires = now + Seconds;
            foreach (var e in ScriptEntries) e.Expires = expires;
        }

        private static void Search(Dictionary<Type, Entry> map, Entry e, float now)
        {
            Object[] found = map == Active ? Object.FindObjectsOfType(e.Type) : Resources.FindObjectsOfTypeAll(e.Type);
            e.Items.Clear();
            e.Items.AddRange(found);
            // FindObjectsOfTypeAll answers are mostly terrain, templates and assets, which change rarely
            e.Expires = now + (map == All ? Seconds * 3f : Seconds);
        }

        private static void Add(Component c)
        {
            if (c == null) return;
            Type t = c.GetType();
            bool active = c.gameObject.activeInHierarchy;
            if (active)
                foreach (var e in Targets(TrackActive, t, Active.Values)) e.Items.Add(c);
            foreach (var e in Targets(TrackAll, t, All.Values)) e.Items.Add(c);
            // GameObject answers get each object once, through its Transform
            if (c is Transform)
            {
                Entry e;
                if (active && Active.TryGetValue(typeof(GameObject), out e)) e.Items.Add(c.gameObject);
                if (All.TryGetValue(typeof(GameObject), out e)) e.Items.Add(c.gameObject);
            }
        }

        private static Entry[] Targets(Dictionary<Type, Entry[]> cache, Type concrete, IEnumerable<Entry> entries)
        {
            Entry[] list;
            if (cache.TryGetValue(concrete, out list)) return list;
            Scratch.Clear();
            foreach (var e in entries)
                if (e.Type != typeof(GameObject) && e.Type.IsAssignableFrom(concrete)) Scratch.Add(e);
            list = Scratch.ToArray();
            cache[concrete] = list;
            return list;
        }

        private static void ForgetTargets()
        {
            SweepTargets.Clear();
            TrackActive.Clear();
            TrackAll.Clear();
        }

        // The caller's own array, leaving out what was destroyed since (and dropping it from the cache).
        private static T[] Copy<T>(Entry e) where T : Object
        {
            var items = e.Items;
            int kept = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var o = items[i];
                if (o == null) continue;
                items[kept++] = o;
            }
            if (kept < items.Count) items.RemoveRange(kept, items.Count - kept);
            var result = new T[kept];
            for (int i = 0; i < kept; i++) result[i] = (T)items[i];
            return result;
        }
    }
}
