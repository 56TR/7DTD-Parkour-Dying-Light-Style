using System;

namespace ParkourMovement.Core
{
    /// <summary>
    /// Parkour state machine. Engine-agnostic: reads a BodyState + PkInput + world probe each physics tick
    /// and returns velocity overrides and camera targets. One instance per local player.
    /// </summary>
    public sealed class ParkourMotor
    {
        public Tuning T;

        public PkState State { get; private set; } = PkState.None;
        public float StateTime => _now - _stateStart;
        string _lastEvent;
        /// <summary>Most recent transition name; EventCount increments on every assignment (even repeats).</summary>
        public string LastEvent
        {
            get => _lastEvent;
            private set
            {
                _lastEvent = value;
                EventCount++;
                History.Add(value);
                if (History.Count > 64) History.RemoveAt(0);
            }
        }
        public int EventCount { get; private set; }
        public readonly System.Collections.Generic.List<string> History = new System.Collections.Generic.List<string>();
        public V3 Carry => _carry;
        public bool CarryActive => _carryActive;
        public int Ticks { get; private set; }

        float _now, _stateStart;
        bool _wasGrounded = true;
        float _lastGroundedTime;
        bool _jumpedSinceGround;
        bool _climbUsed;
        float _slideCooldownUntil;
        float _rollArmedUntil;
        float _blockJumpUntil;
        float _airLockUntil;
        float _lastAirVy;
        bool _pendingRoll, _pendingSlide;
        V3 _lastWallNormal;
        float _lastWallTime = -10f;

        V3 _carry;
        bool _carryActive;

        // per-state
        V3 _dir, _wallN, _ledgeFwd, _ledgeFaceN;
        float _hangFwdHeld, _hangBackHeld;
        float _carryKeepUntil;
        PkFeature _blocked;
        PkBonus _bonus;

        bool On(PkFeature f) => (_blocked & f) == 0;
        bool Has(PkBonus x) => (_bonus & x) != 0;
        float Cost(float c) => Has(PkBonus.Efficient) ? c * T.EfficientStaminaScale : c;
        // going round a corner while hanging: two waypoints, the facing turns from _cornerFrom to _ledgeFwd
        bool _cornering;
        float _cornerT, _cornerYaw;
        V3 _cornerW1, _cornerW2, _cornerFrom;
        bool _hangFwdArmed;
        float _speed, _ledgeTop, _entrySpeed;
        int _side, _phase, _stuck;
        bool _vault;
        float _vaultGap, _vaultUp;
        V3 _phaseStart;
        float _ungroundedSince;
        bool _under;

        // combat
        readonly EnemyInfo[] _enemies = new EnemyInfo[24];
        readonly System.Collections.Generic.Dictionary<int, float> _struckUntil = new System.Collections.Generic.Dictionary<int, float>();
        int _veId;
        V3 _veFeet;
        float _veTop, _veClear;

        // camera animation
        float _mantleStartY;
        V3 _anchorL, _anchorR;   // where the hands grabbed (world), fixed while the body moves
        float _punchPitch, _punchRoll, _punchAt = -10f;
        const float PunchTime = 0.35f;

        public ParkourMotor(Tuning t) { T = t; }

        public void Reset()
        {
            SetState(PkState.None, null);
            _carryActive = false;
            _pendingRoll = _pendingSlide = false;
        }

        void SetState(PkState s, string evt)
        {
            if (State == PkState.Slide && s != PkState.Slide) _slideCooldownUntil = _now + T.SlideCooldown;
            State = s;
            _stateStart = _now;
            _stuck = 0;
            _phase = 0;
            if (evt != null) LastEvent = evt;
        }

        /// <summary>Called by the game when the controller touches ground. Returns fall damage multiplier.</summary>
        public float OnLanding(bool crouchHeld)
        {
            float fall = -_lastAirVy;
            if (State != PkState.None) return 1f;
            bool armed = _rollArmedUntil >= _now || Has(PkBonus.AutoRoll);
            if (On(PkFeature.LandingRoll) && armed && fall >= T.RollMinFallSpeed)
            {
                if (fall >= T.HardLandingFallSpeed) { LastEvent = "hard-landing"; return 1f; }
                _pendingRoll = true;
                return T.RollDamageScale;
            }
            if (crouchHeld && _carry.FlatLength >= T.SlideMinEnterSpeed) _pendingSlide = true;
            return 1f;
        }

        public PkOutput Tick(in BodyState b, in PkInput i, IWorldProbe w)
        {
            _blocked = b.Blocked;
            _bonus = b.Bonus;
            Ticks++;
            _now = b.Time;
            var o = new PkOutput();
            if (!T.Enabled || !b.CanParkour)
            {
                if (State != PkState.None || _carryActive) Reset();
                _wasGrounded = b.Grounded;
                _lastGroundedTime = _now;
                return o;
            }

            bool grounded = b.Grounded;
            if (grounded)
            {
                _lastGroundedTime = _now;
                _jumpedSinceGround = false;
                _climbUsed = false;
            }
            else
            {
                _lastAirVy = b.Velocity.Y;
                if (b.Velocity.Y > 2f || b.VanillaJumpActive) _jumpedSinceGround = true;
                if (i.CrouchPressed) _rollArmedUntil = _now + T.RollWindow;
            }

            // takeoff: remember momentum so it survives the vanilla air damping
            if (_wasGrounded && !grounded && State == PkState.None && _now >= _carryKeepUntil)
            {
                _carry = b.Velocity.Flat;
                _carryActive = true;
            }
            _wasGrounded = grounded;

            switch (State)
            {
                case PkState.None: TickNone(b, i, w, ref o); break;
                case PkState.Slide: TickSlide(b, i, w, ref o); break;
                case PkState.WallRun: TickWallRun(b, i, w, ref o); break;
                case PkState.WallClimb: TickWallClimb(b, i, w, ref o); break;
                case PkState.Mantle: TickMantle(b, i, w, ref o); break;
                case PkState.Hang: TickHang(b, i, w, ref o); break;
                case PkState.Roll: TickRoll(b, i, w, ref o); break;
                case PkState.VaultEnemy: TickVaultEnemy(b, i, w, ref o); break;
            }

            if (State != PkState.None || _now < _blockJumpUntil) o.BlockVanillaJump = true;

            if (_now - _punchAt < PunchTime)
            {
                float f = 1f - (_now - _punchAt) / PunchTime;
                f *= f;
                o.CamPitch += _punchPitch * f;
                o.CamRoll += _punchRoll * f;
            }

            float fs = (o.SetHorizontal ? o.Horizontal : b.Velocity).FlatLength;
            if (State == PkState.None && b.Grounded && i.Sprint && fs > 4.5f) o.CamFovAdd += T.SprintFov;
            float fov = Math.Max(0f, fs - T.AirCruiseSpeed) * T.SpeedFovPerMps;
            o.CamFovAdd = Math.Min(T.CamMaxFovAdd, o.CamFovAdd + fov);
            return o;
        }

        // ---------------------------------------------------------------- None (vanilla ground + air momentum)

