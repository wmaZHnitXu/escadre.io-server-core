// File: Core/Primitives/Quaternion.cs
using System;
using System.Runtime.CompilerServices;
using Core.Logging; // Assuming Core.Logging.Logger is available for warnings

namespace Core.Primitives
{
    /// <summary>
    /// Represents a rotation using single-precision floating-point numbers (x, y, z, w).
    /// </summary>
    [Serializable]
    public struct Quaternion : IEquatable<Quaternion>, IFormattable
    {
        public const float Epsilon = 1e-5f; // A small epsilon value for floating point comparisons
        public const float DotThreshold = 0.999999f; // Threshold for considering quaternions as aligned (cos(angle/2) close to 1)

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
                if (mag < Epsilon)
                {
                    // Logger.LogWarning("Normalizing a zero-magnitude quaternion. Returning identity."); // Optional warning
                    return Identity;
                }
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
                 if (sqrMag < Epsilon * Epsilon)
                 {
                    // Logger.LogWarning("Inverting a near-zero-magnitude quaternion. Returning identity."); // Optional warning
                    return Identity;
                 }
                 float invSqrMag = 1.0f / sqrMag;
                 return new Quaternion(-X * invSqrMag, -Y * invSqrMag, -Z * invSqrMag, W * invSqrMag);
             }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(Quaternion a, Quaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion AngleAxis(Vector3 axis, float angleDegrees)
        {
            if (axis.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon) return Identity; // Handle zero axis

            float halfAngleRad = angleDegrees * Deg2Rad * 0.5f;
            float sinHalfAngle = MathF.Sin(halfAngleRad);
            float cosHalfAngle = MathF.Cos(halfAngleRad);
            Vector3 normAxis = axis.Normalized;
            return new Quaternion(normAxis.X * sinHalfAngle, normAxis.Y * sinHalfAngle, normAxis.Z * sinHalfAngle, cosHalfAngle);
        }

        public static Quaternion Euler(float eulerXDegrees, float eulerYDegrees, float eulerZDegrees)
        {
            float halfX = eulerXDegrees * Deg2Rad * 0.5f;
            float halfY = eulerYDegrees * Deg2Rad * 0.5f;
            float halfZ = eulerZDegrees * Deg2Rad * 0.5f;
            float cx = MathF.Cos(halfX); float sx = MathF.Sin(halfX);
            float cy = MathF.Cos(halfY); float sy = MathF.Sin(halfY);
            float cz = MathF.Cos(halfZ); float sz = MathF.Sin(halfZ);
            return new Quaternion(
                sx * cy * cz - cx * sy * sz,
                cx * sy * cz + sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz
            ).Normalized; // Ensure result is normalized due to potential precision errors
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion Euler(Vector3 eulerAnglesDegrees) => Euler(eulerAnglesDegrees.X, eulerAnglesDegrees.Y, eulerAnglesDegrees.Z);

        public Vector3 ToEulerAngles()
        {
            // Ensure the quaternion is normalized for correct conversion
            Quaternion q = this.Normalized;

            float x, y, z;

            // Roll (x-axis rotation)
            float sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
            x = MathF.Atan2(sinr_cosp, cosr_cosp);

            // Pitch (y-axis rotation)
            float sinp = 2 * (q.W * q.Y - q.Z * q.X);
            if (MathF.Abs(sinp) >= 1.0f - Epsilon) // Use Epsilon for robust comparison
            {
                y = (sinp > 0 ? 1.0f : -1.0f) * (MathF.PI / 2.0f); // Use 90 degrees if gimbal lock
            }
            else
            {
                y = MathF.Asin(sinp);
            }

            // Yaw (z-axis rotation)
            float siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            z = MathF.Atan2(siny_cosp, cosy_cosp);

            return new Vector3(x * Rad2Deg, y * Rad2Deg, z * Rad2Deg);
        }

        /// <summary>
        /// Creates a rotation which rotates from fromDirection to toDirection.
        /// </summary>
        public static Quaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection)
        {
            Vector3 from = fromDirection.Normalized;
            Vector3 to = toDirection.Normalized;

            float dot = Vector3.Dot(from, to);

            if (dot > 1.0f - Epsilon) // Vectors are already aligned
            {
                return Identity;
            }
            else if (dot < -1.0f + Epsilon) // Vectors are opposite
            {
                // Need to find an arbitrary axis orthogonal to 'from'
                Vector3 axis = Vector3.Cross(Vector3.Right, from);
                if (axis.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon) // 'from' was aligned with Right or Left
                {
                    axis = Vector3.Cross(Vector3.Up, from);
                }
                return AngleAxis(axis.Normalized, 180f);
            }
            else
            {
                Vector3 rotAxis = Vector3.Cross(from, to).Normalized;
                float angleRad = MathF.Acos(dot); // Angle in radians

                float halfAngleRad = angleRad * 0.5f;
                float s = MathF.Sin(halfAngleRad);
                return new Quaternion(
                    rotAxis.X * s,
                    rotAxis.Y * s,
                    rotAxis.Z * s,
                    MathF.Cos(halfAngleRad)
                ).Normalized;
            }
        }


        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);

