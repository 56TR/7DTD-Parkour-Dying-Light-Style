using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ParkourMovement.Core;
using ParkourMovement.Patches;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>
    /// In-game self test. Builds a steel course high in the sky, drives the local player with scripted input
    /// and checks what the motor and the real controller did. "pk test" from the console, or fully automatic:
    /// env PARKOUR_AUTOTEST=all (or a scenario name) starts a world from the main menu, runs, writes
    /// PARKOUR_AUTOTEST_OUT (or Parkour_results.txt in the save dir) and quits the game.
    /// </summary>
    public static class AutoTest
    {
        static bool _armed, _started, _running;
        public static bool Armed => _armed && !_started;
        public static bool Starting => _armed && _started && !_running;
        public static bool Running => _running;
        static string _filter = "all";
        static string _outFile;
        static string _shotDir;

        static EntityPlayerLocal P;
        static Vector3 _startPos;
        static Vector3 B; // world position of course local origin (floor top, corner)

        sealed class Result
        {
            public string Name;
            public bool Pass = true;
            public readonly List<string> Notes = new List<string>();
            public void Check(bool ok, string what)
            {
                Notes.Add((ok ? "ok   " : "FAIL ") + what);
                if (!ok) Pass = false;
            }
            public void Note(string s) => Notes.Add("     " + s);
        }

        public static void Init()
        {
            string v = Environment.GetEnvironmentVariable("PARKOUR_AUTOTEST");
            if (string.IsNullOrEmpty(v)) return;
            _armed = true;
            _filter = v == "1" ? "all" : v;
            _outFile = Environment.GetEnvironmentVariable("PARKOUR_AUTOTEST_OUT");
            ModEvents.MainMenuOpened.RegisterHandler(OnMainMenu);
            Log.Out("[Parkour][TEST] autotest armed: " + _filter);
        }

        static void OnMainMenu(ref ModEvents.SMainMenuOpenedData d)
        {
            if (!_armed || _started) return;
            _started = true;
            ThreadManager.StartCoroutine(AutoStartCo());
        }

        static IEnumerator AutoStartCo()
        {
            yield return new WaitForSeconds(3f);
            string world = Environment.GetEnvironmentVariable("PARKOUR_AUTOTEST_WORLD");
            if (string.IsNullOrEmpty(world)) world = "Navezgane";
            // remember prefs we touch: client prefs may be shared with the user's normal game
            string oldWorld = GamePrefs.GetString(EnumGamePrefs.GameWorld);
            string oldName = GamePrefs.GetString(EnumGamePrefs.GameName);
            bool oldSkip = GamePrefs.GetBool(EnumGamePrefs.SkipSpawnButton);
            GamePrefs.Set(EnumGamePrefs.GameWorld, world);
            GamePrefs.Set(EnumGamePrefs.GameName, "PKAutoTest");
            GamePrefs.Set(EnumGamePrefs.SkipSpawnButton, true);
            string connect = Environment.GetEnvironmentVariable("PARKOUR_AUTOTEST_CONNECT");
            NetworkConnectionError err = NetworkConnectionError.NoError;
            if (!string.IsNullOrEmpty(connect))
            {
                // join someone else's (e.g. vanilla, mod-less) server as a client instead of hosting
                string[] hp = connect.Split(':');
                var gsi = new GameServerInfo();
                gsi.SetValue(GameInfoString.IP, hp[0]);
                gsi.SetValue(GameInfoInt.Port, hp.Length > 1 ? int.Parse(hp[1]) : 26900);
                GameManager.Instance.showOpenerMovieOnLoad = false;
                SingletonMonoBehaviour<ConnectionManager>.Instance.LastGameServerInfo = gsi;
                SingletonMonoBehaviour<ConnectionManager>.Instance.LastJoinSource = "Direct Connection";
                Log.Out("[Parkour][TEST] connecting to " + connect);
                SingletonMonoBehaviour<ConnectionManager>.Instance.Connect(gsi);
            }
            else
            {
                Log.Out("[Parkour][TEST] starting world " + world);
                err = SingletonMonoBehaviour<ConnectionManager>.Instance.StartServers(GamePrefs.GetString(EnumGamePrefs.ServerPassword), _offline: false);
            }
            if (err != NetworkConnectionError.NoError)
            {
                WriteResults(new List<Result> { new Result { Name = "startup", Pass = false } }, "StartServers failed: " + err);
                Application.Quit();
                yield break;
            }
            float t0 = Time.realtimeSinceStartup;
            while (true)
            {
                EntityPlayerLocal p = GameManager.Instance.World?.GetPrimaryPlayer();
                if (p != null && p.Spawned) break;
                if (Time.realtimeSinceStartup - t0 > 900f)
                {
                    WriteResults(new List<Result> { new Result { Name = "startup", Pass = false } }, "player never spawned");
                    Application.Quit();
                    yield break;
                }
                yield return null;
            }
            Log.Out($"[Parkour][TEST] player spawned after {Time.realtimeSinceStartup - t0:0}s");
            GamePrefs.Set(EnumGamePrefs.GameWorld, oldWorld);
            GamePrefs.Set(EnumGamePrefs.GameName, oldName);
            GamePrefs.Set(EnumGamePrefs.SkipSpawnButton, oldSkip);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PARKOUR_BODYCHECK")))
            {
                // third-person body right after spawn, before the test touches anything
                P = GameManager.Instance.World.GetPrimaryPlayer();
                yield return new WaitForSeconds(3f);
                P.SetFirstPersonView(false, _bLerpPosition: false);
                yield return new WaitForSeconds(3f);
                Log.Out("[Parkour][BODY] after spawn: " + BodyInfo());
                _shotDir = Path.GetDirectoryName(ResultPath());
                Shot("3p_spawn");
                yield return new WaitForSeconds(1f);
                P.SetFirstPersonView(true, _bLerpPosition: false);
            }
            Protect(GameManager.Instance.World.GetPrimaryPlayer(), true);
            yield return new WaitForSeconds(5f);
            StartSuite(_filter, quitWhenDone: true);
        }

        public static void StartSuite(string filter, bool quitWhenDone)
        {
            if (_running) { Log.Warning("[Parkour][TEST] already running"); return; }
            ThreadManager.StartCoroutine(SuiteCo(string.IsNullOrEmpty(filter) ? "all" : filter, quitWhenDone));
        }

        static readonly string[] Order =
        {
            "slide", "slidejump", "wallrun", "walljump", "climb", "jumpmantle", "vault", "hang", "roll", "coyote",
            "lowslide", "injured", "tackle", "dropkick", "stomp", "vaultzombie", "thirdperson", "anim3p", "tacklegroup", "remoteloop", "fpslide", "hang3p", "perks", "options",
        };

        static IEnumerator SuiteCo(string filter, bool quit)
        {
            _running = true;
            var results = new List<Result>();
            P = GameManager.Instance.World?.GetPrimaryPlayer();
            if (P == null)
            {
                Log.Error("[Parkour][TEST] no local player");
                _running = false;
                yield break;
            }
            _shotDir = Path.GetDirectoryName(ResultPath());
            _startPos = P.position + Vector3.up * 0.5f;
            if (filter == "playground")
            {
                // a sandbox: the player's own settings, every move unlocked (pk perk -1 -1 = the character's real tiers)
                PerkIntegration.ForceParkourTier = 5;
                PerkIntegration.ForceMasteryTier = 4;
            }
            else
            {
                PkSettings.Transient = true;
                TestSettings();
            }
            if (quit)
            {
                GameManager.Instance.World.SetTimeJump(12000uL, _isSeek: true);
                GameStats.Set(EnumGameStats.TimeOfDayIncPerSec, 0);
            }

            if (filter == "playground")
            {
                yield return Playground();
                _running = false;
                yield break;
            }

            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                // client on someone else's server: no course, no world changes, just move and talk
                var mp = new Result { Name = "mpclient" };
                yield return Guard(MpClient(mp), mp);
                ScriptedInput.Active = false;
                ScriptedInput.Clear();
                results.Add(mp);
                WriteResults(results, null);
                _running = false;
                if (quit)
                {
                    yield return new WaitForSeconds(1f);
                    Application.Quit();
                }
                yield break;
            }

            // the sky course has no support: stop structural integrity while the suite runs
            bool oldStability = GameStats.GetBool(EnumGameStats.ChunkStabilityEnabled);
            bool oldPhysics = GameManager.bPhysicsActive;
            GameStats.Set(EnumGameStats.ChunkStabilityEnabled, false);
            GameManager.bPhysicsActive = false;
            bool oldFreeze = GamePrefs.GetBool(EnumGamePrefs.DebugStopEnemiesMoving);
            DumpArms();
            var setup = new Result { Name = "setup" };
            if (quit) Protect(P, true);
            yield return Guard(BuildCourse(setup), setup);
            if (quit) Protect(P, false); // course is 200 m up: nothing can reach us, and fall damage must be real
            results.Add(setup);
            if (setup.Pass)
            {
                foreach (string name in Order)
                {
                    if (P.IsDead()) { Log.Error("[Parkour][TEST] player died, aborting suite"); break; }
                    if (filter != "all" && !filter.Split(',').Contains(name)) continue;
                    var r = new Result { Name = name };
                    Log.Out("[Parkour][TEST] >>> " + name);
                    PkRuntime.Motor.History.Clear();
                    int ticks0 = PkRuntime.TickCount;
                    yield return Guard(Scenario(name, r), r);
                    ScriptedInput.Clear();
                    r.Note("events: " + string.Join(" ", PkRuntime.Motor.History));
                    if (!r.Pass) r.Note("trace:\n" + TraceText(70));
                    r.Note($"diag: physics ticks {PkRuntime.TickCount - ticks0}, motor ticks {PkRuntime.Motor.Ticks}, block reason {PkRuntime.BlockReason ?? "none"}, alive {!P.IsDead()}");
                    results.Add(r);
                    Log.Out($"[Parkour][TEST] {(r.Pass ? "PASS" : "FAIL")} {name}\n    " + string.Join("\n    ", r.Notes));
                    yield return new WaitForSeconds(0.3f);
                }
            }
            ScriptedInput.Active = false;
            ScriptedInput.Clear();
            if (!quit)
            {
                // back to where the player started, then remove the course before block physics return
                P.TeleportToPosition(_startPos, false, null);
                yield return new WaitForSeconds(1f);
                RemoveCourse();
                yield return new WaitForSeconds(0.5f);
            }
            GameStats.Set(EnumGameStats.ChunkStabilityEnabled, oldStability);
            GameManager.bPhysicsActive = oldPhysics;
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, oldFreeze);
            DespawnAll();
            WriteResults(results, null);
            // the player's own saved settings and real perk tiers again
            PkSettings.Transient = false;
            PkSettings.ResetAll();
            PkSettings.Load();
            PerkIntegration.ForceParkourTier = PerkIntegration.ForceMasteryTier = -1;
            _running = false;
            if (quit)
            {
                yield return new WaitForSeconds(1f);
                Application.Quit();
            }
        }

        /// <summary>Log the first-person arms rig so hand IK can target the right bones.</summary>
        public static void DumpArms()
        {
            try
            {
                var alpc = P.emodel?.avatarController as AvatarLocalPlayerController;
                Animator a = alpc?.FPSArms?.Animator;
                if (a == null) { Log.Out("[Parkour][ARMS] no fps arms animator"); return; }
                var sb = new StringBuilder();
                sb.AppendLine($"[Parkour][ARMS] animator {a.name} human={a.isHuman} avatar={(a.avatar != null ? a.avatar.name : "none")} root={a.transform.name}");
                if (a.isHuman)
                    foreach (HumanBodyBones hb in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                                                         HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand })
                    {
                        Transform t = a.GetBoneTransform(hb);
                        sb.AppendLine($"  {hb} -> {(t != null ? t.name : "null")}");
                    }
                int lines = 0;
                void Walk(Transform t, int depth)
                {
                    if (lines++ > 160 || depth > 12) return;
                    sb.AppendLine(new string(' ', depth * 2) + t.name);
                    for (int k = 0; k < t.childCount; k++) Walk(t.GetChild(k), depth + 1);
                }
                Walk(a.transform, 0);

                // third-person body: rig type, parameters, layers, current states
                Animator ba = alpc?.CharacterBody?.Animator;
                if (ba != null)
                {
                    sb.AppendLine($"[Parkour][BODY] animator {ba.name} human={ba.isHuman} avatar={(ba.avatar != null ? ba.avatar.name : "none")} controller={(ba.runtimeAnimatorController != null ? ba.runtimeAnimatorController.name : "none")} layers={ba.layerCount}");
                    foreach (AnimatorControllerParameter prm in ba.parameters) sb.AppendLine($"  param {prm.type} {prm.name}");
                    for (int l = 0; l < ba.layerCount; l++) sb.AppendLine($"  layer {l} {ba.GetLayerName(l)} w={ba.GetLayerWeight(l):0.00}");
                    if (ba.runtimeAnimatorController != null)
                        foreach (AnimationClip clip in ba.runtimeAnimatorController.animationClips)
                            if (clip != null) sb.AppendLine($"  clip {clip.name} {clip.length:0.00}s");
                }
                Log.Out(sb.ToString());
                if (ba != null) ExportSkeleton(ba.transform, Path.Combine(Path.GetDirectoryName(ResultPath()), "skeleton_3p.json"));
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour][ARMS] dump failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Write the third-person skeleton (hierarchy, current local pose, bind pose from a skinned mesh) as JSON,
        /// so animations can be retargeted onto it in Blender.
        /// </summary>
        static void ExportSkeleton(Transform root, string path)
        {
            var bind = new Dictionary<Transform, Matrix4x4>();
            // the body meshes are not children of the animator transform; search the whole entity model
            Component searchRoot = (Component)P?.emodel ?? root;
            foreach (SkinnedMeshRenderer smr in searchRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || smr.bones == null) continue;
                Matrix4x4[] bp = smr.sharedMesh.bindposes;
                for (int k = 0; k < smr.bones.Length && k < bp.Length; k++)
                    if (smr.bones[k] != null && !bind.ContainsKey(smr.bones[k]))
                        // rest pose of the bone, in the animator root's space: root <- mesh <- bone(bind)
                        bind[smr.bones[k]] = root.worldToLocalMatrix * smr.transform.localToWorldMatrix * bp[k].inverse;
            }
            var sb = new StringBuilder();
            sb.Append("{\"root\":\"").Append(root.name).Append("\",\"bones\":[");
            bool first = true;
            void Walk(Transform t, string parent)
            {
                if (!first) sb.Append(',');
                first = false;
                Vector3 p = t.localPosition, s = t.localScale;
                Quaternion q = t.localRotation;
                sb.Append($"{{\"name\":\"{t.name}\",\"parent\":\"{parent}\",\"pos\":[{F(p.x)},{F(p.y)},{F(p.z)}],\"rot\":[{F(q.x)},{F(q.y)},{F(q.z)},{F(q.w)}],\"scale\":[{F(s.x)},{F(s.y)},{F(s.z)}]");
                if (bind.TryGetValue(t, out Matrix4x4 m))
                {
                    sb.Append(",\"bind\":[");
                    for (int k = 0; k < 16; k++) sb.Append(k > 0 ? "," : "").Append(F(m[k]));
                    sb.Append(']');
                }
                sb.Append('}');
                for (int k = 0; k < t.childCount; k++) Walk(t.GetChild(k), t.name);
            }
            Walk(root, "");
            sb.Append("]}");
            File.WriteAllText(path, sb.ToString());
            Log.Out($"[Parkour][BODY] skeleton exported: {path} ({bind.Count} skinned bones)");
        }

        static string F(float v) => v.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

        static IEnumerator Guard(IEnumerator inner, Result r)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext()) break;
                    cur = inner.Current;
                }
                catch (Exception ex)
                {
                    r.Check(false, "exception: " + ex);
                    break;
                }
                yield return cur;
            }
        }

        static string ResultPath()
        {
            if (!string.IsNullOrEmpty(_outFile)) return _outFile;
            return Path.Combine(GameIO.GetSaveGameDir(), "Parkour_results.txt");
        }

        static void WriteResults(List<Result> results, string fatal)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Parkour self test {DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{ModApi.Version}");
            if (fatal != null) sb.AppendLine("FATAL " + fatal);
            foreach (var r in results)
            {
                sb.AppendLine($"{(r.Pass ? "PASS" : "FAIL")} {r.Name}");
                foreach (string n in r.Notes) sb.AppendLine("    " + n);
            }
            int pass = results.Count(r => r.Pass);
            sb.AppendLine($"SUMMARY {pass}/{results.Count} passed");
            string path = ResultPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("[Parkour][TEST] can't write results: " + ex.Message);
            }
            Log.Out("[Parkour][TEST] results -> " + path + "\n" + sb);
        }

        /// <summary>Autotest safety: god buff, no enemy spawns, kill what is already around.</summary>
        static void Protect(EntityPlayerLocal p, bool on)
        {
            if (p == null) return;
            if (on)
            {
                p.Buffs.AddBuff("god");
                GameStats.Set(EnumGameStats.IsSpawnEnemies, false);
                foreach (Entity e in new List<Entity>(GameManager.Instance.World.Entities.list))
                    if (e is EntityEnemy || e is EntityAnimal)
                        e.DamageEntity(new DamageSource(EnumDamageSource.Internal, EnumDamageTypes.Suicide), 99999, _criticalHit: false);
            }
            else p.Buffs.RemoveBuff("god");
        }

        // ------------------------------------------------------------------ course

        static readonly List<Vector3i> _placed = new List<Vector3i>();

        static void RemoveCourse()
        {
            if (_placed.Count == 0) return;
            var list = new List<BlockChangeInfo>();
            foreach (var pos in _placed) list.Add(new BlockChangeInfo(pos, BlockValue.Air, true));
            GameManager.Instance.World.SetBlocksRPC(list);
            _placed.Clear();
        }

        static readonly string[] BlockNames = { "steelShapes:cube", "concreteShapes:cube", "cobblestoneShapes:cube", "woodShapes:cube" };

        static IEnumerator BuildCourse(Result r)
        {
            BlockValue bv = BlockValue.Air;
            foreach (string n in BlockNames)
            {
                if (Block.GetBlockByName(n) == null) continue;
                bv = Block.GetBlockValue(n);
                r.Note("block " + n);
                break;
            }
            if (bv.isair) { r.Check(false, "no solid block found"); yield break; }

            Vector3 pp = P.position;
            var o = new Vector3i(Mathf.FloorToInt(pp.x) - 12, 200, Mathf.FloorToInt(pp.z) - 28);
            B = new Vector3(o.x, o.y, o.z);
            var list = new List<BlockChangeInfo>();
            void Fill(int x0, int y0, int z0, int x1, int y1, int z1)
            {
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                            list.Add(new BlockChangeInfo(new Vector3i(o.x + x, o.y + y, o.z + z), bv, true));
            }
            Fill(0, -1, 0, 23, -1, 55);       // floor
            Fill(-1, 0, -1, -1, 2, 56);       // side rails
            Fill(24, 0, -1, 24, 2, 56);
            Fill(0, 0, -1, 23, 2, -1);        // end rails
            Fill(0, 0, 56, 23, 2, 56);
            Fill(8, 0, 10, 8, 8, 40);         // wall run: right wall of lane B
            Fill(4, 0, 10, 4, 8, 40);         //           left wall of lane B
            Fill(11, 0, 12, 15, 1, 13);       // 2 m wall: climb / mantle
            Fill(17, 0, 12, 21, 0, 12);       // 1 m obstacle: vault
            Fill(17, 0, 30, 21, 4, 31);       // 5 m wall: hang
            Fill(11, 0, 30, 15, 1, 35);       // 2 m platform: coyote
            Fill(11, 1, 47, 15, 1, 50);       // slab 1 m above the floor: slide under it
            GameManager.Instance.World.SetBlocksRPC(list);
            _placed.Clear();
            foreach (var bci in list) _placed.Add((Vector3i)bci.blockValueRef);
            r.Note($"placed {list.Count} blocks at {o}");

            // stand on the course and wait for colliders
            P.TeleportToPosition(B + new Vector3(1.5f, 0.2f, 1.5f), false, new Vector3(0f, 0f, 0f));
            float t0 = Time.time;
            bool ok = false;
            while (Time.time - t0 < 60f)
            {
                yield return new WaitForSeconds(0.5f);
                if (!P.Spawned) continue;
                Vector3 probe = B + new Vector3(19.5f, 8f, 30.5f) - Origin.position;
                if (Physics.Raycast(probe, Vector3.down, out RaycastHit h, 10f) && Mathf.Abs(h.point.y - (B.y + 5f - Origin.position.y)) < 0.1f
                    && Physics.Raycast(B + new Vector3(2.5f, 1f, 50f) - Origin.position, Vector3.down, 3f))
                {
                    ok = true;
                    break;
                }
            }
            r.Check(ok, $"course colliders ready after {Time.time - t0:0.0}s");
            yield return new WaitForSeconds(1f);
        }

        // ------------------------------------------------------------------ helpers

        static Vector3 Local => P.position - B;
        static vp_FPController C => P.vp_FPController;
        static float FlatSpeed => new Vector2(PkRuntime.Body.Velocity.X, PkRuntime.Body.Velocity.Z).magnitude;
        static bool Grounded => C != null && C.Grounded;

        static void In(float fwd = 0f, bool run = false, bool jump = false, bool crouch = false, float strafe = 0f)
        {
            ScriptedInput.Active = true;
            ScriptedInput.Forward = fwd;
            ScriptedInput.Strafe = strafe;
            ScriptedInput.Run = run;
            ScriptedInput.Jump = jump;
            ScriptedInput.Crouch = crouch;
        }

        static void Refill()
        {
            P.Stats.Health.Value = P.Stats.Health.ModifiedMax;
            P.Stats.Stamina.Value = P.Stats.Stamina.ModifiedMax;
            if (P.Buffs?.ActiveBuffs == null) return;
            foreach (var b in P.Buffs.ActiveBuffs.ToList())
            {
                string n = b.BuffName ?? "";
                if (n.IndexOf("injury", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("fall", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0)
                    P.Buffs.RemoveBuff(n);
            }
        }

        static IEnumerator Place(Vector3 local, float yaw, bool settle = true)
        {
            Vector3 w = B + local;
            P.SetPosition(w);
            C.SetPosition(w - Origin.position);
            C.Stop();
            P.SetRotation(new Vector3(0f, yaw, 0f));
            PkRuntime.ResetTracking();
            Refill();
            if (!settle) yield break;
            In();
            for (int k = 0; k < 5; k++) yield return null;
            yield return new WaitForSeconds(0.5f);
            Refill();
        }

        static IEnumerator Watch(float seconds, Action each)
        {
            float t = 0f;
            while (t < seconds)
            {
                each?.Invoke();
                yield return null;
                t += Time.deltaTime;
            }
        }

        static IEnumerator Until(Func<bool> cond, float timeout, Action each = null)
        {
            float t = 0f;
            while (!cond() && t < timeout)
            {
                each?.Invoke();
                yield return null;
                t += Time.deltaTime;
            }
        }

        static IEnumerator Press(Action<bool> set, float seconds = 0.12f)
        {
            set(true);
            yield return new WaitForSeconds(seconds);
            set(false);
        }

        static void Shot(string name)
        {
            try
            {
                string f = Path.Combine(_shotDir ?? ".", "pk_" + name + ".png");
                ScreenCapture.CaptureScreenshot(f);
            }
            catch (Exception) { }
        }

        static string TraceText(int n)
        {
            var sb = new StringBuilder();
            for (int k = n - 1; k >= 0; k--)
            {
                var t = PkRuntime.Trace[(PkRuntime.TraceHead - k + PkRuntime.Trace.Length * 4) % PkRuntime.Trace.Length];
                Vector3 l = t.Feet + Origin.position - B;
                sb.AppendLine($"       {t.Time:0.00} {t.State,-9} g={(t.Grounded ? 1 : 0)} local=({l.x:0.00},{l.y:0.00},{l.z:0.00}) v=({t.Vel.x:0.0},{t.Vel.y:0.0},{t.Vel.z:0.0})");
            }
            return sb.ToString();
        }

        /// <summary>Capture a numbered series of frames (filmstrip of a move's camera animation).</summary>
        static IEnumerator ShotSeries(string name, int count, float interval)
        {
            for (int k = 0; k < count; k++)
            {
                Shot($"{name}_{k:00}");
                yield return new WaitForSeconds(interval);
            }
        }

        static bool Saw(string evt) => PkRuntime.Motor.History.Contains(evt);

        // ------------------------------------------------------------------ scenarios

        static IEnumerator Scenario(string name, Result r)
        {
            switch (name)
            {
                case "slide": return Slide(r, jump: false);
                case "slidejump": return Slide(r, jump: true);
                case "wallrun": return WallRun(r, jump: false);
                case "walljump": return WallRun(r, jump: true);
                case "climb": return Climb(r);
                case "jumpmantle": return JumpMantle(r);
                case "vault": return Vault(r);
                case "hang": return Hang(r);
                case "roll": return Roll(r);
                case "coyote": return Coyote(r);
                case "lowslide": return LowSlide(r);
                case "thirdperson": return ThirdPerson(r);
                case "anim3p": return Anim3P(r);
                case "tacklegroup": return TackleGroup(r);
                case "remoteloop": return RemoteLoop(r);
                case "fpslide": return FpSlide(r);
                case "hang3p": return Hang3p(r);
                case "perks": return Perks(r);
                case "options": return OptionsTab(r);
                case "injured": return Injured(r);
                case "tackle": return Tackle(r);
                case "dropkick": return Dropkick(r);
                case "stomp": return Stomp(r);
                case "vaultzombie": return VaultZombie(r);
            }
            r.Check(false, "unknown scenario");
            return null;
        }

        static IEnumerator Slide(Result r, bool jump)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            float sprint = 0f;
            int n = 0;
            yield return Watch(1.4f, () => { if (Local.z > 4f) { sprint += FlatSpeed; n++; } });
            sprint = n > 0 ? sprint / n : 0f;
            r.Note($"sprint speed {sprint:0.00} m/s");
            r.Check(sprint > 4.5f, "vanilla sprint works");

            In(1f, run: true, crouch: true);
            float max = 0f, slideTime = 0f, airMax = 0f, maxFov = 0f;
            bool shot = false;
            if (!jump)
            {
                yield return Watch(2.6f, () =>
                {
                    if (PkRuntime.Motor.State == PkState.Slide)
                    {
                        max = Mathf.Max(max, FlatSpeed);
                        maxFov = Mathf.Max(maxFov, CameraPatch.CurrentFovAdd);
                        slideTime += Time.deltaTime;
                        if (!shot && slideTime > 0.3f) { Shot("slide"); shot = true; }
                    }
                });
                In();
                r.Note($"slide max speed {max:0.00} m/s, time {slideTime:0.00}s, max cam fov+ {maxFov:0.0}");
                r.Check(maxFov > 3f, "FOV kick during slide");
                r.Check(Saw("slide"), "slide started");
                r.Check(max > sprint + 0.8f, "slide faster than sprint");
                r.Check(slideTime > 0.4f, "slide lasted");
                r.Check(PkRuntime.Motor.State == PkState.None, "slide ended");
            }
            else
            {
                yield return Watch(0.25f, () => max = Mathf.Max(max, FlatSpeed));
                In(1f, run: true, crouch: true, jump: true);
                yield return new WaitForSeconds(0.1f);
                In(1f, run: true);
                float cmdMax = 0f, smMin = 99f;
                yield return Watch(0.6f, () =>
                {
                    if (Grounded) return;
                    airMax = Mathf.Max(airMax, FlatSpeed);
                    var h = PkRuntime.Out.Horizontal;
                    cmdMax = Mathf.Max(cmdMax, new Vector2(h.X, h.Z).magnitude);
                    smMin = Mathf.Min(smMin, C.m_SpeedModifier);
                });
                yield return Until(() => Grounded, 3f);
                In();
                r.Note($"slide speed before jump {max:0.00}, air speed {airMax:0.00} m/s, motor asked {cmdMax:0.00} m/s, speed modifier {smMin:0.00}");
                r.Check(Saw("slide-jump"), "slide jump");
                r.Check(airMax > sprint + 0.5f, "slide jump carries momentum");
            }
        }

        static IEnumerator WallRun(Result r, bool jump)
        {
            yield return Place(new Vector3(7.45f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.6f, 4f);
            In(1f, run: true, jump: true);
            yield return new WaitForSeconds(0.12f);
            In(1f, run: true);
            float runTime = 0f, z0 = -1f, z1 = 0f, maxY = 0f, minRoll = 0f;
            bool shot = false;
            yield return Until(() => PkRuntime.Motor.State == PkState.WallRun, 1.0f);
            if (!jump)
            {
                yield return Watch(1.8f, () =>
                {
                    if (PkRuntime.Motor.State != PkState.WallRun) return;
                    if (z0 < 0f) z0 = Local.z;
                    z1 = Local.z;
                    runTime += Time.deltaTime;
                    maxY = Mathf.Max(maxY, Local.y);
                    minRoll = Mathf.Min(minRoll, CameraPatch.CurrentRoll);
                    if (!shot && runTime > 0.4f) { Shot("wallrun"); shot = true; }
                });
                In();
                yield return Until(() => Grounded, 3f);
                r.Note($"wall run {runTime:0.00}s, {z1 - z0:0.0} m along wall, max height {maxY:0.00} m, camera roll {minRoll:0.0} deg");
                r.Check(Saw("wallrun-right"), "wall run started on the right wall");
                r.Check(runTime > 0.6f, "wall run lasted > 0.6 s");
                r.Check(z1 - z0 > 4f, "wall run covered > 4 m");
                r.Check(minRoll < -5f, "camera tilted away from the wall");
                r.Check(maxY < 2.5f, "wall run stays low (jump height + small arc, no climbing)");
            }
            else
            {
                yield return new WaitForSeconds(0.3f);
                bool wasRunning = PkRuntime.Motor.State == PkState.WallRun;
                float jumpY = Local.y, peakY = Local.y;
                In(1f, run: true, jump: true);
                yield return new WaitForSeconds(0.1f);
                In(1f, run: true);
                float vx = 0f;
                yield return Watch(0.15f, () => { vx = Mathf.Min(vx, PkRuntime.Body.Velocity.X); peakY = Mathf.Max(peakY, Local.y); });
                Shot("walljump");
                yield return Until(() => Saw("wallrun-left") || Grounded, 2.0f, () => { if (!Saw("wallrun-left")) peakY = Mathf.Max(peakY, Local.y); });
                r.Note($"wall jump rise {peakY - jumpY:0.00} m");
                r.Check(peakY - jumpY < 1.3f, "wall jump rises about 1 m (DL2 0.92 m), not 2 m");
                bool chained = Saw("wallrun-left");
                yield return new WaitForSeconds(0.4f);
                In();
                yield return Until(() => Grounded, 3f);
                r.Note($"vx after wall jump {vx:0.00} m/s, chained to left wall: {chained}");
                r.Check(wasRunning, "was wall running before the jump");
                r.Check(Saw("walljump"), "wall jump");
                r.Check(vx < -3f, "pushed away from the wall");
                r.Check(chained, "caught the opposite wall (wall run chain)");
            }
        }

        static IEnumerator Climb(Result r)
        {
            yield return Place(new Vector3(13.5f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 10.9f, 4f);
            In(1f, run: true, jump: true);
            bool shot = false;
            ThreadManager.StartCoroutine(ShotSeries("climbstrip", 12, 0.08f));
            yield return Until(() => Saw("mantle-done"), 2.0f, () =>
            {
                if (!shot && PkRuntime.Motor.State == PkState.Mantle) { Shot("mantle"); shot = true; }
            });
            In();
            yield return new WaitForSeconds(0.2f);
            yield return Until(() => Grounded, 2f);
            Vector3 l = Local;
            r.Note($"end local pos {l}");
            r.Check(Saw("wallclimb") || Saw("mantle"), "climbed");
            r.Check(Saw("mantle"), "mantled");
            r.Check(Grounded && l.y > 1.9f && l.z > 12.2f, "standing on top of the 2 m wall");
        }

        static IEnumerator JumpMantle(Result r)
        {
            yield return Place(new Vector3(13.5f, 0.05f, 11.2f), 0f);
            In(1f, jump: true);
            ThreadManager.StartCoroutine(ShotSeries("jumpmantlestrip", 10, 0.08f));
            yield return new WaitForSeconds(0.1f);
            In(1f);
            yield return Until(() => Saw("mantle-done"), 2.0f);
            In();
            yield return new WaitForSeconds(0.2f);
            yield return Until(() => Grounded, 2f);
            Vector3 l = Local;
            r.Note($"end local pos {l}");
            r.Check(Saw("mantle"), "mantle from a standing jump");
            r.Check(Grounded && l.y > 1.9f, "on top of the 2 m wall");
        }

        static IEnumerator Vault(Result r)
        {
            yield return Place(new Vector3(19.5f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            float minSpeed = 99f;
            bool shot = false;
            yield return Until(() => Local.z > 15f, 5f, () =>
            {
                if (Local.z > 10.5f && Local.z < 13.5f) minSpeed = Mathf.Min(minSpeed, FlatSpeed);
                if (!shot && PkRuntime.Motor.State == PkState.Mantle) { Shot("vault"); shot = true; }
            });
            In();
            Vector3 l = Local;
            r.Note($"end local pos {l}, min speed over obstacle {minSpeed:0.00} m/s");
            r.Check(Saw("vault"), "vaulted");
            r.Check(l.z > 14f, "got over the obstacle");
            r.Check(minSpeed > 2.5f, "kept running speed over the obstacle");
        }

        static IEnumerator Hang(Result r)
        {
            In(1f);
            yield return Place(new Vector3(19.5f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In();
            yield return new WaitForSeconds(0.8f);
            bool hanging = PkRuntime.Motor.State == PkState.Hang;
            float hy = Local.y;
            float errL = ArmIK.ErrorL, errR = ArmIK.ErrorR;
            Shot("hang");
            float x0 = Local.x;
            In(0f, strafe: 1f);
            yield return new WaitForSeconds(0.6f);
            In();
            float dx = Local.x - x0;
            bool stillHanging = PkRuntime.Motor.State == PkState.Hang;
            // look far away from the wall: the body must stay facing it (hands on the edge)
            vp_FPCamera cam = P.vp_FPCamera;
            float yaw0 = cam.m_Yaw;
            cam.m_Yaw = yaw0 + 150f;
            yield return new WaitForSeconds(0.3f);
            float lookOff = Mathf.Abs(Mathf.DeltaAngle(0f, cam.Parent.eulerAngles.y));
            bool heldAfterLook = PkRuntime.Motor.State == PkState.Hang;
            cam.m_Yaw = yaw0;
            yield return new WaitForSeconds(0.1f);
            In(1f, jump: true);
            yield return new WaitForSeconds(0.1f);
            In(1f);
            yield return Until(() => Saw("mantle-done"), 2.0f);
            In();
            yield return new WaitForSeconds(0.2f);
            yield return Until(() => Grounded, 2f);
            Vector3 l = Local;
            r.Note($"hang height (feet) {hy:0.00}, shimmy dx {dx:0.00}, end {l}");
            r.Note($"hand IK bound {ArmIK.Bound}, weights L {ArmIK.WeightL:0.00} R {ArmIK.WeightR:0.00}, hand to ledge error L {errL:0.000} m, R {errR:0.000} m, first person {P.bFirstPersonView}");
            r.Check(Saw("hang") && hanging, "grabbed and held the ledge");
            r.Check(errL >= 0f && errL < 0.1f && errR >= 0f && errR < 0.1f, "both hands on the ledge (IK)");
            r.Check(hy > 3.0f && hy < 3.5f, "hanging below the top");
            r.Check(dx > 0.4f && stillHanging, "shimmied right along the ledge");
            r.Note($"looked 150 deg away while hanging: body {lookOff:0} deg off the wall (limit {PkRuntime.Tuning.HangLookYaw:0})");
            r.Check(heldAfterLook && lookOff <= PkRuntime.Tuning.HangLookYaw + 1f, "body keeps facing the wall when looking away");
            r.Check(Grounded && l.y > 4.9f && l.z > 30.2f, "climbed up onto the 5 m wall");

            // drop off with crouch
            In(1f);
            yield return Place(new Vector3(18.5f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In(0f, crouch: true);
            yield return new WaitForSeconds(0.15f);
            In();
            yield return Until(() => Grounded, 3f);
            r.Check(Saw("hang-drop") && Local.y < 0.3f, "crouch drops from the ledge");

            // round the right end of the wall (outer corner), onto its side face
            In(1f);
            yield return Place(new Vector3(21.2f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In(0f, strafe: 1f);
            yield return Until(() => Saw("hang-corner-out"), 2.5f);
            yield return new WaitForSeconds(1.0f);
            In();
            bool cornerHang = PkRuntime.Motor.State == PkState.Hang;
            PkRuntime.Motor.TryFacingLock(out var cf);
            Vector3 cl = Local;
            float camYaw = P.vp_FPCamera.m_Yaw;
            Shot("hang_corner");
            r.Note($"after the corner: pos {cl}, facing ({cf.X:0.00}, {cf.Z:0.00}), view yaw {camYaw:0}");
            r.Check(Saw("hang-corner-out") && cornerHang && cf.X < -0.9f && cl.x > 22f, "went round the outer corner and kept hanging on the side");
            In(0f, crouch: true);
            yield return new WaitForSeconds(0.15f);
            In();
            yield return Until(() => Grounded, 3f);
        }

        static IEnumerator Roll(Result r)
        {
            // no roll first, as the reference
            In(1f);
            yield return Place(new Vector3(1.5f, 7f, 44f), 0f, settle: false);
            int hp0 = P.Health;
            yield return Until(() => Grounded, 4f);
            float scaleNoRoll = Patches.FallImpactPatch.LastScale;
            float impactNoRoll = Patches.FallImpactPatch.LastSpeedIn;
            yield return new WaitForSeconds(1.5f);
            int lossNoRoll = hp0 - P.Health;

            In(1f);
            yield return Place(new Vector3(1.5f, 7f, 30f), 0f, settle: false);
            hp0 = P.Health;
            yield return Until(() => Local.y < 2.0f, 4f);
            In(1f, crouch: true);
            bool shot = false;
            yield return Until(() => Grounded, 3f);
            yield return Watch(0.6f, () =>
            {
                if (!shot && PkRuntime.Motor.State == PkState.Roll && PkRuntime.Motor.StateTime > 0.2f) { Shot("roll"); shot = true; }
            });
            In(1f);
            float scaleRoll = Patches.FallImpactPatch.LastScale;
            yield return new WaitForSeconds(1.5f);
            In();
            int lossRoll = hp0 - P.Health;
            r.Note($"impact {impactNoRoll:0.000}; hp loss without roll {lossNoRoll} (scale {scaleNoRoll}), with roll {lossRoll} (scale {scaleRoll})");
            r.Check(Saw("roll"), "landing roll");
            r.Check(Mathf.Abs(scaleRoll - PkRuntime.Tuning.RollDamageScale) < 0.001f, "fall impact scaled down");
            r.Check(lossRoll <= lossNoRoll, "roll took no more damage than a plain landing");
            Refill();
        }

        // ------------------------------------------------------------------ low slide, injuries, combat

        static IEnumerator LowSlide(Result r)
        {
            yield return Place(new Vector3(13.5f, 0.05f, 36.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 42.5f, 4f);
            In(1f, run: true, crouch: true);
            float maxY = 0f, minCc = 9f;
            bool shot = false;
            yield return Until(() => Local.z > 51.5f, 6f, () =>   // the slab covers z 47..51: crawl until clear of it
            {
                if (Local.z > 47f && Local.z < 50f)
                {
                    maxY = Mathf.Max(maxY, Local.y);
                    minCc = Mathf.Min(minCc, C.CharacterController.height);
                    if (!shot && Local.z > 48f) { Shot("lowslide"); shot = true; }
                }
            });
            In();
            yield return new WaitForSeconds(0.5f);
            Vector3 l = Local;
            r.Note($"end {l}, max feet height under the slab {maxY:0.00}, min collider {minCc:0.00} m, block reason {PkRuntime.BlockReason ?? "none"}");
            r.Check(Saw("slide-under"), "went under the 1 m slab");
            r.Check(l.z > 51.3f, "came out the other side");
            r.Check(maxY < 0.3f, "stayed on the floor (no push-out through the slab)");
            r.Check(C.CharacterController.height > 1.0f, "collider back to normal after");
        }

        static IEnumerator Injured(Result r)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            P.Buffs.AddBuff("buffLegSprained");
            yield return new WaitForSeconds(0.3f);
            bool flagged = PkRuntime.LegsInjured(P);
            In(1f, run: true);
            float sprint = 0f;
            yield return Watch(1.4f, () => sprint = Mathf.Max(sprint, FlatSpeed));
            In(1f, run: true, crouch: true);
            yield return new WaitForSeconds(1.0f);
            In();
            P.Buffs.RemoveBuff("buffLegSprained");
            Refill();
            yield return new WaitForSeconds(0.3f);
            r.Note($"sprint with sprained leg {sprint:0.00} m/s");
            r.Check(flagged, "sprained leg detected");
            r.Check(!Saw("slide"), "no slide with a sprained leg");
            r.Check(!PkRuntime.LegsInjured(P), "flag clears when the buff is gone");
        }

        static readonly List<EntityAlive> _spawned = new List<EntityAlive>();

        static EntityAlive SpawnZombie(Vector3 local, float yaw)
        {
            foreach (string n in new[] { "zombieBoe", "zombieJoe", "zombieArlene" })
            {
                int cls = EntityClass.FromString(n);
                if (!EntityClass.list.ContainsKey(cls)) continue;
                Entity e = EntityFactory.CreateEntity(cls, B + local, new Vector3(0f, yaw, 0f));
                GameManager.Instance.World.SpawnEntityInWorld(e);
                var ea = e as EntityAlive;
                if (ea != null) _spawned.Add(ea);
                return ea;
            }
            return null;
        }

        static void DespawnAll()
        {
            foreach (var e in _spawned)
                if (e != null && !e.IsDespawned) GameManager.Instance.World.RemoveEntity(e.entityId, EnumRemoveEntityReason.Despawned);
            _spawned.Clear();
        }

        static bool KnockedDown(EntityAlive z) =>
            z != null && (z.bodyDamage.CurrentStun != EnumEntityStunType.None || (z.emodel != null && z.emodel.IsRagdollActive));

        /// <summary>Spawn a frozen zombie, make the player immune to it, run the body, report damage and knockdown.</summary>
        static IEnumerator WithZombie(Result r, Vector3 zLocal, float zYaw, Func<EntityAlive, IEnumerator> body, string expectEvent, bool expectHit)
        {
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, true);
            P.Buffs.AddBuff("god");
            EntityAlive z = SpawnZombie(zLocal, zYaw);
            r.Check(z != null, "zombie spawned");
            if (z == null) yield break;
            yield return new WaitForSeconds(1.5f);   // let it settle on the floor
            int hp0 = z.Health;
            bool down = false;
            float t0 = Time.time;
            ThreadManager.StartCoroutine(WatchDown(z, () => down = true));
            yield return body(z);
            yield return new WaitForSeconds(0.6f);
            int loss = hp0 - (z.IsDead() ? 0 : z.Health);
            r.Note($"zombie {z.EntityName} hp {hp0} -> {(z.IsDead() ? 0 : z.Health)}, knocked down: {down}");
            r.Check(Saw(expectEvent), expectEvent);
            if (expectHit)
            {
                r.Check(loss > 0, "zombie took damage");
                r.Check(down, "zombie knocked down");
            }
            else r.Check(loss == 0, "zombie untouched");
            In();
            P.Buffs.RemoveBuff("god");
            DespawnAll();
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, false);
            yield return new WaitForSeconds(0.5f);
        }

        static IEnumerator WatchDown(EntityAlive z, Action onDown)
        {
            for (float t = 0f; t < 6f && z != null; t += Time.deltaTime)
            {
                if (KnockedDown(z)) { onDown(); yield break; }
                yield return null;
            }
        }

        /// <summary>Where the zombie's body actually is: the ragdoll pelvis while ragdolled, else the entity position.</summary>
        static Vector3 BodyPos(EntityAlive z)
        {
            Rigidbody best = null;
            if (z?.emodel != null)
                foreach (Rigidbody rb in z.emodel.GetComponentsInChildren<Rigidbody>())
                    if (rb != null && (best == null || rb.mass > best.mass)) best = rb;
            return best != null && !best.isKinematic ? best.position + Origin.position : z.position;
        }

        static IEnumerator TackleGroup(Result r)
        {
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, true);
            P.Buffs.AddBuff("god");
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            var zs = new List<EntityAlive>
            {
                SpawnZombie(new Vector3(1.5f, 0.05f, 15f), 180f),
                SpawnZombie(new Vector3(0.8f, 0.05f, 15.9f), 180f),
                SpawnZombie(new Vector3(2.3f, 0.05f, 15.9f), 180f),
            };
            r.Check(zs.TrueForAll(z => z != null), "3 zombies spawned");
            yield return new WaitForSeconds(1.5f);
            var start = zs.ConvertAll(z => z.position);
            int hp0 = 0;
            foreach (var z in zs) hp0 += z.Health;
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.5f, 4f);
            In(1f, run: true, crouch: true);
            yield return Until(() => Saw("tackle"), 2.5f);
            int hits = Combat.LastStrikeCount;
            In(0f, crouch: true);
            float t0 = Time.time;
            foreach (float at in new[] { 0.15f, 0.4f, 0.8f, 1.3f })
            {
                while (Time.time - t0 < at) yield return null;
                yield return SideShot($"tackle_{(int)(at * 100):000}", 3.5f, 0.8f, 3f);
            }
            yield return new WaitForSeconds(0.3f);
            In();
            var sb = new StringBuilder();
            float sum = 0f;
            int knocked = 0;
            for (int k = 0; k < zs.Count; k++)
            {
                Vector3 d = BodyPos(zs[k]) - start[k];
                float flat = new Vector2(d.x, d.z).magnitude;
                sum += flat;
                if (flat > 1.5f) knocked++;
                sb.Append($"z{k}: moved {flat:0.00} m (dz {d.z:0.00}) down={KnockedDown(zs[k])}; ");
            }
            int hp1 = 0;
            foreach (var z in zs) hp1 += z.IsDead() ? 0 : z.Health;
            r.Note($"hit {hits} zombies in one slide; {sb}");
            r.Note($"total hp {hp0} -> {hp1}");
            r.Check(Saw("tackle"), "tackle");
            r.Check(hits >= 2, "slide swept more than one zombie of the group");
            r.Check(knocked >= 2, "at least two zombies thrown back > 1.5 m");
            r.Check(sum / zs.Count > 2f && sum / zs.Count < 5f, "average knock-back about 3 m");
            P.Buffs.RemoveBuff("god");
            DespawnAll();
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, false);
            yield return new WaitForSeconds(0.5f);
        }

        static IEnumerator Tackle(Result r)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            yield return WithZombie(r, new Vector3(1.5f, 0.05f, 15f), 180f, z => TackleRun(), "tackle", expectHit: true);
        }

        static IEnumerator TackleRun()
        {
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.5f, 4f);
            In(1f, run: true, crouch: true);
            yield return Until(() => Saw("tackle"), 2.5f);
            Shot("tackle");
            yield return new WaitForSeconds(0.3f);
            In();
        }

        static IEnumerator Dropkick(Result r)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            yield return WithZombie(r, new Vector3(1.5f, 0.05f, 13f), 180f, z => DropkickRun(), "dropkick", expectHit: true);
        }

        static IEnumerator DropkickRun()
        {
            In(1f, run: true);
            yield return Until(() => Local.z >= 7.6f, 4f);
            In(1f, run: true, jump: true);
            yield return new WaitForSeconds(0.1f);
            In(1f, run: true);
            yield return Until(() => Saw("dropkick") || (Grounded && Local.z > 9f), 2.5f);
            Shot("dropkick");
            In();
        }

        static IEnumerator Stomp(Result r)
        {
            yield return Place(new Vector3(13.5f, 0.05f, 17f), 0f);
            yield return WithZombie(r, new Vector3(13.5f, 0.05f, 22f), 180f, z => StompRun(z, r), "stomp", expectHit: true);
        }

        static IEnumerator StompRun(EntityAlive z, Result r)
        {
            // drop onto its head from 1.5 m above it
            Vector3 head = z.position - B;
            r.Note($"zombie local pos {head}, bbox y {z.boundingBox.min.y - B.y:0.00}..{z.boundingBox.max.y - B.y:0.00}");
            yield return Place(new Vector3(head.x, z.boundingBox.max.y - B.y + 1.5f, head.z), 0f, settle: false);
            yield return Until(() => Saw("stomp") || Grounded, 2.5f);
            Shot("stomp");
        }

        static IEnumerator VaultZombie(Result r)
        {
            yield return Place(new Vector3(19.5f, 0.05f, 13.6f), 0f);
            yield return WithZombie(r, new Vector3(19.5f, 0.05f, 22f), 180f, z => VaultZombieRun(z), "vault-enemy", expectHit: false);
        }

        static IEnumerator VaultZombieRun(EntityAlive z)
        {
            In(1f, run: true);
            yield return Until(() => Local.z >= 19.9f, 4f);
            In(1f, run: true, jump: true);
            yield return new WaitForSeconds(0.1f);
            In(1f, run: true);
            bool shot = false;
            float maxY = 0f;
            yield return Until(() => Local.z > 24f, 2.5f, () =>
            {
                maxY = Mathf.Max(maxY, Local.y);
                if (!shot && PkRuntime.Motor.State == PkState.VaultEnemy && Local.z > 21.5f) { Shot("vaultzombie"); shot = true; }
            });
            In();
            yield return Until(() => Grounded, 2f);
            Log.Out($"[Parkour][TEST] vault over zombie: end {Local}, max height {maxY:0.00}");
        }

        static string BodyInfo()
        {
            var alpc = P.emodel?.avatarController as AvatarLocalPlayerController;
            BodyAnimator body = alpc?.CharacterBody;
            if (body == null) return "no character body";
            int on = 0, total = 0;
            if (body.skinnedMeshes != null)
                foreach (var m in body.skinnedMeshes) { total++; if (m != null && m.enabled && m.gameObject.activeInHierarchy) on++; }
            int rOn = 0, rVis = 0, rTot = 0;
            if (P.emodel != null)
                foreach (Renderer rr in P.emodel.GetComponentsInChildren<Renderer>(true))
                {
                    rTot++;
                    if (rr.enabled && rr.gameObject.activeInHierarchy) rOn++;
                    if (rr.isVisible) rVis++;
                }
            float camDist = P.playerCamera != null ? (P.playerCamera.transform.position - (P.position - Origin.position)).magnitude : -1f;
            return $"body state {body.state}, meshes enabled {on}/{total}, renderers on {rOn} visible {rVis} of {rTot}, camera {camDist:0.00} m from feet, FPV {P.bFirstPersonView}";
        }

        static IEnumerator ThirdPerson(Result r)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            P.SetFirstPersonView(false, _bLerpPosition: false);
            yield return new WaitForSeconds(1.0f);
            string idle1s = BodyInfo();
            yield return new WaitForSeconds(4.0f);
            string idle = BodyInfo();
            r.Note("idle after 1 s: " + idle1s);
            Shot("3p_idle");
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.5f, 4f);
            In(1f, run: true, crouch: true);
            yield return new WaitForSeconds(0.3f);
            string sliding = BodyInfo();
            Shot("3p_slide");
            yield return new WaitForSeconds(1.0f);
            In();
            yield return new WaitForSeconds(0.5f);
            string after = BodyInfo();

            // same thing with parkour switched off, to tell vanilla from mod problems
            PkRuntime.Tuning.Enabled = false;
            PkRuntime.Motor.Reset();
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            yield return new WaitForSeconds(1.0f);
            string vanilla = BodyInfo();
            Shot("3p_vanilla");
            PkRuntime.Tuning.Enabled = true;
            P.SetFirstPersonView(true, _bLerpPosition: false);
            yield return new WaitForSeconds(0.5f);
            r.Note("idle: " + idle);
            r.Note("sliding: " + sliding);
            r.Note("after: " + after);
            r.Note("parkour off: " + vanilla);
            r.Check(idle.Contains("Visible") && sliding.Contains("Visible") && after.Contains("Visible"), "body visible in third person");
        }

        /// <summary>The suite tests the moves themselves: defaults, not saved, no perk gating, no perk bonuses.</summary>
        static void TestSettings()
        {
            PkSettings.ResetAll();
            PkSettings.Set("PerkIntegration", false, save: false);
            PkSettings.Set("AutoRoll", false, save: false);
            PkSettings.Set("Efficient", false, save: false);
            PkSettings.Set("HangFree", false, save: false);
            PerkIntegration.ForceParkourTier = PerkIntegration.ForceMasteryTier = -1;
        }

        static bool _slid;

        /// <summary>Sprint and crouch on the flat start: did a slide start?</summary>
        static IEnumerator TrySlide()
        {
            PkRuntime.Motor.History.Clear();
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return new WaitForSeconds(1.2f);
            In(1f, run: true, crouch: true);
            yield return new WaitForSeconds(0.4f);
            _slid = Saw("slide");
            In();
            yield return new WaitForSeconds(1.2f);
        }

        static float _jumpRise;

        /// <summary>A standing vanilla jump on the flat start: how high do the feet get?</summary>
        static IEnumerator MeasureJump()
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            yield return new WaitForSeconds(0.5f);
            float y0 = Local.y, top = y0;
            In(0f, jump: true);
            yield return new WaitForSeconds(0.1f);
            In();
            yield return Watch(1.2f, () => top = Mathf.Max(top, Local.y));
            yield return Until(() => Grounded, 2f);
            _jumpRise = top - y0;
        }

        static int _fallLoss;
        static string _fallInfo;

        /// <summary>Drop 14 m onto the flat start lane and report the health lost.</summary>
        static IEnumerator Fall14()
        {
            P.Health = P.GetMaxHealth();
            yield return new WaitForSeconds(0.3f);
            int hp0 = P.Health;
            Log.Out($"[Parkour][TEST] fall14 start, option {PkSettings.NoFallImmunity}, tier {PerkIntegration.RealParkourTier}, hp {hp0}");
            yield return Place(new Vector3(1.5f, 14f, 3f), 0f, settle: false);
            yield return Until(() => Local.y < 1.5f, 5f);   // right after the teleport the grounded flag is still the old one
            yield return Until(() => Grounded, 3f);
            Log.Out($"[Parkour][TEST] fall14 grounded at y {Local.y:0.00}");
            yield return new WaitForSeconds(1.5f);
            Log.Out($"[Parkour][TEST] fall14 end, hp {P.Health}");
            _fallLoss = hp0 - P.Health;
            _fallInfo = $"real tier {PerkIntegration.RealParkourTier}, option {PkSettings.NoFallImmunity}, impact speed {Patches.FallImpactPatch.LastSpeedIn:0.000}, $parkourBonus {P.Buffs.GetCustomVar("$parkourBonus"):0.0000}, extra damage {PerkIntegration.LastNoImmunityDamage}, _fallSpeed {P.Buffs.GetCustomVar("_fallSpeed"):0.000}";
            P.Health = P.GetMaxHealth();
        }

        static void Tiers(int parkour, int mastery)
        {
            PerkIntegration.ForceParkourTier = parkour;
            PerkIntegration.ForceMasteryTier = mastery;
        }

        /// <summary>Parkour perk integration: unlocks per tier, auto-roll, vanilla jump / fall tweaks, perk texts.</summary>
        static IEnumerator Perks(Result r)
        {
            PkSettings.ResetAll();   // defaults: integration and bonuses on

            // moves unlock with the tier
            Tiers(0, 0);
            yield return new WaitForSeconds(0.7f);
            PkFeature b0 = PerkIntegration.Blocked;
            yield return TrySlide();
            bool slideT0 = _slid;
            Tiers(1, 0);
            yield return new WaitForSeconds(0.7f);
            PkFeature b1 = PerkIntegration.Blocked;
            yield return TrySlide();
            bool slideT1 = _slid;
            r.Note($"tier 0 blocked [{b0}] | tier 1 blocked [{b1}]");
            r.Check(!slideT0 && (b0 & PkFeature.Mantle) == 0 && (b0 & PkFeature.Slide) != 0, "no perk: mantle only, no slide");
            r.Check(slideT1 && (b1 & PkFeature.Hang) != 0, "tier 1: slide unlocked, hang still locked");
            Tiers(5, 0);
            yield return new WaitForSeconds(0.7f);
            r.Check(PerkIntegration.Blocked == PkFeature.None && (PerkIntegration.Bonus & (PkBonus.Efficient | PkBonus.HangFree)) != 0
                    && (PerkIntegration.Bonus & PkBonus.AutoRoll) == 0, "tier 5: everything, tier 5 bonuses, no auto-roll without Agility Mastery 4");
            PkSettings.Set("WallRun", false, save: false);
            yield return new WaitForSeconds(0.1f);
            r.Check((PerkIntegration.Blocked & PkFeature.WallRun) != 0, "a move switched off in the options stays off at tier 5");
            PkSettings.Set("WallRun", true, save: false);

            // auto-roll: Agility Mastery 4, no crouch pressed
            Tiers(1, 4);
            yield return new WaitForSeconds(0.7f);
            PkRuntime.Motor.History.Clear();
            In(1f);
            yield return Place(new Vector3(1.5f, 7f, 30f), 0f, settle: false);
            yield return Until(() => Saw("roll"), 4f);
            bool autoRolled = Saw("roll");
            float autoScale = Patches.FallImpactPatch.LastScale;
            In();
            yield return new WaitForSeconds(1.0f);
            r.Note($"auto-roll: rolled {autoRolled}, impact scale {autoScale}, events {string.Join(" ", PkRuntime.Motor.History)}");
            r.Check(autoRolled, "Agility Mastery 4: landing roll without crouch");

            // perk texts follow the settings
            PerkIntegration.Descriptions.Refresh(force: true);
            string r1 = Localization.Get("perkParkourRank1LongDesc"), r2 = Localization.Get("perkParkourRank2LongDesc");
            string r3 = Localization.Get("perkParkourRank3LongDesc"), r5 = Localization.Get("perkParkourRank5LongDesc");
            string m4 = Localization.Get("perkAgilityMasteryRank4LongDesc");
            r.Note("rank 2 text: " + r2.Replace("\n", " | "));
            r.Check(r1.Contains("Slide:") && r1.Contains("Landing Roll:") && !r1.Contains("Wall Run"), "tier 1 text lists slide and landing roll");
            r.Check(r2.Contains("Vault:") && r2.Contains("Ledge Hang:") && !r2.Contains("higher"), "tier 2 text: our moves, vanilla jump line gone");
            r.Check(r3.Contains("Wall Run:") && r5.Contains("stamina") && m4.Contains("Auto-Roll"), "tiers 3, 5 and Agility Mastery 4 texts");
            PkSettings.Set("WallRun", false, save: false);
            PkSettings.Set("ReplaceVanillaJump", false, save: false);
            PkSettings.Set("NoFallImmunity", true, save: false);
            PerkIntegration.Descriptions.Refresh(force: true);
            string r3off = Localization.Get("perkParkourRank3LongDesc"), r2van = Localization.Get("perkParkourRank2LongDesc");
            string r5off = Localization.Get("perkParkourRank5LongDesc");
            r.Check(!r3off.Contains("Wall Run:") && r3off.Contains("Wall Climb:"), "wall run off: gone from the tier 3 text");
            r.Check(r2van.Contains("higher"), "jump not replaced: vanilla jump line back");
            r.Check(r5off.Contains("5 meters") && !r5off.ToLowerInvariant().Contains("never take fall damage"), "no fall immunity: tier 5 text says 5 m");
            PkSettings.Set("PerkIntegration", false, save: false);
            PerkIntegration.Descriptions.Refresh(force: true);
            string r1noint = Localization.Get("perkParkourRank1LongDesc");
            r.Check(!r1noint.Contains("Parkour Movement"), "integration off: perk texts without mod lines");
            TestSettings();
            PerkIntegration.Descriptions.Refresh(force: true);

            // vanilla jump bonus of the real perk (tier 4: +75 %) and the tier 5 fall immunity
            ProgressionValue pv = P.Progression.GetProgressionValue(PerkIntegration.ParkourPerk);
            ProgressionValue agi = P.Progression.GetProgressionValue("attAgility");
            int oldLevel = pv.Level, oldAgi = agi.Level;
            agi.Level = 10;   // the perk tiers need Agility 2..10, otherwise they count as 0
            Tiers(-1, -1);
            pv.Level = 0;
            yield return new WaitForSeconds(0.7f);
            yield return MeasureJump();
            float rise0 = _jumpRise;
            pv.Level = 4;
            PkSettings.Set("ReplaceVanillaJump", false, save: false);
            yield return new WaitForSeconds(0.7f);
            yield return MeasureJump();
            float riseVanilla4 = _jumpRise;
            PkSettings.Set("ReplaceVanillaJump", true, save: false);
            yield return new WaitForSeconds(0.7f);
            yield return MeasureJump();
            float riseReplaced4 = _jumpRise;
            r.Note($"real tier read {PerkIntegration.RealParkourTier}; jump rise: no perk {rise0:0.00} m, tier 4 vanilla {riseVanilla4:0.00} m, tier 4 replaced {riseReplaced4:0.00} m");
            r.Check(riseVanilla4 > rise0 + 0.3f, "vanilla tier 4 jumps higher (reference)");
            r.Check(Mathf.Abs(riseReplaced4 - rise0) < 0.15f, "replaced: tier 4 jumps like no perk");

            PkSettings.Set("LandingRoll", false, save: false);   // a plain fall, no roll
            pv.Level = 0;
            yield return new WaitForSeconds(1.2f);
            yield return Fall14();
            r.Note($"tier 0 fall: hp loss {_fallLoss}, " + _fallInfo);
            pv.Level = 5;
            PkSettings.Set("NoFallImmunity", false, save: false);
            yield return new WaitForSeconds(1.2f);
            yield return Fall14();
            int lossImmune = _fallLoss;
            r.Note("vanilla fall: " + _fallInfo);
            PkSettings.Set("NoFallImmunity", true, save: false);
            yield return new WaitForSeconds(0.7f);
            yield return Fall14();
            int lossNoImmunity = _fallLoss;
            r.Note("no-immunity fall: " + _fallInfo);
            PkSettings.Set("NoFallImmunity", false, save: false);
            r.Note($"tier 5, 14 m fall: hp loss vanilla {lossImmune}, with 'no fall immunity' {lossNoImmunity}");
            r.Check(lossImmune <= 0 && lossNoImmunity > 0, "tier 5 fall immunity: vanilla none, option on takes damage");

            pv.Level = oldLevel;
            agi.Level = oldAgi;
            PkSettings.ResetAll();
            PkSettings.Set("PerkIntegration", false, save: false);
            yield return new WaitForSeconds(0.7f);
        }

        /// <summary>Options > General > Parkour: the tab exists, has a row per setting, and a row changes the setting.</summary>
        static IEnumerator OptionsTab(Result r)
        {
            LocalPlayerUI ui = LocalPlayerUI.primaryUI;
            // the options window pauses a single player game: wait in real time here
            ui.windowManager.Open(XUiC_OptionsGeneral.ID, _bModal: true);
            yield return new WaitForSecondsRealtime(0.8f);
            XUiC_OptionsGeneral win = ui.xui.GetChildByType<XUiC_OptionsGeneral>();
            XUiC_TabSelector tabs = win?.GetChildByType<XUiC_TabSelector>();
            int idx = -1;
            if (tabs != null)
                for (int k = 0; k < tabs.Tabs.Length; k++)
                    if (tabs.Tabs[k].TabKey == "xuiOptionsGeneralParkour") idx = k;
            r.Note($"options window {(win != null)}, tabs {tabs?.Tabs.Length ?? 0}, parkour tab index {idx}");
            r.Check(idx >= 0, "Parkour tab in Options > General");
            if (idx >= 0) tabs.SelectedTabIndex = idx;
            yield return new WaitForSecondsRealtime(0.8f);
            Shot("options_parkour_" + Localization.ActiveLanguage);
            yield return new WaitForSecondsRealtime(0.3f);   // the capture happens at the end of the frame
            var rows = win != null ? win.GetChildrenByType<XUiC_OptionEntryParkour>() : new XUiC_OptionEntryParkour[0];
            r.Note($"parkour rows {rows.Length} (settings {PkSettings.Defs.Length})");
            r.Check(rows.Length == PkSettings.Defs.Length, "a row for every setting");

            // flip "Wall Run" through its combo, apply, check the setting and the move mask
            XUiC_OptionEntryParkour wallRun = null;
            foreach (var row in rows)
                if (row.ViewComponent != null && row.GetChildByType<XUiC_ComboBoxBool>() != null && row.ViewComponent.ID == "OptionWallRun") wallRun = row;
            bool flipped = false;
            if (wallRun != null)
            {
                var combo = wallRun.GetChildByType<XUiC_ComboBoxBool>();
                combo.Value = false;
                bool pending = wallRun.IsChanged;
                wallRun.ApplySelection();
                flipped = pending && !wallRun.IsChanged && !PkSettings.Get("WallRun");
            }
            r.Check(flipped, "the Wall Run row switches wall run off (shows as a pending change, applies)");
            ui.windowManager.Close(XUiC_OptionsGeneral.ID);
            // the game runs again: the move mask picks the change up
            yield return new WaitForSeconds(0.3f);
            r.Check((PerkIntegration.Blocked & PkFeature.WallRun) != 0, "wall run blocked in the motor after the change");
            TestSettings();

            // the game keeps only English and the current language loaded: check the mod's texts in the current one
            // (clienttest.ps1 -Language <name> starts the game in another language)
            string cur = Localization.ActiveLanguage;
            string[] keys = { "xuiOptionsGeneralParkour", "xuiPkWallRun", "xuiPkWallRunTooltip", "xuiPkAutoRollTooltip", "pkPerkSlide", "pkPerkLandingRoll", "pkPerkSafeFall5", "pkPerkWithoutPerk" };
            var missing = new List<string>();
            foreach (string k in keys)
            {
                string v = Localization.Get(k);
                bool same = v == Localization.Get(k, false, "english");
                if (string.IsNullOrEmpty(v) || v == k || (cur != "english" && same && k != "xuiOptionsGeneralParkour")) missing.Add(k);
            }
            r.Note($"language {cur}: {keys.Length - missing.Count}/{keys.Length} mod texts translated {string.Join(" ", missing)}");
            r.Check(missing.Count == 0, "mod texts in the game's language");
            PkSettings.Set("PerkIntegration", true, save: false);
            PerkIntegration.Descriptions.Refresh(force: true);
            string rank1 = Localization.Get("perkParkourRank1LongDesc");
            TestSettings();
            PerkIntegration.Descriptions.Refresh(force: true);
            r.Note("rank 1 text: " + rank1.Replace("\\n", " | ").Replace("\n", " | "));
            r.Check(rank1.Contains(Localization.Get("pkPerkSlide")) && rank1.Contains("75"), "perk lines in the game's language");
        }

        static string HeadInfo()
        {
            var d = BodyAnimDriver.Instance;
            return d == null ? "no driver" : $"fp {P.bFirstPersonView} fpBody {d.FpBodyOn} head/arms scale {d.HeadArmsScale:0.000} clip {d.ClipName}";
        }

        /// <summary>Third person while hanging: head and arms must stay (they are only hidden for the first-person body).</summary>
        static IEnumerator Hang3p(Result r)
        {
            // a) hang entered in third person
            P.SetFirstPersonView(false, _bLerpPosition: false);
            yield return new WaitForSeconds(0.5f);
            In(1f);
            yield return Place(new Vector3(19.5f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In();
            yield return new WaitForSeconds(1.0f);
            string a = HeadInfo();
            float sa = BodyAnimDriver.Instance?.HeadArmsScale ?? -1f;
            yield return SideShot("hang3p_a", 3f, 0.6f, -1.2f);
            In(0f, crouch: true);
            yield return new WaitForSeconds(0.15f);
            In();
            yield return Until(() => Grounded, 3f);

            // b) hang in first person, switch to third person while hanging
            P.SetFirstPersonView(true, _bLerpPosition: false);
            yield return new WaitForSeconds(0.5f);
            In(1f);
            yield return Place(new Vector3(19.5f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In();
            yield return new WaitForSeconds(0.6f);
            string bFp = HeadInfo();
            P.SetFirstPersonView(false, _bLerpPosition: false);
            yield return new WaitForSeconds(1.0f);
            string b = HeadInfo();
            float sb = BodyAnimDriver.Instance?.HeadArmsScale ?? -1f;
            yield return SideShot("hang3p_b", 3f, 0.6f, -1.2f);
            In(0f, crouch: true);
            yield return new WaitForSeconds(0.15f);
            In();
            yield return Until(() => Grounded, 3f);
            yield return new WaitForSeconds(0.5f);
            string c = HeadInfo();
            float sc = BodyAnimDriver.Instance?.HeadArmsScale ?? -1f;
            P.SetFirstPersonView(true, _bLerpPosition: false);
            yield return new WaitForSeconds(0.5f);
            r.Note("3p hang: " + a);
            r.Note("fp hang: " + bFp + " | switched to 3p: " + b);
            r.Note("3p after drop: " + c);
            r.Check(sa > 0.99f, "head and arms shown when hanging in third person");
            r.Check(sb > 0.99f, "head and arms come back when switching to third person mid-hang");
            r.Check(sc > 0.99f, "head and arms shown in third person after the hang");
        }

        /// <summary>Render the player from the side with a temporary camera (third-person body check).</summary>
        static IEnumerator SideShot(string name, float side = 3f, float up = 0.6f, float ahead = 0.4f)
        {
            yield return new WaitForEndOfFrame();
            GameObject go = null;
            RenderTexture rt = null;
            try
            {
                Transform body = C.Transform;
                Vector3 target = body.position + Vector3.up * 0.9f;
                go = new GameObject("pkSideCam");
                var cam = go.AddComponent<Camera>();
                go.transform.position = target + body.right * side + Vector3.up * up + body.forward * ahead;
                go.transform.LookAt(target);
                cam.fieldOfView = 55f;
                cam.nearClipPlane = 0.05f;
                rt = new RenderTexture(960, 540, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(_shotDir ?? ".", "pk_side_" + name + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour][TEST] side shot: " + ex.Message);
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                if (rt != null) rt.Release();
            }
        }

        static string AnimNote() => $"clip {BodyAnimDriver.CurrentClip ?? "none"} w {BodyAnimDriver.CurrentWeight:0.00}";

        static IEnumerator Anim3P(Result r)
        {
            P.SetFirstPersonView(false, _bLerpPosition: false);
            yield return new WaitForSeconds(0.5f);
            r.Note("clips loaded: " + BodyAnimLibrary.Clips.Count);

            // slide
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.5f, 4f);
            yield return SideShot("run");
            In(1f, run: true, crouch: true);
            yield return new WaitForSeconds(0.25f);
            string slideStart = AnimNote();
            yield return SideShot("slide_start");
            yield return new WaitForSeconds(0.45f);
            string slideLoop = AnimNote();
            yield return SideShot("slide_loop");
            yield return new WaitForSeconds(0.8f);
            In();
            yield return new WaitForSeconds(0.15f);
            string slideExit = AnimNote();
            yield return SideShot("slide_exit");
            yield return new WaitForSeconds(0.6f);

            // mantle onto the 2 m wall
            yield return Place(new Vector3(13.5f, 0.05f, 11.2f), 0f);
            In(1f, jump: true);
            yield return new WaitForSeconds(0.1f);
            In(1f);
            yield return Until(() => PkRuntime.Motor.State == PkState.Mantle, 1.5f);
            yield return new WaitForSeconds(0.12f);
            string mantle = AnimNote();
            yield return SideShot("mantle", 3f, 0.8f, 0f);
            yield return Until(() => Saw("mantle-done"), 2f);
            In();

            // wall run
            yield return Place(new Vector3(7.45f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.6f, 4f);
            In(1f, run: true, jump: true);
            yield return new WaitForSeconds(0.12f);
            In(1f, run: true);
            yield return Until(() => PkRuntime.Motor.State == PkState.WallRun, 1f);
            yield return new WaitForSeconds(0.35f);
            string wallrun = AnimNote();
            yield return SideShot("wallrun", -2.5f, 0.6f, 0f);
            In();
            yield return Until(() => Grounded, 3f);

            // hang
            In(1f);
            yield return Place(new Vector3(19.5f, 2.8f, 29.45f), 0f, settle: false);
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang, 1.5f);
            In();
            yield return new WaitForSeconds(0.6f);
            string hang = AnimNote();
            yield return SideShot("hang", 3f, 0.3f, -1.2f);
            In(1f, jump: true);                       // climb up from the hang: pull-up clip
            yield return new WaitForSeconds(0.2f);
            In(1f);
            string pullup = AnimNote();
            yield return SideShot("pullup", 3f, 0.8f, -0.8f);
            yield return Until(() => Saw("mantle-done"), 2f);
            In();
            yield return new WaitForSeconds(0.3f);

            // wall climb up the 5 m wall (lane D): long enough to see the climb before the ledge
            yield return Place(new Vector3(19.5f, 0.05f, 23.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 28.9f, 4f);
            In(1f, run: true, jump: true);
            yield return Until(() => PkRuntime.Motor.State == PkState.WallClimb, 1f);
            yield return new WaitForSeconds(0.15f);
            string climb = AnimNote();
            yield return SideShot("wallclimb", 3f, 0.6f, -1f);
            yield return Until(() => Saw("mantle-done") || Grounded, 2f);
            In();
            yield return new WaitForSeconds(0.3f);

            // landing roll
            In(1f);
            yield return Place(new Vector3(1.5f, 7f, 30f), 0f, settle: false);
            yield return Until(() => Local.y < 2.0f, 4f);
            In(1f, crouch: true);
            yield return Until(() => PkRuntime.Motor.State == PkState.Roll, 3f);
            yield return new WaitForSeconds(0.2f);
            string roll = AnimNote();
            yield return SideShot("roll");
            In();
            yield return new WaitForSeconds(1f);
            Refill();

            P.SetFirstPersonView(true, _bLerpPosition: false);
            r.Note($"slide start: {slideStart} | loop: {slideLoop} | exit: {slideExit}");
            r.Note($"mantle: {mantle} | wallrun: {wallrun} | hang: {hang} | roll: {roll}");
            r.Note($"pull-up: {pullup} | wall climb: {climb}");
            r.Check(hang.Contains("clip hang"), "hang clip plays");
            r.Check(pullup.Contains("pullup"), "pull-up clip when climbing out of a hang");
            r.Check(climb.Contains("climb_loop"), "wall climb clip plays");
            r.Check(slideStart.Contains("slide_start") && slideLoop.Contains("slide_loop"), "slide clips play");
            r.Check(mantle.Contains("mantle"), "mantle clip plays");
            r.Check(wallrun.Contains("sprint_loop"), "wall run plays a run cycle");
            r.Check(roll.Contains("roll"), "roll clip plays");
        }

        /// <summary>
        /// Joined a server (vanilla, without this mod): do parkour moves on the spot and check we stay connected,
        /// alive and that our animation choices are being sent (the server just relays them; vanilla ignores them).
        /// </summary>
        static IEnumerator MpClient(Result r)
        {
            ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            r.Note($"client {cm.IsClient}, server {cm.IsServer}");
            int sent0 = BodyAnimDriver.NetSent;
            P.SetFirstPersonView(false, _bLerpPosition: false);
            yield return new WaitForSeconds(1f);
            for (int k = 0; k < 3; k++)
            {
                In(1f, run: true);
                yield return new WaitForSeconds(1.3f);
                In(1f, run: true, crouch: true);       // slide
                yield return new WaitForSeconds(0.9f);
                In(1f, run: true, jump: true);         // slide jump
                yield return new WaitForSeconds(0.15f);
                In(-1f, run: false);                   // back the other way
                yield return new WaitForSeconds(1.2f);
                In();
                yield return new WaitForSeconds(0.5f);
            }
            Shot("mp_3p");
            yield return new WaitForSeconds(3f);       // let the packets flush and the server react
            P.SetFirstPersonView(true, _bLerpPosition: false);
            int sent = BodyAnimDriver.NetSent - sent0;
            r.Note($"animation parameters sent to the server: {sent}; still connected {cm.IsConnected}; alive {!P.IsDead()}");
            r.Check(Saw("slide"), "parkour works on the server (slide)");
            r.Check(sent > 0, "animation state sent to other players");
            r.Check(cm.IsConnected && !P.IsDead(), "still connected (not kicked) and alive");
        }

        /// <summary>
        /// Receiving side of the animation sync without a second client: drive a zombie (same skeleton naming) as if it
        /// were a remote player announcing "hang", then "slide_loop", and check the clip lands on its bones.
        /// </summary>
        static IEnumerator RemoteLoop(Result r)
        {
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, true);
            P.Buffs.AddBuff("god");
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            EntityAlive z = SpawnZombie(new Vector3(1.5f, 0.05f, 5f), 180f);
            r.Check(z != null, "stand-in remote body spawned");
            if (z == null) yield break;
            yield return new WaitForSeconds(1.5f);
            // the vanilla receive path must swallow our types without errors
            z.emodel.avatarController.SetDataFloat((AvatarController.DataTypes)BodyAnimDriver.NetClip, 9.1f);
            BodyAnimDriver d = BodyAnimDriver.ForRemote(z);
            d.Receive(BodyAnimDriver.NetClip, BodyAnimLibrary.IdOf("hang") + 0.1f);
            d.Receive(BodyAnimDriver.NetSeq, 1f);
            yield return new WaitForSeconds(0.5f);
            string hang = $"{d.ClipName} w {d.Weight:0.00}";
            yield return SideShot("remote_hang", 3f, 0.6f, 3.5f);
            d.Receive(BodyAnimDriver.NetClip, BodyAnimLibrary.IdOf("slide_loop") + 0.1f);
            d.Receive(BodyAnimDriver.NetLean, -20f);
            yield return new WaitForSeconds(0.5f);
            string slide = $"{d.ClipName} w {d.Weight:0.00}";
            yield return SideShot("remote_slide", 3f, 0.6f, 3.5f);
            d.Receive(BodyAnimDriver.NetClip, 0f);
            d.Receive(BodyAnimDriver.NetLean, 0f);
            yield return new WaitForSeconds(0.5f);
            float off = d.Weight;
            r.Note($"remote hang: {hang} | slide: {slide} | after stop weight {off:0.00}");
            r.Check(hang.StartsWith("hang") && slide.StartsWith("slide_loop"), "received clips play on the remote body");
            r.Check(off < 0.05f, "stops when the sender stops");
            UnityEngine.Object.Destroy(d);
            P.Buffs.RemoveBuff("god");
            DespawnAll();
            GamePrefs.Set(EnumGamePrefs.DebugStopEnemiesMoving, false);
        }

        /// <summary>First-person slide: what the player sees (legs, hands) + rig / layer facts for the FP body.</summary>
        static IEnumerator FpSlide(Result r)
        {
            yield return Place(new Vector3(1.5f, 0.05f, 1.5f), 0f);
            var alpc = P.emodel?.avatarController as AvatarLocalPlayerController;
            Camera cam = P.playerCamera;
            var layers = new SortedDictionary<int, int>();
            if (alpc?.CharacterBody?.Animator != null)
                foreach (Renderer rr in alpc.CharacterBody.Animator.GetComponentsInChildren<Renderer>(true))
                    layers[rr.gameObject.layer] = layers.TryGetValue(rr.gameObject.layer, out int c) ? c + 1 : 1;
            var fpLayers = new SortedDictionary<int, int>();
            Animator fa = alpc?.FPSArms?.Animator;
            if (fa != null)
                foreach (Renderer rr in fa.GetComponentsInChildren<Renderer>(true))
                    fpLayers[rr.gameObject.layer] = fpLayers.TryGetValue(rr.gameObject.layer, out int c) ? c + 1 : 1;
            var fingers = new List<string>();
            if (fa != null)
                foreach (Transform t in fa.GetComponentsInChildren<Transform>(true))
                    if (t.parent != null && (t.parent.name == "LeftHand" || t.parent.name == "RightHand")) fingers.Add(t.name);
            r.Note($"camera {cam?.name} cullingMask 0x{cam?.cullingMask:X8}");
            r.Note("3P body renderer layers: " + string.Join(", ", layers.Select(kv => $"{kv.Key}x{kv.Value}")));
            r.Note("FP arms renderer layers: " + string.Join(", ", fpLayers.Select(kv => $"{kv.Key}x{kv.Value}")));
            r.Note("FP hand children: " + string.Join(", ", fingers));
            yield return new WaitForSeconds(0.5f);
            int idleOn = BodyAnimDriver.Instance != null ? BodyAnimDriver.Instance.BodyRenderersOn() : -1;
            Shot("fp_idle");
            r.Note($"body renderers on while standing in first person: {idleOn}");
            r.Check(idleOn == 0, "no body (no head in the camera) in first person outside a move");
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.5f, 4f);
            In(1f, run: true, crouch: true);
            yield return new WaitForSeconds(0.2f);
            Shot("fpslide_a");
            yield return new WaitForSeconds(0.35f);
            Shot("fpslide_b");
            yield return SideShot("fpslide_side", 2.5f, 0.3f, 0f);
            r.Note($"camera {(cam.transform.position - (P.position - Origin.position)).y:0.00} m over the feet");
            r.Note($"during slide: {AnimNote()}, FP hand weights L {ArmIK.WeightL:0.00} R {ArmIK.WeightR:0.00}");
            var body = alpc?.CharacterBody;
            var rs = new StringBuilder();
            if (P.emodel != null)
                foreach (Renderer rr in P.emodel.GetComponentsInChildren<Renderer>(true))
                {
                    if (rr.gameObject.layer == 10) continue;
                    rs.Append($"{rr.name}[on={rr.enabled && rr.gameObject.activeInHierarchy} L{rr.gameObject.layer} {rr.shadowCastingMode} vis={rr.isVisible}] ");
                }
            Transform hips = body?.Animator != null ? body.Animator.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Hips") : null;
            r.Note($"body state {body?.state}, hips {(hips != null ? (hips.position - cam.transform.position).ToString("F2") : "?")} from camera");
            r.Note("body renderers: " + rs);
            yield return new WaitForSeconds(0.8f);
            In();
            yield return new WaitForSeconds(0.5f);
            r.Check(Saw("slide"), "slid");

            // the other moves, as the player sees them
            yield return Place(new Vector3(7.45f, 0.05f, 1.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 8.6f, 4f);
            In(1f, run: true, jump: true);
            yield return new WaitForSeconds(0.12f);
            In(1f, run: true);
            yield return Until(() => PkRuntime.Motor.State == PkState.WallRun, 1f);
            yield return new WaitForSeconds(0.3f);
            Shot("fp_wallrun");
            In();
            yield return Until(() => Grounded, 3f);

            yield return Place(new Vector3(19.5f, 0.05f, 23.5f), 0f);
            In(1f, run: true);
            yield return Until(() => Local.z >= 28.9f, 4f);
            In(1f, run: true, jump: true);
            yield return Until(() => PkRuntime.Motor.State == PkState.WallClimb, 1f);
            yield return new WaitForSeconds(0.2f);
            Shot("fp_wallclimb");
            yield return Until(() => PkRuntime.Motor.State == PkState.Hang || Grounded, 1.5f);
            In();
            yield return new WaitForSeconds(0.5f);
            Shot("fp_hang");
            In(1f, jump: true);
            yield return new WaitForSeconds(0.25f);
            In(1f);
            Shot("fp_pullup");
            yield return Until(() => Saw("mantle-done"), 2f);
            In();
            yield return new WaitForSeconds(0.3f);
            r.Check(Saw("wallrun-right") && Saw("wallclimb"), "wall run and wall climb happened");
        }

        // ------------------------------------------------------------------ playground (hand the course to the player)

        public static bool CourseBuilt { get; private set; }

        /// <summary>Course sections: (local start position, yaw).</summary>
        public static readonly Dictionary<string, KeyValuePair<Vector3, float>> Lanes = new Dictionary<string, KeyValuePair<Vector3, float>>
        {
            { "start",    new KeyValuePair<Vector3, float>(new Vector3(1.5f, 0.05f, 1.5f), 0f) },    // slide lane + roll drop zone
            { "slide",    new KeyValuePair<Vector3, float>(new Vector3(1.5f, 0.05f, 1.5f), 0f) },
            { "wallrun",  new KeyValuePair<Vector3, float>(new Vector3(6.5f, 0.05f, 1.5f), 0f) },     // two walls 9 m high
            { "climb",    new KeyValuePair<Vector3, float>(new Vector3(13.5f, 0.05f, 1.5f), 0f) },  // 2 m wall
            { "vault",    new KeyValuePair<Vector3, float>(new Vector3(19.5f, 0.05f, 1.5f), 0f) },  // 1 m obstacle
            { "hang",     new KeyValuePair<Vector3, float>(new Vector3(19.5f, 0.05f, 23.5f), 0f) }, // 5 m wall
            { "coyote",   new KeyValuePair<Vector3, float>(new Vector3(13.5f, 2.05f, 30.5f), 0f) }, // 2 m platform edge
            { "lowslide", new KeyValuePair<Vector3, float>(new Vector3(13.5f, 0.05f, 37f), 0f) },   // 1 m high slab
            { "roll",     new KeyValuePair<Vector3, float>(new Vector3(1.5f, 9f, 40f), 0f) },       // 9 m drop: crouch before landing
        };

        static IEnumerator Playground()
        {
            Log.Out("[Parkour][PLAY] building the course");
            GameManager.Instance.World.SetTimeJump(12000uL, _isSeek: true);
            GameStats.Set(EnumGameStats.TimeOfDayIncPerSec, 0);
            // the sky course has no support: keep block physics off for this session
            GameStats.Set(EnumGameStats.ChunkStabilityEnabled, false);
            GameManager.bPhysicsActive = false;
            Protect(P, true);
            var setup = new Result { Name = "setup" };
            yield return Guard(BuildCourse(setup), setup);
            Protect(P, false);
            GameStats.Set(EnumGameStats.IsSpawnEnemies, false);   // no random hordes; use "pk zombies" for targets
            ScriptedInput.Active = false;
            ScriptedInput.Clear();
            CourseBuilt = setup.Pass;
            yield return Place(Lanes["start"].Key, Lanes["start"].Value);
            ScriptedInput.Active = false;   // Place() takes the controls for tests: give them back
            ScriptedInput.Clear();
            Refill();
            Log.Out("[Parkour][PLAY] course ready: " + string.Join("; ", setup.Notes));
            GameManager.ShowTooltip(P, "Parkour playground ready. Console: pk tp slide|wallrun|climb|vault|hang|coyote|lowslide|roll, pk zombies 3");
        }

        /// <summary>Console: teleport to a course section.</summary>
        public static string TeleportTo(string lane)
        {
            P = GameManager.Instance.World?.GetPrimaryPlayer();
            if (P == null) return "no player";
            if (!CourseBuilt) return "no course in this session (start the playground: tools\\playground.ps1)";
            if (!Lanes.TryGetValue(lane, out var l)) return "unknown section. Use: " + string.Join(", ", Lanes.Keys);
            ThreadManager.StartCoroutine(Place(l.Key, l.Value, settle: false));
            ScriptedInput.Active = false;
            return "teleported to " + lane;
        }

        /// <summary>Console: spawn a group of zombies a few metres in front of the player.</summary>
        public static string SpawnTargets(int n)
        {
            P = GameManager.Instance.World?.GetPrimaryPlayer();
            if (P == null) return "no player";
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) return "only in your own (hosted) game";
            Transform body = P.vp_FPController.Transform;
            Vector3 f = body.forward; f.y = 0f; f.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, f);
            Vector3 c = P.position + f * 7f;
            int cls = EntityClass.FromString("zombieBoe");
            if (!EntityClass.list.ContainsKey(cls)) return "zombie class not found";
            for (int k = 0; k < n; k++)
            {
                Vector3 pos = c + right * ((k % 3) - 1) * 0.8f + f * (k / 3) * 0.9f;
                Entity e = EntityFactory.CreateEntity(cls, pos, new Vector3(0f, Quaternion.LookRotation(-f).eulerAngles.y, 0f));
                GameManager.Instance.World.SpawnEntityInWorld(e);
            }
            return $"spawned {n} zombies 7 m ahead";
        }

        static IEnumerator Coyote(Result r)
        {
            yield return Place(new Vector3(13.5f, 2.05f, 30.5f), 0f);
            In(1f, run: true);
            yield return Until(() => !Grounded && Local.z > 35.5f, 3f);
            yield return new WaitForSeconds(0.04f);
            In(1f, run: true, jump: true);
            float vy = -99f;
            yield return Watch(0.2f, () => vy = Mathf.Max(vy, PkRuntime.Body.Velocity.Y));
            In();
            yield return Until(() => Grounded, 3f);
            r.Note($"max vy after late jump {vy:0.00}");
            r.Check(Saw("coyote-jump"), "coyote jump fired");
            r.Check(vy > 2f, "went up");
        }
    }
}

