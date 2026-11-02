using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SmoothFrames
{
    // Points the FindObjectsOfType / FindObjectsOfTypeAll calls of every mod in BepInEx\plugins at FindCache; inside those
    // same methods, what Instantiate / AddComponent create goes to FindCache.Track. Works within a time budget per frame;
    // methods that save or load are left alone.
    internal class FindCachePatcher
    {
        private static readonly MethodInfo FindT = Generic(typeof(Object), "FindObjectsOfType", 0);
        private static readonly MethodInfo FindTBool = Generic(typeof(Object), "FindObjectsOfType", 1);
        private static readonly MethodInfo AllT = Generic(typeof(Resources), "FindObjectsOfTypeAll", 0);
        private static readonly MethodInfo FindType = typeof(Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        private static readonly MethodInfo AllType = typeof(Resources).GetMethod("FindObjectsOfTypeAll", new[] { typeof(Type) });

        private static readonly MethodInfo OurFindT = Generic(typeof(FindCache), "Find", 0);
        private static readonly MethodInfo OurFindTBool = Generic(typeof(FindCache), "Find", 1);
        private static readonly MethodInfo OurAllT = Generic(typeof(FindCache), "FindAll", 0);
        private static readonly MethodInfo OurFindType = typeof(FindCache).GetMethod("Find", new[] { typeof(Type) });
        private static readonly MethodInfo OurAllType = typeof(FindCache).GetMethod("FindAll", new[] { typeof(Type) });
        private static readonly MethodInfo OurTrack = typeof(FindCache).GetMethod("Track");

        // types whose names contain these save or load the game: their searches stay as they were
        private static readonly string[] SkippedTypes = { "Persistence", "Save", "Load" };
        // loader and patching libraries are never touched
        private static readonly string[] SkippedAssemblies = { "BepInEx", "0Harmony", "HarmonyX", "MonoMod", "Mono.Cecil" };

        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                              BindingFlags.Static | BindingFlags.DeclaredOnly;

        private readonly Harmony _harmony = new Harmony(SmoothFramesPlugin.GUID + ".findcache");
        private readonly HashSet<string> _excluded;
        private readonly Dictionary<Module, Dictionary<int, int>> _tokens = new Dictionary<Module, Dictionary<int, int>>();
        private readonly List<MethodBase> _searchers = new List<MethodBase>();
        private readonly HashSet<MethodBase> _seen = new HashSet<MethodBase>();
        private bool _listed;
        private readonly HashSet<Assembly> _mods = new HashSet<Assembly>();
        private IEnumerator<MethodBase> _methods;
        private int _next, _patched, _failed;
        private long _workTicks;

        public bool Done { get; private set; }

        public FindCachePatcher(string excluded)
        {
            _excluded = new HashSet<string>(excluded.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static MethodInfo Generic(Type owner, string name, int parameters)
        {
            return owner.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == parameters);
        }

        // Runs for at most budgetTicks; call once per frame until Done.
        public void Step(long budgetTicks)
        {
            long start = Stopwatch.GetTimestamp();
            // the list of methods is read once; afterwards _methods stays null and only patching goes on
            if (_methods == null && !_listed) { _methods = Methods().GetEnumerator(); _listed = true; }

            // 1) find the methods that search (cheap look at the raw IL). Methods that only create objects are left
            //    alone: there are thousands of them and altering each costs milliseconds.
            while (_methods != null && Stopwatch.GetTimestamp() - start < budgetTicks)
            {
                if (!_methods.MoveNext()) { _methods = null; break; }
                var m = _methods.Current;
                if ((Kinds(m) & 1) != 0 && _seen.Add(m)) _searchers.Add(m);
            }

            // 2) alter them
            while (_methods == null && _next < _searchers.Count && Stopwatch.GetTimestamp() - start < budgetTicks)
                Patch(_searchers[_next++]);

            _workTicks += Stopwatch.GetTimestamp() - start;
            if (_methods == null && _next >= _searchers.Count && !Done)
            {
                Done = true;
                SmoothFramesPlugin.Log.LogInfo("Find cache ready: " + _patched + " methods patched in " + _mods.Count + " mods (" +
                    (_workTicks * 1000.0 / Stopwatch.Frequency).ToString("0") + " ms" + (_failed > 0 ? ", " + _failed + " failed" : "") + ").");
            }
        }

        private void Patch(MethodBase m)
        {
            try
            {
                bool searches = false;
                foreach (var ins in PatchProcessor.ReadMethodBody(m))
                {
                    var mi = ins.Value as MethodInfo;
                    if (mi != null && Replacement(mi) != null) { searches = true; break; }
                }
                if (!searches) return;
                _harmony.Patch(m, transpiler: new HarmonyMethod(typeof(FindCachePatcher).GetMethod(nameof(Transpiler),
                    BindingFlags.NonPublic | BindingFlags.Static)));
                _patched++;
                _mods.Add(m.DeclaringType.Assembly);
            }
            catch (Exception e)
            {
                if (_failed++ < 5) SmoothFramesPlugin.Log.LogWarning("Find cache: could not patch " + m.DeclaringType + "." + m.Name + ": " + e.Message);
            }
        }

        // Every method of every mod in the plugins folder, except ours, the loader's and the excluded ones.
        private IEnumerable<MethodBase> Methods()
        {
            string plugins = Path.GetFullPath(Paths.PluginPath);
            var own = typeof(FindCache).Assembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == own || asm.IsDynamic) continue;
                string location;
                try { location = asm.Location; } catch { continue; }
                if (string.IsNullOrEmpty(location) || !Path.GetFullPath(location).StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) continue;
                string name = asm.GetName().Name, file = Path.GetFileNameWithoutExtension(location);
                if (_excluded.Contains(name) || _excluded.Contains(file)) continue;
                if (SkippedAssemblies.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null || type.ContainsGenericParameters || Skipped(type)) continue;
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(Declared); } catch { continue; }
                    foreach (var m in methods)
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        if ((m.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) continue;
                        yield return m;
                    }
                }
            }
        }

        private static bool Skipped(Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
                foreach (var s in SkippedTypes)
                    if (t.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 1 = calls a search, 2 = creates objects. Reads only the raw IL bytes: after each call/callvirt byte comes a
        // method token, resolved once per module. A byte that only looks like a call is harmless: Patch reads the
        // method properly before changing anything.
        private int Kinds(MethodBase m)
        {
            byte[] il;
            try
            {
                var body = m.GetMethodBody();
                if (body == null) return 0;
                il = body.GetILAsByteArray();
            }
            catch { return 0; }
            if (il == null) return 0;
            Dictionary<int, int> known;
            if (!_tokens.TryGetValue(m.Module, out known)) _tokens[m.Module] = known = new Dictionary<int, int>();
            int kinds = 0;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;     // call, callvirt
                int token = BitConverter.ToInt32(il, i + 1);
                int table = (token >> 24) & 0xFF;
                if (table != 0x06 && table != 0x0A && table != 0x2B) continue;   // MethodDef, MemberRef, MethodSpec
                int kind;
                if (!known.TryGetValue(token, out kind)) known[token] = kind = Kind(m.Module, token);
                kinds |= kind;
            }
            return kinds;
        }

        private static int Kind(Module module, int token)
        {
            MethodBase target;
            try { target = module.ResolveMethod(token); }
            catch { return 0; }
            var mi = target as MethodInfo;
            if (mi == null) return 0;
            if (Replacement(mi) != null) return 1;
            return Creates(mi) ? 2 : 0;
        }

        private static MethodInfo Replacement(MethodInfo mi)
        {
            if (mi.IsGenericMethod)
            {
                Type[] args = mi.GetGenericArguments();
                if (args.Any(a => a.ContainsGenericParameters)) return null;
                MethodInfo def = mi.GetGenericMethodDefinition();
                if (def == FindT) return OurFindT.MakeGenericMethod(args);
                if (def == FindTBool) return OurFindTBool.MakeGenericMethod(args);
                if (def == AllT) return OurAllT.MakeGenericMethod(args);
                return null;
            }
            if (mi == FindType) return OurFindType;
            if (mi == AllType) return OurAllType;
            return null;
        }

        private static bool Creates(MethodInfo mi)
        {
            if (mi.ReturnType == typeof(void) || mi.ReturnType.IsValueType) return false;
            return (mi.DeclaringType == typeof(Object) && mi.Name == "Instantiate") ||
                   (mi.DeclaringType == typeof(GameObject) && mi.Name == "AddComponent");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                var mi = ins.operand as MethodInfo;
                if (mi != null && (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt))
                {
                    MethodInfo ours = Replacement(mi);
                    if (ours != null)
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = ours;
                        yield return ins;
                        continue;
                    }
                    if (Creates(mi))
                    {
                        // a copy of the created object goes to Track; the original stays on the stack
                        yield return ins;
                        yield return new CodeInstruction(OpCodes.Dup);
                        yield return new CodeInstruction(OpCodes.Call, OurTrack);
                        continue;
                    }
                }
                yield return ins;
            }
        }
    }
}
