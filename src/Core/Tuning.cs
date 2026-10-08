using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Xml;

namespace ParkourMovement.Core
{
    /// <summary>
    /// All gameplay numbers. Speeds in m/s, times in s, heights in m above the feet.
    /// Overridable from Config/parkour.xml: &lt;property name="SlideFriction" value="4.5"/&gt;
    /// </summary>
    public sealed class Tuning
    {
        public bool Enabled = true;

        // generic
        public float JumpSpeed = 6.0f;              // used by motor-made jumps (coyote, slide jump); ~1.5 m with vanilla gravity
        public float CoyoteTime = 0.2f; // DL2 AmortizerJumpHelpTimer
        public float StuckTicks = 4;                // forced moves abort after this many ticks of no progress

        // air momentum
        public float AirControlAccel = 9f;          // m/s^2 steering while airborne
        public float AirDrag = 0.35f;               // fraction of speed lost per second above AirCruiseSpeed
        public float AirCruiseSpeed = 6.2f;         // speed reachable by air steering alone
        public float AirMaxSpeed = 16f;

        // slide
        public float SlideMinEnterSpeed = 4.6f;
        public float SlideBoost = 1.25f;            // entry speed multiplier
        public float SlideMinSpeed = 7.0f;          // entry speed floor
        public float SlideMaxSpeed = 14f;
        public float SlideFriction = 3.9f; // DL2 SlidingMovementDeceleration 3.0, scaled
        public float SlideSlopeAccel = 1.0f;        // multiplier of gravity along the slope
        public float SlideExitSpeed = 2.8f;
        public float SlideMaxTime = 1.25f; // only the crawl under a low gap: after this it stands up once there is room. A normal slide ends when its speed drops below SlideExitSpeed
        public float SlideSteerDeg = 45f;           // deg/s
        public float SlideCooldown = 0.35f;
        public float SlideStamina = 4f;
        public float SlideJumpBoost = 1.1f;         // horizontal carry multiplier
        public float JumpCarryKeep = 0.12f;         // s: slide / roll jump momentum survives the game still reporting grounded right after the push off
        public float SlideJumpUp = 0.8f;            // * JumpSpeed: ~1 m hop, 7DTD's own jump (1.5 m) is far higher than DL2's

        // wall run (side)
        public float WallRunMinSpeed = 5.5f; // DL2 WallRunSideMinSpeed 4.5 x1.3 (7DTD sprint 6.6 vs DL2 5.0)
        public float WallRunProbe = 0.55f;          // beyond capsule radius
        public float WallRunSpeed = 8.5f; // DL2 WallRunSideMaxSpeed 7.0, scaled
        public float WallRunMaxTime = 1.25f; // DL2 WallRunSideMaxDuration
        public float WallRunEntryUp = 1.5f;         // vertical speed floor at entry
        public float WallRunEntryMaxUp = 2.0f;      // jump momentum kept on the wall is capped: ~0.5 m arc, not a 2 m climb
        public float WallRunGravity = 0.35f;        // * body gravity
        public float WallRunMaxFallSpeed = 5f;      // won't attach when falling faster
        public float WallRunMaxAngle = 50f;         // velocity vs wall tangent
        public float WallRunStaminaPerSec = 6f;
        public float WallRunStaminaEnter = 3f;
        public float WallRunRoll = 13f;
        public float WallRunSameWallCooldown = 0.7f;

        // wall jump
        public float WallJumpOut = 7.0f; // DL2 WallJumpSideSpeed 7.0
        public float WallJumpUp = 4.65f;            // DL2 AdvancedParkourNormalJumpHeight 0.92 m
        public float WallJumpAlong = 0.8f;          // keep this fraction of along-wall speed
        public float WallJumpLook = 3.0f;           // extra push in look direction (only if looking off the wall)
        public float WallJumpStamina = 5f;
        public float WallJumpControlLock = 0.25f;  // no air steering right after the kick-off

        // wall climb (run up a wall you face)
        public float WallClimbSpeed = 6.5f;
        public float WallClimbTime = 0.55f;
        public float WallClimbMaxAngle = 35f;       // look vs wall normal
        public float WallClimbStamina = 6f;

        // ledges
        public float LedgeForward = 0.45f;          // probe this far past capsule radius
        public float MantleMinHeight = 0.55f;       // lower is handled by step-up / vault
        public float MantleMaxHeight = 1.65f;       // direct pull-up from air / climb
        public float HangMaxHeight = 2.45f;         // reachable by hands, hang first
        public float HangHeight = 1.72f;            // ledge top - feet while hanging (eyes at the edge, hands in view)
        public float MantleUpSpeed = 4.2f;          // ~0.5 s pull-up like DL
        public float MantleForwardSpeed = 2.8f;
        public float MantleMaxTime = 1.2f;
        public float MantleStamina = 3f;
        public float ClimbMinStamina = 10f;
        public float EfficientStaminaScale = 0.5f;  // Parkour perk tier 5: parkour moves cost this much stamina         // below this you are spent: no pull-up / climb up / wall climb (hanging still works)
        public float ShimmySpeed = 1.6f;
        public float HangStaminaPerSec = 1.5f;      // holding on drains slowly; at 0 you let go
        public float ShimmyStaminaPerSec = 3f;      // moving along the edge drains faster
        public float HangCornerTime = 0.6f;         // going round a block corner while hanging (s)
        public float HangClimbHold = 0.35f;         // W held this long while hanging climbs up (a fresh W press climbs at once)
        public float HangLookYaw = 80f;             // first person: how far (deg) you can look left/right off the wall while hanging / climbing
        public float HangBodyYaw = 25f;             // third person: how far the body may turn off the wall (more = arms twist over the head)

