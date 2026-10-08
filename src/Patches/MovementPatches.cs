using System;
using HarmonyLib;
using UnityEngine;

namespace ParkourMovement.Patches
{
    /// <summary>Motor tick: after vanilla UpdateMotor/UpdateJump/UpdateForces/UpdateSliding, right before the move.</summary>
    [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.FixedMove))]
    public static class FixedMovePatch
    {
        static bool _failed;

        public static void Prefix(vp_FPController __instance)
        {
            if (_failed || !PkRuntime.IsLocalController(__instance)) return;
            try
            {
                PkRuntime.Tick(__instance);
            }
            catch (Exception ex)
            {
                _failed = true;
                PkRuntime.Motor.Reset();
                Log.Error("[Parkour] motor disabled after exception: " + ex);
            }
        }
    }

    /// <summary>No vanilla ground jump while the motor owns the body (slide jump, mantle, roll...).</summary>
    [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.CanStart_Jump))]
    public static class CanStartJumpPatch
    {
        public static bool Prefix(vp_FPController __instance, ref bool __result)
        {
            if (!PkRuntime.BlockJump || !PkRuntime.IsLocalController(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Landing roll scales the fall impact before the game turns it into damage buffs.</summary>
    [HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.FallImpact))]
    public static class FallImpactPatch
    {
        public static float LastScale = 1f;
        public static float LastSpeedIn;

        public static void Postfix(EntityPlayerLocal __instance)
        {
            try
            {
                PerkIntegration.AfterFallImpact(__instance);
            }
            catch (Exception ex)
            {
                Log.Error("[Parkour] FallImpact (tier 5): " + ex.Message);
            }
        }

        public static void Prefix(ref float speed)
        {
            try
            {
                LastSpeedIn = speed;
                float k = PkRuntime.Motor.OnLanding(PkInputLatch.CrouchHeld);
                LastScale = k;
                if (k < 1f && speed > 0f)
                {
                    speed *= k;
                    if (PkRuntime.Verbose) Log.Out($"[Parkour] roll landing: impact {LastSpeedIn:0.000} -> {speed:0.000}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[Parkour] FallImpact: " + ex.Message);
            }
        }
    }

    /// <summary>Latch input edges every frame; replace input while the self test drives the player.</summary>
    [HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.MoveByInput))]
    public static class MoveByInputPatch
    {
        public static void Prefix(EntityPlayerLocal __instance)
        {
            MovementInput mi = __instance.movementInput;
            if (mi == null) return;
            if (ScriptedInput.Active)
            {
                mi.moveForward = ScriptedInput.Forward;
                mi.moveStrafe = ScriptedInput.Strafe;
                mi.running = ScriptedInput.Run;
                mi.jump = ScriptedInput.Jump;
                mi.down = ScriptedInput.Crouch;
                mi.downToggle = false;
                mi.rotation = Vector3.zero;
            }
            bool crouch = mi.down || __instance.CrouchingLocked;
            PkInputLatch.Sample(mi.moveStrafe, mi.moveForward, mi.running, mi.jump, crouch);
        }
    }
}