            // Ensure quaternions are normalized for Slerp
            Quaternion qa = a.Normalized;
            Quaternion qb = b.Normalized;

            float dot = Dot(qa, qb);

            // If the dot product is negative, the quaternions are more than 90 degrees apart.
            // Slerp will take the shorter path, so we invert one quaternion.
            if (dot < 0.0f)
            {
                qb = new Quaternion(-qb.X, -qb.Y, -qb.Z, -qb.W);
                dot = -dot;
            }

            // If the quaternions are very close, to avoid division by zero and precision issues,
            // use linear interpolation (Lerp) and normalize the result.
            if (dot > DotThreshold) // DotThreshold typically 0.9995f or higher
            {
                Quaternion result = new Quaternion(
                    qa.X + t * (qb.X - qa.X),
                    qa.Y + t * (qb.Y - qa.Y),
                    qa.Z + t * (qb.Z - qa.Z),
                    qa.W + t * (qb.W - qa.W)
                );
                return result.Normalized;
            }

            float theta_0 = MathF.Acos(dot);      // angle between input quaternions
            float theta = theta_0 * t;          // angle of the interpolated quaternion
            float sin_theta = MathF.Sin(theta);
            float sin_theta_0 = MathF.Sin(theta_0);

            if (MathF.Abs(sin_theta_0) < Epsilon) // Avoid division by zero if theta_0 is 0 or PI
            {
                return qa; // Quaternions are collinear
            }

            float scale0 = MathF.Cos(theta) - dot * sin_theta / sin_theta_0;
            float scale1 = sin_theta / sin_theta_0;

            return new Quaternion(
                (scale0 * qa.X) + (scale1 * qb.X),
                (scale0 * qa.Y) + (scale1 * qb.Y),
                (scale0 * qa.Z) + (scale1 * qb.Z),
                (scale0 * qa.W) + (scale1 * qb.W)
            ).Normalized; // Final normalization for robustness
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            if (forward.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon)
            {
                // Logger.LogWarning("LookRotation: Forward vector is zero. Returning identity."); // Optional warning
                return Identity;
            }

            Vector3 P = forward.Normalized;        // Primary axis (e.g., Z-axis)
            Vector3 S = Vector3.Cross(upwards, P).Normalized; // Secondary axis (e.g., X-axis)