        void TickNone(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            V3 inputDir = InputDir(b, i);
            float flat = b.Velocity.FlatLength;

            // a jump we launched (slide / roll jump): the game may still report grounded for a tick or two after
            // the push off. Keep the boosted momentum through that instead of falling back to vanilla run speed.
            if (b.Grounded && _carryActive && _now < _carryKeepUntil)
            {
                o.SetHorizontal = true;
                o.Horizontal = _carry;
                return;
            }
            if (b.Grounded)
            {
                _carryActive = false;
                if (_pendingRoll) { _pendingRoll = _pendingSlide = false; EnterRoll(b, ref o); return; }
                bool wantSlide = _pendingSlide || (i.CrouchPressed && flat >= T.SlideMinEnterSpeed);
                _pendingSlide = false;
                if (b.LegsInjured) return;
                if (wantSlide && On(PkFeature.Slide) && _now >= _slideCooldownUntil && Pay(b, T.SlideStamina, ref o))
                {
                    EnterSlide(b, Math.Max(flat, _carry.FlatLength), ref o);
                    return;
                }
                if (On(PkFeature.VaultEnemy) && i.JumpPressed && i.MoveY > 0f && flat >= 3.5f && EnemyAhead(b, w, b.LookFlat, T.VaultEnemyRange, out EnemyInfo ve)
                    && ve.Height <= T.VaultEnemyMaxHeight && Pay(b, T.VaultEnemyStamina, ref o))
                {
                    EnterVaultEnemy(b, ve, Math.Max(flat, 5.5f), ref o);
                    return;
                }
                if (On(PkFeature.WallClimb) && i.JumpPressed && i.MoveY > 0f && flat >= 2.5f && FacingWall(b, w, 0.6f, out Hit wall)
                    && PayClimb(b, T.WallClimbStamina, ref o))
                {
                    EnterWallClimb(b, wall, ref o);
                    return;
                }
                if (On(PkFeature.Vault) && i.Sprint && i.MoveY > 0f && flat >= T.VaultMinSpeed && TryVault(b, w, out float top)
                    && Pay(b, T.VaultStamina, ref o))
                {
                    EnterMantle(b, b.LookFlat, top, vault: true, entrySpeed: flat, ref o);
                    return;
                }
                return;
            }

            // ---- airborne
            if (!b.LegsInjured && TryAirStrike(b, w, ref o)) { }
            else if (!_jumpedSinceGround && !b.VanillaJumpActive && i.JumpPressed && _now - _lastGroundedTime <= T.CoyoteTime)
            {
                _jumpedSinceGround = true;
                o.SetVertical = true;
                o.Vertical = T.JumpSpeed;
                LastEvent = "coyote-jump";
            }
            else
            {
                if (i.MoveY > 0f && b.Velocity.Y < 3f && DetectLedge(b, w, b.LookFlat, out float top, out float h))
                {
                    if (h <= T.MantleMaxHeight && On(PkFeature.Mantle) && PayClimb(b, T.MantleStamina, ref o))
                    {
                        EnterMantle(b, b.LookFlat, top, vault: false, entrySpeed: 0f, ref o);
                        return;
                    }
                    // too tired to pull up: the hands still catch the edge (W / Jump won't climb until stamina is back)
                    if (On(PkFeature.Hang) && (h > T.MantleMaxHeight || ((Exhausted(b) || !On(PkFeature.Mantle)) && h >= 1.0f)) && b.Velocity.Y <= 0.5f)
                    {
                        EnterHang(b, b.LookFlat, top, ref o);
                        return;
                    }
                }
                if (!b.LegsInjured && On(PkFeature.WallClimb) && i.JumpHeld && i.MoveY > 0f && !_climbUsed && b.Velocity.Y > -3f && FacingWall(b, w, 0.6f, out Hit wall)
                    && PayClimb(b, T.WallClimbStamina, ref o))
                {
                    EnterWallClimb(b, wall, ref o);
                    return;
                }
                if (!b.LegsInjured && TryWallRun(b, i, w, ref o)) return;
            }

            // air momentum + steering
            if (!_carryActive) { _carry = b.Velocity.Flat; _carryActive = true; }
            // collisions: drop the part of the momentum that points into a wall right in front of us
            // (measured velocity is too noisy for this: vp_FPController mixes fixed and smoothed positions)
            if (_carry.FlatLength > 0.5f)
            {
                V3 dir = _carry.Flat.Normalized;
                for (int k = 0; k < 3; k++)
                {
                    V3 org = b.Feet + V3.Up * (0.3f + k * 0.6f);
                    if (!w.Ray(org, dir, b.Radius + 0.15f, out Hit hw) || Math.Abs(hw.Normal.Y) > 0.5f) continue;
                    V3 n = hw.Normal.Flat.Normalized;
                    float into = V3.Dot(_carry, n);
                    if (into < 0f) _carry = _carry - n * into;
                    break;
                }
            }

            float before = _carry.FlatLength;
            if (_now >= _airLockUntil) _carry = _carry + inputDir * (T.AirControlAccel * b.Dt);
            float after = _carry.FlatLength;
            float cap = Math.Max(before, T.AirCruiseSpeed * b.SpeedScale);
            if (after > cap) _carry = _carry * (cap / after);
            if (_carry.FlatLength > T.AirCruiseSpeed)
            {
                float excess = _carry.FlatLength - T.AirCruiseSpeed;
                float l = _carry.FlatLength;
                _carry = _carry * ((l - excess * T.AirDrag * b.Dt) / l);
            }
            if (_carry.FlatLength > T.AirMaxSpeed) _carry = _carry.Normalized * T.AirMaxSpeed;
            o.SetHorizontal = true;
            o.Horizontal = _carry;
        }

        // ---------------------------------------------------------------- slide

        void EnterSlide(in BodyState b, float speed, ref PkOutput o)
        {
            V3 d = (b.Velocity.FlatLength > 0.5f ? b.Velocity : _carry).Flat.Normalized;
            if (d.SqrLength < 0.5f) d = b.LookFlat;
            _dir = d;
            _speed = Clamp(Math.Max(speed * T.SlideBoost, T.SlideMinSpeed), 0f, T.SlideMaxSpeed);
            _ungroundedSince = -1f;
            _under = false;
            SetState(PkState.Slide, "slide");
            OutSlide(b, ref o);
        }

        void TickSlide(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (On(PkFeature.Tackle) && _speed >= T.TackleMinSpeed && EnemyAhead(b, w, _dir, 0.35f + _speed * b.Dt * 2f, out EnemyInfo e))
            {
                StrikeGroup(ref o, b, w, StrikeKind.Tackle, e, _dir, T.TackleDamage, T.TackleStun, T.TackleWidth, T.TackleKnockSpeed, T.TackleKnockLift);
                _speed *= T.TackleSpeedKeep;
            }
            bool roomToRise = w.HeadroomFree(b.Feet + V3.Up * 0.02f, b.Radius * 0.95f, b.CrouchHeight);
            if ((_under || !roomToRise) && !On(PkFeature.LowSlide))
            {
                // low slide switched off: a gap lower than crouch height just stops the slide
                _under = false;
                SetState(PkState.None, "slide-blocked");
                return;
            }
            if (_under || !roomToRise)
            {
                TickSlideUnder(b, i, roomToRise, ref o);
                return;
            }
            if (i.JumpPressed && !On(PkFeature.SlideJump))
            {
                SetState(PkState.None, "slide-end");   // a plain vanilla jump instead
                return;
            }
            if (i.JumpPressed)
            {
                _carry = _dir * (_speed * T.SlideJumpBoost);
                _carryActive = true;
                _carryKeepUntil = _now + T.JumpCarryKeep;
                _jumpedSinceGround = true;
                _blockJumpUntil = _now + 0.2f;
                SetState(PkState.None, "slide-jump");
                o.SetHorizontal = true;
                o.Horizontal = _carry;
                o.SetVertical = true;
                o.Vertical = T.JumpSpeed * T.SlideJumpUp;
                return;
            }
            if (!b.Grounded)
            {
                if (_ungroundedSince < 0f) _ungroundedSince = _now;
                if (_now - _ungroundedSince > 0.12f) { ExitToAir(_dir * _speed, "slide-off-edge"); return; }
            }
            else _ungroundedSince = -1f;

            if (!i.CrouchHeld || _speed < T.SlideExitSpeed || StateTime > T.SlideMaxTime)
            {
                SetState(PkState.None, "slide-end");
                o.SetHorizontal = true;
                o.Horizontal = _dir * Math.Min(_speed, T.AirCruiseSpeed);
                return;
            }
            if (Stuck(b.Velocity.FlatLength < _speed * 0.35f)) { SetState(PkState.None, "slide-blocked"); return; }

            // slope: gravity component along the ground in slide direction
            V3 n = b.GroundNormal.SqrLength > 0.5f ? b.GroundNormal : V3.Up;
            V3 down = new V3(0f, -1f, 0f);
            V3 along = down - n * V3.Dot(down, n);
            float slopeA = V3.Dot(along.Flat, _dir) * b.Gravity * T.SlideSlopeAccel;
            _speed += (slopeA - T.SlideFriction) * b.Dt;
            _speed = Math.Min(_speed, T.SlideMaxSpeed);
            V3 inDir = InputDir(b, i);
            if (inDir.SqrLength > 0.1f && V3.Dot(inDir, _dir) > -0.2f)
                _dir = V3.RotateFlatTowards(_dir, inDir, T.SlideSteerDeg * (float)(Math.PI / 180.0) * b.Dt);
            OutSlide(b, ref o);
        }

