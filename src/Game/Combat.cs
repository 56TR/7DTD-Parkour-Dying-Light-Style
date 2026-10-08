using System;
using System.Collections.Generic;
using HarmonyLib;
using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>Applies motor strikes as real game damage from the local player, and lets the player pass through a vaulted enemy.</summary>
    public static class Combat
    {
        // set only while our own DamageEntity call runs: turns that hit into a knockdown
        public static int PendingTarget = -1;
        public static float PendingStun;

        public static int LastStrikeTarget = -1;
        public static StrikeKind LastStrikeKind;

        public static int LastStrikeCount;

        public static void Apply(EntityPlayerLocal p, in PkOutput o)
        {
            if (o.Strike == StrikeKind.None) return;
            int n = o.StrikeTargets != null ? o.StrikeCount : 1;
            LastStrikeCount = 0;
            for (int k = 0; k < n; k++)
            {
                int id = o.StrikeTargets != null ? o.StrikeTargets[k] : o.StrikeTarget;
                if (!(GameManager.Instance.World?.GetEntity(id) is EntityAlive target) || target.IsDead()) continue;
                Hit(p, target, o);
                LastStrikeCount++;
                LastStrikeTarget = target.entityId;
            }
            LastStrikeKind = o.Strike;
        }

        static void Hit(EntityPlayerLocal p, EntityAlive target, in PkOutput o)
        {
            Vector3 dir = PkRuntime.ToU(o.StrikeDir);
            var src = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Bashing, p.entityId, dir);
            bool throwIt = o.KnockSpeed > 0f && o.StrikeStun > 0f;
            // a thrown target gets its ragdoll from Throw(), so the hit itself must not start one with a random impulse
            PendingTarget = !throwIt && o.StrikeStun > 0f ? target.entityId : -1;
            PendingStun = o.StrikeStun;
            // throw first: if the damage itself crossed the knockdown threshold, the ragdoll is already ours
            if (throwIt) Throw(target, dir, o.KnockSpeed, o.KnockLift, o.StrikeStun);
            try
            {
                target.DamageEntity(src, Math.Max(1, o.StrikeDamage), _criticalHit: !throwIt, _impulseScale: throwIt ? 0f : 1f);
            }
            finally
            {
                PendingTarget = -1;
            }
            if (PkRuntime.Verbose) Log.Out($"[Parkour] {o.Strike} -> {target.EntityName} ({target.entityId}) dmg {o.StrikeDamage} stun {o.StrikeStun} knock {o.KnockSpeed}");
        }

        /// <summary>
        /// Knock the target down as a ragdoll flying away along dir. With no body part given, the game sets this as the
        /// ragdoll's velocity (not a mass-clamped impulse); on a multiplayer client it is forwarded to the server.
        /// </summary>
        public static void Throw(EntityAlive target, Vector3 dir, float speed, float lift, float stunTime)
        {
            if (target.emodel == null) return;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            Vector3 v = dir.normalized * speed + Vector3.up * lift;
            target.emodel.DoRagdoll(EModelBase.RagdollMode.Default, Mathf.Max(stunTime, 0.5f), EnumBodyPartHit.None, v, Vector3.zero, false);
            if (target.isEntityRemote) return;   // a client only asks the server; it applies the plain throw
            // topple: the upper body flies a bit faster than the legs, so it goes over backwards instead of sliding upright
            Vector3 d = dir.normalized;
            foreach (Rigidbody rb in target.emodel.GetComponentsInChildren<Rigidbody>())
            {
                if (rb == null || rb.isKinematic) continue;
                string n = rb.name.ToLowerInvariant();
                if (n.Contains("spine") || n.Contains("chest") || n.Contains("neck") || n.Contains("head") || n.Contains("arm"))
                    rb.velocity += d * (speed * 0.35f);
                else if (n.Contains("leg") || n.Contains("foot") || n.Contains("calf") || n.Contains("thigh") || n.Contains("shin"))
                    rb.velocity -= d * (speed * 0.25f);
            }
        }

        public static void ForceStun(ref DamageResponse r, EntityAlive target)
        {
            if (PendingTarget < 0 || target == null || target.entityId != PendingTarget || r.Fatal) return;
            r.Stun = EnumEntityStunType.Prone;
            r.StunDuration = PendingStun;
            r.Critical = true;   // critical bashing prone = ragdoll with the hit impulse
        }

        // ---- pass-through (vault over an enemy)

        static readonly List<Collider> _ignored = new List<Collider>();
        static int _ignoredTarget = -1;
        static float _releaseAt;

        public static void UpdatePassThrough(vp_FPController c, in PkOutput o)
        {
            if (o.PassThrough)
            {
                _releaseAt = Time.time + 0.3f;
                if (_ignoredTarget == o.PassThroughTarget) return;
                Release(c);
                if (!(GameManager.Instance.World?.GetEntity(o.PassThroughTarget) is Entity e)) return;
                foreach (Collider col in e.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || col == c.CharacterController) continue;
                    Physics.IgnoreCollision(c.CharacterController, col, true);
                    _ignored.Add(col);
                }
                _ignoredTarget = o.PassThroughTarget;
            }
            else if (_ignoredTarget >= 0 && Time.time >= _releaseAt)
            {
                Release(c);
            }
        }

        static void Release(vp_FPController c)
        {
            foreach (Collider col in _ignored)
                if (col != null) Physics.IgnoreCollision(c.CharacterController, col, false);
            _ignored.Clear();
            _ignoredTarget = -1;
        }
    }

    namespace Patches
    {
        /// <summary>The response that goes to the server / other players carries our knockdown.</summary>
        [HarmonyPatch(typeof(EntityAlive), nameof(EntityAlive.damageEntityLocal))]
        public static class DamageEntityLocalPatch
        {
            public static void Postfix(EntityAlive __instance, ref DamageResponse __result)
            {
                Combat.ForceStun(ref __result, __instance);
            }
        }

        /// <summary>The response applied locally (inside damageEntityLocal) carries it too.</summary>
        [HarmonyPatch(typeof(EntityAlive), nameof(EntityAlive.ProcessDamageResponseLocal))]
        public static class ProcessDamageResponseLocalPatch
        {
            public static void Prefix(EntityAlive __instance, ref DamageResponse _dmResponse)
            {
                Combat.ForceStun(ref _dmResponse, __instance);
            }
        }

        /// <summary>Vanilla crouch start/stop re-applies collider heights; keep the low slide collider.</summary>
        [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.RefreshColliders))]
        public static class RefreshCollidersPatch
        {
            public static void Postfix(vp_FPController __instance)
            {
                if (PkRuntime.LowColliderHeight > 0f && PkRuntime.IsLocalController(__instance))
                    PkRuntime.ApplyLowCollider(__instance, PkRuntime.LowColliderHeight);
            }
        }
    }
}
