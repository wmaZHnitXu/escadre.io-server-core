// File: Core/Model/MathUtils.cs (assuming it's part of Core.Model or a new Core.Utils)
// Or Core.Primitives if it's more fundamental like Vector/Quaternion
namespace Core.Primitives // Placing in Primitives as it's a fundamental math helper
{
    using System; // For MathF

    public static class MathUtils
    {
        public const float Deg2Rad = MathF.PI / 180.0f;
        public const float Rad2Deg = 180.0f / MathF.PI;

        public static float MoveTowardsAngle(float current, float target, float maxDelta)
        {
            float deltaAngle = NormalizeAngle(target - current);
            if (MathF.Abs(deltaAngle) <= maxDelta)
            {
                return target;
            }
            return NormalizeAngle(current + MathF.Sign(deltaAngle) * maxDelta);
        }

        public static float NormalizeAngle(float degrees)
        {
            degrees = degrees % 360;
            if (degrees > 180)
            {
                degrees -= 360;
            }
            else if (degrees < -180)
            {
                degrees += 360;
            }
            return degrees;
        }

        /// <summary>
        /// Moves a value current towards target, by at most maxDelta.
        /// </summary>
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (MathF.Abs(target - current) <= maxDelta)
            {
                return target;
            }
            return current + MathF.Sign(target - current) * maxDelta;
        }

        /// <summary>
        /// Linearly interpolates between a and b by t.
        /// t is clamped between 0 and 1.
        /// </summary>
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Clamp(t, 0f, 1f);
        }

        /// <summary>
        /// Linearly interpolates between a and b by t without clamping t.
        /// </summary>
        public static float LerpUnclamped(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }
}