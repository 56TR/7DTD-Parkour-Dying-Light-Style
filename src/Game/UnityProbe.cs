using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>World probe on UnityEngine.Physics, same layer mask the controller uses for ground checks.</summary>
    public sealed class UnityProbe : IWorldProbe
    {
        // vp_FPController ground / ceiling mask: layers 3,15,16,19,21,23,30
        const int ControllerMask = 1084850184;
        // never run on / grab: remote players (3), entity capsules (15), large entity blockers (19), trees (23)
        const int NotASurface = (1 << 3) | (1 << 15) | (1 << 19) | (1 << 23);

        static readonly UnityProbe Instance = new UnityProbe();
        int _mask = ControllerMask;      // occupancy: anything that blocks the body
        int _surface = ControllerMask;   // walls / ledges: world geometry only

        public static UnityProbe For(vp_FPController c)
        {
            // never collide with our own capsule
            Instance._mask = ControllerMask & ~(1 << c.gameObject.layer);
            Instance._surface = Instance._mask & ~NotASurface;
            return Instance;
        }

        public bool Ray(V3 origin, V3 dir, float maxDist, out Hit hit)
        {
            hit = default;
            // first hit on anything solid; an entity in front hides the wall behind it
            if (!Physics.Raycast(PkRuntime.ToU(origin), PkRuntime.ToU(dir), out RaycastHit h, maxDist, _mask, QueryTriggerInteraction.Ignore))
                return false;
            if (((1 << h.collider.gameObject.layer) & _surface) == 0 || h.collider.GetComponentInParent<Entity>() != null)
                return false;
            hit = new Hit { Point = PkRuntime.ToV(h.point), Normal = PkRuntime.ToV(h.normal), Distance = h.distance };
            return true;
        }

        static readonly System.Collections.Generic.List<Entity> _ents = new System.Collections.Generic.List<Entity>();

        /// <summary>Living zombies and animals around center (Unity space). Positions come from the game's entity list, not physics.</summary>
        public int Enemies(V3 center, float radius, EnemyInfo[] buffer)
        {
            World world = GameManager.Instance?.World;
            if (world == null) return 0;
            Vector3 c = PkRuntime.ToU(center) + Origin.position;
            _ents.Clear();
            world.GetEntitiesInBounds(typeof(EntityAlive), new Bounds(c, Vector3.one * (radius * 2f + 4f)), _ents);
            int n = 0;
            foreach (Entity ent in _ents)
            {
                if (n >= buffer.Length) break;
                if (!(ent is EntityEnemy) && !(ent is EntityAnimal)) continue;
                var e = (EntityAlive)ent;
                if (e.IsDead()) continue;
                Bounds bb = e.boundingBox;
                Vector3 feet = new Vector3(e.position.x, bb.min.y, e.position.z) - Origin.position;
                if ((feet - PkRuntime.ToU(center)).magnitude > radius + bb.size.y) continue;
                bool down = e.bodyDamage.CurrentStun != EnumEntityStunType.None || (e.emodel != null && e.emodel.IsRagdollActive);
                buffer[n++] = new EnemyInfo
                {
                    Id = e.entityId,
                    Feet = PkRuntime.ToV(feet),
                    Height = Mathf.Max(0.4f, bb.size.y),
                    Radius = Mathf.Clamp(Mathf.Max(bb.extents.x, bb.extents.z), 0.2f, 1.0f),
                    Down = down,
                };
            }
            return n;
        }

        public bool HeadroomFree(V3 feet, float radius, float height)
        {
            Vector3 f = PkRuntime.ToU(feet);
            Vector3 p1 = f + Vector3.up * (radius + 0.02f);
            Vector3 p2 = f + Vector3.up * Mathf.Max(radius + 0.02f, height - radius);
            return !Physics.CheckCapsule(p1, p2, radius, _surface, QueryTriggerInteraction.Ignore);
        }

        public bool CapsuleFree(V3 feet, float radius, float height)
        {
            Vector3 f = PkRuntime.ToU(feet);
            Vector3 p1 = f + Vector3.up * (radius + 0.02f);
            Vector3 p2 = f + Vector3.up * Mathf.Max(radius + 0.02f, height - radius);
            return !Physics.CheckCapsule(p1, p2, radius, _mask, QueryTriggerInteraction.Ignore);
        }

        public bool CapsuleCast(V3 feet, float radius, float height, V3 dir, float maxDist, out Hit hit)
        {
            hit = default;
            Vector3 f = PkRuntime.ToU(feet);
            Vector3 p1 = f + Vector3.up * (radius + 0.02f);
            Vector3 p2 = f + Vector3.up * Mathf.Max(radius + 0.02f, height - radius);
            if (!Physics.CapsuleCast(p1, p2, radius, PkRuntime.ToU(dir), out RaycastHit h, maxDist, _mask, QueryTriggerInteraction.Ignore))
                return false;
            hit = new Hit { Point = PkRuntime.ToV(h.point), Normal = PkRuntime.ToV(h.normal), Distance = h.distance };
            return true;
        }
    }
}
