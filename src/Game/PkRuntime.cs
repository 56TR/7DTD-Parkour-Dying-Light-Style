using System;
using System.IO;
using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>Per-process state shared by the patches: tuning, motor, latched input, camera targets, trace.</summary>
    public static class PkRuntime
    {
        public static readonly Tuning Tuning = new Tuning();
        public static readonly ParkourMotor Motor = new ParkourMotor(Tuning);
        public static string ModPath;
        public static bool Verbose;

        // last tick, for camera / console / tests
        public static PkOutput Out;
        public static BodyState Body;
        public static bool BlockJump;
        public static bool InfiniteStamina;
        public static float PendingYaw;       // view turn requested by the motor (hanging round a corner), applied by the camera   // console "pk stamina": testing aid, stamina refilled every physics tick
        public static string BlockReason;
        public static int TickCount;

        // trace ring
        public struct TraceRow
        {
            public float Time;
            public PkState State;
            public Vector3 Feet;
            public Vector3 Vel;
            public bool Grounded;
        }
        public static readonly TraceRow[] Trace = new TraceRow[512];
        public static int TraceHead;

        static Vector3 _lastFeet;
        static bool _hasLast;
        static Vector3 _lastVel;
        static float _staminaCap;
        static bool _staminaCapActive;

        public static string ConfigFile => Path.Combine(ModPath ?? ".", "Config", "parkour.xml");

        public static string LoadConfig()
        {
            try
            {
                string f = ConfigFile;
                if (!File.Exists(f)) return "no config file, using defaults";
                string err = Tuning.LoadXml(File.ReadAllText(f));
                return err.Length == 0 ? "config loaded" : "config loaded with problems: " + err;
            }
            catch (Exception ex)
            {
                return "config error: " + ex.Message;
            }
        }

        public static bool IsLocalController(vp_FPController c)
        {
            return c != null && c.localPlayer != null && c.enabled;
        }

        /// <summary>Runs at the start of vp_FPController.FixedMove (after vanilla motor/jump/forces were computed).</summary>
        public static void Tick(vp_FPController c)
        {
            EntityPlayerLocal p = c.localPlayer;
            TickCount++;
            BodyAnimDriver.AttachLocal(p);
            float dt = Time.deltaTime;
            if (dt <= 0f || Time.timeScale <= 0f) return;

            Vector3 feet = c.Transform.position;
            Vector3 raw = _hasLast ? (feet - _lastFeet) / dt : Vector3.zero;
            if (raw.sqrMagnitude > 60f * 60f) raw = _lastVel; // teleport / origin shift
            // per-tick displacement is noisy (vp mixes fixed and smoothed positions), smooth it a little
            Vector3 vel = _hasLast ? Vector3.Lerp(_lastVel, raw, 0.5f) : raw;
            _lastFeet = feet;
            _lastVel = vel;
            _hasLast = true;

            CharacterController cc = c.CharacterController;
            Vector3 fwd = c.Transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 look = p.cameraTransform != null ? p.cameraTransform.forward : fwd;

            float gUnits = -Physics.gravity.y * (c.PhysicsGravityModifier * 0.002f) * vp_TimeUtility.AdjustedTimeScale;
            float gravity = gUnits * 60f * Time.timeScale / dt;

            PerkIntegration.Tick(p);
            string why = !PkSettings.Enabled ? "disabled-in-options" : !p.Spawned ? "not-spawned" : p.IsDead() ? "dead" : p.isSwimming ? "swimming" : p.isLadderAttached ? "ladder"
                       : p.IsFlyMode.Value ? "flymode" : p.AttachedToEntity != null ? "attached" : c.MotorFreeFly ? "freefly"
                       : c.Player.Driving.Active ? "driving" : p.IsStuck ? "stuck" : null;
            BlockReason = why;
            bool can = why == null;

            var b = new BodyState
            {
                Time = Time.time,
                Dt = dt,
                Feet = ToV(feet),
                Velocity = ToV(vel),
                Grounded = c.Grounded,
                GroundNormal = c.Grounded ? ToV(c.GroundNormal) : V3.Up,
                LookFlat = ToV(fwd),
                Look = ToV(look),
                Radius = cc.radius,
                Height = c.m_NormalHeight > 0.1f ? c.m_NormalHeight : cc.height,
                CrouchHeight = c.m_CrouchHeight > 0.1f ? c.m_CrouchHeight : cc.height * 0.7f,
                Gravity = gravity,
                Stamina = p.Stamina,
                SpeedScale = Mathf.Clamp(EffectManager.GetValue(PassiveEffects.Mobility, null, 1f, p), 0.2f, 1.5f),
                CanParkour = can,
                VanillaJumpActive = c.Player.Jump.Active,
                LegsInjured = LegsInjured(p),
                Blocked = PerkIntegration.Blocked,
                Bonus = PerkIntegration.Bonus,
            };
            PkInput input = PkInputLatch.Consume();
            int evBefore = Motor.EventCount;
            PkOutput o = Motor.Tick(b, input, UnityProbe.For(c));
            Body = b;
            Out = o;
            BlockJump = o.BlockVanillaJump;

            if (Verbose && Motor.EventCount != evBefore)
                Log.Out($"[Parkour] {Motor.LastEvent} state={Motor.State} feet={feet} v={vel.magnitude:0.0}");

            float conv = 1f / (60f * Time.timeScale);
            if (o.SetHorizontal)
            {
                float sm = Mathf.Max(c.m_SpeedModifier, 0.05f);
                c.m_MotorThrottle.x = o.Horizontal.X * conv / sm;
                c.m_MotorThrottle.z = o.Horizontal.Z * conv / sm;
                c.m_ExternalForce.x = 0f;
                c.m_ExternalForce.z = 0f;
            }
            if (o.SetVertical)
            {
                c.m_MotorJumpForceAcc = 0f;
                c.m_ExternalForce.y = 0f;
                if (o.Vertical > 0f && c.Grounded)
                {
                    // grounded FixedMove pulls down unless the throttle points up (anti-bump)
                    c.m_MotorThrottle.y = o.Vertical * conv;
                    c.m_FallSpeed = 0f;
                }
                else
                {
                    c.m_MotorThrottle.y = 0f;
                    c.m_FallSpeed = o.Vertical * conv;
                }
            }
            PendingYaw += o.CamYawTurn;
            if (o.NoStaminaRegen && !InfiniteStamina)
            {
                // vanilla regenerates between ticks; while hanging, stamina may only go down
                if (_staminaCapActive && p.Stamina > _staminaCap) p.Stamina = _staminaCap;
                _staminaCapActive = true;
            }
            else _staminaCapActive = false;
            if (o.StaminaCost > 0f) p.Stamina = Mathf.Max(0f, p.Stamina - o.StaminaCost);
            _staminaCap = p.Stamina;
            if (InfiniteStamina) p.Stats.Stamina.Value = p.Stats.Stamina.ModifiedMax;
            if (o.ResetFallHeight)
            {
                // a bounce off an enemy: fall damage counts from the new apex, not from where we jumped
                c.m_MaxHeight = float.MinValue;
                c.m_MaxHeightInitialFallSpeed = 0f;
            }
            Combat.Apply(p, o);
            Combat.UpdatePassThrough(c, o);
            if (o.ColliderHeight > 0f)
            {
                LowColliderHeight = o.ColliderHeight;
                ApplyLowCollider(c, o.ColliderHeight);
            }
            else if (LowColliderHeight > 0f)
            {
                LowColliderHeight = 0f;
                c.RefreshColliders();   // back to vanilla crouch / stand
            }

            TraceHead = (TraceHead + 1) % Trace.Length;
            Trace[TraceHead] = new TraceRow { Time = Time.time, State = Motor.State, Feet = feet, Vel = vel, Grounded = c.Grounded };
        }

        public static float LowColliderHeight;

        public static void ApplyLowCollider(vp_FPController c, float h)
        {
            CharacterController cc = c.CharacterController;
            if (Mathf.Abs(cc.height - h) < 0.001f) return;
            cc.height = h;
            cc.center = new Vector3(0f, h * 0.5f, 0f);
        }

        static readonly string[] InjuryBuffs = { "buffLegSprained", "buffLegBroken" };

        /// <summary>Sprained / broken leg, or anything that zeroes jumping (knockdown, stun): no leg-powered parkour.</summary>
        public static bool LegsInjured(EntityPlayerLocal p)
        {
            if (p.Buffs != null)
                foreach (string b in InjuryBuffs)
                    if (p.Buffs.HasBuff(b)) return true;
            return EffectManager.GetValue(PassiveEffects.JumpStrength, null, 1f, p) <= 0.001f;
        }

        public static void ResetTracking()
        {
            _hasLast = false;
            Motor.Reset();
        }

        public static V3 ToV(Vector3 v) => new V3(v.x, v.y, v.z);
        public static Vector3 ToU(V3 v) => new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>Held state + rising edges between physics ticks. Fed from EntityPlayerLocal.MoveByInput.</summary>
    public static class PkInputLatch
    {
        static PkInput _held;
        static bool _jumpEdge, _crouchEdge, _prevJump, _prevCrouch;

        public static void Sample(float moveX, float moveY, bool sprint, bool jump, bool crouch)
        {
            if (jump && !_prevJump) _jumpEdge = true;
            if (crouch && !_prevCrouch) _crouchEdge = true;
            _prevJump = jump;
            _prevCrouch = crouch;
            _held = new PkInput { MoveX = moveX, MoveY = moveY, Sprint = sprint, JumpHeld = jump, CrouchHeld = crouch };
        }

        public static PkInput Consume()
        {
            PkInput i = _held;
            i.JumpPressed = _jumpEdge;
            i.CrouchPressed = _crouchEdge;
            _jumpEdge = _crouchEdge = false;
            return i;
        }

        public static bool CrouchHeld => _held.CrouchHeld;
    }

    /// <summary>Scripted input for the self test. When Active it replaces the player's movement input.</summary>
    public static class ScriptedInput
    {
        public static bool Active;
        public static float Forward, Strafe;
        public static bool Run, Jump, Crouch;

        public static void Clear()
        {
            Forward = Strafe = 0f;
            Run = Jump = Crouch = false;
        }
    }
}
