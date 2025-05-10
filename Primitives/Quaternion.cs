using System;
using System.Runtime.CompilerServices;

namespace Core.Primitives
{
    /// <summary>
    /// Represents a rotation using single-precision floating-point numbers (x, y, z, w).
    /// </summary>
    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>, IFormattable
    {
        public const float Epsilon = 1e-5f;
        private const float Deg2Rad = MathF.PI / 180.0f;
        private const float Rad2Deg = 180.0f / MathF.PI;

        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float W;

        public static readonly Quaternion Identity = new Quaternion(0f, 0f, 0f, 1f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float SqrMagnitude { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => X * X + Y * Y + Z * Z + W * W; }
        public float Magnitude { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => MathF.Sqrt(SqrMagnitude); }

        public Quaternion Normalized
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                float mag = Magnitude;
                if (mag < Epsilon) return Identity;
                float invMag = 1.0f / mag;
                return new Quaternion(X * invMag, Y * invMag, Z * invMag, W * invMag);
            }
        }

        public Quaternion Conjugate { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new Quaternion(-X, -Y, -Z, W); }

        public Quaternion Inverse
        {
             [MethodImpl(MethodImplOptions.AggressiveInlining)]
             get
             {
                 float sqrMag = SqrMagnitude;
                 if (sqrMag < Epsilon * Epsilon) return Identity;
                 float invSqrMag = 1.0f / sqrMag;
                 return new Quaternion(-X * invSqrMag, -Y * invSqrMag, -Z * invSqrMag, W * invSqrMag);
             }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(Quaternion a, Quaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion AngleAxis(Vector3 axis, float angleDegrees)
        {
            float halfAngleRad = angleDegrees * Deg2Rad * 0.5f;
            float sinHalfAngle = MathF.Sin(halfAngleRad);
            float cosHalfAngle = MathF.Cos(halfAngleRad);
            // Vector3 normAxis = axis.Normalized; // Uncomment if axis might not be normalized
            return new Quaternion(axis.X * sinHalfAngle, axis.Y * sinHalfAngle, axis.Z * sinHalfAngle, cosHalfAngle);
        }

        public static Quaternion Euler(float eulerXDegrees, float eulerYDegrees, float eulerZDegrees)
        {
            float halfX = eulerXDegrees * Deg2Rad * 0.5f;
            float halfY = eulerYDegrees * Deg2Rad * 0.5f;
            float halfZ = eulerZDegrees * Deg2Rad * 0.5f;
            float cx = MathF.Cos(halfX); float sx = MathF.Sin(halfX);
            float cy = MathF.Cos(halfY); float sy = MathF.Sin(halfY);
            float cz = MathF.Cos(halfZ); float sz = MathF.Sin(halfZ);
            return new Quaternion(sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion Euler(Vector3 eulerAnglesDegrees) => Euler(eulerAnglesDegrees.X, eulerAnglesDegrees.Y, eulerAnglesDegrees.Z);

        public Vector3 ToEulerAngles()
        {
            Quaternion q = this.Normalized;
            float x, y, z;
            float sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
            x = MathF.Atan2(sinr_cosp, cosr_cosp);
            float sinp = 2 * (q.W * q.Y - q.Z * q.X);
            if (MathF.Abs(sinp) >= 1.0f - Epsilon)
            {
                y = (sinp > 0 ? 1.0f : -1.0f) * (MathF.PI / 2.0f);
                 z = MathF.Atan2(2 * (q.X * q.Y + q.W * q.Z), q.W * q.W + q.X * q.X - q.Y * q.Y - q.Z * q.Z); // Yaw when locked
            }
            else
            {
                y = MathF.Asin(sinp);
                float siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
                float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
                z = MathF.Atan2(siny_cosp, cosy_cosp);
            }
            return new Vector3(x * Rad2Deg, y * Rad2Deg, z * Rad2Deg);
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float dot = Dot(a, b);
            if (dot < 0.0f) { b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
            const float threshold = 0.9995f;
            if (dot > threshold)
            {
                Quaternion result = new Quaternion(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z), a.W + t * (b.W - a.W));
                return result.Normalized;
            }
            float theta_0 = MathF.Acos(dot); float theta = theta_0 * t;
            float sin_theta = MathF.Sin(theta); float sin_theta_0 = MathF.Sin(theta_0);
            if (sin_theta_0 < Epsilon) return a.Normalized;
            float scale0 = MathF.Cos(theta) - dot * sin_theta / sin_theta_0;
            float scale1 = sin_theta / sin_theta_0;
            return new Quaternion((scale0 * a.X) + (scale1 * b.X), (scale0 * a.Y) + (scale1 * b.Y), (scale0 * a.Z) + (scale1 * b.Z), (scale0 * a.W) + (scale1 * b.W));
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            if (forward.SqrMagnitude < Epsilon * Epsilon) return Identity;
            forward = forward.Normalized;
            Vector3 right = Vector3.Cross(upwards, forward);
            if (right.SqrMagnitude < Epsilon * Epsilon)
            {
                if(MathF.Abs(forward.X) > MathF.Abs(forward.Z)) right = new Vector3(-forward.Y, forward.X, 0f);
                else right = new Vector3(0f, -forward.Z, forward.Y);
                if(right.SqrMagnitude < Epsilon * Epsilon) right = Vector3.Right; // Default if forward is vertical
            }
            right = right.Normalized;
            upwards = Vector3.Cross(forward, right); // Already normalized

            float m00 = right.X;   float m01 = upwards.X; float m02 = forward.X;
            float m10 = right.Y;   float m11 = upwards.Y; float m12 = forward.Y;
            float m20 = right.Z;   float m21 = upwards.Z; float m22 = forward.Z;
            float trace = m00 + m11 + m22; float x, y, z, w;
            if (trace > 0f) { float s = 0.5f / MathF.Sqrt(trace + 1.0f); w = 0.25f / s; x = (m21 - m12) * s; y = (m02 - m20) * s; z = (m10 - m01) * s; }
            else {
                if (m00 > m11 && m00 > m22) { float s = 2.0f * MathF.Sqrt(1.0f + m00 - m11 - m22); w = (m21 - m12) / s; x = 0.25f * s; y = (m01 + m10) / s; z = (m02 + m20) / s; }
                else if (m11 > m22) { float s = 2.0f * MathF.Sqrt(1.0f + m11 - m00 - m22); w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25f * s; z = (m12 + m21) / s; }
                else { float s = 2.0f * MathF.Sqrt(1.0f + m22 - m00 - m11); w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25f * s; }
            }
            return new Quaternion(x, y, z, w).Normalized; // Explicit normalization for robustness
        }

        // --- Operators ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion operator *(Quaternion b, Quaternion a) => new Quaternion(b.W*a.X + b.X*a.W + b.Y*a.Z - b.Z*a.Y, b.W*a.Y - b.X*a.Z + b.Y*a.W + b.Z*a.X, b.W*a.Z + b.X*a.Y - b.Y*a.X + b.Z*a.W, b.W*a.W - b.X*a.X - b.Y*a.Y - b.Z*a.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator *(Quaternion rot, Vector3 point) // Vector3 из Core.Primitives
        {
            float x2 = rot.X * 2f; float y2 = rot.Y * 2f; float z2 = rot.Z * 2f;
            float xx = rot.X * x2; float yy = rot.Y * y2; float zz = rot.Z * z2;
            float xy = rot.X * y2; float xz = rot.X * z2; float yz = rot.Y * z2;
            float wx = rot.W * x2; float wy = rot.W * y2; float wz = rot.W * z2;

            float resX = (1f - (yy + zz)) * point.X + (xy - wz) * point.Y + (xz + wy) * point.Z;
            float resY = (xy + wz) * point.X + (1f - (xx + zz)) * point.Y + (yz - wx) * point.Z;
            float resZ = (xz - wy) * point.X + (yz + wx) * point.Y + (1f - (xx + yy)) * point.Z;

            return new Vector3(resX, resY, resZ); // Создаем новый Vector3 через конструктор
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Quaternion lhs, Quaternion rhs) => MathF.Abs(MathF.Abs(Dot(lhs, rhs)) - 1.0f) < Epsilon * Epsilon;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Quaternion lhs, Quaternion rhs) => !(lhs == rhs);

        // --- Equality and Formatting ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Quaternion other && Equals(other);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Quaternion other) => MathF.Abs(MathF.Abs(Dot(this, other)) - 1.0f) < Epsilon * Epsilon;
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider formatProvider) => $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)}, {W.ToString(format, formatProvider)})";
    }
}