        /// <summary>Under something lower than crouch height: stay low and keep moving (crawl) until there is room.</summary>
        void TickSlideUnder(in BodyState b, in PkInput i, bool roomToRise, ref PkOutput o)
        {
            if (!_under && !roomToRise)
            {
                _under = true;
                LastEvent = "slide-under";
            }
            if (roomToRise && (!i.CrouchHeld || _speed < T.SlideExitSpeed || StateTime > T.SlideMaxTime))
            {
                _under = false;
                SetState(PkState.None, "crawl-out");
                o.SetHorizontal = true;
                o.Horizontal = _dir * Math.Min(_speed, T.AirCruiseSpeed);
                return;
            }
            if (roomToRise) _under = false;
            _speed = Math.Max(_speed - T.SlideFriction * b.Dt, T.CrawlSpeed);
            V3 inDir = InputDir(b, i);
            o.SetHorizontal = true;
            if (_speed <= T.CrawlSpeed + 0.01f)
            {
                // crawling: input decides, standing still is allowed
                o.Horizontal = inDir * T.CrawlSpeed;
                if (inDir.SqrLength > 0.1f) _dir = inDir.Flat.Normalized;
            }
            else
            {
                if (inDir.SqrLength > 0.1f && V3.Dot(inDir, _dir) > -0.2f)
                    _dir = V3.RotateFlatTowards(_dir, inDir, T.SlideSteerDeg * (float)(Math.PI / 180.0) * b.Dt);
                o.Horizontal = _dir * (_speed * b.SpeedScale);
            }
            o.ColliderHeight = T.SlideColliderHeight;
            o.CamHeight = -0.45f;
            o.CamFovAdd = T.SlideFov * 0.5f;
        }

        void OutSlide(in BodyState b, ref PkOutput o)
        {
            o.SetHorizontal = true;
            o.Horizontal = _dir * (_speed * b.SpeedScale);
            o.ColliderHeight = T.SlideColliderHeight;
            o.CamHeight = T.SlideEyeDrop;
            o.CamFovAdd = T.SlideFov;
            o.CamRoll = T.SlideRoll * (V3.Dot(V3.Cross(V3.Up, b.LookFlat), _dir) >= 0f ? -1f : 1f);
            // hands: left one braces on the ground beside the hip, right one reaches forward for balance
            V3 right = V3.Cross(V3.Up, _dir).Normalized;
            float k = Smooth(Math.Min(1f, StateTime / 0.15f));
            o.CamPitch = T.SlideLookDown * k;   // look down the legs, like DL's slide view
            o.HandL = new HandTarget { Point = b.Feet - right * 0.35f + _dir * 0.25f + V3.Up * 0.05f, Weight = 0.9f * k };
            o.HandR = new HandTarget { Point = b.Feet + _dir * 0.6f + right * 0.2f + V3.Up * 0.5f, Weight = 0.8f * k };
        }

        // ---------------------------------------------------------------- wall run

        bool TryWallRun(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (!On(PkFeature.WallRun)) return false;
            if (i.MoveY <= 0f || _now - _lastGroundedTime < 0.05f) return false;
            V3 v = b.Velocity.Flat;
            if (_carryActive && _carry.FlatLength > v.FlatLength) v = _carry;
            float speed = v.FlatLength;
            if (speed < T.WallRunMinSpeed || b.Velocity.Y < -T.WallRunMaxFallSpeed) return false;
            if (b.Stamina < T.WallRunStaminaEnter) return false;

            V3 right = V3.Cross(V3.Up, b.LookFlat).Normalized;
            V3 chest = b.Feet + V3.Up * (b.Height * 0.55f);
            for (int s = 1; s >= -1; s -= 2)
            {
                if (!w.Ray(chest, right * s, b.Radius + T.WallRunProbe, out Hit h)) continue;
                if (Math.Abs(h.Normal.Y) > 0.35f) continue;
                V3 n = h.Normal.Flat.Normalized;
                V3 t = V3.Cross(n, V3.Up).Normalized;
                if (V3.Dot(t, v) < 0f) t = -t;
                float cos = V3.Dot(t, v.Normalized);
                if (cos < (float)Math.Cos(T.WallRunMaxAngle * Math.PI / 180.0)) continue;
                if (V3.Dot(n, _lastWallNormal) > 0.95f && _now - _lastWallTime < T.WallRunSameWallCooldown) continue;

                Pay(b, T.WallRunStaminaEnter, ref o);
                _wallN = n;
                _dir = t;
                _side = s;
                _speed = Math.Max(V3.Dot(v, t), T.WallRunSpeed);
                _entrySpeed = Clamp(b.Velocity.Y, T.WallRunEntryUp, T.WallRunEntryMaxUp);
                SetState(PkState.WallRun, s > 0 ? "wallrun-right" : "wallrun-left");
                OutWallRun(b, _entrySpeed, ref o);
                _entrySpeed -= b.Gravity * T.WallRunGravity * b.Dt;
                return true;
            }
            return false;
        }

        void TickWallRun(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (b.Grounded) { SetState(PkState.None, "wallrun-ground"); return; }
            if (i.JumpPressed && On(PkFeature.WallJump))
            {
                Pay(b, Math.Min(b.Stamina, T.WallJumpStamina), ref o);
                WallJump(b, _wallN, _dir * _speed, ref o);
                return;
            }
            if (i.CrouchPressed || i.MoveY <= 0f)
            {
                ExitToAir(_dir * _speed + _wallN * 1.5f, "wallrun-drop");
                RememberWall();
                return;
            }
            if (StateTime > T.WallRunMaxTime || b.Stamina < T.WallRunStaminaPerSec * b.Dt)
            {
                ExitToAir(_dir * _speed + _wallN * 1.0f, "wallrun-timeout");
                RememberWall();
                return;
            }
            V3 chest = b.Feet + V3.Up * (b.Height * 0.55f);
            if (!w.Ray(chest, -_wallN, b.Radius + T.WallRunProbe + 0.25f, out Hit h) || Math.Abs(h.Normal.Y) > 0.35f)
            {
                ExitToAir(_dir * _speed, "wallrun-wall-end");
                RememberWall();
                return;
            }
            if (Stuck(b.Velocity.FlatLength < _speed * 0.3f))
            {
                ExitToAir(_wallN * 1.0f, "wallrun-blocked");
                RememberWall();
                return;
            }
            V3 n = h.Normal.Flat.Normalized;
            V3 t = V3.Cross(n, V3.Up).Normalized;
            if (V3.Dot(t, _dir) < 0f) t = -t;
            _wallN = n;
            _dir = t;
            o.StaminaCost += Cost(T.WallRunStaminaPerSec * b.Dt);
            _entrySpeed = Math.Max(_entrySpeed - b.Gravity * T.WallRunGravity * b.Dt, -T.WallRunMaxFallSpeed);
            OutWallRun(b, _entrySpeed, ref o);
        }

