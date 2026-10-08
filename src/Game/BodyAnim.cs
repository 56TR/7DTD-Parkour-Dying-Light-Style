using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>One retargeted clip (tools/retarget_ual.py, tools/retarget_bvh.py): local bone rotations per frame + hips offset.</summary>
    [Serializable]
    public class PkClipData
    {
        public string name;
        public float fps;
        public float length;
        public bool loop;
        public int frameCount;
        public string[] bones;
        public float[] hips;
        public float[] rot;
    }

    public static class BodyAnimLibrary
    {
        public static readonly Dictionary<string, PkClipData> Clips = new Dictionary<string, PkClipData>();

        /// <summary>Network ids: index in this table. Append only, never reorder (other players' mod versions).</summary>
        public static readonly string[] Ids =
        {
            "", "slide_start", "slide_loop", "slide_exit", "mantle", "roll", "sprint_loop", "air_start",
            "fall_loop", "hang", "pullup", "climb_loop", "dropkick", "air_land", "air_loop",
        };

        public static int IdOf(string name) => name == null ? 0 : Math.Max(0, Array.IndexOf(Ids, name));

        public static string Load(string modPath)
        {
            Clips.Clear();
            string dir = Path.Combine(modPath ?? ".", "Anims");
            if (!Directory.Exists(dir)) return "no animation folder";
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var c = JsonUtility.FromJson<PkClipData>(File.ReadAllText(f));
                    if (c?.bones != null && c.rot != null && c.frameCount > 0 && c.rot.Length == c.frameCount * c.bones.Length * 4)
                        Clips[c.name] = c;
                    else Log.Warning("[Parkour] bad clip " + f);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Parkour] clip " + f + ": " + ex.Message);
                }
            }
            return $"{Clips.Count} body clips";
        }
    }

    /// <summary>
    /// Third-person body animation for parkour moves, applied after the game's Animator each frame.
    /// Local player: the clip follows the motor state, plus wall-run lean and hand IK, and is announced to other players.
    /// Remote players: the clip comes from the network (only players running this mod see it).
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class BodyAnimDriver : MonoBehaviour
    {
        public static BodyAnimDriver Instance;   // local player's driver
        public static string CurrentClip => Instance?._clip?.name;
        public static float CurrentWeight => Instance?._w ?? 0f;
        public string ClipName => _clip?.name;
        /// <summary>Tests: smallest of neck / shoulder scale (1 = shown, ~0 = hidden for the first-person body).</summary>
        public float HeadArmsScale
        {
            get
            {
                float m = 99f;
                foreach (string n in new[] { "Neck", "LeftShoulder", "RightShoulder" })
                    if (_bones.TryGetValue(n, out Transform t) && t != null) m = Mathf.Min(m, t.localScale.x);
                return m;
            }
        }
        public bool FpBodyOn => _fpBodyOn;
        public float Weight => _w;

        EntityAlive _e;
        bool _local;
        Animator _anim;
        readonly Dictionary<string, Transform> _bones = new Dictionary<string, Transform>();
        static readonly Vector3 HipsRest = new Vector3(0f, 0.9743411f, -0.0282091f);   // _assets/skeleton_3p.json
        Transform _hips, _lUp, _lLo, _lHand, _rUp, _rLo, _rHand;

        PkClipData _clip;
        float _t, _speed = 1f, _w, _target;
        PkState _lastState;
        float _exitUntil, _oneShotUntil;
        string _oneShot;
        int _seenEvents;
        float _lean, _armW;

        // network state (remote: received; local: last sent)
        int _netClip, _netSeq;
        float _netSpeed = 1f, _netLean;
        int _sentClip = -1, _sentSeq = -1;
        float _sentSpeed = -1f, _sentLean = 999f;
        int _restartSeq;

        public static void AttachLocal(EntityPlayerLocal p)
        {
            if (p == null || Instance != null && Instance._e == p) return;
            Instance = p.gameObject.GetComponent<BodyAnimDriver>() ?? p.gameObject.AddComponent<BodyAnimDriver>();
            Instance._e = p;
            Instance._local = true;
        }

        /// <summary>
        /// Photo mode (screenshots for the mod page): hold one frame of a clip on this body, with a wall-run lean.
        /// The entity must be a humanoid SDCS character (player, trader); nothing is sent over the network.
        /// </summary>
        public void Pose(string clip, float time, float lean)
        {
            if (!BodyAnimLibrary.Clips.TryGetValue(clip, out PkClipData c)) return;
            if (!_posing)
            {
                // true bind pose of this skeleton: reset the animator, then read the rest rotations
                Animator an = FindAnimator();
                if (an != null)
                {
                    an.Rebind();
                    an.Update(0f);
                    _anim = null;   // Bind() again: captures the rest pose now
                    Bind();
                }
            }
            _posing = true;
            _clip = c;
            _t = Mathf.Clamp(time, 0f, c.length);
            _speed = 0f;
            _target = 1f;
            _w = 1f;
            _lean = lean;
        }

        public void EndPose() { _posing = false; _target = 0f; _speed = 1f; _lean = 0f; }

        bool _posing;
        readonly Dictionary<string, Quaternion> _restRot = new Dictionary<string, Quaternion>();
        Vector3 _restHips = HipsRest;

        public static BodyAnimDriver ForRemote(EntityAlive e)
        {
            BodyAnimDriver d = e.gameObject.GetComponent<BodyAnimDriver>() ?? e.gameObject.AddComponent<BodyAnimDriver>();
            d._e = e;
            d._local = false;
            return d;
        }

        Animator FindAnimator()
        {
            if (_local) return ((_e as EntityPlayerLocal)?.emodel?.avatarController as AvatarLocalPlayerController)?.CharacterBody?.Animator;
            return _e?.emodel?.avatarController?.anim;
        }

        bool Bind()
        {
            Animator a = FindAnimator();
            if (a == null) return false;
            if (a == _anim) return _hips != null;
            _anim = a;
            _bones.Clear();
            foreach (Transform t in a.GetComponentsInChildren<Transform>(true))
                if (!_bones.ContainsKey(t.name)) _bones[t.name] = t;
            _bones.TryGetValue("Hips", out _hips);
            // rest pose of this skeleton (photo mode: other characters have a different bind pose than the player's)
            _restRot.Clear();
            foreach (var kv in _bones) _restRot[kv.Key] = kv.Value.localRotation;
            _restHips = _hips != null ? _hips.localPosition : HipsRest;
            _bones.TryGetValue("LeftArm", out _lUp);
            _bones.TryGetValue("LeftForeArm", out _lLo);
            _bones.TryGetValue("LeftHand", out _lHand);
            _bones.TryGetValue("RightArm", out _rUp);
            _bones.TryGetValue("RightForeArm", out _rLo);
            _bones.TryGetValue("RightHand", out _rHand);
            return _hips != null;
        }

        void LateUpdate()
        {
            try
            {
                if (_e == null) { Destroy(this); return; }
                float dt = Time.deltaTime;
                if (_local)
                {
                    if (!_posing) { ChooseLocal(dt); Announce(); }
                    bool fp = (_e as EntityPlayerLocal).bFirstPersonView;
                    if (!fp && _wasFp) ShowBodyRenderers(true);   // back to third person: the body is the player's again
                    _wasFp = fp;
                    // first person: show the body (legs) during moves where looking down should show it
                    bool showFp = fp && FpBodyOnInSettings && (_target > 0f || _w > 0.01f) && FpBodyState();
                    SetFpBody(showFp);
                    // outside the first-person body the head and arms are always full size. Re-asserted every frame:
                    // a view switch rebinds the animator, and if that happened while they were collapsed the animator
                    // keeps writing the collapsed scale as its default (they shrank away in third person).
                    if (!_fpBodyOn) ShowHeadAndArms(true);
                    if ((fp && !showFp) || !Bind()) { _w = 0f; return; }
                }
                else
                {
                    if (!_posing) ChooseRemote();
                    if (!Bind()) return;
                }
                Advance(dt);
                _w = Mathf.MoveTowards(_w, _target, dt / 0.15f);
                if (_clip != null && _w > 0.001f) ApplyClip();
                ApplyProcedural(dt);
                if (_fpBodyOn) ShowHeadAndArms(false);
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour] body anim: " + ex.Message);
                enabled = false;
            }
        }

        void Play(string name, float speed, bool restart = false)
        {
            if (!BodyAnimLibrary.Clips.TryGetValue(name, out PkClipData c)) { _target = 0f; return; }
            if (_clip != c || restart)
            {
                _clip = c;
                _t = 0f;
                _restartSeq++;
            }
            _speed = speed;
            _target = 1f;
        }

        void ChooseLocal(float dt)
        {
            ParkourMotor m = PkRuntime.Motor;
            PkState s = m.State;
            Tuning T = PkRuntime.Tuning;
            if (!PkSettings.Enabled || !PkSettings.BodyAnims)
            {
                // Options > Parkour > Body animations off: vanilla body (also what other players see)
                _target = 0f;
                _lastState = s;
                _seenEvents = m.EventCount;
                return;
            }
            bool entered = s != _lastState;
            PkState prev = _lastState;
            if (entered && prev == PkState.Slide && s != PkState.Slide) _exitUntil = Time.time + 0.45f;
            _lastState = s;

            // one-shots triggered by events (dropkick)
            if (m.EventCount != _seenEvents)
            {
                _seenEvents = m.EventCount;
                if (m.LastEvent == "dropkick") { _oneShot = "dropkick"; _oneShotUntil = Time.time + 0.7f; Play("dropkick", 1.2f, true); }
            }
            if (_oneShot != null && Time.time < _oneShotUntil && s == PkState.None) return;
            _oneShot = null;

            switch (s)
            {
                case PkState.Slide:
                    if (m.StateTime < 0.45f) Play("slide_start", 1.6f, entered);
                    else Play("slide_loop", 1f);
                    break;
                case PkState.Mantle:
                    if (entered && prev == PkState.Hang) _pullUp = true;
                    else if (entered) _pullUp = false;
                    if (_pullUp) Play("pullup", 2.0f, entered);
                    else Play("mantle", 1.3f, entered);
                    break;
                case PkState.Roll:
                    if (BodyAnimLibrary.Clips.TryGetValue("roll", out PkClipData roll))
                        Play("roll", roll.length / Mathf.Max(T.RollTime, 0.2f), entered);
                    break;
                case PkState.WallRun:
                    Play("sprint_loop", 1.2f);
                    break;
                case PkState.WallClimb:
                    Play("climb_loop", 2.2f);
                    break;
                case PkState.VaultEnemy:
                    Play("air_start", 1.4f, entered);
                    break;
                case PkState.Hang:
                    Play("hang", 1f);
                    break;
                default:
                    if (Time.time < _exitUntil) Play("slide_exit", 1.2f, _clip == null || _clip.name != "slide_exit");
                    else _target = 0f;
                    break;
            }
            float leanTarget = s == PkState.WallRun ? PkRuntime.Out.CamRoll * 1.8f : 0f;   // camera roll is away from the wall
            _lean = Mathf.MoveTowards(_lean, leanTarget, dt * 120f);
        }

        bool _pullUp;

        static bool FpBodyOnInSettings => PkRuntime.Tuning.FpBody && PkSettings.Enabled && PkSettings.FpBody;

        // ---- first-person body ("full body awareness" while doing a move)

        bool _fpBodyOn;
        Transform _head, _lShoulder, _rShoulder;

        bool FpBodyState()
        {
            PkState s = PkRuntime.Motor.State;
            if (_oneShot == "dropkick") return true;
            // not while climbing / pulling up: the knees come up right under the camera and block the view
            return s == PkState.Slide || s == PkState.WallRun || s == PkState.Hang || s == PkState.VaultEnemy
                || (s == PkState.None && Time.time < _exitUntil);
        }

        GameObject _builtFor;

        /// <summary>
        /// In first person the game builds the third-person rig without any meshes (SDCSUtils.CreateVizTP skips the
        /// body parts when isFPV). Build them once, like a switch to third person does, and keep them hidden.
        /// </summary>
        void EnsureFpBodyMeshes()
        {
            var sd = _e.emodel as EModelSDCS;
            if (sd == null || sd.baseRig == null || _builtFor == sd.baseRig && HasBodyMeshes(sd)) return;
            if (!HasBodyMeshes(sd))
            {
                SDCSUtils.CreateVizTP(sd.archetype, ref sd.baseRig, ref sd.boneCatalog, _e, false);
                Utils.SetLayerRecursively(sd.baseRig, 24);
                _anim = null;   // rebind bones
                Log.Out("[Parkour] first-person body meshes built");
            }
            _builtFor = sd.baseRig;
            // the new meshes come up enabled; the body animator thinks it is already hidden, so we own them
            _bodyRenderers.Clear();
            foreach (Renderer r in sd.baseRig.GetComponentsInChildren<Renderer>(true))
                if (r.gameObject.layer != 10) _bodyRenderers.Add(r);
            ShowBodyRenderers(_fpBodyOn);
        }

        readonly List<Renderer> _bodyRenderers = new List<Renderer>();
        bool _wasFp;

        /// <summary>How many body renderers are on (tests: must be 0 in first person outside a move).</summary>
        public int BodyRenderersOn()
        {
            int n = 0;
            foreach (Renderer r in _bodyRenderers) if (r != null && r.enabled && r.gameObject.activeInHierarchy) n++;
            return n;
        }

        void ShowBodyRenderers(bool show)
        {
            foreach (Renderer r in _bodyRenderers)
                if (r != null && r.enabled != show) r.enabled = show;
        }

        static bool HasBodyMeshes(EModelSDCS sd) =>
            sd.baseRig.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0;

        void SetFpBody(bool on)
        {
            bool fpView = (_e as EntityPlayerLocal).bFirstPersonView;
            if (_local && FpBodyOnInSettings && fpView)
            {
                EnsureFpBodyMeshes();
                ShowBodyRenderers(on);   // every frame in first person: hidden unless a move shows the body
            }
            if (on == _fpBodyOn) return;
            var body = ((_e as EntityPlayerLocal)?.emodel?.avatarController as AvatarLocalPlayerController)?.CharacterBody;
            if (body == null) return;
            _fpBodyOn = on;
            bool fp = (_e as EntityPlayerLocal).bFirstPersonView;
            // off: back to what the current view uses (first person hides the body, third person shows it)
            body.State = on || !fp ? BodyAnimator.EnumState.Visible : BodyAnimator.EnumState.OnlyColliders;
            if (!on) ShowHeadAndArms(true);
        }

        /// <summary>Before the view switches (the animator rebinds then and keeps the current scale as its default).</summary>
        public void RestoreHeadArms() => ShowHeadAndArms(true);

        public bool IsFor(Entity e) => _e == e;

        /// <summary>The camera sits in the head and the first-person rig has its own arms: hide the body's head and arms.</summary>
        void ShowHeadAndArms(bool show)
        {
            // collapse from the neck: the head mesh is partly weighted to the neck, hair / headgear follow the head
            if (_lShoulder == null) _bones.TryGetValue("LeftShoulder", out _lShoulder);
            if (_rShoulder == null) _bones.TryGetValue("RightShoulder", out _rShoulder);
            if (_head == null) _bones.TryGetValue("Neck", out _head);
            Vector3 s = show ? Vector3.one : Vector3.one * 0.0001f;
            if (_head != null) _head.localScale = s;
            if (_lShoulder != null) _lShoulder.localScale = s;
            if (_rShoulder != null) _rShoulder.localScale = s;
        }

        // ---- network: our choice goes out through the animation-data packet as unknown "DataFloat" types.
        // Vanilla clients and servers ignore them (only DataTypes.HitDuration = 0 is handled); servers relay them.

        public const int NetClip = 0x504B4331;   // value = clip id + speed / 10
        public const int NetSeq = 0x504B4332;    // value = restart counter (same clip played again)
        public const int NetLean = 0x504B4333;   // value = body lean in degrees

        public static int NetSent;   // parameters queued for other players (tests)

        void Announce()
        {
            AvatarController ac = _e?.emodel?.avatarController;
            if (ac == null || !ConnectionManagerHasPeers()) return;
            int id = _target > 0f && _clip != null ? BodyAnimLibrary.IdOf(_clip.name) : 0;
            float speed = Mathf.Round(Mathf.Clamp(_speed, 0.1f, 9.9f) * 10f) / 10f;
            if (id != _sentClip || (id != 0 && Mathf.Abs(speed - _sentSpeed) > 0.05f))
            {
                ac.changedAnimationParameters.Add(new AnimParamData(NetClip, AnimParamData.ValueTypes.DataFloat, id + speed / 10f));
                NetSent++;
                _sentClip = id;
                _sentSpeed = speed;
            }
            if (id != 0 && _restartSeq != _sentSeq)
            {
                ac.changedAnimationParameters.Add(new AnimParamData(NetSeq, AnimParamData.ValueTypes.DataFloat, _restartSeq % 1000));
                NetSent++;
                _sentSeq = _restartSeq;
            }
            float lean = Mathf.Round(_lean / 2f) * 2f;
            if (Mathf.Abs(lean - _sentLean) >= 2f)
            {
                ac.changedAnimationParameters.Add(new AnimParamData(NetLean, AnimParamData.ValueTypes.DataFloat, lean));
                NetSent++;
                _sentLean = lean;
            }
        }

        static bool ConnectionManagerHasPeers()
        {
            ConnectionManager cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return cm != null && (cm.IsClient || cm.ClientCount() > 0);
        }

        public void Receive(int type, float value)
        {
            switch (type)
            {
                case NetClip:
                    _netClip = Mathf.Clamp(Mathf.FloorToInt(value + 0.001f), 0, BodyAnimLibrary.Ids.Length - 1);
                    _netSpeed = Mathf.Max(0.1f, (value - _netClip) * 10f);
                    break;
                case NetSeq:
                    _netSeq = Mathf.RoundToInt(value);
                    break;
                case NetLean:
                    _netLean = value;
                    break;
            }
        }

        int _playedSeq = -1;

        void ChooseRemote()
        {
            if (_netClip == 0) { _target = 0f; }
            else
            {
                bool restart = _netSeq != _playedSeq;
                _playedSeq = _netSeq;
                Play(BodyAnimLibrary.Ids[_netClip], _netSpeed, restart);
            }
            _lean = Mathf.MoveTowards(_lean, _netLean, Time.deltaTime * 120f);
        }

        void Advance(float dt)
        {
            if (_clip == null) return;
            _t += dt * _speed;
            if (_clip.loop) _t %= Mathf.Max(_clip.length, 0.01f);
            else _t = Mathf.Min(_t, _clip.length);
        }

        void ApplyClip()
        {
            PkClipData c = _clip;
            float ft = _t * c.fps;
            int f0 = Mathf.Clamp(Mathf.FloorToInt(ft), 0, c.frameCount - 1);
            int f1 = c.loop ? (f0 + 1) % c.frameCount : Mathf.Min(f0 + 1, c.frameCount - 1);
            float k = ft - Mathf.Floor(ft);
            int nb = c.bones.Length;
            for (int b = 0; b < nb; b++)
            {
                if (!_bones.TryGetValue(c.bones[b], out Transform tr)) continue;
                Quaternion q = Quaternion.Slerp(Q(c, f0, b, nb), Q(c, f1, b, nb), k);
                if (_posing && _restRot.TryGetValue(c.bones[b], out Quaternion rest)) q = rest * q;
                tr.localRotation = Quaternion.Slerp(tr.localRotation, q, _w);
            }
            Vector3 hp = (_posing ? _restHips : HipsRest) + Vector3.Lerp(H(c, f0), H(c, f1), k);
            _hips.localPosition = Vector3.Lerp(_hips.localPosition, hp, _w);
        }

        static Quaternion Q(PkClipData c, int f, int b, int nb)
        {
            int i = (f * nb + b) * 4;
            return new Quaternion(c.rot[i], c.rot[i + 1], c.rot[i + 2], c.rot[i + 3]);
        }

        static Vector3 H(PkClipData c, int f)
        {
            int i = f * 3;
            return i + 2 < c.hips.Length ? new Vector3(c.hips[i], c.hips[i + 1], c.hips[i + 2]) : Vector3.zero;
        }

        /// <summary>Lean into wall runs; local player also puts the hands on ledges / walls (two-bone IK).</summary>
        void ApplyProcedural(float dt)
        {
            if (Mathf.Abs(_lean) > 0.1f)
                _hips.rotation = Quaternion.AngleAxis(-_lean, _e.transform.forward) * _hips.rotation;
            if (!_local) return;

            ParkourMotor m = PkRuntime.Motor;
            PkOutput o = PkRuntime.Out;
            bool hands = m.State == PkState.Hang || m.State == PkState.WallClimb || (m.State == PkState.Mantle && o.HandL.Weight > 0.3f);
            _armW = Mathf.MoveTowards(_armW, hands ? 1f : 0f, dt / 0.12f);
            if (_armW <= 0.001f || _lUp == null || _rUp == null) return;
            if (o.HandL.Weight > 0.001f)
            {
                ArmIK.SolveTwoBone(_lUp, _lLo, _lHand, PkRuntime.ToU(o.HandL.Point), _armW * o.HandL.Weight);
                ArmIK.OpenFingers(_lHand, _armW * o.HandL.Weight);
            }
            if (o.HandR.Weight > 0.001f)
            {
                ArmIK.SolveTwoBone(_rUp, _rLo, _rHand, PkRuntime.ToU(o.HandR.Point), _armW * o.HandR.Weight);
                ArmIK.OpenFingers(_rHand, _armW * o.HandR.Weight);
            }
        }
    }

    namespace Patches
    {
        /// <summary>Receive other players' parkour animation (sent as unknown DataFloat types; vanilla ignores them).</summary>
        [HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.SetFirstPersonView))]
        public static class ViewSwitchPatch
        {
            public static void Prefix(EntityPlayerLocal __instance)
            {
                try
                {
                    var d = BodyAnimDriver.Instance;
                    if (d != null && d.IsFor(__instance)) d.RestoreHeadArms();
                }
                catch (Exception ex)
                {
                    Log.Warning("[Parkour] view switch: " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(AvatarController), nameof(AvatarController.SetDataFloat))]
        public static class SetDataFloatPatch
        {
            public static bool Prefix(AvatarController __instance, AvatarController.DataTypes _type, float _value)
            {
                int t = (int)_type;
                if (t != BodyAnimDriver.NetClip && t != BodyAnimDriver.NetSeq && t != BodyAnimDriver.NetLean) return true;
                EntityAlive e = __instance.entity;
                if (e != null && e.isEntityRemote && e is EntityPlayer) BodyAnimDriver.ForRemote(e).Receive(t, _value);
                return false;
            }
        }
    }
}
