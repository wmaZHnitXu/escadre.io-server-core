// File: Core/Primitives/MathUtils.cs 
namespace Core.Primitives 
{
    using System; // For MathF, Math.Clamp

    public static class MathUtils
    {
        public const float Deg2Rad = MathF.PI / 180.0f;
        public const float Rad2Deg = 180.0f / MathF.PI;

        public static float MoveTowardsAngle(float current, float target, float maxDelta)
        {
            float delta = DeltaAngle(current, target);
            // Ensure maxDelta is positive
            if (maxDelta < 0) maxDelta = -maxDelta;

            if (MathF.Abs(delta) <= maxDelta)
            {
                return target;
            }
            return NormalizeAngle(current + MathF.Sign(delta) * maxDelta);
        }

        public static float NormalizeAngle(float degrees)
        {
            degrees = degrees % 360f;
            if (degrees > 180f)
            {
                degrees -= 360f;
            }
            else if (degrees < -180f)
            {
                degrees += 360f;
            }
            return degrees;
        }

        /// <summary>
        /// Moves a value current towards target, by at most maxDelta.
        /// </summary>
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            // Ensure maxDelta is positive
            if (maxDelta < 0) maxDelta = -maxDelta;

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

        /// <summary>
        /// Calculates the shortest difference between two angles in degrees.
        /// The result will be in the range (-180, 180].
        /// </summary>
        public static float DeltaAngle(float current, float target)
        {
            float num = NormalizeAngle(target - current);
            // This was returning NormalizeAngle(target-current), which is already in [-180, 180]
            // No, Unity's Mathf.DeltaAngle uses Repeat:
            // float delta = Mathf.Repeat(target - current, 360f);
            // if (delta > 180f) delta -= 360f;
            // Our NormalizeAngle already does this.
            return num;
        }
    }
}