        void OutWallRun(in BodyState b, float vy, ref PkOutput o)
        {
            o.SetHorizontal = true;
            o.Horizontal = _dir * (_speed * b.SpeedScale) - _wallN * 1.0f;
            o.SetVertical = true;
            o.Vertical = vy;
            o.CamHeight = 0.035f * (float)Math.Sin(2.0 * Math.PI * 3.2 * StateTime);   // footsteps on the wall
            // the wall-side hand brushes along the wall
            var touch = new HandTarget { Point = b.Feet + V3.Up * 1.3f - _wallN * (b.Radius + 0.1f) + _dir * 0.65f, Weight = 0.8f };
            if (_side > 0) o.HandR = touch; else o.HandL = touch;
            o.CamRoll = -_side * T.WallRunRoll;
        }

        void WallJump(in BodyState b, V3 n, V3 alongVel, ref PkOutput o)
        {
            V3 along = alongVel.Flat - n * V3.Dot(alongVel.Flat, n);
            V3 look = b.LookFlat;
            V3 extra = V3.Dot(look, n) > 0.2f ? look * T.WallJumpLook : V3.Zero;
            _carry = n * T.WallJumpOut + along * T.WallJumpAlong + extra;
            _carryActive = true;
            _jumpedSinceGround = true;
            _blockJumpUntil = _now + 0.25f;
            _airLockUntil = _now + T.WallJumpControlLock;
            _wallN = n;
            RememberWall();
            SetState(PkState.None, "walljump");
            o.SetHorizontal = true;
            o.Horizontal = _carry;
            o.SetVertical = true;
            o.Vertical = T.WallJumpUp;
        }

        void RememberWall()
        {
            _lastWallNormal = _wallN;
            _lastWallTime = _now;
        }

        // ---------------------------------------------------------------- wall climb

        bool FacingWall(in BodyState b, IWorldProbe w, float reach, out Hit hit)
        {
            V3 chest = b.Feet + V3.Up * (b.Height * 0.55f);
            if (!w.Ray(chest, b.LookFlat, b.Radius + reach, out hit)) return false;
            if (Math.Abs(hit.Normal.Y) > 0.35f) return false;
            V3 n = hit.Normal.Flat.Normalized;
            return V3.Dot(b.LookFlat, -n) >= (float)Math.Cos(T.WallClimbMaxAngle * Math.PI / 180.0);
        }

        void EnterWallClimb(in BodyState b, Hit wall, ref PkOutput o)
        {
            _wallN = wall.Normal.Flat.Normalized;
            _climbUsed = true;
            _jumpedSinceGround = true;
            SetState(PkState.WallClimb, "wallclimb");
            OutClimb(b, ref o);
        }

        void TickWallClimb(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            V3 fwd = -_wallN;
            if (DetectLedge(b, w, fwd, out float top, out float h))
            {
                if (h <= T.MantleMaxHeight && On(PkFeature.Mantle))
                {
                    EnterMantle(b, fwd, top, vault: false, entrySpeed: 0f, ref o);
                    return;
                }
                if (On(PkFeature.Hang) && (StateTime >= T.WallClimbTime * 0.7f || !On(PkFeature.Mantle)))
                {
                    EnterHang(b, fwd, top, ref o);
                    return;
                }
            }
            if (i.JumpPressed && StateTime > 0.12f && On(PkFeature.WallJump))
            {
                WallJump(b, _wallN, V3.Zero, ref o);
                return;
            }
            V3 chest = b.Feet + V3.Up * (b.Height * 0.55f);
            bool wall = w.Ray(chest, fwd, b.Radius + 0.8f, out Hit hw);
            if (StateTime >= T.WallClimbTime || !wall || i.MoveY <= 0f)
            {
                ExitToAir(_wallN * 0.8f, "wallclimb-end");
                return;
            }
            if (Stuck(StateTime > 0.1f && b.Velocity.Y < 0.4f)) { ExitToAir(_wallN * 0.8f, "wallclimb-blocked"); return; }
            if (Math.Abs(hw.Normal.Y) <= 0.35f) _wallN = hw.Normal.Flat.Normalized;
            OutClimb(b, ref o);
        }

        void OutClimb(in BodyState b, ref PkOutput o)
        {
            float k = Clamp(StateTime / T.WallClimbTime, 0f, 1f);
            // camera: look up the wall, running steps
            float t = StateTime;
            o.CamPitch = -16f * Smooth(Math.Min(1f, t / 0.15f));
            o.CamHeight = 0.05f * (float)Math.Sin(2.0 * Math.PI * 4.0 * t);
            o.CamRoll = 2.5f * (float)Math.Sin(2.0 * Math.PI * 2.0 * t);
            // hands paw up the wall, alternating
            V3 wf = -_wallN;
            V3 wr = V3.Cross(V3.Up, wf).Normalized;
            V3 wallAt = b.Feet + wf * (b.Radius + 0.08f);
            double ph = 2.0 * Math.PI * 2.0 * t;
            o.HandL = new HandTarget { Point = wallAt - wr * 0.22f + V3.Up * (1.55f + 0.3f * (float)Math.Sin(ph)), Weight = 0.9f };
            o.HandR = new HandTarget { Point = wallAt + wr * 0.22f + V3.Up * (1.55f + 0.3f * (float)Math.Sin(ph + Math.PI)), Weight = 0.9f };
            o.SetHorizontal = true;
            o.Horizontal = -_wallN * 1.2f;
            o.SetVertical = true;
            o.Vertical = T.WallClimbSpeed * (1f - 0.65f * k);
        }

        // ---------------------------------------------------------------- ledges, mantle, vault, hang

        /// <summary>Finds a walkable top in front of the body. h = top - feet.</summary>
        public bool DetectLedge(in BodyState b, IWorldProbe w, V3 fwd, out float top, out float h)
        {
            top = h = 0f;
            fwd = fwd.Flat.Normalized;
            float probeTop = T.HangMaxHeight + 0.15f;
            V3 origin = b.Feet + fwd * (b.Radius + T.LedgeForward) + V3.Up * probeTop;
            if (!w.Ray(origin, new V3(0f, -1f, 0f), probeTop - T.MantleMinHeight + 0.05f, out Hit down)) return false;
            if (down.Normal.Y < 0.7f || down.Distance < 0.01f) return false;
            top = down.Point.Y;
            h = top - b.Feet.Y;
            if (h < T.MantleMinHeight || h > T.HangMaxHeight) return false;
            // a face must be in front just below the top, otherwise this is a ramp
            V3 face = b.Feet + V3.Up * (h - 0.2f);
            if (!w.Ray(face, fwd, b.Radius + T.LedgeForward + 0.1f, out Hit fh) || Math.Abs(fh.Normal.Y) > 0.5f) return false;
            _ledgeFaceN = fh.Normal.Flat.Normalized;
            // room to stand (crouched) on top, and room to rise in place
            V3 standAt = new V3(down.Point.X, top + 0.05f, down.Point.Z);
            if (!w.CapsuleFree(standAt, b.Radius * 0.9f, b.CrouchHeight)) return false;
            V3 raised = new V3(b.Feet.X, top + 0.05f, b.Feet.Z);
            if (!w.CapsuleFree(raised, b.Radius * 0.85f, b.CrouchHeight)) return false;
            return true;
        }

        bool TryVault(in BodyState b, IWorldProbe w, out float top)
        {
            top = 0f;
            V3 knee = b.Feet + V3.Up * (T.VaultMinHeight - 0.15f);
            float reach = b.Radius + 0.35f + b.Velocity.FlatLength * 0.1f;
            if (!w.Ray(knee, b.LookFlat, reach, out Hit face) || Math.Abs(face.Normal.Y) > 0.35f) return false;
            float probeTop = T.VaultMaxHeight + 0.2f;
            V3 origin = b.Feet + b.LookFlat * (face.Distance + 0.15f) + V3.Up * probeTop;
            if (!w.Ray(origin, new V3(0f, -1f, 0f), probeTop - T.VaultMinHeight + 0.15f, out Hit down)) return false;
            if (down.Normal.Y < 0.7f || down.Distance < 0.01f) return false;
            top = down.Point.Y;
            float h = top - b.Feet.Y;
            if (h < T.VaultMinHeight || h > T.VaultMaxHeight) return false;
            _vaultGap = Math.Max(face.Distance - b.Radius, 0.05f);
            V3 raised = new V3(b.Feet.X, top + 0.05f, b.Feet.Z);
            if (!w.CapsuleFree(raised, b.Radius * 0.85f, b.CrouchHeight)) return false;
            V3 over = new V3(down.Point.X, top + 0.05f, down.Point.Z);
            return w.CapsuleFree(over, b.Radius * 0.9f, b.CrouchHeight);
        }

