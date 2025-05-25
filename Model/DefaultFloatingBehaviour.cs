// File: Core/Ocean/DefaultFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Model;
    using Core.Primitives;
    using System; // For MathF
    using MathUtils = Primitives.MathUtils;

    public class DefaultFloatingBehavior : IFloatingBehavior
    {
        public float BuoyancyFactor { get; set; }
        public float RollInfluence { get; set; } 
        public float PitchInfluence { get; set; }
        public float HorizontalInfluence { get; set; }
        public float VerticalInterpolationSpeed { get; set; }
        public float RotationalInterpolationSpeed { get; set; }

        public DefaultFloatingBehavior(
            float buoyancyFactor = 1.0f, 
            float rollInfluence = 0.7f, 
            float pitchInfluence = 0.7f, 
            float horizontalInfluence = 0.3f, 
            float verticalInterpolationSpeed = 3.0f, 
            float rotationalInterpolationSpeed = 60.0f)
        {
            BuoyancyFactor = buoyancyFactor;
            RollInfluence = rollInfluence;
            PitchInfluence = pitchInfluence;
            HorizontalInfluence = horizontalInfluence;
            VerticalInterpolationSpeed = verticalInterpolationSpeed;
            RotationalInterpolationSpeed = rotationalInterpolationSpeed;
        }

        public void ApplyFloating(Entity entity, Vector3 oceanSurfaceWorldPoint, Vector3 oceanNormal, float deltaTime)
        {
            if (entity == null || entity.IsDead) return;

            Vector3 currentPos = entity.Position;
            Quaternion currentRot = entity.Rotation;

            // --- Vertical Position Adjustment ---
            // Target Y for the entity's center, influenced by BuoyancyFactor (e.g. how deep it sits)
            // oceanSurfaceWorldPoint.Y is the actual water surface height.
            // A simple model: BuoyancyFactor could scale the interpolation speed or slightly adjust targetY.
            // For now, targetY is the ocean surface Y.
            float targetY = oceanSurfaceWorldPoint.Y; 
            float newY = MathUtils.MoveTowards(currentPos.Y, targetY, VerticalInterpolationSpeed * BuoyancyFactor * deltaTime);

            // --- Horizontal Position Adjustment (Drift) ---
            // oceanSurfaceWorldPoint.X and .Z are the world coordinates a point on the surface would drift to.
            // The entity drifts towards this XZ target.
            float newX = MathUtils.MoveTowards(currentPos.X, oceanSurfaceWorldPoint.X, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime); // Link drift speed to vertical speed for now
            float newZ = MathUtils.MoveTowards(currentPos.Z, oceanSurfaceWorldPoint.Z, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime);

            entity.Position = new Vector3(newX, newY, newZ);

            // --- Rotational Adjustment ---
            // Align entity's Up vector with oceanNormal, modified by Roll/Pitch influence.
            // Preserve current world forward direction as much as possible for Yaw.
            Vector3 entityWorldForward = currentRot * Vector3.Forward;

            // Project current forward onto the plane defined by oceanNormal to get desired forward.
            Vector3 desiredForwardOnPlane = Vector3.ProjectOnPlane(entityWorldForward, oceanNormal);
            if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon) // If current forward is (anti)parallel to normal
            {
                // Try projecting global X or Z axis onto the plane as a fallback.
                desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Right, oceanNormal); // Try X first
                if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                {
                    desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Forward, oceanNormal); // Then Z
                    if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                    { // Should be very rare (e.g. normal is perfectly aligned with chosen fallback and entity forward was also aligned)
                        desiredForwardOnPlane = entityWorldForward; // Keep current forward if all fails
                    }
                }
            }
            desiredForwardOnPlane = desiredForwardOnPlane.Normalized;


            // The target "Up" vector for the ship, considering influences.
            // Lerp between global up (Vector3.Up) and oceanNormal based on influence.
            // A combined influence can be sqrt(RollInfluence^2 + PitchInfluence^2) or average.
            // Let's use an average influence for now for simplicity.
            float combinedTiltInfluence = (RollInfluence + PitchInfluence) * 0.5f;
            Vector3 targetUpVector = Vector3.Lerp(Vector3.Up, oceanNormal, combinedTiltInfluence).Normalized;
            
            Quaternion targetLookRotation;
            try
            {
                targetLookRotation = Quaternion.LookRotation(desiredForwardOnPlane, targetUpVector);
            }
            catch (ArgumentException) // LookRotation can fail if forward and up are too collinear
            {
                // Fallback: try to align with normal as up, using a stabler forward if previous failed.
                targetLookRotation = Quaternion.LookRotation(entityWorldForward, oceanNormal); // Simpler, less stable forward potentially
            }


            Quaternion newRotation = Quaternion.RotateTowards(currentRot, targetLookRotation, RotationalInterpolationSpeed * deltaTime);
            entity.Rotation = newRotation;
        }
    }
}