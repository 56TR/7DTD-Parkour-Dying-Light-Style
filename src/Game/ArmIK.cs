using System.Collections.Generic;
using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>
    /// Procedural first-person hand placement: two-bone IK on the baseRigFP arm bones (Arm -> ForeArm -> Hand),
    /// run after the animator so the hands grab ledges / walls the motor points at. The held item is hidden meanwhile.
    /// </summary>
    public static class ArmIK
    {
        sealed class Chain
        {
            public Transform Upper, Lower, Hand;
            public float Weight;
            public Vector3 Target;
        }

        static Animator _anim;
        static Chain _l, _r;
        static readonly List<Renderer> _hidden = new List<Renderer>();
        public static float WeightL => _l?.Weight ?? 0f;
        public static float WeightR => _r?.Weight ?? 0f;
        public static bool Bound => _l != null && _r != null;

        public static void Apply(EntityPlayerLocal p, in PkOutput o, float dt)
        {
            if (p == null || !p.bFirstPersonView || !Bind(p))
            {
                ShowHeld(true);
                return;
            }
            // two-handed holds put the weapon away; in a slide / wall run the right hand keeps what it holds
            PkState s = PkRuntime.Motor.State;
            bool twoHanded = s == PkState.Hang || s == PkState.Mantle || s == PkState.WallClimb || s == PkState.VaultEnemy;
            bool bare = p.inventory == null || p.inventory.holdingItem == p.inventory.bareHandItem;
            HandTarget right = o.HandR;
            if (!twoHanded && !bare) right.Weight = 0f;
            Step(_l, o.HandL, dt);
            Step(_r, right, dt);
            if (twoHanded && (_l.Weight > 0.25f || _r.Weight > 0.25f)) HideHeld(p); else ShowHeld(true);
            if (_l.Weight > 0.001f) { Solve(_l); OpenFingers(_l.Hand, _l.Weight); }
            if (_r.Weight > 0.001f) { Solve(_r); OpenFingers(_r.Hand, _r.Weight); }
        }

        static readonly Dictionary<Transform, Transform[]> _fingerCache = new Dictionary<Transform, Transform[]>();

        /// <summary>Blend the finger bones toward the skeleton's rest pose (flat open hand) instead of the idle fist.</summary>
        public static void OpenFingers(Transform hand, float weight)
        {
            if (hand == null || !BodyAnimLibrary.Clips.TryGetValue("open_hands", out PkClipData c)) return;
            if (!_fingerCache.TryGetValue(hand, out Transform[] map))
            {
                map = new Transform[c.bones.Length];
                var all = new Dictionary<string, Transform>();
                foreach (Transform t in hand.GetComponentsInChildren<Transform>(true))
                    if (!all.ContainsKey(t.name)) all[t.name] = t;
                for (int k = 0; k < c.bones.Length; k++) all.TryGetValue(c.bones[k], out map[k]);
                _fingerCache[hand] = map;
            }
            for (int k = 0; k < map.Length; k++)
            {
                if (map[k] == null) continue;
                var q = new Quaternion(c.rot[k * 4], c.rot[k * 4 + 1], c.rot[k * 4 + 2], c.rot[k * 4 + 3]);
                map[k].localRotation = Quaternion.Slerp(map[k].localRotation, q, weight);
            }
        }

        static void Step(Chain c, HandTarget h, float dt)
        {
            if (h.Weight > 0.001f) c.Target = PkRuntime.ToU(h.Point);
            c.Weight = Mathf.MoveTowards(c.Weight, Mathf.Clamp01(h.Weight), dt / 0.12f);
        }

        static bool Bind(EntityPlayerLocal p)
        {
            Animator a = (p.emodel?.avatarController as AvatarLocalPlayerController)?.FPSArms?.Animator;
            if (a == null) return false;
            if (a == _anim) return _l != null && _r != null;
            _anim = a;
            _l = Find(a.transform, "Left");
            _r = Find(a.transform, "Right");
            if (_l == null || _r == null) Log.Warning("[Parkour] arm IK: bones not found on " + a.name);
            return _l != null && _r != null;
        }

        static Chain Find(Transform root, string side)
        {
            Transform up = Deep(root, side + "Arm"), lo = Deep(root, side + "ForeArm"), hand = Deep(root, side + "Hand");
            return up != null && lo != null && hand != null ? new Chain { Upper = up, Lower = lo, Hand = hand } : null;
        }

        static Transform Deep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int k = 0; k < t.childCount; k++)
            {
                Transform f = Deep(t.GetChild(k), name);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Analytic two-bone IK (law of cosines), keeps the current elbow plane, blended by weight in local space.</summary>
        static void Solve(Chain c) => SolveTwoBone(c.Upper, c.Lower, c.Hand, c.Target, c.Weight);

        /// <summary>Two-bone IK on any upper / lower / end chain (first- or third-person arms).</summary>
        public static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target, float weight)
        {
            var c = new Chain { Upper = upper, Lower = lower, Hand = end, Target = target, Weight = weight };
            Quaternion upper0 = c.Upper.localRotation, lower0 = c.Lower.localRotation;
            Vector3 a = c.Upper.position, b = c.Lower.position, h = c.Hand.position, t = c.Target;
            float lab = (b - a).magnitude, lbh = (h - b).magnitude;
            if (lab < 1e-4f || lbh < 1e-4f) return;
            float lat = Mathf.Clamp((t - a).magnitude, 0.01f, lab + lbh - 0.005f);

            float ah_ab0 = Angle(h - a, b - a);
            float ba_bh0 = Angle(a - b, h - b);
            float ah_at0 = Angle(h - a, t - a);
            float ah_ab1 = Mathf.Acos(Mathf.Clamp((lbh * lbh - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float ba_bh1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lbh * lbh) / (-2f * lab * lbh), -1f, 1f));

            Vector3 axis0 = Vector3.Cross(h - a, b - a);
            if (axis0.sqrMagnitude < 1e-8f) axis0 = Vector3.Cross(h - a, Vector3.down);
            axis0.Normalize();
            Vector3 axis1 = Vector3.Cross(h - a, t - a);

            c.Lower.rotation = Quaternion.AngleAxis((ba_bh1 - ba_bh0) * Mathf.Rad2Deg, axis0) * c.Lower.rotation;
            Quaternion r0 = Quaternion.AngleAxis((ah_ab1 - ah_ab0) * Mathf.Rad2Deg, axis0);
            Quaternion r2 = axis1.sqrMagnitude > 1e-8f ? Quaternion.AngleAxis(ah_at0 * Mathf.Rad2Deg, axis1.normalized) : Quaternion.identity;
            c.Upper.rotation = r2 * r0 * c.Upper.rotation;

            if (c.Weight < 0.999f)
            {
                Quaternion upper1 = c.Upper.localRotation, lower1 = c.Lower.localRotation;
                c.Upper.localRotation = Quaternion.Slerp(upper0, upper1, c.Weight);
                c.Lower.localRotation = Quaternion.Slerp(lower0, lower1, c.Weight);
            }
        }

        static float Angle(Vector3 x, Vector3 y) =>
            Mathf.Acos(Mathf.Clamp(Vector3.Dot(x.normalized, y.normalized), -1f, 1f));

        static void HideHeld(EntityPlayerLocal p)
        {
            if (_hidden.Count > 0) return;
            Transform held = p.inventory?.GetHoldingItemTransform();
            if (held == null) return;
            foreach (Renderer r in held.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; _hidden.Add(r); }
        }

        static void ShowHeld(bool _)
        {
            if (_hidden.Count == 0) return;
            foreach (Renderer r in _hidden)
                if (r != null) r.enabled = true;
            _hidden.Clear();
        }

        /// <summary>Distance of each hand from its target (for tests), -1 when inactive.</summary>
        public static float ErrorL => _l != null && _l.Weight > 0.99f ? (_l.Hand.position - _l.Target).magnitude : -1f;
        public static float ErrorR => _r != null && _r.Weight > 0.99f ? (_r.Hand.position - _r.Target).magnitude : -1f;
    }
}