        /// <summary>Straight into the face the last DetectLedge found, so hanging / climbing at an angle doesn't
        /// push the body along the wall (the controller slides off the tangent part of a slanted push).</summary>
        V3 IntoLedgeFace(V3 fwd)
        {
            fwd = fwd.Flat.Normalized;
            V3 n = -_ledgeFaceN;
            return n.FlatLength > 0.5f && V3.Dot(n, fwd) > 0.5f ? n : fwd;
        }

        void EnterMantle(in BodyState b, V3 fwd, float top, bool vault, float entrySpeed, ref PkOutput o)
        {
            _ledgeFwd = vault ? fwd.Flat.Normalized : IntoLedgeFace(fwd);
            _ledgeTop = top;
            _vault = vault;
            _entrySpeed = entrySpeed;
            _jumpedSinceGround = true;
            _mantleStartY = b.Feet.Y;
            if (vault)
            {
                float tReach = _vaultGap / Math.Max(entrySpeed, 1f);
                _vaultUp = Clamp((top + 0.12f - b.Feet.Y) / Math.Max(tReach, 0.05f), T.VaultUpSpeed, 11f);
            }
            // hands grab the edge (vault: left hand plants on the obstacle)
            V3 r = V3.Cross(V3.Up, _ledgeFwd).Normalized;
            V3 edge = b.Feet + _ledgeFwd * (vault ? _vaultGap + b.Radius + 0.2f : b.Radius + 0.15f);
            edge.Y = top + 0.03f;
            _anchorL = edge - r * (vault ? 0.1f : 0.22f);
            _anchorR = edge + r * 0.22f;
            SetState(PkState.Mantle, vault ? "vault" : "mantle");
            TickMantle(b, default, null, ref o);
        }

        void TickMantle(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (StateTime > T.MantleMaxTime) { SetState(PkState.None, "mantle-timeout"); return; }
            if (_phase == 0)
            {
                if (b.Feet.Y >= _ledgeTop + 0.04f)
                {
                    _phase = 1;
                    _phaseStart = b.Feet;
                    _stuck = 0;
                }
                else
                {
                    // cancel while rising: crouch lets go; a vault also stops when forward is released
                    if (w != null && StateTime > 0.05f && (i.CrouchPressed || (_vault && i.MoveY <= 0f)))
                    {
                        SetState(PkState.None, _vault ? "vault-cancel" : "mantle-cancel");
                        o.SetVertical = true;
                        o.Vertical = Math.Min(b.Velocity.Y, 0f);
                        o.SetHorizontal = true;
                        o.Horizontal = -_ledgeFwd * 0.5f;
                        return;
                    }
                    if (w != null && Stuck(StateTime > 0.08f && b.Velocity.Y < 0.5f)) { SetState(PkState.None, "mantle-blocked"); return; }
                    float up = _vault ? _vaultUp : T.MantleUpSpeed;
                    // don't overshoot more than one tick of travel above the top
                    float need = (_ledgeTop + 0.08f - b.Feet.Y) / Math.Max(b.Dt, 1e-3f);
                    o.SetVertical = true;
                    o.Vertical = Math.Min(up, Math.Max(need, 1.5f));
                    o.SetHorizontal = true;
                    o.Horizontal = _ledgeFwd * (_vault ? _entrySpeed : 0.25f);
                    // camera: look down at the ledge, eye lags below while the body pulls up, hand-over-hand sway
                    float k = Clamp((b.Feet.Y - _mantleStartY) / Math.Max(_ledgeTop - _mantleStartY, 0.1f), 0f, 1f);
                    o.HandL = new HandTarget { Point = _anchorL, Weight = 1f };
                    if (!_vault) o.HandR = new HandTarget { Point = _anchorR, Weight = 1f };
                    if (_vault)
                    {
                        o.CamPitch = 10f * Smooth(k);
                        o.CamRoll = 7f * (float)Math.Sin(Math.PI * k);
                        o.CamHeight = -0.1f;
                    }
                    else
                    {
                        o.CamPitch = 22f * Smooth(Math.Min(1f, StateTime / 0.12f));
                        o.CamRoll = 3f * (float)Math.Sin(2.0 * Math.PI * k);
                        o.CamHeight = -0.35f * (1f - k);
                    }
                    return;
                }
            }
            float fwdSpeed = _vault ? Math.Max(_entrySpeed, T.MantleForwardSpeed) : T.MantleForwardSpeed;
            float moved = (b.Feet - _phaseStart).FlatLength;
            float need2 = b.Radius * 2f + (_vault ? 0.35f : 0.15f);
            if (_vault && w != null && moved >= b.Radius + 0.1f)
            {
                // body over the edge: the player decides. Forward held = run on (over a thin obstacle, off its far
                // side), released = stay standing on top.
                bool on = i.MoveY > 0f;
                if (on && !b.Grounded) ExitToAir(_ledgeFwd * fwdSpeed, "vault-done");
                else SetState(PkState.None, "vault-done");
                _blockJumpUntil = _now + 0.3f;
                o.SetHorizontal = true;
                o.Horizontal = on ? _ledgeFwd * fwdSpeed : V3.Zero;
                return;
            }
            if (moved >= need2)
            {
                if (_vault) ExitToAir(_ledgeFwd * fwdSpeed, "vault-done");
                else SetState(PkState.None, "mantle-done");
                _blockJumpUntil = _now + 0.3f; // a held jump key must not hop again right on top
                o.SetHorizontal = true;
                o.Horizontal = _ledgeFwd * (_vault ? fwdSpeed : 1.5f);
                return;
            }
            if (w != null && Stuck(StateTime > 0.1f && b.Velocity.FlatLength < 0.3f)) { SetState(PkState.None, "mantle-blocked2"); return; }
            o.SetHorizontal = true;
            o.Horizontal = _ledgeFwd * fwdSpeed;
            o.SetVertical = true;
            o.Vertical = _vault ? 0.3f : -0.5f;
            // camera: straighten up while stepping over the edge
            float k1 = Clamp(moved / need2, 0f, 1f);
            float hw = 1f - Smooth(k1 / 0.8f);   // hands let go as we step over
            o.HandL = new HandTarget { Point = _anchorL, Weight = hw };
            if (!_vault) o.HandR = new HandTarget { Point = _anchorR, Weight = hw };
            o.CamPitch = (_vault ? 10f : 22f) * (1f - Smooth(k1));
            o.CamHeight = -0.12f * (float)Math.Sin(Math.PI * k1);
            if (_vault) o.CamRoll = 7f * (1f - k1);
        }

        /// <summary>While the hands hold an edge or the body runs up a wall: the direction the body must face.</summary>
        public bool TryFacingLock(out V3 dir)
        {
            switch (State)
            {
                case PkState.Hang: dir = _cornering ? CornerFacing() : _ledgeFwd; return true;
                case PkState.WallClimb: dir = -_wallN; return true;
                case PkState.Mantle: dir = _ledgeFwd; return !_vault;
                default: dir = V3.Zero; return false;
            }
        }

