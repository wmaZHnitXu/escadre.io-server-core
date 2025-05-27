// File: Core/Ocean/MultiPointFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Primitives;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using MathUtils = Primitives.MathUtils; // Alias

    public class MultiPointFloatingBehavior : IFloatingBehavior
    {
        private readonly IOceanDataProvider _oceanDataProvider;
        private readonly List<Vector3> _localFloatingPointOffsets;

        public float BuoyancyFactor { get; set; }
        public float RollInfluence { get; set; } // Less directly used, implicit in multi-point alignment
        public float PitchInfluence { get; set; } // Less directly used, implicit in multi-point alignment
        public float HorizontalInfluence { get; set; }
        public float VerticalInterpolationSpeed { get; set; }
        public float RotationalInterpolationSpeed { get; set; }

        public MultiPointFloatingBehavior(
            IEnumerable<Vector3> localFloatingPointOffsets,
            IOceanDataProvider oceanDataProvider,
            float buoyancyFactor = 10.0f,
            float horizontalInfluence = 0.0f, // Usually less for large ships
            float verticalInterpolationSpeed = 2.0f,
            float rotationalInterpolationSpeed = 50.0f)
        {
            _oceanDataProvider = oceanDataProvider ?? throw new ArgumentNullException(nameof(oceanDataProvider));
            _localFloatingPointOffsets = localFloatingPointOffsets?.ToList() ?? throw new ArgumentNullException(nameof(localFloatingPointOffsets));
            if (!_localFloatingPointOffsets.Any())
            {
                throw new ArgumentException("At least one floating point offset must be provided.", nameof(localFloatingPointOffsets));
            }

            BuoyancyFactor = buoyancyFactor;
            // Roll/Pitch influence are more implicitly handled by aligning to multiple points.
            // We can keep them if we want to blend the "average normal" approach with something else.
            // For now, let's assume they are not directly used in the same way as DefaultFloatingBehavior.
            RollInfluence = 0.7f; // Default, but less direct impact
            PitchInfluence = 0.7f; // Default, but less direct impact
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
            if (_oceanDataProvider == null || !_localFloatingPointOffsets.Any())
            {
                newPosition = currentPosition;
                newRotation = currentRotation;
                return;
            }

            // --- Rotation Calculation ---
            var worldFloatingPointsCurrent = new List<Vector3>();
            var oceanNormalsAtPoints = new List<Vector3>();

            foreach (var localOffset in _localFloatingPointOffsets)
            {
                Vector3 worldPoint = currentRotation * localOffset + currentPosition;
                worldFloatingPointsCurrent.Add(worldPoint);
                oceanNormalsAtPoints.Add(_oceanDataProvider.GetNormal(worldPoint.X, worldPoint.Z, currentTime));
            }

            Vector3 avgOceanNormal = Vector3.Zero;
            foreach (var normal in oceanNormalsAtPoints)
            {
                avgOceanNormal += normal;
            }
            avgOceanNormal = (avgOceanNormal / oceanNormalsAtPoints.Count).NormalizedSafe(Vector3.Up);

            Vector3 entityWorldForward = currentRotation * Vector3.Forward;
            Vector3 desiredForwardOnPlane = Vector3.ProjectOnPlane(entityWorldForward, avgOceanNormal).NormalizedSafe(entityWorldForward);
            
            Quaternion targetLookRotation;
            try
            {
                targetLookRotation = Quaternion.LookRotation(desiredForwardOnPlane, avgOceanNormal);
            }
            catch (ArgumentException)
            {
                 targetLookRotation = Quaternion.FromToRotation(currentRotation * Vector3.Up, avgOceanNormal) * currentRotation;
            }
            
            Quaternion interpolatedRotation = Quaternion.RotateTowards(currentRotation, targetLookRotation, RotationalInterpolationSpeed * deltaTime);
            newRotation = interpolatedRotation;

            // --- Position Calculation (Vertical - Buoyancy) ---
            float totalVerticalAdjustment = 0f;
            for (int i = 0; i < _localFloatingPointOffsets.Count; i++)
            {
                var localOffset = _localFloatingPointOffsets[i];
                // Use the newly interpolated rotation to find where the point *would be* relative to current XZ center
                Vector3 pointPosWithNewRotation = newRotation * localOffset + currentPosition;
                
                // Get ocean height at this point's XZ
                Vector3 oceanDisplacementAtPointXZ = _oceanDataProvider.GetDisplacement(pointPosWithNewRotation.X, pointPosWithNewRotation.Z, currentTime);
                float targetYForPoint = oceanDisplacementAtPointXZ.Y;
                
                // The Y component of pointPosWithNewRotation is where this point currently is *after rotation*
                // but *before* vertical adjustment of the whole entity.
                float currentYOfPoint = pointPosWithNewRotation.Y;
                
                totalVerticalAdjustment += (targetYForPoint - currentYOfPoint);
            }
            float avgVerticalAdjustment = totalVerticalAdjustment / _localFloatingPointOffsets.Count;
            float targetOverallY = currentPosition.Y + avgVerticalAdjustment * BuoyancyFactor;
            float newY = MathUtils.MoveTowards(currentPosition.Y, targetOverallY, VerticalInterpolationSpeed * deltaTime);

            // --- Position Calculation (Horizontal - Drift) ---
            float totalHorizontalDriftX = 0f;
            float totalHorizontalDriftZ = 0f;
            foreach (var worldPointCurrent in worldFloatingPointsCurrent) // Use original world points for drift sampling
            {
                Vector3 oceanDisplacement = _oceanDataProvider.GetDisplacement(worldPointCurrent.X, worldPointCurrent.Z, currentTime);
                totalHorizontalDriftX += oceanDisplacement.X;
                totalHorizontalDriftZ += oceanDisplacement.Z;
            }
            float avgHorizontalDriftX = totalHorizontalDriftX / worldFloatingPointsCurrent.Count;
            float avgHorizontalDriftZ = totalHorizontalDriftZ / worldFloatingPointsCurrent.Count;

            float targetOverallX = currentPosition.X + avgHorizontalDriftX; // Influence applied at MoveTowards
            float targetOverallZ = currentPosition.Z + avgHorizontalDriftZ;

            // Apply horizontal influence during the MoveTowards
            float driftSpeed = VerticalInterpolationSpeed * HorizontalInfluence; // Link drift speed to vertical speed and influence
            float newX = MathUtils.MoveTowards(currentPosition.X, targetOverallX, driftSpeed * deltaTime);
            float newZ = MathUtils.MoveTowards(currentPosition.Z, targetOverallZ, driftSpeed * deltaTime);

            newPosition = new Vector3(newX, newY, newZ);
        }
    }
}