// File: Core/Primitives/Vector3.cs
using System;
using System.Runtime.CompilerServices;

namespace Core.Primitives
{
    [Serializable]
    public struct Vector3 : IEquatable<Vector3>, IFormattable
    {
        public const float Epsilon = 1e-5f;

        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public static readonly Vector3 Zero = new Vector3(0f, 0f, 0f);
        public static readonly Vector3 One = new Vector3(1f, 1f, 1f);
        public static readonly Vector3 Forward = new Vector3(0f, 0f, 1f);
        public static readonly Vector3 Back = new Vector3(0f, 0f, -1f);
        public static readonly Vector3 Up = new Vector3(0f, 1f, 0f);
        public static readonly Vector3 Down = new Vector3(0f, -1f, 0f);
        public static readonly Vector3 Left = new Vector3(-1f, 0f, 0f);
        public static readonly Vector3 Right = new Vector3(1f, 0f, 0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3(Vector2 xy, float z) 
        {
            X = xy.X;
            Y = xy.Y;
            Z = z;
        }

        public float this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                switch (index)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new IndexOutOfRangeException("Invalid Vector3 index!");
                }
            }
        }

        public float SqrMagnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => X * X + Y * Y + Z * Z;
        }

        public float Magnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => MathF.Sqrt(SqrMagnitude);
        }

        public Vector3 Normalized
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                float mag = Magnitude;
                if (mag > Epsilon)
                    return this / mag;
                else
                    return Zero;
            }
        }
        
        /// <summary>
        /// Returns this vector with a magnitude of 1 (Read Only).
        /// Returns Zero if the vector is too small to be normalized.
        /// Includes a fallback if magnitude is zero.
        /// </summary>
        /// <param name="fallback">The vector to return if the magnitude is too small for normalization.</param>
        /// <returns>The normalized vector or the fallback if normalization is not possible.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NormalizedSafe(Vector3 fallback)
        {
            float mag = Magnitude;
            if (mag > Epsilon)
                return this / mag;
            else
                return fallback;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(Vector3 lhs, Vector3 rhs)
        {
            return lhs.X * rhs.X + lhs.Y * rhs.Y + lhs.Z * rhs.Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Cross(Vector3 lhs, Vector3 rhs)
        {
            return new Vector3(
                lhs.Y * rhs.Z - lhs.Z * rhs.Y,
                lhs.Z * rhs.X - lhs.X * rhs.Z,
                lhs.X * rhs.Y - lhs.Y * rhs.X);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(Vector3 a, Vector3 b)
        {
            return (a - b).Magnitude;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f); 
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }


         [MethodImpl(MethodImplOptions.AggressiveInlining)]
         public static Vector3 Project(Vector3 vector, Vector3 onNormal)
        {
            float sqrMag = onNormal.SqrMagnitude;
            if (sqrMag < Epsilon * Epsilon)
                return Zero;
            else
            {
                var dot = Dot(vector, onNormal);
                return new Vector3(onNormal.X * dot / sqrMag,
                                   onNormal.Y * dot / sqrMag,
                                   onNormal.Z * dot / sqrMag);
            }
        }
        
        /// <summary>
        /// Projects a vector onto a plane defined by a normal orthogonal to the plane.
        /// </summary>
        /// <param name="vector">The vector to project.</param>
        /// <param name="planeNormal">The normal of the plane.</param>
        /// <returns>The projected vector.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            float sqrMag = planeNormal.SqrMagnitude;
            if (sqrMag < Epsilon * Epsilon) // If planeNormal is zero vector, cannot project
                return vector; // Or return Zero or throw, depending on desired behavior

            var dot = Dot(vector, planeNormal);
            return new Vector3(vector.X - planeNormal.X * dot / sqrMag,
                               vector.Y - planeNormal.Y * dot / sqrMag,
                               vector.Z - planeNormal.Z * dot / sqrMag);
        }


        // --- Operators ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.X, -a.Y, -a.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.X * d, a.Y * d, a.Z * d);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.X * d, a.Y * d, a.Z * d);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.X / d, a.Y / d, a.Z / d);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Vector3 lhs, Vector3 rhs) 
        {
            // Using SqrMagnitude for comparison avoids sqrt, slightly more efficient
            return (lhs - rhs).SqrMagnitude < Epsilon * Epsilon;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Vector3 lhs, Vector3 rhs) => !(lhs == rhs);

        // --- Equality and Formatting ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Vector3 other && Equals(other);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Vector3 other) 
        {
            // Exact comparison for performance if Epsilon is not strictly needed for equality semantics here.
            // If fuzzy equality is needed:
            // return MathF.Abs(X - other.X) < Epsilon && MathF.Abs(Y - other.Y) < Epsilon && MathF.Abs(Z - other.Z) < Epsilon;
            // For == operator, we use SqrMagnitude. For Equals, typically strict equality is expected unless specified.
            // Let's align with == for consistency with fuzzy comparison.
            return (this - other).SqrMagnitude < Epsilon * Epsilon;
        }

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider formatProvider) => $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)}, {Z.ToString(format, formatProvider)})";

         // --- Implicit/Explicit Conversions ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.X, v.Y, 0); 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Vector2(Vector3 v) => new Vector2(v.X, v.Y); 
    }
}