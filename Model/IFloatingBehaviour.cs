// File: Core/Ocean/IFloatingBehavior.cs
namespace Core.Ocean
{
    using Core.Model; // For Entity
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
        /// Applies floating physics to an entity based on ocean conditions.
        /// This method is expected to modify the entity's Position (especially Y) and Rotation.
        /// </summary>
        /// <param name="entity">The entity to apply floating behavior to.</param>
        /// <param name="oceanSurfaceWorldPoint">
        /// The target world position a point particle would have if it were exactly on the ocean surface
        /// at the entity's current XZ base. This point includes the ocean's full XYZ displacement.
        /// (i.e., entity.BaseXZ + oceanProvider.GetDisplacement(entity.BaseXZ, time))
        /// </param>
        /// <param name="oceanNormal">The normal of the ocean surface at the entity's location.</param>
        /// <param name="deltaTime">The time elapsed since the last update.</param>
        void ApplyFloating(Entity entity, Vector3 oceanSurfaceWorldPoint, Vector3 oceanNormal, float deltaTime);
    }
}