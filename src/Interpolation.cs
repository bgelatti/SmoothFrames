using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SmoothFrames
{
    // Turns on interpolation on the player's bodies and gives each one back its original value when it is
    // no longer wanted (switched off, other body, scene changed).
    internal class Interpolation
    {
        private readonly Dictionary<Rigidbody, RigidbodyInterpolation> _original = new Dictionary<Rigidbody, RigidbodyInterpolation>();
        private readonly List<Rigidbody> _wanted = new List<Rigidbody>(12);
        private readonly List<Rigidbody> _drop = new List<Rigidbody>(12);
        private readonly List<Rigidbody> _towed = new List<Rigidbody>(8);

        public void Refresh(bool on, bool vehicle, bool trailer)
        {
            Rigidbody player = null, car = null;
            _towed.Clear();
            if (on)
            {
                PlayerFinder.Find(Camera.main, out player, out car);
                if (trailer && car != null) PlayerFinder.FindTrailers(car, _towed);
            }

            _wanted.Clear();
            Want(player);
            if (vehicle) Want(car);
            for (int i = 0; i < _towed.Count; i++) Want(_towed[i]);

            _drop.Clear();
            foreach (var kv in _original)
                if (kv.Key == null || !_wanted.Contains(kv.Key)) _drop.Add(kv.Key);
            foreach (var rb in _drop)
            {
                if (rb != null) rb.interpolation = _original[rb];
                _original.Remove(rb);
            }

            foreach (var rb in _wanted)
            {
                if (!_original.ContainsKey(rb)) _original[rb] = rb.interpolation;
                if (rb.interpolation != RigidbodyInterpolation.Interpolate) rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        private void Want(Rigidbody rb)
        {
            if (rb != null && !_wanted.Contains(rb)) _wanted.Add(rb);
        }
    }

    // Finds the player's bodies by walking up from the main camera.
    internal static class PlayerFinder
    {
        private const float TowRange = 15f;
        private const int MaxTrailers = 8;
        // layer of the trailers' couplers, the one the hitches' sphere casts look on
        private const int TrailerLayer = 20;
        private static readonly Dictionary<int, bool> Wheeled = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> Trailers = new Dictionary<int, bool>();
        private static readonly List<Component> Comps = new List<Component>();
        private static readonly List<Transform> Kids = new List<Transform>();
        private static readonly List<Joint> Joints = new List<Joint>();
        private static readonly Collider[] Overlap = new Collider[512];
        private static readonly HashSet<int> Seen = new HashSet<int>();

        // [Debug] LogTrailers: the chain is written to the log when it changes, with what was turned down after its last link
        internal static bool LogTrailers;
        private static readonly StringBuilder Diag = new StringBuilder();
        private static string _lastChain;

        public static void ClearCache() { Wheeled.Clear(); Trailers.Clear(); _lastChain = null; }

        // Above the camera: the lowest body without wheels is the player, the topmost wheeled one the vehicle.
        // Without a body above the camera, the object tagged "Player" is used.
        public static void Find(Camera cam, out Rigidbody player, out Rigidbody vehicle)
        {
            player = null; vehicle = null;
            if (cam == null) return;
            bool controller = false;
            for (var x = cam.transform; x != null; x = x.parent)
            {
                var rb = x.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    if (IsWheeled(rb)) vehicle = rb;
                    else if (player == null && vehicle == null) player = rb;
                }
                if (!controller) controller = x.GetComponent<CharacterController>() != null;
            }
            if (player == null && !controller)
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null)
                {
                    player = go.GetComponentInChildren<Rigidbody>();
                    if (player != null && IsWheeled(player)) player = null;
                }
            }
            if (player != null && vehicle == null)
            {
                for (var x = player.transform.parent; x != null; x = x.parent)
                {
                    var rb = x.GetComponent<Rigidbody>();
                    if (rb != null && IsWheeled(rb)) vehicle = rb;
                }
            }
        }

        // A body with a WheelCollider or an NWH WheelController under it.
        public static bool IsWheeled(Rigidbody rb)
        {
            int id = rb.GetInstanceID();
            bool w;
            if (Wheeled.TryGetValue(id, out w)) return w;
            rb.GetComponentsInChildren(true, Comps);
            for (int i = 0; i < Comps.Count && !w; i++)
            {
                var c = Comps[i];
                if (c == null) continue;
                string n = c.GetType().Name;
                w = n == "WheelCollider" || n.IndexOf("WheelController", StringComparison.Ordinal) >= 0;
            }
            Comps.Clear();
            Wheeled[id] = w;
            return w;
        }

        // The trailers: the body at the other end of a hitch joint on the vehicle (or on a nearby body), then the
        // body hitched to that trailer, and so on, so trailer trains are followed to the end.
        // Only bodies with a coupler (a child named "attacher", which the game's hitches and Burner's Utilities' need) count
        // as trailers, so cargo nailed or locked into a trailer is not taken for the next one. The first trailer falls back
        // to any hitched body, as in 1.1.0, in case a modded trailer has no coupler of that name.
        public static void FindTrailers(Rigidbody vehicle, List<Rigidbody> chain)
        {
            chain.Clear();
            var puller = vehicle;
            while (chain.Count < MaxTrailers)
            {
                var next = FindHitched(puller, vehicle.transform, chain, true);
                if (next == null && chain.Count == 0) next = FindHitched(puller, vehicle.transform, chain, false);
                if (next == null || next == vehicle || chain.Contains(next)) break;
                chain.Add(next);
                puller = next;
            }
            if (LogTrailers) LogChain(vehicle, chain);
        }

        // The body hitched to the puller that is not the vehicle or a trailer already found.
        private static Rigidbody FindHitched(Rigidbody puller, Transform vehicleRoot, List<Rigidbody> chain, bool strict)
        {
            if (LogTrailers && strict) Diag.Length = 0;
            var root = puller.transform;
            Rigidbody found = null;

            // a joint on the puller holding the trailer
            puller.GetComponentsInChildren(false, Joints);
            for (int i = 0; i < Joints.Count && found == null; i++)
            {
                var other = Joints[i] != null ? Joints[i].connectedBody : null;
                if (other == null || InChain(other.transform, vehicleRoot, chain)) continue;
                var top = TopBody(other);
                if (!strict || IsTrailer(top)) found = top;
                else if (LogTrailers) Note(top, Joints[i]);
            }
            Joints.Clear();
            if (found != null) return found;

            // a joint on a nearby trailer holding the puller: couplers' layer first, then everything
            found = Scan(puller, vehicleRoot, chain, strict, 1 << TrailerLayer, QueryTriggerInteraction.Collide);
            if (found == null) found = Scan(puller, vehicleRoot, chain, strict, ~0, QueryTriggerInteraction.Ignore);
            return found;
        }

        private static Rigidbody Scan(Rigidbody puller, Transform vehicleRoot, List<Rigidbody> chain, bool strict, int mask, QueryTriggerInteraction triggers)
        {
            var root = puller.transform;
            Rigidbody found = null;
            int n = Physics.OverlapSphereNonAlloc(puller.worldCenterOfMass, TowRange, Overlap, mask, triggers);
            Seen.Clear();
            for (int i = 0; i < n; i++)
            {
                var col = Overlap[i];
                Overlap[i] = null;
                if (found != null) continue;
                var body = col != null ? col.attachedRigidbody : null;
                if (body == null) continue;
                if (strict)
                {
                    // the whole trailer, since the hitch joint can sit on a body under it
                    var top = TopBody(body);
                    if (!Seen.Add(top.GetInstanceID()) || InChain(top.transform, vehicleRoot, chain)) continue;
                    bool trailer = IsTrailer(top);
                    if (!trailer && !LogTrailers) continue;
                    top.GetComponentsInChildren(false, Joints);
                    var j = HoldsPuller(root);
                    if (j != null)
                    {
                        if (trailer) found = top;
                        else Note(top, j);
                    }
                }
                else
                {
                    if (!Seen.Add(body.GetInstanceID()) || InChain(body.transform, vehicleRoot, chain)) continue;
                    body.GetComponents(Joints);
                    if (HoldsPuller(root) != null) found = TopBody(body);
                }
                Joints.Clear();
            }
            return found;
        }

        // The joint in Joints whose other end is part of the puller.
        private static Joint HoldsPuller(Transform root)
        {
            for (int k = 0; k < Joints.Count; k++)
            {
                var held = Joints[k] != null ? Joints[k].connectedBody : null;
                if (held != null && held.transform.IsChildOf(root)) return Joints[k];
            }
            return null;
        }

        // A body with a child named "attacher", the coupler a hitch locks onto.
        public static bool IsTrailer(Rigidbody rb)
        {
            int id = rb.GetInstanceID();
            bool t;
            if (Trailers.TryGetValue(id, out t)) return t;
            rb.GetComponentsInChildren(true, Kids);
            for (int i = 0; i < Kids.Count && !t; i++)
                t = Kids[i] != null && Kids[i].name == "attacher";
            Kids.Clear();
            Trailers[id] = t;
            return t;
        }

        private static void Note(Rigidbody body, Joint joint)
        {
            if (Diag.Length > 1500) return;
            Diag.Append("\n  turned down ").Append(body.name).Append(" (").Append(joint.GetType().Name).Append(" on ")
                .Append(joint.name).Append(" -> ").Append(joint.connectedBody != null ? joint.connectedBody.name : "none")
                .Append(", no \"attacher\" child)");
        }

        private static void LogChain(Rigidbody vehicle, List<Rigidbody> chain)
        {
            var sb = new StringBuilder("Trailers: ").Append(vehicle.name);
            for (int i = 0; i < chain.Count; i++) sb.Append(" -> ").Append(chain[i] != null ? chain[i].name : "?");
            string line = sb.ToString();
            if (line == _lastChain) return;
            _lastChain = line;
            SmoothFramesPlugin.Log.LogInfo(line + Diag);
        }

        // Part of the vehicle or of a trailer already in the chain.
        private static bool InChain(Transform t, Transform vehicleRoot, List<Rigidbody> chain)
        {
            if (t.IsChildOf(vehicleRoot)) return true;
            for (int i = 0; i < chain.Count; i++)
                if (chain[i] != null && t.IsChildOf(chain[i].transform)) return true;
            return false;
        }

        private static Rigidbody TopBody(Rigidbody rb)
        {
            var top = rb;
            for (var x = rb.transform.parent; x != null; x = x.parent)
            {
                var r = x.GetComponent<Rigidbody>();
                if (r != null) top = r;
            }
            return top;
        }
    }
}