            // If P and upwards are (anti-)parallel, S will be zero.
            // We need to pick a new S. If P is (0,1,0) or (0,-1,0), S can be (1,0,0).
            // Otherwise, S can be Cross( (0,1,0), P ).
            if (S.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon)
            {
                // This case happens if forward is (anti-)parallel to upwards.
                // Recompute S using a robust fallback for upwards if necessary.
                // If forward is (0,1,0) or (0,-1,0), then S should be based on global X or Z.
                if (MathF.Abs(P.Y) > 1.0f - Vector3.Epsilon) // Forward is mostly up/down
                {
                     // If forward is (0,1,0), S should be (1,0,0) (right) assuming standard up (0,1,0)
                     // If forward is (0,-1,0), S should be (1,0,0) (right) assuming standard up (0,1,0)
                     // This means the "new up" vector U would be -forward.Cross(S)
                     // Let's pick S = Vector3.Right or Vector3.Forward if P is aligned with those too.
                    if(MathF.Abs(P.X) < Vector3.Epsilon && MathF.Abs(P.Z) < Vector3.Epsilon) // P is purely Y axis
                    {
                        // If P is (0,1,0), S can be (1,0,0). U will be (0,0,-1)
                        // If P is (0,-1,0), S can be (1,0,0). U will be (0,0,1)
                        // In general, if P is (0,y,0), P = (0,1,0) or (0,-1,0)
                        // S = (1,0,0)
                        S = Vector3.Right;
                    } else { // P has some X or Z component, but is still mostly Y
                        S = Vector3.Cross(Vector3.Up, P).Normalized; // Try to get an S perpendicular to global UP and P
                        if (S.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon) // Still no good S (P is likely global UP)
                        {
                             S = Vector3.Right; // Final fallback for S
                        }
                    }
                }
                else // Forward is not primarily up/down, the original upwards was just aligned with P
                {
                    S = Vector3.Cross(Vector3.Up, P).Normalized; // Default S calculation if original upwards was bad
                }
            }

            Vector3 U = Vector3.Cross(P, S); // Tertiary axis (e.g., Y-axis), U should already be normalized

            // Construct rotation matrix components
            float m00 = S.X; float m01 = U.X; float m02 = P.X;
            float m10 = S.Y; float m11 = U.Y; float m12 = P.Y;
            float m20 = S.Z; float m21 = U.Z; float m22 = P.Z;

            float trace = m00 + m11 + m22;
            float x, y, z, w;

            if (trace > 0f)
            {
                float s_val = 0.5f / MathF.Sqrt(trace + 1.0f);
                w = 0.25f / s_val;
                x = (m21 - m12) * s_val;
                y = (m02 - m20) * s_val;
                z = (m10 - m01) * s_val;
            }
            else
            {
                if (m00 > m11 && m00 > m22)
                {
                    float s_val = 2.0f * MathF.Sqrt(1.0f + m00 - m11 - m22);
                    w = (m21 - m12) / s_val;
                    x = 0.25f * s_val;
                    y = (m01 + m10) / s_val;
                    z = (m02 + m20) / s_val;
                }
                else if (m11 > m22)
                {
                    float s_val = 2.0f * MathF.Sqrt(1.0f + m11 - m00 - m22);
                    w = (m02 - m20) / s_val;
                    x = (m01 + m10) / s_val;
                    y = 0.25f * s_val;
                    z = (m12 + m21) / s_val;
                }
                else
                {
                    float s_val = 2.0f * MathF.Sqrt(1.0f + m22 - m00 - m11);
                    w = (m10 - m01) / s_val;
                    x = (m02 + m20) / s_val;
                    y = (m12 + m21) / s_val;
                    z = 0.25f * s_val;
                }
            }
            return new Quaternion(x, y, z, w).Normalized; // Ensure normalization
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            // Ensure quaternions are normalized for accurate angle calculation
            Quaternion qa = a.Normalized;
            Quaternion qb = b.Normalized;

            float dot = Dot(qa, qb);

            // The dot product can be slightly outside [-1, 1] due to floating point inaccuracies.
            // Clamp it to prevent Acos from returning NaN.
            dot = Math.Clamp(dot, -1.0f, 1.0f);

            // Since q and -q represent the same rotation, we use Abs(dot) if we want the shortest angle.
            // However, for Angle(a, b), we typically want the angle representing the rotation from a to b.
            // The angle is 2 * acos(dot). If dot is negative, it means the angle is > 90 degrees.
            // If we always want the acute angle (e.g. difference regardless of path), use Math.Abs(dot).
            // For RotateTowards, we need the actual angle (up to 180 deg for theta/2, so 360 for theta)
            // The angle between two quaternions a and b is 2 * acos(|a·b|) if we consider shortest path,
            // or 2 * acos(a·b) if we allow the longer path. Standard libraries often give shortest path.
            // Unity's Quaternion.Angle is 2 * Acos(Min(1f, Abs(Dot(q1, q2)))) * Rad2Deg;
            // This ensures the smallest angle between the two orientations.

