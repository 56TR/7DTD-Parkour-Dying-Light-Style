using System;
using HarmonyLib;
using UnityEngine;

namespace ParkourMovement.Patches
{
    /// <summary>First-person camera feel: roll on wall runs, FOV kick with speed, lowered eye in slides, flip on landing roll.</summary>
    [HarmonyPatch(typeof(vp_FPCamera), nameof(vp_FPCamera.LateUpdate))]
    public static class CameraPatch
    {
        static float _roll, _fov, _height, _spin, _pitch;
        static bool _fovTouched;
        static Camera _cam;

        public static float CurrentRoll => _roll;
        public static float CurrentFovAdd => _fov;
        public static float CurrentPitch => _pitch;

        public static void Postfix(vp_FPCamera __instance)
        {
            try
            {
                if (__instance.FPController == null || !__instance.FPController.enabled || __instance.FPController.localPlayer == null) return;
                if (!__instance.Player.IsFirstPerson.Get() || __instance.Locked3rdPerson || __instance.Player.Driving.Active) return;

                var o = PkRuntime.Out;
                float dt = Time.deltaTime;
                // Options > Parkour > Camera effects off: tilt, FOV kick, eye drop, roll flip all ease back to zero
                bool fx = PkSettings.Enabled && PkSettings.CameraEffects;
                _roll = Mathf.Lerp(_roll, fx ? o.CamRoll : 0f, 1f - Mathf.Exp(-10f * dt));
                _fov = Mathf.Lerp(_fov, fx ? o.CamFovAdd : 0f, 1f - Mathf.Exp(-6f * dt));
                _height = Mathf.Lerp(_height, fx ? o.CamHeight : 0f, 1f - Mathf.Exp(-14f * dt));
                _pitch = Mathf.Lerp(_pitch, fx ? o.CamPitch : 0f, 1f - Mathf.Exp(-16f * dt));
                // the roll spin is already a timed curve, follow it directly and snap back when it ends
                _spin = fx && o.CamPitchSpin > 0f ? o.CamPitchSpin : 0f;

                Transform t = __instance.Transform;
                if (Mathf.Abs(_height) > 0.001f) t.position += Vector3.up * _height;
                if (Mathf.Abs(_roll) > 0.01f || _spin > 0.01f || Mathf.Abs(_pitch) > 0.01f) t.rotation = t.rotation * Quaternion.Euler(_spin + _pitch, 0f, _roll);

                ApplyFov(__instance);
                ArmIK.Apply(__instance.FPController.localPlayer, o, dt);
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour] camera: " + ex.Message);
            }
        }

        static void ApplyFov(vp_FPCamera cam)
        {
            if (_cam == null) _cam = cam.GetComponent<Camera>();
            if (_cam == null) return;
            EntityPlayerLocal p = cam.FPController.localPlayer;
            bool zooming = cam.m_FinalZoomTime > Time.time || p.AimingGun || p.isAimingScoped;
            if (zooming)
            {
                _fovTouched = false;
                return;
            }
            float baseFov = Constants.cDefaultCameraFieldOfView + cam.ZoomOffset;
            if (_fov > 0.05f)
            {
                _cam.fieldOfView = baseFov + _fov;
                _fovTouched = true;
            }
            else if (_fovTouched)
            {
                _cam.fieldOfView = baseFov;
                _fovTouched = false;
            }
        }
    }

    /// <summary>
    /// Hanging / climbing: the hands are fixed on the edge, so the body must keep facing the wall. First person:
    /// look yaw limited to HangLookYaw (the body follows the camera). Third person: the body turns at most
    /// HangBodyYaw, the camera still orbits freely. Without it the arms cross over the head.
    /// </summary>
    [HarmonyPatch(typeof(vp_FPCamera), nameof(vp_FPCamera.Update))]
    public static class YawLockPatch
    {
        public static void Postfix(vp_FPCamera __instance)
        {
            try
            {
                if (__instance.FPController == null || !__instance.FPController.enabled || __instance.FPController.localPlayer == null) return;
                if (__instance.Locked3rdPerson || __instance.Player.Driving.Active || Time.timeScale <= 0f) return;
                float turn = PkRuntime.PendingYaw;
                PkRuntime.PendingYaw = 0f;
                if (__instance.Player.IsFirstPerson.Get()) __instance.m_Yaw += turn;   // third person: the body lock below turns it
                if (!PkRuntime.Tuning.Enabled || !PkRuntime.Motor.TryFacingLock(out var d) || d.FlatLength < 0.5f) return;
                float wallYaw = Mathf.Atan2(d.X, d.Z) * Mathf.Rad2Deg;
                if (__instance.Player.IsFirstPerson.Get())
                {
                    float lim = PkRuntime.Tuning.HangLookYaw;
                    float delta = Mathf.DeltaAngle(wallYaw, __instance.m_Yaw);
                    if (Mathf.Abs(delta) > lim) __instance.m_Yaw -= delta - Mathf.Sign(delta) * lim;
                }
                else
                {
                    Transform parent = __instance.Parent;
                    float lim = PkRuntime.Tuning.HangBodyYaw;
                    float cur = parent.eulerAngles.y;
                    float delta = Mathf.DeltaAngle(wallYaw, cur);
                    float want = wallYaw + Mathf.Clamp(delta, -lim, lim);
                    float y = Mathf.LerpAngle(cur, want, 1f - Mathf.Exp(-14f * Time.deltaTime));
                    __instance.targetRotation3P = wallYaw + Mathf.Clamp(Mathf.DeltaAngle(wallYaw, __instance.targetRotation3P), -lim, lim);
                    __instance.Player.Rotation.Set(new Vector2(__instance.m_Pitch, y));
                    parent.rotation = Quaternion.Euler(0f, y, 0f);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour] yaw lock: " + ex.Message);
            }
        }
    }
}
