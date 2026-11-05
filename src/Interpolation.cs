using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<int, bool> Wheeled = new Dictionary<int, bool>();
        private static readonly List<Component> Comps = new List<Component>();
        private static readonly List<Joint> Joints = new List<Joint>();
        private static readonly Collider[] Overlap = new Collider[512];
        private static readonly HashSet<int> Seen = new HashSet<int>();

        public static void ClearCache() { Wheeled.Clear(); }

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
        public static void FindTrailers(Rigidbody vehicle, List<Rigidbody> chain)
        {
            chain.Clear();
            var puller = vehicle;
            while (chain.Count < MaxTrailers)
            {
                var next = FindHitched(puller, vehicle.transform, chain);
                if (next == null || next == vehicle || chain.Contains(next)) break;
                chain.Add(next);
                puller = next;
            }
        }

        // The body hitched to the puller that is not the vehicle or a trailer already found.
        private static Rigidbody FindHitched(Rigidbody puller, Transform vehicleRoot, List<Rigidbody> chain)
        {
            var root = puller.transform;
            puller.GetComponentsInChildren(false, Joints);
            for (int i = 0; i < Joints.Count; i++)
            {
                var other = Joints[i] != null ? Joints[i].connectedBody : null;
                if (other != null && !InChain(other.transform, vehicleRoot, chain)) { Joints.Clear(); return TopBody(other); }
            }
            Joints.Clear();

            Rigidbody found = null;
            int n = Physics.OverlapSphereNonAlloc(puller.worldCenterOfMass, TowRange, Overlap, ~0, QueryTriggerInteraction.Ignore);
            Seen.Clear();
            for (int i = 0; i < n; i++)
            {
                var col = Overlap[i];
                Overlap[i] = null;
                if (found != null) continue;
                var body = col != null ? col.attachedRigidbody : null;
                if (body == null || !Seen.Add(body.GetInstanceID()) || InChain(body.transform, vehicleRoot, chain)) continue;
                body.GetComponents(Joints);
                for (int k = 0; k < Joints.Count; k++)
                {
                    var held = Joints[k] != null ? Joints[k].connectedBody : null;
                    if (held != null && held.transform.IsChildOf(root)) { found = TopBody(body); break; }
                }
                Joints.Clear();
            }
            return found;
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