        // vault (sprinting into a low obstacle)
        public float VaultMinHeight = 0.45f;
        public float VaultMaxHeight = 1.35f;
        public float VaultMinSpeed = 4.0f;
        public float VaultUpSpeed = 6.5f;
        public float VaultStamina = 2f;

        // landing roll
        public float RollWindow = 0.35f; // DL2 ActiveLandingMaxInputTime 0.3 + one frame of slack
        public float RollMinFallSpeed = 7f;         // vertical m/s at landing to trigger a roll
        public float RollDamageScale = 0.25f;
        public float RollSpeed = 6.5f;
        public float RollTime = 0.55f;
        public float HardLandingFallSpeed = 18f; // ~14 m fall = DL2 AmortizedLethalHeight

        // combat (7DTD zombies have ~100-300 hp; bashing damage)
        public float StrikeCooldown = 0.8f;         // same enemy can't be hit again sooner
        public float TackleMinSpeed = 4.5f;
        public float TackleDamage = 15f;
        public float TackleStun = 2.0f;
        public float TackleSpeedKeep = 0.55f;       // slide speed kept after hitting someone
        public float TackleKnockSpeed = 4.3f;       // zombies hit by a slide fly back ~3 m (ragdoll velocity)
        public float TackleKnockLift = 2.6f;
        public float TackleWidth = 0.5f;            // extra half-width of the hit zone: a slide sweeps a group
        public float DropkickMinSpeed = 5.0f;
        public float DropkickDamage = 30f;
        public float DropkickStun = 2.5f;
        public float DropkickRecoil = 2.5f;         // player bounces back this fast
        public float DropkickKnockSpeed = 7.0f;
        public float DropkickKnockLift = 3.5f;
        public float DropkickStamina = 6f;
        public float StompMinFallSpeed = 2.5f;
        public float StompDamage = 25f;
        public float StompDamagePerMps = 4f;        // extra per m/s of fall speed
        public float StompStun = 2.0f;
        public float StompBounce = 6.0f;
        public float VaultEnemyRange = 2.4f;        // sprint + jump at an enemy this close: vault over it
        public float VaultEnemyMaxHeight = 2.4f;
        public float VaultEnemyClear = 0.25f;       // feet pass this far above its head
        public float VaultEnemyStamina = 4f;

        // low slide (fits under 1 block)
        public float SlideColliderHeight = 0.9f;
        public float CrawlSpeed = 1.6f;             // while stuck low under something

        // first-person body: show your legs / body during moves (slide, wall run, climb, hang, mantle, dropkick)
        public bool FpBody = true;

        // camera
        public float SprintFov = 5f;               // DL2 FovSprintModif 6.0
        public float SlideFov = 8f;
        public float SlideLookDown = 20f;           // degrees the view tilts down in a slide (legs and the bracing hand in view)
        public float SlideEyeDrop = -0.6f;          // camera drop in a slide: eye ~0.7 m over the ground, like DL
        public float SlideRoll = 4f;
        public float SpeedFovPerMps = 0.6f;         // above AirCruiseSpeed
        public float CamMaxFovAdd = 12f;

        public string LoadXml(string xml)
        {
            var errors = new List<string>();
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var nodes = doc.SelectNodes("//property");
            if (nodes == null) return "";
            foreach (XmlNode n in nodes)
            {
                string name = n.Attributes?["name"]?.Value;
                string value = n.Attributes?["value"]?.Value;
                if (name == null || value == null) continue;
                if (!Set(name, value)) errors.Add(name);
            }
            return errors.Count == 0 ? "" : "unknown/invalid: " + string.Join(", ", errors);
        }

        public bool Set(string name, string value)
        {
            FieldInfo f = typeof(Tuning).GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (f == null) return false;
            try
            {
                if (f.FieldType == typeof(float))
                    f.SetValue(this, float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(bool))
                    f.SetValue(this, bool.Parse(value));
                else return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string Dump()
        {
            var parts = new List<string>();
            foreach (FieldInfo f in typeof(Tuning).GetFields(BindingFlags.Public | BindingFlags.Instance))
                parts.Add(f.Name + "=" + Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture));
            return string.Join(" ", parts);
        }
    }
}
