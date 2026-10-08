using System.Linq;
using ParkourMovement.Core;
using Xunit;
using Xunit.Abstractions;

namespace ParkourMovement.Tests
{
    public class CombatAndLowSlideTests
    {
        readonly ITestOutputHelper _log;
        public CombatAndLowSlideTests(ITestOutputHelper log) { _log = log; }

        static BoxWorld Floor() => new BoxWorld().Add(new Box(-100, -1, -100, 100, 0, 200));

        static EnemyInfo Zombie(int id, float x, float y, float z) =>
            new EnemyInfo { Id = id, Feet = new V3(x, y, z), Height = 1.8f, Radius = 0.3f };

        void Dump(BoxSim s) =>
            _log.WriteLine($"t={s.Time:0.00} pos={s.Pos} vel={s.Measured} state={s.Motor.State} events=[{string.Join(", ", s.Events)}] strikes={s.Strikes.Count}");

        [Fact]
        public void Slide_into_zombie_tackles_it()
        {
            var w = Floor();
            w.EnemyList.Add(Zombie(7, 0, 0, 11));
            var s = new BoxSim(w, new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            for (int k = 0; k < 60 && s.Strikes.Count == 0; k++) s.Step(held);
            Dump(s);
            Assert.Single(s.Strikes);
            Assert.Equal(StrikeKind.Tackle, s.Strikes[0].Strike);
            Assert.Equal(7, s.Strikes[0].StrikeTarget);
            Assert.True(s.Strikes[0].StrikeStun > 0f);
            Assert.True(s.Strikes[0].StrikeDir.Z > 0.9f);
        }

        [Fact]
        public void Same_zombie_is_not_hit_every_tick()
        {
            var w = Floor();
            w.EnemyList.Add(Zombie(7, 0, 0, 11));
            var s = new BoxSim(w, new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            s.Run(70, held);
            Assert.Single(s.Strikes);
        }

        [Fact]
        public void Running_jump_into_zombie_is_a_dropkick_with_recoil()
        {
            var w = Floor();
            w.EnemyList.Add(Zombie(9, 0, 0, 9.5f));
            var s = new BoxSim(w, new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            // jump from far enough that we are in the air when we reach it (not the vault-over range)
            s.Step(BoxSim.Sprint(jump: true));
            for (int k = 0; k < 60 && s.Strikes.Count == 0; k++) s.Step(BoxSim.Sprint());
            Dump(s);
            Assert.Equal(StrikeKind.Dropkick, s.Strikes.Single().Strike);
            s.Run(3, BoxSim.Sprint());
            Assert.True(s.Measured.Z < 0f, $"recoil vz {s.Measured.Z}");
        }

        [Fact]
        public void Falling_on_a_head_is_a_stomp_with_bounce()
        {
            var w = Floor();
            w.EnemyList.Add(Zombie(3, 0, 0, 0));
            var s = new BoxSim(w, new V3(0, 3.5f, 0));
            s.Grounded = false;
            for (int k = 0; k < 60 && s.Strikes.Count == 0; k++) s.Step(new PkInput());
            Assert.Equal(StrikeKind.Stomp, s.Strikes.Single().Strike);
            Assert.True(s.Strikes[0].ResetFallHeight);
            s.Step(new PkInput());
            Dump(s);
            Assert.True(s.Measured.Y > 3f, $"bounce vy {s.Measured.Y}");
        }

        [Fact]
        public void Sprint_jump_at_zombie_vaults_over_it()
        {
            var w = Floor();
            w.EnemyList.Add(Zombie(5, 0, 0, 8));
            var s = new BoxSim(w, new V3(0, 0, 0));
            s.Hold(0.9f, BoxSim.Sprint());
            for (int k = 0; k < 40 && s.Pos.Z < 6.2f; k++) s.Step(BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            float maxY = 0f;
            bool passThrough = false;
            for (int k = 0; k < 80; k++)
            {
                s.Step(BoxSim.Sprint());
                maxY = System.Math.Max(maxY, s.Pos.Y);
                if (s.Pos.Z > 7.5f && s.Pos.Z < 8.5f) passThrough |= s.LastOut.PassThrough || s.Pos.Y > 1.9f;
            }
            Dump(s);
            _log.WriteLine($"max y {maxY}");
            Assert.Contains("vault-enemy", s.Events);
            Assert.Contains("vault-enemy-done", s.Events);
            Assert.True(maxY > 1.9f, $"max y {maxY}");
            Assert.True(s.Pos.Z > 9.5f);
            Assert.Empty(s.Strikes);
        }

        [Fact]
        public void Knocked_down_zombies_are_ignored()
        {
            var w = Floor();
            var z = Zombie(7, 0, 0, 11); z.Down = true;
            w.EnemyList.Add(z);
            var s = new BoxSim(w, new V3(0, 0, 0));
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            s.Run(40, held);
            Assert.Empty(s.Strikes);
        }

        [Fact]
        public void Injured_legs_block_slide_wallrun_and_vault()
        {
            var s = new BoxSim(Floor().Add(new Box(0.8f, 0, -10, 1.8f, 6, 60)), new V3(0.2f, 0, 0));
            s.LegsInjured = true;
            s.Hold(1.0f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            Assert.Equal(PkState.None, s.Motor.State);
            s.Run(5, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(jump: true));
            s.Run(40, BoxSim.Sprint());
            Assert.DoesNotContain("wallrun-right", s.Events);
            Assert.DoesNotContain("slide", s.Events);
        }

        // 1 m high opening: ceiling block from y=1 to y=2, z 8..12
        static BoxWorld LowGap() => Floor().Add(new Box(-5, 1.0f, 8, 5, 2.0f, 12));

        [Fact]
        public void Slide_passes_under_a_1m_gap()
        {
            var s = new BoxSim(LowGap(), new V3(0, 0, 0));
            s.Hold(0.8f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            for (int k = 0; k < 150 && s.Pos.Z < 12.5f; k++) s.Step(held);
            Dump(s);
            Assert.True(s.Pos.Z > 12.4f, $"pos {s.Pos}");
            Assert.Contains("slide-under", s.Events);
            Assert.False(s.World.Overlaps(s.Pos, BoxSim.R, s.BodyH), "body inside the ceiling");
        }

        [Fact]
        public void Releasing_crouch_under_the_gap_keeps_you_low_and_crawling()
        {
            var s = new BoxSim(LowGap(), new V3(0, 0, 0));
            s.Hold(0.8f, BoxSim.Sprint());
            s.Step(BoxSim.Sprint(crouch: true));
            var held = BoxSim.Sprint(); held.CrouchHeld = true;
            for (int k = 0; k < 100 && s.Pos.Z < 9.5f; k++) s.Step(held);
            Assert.True(s.Pos.Z >= 9.5f && s.Pos.Z < 12f, $"pos {s.Pos}");
            var walk = new PkInput { MoveY = 1f };   // crouch released, still under the ceiling
            s.Run(10, walk);
            Assert.Equal(PkState.Slide, s.Motor.State);
            Assert.True(s.BodyH < 1.0f, $"body height {s.BodyH}");
            for (int k = 0; k < 300 && s.Pos.Z < 12.6f; k++) s.Step(walk);
            s.Run(5, walk);
            Dump(s);
            Assert.True(s.Pos.Z > 12.4f, $"crawled out {s.Pos}");
            Assert.Contains("crawl-out", s.Events);
            Assert.False(s.World.Overlaps(s.Pos, BoxSim.R, s.BodyH));
        }
    }
}
