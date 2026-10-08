using System.Linq;
using ParkourMovement.Core;
using Xunit;
using Xunit.Abstractions;

namespace ParkourMovement.Tests
{
    public class MotorScenarioTests
    {
        readonly ITestOutputHelper _log;
        public MotorScenarioTests(ITestOutputHelper log) { _log = log; }

        static BoxWorld Floor() => new BoxWorld().Add(new Box(-100, -1, -100, 100, 0, 200));

        void Dump(BoxSim s) => _log.WriteLine($"t={s.Time:0.00} pos={s.Pos} vel={s.Measured} state={s.Motor.State} events=[{string.Join(", ", s.Events)}]");

        [Fact]
        public void Slide_from_sprint_is_faster_than_running_then_ends()
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            Assert.InRange(s.Measured.FlatLength, 5.5f, 6.5f);
            s.Step(BoxSim.Sprint(crouch: true));
            Assert.Equal(PkState.Slide, s.Motor.State);
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            s.Run(5, held);
            Assert.True(s.Measured.FlatLength > BoxSim.RunSpeed + 0.5f, $"slide speed {s.Measured.FlatLength}");
            float z0 = s.Pos.Z;
            s.Hold(2.0f, held);
            Dump(s);
            Assert.Equal(PkState.None, s.Motor.State);
            Assert.Contains("slide-end", s.Events);
            Assert.True(s.Pos.Z - z0 > 4f);
        }

