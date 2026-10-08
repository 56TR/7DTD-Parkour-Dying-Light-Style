using System;
using System.Collections.Generic;
using ParkourMovement.Core;

namespace ParkourMovement.Tests
{
    public struct Box
    {
        public V3 Min, Max;
        public Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            Min = new V3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1));
            Max = new V3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1));
        }
        public bool Contains(V3 p) =>
            p.X > Min.X && p.X < Max.X && p.Y > Min.Y && p.Y < Max.Y && p.Z > Min.Z && p.Z < Max.Z;
    }

    /// <summary>Axis-aligned box world. Rays starting inside a box ignore it (like Unity mesh colliders).</summary>
    public sealed class BoxWorld : IWorldProbe
    {
        public readonly List<Box> Boxes = new List<Box>();

        public BoxWorld Add(Box b) { Boxes.Add(b); return this; }

        public bool Ray(V3 o, V3 d, float maxDist, out Hit hit)
        {
            hit = default;
            float best = maxDist;
            bool found = false;
            foreach (var b in Boxes)
            {
                if (b.Contains(o)) continue;
                if (!Slab(o, d, b, out float t, out V3 n) || t > best || t < 0f) continue;
                best = t;
                found = true;
                hit = new Hit { Point = o + d * t, Normal = n, Distance = t };
            }
            return found;
        }

        static bool Slab(V3 o, V3 d, Box b, out float tNear, out V3 normal)
        {
            tNear = float.NegativeInfinity;
            float tFar = float.PositiveInfinity;
            normal = V3.Zero;
            float[] oo = { o.X, o.Y, o.Z }, dd = { d.X, d.Y, d.Z }, mn = { b.Min.X, b.Min.Y, b.Min.Z }, mx = { b.Max.X, b.Max.Y, b.Max.Z };
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(dd[a]) < 1e-8f)
                {
                    if (oo[a] < mn[a] || oo[a] > mx[a]) return false;
                    continue;
                }
                float t1 = (mn[a] - oo[a]) / dd[a], t2 = (mx[a] - oo[a]) / dd[a];
                float sign = -1f;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; sign = 1f; }
                if (t1 > tNear)
                {
                    tNear = t1;
                    normal = new V3(a == 0 ? sign : 0f, a == 1 ? sign : 0f, a == 2 ? sign : 0f);
                }
                if (t2 < tFar) tFar = t2;
                if (tNear > tFar) return false;
            }
            return tFar >= 0f;
        }

        public bool CapsuleFree(V3 feet, float r, float h) => !Overlaps(feet, r, h);

        public bool HeadroomFree(V3 feet, float r, float h) => !Overlaps(feet, r, h);

        public bool Overlaps(V3 feet, float r, float h)
        {
            const float e = 0.001f;
            foreach (var b in Boxes)
                if (feet.X + r > b.Min.X + e && feet.X - r < b.Max.X - e &&
                    feet.Y + h > b.Min.Y + e && feet.Y < b.Max.Y - e &&
                    feet.Z + r > b.Min.Z + e && feet.Z - r < b.Max.Z - e) return true;
            return false;
        }

        public readonly List<EnemyInfo> EnemyList = new List<EnemyInfo>();

        public int Enemies(V3 center, float radius, EnemyInfo[] buffer)
        {
            int n = 0;
            foreach (var e in EnemyList)
                if ((e.Feet - center).Length <= radius + e.Height && n < buffer.Length) buffer[n++] = e;
            return n;
        }

        public bool CapsuleCast(V3 feet, float r, float h, V3 dir, float maxDist, out Hit hit)
        {
            hit = default;
            for (float t = 0f; t <= maxDist; t += 0.02f)
                if (Overlaps(feet + dir * t, r, h)) { hit = new Hit { Point = feet + dir * t, Distance = t, Normal = -dir }; return true; }
            return false;
        }
    }

    /// <summary>Crude stand-in for vp_FPController: run/walk speeds, vanilla gravity and jump, AABB collisions.</summary>
    public sealed class BoxSim
    {
        public const float Dt = 0.02f, Gravity = 11.77f, R = 0.3f, H = 1.8f, CrouchH = 1.26f;
        public const float RunSpeed = 6.07f, WalkSpeed = 2.1f, VanillaJump = 6.0f;

        public readonly BoxWorld World;
        public readonly Tuning T = new Tuning();
        public readonly ParkourMotor Motor;
        public V3 Pos, Vel, Measured;
        public V3 LookFlat = new V3(0f, 0f, 1f);
        public bool Grounded;
        public float Time;
        public float Stamina = 100f;
        public float LastLandingScale = -1f;
        public float LastLandingSpeed;
        public PkOutput LastOut;
        public readonly List<string> Events = new List<string>();
        bool _vanillaJump;

        public BoxSim(BoxWorld w, V3 start)
        {
            World = w;
            Motor = new ParkourMotor(T);
            Pos = start;
            Grounded = Probe(Pos + new V3(0f, -0.01f, 0f));
        }

        public bool LegsInjured;
        public PkFeature Blocked;
        public PkBonus Bonus;
        public float BodyH = H;   // current collider height (crouch / low slide)
        public readonly List<PkOutput> Strikes = new List<PkOutput>();

        bool Probe(V3 feet) => World.Overlaps(feet, R, BodyH);

        /// <summary>Like the game: after leaving the ground the controller can still report grounded for a tick or two.</summary>
        public int GroundedLag;
        int _lagLeft;

        public void Step(PkInput i)
        {
            var b = new BodyState
            {
                Time = Time, Dt = Dt, Feet = Pos, Velocity = Measured, Grounded = Grounded || _lagLeft > 0, GroundNormal = V3.Up,
                LookFlat = LookFlat, Look = LookFlat, Radius = R, Height = H, CrouchHeight = CrouchH, Gravity = Gravity,
                Stamina = Stamina, SpeedScale = 1f, CanParkour = true, VanillaJumpActive = _vanillaJump, LegsInjured = LegsInjured,
                Blocked = Blocked, Bonus = Bonus,
            };
            string before = Motor.LastEvent;
            var o = Motor.Tick(b, i, World);
            LastOut = o;
            if (o.Strike != StrikeKind.None) Strikes.Add(o);
            // collider like the game: forced low slide, else crouch, else standing (only if there is room)
            float want = o.ColliderHeight > 0f ? o.ColliderHeight : (i.CrouchHeld ? CrouchH : H);
            if (want <= BodyH || !World.Overlaps(Pos, R, want)) BodyH = want;
            else if (BodyH < CrouchH && !World.Overlaps(Pos, R, CrouchH)) BodyH = CrouchH;
            if (Motor.LastEvent != before || (Motor.LastEvent != null && Events.Count == 0)) Events.Add(Motor.LastEvent);
            Stamina -= o.StaminaCost;

            // vanilla-ish motor
            V3 right = V3.Cross(V3.Up, LookFlat);
            V3 wish = LookFlat * i.MoveY + right * i.MoveX;
            if (wish.SqrLength > 1f) wish = wish.Normalized;
            float spd = i.Sprint && i.MoveY > 0f ? RunSpeed : WalkSpeed;
            if (i.CrouchHeld) spd *= 0.4f;
            V3 hv = Vel.Flat;
            if (Grounded) hv = hv + (wish * spd - hv) * 0.3f;
            else if (_lagLeft > 0) hv = wish * spd;   // the game's controller still thinks it runs on the ground: run speed
            if (Grounded && Vel.Y <= 0f) Vel.Y = -0.5f;
            else Vel.Y -= Gravity * Dt;
            if (Grounded) _vanillaJump = false;
            if (i.JumpHeld && Grounded && !o.BlockVanillaJump) { Vel.Y = VanillaJump; _vanillaJump = true; }
            if (o.SetHorizontal) hv = o.Horizontal.Flat;
            if (o.SetVertical) Vel.Y = o.Vertical;
            Vel = new V3(hv.X, Vel.Y, hv.Z);

            V3 prev = Pos;
            bool wasGrounded = Grounded;
            float fallVy = Vel.Y;
            MoveAxis(1, Vel.Y * Dt, out bool hitY);
            MoveAxis(0, Vel.X * Dt, out bool hitX);
            MoveAxis(2, Vel.Z * Dt, out bool hitZ);
            if (hitY) Vel.Y = 0f;
            if (hitX) Vel.X = 0f;
            if (hitZ) Vel.Z = 0f;
            Grounded = Probe(Pos + new V3(0f, -0.02f, 0f));
            if (Grounded) _lagLeft = 0;
            else if (wasGrounded) _lagLeft = GroundedLag;
            else if (_lagLeft > 0) _lagLeft--;
            Measured = (Pos - prev) / Dt;
            Time += Dt;
            if (!wasGrounded && Grounded)
            {
                LastLandingSpeed = -fallVy;
                LastLandingScale = Motor.OnLanding(i.CrouchHeld);
            }
        }

        void MoveAxis(int axis, float d, out bool hit)
        {
            hit = false;
            if (d == 0f) return;
            V3 delta = axis == 0 ? new V3(d, 0, 0) : axis == 1 ? new V3(0, d, 0) : new V3(0, 0, d);
            V3 np = Pos + delta;
            if (!Probe(np)) { Pos = np; return; }
            hit = true;
            // binary search the free fraction
            float lo = 0f, hi = 1f;
            for (int k = 0; k < 12; k++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Probe(Pos + delta * mid)) hi = mid; else lo = mid;
            }
            Pos = Pos + delta * lo;
        }

        public void Run(int ticks, PkInput i)
        {
            for (int k = 0; k < ticks; k++) Step(i);
        }

        /// <summary>Hold the given input; jump/crouch "pressed" only on the first tick.</summary>
        public void Hold(float seconds, PkInput i)
        {
            int n = (int)Math.Round(seconds / Dt);
            for (int k = 0; k < n; k++)
            {
                var j = i;
                if (k > 0) { j.JumpPressed = false; j.CrouchPressed = false; }
                Step(j);
            }
        }

        public static PkInput Sprint(bool jump = false, bool crouch = false) => new PkInput
        {
            MoveY = 1f, Sprint = true, JumpHeld = jump, JumpPressed = jump, CrouchHeld = crouch, CrouchPressed = crouch,
        };
    }
}
