using System;
using System.Runtime.CompilerServices;

// Измененный неймспейс
namespace Core.Primitives
{
    /// <summary>
    /// Represents a 2D vector using single-precision floating-point numbers.
    /// </summary>
    [Serializable]
    public struct Vector2 : IEquatable<Vector2>, IFormattable
    {
        public const float Epsilon = 1e-5f;

        public readonly float X;
        public readonly float Y;

        public static readonly Vector2 Zero = new Vector2(0f, 0f);
        public static readonly Vector2 One = new Vector2(1f, 1f);
        public static readonly Vector2 Up = new Vector2(0f, 1f);
        public static readonly Vector2 Down = new Vector2(0f, -1f);
        public static readonly Vector2 Left = new Vector2(-1f, 0f);
        public static readonly Vector2 Right = new Vector2(1f, 0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
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
                    default: throw new IndexOutOfRangeException("Invalid Vector2 index!");
                }
            }
        }

        public float SqrMagnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => X * X + Y * Y;
        }

        public float Magnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => MathF.Sqrt(SqrMagnitude);
        }

        public Vector2 Normalized
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
        public Vector2 NormalizedSafe(Vector2 fallback)
        {
            float mag = Magnitude;
            if (mag > Epsilon)
                return this / mag;
            else
                return fallback;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(Vector2 lhs, Vector2 rhs)
        {
            return lhs.X * rhs.X + lhs.Y * rhs.Y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(Vector2 a, Vector2 b)
        {
            return (a - b).Magnitude;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f); // Use System.Math.Clamp
            return new Vector2(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t
            );
        }

        // --- Operators ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.X + b.X, a.Y + b.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.X - b.X, a.Y - b.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.X, -a.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.X * d, a.Y * d);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.X * d, a.Y * d);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.X / d, a.Y / d);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Vector2 lhs, Vector2 rhs) => (lhs - rhs).SqrMagnitude < Epsilon * Epsilon;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Vector2 lhs, Vector2 rhs) => !(lhs == rhs);

        // --- Equality and Formatting ---
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object obj) => obj is Vector2 other && Equals(other);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Vector2 other) => MathF.Abs(X - other.X) < Epsilon && MathF.Abs(Y - other.Y) < Epsilon;
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider formatProvider) => $"({X.ToString(format, formatProvider)}, {Y.ToString(format, formatProvider)})";
    }
}