        [Fact]
        public void Slide_needs_speed()
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0));
            var walk = new PkInput { MoveY = 1f };
            s.Hold(1.0f, walk);
            walk.CrouchPressed = walk.CrouchHeld = true;
            s.Step(walk);
            Assert.Equal(PkState.None, s.Motor.State);
        }

        [Fact]
        public void Slide_jump_keeps_boosted_momentum_in_air()
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            s.Run(3, held);
            var jump = held; jump.JumpPressed = jump.JumpHeld = true;
            s.Step(jump);
            Assert.Contains("slide-jump", s.Events);
            s.Run(10, BoxSim.Sprint());
            Dump(s);
            Assert.False(s.Grounded);
            Assert.True(s.Measured.FlatLength > BoxSim.RunSpeed + 1f, $"air speed {s.Measured.FlatLength}");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Slide_jump_keeps_momentum_when_ground_flag_lags(int lag)
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0)) { GroundedLag = lag };
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            s.Run(3, held);
            var jump = held; jump.JumpPressed = jump.JumpHeld = true;
            s.Step(jump);
            Assert.Contains("slide-jump", s.Events);
            s.Run(10, BoxSim.Sprint());
            Dump(s);
            Assert.False(s.Grounded);
            Assert.True(s.Measured.FlatLength > BoxSim.RunSpeed + 1f, $"air speed {s.Measured.FlatLength}");
        }

        static BoxWorld WallRight() => Floor().Add(new Box(0.8f, 0, -10, 1.8f, 6, 60));

        [Fact]
        public void Wall_run_along_right_wall()
        {
            var s = new BoxSim(WallRight(), new V3(0.2f, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            float z0 = s.Pos.Z;
            float minY = 99f, maxY = 0f;
            for (int k = 0; k < 60; k++)
            {
                s.Step(BoxSim.Sprint());
                if (s.Motor.State == PkState.WallRun) { minY = System.Math.Min(minY, s.Pos.Y); maxY = System.Math.Max(maxY, s.Pos.Y); }
            }
            Dump(s);
            Assert.Contains("wallrun-right", s.Events);
            Assert.True(s.Pos.Z - z0 > 6f, $"travelled {s.Pos.Z - z0}");
            Assert.True(maxY > 0.8f, $"maxY {maxY}");
        }

        [Fact]
        public void Wall_run_camera_tilts_away_from_wall()
        {
            var s = new BoxSim(WallRight(), new V3(0.2f, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            for (int k = 0; k < 15 && s.Motor.State != PkState.WallRun; k++) s.Step(BoxSim.Sprint());
            Assert.Equal(PkState.WallRun, s.Motor.State);
            Assert.True(s.LastOut.CamRoll < 0f);
        }

        [Fact]
        public void Wall_jump_pushes_away_from_wall()
        {
            var s = new BoxSim(WallRight(), new V3(0.2f, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            for (int k = 0; k < 20 && s.Motor.State != PkState.WallRun; k++) s.Step(BoxSim.Sprint());
            s.Run(10, BoxSim.Sprint());
            Assert.Equal(PkState.WallRun, s.Motor.State);
            s.Step(BoxSim.Sprint(jump: true));
            Assert.Contains("walljump", s.Events);
            s.Run(3, BoxSim.Sprint());
            Dump(s);
            Assert.True(s.Measured.X < -4f, $"vx {s.Measured.X}");
            Assert.True(s.Measured.Y > 2f, $"vy {s.Measured.Y}");
        }

        [Fact]
        public void Wall_jump_between_two_walls_chains()
        {
            var w = WallRight().Add(new Box(-4.8f, 0, -10, -3.8f, 8, 60));
            var s = new BoxSim(w, new V3(0.2f, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            for (int k = 0; k < 20 && s.Motor.State != PkState.WallRun; k++) s.Step(BoxSim.Sprint());
            s.Run(8, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            for (int k = 0; k < 40 && !s.Events.Contains("wallrun-left"); k++) s.Step(BoxSim.Sprint());
            Dump(s);
            Assert.Contains("wallrun-left", s.Events);
        }

        static BoxWorld LedgeAhead(float top) => Floor().Add(new Box(-5, 0, 3, 5, top, 8));

        [Fact]
        public void Wall_climb_then_mantle_onto_2_5m_ledge()
        {
            var s = new BoxSim(LedgeAhead(2.5f), new V3(0, 0, 0));
            s.Hold(0.4f, BoxSim.Sprint());
            var jump = BoxSim.Sprint(jump: true);
            s.Step(jump);
            var hold = BoxSim.Sprint(); hold.JumpHeld = true;
            for (int k = 0; k < 100 && !(s.Grounded && s.Pos.Y > 2.4f); k++) s.Step(hold);
            s.Run(10, new PkInput());
            Dump(s);
            Assert.Contains("wallclimb", s.Events);
            Assert.Contains("mantle", s.Events);
            Assert.True(s.Pos.Y > 2.45f && s.Pos.Z > 3.1f, $"pos {s.Pos}");
        }

        [Fact]
        public void Exhausted_cannot_pull_up_a_2m_wall()
        {
            var s = new BoxSim(LedgeAhead(2.0f), new V3(0, 0, 1.0f));
            s.Stamina = 5f;
            s.Hold(0.3f, new PkInput { MoveY = 1f });
            s.Step(new PkInput { MoveY = 1f, JumpHeld = true, JumpPressed = true });
            s.Hold(1.0f, new PkInput { MoveY = 1f, JumpHeld = true });
            s.Hold(1.0f, new PkInput { MoveY = 1f });
            Dump(s);
            Assert.DoesNotContain("mantle", s.Events);
            Assert.DoesNotContain("wallclimb", s.Events);
            Assert.True(s.Pos.Y < 1.0f, $"pos {s.Pos}");
            // rested: the same wall works
            s.Stamina = 100f;
            s.Hold(0.5f, new PkInput());
            s.Step(new PkInput { MoveY = 1f, JumpHeld = true, JumpPressed = true });
            for (int k = 0; k < 120 && !(s.Grounded && s.Pos.Y > 1.9f); k++) s.Step(new PkInput { MoveY = 1f });
            Assert.True(s.Pos.Y > 1.9f, $"rested pos {s.Pos}");
        }

        [Fact]
        public void Exhausted_hang_cannot_climb_up()
        {
            var s = HangFrom(LedgeAhead(4.0f), 4.0f);
            s.Stamina = 8f;
            s.Run(5, new PkInput());
            s.Hold(1.0f, new PkInput { MoveY = 1f });
            s.Step(new PkInput { JumpPressed = true, JumpHeld = true });
            Dump(s);
            Assert.DoesNotContain("mantle", s.Events);
            Assert.Equal(PkState.Hang, s.Motor.State);
        }

        [Fact]
        public void Jump_into_1_8m_ledge_mantles()
        {
            var s = new BoxSim(LedgeAhead(1.8f), new V3(0, 0, 1.0f));
            s.Hold(0.3f, new PkInput { MoveY = 1f });
            var jump = new PkInput { MoveY = 1f, JumpHeld = true, JumpPressed = true };
            s.Step(jump);
            var fwd = new PkInput { MoveY = 1f };
            for (int k = 0; k < 120 && !(s.Grounded && s.Pos.Y > 1.7f); k++) s.Step(fwd);
            Dump(s);
            Assert.True(s.Events.Contains("mantle") || s.Events.Contains("wallclimb"));
            Assert.True(s.Pos.Y > 1.75f, $"pos {s.Pos}");
        }

        [Fact]
        public void Hang_on_high_ledge_then_climb_up()
        {
            // ledge top 4.0, drop the body in front of it with feet ~2.1 below the top
            var s = new BoxSim(LedgeAhead(4.0f), new V3(0, 1.9f, 2.55f));
            s.Grounded = false;
            var fwd = new PkInput { MoveY = 1f };
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(fwd);
            Assert.Equal(PkState.Hang, s.Motor.State);
            s.Run(30, new PkInput());
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.InRange(s.Pos.Y, 2.13f, 2.43f);
            s.Step(new PkInput { JumpPressed = true, JumpHeld = true });
            for (int k = 0; k < 100 && !(s.Grounded && s.Pos.Y > 3.9f && s.Motor.State == PkState.None); k++) s.Step(fwd);
            Dump(s);
            Assert.True(s.Pos.Y > 3.95f && s.Pos.Z > 3.1f, $"pos {s.Pos}");
        }

        [Fact]
        public void Hang_at_an_angle_does_not_slide_along_the_edge()
        {
            // looking 35 degrees off the wall normal when the ledge is grabbed
            var s = new BoxSim(LedgeAhead(4.0f), new V3(0, 1.9f, 2.55f));
            s.Grounded = false;
            float a = 35f * (float)System.Math.PI / 180f;
            s.LookFlat = new V3((float)System.Math.Sin(a), 0f, (float)System.Math.Cos(a));
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(PkState.Hang, s.Motor.State);
            float x0 = s.Pos.X;
            s.Run(100, new PkInput());
            Dump(s);
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.True(System.Math.Abs(s.Pos.X - x0) < 0.05f, $"drifted {s.Pos.X - x0:0.00} m along the edge");
        }

        [Fact]
        public void Hang_drains_stamina_and_lets_go_when_empty()
        {
            var s = new BoxSim(LedgeAhead(4.0f), new V3(0, 1.9f, 2.55f));
            s.Grounded = false;
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(PkState.Hang, s.Motor.State);
            float st0 = s.Stamina;
            s.Hold(2f, new PkInput());
            float hold = st0 - s.Stamina;
            s.Hold(2f, new PkInput { MoveX = 1f });
            float shimmy = st0 - s.Stamina - hold;
            _log.WriteLine($"2 s hang {hold:0.00}, 2 s shimmy {shimmy:0.00}");
            Assert.InRange(hold, 2.5f, 3.5f);
            Assert.True(shimmy > hold * 1.5f, $"shimmy {shimmy} vs hold {hold}");
            s.Stamina = 0.5f;
            s.Hold(1.5f, new PkInput());
            Assert.Contains("hang-exhausted", s.Events);
            Assert.True(s.Grounded, $"pos {s.Pos}");
        }

        static BoxSim HangFrom(BoxWorld w, float top)
        {
            var s = new BoxSim(w, new V3(0, top - 2.1f, 2.55f));
            s.Grounded = false;
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(PkState.Hang, s.Motor.State);
            return s;
        }

        [Fact]
        public void Hang_W_press_climbs_up()
        {
            var s = HangFrom(LedgeAhead(4.0f), 4.0f);
            s.Run(10, new PkInput());
            Assert.Equal(PkState.Hang, s.Motor.State);
            s.Hold(1.5f, new PkInput { MoveY = 1f });
            Dump(s);
            Assert.Contains("mantle", s.Events);
            Assert.True(s.Pos.Y > 3.95f, $"pos {s.Pos}");
        }

        [Fact]
        public void Hang_W_held_from_the_run_in_climbs_after_a_moment()
        {
            var s = HangFrom(LedgeAhead(4.0f), 4.0f);
            s.Run(5, new PkInput { MoveY = 1f });
            Assert.Equal(PkState.Hang, s.Motor.State);    // grabbed first, no instant climb
            s.Hold(1.5f, new PkInput { MoveY = 1f });
            Assert.Contains("mantle", s.Events);
        }

        [Fact]
        public void Hang_S_steps_down_to_a_lower_ledge()
        {
            // upper ledge top 5.0, a 1.5 m recess under it, sill top 2.5 (window-like)
            var w = Floor().Add(new Box(-5, 0, 3, 5, 2.5f, 4.5f)).Add(new Box(-5, 2.5f, 3.8f, 5, 4.0f, 4.5f)).Add(new Box(-5, 4.0f, 3, 5, 5.0f, 4.5f));
            var s = HangFrom(w, 5.0f);
            s.Run(20, new PkInput());
            s.Hold(0.4f, new PkInput { MoveY = -1f });
            s.Hold(1.0f, new PkInput());
            Dump(s);
            Assert.Contains("hang-down", s.Events);
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.InRange(s.Pos.Y, 2.5f - T0.HangHeight - 0.1f, 2.5f - T0.HangHeight + 0.1f);
        }

        [Fact]
        public void Hang_S_on_a_flat_wall_lets_go()
        {
            var s = HangFrom(LedgeAhead(4.0f), 4.0f);
            s.Run(20, new PkInput());
            s.Hold(0.4f, new PkInput { MoveY = -1f });
            s.Hold(1.5f, new PkInput());
            Dump(s);
            Assert.Contains("hang-letgo", s.Events);
            Assert.True(s.Grounded && s.Pos.Y < 0.05f, $"pos {s.Pos}");
        }

        [Fact]
        public void Hang_S_plus_jump_still_kicks_off()
        {
            var s = HangFrom(LedgeAhead(4.0f), 4.0f);
            s.Run(20, new PkInput());
            s.Step(new PkInput { MoveY = -1f });
            s.Step(new PkInput { MoveY = -1f, JumpPressed = true, JumpHeld = true });
            Assert.DoesNotContain("hang-letgo", s.Events);
            Assert.NotEqual(PkState.Hang, s.Motor.State);
        }

        [Fact]
        public void Hang_goes_round_an_outer_corner()
        {
            // the block ends at x = 0.6; its edge continues round the side face (normal +X)
            var s = HangFrom(Floor().Add(new Box(-5, 0, 3, 0.6f, 4f, 8)), 4.0f);
            s.Run(10, new PkInput());
            s.Hold(2.5f, new PkInput { MoveX = 1f });
            Dump(s);
            Assert.Contains("hang-corner-out", s.Events);
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.True(s.Motor.TryFacingLock(out V3 f) && f.X < -0.95f, $"facing {f}");
            Assert.InRange(s.Pos.X, 0.8f, 1.1f);
            Assert.True(s.Pos.Z > 3.8f, $"kept shimmying along the side, pos {s.Pos}");
            Assert.InRange(s.Pos.Y, 4.0f - T0.HangHeight - 0.1f, 4.0f - T0.HangHeight + 0.1f);
        }

        [Fact]
        public void Hang_goes_round_an_inner_corner()
        {
            // a second wall at x = 1 turns towards us, same top height
            var w = Floor().Add(new Box(-5, 0, 3, 5, 4f, 8)).Add(new Box(1f, 0, -2, 5, 4f, 3));
            var s = HangFrom(w, 4.0f);
            s.Run(10, new PkInput());
            s.Hold(2.5f, new PkInput { MoveX = 1f });
            Dump(s);
            Assert.Contains("hang-corner-in", s.Events);
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.True(s.Motor.TryFacingLock(out V3 f) && f.X > 0.95f, $"facing {f}");
            Assert.InRange(s.Pos.X, 0.5f, 0.75f);
            Assert.True(s.Pos.Z < 2.0f, $"kept shimmying along the new wall, pos {s.Pos}");
        }

        [Fact]
        public void Hang_at_a_blocked_corner_stops_at_the_end()
        {
            // the way round the end is blocked by a pillar next to the corner: stop at the end, keep hanging
            var w = Floor().Add(new Box(-5, 0, 3, 0.6f, 4f, 8)).Add(new Box(0.75f, 0, 1.5f, 3, 3.0f, 2.95f));
            var s = HangFrom(w, 4.0f);
            s.Run(10, new PkInput());
            s.Hold(2.0f, new PkInput { MoveX = 1f });
            Dump(s);
            Assert.DoesNotContain("hang-corner-out", s.Events);
            Assert.Equal(PkState.Hang, s.Motor.State);
            Assert.True(s.Pos.X < 0.7f, $"pos {s.Pos}");
        }

        static readonly Tuning T0 = new Tuning();

        [Fact]
        public void Hang_drop_with_crouch()
        {
            var s = new BoxSim(LedgeAhead(4.0f), new V3(0, 1.9f, 2.55f));
            s.Grounded = false;
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(new PkInput { MoveY = 1f });
            s.Step(new PkInput { CrouchPressed = true, CrouchHeld = true });
            s.Run(60, new PkInput());
            Assert.Contains("hang-drop", s.Events);
            Assert.True(s.Grounded && s.Pos.Y < 0.05f);
        }

        [Fact]
        public void Vault_over_1m_obstacle_keeps_speed()
        {
            var w = Floor().Add(new Box(-5, 0, 6, 5, 1.0f, 6.6f));
            var s = new BoxSim(w, new V3(0, 0, 0));
            float minSpeed = 99f;
            for (int k = 0; k < 80; k++)
            {
                s.Step(BoxSim.Sprint());
                if (s.Time > 1.0f && s.Pos.Z > 4.5f && s.Pos.Z < 7.5f) minSpeed = System.Math.Min(minSpeed, s.Measured.FlatLength);
            }
            Dump(s);
            _log.WriteLine($"min speed over obstacle {minSpeed}");
            Assert.True(minSpeed > 4f, $"min speed {minSpeed}");
            Assert.Contains("vault", s.Events);
            Assert.True(s.Pos.Z > 7.5f, $"pos {s.Pos}");
            Assert.True(s.Measured.FlatLength > 4.5f, $"speed {s.Measured.FlatLength}");
        }

        [Fact]
        public void Vault_then_release_forward_stays_on_top()
        {
            // 1 m high, 3 m deep block: the player lets go of forward once over the edge and stays up there
            var w = Floor().Add(new Box(-5, 0, 6, 5, 1.0f, 9f));
            var s = new BoxSim(w, new V3(0, 0, 0));
            for (int k = 0; k < 150 && !s.Events.Contains("vault-done"); k++) s.Step(BoxSim.Sprint());
            Assert.Contains("vault-done", s.Events);
            s.Run(60, new PkInput());
            Dump(s);
            Assert.True(s.Grounded && s.Pos.Y > 0.95f && s.Pos.Z > 6f && s.Pos.Z < 9f, $"pos {s.Pos}");
        }

        [Fact]
        public void Vault_cancels_when_forward_is_released_while_rising()
        {
            var w = Floor().Add(new Box(-5, 0, 6, 5, 1.0f, 6.6f));
            var s = new BoxSim(w, new V3(0, 0, 0));
            for (int k = 0; k < 150 && s.Motor.State != PkState.Mantle; k++) s.Step(BoxSim.Sprint());
            Assert.Equal(PkState.Mantle, s.Motor.State);
            s.Run(5, new PkInput());
            s.Run(60, new PkInput());
            Dump(s);
            Assert.Contains("vault-cancel", s.Events);
            Assert.True(s.Pos.Z < 6f && s.Pos.Y < 0.1f, $"pos {s.Pos}");
        }

        [Fact]
        public void Landing_roll_cuts_fall_damage()
        {
            var s = new BoxSim(Floor(), new V3(0, 8f, 0));
            s.Grounded = false;
            var fwd = new PkInput { MoveY = 1f };
            // fall until ~0.25 s before impact, then press crouch
            while (s.Pos.Y > 3.5f) s.Step(fwd);
            s.Step(new PkInput { MoveY = 1f, CrouchPressed = true, CrouchHeld = true });
            for (int k = 0; k < 100 && !s.Grounded; k++) s.Step(fwd);
            s.Step(fwd);
            Dump(s);
            Assert.Equal(0.25f, s.LastLandingScale, 3);
            Assert.Equal(PkState.Roll, s.Motor.State);
            s.Run(5, fwd);
            Assert.True(s.LastOut.CamPitchSpin > 0f);
            s.Run(40, fwd);
            Assert.Contains("roll-done", s.Events);
        }

        [Fact]
        public void No_roll_without_crouch_means_full_damage()
        {
            var s = new BoxSim(Floor(), new V3(0, 8f, 0));
            s.Grounded = false;
            for (int k = 0; k < 200 && !s.Grounded; k++) s.Step(new PkInput());
            Assert.Equal(1f, s.LastLandingScale);
        }

        [Fact]
        public void Coyote_jump_after_walking_off_edge()
        {
            var w = new BoxWorld().Add(new Box(-5, -1, -5, 5, 0, 2)).Add(new Box(-50, -20, -50, 50, -19, 50));
            var s = new BoxSim(w, new V3(0, 0, 0));
            var run = BoxSim.Sprint();
            for (int k = 0; k < 200 && s.Grounded; k++) s.Step(run);
            s.Run(3, run);
            Assert.False(s.Grounded);
            s.Step(BoxSim.Sprint(jump: true));
            Assert.Contains("coyote-jump", s.Events);
            s.Step(run);
            Assert.True(s.Measured.Y > 3f);
        }

        [Fact]
        public void Air_momentum_survives_without_input()
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            s.Run(15, new PkInput());
            Assert.False(s.Grounded);
            Assert.True(s.Measured.FlatLength > 5f, $"air speed {s.Measured.FlatLength}");
        }

        [Fact]
        public void Disabled_motor_never_overrides()
        {
            var s = new BoxSim(WallRight(), new V3(0.2f, 0, 0));
            s.T.Enabled = false;
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            s.Run(30, BoxSim.Sprint());
            Assert.Equal(PkState.None, s.Motor.State);
            Assert.DoesNotContain(s.Events, e => e != null);
        }

        [Fact]
        public void No_stamina_no_slide()
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0));
            s.Stamina = 0f;
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            Assert.Equal(PkState.None, s.Motor.State);
        }

        [Fact]
        public void Tuning_loads_from_xml()
        {
            var t = new Tuning();
            string err = t.LoadXml("<parkour><property name=\"SlideFriction\" value=\"2.5\"/><property name=\"Enabled\" value=\"false\"/><property name=\"Nope\" value=\"1\"/></parkour>");
            Assert.Equal(2.5f, t.SlideFriction);
            Assert.False(t.Enabled);
            Assert.Contains("Nope", err);
        }
    }
}
