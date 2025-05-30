// File: Core/Primitives/Collider.cs
using System;
using Core.Model; // Required for DestructibleEntity

namespace Core.Primitives
{
    public abstract class Collider
    {
        /// <summary>
        /// The entity this collider is attached to. Set internally by DestructibleEntity.
        /// </summary>
        public DestructibleEntity OwnerEntity { get; internal set; }

        /// <summary>
        /// Local positional offset from the OwnerEntity's origin.
        /// The collider's orientation is assumed to be the same as the OwnerEntity's orientation.
        /// </summary>
        public Vector3 LocalOffset { get; set; }

        protected Collider(DestructibleEntity owner, Vector3 localOffset)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner)); // Owner must be set at construction
            OwnerEntity = owner;
            LocalOffset = localOffset;
        }

        /// <summary>
        /// Gets the world position of this collider's center.
        /// </summary>
        public Vector3 WorldCenterPosition
        {
            get
            {
                if (OwnerEntity == null) return LocalOffset; // Should not happen if properly managed
                return OwnerEntity.Position + (OwnerEntity.Rotation * LocalOffset);
            }
        }

        /// <summary>
        /// Checks if a given world-space point is inside this collider.
        /// </summary>
        /// <param name="worldPoint">The point to check, in world space.</param>
        /// <returns>True if the point is inside, false otherwise.</returns>
        public abstract bool IsPointInside(Vector3 worldPoint);

        /// <summary>
        /// Checks if a ray intersects with this collider.
        /// </summary>
        /// <param name="worldRayOrigin">The origin of the ray in world space.</param>
        /// <param name="worldRayDirection">The direction of the ray in world space (should be normalized).</param>
        /// <param name="maxDistance">The maximum distance the ray should check for intersection.</param>
        /// <param name="hitDistance">Output: The distance from the ray origin to the intersection point, if an intersection occurs.</param>
        /// <param name="hitPoint">Output: The intersection point in world space, if an intersection occurs.</param>
        /// <param name="hitNormal">Output: The normal of the surface at the intersection point in world space, if an intersection occurs.</param>
        /// <returns>True if an intersection occurs within maxDistance, false otherwise.</returns>
        public abstract bool IntersectsRay(
            Vector3 worldRayOrigin, 
            Vector3 worldRayDirection, 
            float maxDistance,
            out float hitDistance, 
            out Vector3 hitPoint, 
            out Vector3 hitNormal);

        /// <summary>
        /// Gets the Axis-Aligned Bounding Box (AABB) of this collider in world space.
        /// </summary>
        /// <returns>A RectFloat representing the 2D AABB (X and Z dimensions) of the collider in world space for QuadTree.
        /// A more complete 3D AABB might be needed for 3D broad-phase if implemented later.</returns>
        public abstract RectFloat GetWorldAABB_XZ(); // For 2D spatial indexing like QuadTree
        
        // public abstract AABB3D GetWorldAABB_3D(); // If a 3D AABB struct existed and was needed

        /// <summary>
        /// Transforms a point from world space to the collider's local space (where the collider is treated as an AABB centered at origin).
        /// </summary>
        protected Vector3 TransformWorldPointToLocal(Vector3 worldPoint)
        {
            if (OwnerEntity == null) return worldPoint - LocalOffset; // Should not happen
            Vector3 relativeToOwnerOrigin = worldPoint - OwnerEntity.Position;
            Vector3 pointInOwnerLocalSpace = OwnerEntity.Rotation.Inverse * relativeToOwnerOrigin;
            return pointInOwnerLocalSpace - LocalOffset; // Now relative to collider's local origin
        }

        /// <summary>
        /// Transforms a direction from world space to the collider's local space.
        /// </summary>
        protected Vector3 TransformWorldDirectionToLocal(Vector3 worldDirection)
        {
            if (OwnerEntity == null) return worldDirection; // Should not happen
            return OwnerEntity.Rotation.Inverse * worldDirection;
        }

        /// <summary>
        /// Transforms a point from the collider's local space back to world space.
        /// </summary>
        protected Vector3 TransformLocalPointToWorld(Vector3 localPoint)
        {
             if (OwnerEntity == null) return localPoint + LocalOffset; // Should not happen
             Vector3 pointInOwnerLocalSpace = localPoint + LocalOffset;
             Vector3 worldRelativeToOwnerOrigin = OwnerEntity.Rotation * pointInOwnerLocalSpace;
             return OwnerEntity.Position + worldRelativeToOwnerOrigin;
        }
        
        /// <summary>
        /// Transforms a direction (like a normal) from the collider's local space back to world space.
        /// </summary>
        protected Vector3 TransformLocalDirectionToWorld(Vector3 localDirection)
        {
            if (OwnerEntity == null) return localDirection; // Should not happen
            return OwnerEntity.Rotation * localDirection;
        }
    }
}