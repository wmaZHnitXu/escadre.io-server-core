// File: Core/Ocean/MultiPointFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Primitives;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public class MultiPointFloatingBehavior : IFloatingBehavior
    {
        private readonly IOceanDataProvider _oceanDataProvider;
        private readonly List<Vector3> _localFloatingPointOffsetsList; // Renamed for clarity

        public IReadOnlyList<Vector3> LocalFloatingPointOffsets => _localFloatingPointOffsetsList.AsReadOnly();

        public float BuoyancyFactor { get; set; }
        public float RollInfluence { get; set; } 
        public float PitchInfluence { get; set; } 
        public float HorizontalInfluence { get; set; }
        public float VerticalInterpolationSpeed { get; set; }
        public float RotationalInterpolationSpeed { get; set; }

        public MultiPointFloatingBehavior(
            IEnumerable<Vector3> localFloatingPointOffsets,
            IOceanDataProvider oceanDataProvider,
            float buoyancyFactor = 1.0f,
            float horizontalInfluence = 0.0f, 
            float verticalInterpolationSpeed = 2.0f,
            float rotationalInterpolationSpeed = 50.0f)
        {
            _oceanDataProvider = oceanDataProvider ?? throw new ArgumentNullException(nameof(oceanDataProvider));
            _localFloatingPointOffsetsList = localFloatingPointOffsets?.ToList() ?? throw new ArgumentNullException(nameof(localFloatingPointOffsets));
            if (!_localFloatingPointOffsetsList.Any())
            {
                throw new ArgumentException("At least one floating point offset must be provided.", nameof(localFloatingPointOffsets));
            }

            BuoyancyFactor = buoyancyFactor;
            RollInfluence = 0.7f; 
            PitchInfluence = 0.7f; 
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
            if (_oceanDataProvider == null || !_localFloatingPointOffsetsList.Any())
            {
                newPosition = currentPosition;
                newRotation = currentRotation;
                return;
            }

            // --- Rotation Calculation ---
            var worldFloatingPointsCurrent = new List<Vector3>();
            var oceanNormalsAtPoints = new List<Vector3>();

            foreach (var localOffset in _localFloatingPointOffsetsList)
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
            for (int i = 0; i < _localFloatingPointOffsetsList.Count; i++)
            {
                var localOffset = _localFloatingPointOffsetsList[i];
                Vector3 pointPosWithNewRotation = newRotation * localOffset + currentPosition;
                
                Vector3 oceanDisplacementAtPointXZ = _oceanDataProvider.GetDisplacement(pointPosWithNewRotation.X, pointPosWithNewRotation.Z, currentTime);
                float targetYForPoint = oceanDisplacementAtPointXZ.Y;
                
                float currentYOfPoint = pointPosWithNewRotation.Y;
                
                totalVerticalAdjustment += (targetYForPoint - currentYOfPoint);
            }
            float avgVerticalAdjustment = totalVerticalAdjustment / _localFloatingPointOffsetsList.Count;
            float targetOverallY = currentPosition.Y + avgVerticalAdjustment * BuoyancyFactor;
            float newY = MathUtils.MoveTowards(currentPosition.Y, targetOverallY, VerticalInterpolationSpeed * deltaTime);

            // --- Position Calculation (Horizontal - Drift) ---
            float totalHorizontalDriftX = 0f;
            float totalHorizontalDriftZ = 0f;
            foreach (var worldPointCurrent in worldFloatingPointsCurrent) 
            {
                Vector3 oceanDisplacement = _oceanDataProvider.GetDisplacement(worldPointCurrent.X, worldPointCurrent.Z, currentTime);
                totalHorizontalDriftX += oceanDisplacement.X;
                totalHorizontalDriftZ += oceanDisplacement.Z;
            }
            float avgHorizontalDriftX = totalHorizontalDriftX / worldFloatingPointsCurrent.Count;
            float avgHorizontalDriftZ = totalHorizontalDriftZ / worldFloatingPointsCurrent.Count;

            float targetOverallX = currentPosition.X + avgHorizontalDriftX; 
            float targetOverallZ = currentPosition.Z + avgHorizontalDriftZ;

            float driftSpeed = VerticalInterpolationSpeed * HorizontalInfluence; 
            float newX = MathUtils.MoveTowards(currentPosition.X, targetOverallX, driftSpeed * deltaTime);
            float newZ = MathUtils.MoveTowards(currentPosition.Z, targetOverallZ, driftSpeed * deltaTime);

            newPosition = new Vector3(newX, newY, newZ);
        }
    }
}