namespace ParkourMovement.Patches
{
    /// <summary>Autotest only: skip the "click to continue" news screen so the main menu (and our world start) opens.</summary>
    [HarmonyLib.HarmonyPatch(typeof(XUiC_NewsScreen), nameof(XUiC_NewsScreen.Update))]
    public static class NewsScreenSkipPatch
    {
        static bool _done;

        public static void Postfix(XUiC_NewsScreen __instance)
        {
            if (_done || !AutoTest.Armed) return;
            _done = true;
            UnityEngine.Debug.Log("[Parkour][TEST] skipping news screen");
            __instance.BtnContinue_OnPress(__instance, 0);
        }
    }
}

namespace ParkourMovement.Patches
{
    /// <summary>Autotest only: press "Spawn" if the spawn selection window shows up anyway.</summary>
    [HarmonyLib.HarmonyPatch(typeof(XUiC_SpawnSelectionWindow), nameof(XUiC_SpawnSelectionWindow.Update))]
    public static class SpawnWindowSkipPatch
    {
        static float _openSince = -1f;

        public static void Postfix(XUiC_SpawnSelectionWindow __instance)
        {
            if (!AutoTest.Running && !AutoTest.Starting) return;
            if (_openSince < 0f) _openSince = UnityEngine.Time.realtimeSinceStartup;
            if (UnityEngine.Time.realtimeSinceStartup - _openSince < 2f) return;
            _openSince = UnityEngine.Time.realtimeSinceStartup + 5f;
            UnityEngine.Debug.Log("[Parkour][TEST] pressing Spawn");
            __instance.SpawnButtonPressed(SpawnMethod.Invalid);
        }
    }
}
