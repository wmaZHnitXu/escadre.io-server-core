// File: Core/Ocean/IFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Model; 
    using Core.Primitives;

    public interface IFloatingBehavior
    {
        // Configuration properties
        float BuoyancyFactor { get; set; } 
        float RollInfluence { get; set; }  
        float PitchInfluence { get; set; } 
        float HorizontalInfluence { get; set; } 
        float VerticalInterpolationSpeed { get; set; } 
        float RotationalInterpolationSpeed { get; set; }

        /// <summary>
        /// Calculates the new position and rotation for an object based on floating physics.
        /// </summary>
        /// <param name="currentPosition">The current world position of the object.</param>
        /// <param name="currentRotation">The current world rotation of the object.</param>
        /// <param name="oceanSurfaceWorldPoint">
        /// The target world position a point particle would have if it were exactly on the ocean surface
        /// at the object's current XZ base. This point includes the ocean's full XYZ displacement.
        /// </param>
        /// <param name="oceanNormal">The normal of the ocean surface at the object's location.</param>
        /// <param name="deltaTime">The time elapsed since the last update.</param>
        /// <param name="newPosition">Output: The calculated new world position after applying floating effects.</param>
        /// <param name="newRotation">Output: The calculated new world rotation after applying floating effects.</param>
        void ApplyFloating(
            Vector3 currentPosition, 
            Quaternion currentRotation, 
            Vector3 oceanSurfaceWorldPoint, 
            Vector3 oceanNormal, 
            float deltaTime, 
            out Vector3 newPosition, 
            out Quaternion newRotation);
    }
}