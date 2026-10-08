using System;

namespace ParkourMovement.Core
{
    /// <summary>Minimal float vector so the motor builds and tests without UnityEngine. Y is up.</summary>
    public struct V3
    {
        public float X, Y, Z;

        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly V3 Zero = new V3(0f, 0f, 0f);
        public static readonly V3 Up = new V3(0f, 1f, 0f);

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public static V3 operator *(V3 a, float s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator *(float s, V3 a) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator /(V3 a, float s) => new V3(a.X / s, a.Y / s, a.Z / s);

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public float SqrLength => X * X + Y * Y + Z * Z;
        public V3 Flat => new V3(X, 0f, Z);
        public float FlatLength => (float)Math.Sqrt(X * X + Z * Z);

        public V3 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-6f ? this / l : Zero;
            }
        }

        public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static V3 Cross(V3 a, V3 b) =>
            new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Rotate a flat direction towards another flat direction by at most maxRad radians.</summary>
        public static V3 RotateFlatTowards(V3 from, V3 to, float maxRad)
        {
            from = from.Flat.Normalized;
            to = to.Flat.Normalized;
            if (from.SqrLength < 1e-6f) return to;
            if (to.SqrLength < 1e-6f) return from;
            float a = (float)Math.Atan2(from.X, from.Z);
            float b = (float)Math.Atan2(to.X, to.Z);
            float d = b - a;
            while (d > Math.PI) d -= (float)(2 * Math.PI);
            while (d < -Math.PI) d += (float)(2 * Math.PI);
            if (d > maxRad) d = maxRad;
            if (d < -maxRad) d = -maxRad;
            float r = a + d;
            return new V3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
        }

        public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
    }
}
