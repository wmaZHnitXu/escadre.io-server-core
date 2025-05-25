// File: Core/Ocean/DefaultFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Model;
    using Core.Primitives;
    using System;
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

        public void ApplyFloating(
            Vector3 currentPosition, 
            Quaternion currentRotation, 
            Vector3 oceanSurfaceWorldPoint, 
            Vector3 oceanNormal, 
            float deltaTime, 
            out Vector3 newPosition, 
            out Quaternion newRotation)
        {
            // --- Vertical Position Adjustment ---
            float targetY = oceanSurfaceWorldPoint.Y; 
            float calculatedNewY = MathUtils.MoveTowards(currentPosition.Y, targetY, VerticalInterpolationSpeed * BuoyancyFactor * deltaTime);

            // --- Horizontal Position Adjustment (Drift) ---
            float calculatedNewX = MathUtils.MoveTowards(currentPosition.X, oceanSurfaceWorldPoint.X, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime);
            float calculatedNewZ = MathUtils.MoveTowards(currentPosition.Z, oceanSurfaceWorldPoint.Z, HorizontalInfluence * VerticalInterpolationSpeed * deltaTime);

            newPosition = new Vector3(calculatedNewX, calculatedNewY, calculatedNewZ);

            // --- Rotational Adjustment ---
            Vector3 entityWorldForward = currentRotation * Vector3.Forward;
            Vector3 desiredForwardOnPlane = Vector3.ProjectOnPlane(entityWorldForward, oceanNormal);
            if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon) 
            {
                desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Right, oceanNormal);
                if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                {
                    desiredForwardOnPlane = Vector3.ProjectOnPlane(Vector3.Forward, oceanNormal);
                    if (desiredForwardOnPlane.SqrMagnitude < Vector3.Epsilon)
                    { 
                        desiredForwardOnPlane = entityWorldForward; 
                    }
                }
            }
            desiredForwardOnPlane = desiredForwardOnPlane.NormalizedSafe(entityWorldForward); // Use NormalizedSafe

            float combinedTiltInfluence = (RollInfluence + PitchInfluence) * 0.5f;
            Vector3 targetUpVector = Vector3.Lerp(Vector3.Up, oceanNormal, combinedTiltInfluence).NormalizedSafe(Vector3.Up); // Use NormalizedSafe
            
            Quaternion targetLookRotation;
            try
            {
                targetLookRotation = Quaternion.LookRotation(desiredForwardOnPlane, targetUpVector);
            }
            catch (ArgumentException) 
            {
                targetLookRotation = Quaternion.LookRotation(entityWorldForward, oceanNormal); 
            }

            newRotation = Quaternion.RotateTowards(currentRotation, targetLookRotation, RotationalInterpolationSpeed * deltaTime);
        }
    }
}