            float dotAbs = MathF.Abs(dot); // Use absolute dot for the shortest angle.
            // Clamp dotAbs again just in case, though dot already clamped.
            if (dotAbs > 1.0f - Epsilon) // If they are effectively the same or opposite
                return 0.0f; // or 180f if dot was negative and we didn't take Abs - but Abs makes it 0.

            return 2.0f * MathF.Acos(dotAbs) * Rad2Deg;
        }

        /// <summary>
        /// Rotates a rotation 'from' towards 'to' by a maximum of 'maxDegreesDelta'.
        /// </summary>
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            // Ensure inputs are normalized
            Quaternion qFrom = from.Normalized;
            Quaternion qTo = to.Normalized;

            float angle = Quaternion.Angle(qFrom, qTo); // This gives the shortest angle in degrees

            if (angle < Epsilon) // Already at or very close to the target rotation
            {
                return qTo;
            }

            // If maxDegreesDelta allows to reach the target in one step or less
            if (maxDegreesDelta >= angle)
            {
                return qTo;
            }

            // Calculate interpolation factor 't'
            // t = 0 means 'from', t = 1 means 'to'
            float t = maxDegreesDelta / angle; // This t is for Slerp based on the angle

            return Slerp(qFrom, qTo, t);
        }


        // --- Operators ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion operator *(Quaternion b, Quaternion a) // Standard Hamilton product: q1 * q2
        {
            return new Quaternion(
                b.W*a.X + b.X*a.W + b.Y*a.Z - b.Z*a.Y,
                b.W*a.Y - b.X*a.Z + b.Y*a.W + b.Z*a.X,
                b.W*a.Z + b.X*a.Y - b.Y*a.X + b.Z*a.W,
                b.W*a.W - b.X*a.X - b.Y*a.Y - b.Z*a.Z
            ); // This should be normalized if inputs are normalized and precision is critical.
               // However, if used in accumulation, normalize at the end.
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator *(Quaternion rot, Vector3 point)
        {
            // Ensure quaternion is normalized for correct vector rotation
            Quaternion q = rot.Normalized;

            float x = q.X * 2f;
            float y = q.Y * 2f;
            float z = q.Z * 2f;
            float xx = q.X * x;
            float yy = q.Y * y;
            float zz = q.Z * z;
            float xy = q.X * y;
            float xz = q.X * z;
            float yz = q.Y * z;
            float wx = q.W * x;
            float wy = q.W * y;
            float wz = q.W * z;

            float X_res = (1f - (yy + zz)) * point.X + (xy - wz) * point.Y + (xz + wy) * point.Z;
            float Y_res = (xy + wz) * point.X + (1f - (xx + zz)) * point.Y + (yz - wx) * point.Z;
            float Z_res = (xz - wy) * point.X + (yz + wx) * point.Y + (1f - (xx + yy)) * point.Z;
            Vector3 res = new Vector3(X_res, Y_res, Z_res);
            return res;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Quaternion lhs, Quaternion rhs)
        {
            // Quaternions are equal if they represent the same rotation.
            // This means q == -q. So, Dot(lhs, rhs) should be close to 1 or -1.
            // Thus, Abs(Dot(lhs, rhs)) should be close to 1.
            float dot = Dot(lhs.Normalized, rhs.Normalized); // Compare normalized versions
            return MathF.Abs(MathF.Abs(dot) - 1.0f) < Epsilon * Epsilon; // More robust comparison
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Quaternion lhs, Quaternion rhs) => !(lhs == rhs);

        // --- Equality and Formatting ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Quaternion other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Quaternion other) // Same logic as operator==
        {
            float dot = Dot(this.Normalized, other.Normalized);
            return MathF.Abs(MathF.Abs(dot) - 1.0f) < Epsilon * Epsilon;
        }
        public override int GetHashCode()
        {
            // A simple hash code. For a hash code that is the same for q and -q,
            // you might normalize and ensure W is positive before hashing.
            // This basic one is fine for dictionary keys if you always store normalized/consistent form.
            return HashCode.Combine(X, Y, Z, W);
        }
        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider formatProvider) => $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)}, {W.ToString(format, formatProvider)})";
    }
}