        void EnterHang(in BodyState b, V3 fwd, float top, ref PkOutput o)
        {
            _ledgeFwd = IntoLedgeFace(fwd);
            _ledgeTop = top;
            _carryActive = false;
            _hangFwdHeld = 0f;
            _hangBackHeld = 0f;
            _hangFwdArmed = false;
            _cornering = false;
            SetState(PkState.Hang, "hang");
            OutHang(b, 0f, ref o);
        }

        void TickHang(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (b.Grounded && StateTime > 0.2f) { SetState(PkState.None, "hang-ground"); return; }
            if (i.CrouchPressed) { _cornering = false; ExitToAir(-_ledgeFwd * 1.0f, "hang-drop"); return; }
            if (_cornering) { TickCorner(b, ref o); return; }
            if (i.JumpPressed && i.MoveY < 0f)
            {
                if (On(PkFeature.WallJump)) WallJump(b, -_ledgeFwd, V3.Zero, ref o);
                else ExitToAir(-_ledgeFwd * 0.5f, "hang-letgo");
                return;
            }
            // W climbs up: a fresh press, or holding it a moment (W still held from the run-in grabs first)
            if (i.MoveY > 0.5f) _hangFwdHeld += b.Dt; else _hangFwdHeld = 0f;
            if (i.MoveY <= 0.2f) _hangFwdArmed = true;
            bool fwdClimb = i.MoveY > 0.5f && (_hangFwdArmed || _hangFwdHeld >= T.HangClimbHold);
            if ((i.JumpPressed || fwdClimb) && On(PkFeature.Mantle) && PayClimb(b, T.MantleStamina, ref o))
            {
                EnterMantle(b, _ledgeFwd, _ledgeTop, vault: false, entrySpeed: 0f, ref o);
                return;
            }
            // S: down to a lower ledge if one is in reach, otherwise let go. Held briefly so S + Jump still kicks off.
            if (i.MoveY < -0.5f) _hangBackHeld += b.Dt; else _hangBackHeld = 0f;
            if (_hangBackHeld >= 0.15f)
            {
                if (LowerLedge(b, w, out float lower))
                {
                    _ledgeTop = lower;
                    _hangBackHeld = -10f;   // one step per press
                    SetState(PkState.Hang, "hang-down");
                    OutHang(b, 0f, ref o);
                    return;
                }
                if (_hangBackHeld > 0f) { ExitToAir(-_ledgeFwd * 0.5f, "hang-letgo"); return; }
            }
            bool settling = Math.Abs(b.Feet.Y - (_ledgeTop - T.HangHeight)) > 0.2f;   // still sliding down to a lower ledge
            // ledge still there? (re-detect around the hang height)
            float shimmy = 0f;
            if (Math.Abs(i.MoveX) > 0.2f)
            {
                V3 right = V3.Cross(V3.Up, _ledgeFwd).Normalized;
                V3 side = right * Math.Sign(i.MoveX);
                if (w != null && !settling && TryInnerCorner(b, w, side, ref o)) return;
                var probe = b;
                probe.Feet = b.Feet + side * (b.Radius + 0.1f);
                if (DetectLedge(probe, w, _ledgeFwd, out float t2, out float h2) && Math.Abs(t2 - _ledgeTop) < 0.6f)
                {
                    shimmy = Math.Sign(i.MoveX) * T.ShimmySpeed;
                    _ledgeTop = t2;
                }
                else if (w != null && !settling && TryOuterCorner(b, w, side, ref o)) return;
            }
            if (StateTime > 0.25f && !settling && !DetectLedge(b, w, _ledgeFwd, out float t3, out _))
            {
                ExitToAir(V3.Zero, "hang-lost");
                return;
            }
            float drain = Has(PkBonus.HangFree) ? 0f : Cost((shimmy != 0f ? T.ShimmyStaminaPerSec : T.HangStaminaPerSec) * b.Dt);
            if (b.Stamina <= drain && drain > 0f)
            {
                ExitToAir(-_ledgeFwd * 0.5f, "hang-exhausted");
                return;
            }
            o.StaminaCost += drain;
            o.NoStaminaRegen = true;
            OutHang(b, shimmy, ref o);
        }

        // ---------------------------------------------------------------- hanging round corners

        /// <summary>The wall turns towards us (concave corner): a face right beside us, in the shimmy direction,
        /// with an edge at the same height. Slide into the corner and turn to face it.</summary>
        bool TryInnerCorner(in BodyState b, IWorldProbe w, V3 side, ref PkOutput o)
        {
            V3 at = new V3(b.Feet.X, _ledgeTop - 0.15f, b.Feet.Z);
            if (!w.Ray(at, side, b.Radius + 0.35f, out Hit h) || Math.Abs(h.Normal.Y) > 0.5f) return false;
            V3 n = h.Normal.Flat.Normalized;
            if (V3.Dot(n, -side) < 0.7f) return false;
            if (!DetectLedge(b, w, -n, out float top, out _) || Math.Abs(top - _ledgeTop) > 0.3f) return false;
            V3 target = b.Feet + side * Math.Max(0f, h.Distance - (b.Radius + 0.05f));
            StartCorner(b, b.Feet + (target - b.Feet) * 0.5f, target, -n, top, "hang-corner-in", ref o);
            return true;
        }

        /// <summary>The block ends but its edge goes on round the side (convex corner): move past the end and onto
        /// the side face, turning 90 degrees.</summary>
        bool TryOuterCorner(in BodyState b, IWorldProbe w, V3 side, ref PkOutput o)
        {
            V3 f = _ledgeFwd;
            V3 chest = new V3(b.Feet.X, _ledgeTop - 0.15f, b.Feet.Z);
            float front = w.Ray(chest, f, b.Radius + 0.6f, out Hit fh) ? fh.Distance : b.Radius + 0.05f;
            // from beside the end of the block, just past its front face, look back at its side face
            V3 s = chest + side * (b.Radius + 0.7f) + f * (front + 0.3f);
            if (!w.Ray(s, -side, b.Radius + 0.9f, out Hit sh) || Math.Abs(sh.Normal.Y) > 0.5f) return false;
            V3 n = sh.Normal.Flat.Normalized;
            if (V3.Dot(n, side) < 0.7f) return false;
            V3 dest = sh.Point + n * (b.Radius + 0.05f);
            dest.Y = b.Feet.Y;
            var probe = b;
            probe.Feet = dest;
            if (!DetectLedge(probe, w, -n, out float top, out _) || Math.Abs(top - _ledgeTop) > 0.3f) return false;
            // first out past the end (same distance from the front face), then round onto the side
            V3 w1 = b.Feet + side * V3.Dot(dest - b.Feet, side);
            if (!w.CapsuleFree(w1, b.Radius * 0.9f, b.Height * 0.9f) || !w.CapsuleFree(dest, b.Radius * 0.9f, b.Height * 0.9f)) return false;
            StartCorner(b, w1, dest, -n, top, "hang-corner-out", ref o);
            return true;
        }

        static float YawOf(V3 d) => (float)(Math.Atan2(d.X, d.Z) * 180.0 / Math.PI);

