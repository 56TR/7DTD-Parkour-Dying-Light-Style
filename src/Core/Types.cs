namespace ParkourMovement.Core
{
    /// <summary>Moves the game may switch off (settings) or lock (Parkour perk tier). The motor checks Blocked.</summary>
    [System.Flags]
    public enum PkFeature : uint
    {
        None = 0,
        Slide = 1 << 0,
        LowSlide = 1 << 1,       // slide / crawl under a 1-block gap
        SlideJump = 1 << 2,
        LandingRoll = 1 << 3,
        Vault = 1 << 4,
        Mantle = 1 << 5,         // pull up onto an edge, also climbing out of a hang
        Hang = 1 << 6,
        WallClimb = 1 << 7,
        WallRun = 1 << 8,
        WallJump = 1 << 9,
        Tackle = 1 << 10,
        Dropkick = 1 << 11,
        Stomp = 1 << 12,
        VaultEnemy = 1 << 13,
    }

    /// <summary>Extras on top of the moves (perk rewards).</summary>
    [System.Flags]
    public enum PkBonus : uint
    {
        None = 0,
        AutoRoll = 1 << 0,       // landing roll without pressing crouch
        Efficient = 1 << 1,      // parkour moves cost less stamina
        HangFree = 1 << 2,       // hanging drains no stamina
    }

    public enum PkState
    {
        /// <summary>Vanilla controller drives (ground walking / idle). Air momentum still handled by the motor.</summary>
        None,
        Slide,
        WallRun,
        WallClimb,
        Mantle,
        Hang,
        Roll,
        VaultEnemy,
    }

    public enum StrikeKind
    {
        None,
        Tackle,     // slide into an enemy
        Dropkick,   // fly into an enemy with momentum
        Stomp,      // land on an enemy's head
    }

    /// <summary>A living enemy near the player, positions in the same space as BodyState.Feet.</summary>
    public struct EnemyInfo
    {
        public int Id;
        public V3 Feet;
        public float Height;
        public float Radius;
        public bool Down;   // knocked down / ragdolled / crawling on the ground
    }

    public struct Hit
    {
        public V3 Point;
        public V3 Normal;
        public float Distance;
    }

    /// <summary>World queries. Implemented with UnityEngine.Physics in game, with boxes in tests.</summary>
    public interface IWorldProbe
    {
        bool Ray(V3 origin, V3 dir, float maxDist, out Hit hit);

        /// <summary>True if an upright capsule standing with its feet at <paramref name="feet"/> overlaps nothing.</summary>
        bool CapsuleFree(V3 feet, float radius, float height);

        /// <summary>Like CapsuleFree but only world geometry counts (an enemy is not a ceiling).</summary>
        bool HeadroomFree(V3 feet, float radius, float height);

        /// <summary>Sweep an upright capsule (feet at origin) along dir. True if something is hit.</summary>
        bool CapsuleCast(V3 feet, float radius, float height, V3 dir, float maxDist, out Hit hit);

        /// <summary>Fill buffer with living enemies within radius of center; returns the count.</summary>
        int Enemies(V3 center, float radius, EnemyInfo[] buffer);
    }

    /// <summary>Player input for one physics tick. "Pressed" flags are edges latched since the previous tick.</summary>
    public struct PkInput
    {
        public float MoveX;      // strafe -1..1
        public float MoveY;      // forward -1..1
        public bool Sprint;
        public bool JumpHeld;
        public bool JumpPressed;
        public bool CrouchHeld;
        public bool CrouchPressed;
    }

    /// <summary>Everything the motor needs to know about the body this tick.</summary>
    public struct BodyState
    {
        public float Time;
        public float Dt;
        public V3 Feet;            // world position of capsule bottom
        public V3 Velocity;        // measured, m/s
        public bool Grounded;
        public V3 GroundNormal;
        public V3 LookFlat;        // horizontal unit forward of the body
        public V3 Look;            // camera forward incl. pitch
        public float Radius;
        public float Height;       // standing height
        public float CrouchHeight;
        public float Gravity;      // positive m/s^2 the vanilla controller applies
        public float Stamina;
        public float SpeedScale;   // mobility / encumbrance multiplier, 1 = normal
        public bool CanParkour;    // false while swimming, on ladder, driving, flying, dead, attached
        public bool VanillaJumpActive;
        public bool LegsInjured;   // sprained / broken leg: no slide, wall moves, vaults or kicks
        public PkFeature Blocked;  // switched off in the settings or not unlocked yet (0 = everything allowed)
        public PkBonus Bonus;
    }

    /// <summary>What the game layer must apply this tick.</summary>
    public struct PkOutput
    {
        public bool SetHorizontal;
        public V3 Horizontal;      // m/s, Y ignored
        public bool SetVertical;
        public float Vertical;     // m/s

        public bool BlockVanillaJump;
        public float StaminaCost;
        public bool NoStaminaRegen;
        public float CamYawTurn;      // degrees to turn the view this tick (going round a corner while hanging)   // hanging: vanilla regen must not refill what the hold drains

        // camera targets (smoothed by the camera layer)
        public float CamRoll;        // degrees, + = tilt right
        public float CamFovAdd;      // degrees
        public float CamHeight;      // metres added to eye height
        public float CamPitchSpin;   // degrees, absolute pitch offset (landing roll)
        public float CamPitch;       // degrees, + = look down (move "animations": mantle, climb, kicks)

        // combat
        public StrikeKind Strike;
        public int StrikeTarget;        // first target; all of them are in StrikeTargets[0..StrikeCount)
        public int[] StrikeTargets;
        public int StrikeCount;
        public float KnockSpeed;        // horizontal speed the targets are thrown away with (ragdoll), 0 = none
        public float KnockLift;
        public V3 StrikeDir;
        public int StrikeDamage;
        public float StrikeStun;     // knockdown seconds, 0 = damage only
        public bool ResetFallHeight; // a bounce: fall damage counts from here
        public bool PassThrough;     // ignore collisions with PassThroughTarget this tick
        public int PassThroughTarget;

        // collider
        public float ColliderHeight; // > 0: force this CharacterController height (low slide)

        // first-person hands (IK targets in world space, weight 0..1)
        public HandTarget HandL, HandR;
    }

    public struct HandTarget
    {
        public V3 Point;
        public float Weight;
    }
}
