// File: Core/Ocean/DefaultFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Primitives;
    using System;
    public class DefaultFloatingBehavior : IFloatingBehavior
    {
        private readonly IOceanDataProvider _oceanDataProvider;

        public float BuoyancyFactor { get; set; }
        public float RollInfluence { get; set; } 
        public float PitchInfluence { get; set; }
        public float HorizontalInfluence { get; set; }
        public float VerticalInterpolationSpeed { get; set; }
        public float RotationalInterpolationSpeed { get; set; }

        public DefaultFloatingBehavior(
            IOceanDataProvider oceanDataProvider,
            float buoyancyFactor = 1.0f, 
            float rollInfluence = 0.7f, 
            float pitchInfluence = 0.7f, 
            float horizontalInfluence = 0.3f, 
            float verticalInterpolationSpeed = 3.0f, 
            float rotationalInterpolationSpeed = 60.0f)
        {
            _oceanDataProvider = oceanDataProvider ?? throw new ArgumentNullException(nameof(oceanDataProvider));
            BuoyancyFactor = buoyancyFactor;
            RollInfluence = rollInfluence;
            PitchInfluence = pitchInfluence;
            HorizontalInfluence = horizontalInfluence;
            VerticalInterpolationSpeed = verticalInterpolationSpeed;
            RotationalInterpolationSpeed = rotationalInterpolationSpeed;
        }

        public void ApplyFloating(
            Vector3 currentPosition, 
            Quaternion currentRotation, 
            float currentTime,
            float deltaTime, 
            out Vector3 newPosition, 
            out Quaternion newRotation)
        {
            if (_oceanDataProvider == null) // Should not happen if constructor enforces it
            {
                newPosition = currentPosition;
                newRotation = currentRotation;
                return;
            }

            // Calculate ocean surface point and normal internally
            float sampleX = currentPosition.X;
            float sampleZ = currentPosition.Z;
            Vector3 oceanDisplacement = _oceanDataProvider.GetDisplacement(sampleX, sampleZ, currentTime);
            Vector3 oceanNormal = _oceanDataProvider.GetNormal(sampleX, sampleZ, currentTime);
            
            // This is the world point on the (potentially horizontally displaced) ocean surface
            // directly "under" or "at" the entity's current XZ coordinates.
            Vector3 oceanSurfaceWorldPoint = new Vector3(
                sampleX + oceanDisplacement.X, // Horizontal displacement applied
                oceanDisplacement.Y,           // Vertical displacement is the Y coord
                sampleZ + oceanDisplacement.Z  // Horizontal displacement applied
            );

            // --- Vertical Position Adjustment ---
            // Target the entity's Y to be at the ocean surface's Y.
            float targetY = oceanSurfaceWorldPoint.Y; 
            float calculatedNewY = MathUtils.MoveTowards(currentPosition.Y, targetY, VerticalInterpolationSpeed * BuoyancyFactor * deltaTime);

            // --- Horizontal Position Adjustment (Drift) ---
            // Target the entity's XZ to match the ocean surface's XZ (which includes horizontal displacement)
            float calculatedNewX = MathUtils.MoveTowards(currentPosition.X, oceanSurfaceWorldPoint.X, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime);
            float calculatedNewZ = MathUtils.MoveTowards(currentPosition.Z, oceanSurfaceWorldPoint.Z, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime);

            newPosition = new Vector3(calculatedNewX, calculatedNewY, calculatedNewZ);

            // --- Rotational Adjustment ---
            Vector3 entityWorldForward = currentRotation * Vector3.Forward;
            
            // Project the entity's current forward vector onto the ocean plane defined by the ocean normal
            Vector3 desiredForwardOnPlane = Vector3.ProjectOnPlane(entityWorldForward, oceanNormal);
            if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon) 
            {
                // If current forward is (anti)parallel to ocean normal, try projecting world right vector
                desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Right, oceanNormal);
                if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                {
                    // If that also fails (e.g. oceanNormal is world right), try world forward
                    desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Forward, oceanNormal);
                     if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                    { 
                        // Absolute fallback: use entity's current forward (no change in planar direction)
                        desiredForwardOnPlane = entityWorldForward; 
                    }
                }
            }
            desiredForwardOnPlane = desiredForwardOnPlane.NormalizedSafe(entityWorldForward);

            // Determine the target "up" vector by blending world up with ocean normal
            float combinedTiltInfluence = (RollInfluence + PitchInfluence) * 0.5f;
            Vector3 targetUpVector = Vector3.Lerp(Vector3.Up, oceanNormal, combinedTiltInfluence).NormalizedSafe(Vector3.Up);
            
            Quaternion targetLookRotation;
            try
            {
                // Create a rotation that looks in desiredForwardOnPlane direction with targetUpVector as up
                targetLookRotation = Quaternion.LookRotation(desiredForwardOnPlane, targetUpVector);
            }
            catch (ArgumentException) // Fallback if LookRotation fails (e.g. vectors are collinear)
            {
                // A simpler fallback: align with ocean normal, try to maintain original yaw as much as possible
                targetLookRotation = Quaternion.FromToRotation(currentRotation * Vector3.Up, targetUpVector) * currentRotation;
            }

            newRotation = Quaternion.RotateTowards(currentRotation, targetLookRotation, RotationalInterpolationSpeed * deltaTime);
        }
    }
}