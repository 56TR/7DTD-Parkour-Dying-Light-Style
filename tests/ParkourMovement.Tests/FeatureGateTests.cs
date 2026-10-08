using ParkourMovement.Core;
using Xunit;
using Xunit.Abstractions;

namespace ParkourMovement.Tests
{
    /// <summary>Moves switched off in the settings or locked behind the Parkour perk, and the perk bonuses.</summary>
    public class FeatureGateTests
    {
        readonly ITestOutputHelper _log;
        public FeatureGateTests(ITestOutputHelper log) { _log = log; }

        static BoxWorld Floor() => new BoxWorld().Add(new Box(-100, -1, -100, 100, 0, 200));
        static BoxWorld LedgeAhead(float top) => Floor().Add(new Box(-5, 0, 3, 5, top, 8));

        void Dump(BoxSim s) => _log.WriteLine($"t={s.Time:0.00} pos={s.Pos} state={s.Motor.State} events=[{string.Join(", ", s.Events)}]");

        static BoxSim SprintSlide(PkFeature blocked)
        {
            var s = new BoxSim(Floor(), new V3(0, 0, 0)) { Blocked = blocked };
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint();
            held.CrouchHeld = true;
            s.Run(5, held);
            return s;
        }

        [Fact]
        public void Slide_off_means_no_slide()
        {
            var s = SprintSlide(PkFeature.Slide);
            Dump(s);
            Assert.DoesNotContain("slide", s.Events);
            Assert.Equal(PkState.None, s.Motor.State);
        }

        [Fact]
        public void Other_features_off_keep_the_slide()
        {
            var s = SprintSlide(PkFeature.WallRun | PkFeature.Hang | PkFeature.Tackle);
            Assert.Contains("slide", s.Events);
        }

        [Fact]
        public void Slide_jump_off_gives_no_boosted_hop()
        {
            var s = SprintSlide(PkFeature.SlideJump);
            var jump = BoxSim.Sprint();
            jump.CrouchHeld = true;
            jump.JumpPressed = jump.JumpHeld = true;
            s.Step(jump);
            Dump(s);
            Assert.DoesNotContain("slide-jump", s.Events);
            Assert.Contains("slide-end", s.Events);
        }

        [Fact]
        public void Wall_run_off()
        {
            var s = new BoxSim(Floor().Add(new Box(0.8f, 0, -10, 1.8f, 6, 60)), new V3(0.2f, 0, 0)) { Blocked = PkFeature.WallRun };
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            s.Run(60, BoxSim.Sprint());
            Dump(s);
            Assert.DoesNotContain("wallrun-right", s.Events);
        }

        [Fact]
        public void Mantle_off_but_hang_on_grabs_the_edge_instead()
        {
            var s = new BoxSim(LedgeAhead(1.8f), new V3(0, 0, 1.0f)) { Blocked = PkFeature.Mantle | PkFeature.WallClimb };
            s.Hold(0.3f, new PkInput { MoveY = 1f });
            s.Step(new PkInput { MoveY = 1f, JumpHeld = true, JumpPressed = true });
            s.Hold(1.5f, new PkInput { MoveY = 1f });
            Dump(s);
            Assert.DoesNotContain("mantle", s.Events);
            Assert.True(s.Pos.Y < 1.0f, $"did not get on top, pos {s.Pos}");
        }

        [Fact]
        public void Mantle_and_hang_off_nothing_happens_at_a_ledge()
        {
            var s = new BoxSim(LedgeAhead(1.8f), new V3(0, 0, 1.0f)) { Blocked = PkFeature.Mantle | PkFeature.Hang | PkFeature.WallClimb };
            s.Hold(0.3f, new PkInput { MoveY = 1f });
            s.Step(new PkInput { MoveY = 1f, JumpHeld = true, JumpPressed = true });
            s.Hold(1.5f, new PkInput { MoveY = 1f });
            Dump(s);
            Assert.DoesNotContain("mantle", s.Events);
            Assert.DoesNotContain("hang", s.Events);
            Assert.True(s.Grounded && s.Pos.Y < 0.1f, $"pos {s.Pos}");
        }

        [Fact]
        public void Landing_roll_off_means_full_damage_even_with_crouch()
        {
            var s = new BoxSim(Floor(), new V3(0, 8f, 0)) { Blocked = PkFeature.LandingRoll };
            s.Grounded = false;
            while (s.Pos.Y > 3.5f) s.Step(new PkInput { MoveY = 1f });
            s.Step(new PkInput { MoveY = 1f, CrouchPressed = true, CrouchHeld = true });
            for (int k = 0; k < 100 && !s.Grounded; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(1f, s.LastLandingScale);
        }

        [Fact]
        public void Auto_roll_rolls_without_crouch()
        {
            var s = new BoxSim(Floor(), new V3(0, 8f, 0)) { Bonus = PkBonus.AutoRoll };
            s.Grounded = false;
            for (int k = 0; k < 200 && !s.Grounded; k++) s.Step(new PkInput { MoveY = 1f });
            s.Step(new PkInput { MoveY = 1f });
            Dump(s);
            Assert.Equal(0.25f, s.LastLandingScale, 3);
            Assert.Equal(PkState.Roll, s.Motor.State);
        }

        [Fact]
        public void Auto_roll_needs_landing_roll_enabled()
        {
            var s = new BoxSim(Floor(), new V3(0, 8f, 0)) { Bonus = PkBonus.AutoRoll, Blocked = PkFeature.LandingRoll };
            s.Grounded = false;
            for (int k = 0; k < 200 && !s.Grounded; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(1f, s.LastLandingScale);
        }

        [Fact]
        public void Efficient_halves_the_slide_cost()
        {
            var plain = SprintSlide(PkFeature.None);
            var eff = new BoxSim(Floor(), new V3(0, 0, 0)) { Bonus = PkBonus.Efficient };
            eff.Hold(1.0f, BoxSim.Sprint());
            eff.Step(BoxSim.Sprint(crouch: true));
            float paidPlain = 100f - plain.Stamina, paidEff = 100f - eff.Stamina;
            _log.WriteLine($"plain {paidPlain} efficient {paidEff}");
            Assert.True(paidPlain > 0f);
            Assert.Equal(paidPlain * 0.5f, paidEff, 3);
        }

        [Fact]
        public void Hang_free_drains_nothing()
        {
            var s = new BoxSim(LedgeAhead(4.0f), new V3(0, 1.9f, 2.55f)) { Bonus = PkBonus.HangFree };
            s.Grounded = false;
            for (int k = 0; k < 40 && s.Motor.State != PkState.Hang; k++) s.Step(new PkInput { MoveY = 1f });
            Assert.Equal(PkState.Hang, s.Motor.State);
            float st = s.Stamina;
            s.Hold(2f, new PkInput());
            s.Hold(1f, new PkInput { MoveX = 1f });
            Assert.Equal(st, s.Stamina, 3);
        }
    }
}
