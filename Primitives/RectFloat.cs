// File: Core/Primitives/RectFloat.cs
using System;
using System.Runtime.CompilerServices;

namespace Core.Primitives
{
    [Serializable]
    public struct RectFloat : IEquatable<RectFloat>
    {
        public float X { get; set; }
        public float Y { get; set; } // Corresponds to world X and Z for a 2D QuadTree
        public float Width { get; set; }
        public float Height { get; set; }

        public float MinX => X;
        public float MinY => Y;
        public float MaxX => X + Width;
        public float MaxY => Y + Height;
        public Vector2 Center => new Vector2(X + Width * 0.5f, Y + Height * 0.5f);
        public Vector2 Size => new Vector2(Width, Height);

        public RectFloat(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public RectFloat(Vector2 position, Vector2 size)
        {
            X = position.X;
            Y = position.Y;
            Width = size.X;
            Height = size.Y;
        }

        /// <summary>
        /// Creates a RectFloat centered at a point with a given radius (forms a square).
        /// </summary>
        public static RectFloat FromCenterRadius(Vector2 center, float radius)
        {
            return new RectFloat(center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(Vector2 point)
        {
            return point.X >= MinX && point.X < MaxX && point.Y >= MinY && point.Y < MaxY;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Intersects(RectFloat other)
        {
            return MinX < other.MaxX && MaxX > other.MinX && MinY < other.MaxY && MaxY > other.MinY;
        }

        public override bool Equals(object obj) => obj is RectFloat other && Equals(other);

        public bool Equals(RectFloat other) =>
            MathF.Abs(X - other.X) < Vector2.Epsilon &&
            MathF.Abs(Y - other.Y) < Vector2.Epsilon &&
            MathF.Abs(Width - other.Width) < Vector2.Epsilon &&
            MathF.Abs(Height - other.Height) < Vector2.Epsilon;

        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

        public static bool operator ==(RectFloat lhs, RectFloat rhs) => lhs.Equals(rhs);
        public static bool operator !=(RectFloat lhs, RectFloat rhs) => !(lhs == rhs);

        public override string ToString() => $"Rect(X:{X:F2}, Y:{Y:F2}, W:{Width:F2}, H:{Height:F2})";
    }
}