        static float DeltaYaw(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        void StartCorner(in BodyState b, V3 w1, V3 w2, V3 newFwd, float top, string evt, ref PkOutput o)
        {
            _cornerFrom = _ledgeFwd;
            _cornerW1 = w1;
            _cornerW2 = w2;
            _cornerT = 0f;
            _cornerYaw = DeltaYaw(YawOf(_ledgeFwd), YawOf(newFwd));
            _ledgeFwd = newFwd.Flat.Normalized;
            _ledgeTop = top;
            _cornering = true;
            SetState(PkState.Hang, evt);
            TickCorner(b, ref o);
        }

        /// <summary>The facing while going round a corner (hands, camera lock).</summary>
        V3 CornerFacing()
        {
            float k = Clamp(_cornerT / T.HangCornerTime, 0f, 1f);
            double a = (YawOf(_cornerFrom) + _cornerYaw * Smooth(k)) * Math.PI / 180.0;
            return new V3((float)Math.Sin(a), 0f, (float)Math.Cos(a));
        }

        void TickCorner(in BodyState b, ref PkOutput o)
        {
            float kPrev = Smooth(Clamp(_cornerT / T.HangCornerTime, 0f, 1f));
            _cornerT += b.Dt;
            float k = Clamp(_cornerT / T.HangCornerTime, 0f, 1f);
            o.CamYawTurn = _cornerYaw * (Smooth(k) - kPrev);   // the view turns with the body
            if (!Has(PkBonus.HangFree)) o.StaminaCost += Cost(T.ShimmyStaminaPerSec * b.Dt);
            o.NoStaminaRegen = true;
            V3 target = k < 0.5f ? _cornerW1 : _cornerW2;
            float left = Math.Max((k < 0.5f ? 0.5f : 1f) * T.HangCornerTime - _cornerT, b.Dt);
            V3 v = (target - b.Feet).Flat * (1f / left);
            if (v.FlatLength > 4f) v = v.Flat.Normalized * 4f;
            OutHang(b, 0f, ref o);
            if (k >= 1f)
            {
                _cornering = false;
                _stateStart = _now;   // fresh grace before the ledge re-check
                return;
            }
            o.SetHorizontal = true;
            o.Horizontal = v;
            // hands follow the turning facing
            V3 f = CornerFacing();
            V3 r = V3.Cross(V3.Up, f).Normalized;
            V3 edge = b.Feet + f * (b.Radius + 0.05f);
            edge.Y = _ledgeTop + 0.03f;
            o.HandL = new HandTarget { Point = edge - r * 0.22f, Weight = 1f };
            o.HandR = new HandTarget { Point = edge + r * 0.22f, Weight = 1f };
        }

        /// <summary>The next ledge below the one we hang from, straight down the same face (a sill under a recess,
        /// a lower window, a step in the wall). A flat wall has none: the probe starts inside it and finds nothing.</summary>
        bool LowerLedge(in BodyState b, IWorldProbe w, out float top)
        {
            top = 0f;
            if (w == null) return false;
            for (float d = 0.5f; d <= T.HangMaxHeight + 0.01f; d += 0.25f)
            {
                var probe = b;
                probe.Feet = b.Feet - V3.Up * d;
                if (!DetectLedge(probe, w, _ledgeFwd, out float t, out _) || t > _ledgeTop - 0.4f) continue;
                // room to hang there: the body fits along the face under the new edge
                V3 at = new V3(b.Feet.X, t - T.HangHeight, b.Feet.Z);
                if (!w.CapsuleFree(at, b.Radius * 0.9f, b.Height * 0.9f)) continue;
                top = t;
                return true;
            }
            return false;
        }

        void OutHang(in BodyState b, float shimmy, ref PkOutput o)
        {
            float targetY = _ledgeTop - T.HangHeight;
            V3 right = V3.Cross(V3.Up, _ledgeFwd).Normalized;
            o.SetVertical = true;
            o.Vertical = Clamp((targetY - b.Feet.Y) * 12f, -5f, 5f);
            o.SetHorizontal = true;
            o.Horizontal = _ledgeFwd * 0.4f + right * shimmy;
            // camera: slight look down at the hands, slow sway, hand-over-hand rhythm while shimmying
            float t = StateTime;
            o.CamPitch = -6f;
            o.CamRoll = 1.2f * (float)Math.Sin(2.0 * Math.PI * 0.5 * t);
            o.CamHeight = 0f;
            float lead = 0f;
            if (shimmy != 0f)
            {
                o.CamRoll += 2.5f * (float)Math.Sin(2.0 * Math.PI * 3.0 * t);
                o.CamHeight += 0.03f * (float)Math.Sin(2.0 * Math.PI * 6.0 * t);
                lead = 0.1f * (float)Math.Sin(2.0 * Math.PI * 3.0 * t);   // hand over hand
            }
            // both hands on the edge
            V3 edge = b.Feet + _ledgeFwd * (b.Radius + 0.05f);
            edge.Y = _ledgeTop + 0.03f;
            o.HandL = new HandTarget { Point = edge - right * (0.22f + lead), Weight = 1f };
            o.HandR = new HandTarget { Point = edge + right * (0.22f - lead), Weight = 1f };
        }

        // ---------------------------------------------------------------- landing roll

        void EnterRoll(in BodyState b, ref PkOutput o)
        {
            V3 d = _carry.FlatLength > 0.5f ? _carry.Flat.Normalized : b.LookFlat;
            _dir = d;
            _speed = Math.Max(_carry.FlatLength, T.RollSpeed);
            SetState(PkState.Roll, "roll");
            OutRoll(b, ref o);
        }

        void TickRoll(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            if (StateTime >= T.RollTime)
            {
                SetState(PkState.None, "roll-done");
                o.SetHorizontal = true;
                o.Horizontal = _dir * Math.Min(_speed, T.AirCruiseSpeed);
                return;
            }
            if (i.JumpPressed && StateTime > 0.25f && b.Grounded)
            {
                _carry = _dir * _speed;
                _carryActive = true;
                _carryKeepUntil = _now + T.JumpCarryKeep;
                _jumpedSinceGround = true;
                SetState(PkState.None, "roll-jump");
                o.SetHorizontal = true;
                o.Horizontal = _carry;
                o.SetVertical = true;
                o.Vertical = T.JumpSpeed;
                return;
            }
            if (!b.Grounded && StateTime > 0.1f) { ExitToAir(_dir * _speed, "roll-off-edge"); return; }
            if (Stuck(b.Velocity.FlatLength < _speed * 0.3f)) { SetState(PkState.None, "roll-blocked"); return; }
            OutRoll(b, ref o);
        }

        void OutRoll(in BodyState b, ref PkOutput o)
        {
            float k = Clamp(StateTime / T.RollTime, 0f, 1f);
            o.SetHorizontal = true;
            o.Horizontal = _dir * (_speed * (1f - 0.3f * k) * b.SpeedScale);
            float s = k * k * (3f - 2f * k);
            o.CamPitchSpin = 360f * s;
            o.CamHeight = -0.7f * (float)Math.Sin(Math.PI * k);
        }

        // ---------------------------------------------------------------- combat

        bool CanStrike(int id) => !_struckUntil.TryGetValue(id, out float t) || _now >= t;

        void Strike(ref PkOutput o, StrikeKind kind, in EnemyInfo e, V3 dir, float damage, float stun)
        {
            o.Strike = kind;
            o.StrikeTarget = e.Id;
            o.StrikeDir = dir;
            o.StrikeDamage = (int)Math.Round(damage);
            o.StrikeStun = stun;
            _struckUntil[e.Id] = _now + T.StrikeCooldown;
            // camera punch: head snaps with the impact
            _punchAt = _now;
            _punchPitch = kind == StrikeKind.Stomp ? 18f : kind == StrikeKind.Dropkick ? -10f : -5f;
            _punchRoll = kind == StrikeKind.Dropkick ? 5f : kind == StrikeKind.Tackle ? 3f : 0f;
            LastEvent = kind == StrikeKind.Tackle ? "tackle" : kind == StrikeKind.Dropkick ? "dropkick" : "stomp";
            if (o.StrikeTargets == null) o.StrikeTargets = new int[8];
            o.StrikeTargets[0] = e.Id;
            o.StrikeCount = 1;
        }

        /// <summary>
        /// The first enemy hit plus everyone standing close behind / beside it in the sweep (a slide into a group
        /// takes them all down). All of them are thrown back along dir.
        /// </summary>
        void StrikeGroup(ref PkOutput o, in BodyState b, IWorldProbe w, StrikeKind kind, in EnemyInfo first, V3 dir,
                         float damage, float stun, float width, float knockSpeed, float knockLift)
        {
            Strike(ref o, kind, first, dir, damage, stun);
            o.KnockSpeed = knockSpeed;
            o.KnockLift = knockLift;
            dir = dir.Flat.Normalized;
            float firstAlong = V3.Dot((first.Feet - b.Feet).Flat, dir);
            int n = w.Enemies(b.Feet, firstAlong + 4f, _enemies);
            for (int k = 0; k < n && o.StrikeCount < o.StrikeTargets.Length; k++)
            {
                EnemyInfo e = _enemies[k];
                if (e.Id == first.Id || e.Down || !CanStrike(e.Id)) continue;
                V3 d = (e.Feet - b.Feet).Flat;
                float along = V3.Dot(d, dir);
                if (along <= 0f || along > firstAlong + 1.3f) continue;          // up to ~one body behind the first
                if ((d - dir * along).FlatLength > e.Radius + b.Radius + width) continue;
                if (b.Feet.Y > e.Feet.Y + e.Height * 0.85f || b.Feet.Y + b.Height * 0.5f < e.Feet.Y) continue;
                o.StrikeTargets[o.StrikeCount++] = e.Id;
                _struckUntil[e.Id] = _now + T.StrikeCooldown;
            }
        }

        /// <summary>Closest standing enemy in front within reach (gap between capsules), overlapping our body height.</summary>
        bool EnemyAhead(in BodyState b, IWorldProbe w, V3 dir, float reach, out EnemyInfo hit)
        {
            hit = default;
            dir = dir.Flat.Normalized;
            int n = w.Enemies(b.Feet, reach + 3f, _enemies);
            float best = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                EnemyInfo e = _enemies[k];
                if (e.Down || !CanStrike(e.Id)) continue;
                V3 d = (e.Feet - b.Feet).Flat;
                float along = V3.Dot(d, dir);
                if (along <= 0f) continue;
                float lateral = (d - dir * along).FlatLength;
                if (lateral > e.Radius + b.Radius) continue;
                float gap = along - e.Radius - b.Radius;
                if (gap > reach || gap >= best) continue;
                if (b.Feet.Y > e.Feet.Y + e.Height * 0.85f || b.Feet.Y + b.Height * 0.5f < e.Feet.Y) continue;
                best = gap;
                hit = e;
            }
            return best < float.MaxValue;
        }

        bool EnemyBelow(in BodyState b, IWorldProbe w, out EnemyInfo hit)
        {
            hit = default;
            int n = w.Enemies(b.Feet, 3f, _enemies);
            for (int k = 0; k < n; k++)
            {
                EnemyInfo e = _enemies[k];
                if (e.Down || !CanStrike(e.Id)) continue;
                if ((e.Feet - b.Feet).FlatLength > e.Radius + b.Radius * 0.8f) continue;
                float top = e.Feet.Y + e.Height;
                if (b.Feet.Y < top - 0.5f || b.Feet.Y > top + 0.4f) continue;
                hit = e;
                return true;
            }
            return false;
        }

        /// <summary>Airborne hits: stomp when falling onto a head, dropkick when flying into a body.</summary>
        bool TryAirStrike(in BodyState b, IWorldProbe w, ref PkOutput o)
        {
            if (On(PkFeature.Stomp) && b.Velocity.Y < -T.StompMinFallSpeed && EnemyBelow(b, w, out EnemyInfo below))
            {
                float dmg = T.StompDamage + T.StompDamagePerMps * Math.Max(0f, -b.Velocity.Y - T.StompMinFallSpeed);
                Strike(ref o, StrikeKind.Stomp, below, new V3(0f, -1f, 0f), dmg, T.StompStun);
                _jumpedSinceGround = true;
                _rollArmedUntil = 0f;
                o.SetVertical = true;
                o.Vertical = T.StompBounce;
                o.ResetFallHeight = true;
                return true;
            }
            V3 v = _carryActive ? _carry : b.Velocity.Flat;
            if (On(PkFeature.Dropkick) && v.FlatLength >= T.DropkickMinSpeed && EnemyAhead(b, w, v, 0.4f + v.FlatLength * b.Dt * 2f, out EnemyInfo e))
            {
                V3 dir = v.Flat.Normalized;
                Pay(b, Math.Min(b.Stamina, T.DropkickStamina), ref o);
                StrikeGroup(ref o, b, w, StrikeKind.Dropkick, e, dir, T.DropkickDamage, T.DropkickStun, 0.2f, T.DropkickKnockSpeed, T.DropkickKnockLift);
                _carry = -dir * T.DropkickRecoil;
                _carryActive = true;
                _airLockUntil = _now + 0.25f;
                o.SetVertical = true;
                o.Vertical = Math.Max(b.Velocity.Y, 2.5f);
                o.ResetFallHeight = true;
                return true;
            }
            return false;
        }

        void EnterVaultEnemy(in BodyState b, in EnemyInfo e, float speed, ref PkOutput o)
        {
            _veId = e.Id;
            _veFeet = e.Feet;
            _veTop = e.Feet.Y + e.Height + T.VaultEnemyClear;
            _veClear = e.Radius + b.Radius + 0.25f;
            _dir = b.LookFlat;
            _speed = speed;
            _jumpedSinceGround = true;
            SetState(PkState.VaultEnemy, "vault-enemy");
            TickVaultEnemy(b, default, null, ref o);
        }

        void TickVaultEnemy(in BodyState b, in PkInput i, IWorldProbe w, ref PkOutput o)
        {
            o.PassThrough = true;
            o.PassThroughTarget = _veId;
            float ahead = V3.Dot((_veFeet - b.Feet).Flat, _dir);   // distance to its centre along our path
            if (-ahead > _veClear || StateTime > 1.0f)
            {
                ExitToAir(_dir * _speed, "vault-enemy-done");
                _blockJumpUntil = _now + 0.25f;
                o.SetHorizontal = true;
                o.Horizontal = _dir * _speed;
                return;
            }
            if (w != null && Stuck(StateTime > 0.1f && b.Velocity.FlatLength < 0.5f)) { ExitToAir(V3.Zero, "vault-enemy-blocked"); return; }
            float t = Math.Max((ahead + _veClear * 0.5f) / Math.Max(_speed, 1f), 0.06f);
            float vy = b.Feet.Y >= _veTop ? 0f : (_veTop - b.Feet.Y) / t;
            o.SetVertical = true;
            o.Vertical = Clamp(vy, -2f, 11f);
            o.SetHorizontal = true;
            o.Horizontal = _dir * _speed;
            // camera: glance down at the head we are hopping over, slight side tilt
            float s = (float)Math.Sin(Math.PI * Math.Min(1f, StateTime / 0.6f));
            o.CamPitch = 14f * s;
            o.CamRoll = 4f * s;
        }

        // ---------------------------------------------------------------- helpers

        void ExitToAir(V3 carry, string evt)
        {
            _carry = carry.Flat;
            _carryActive = true;
            SetState(PkState.None, evt);
        }

        bool Stuck(bool noProgress)
        {
            if (noProgress) _stuck++;
            else _stuck = 0;
            return _stuck >= T.StuckTicks;
        }

        /// <summary>Below ClimbMinStamina the body is spent: no pulling up, no running up walls.</summary>
        bool Exhausted(in BodyState b) => b.Stamina < T.ClimbMinStamina;

        bool PayClimb(in BodyState b, float cost, ref PkOutput o) => !Exhausted(b) && Pay(b, cost, ref o);

        bool Pay(in BodyState b, float cost, ref PkOutput o)
        {
            cost = Cost(cost);
            if (b.Stamina < cost) return false;
            o.StaminaCost += cost;
            return true;
        }

        static V3 InputDir(in BodyState b, in PkInput i)
        {
            V3 right = V3.Cross(V3.Up, b.LookFlat).Normalized;
            V3 d = b.LookFlat * i.MoveY + right * i.MoveX;
            return d.SqrLength > 1f ? d.Normalized : d;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        static float Smooth(float k)
        {
            k = Clamp(k, 0f, 1f);
            return k * k * (3f - 2f * k);
        }
    }
}
