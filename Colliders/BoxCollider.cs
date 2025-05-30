// File: Core/Primitives/BoxCollider.cs
using System;

namespace Core.Primitives
{
    public class BoxCollider : Collider
    {
        public Vector3 Size { get; set; }
        public Vector3 HalfSize => Size * 0.5f;

        public BoxCollider(Model.DestructibleEntity owner, Vector3 localOffset, Vector3 size)
            : base(owner, localOffset)
        {
            Size = size;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "BoxCollider size components must be positive.");
            }
        }

        public override bool IsPointInside(Vector3 worldPoint)
        {
            if (OwnerEntity == null || OwnerEntity.IsDead) return false;

            Vector3 localPoint = TransformWorldPointToLocal(worldPoint);

            return localPoint.X >= -HalfSize.X && localPoint.X <= HalfSize.X &&
                   localPoint.Y >= -HalfSize.Y && localPoint.Y <= HalfSize.Y &&
                   localPoint.Z >= -HalfSize.Z && localPoint.Z <= HalfSize.Z;
        }

        public override bool IntersectsRay(
            Vector3 worldRayOrigin, 
            Vector3 worldRayDirection, 
            float maxDistance,
            out float hitDistance, 
            out Vector3 hitPoint, 
            out Vector3 hitNormal)
        {
            hitDistance = float.MaxValue;
            hitPoint = Vector3.Zero;
            hitNormal = Vector3.Zero;

            if (OwnerEntity == null || OwnerEntity.IsDead) return false;
            if (worldRayDirection.SqrMagnitude < Vector3.Epsilon * Vector3.Epsilon) return false; // Invalid direction

            // Transform ray to entity's local space, where the box is an AABB centered at this.LocalOffset
            Vector3 entityLocalRayOrigin = OwnerEntity.Rotation.Inverse * (worldRayOrigin - OwnerEntity.Position);
            Vector3 entityLocalRayDirection = OwnerEntity.Rotation.Inverse * worldRayDirection;

            // Box min/max in entity's local space
            Vector3 boxMin = LocalOffset - HalfSize;
            Vector3 boxMax = LocalOffset + HalfSize;

            float tmin = 0.0f;
            float tmax = maxDistance; // Use maxDistance from parameter

            Vector3 intersectionNormal = Vector3.Zero;

            // X slab
            if (MathF.Abs(entityLocalRayDirection.X) < Vector3.Epsilon)
            {
                if (entityLocalRayOrigin.X < boxMin.X || entityLocalRayOrigin.X > boxMax.X) return false;
            }
            else
            {
                float ood = 1.0f / entityLocalRayDirection.X;
                float t1 = (boxMin.X - entityLocalRayOrigin.X) * ood;
                float t2 = (boxMax.X - entityLocalRayOrigin.X) * ood;
                Vector3 currentNormal = entityLocalRayDirection.X < 0 ? Vector3.Right : Vector3.Left;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; currentNormal = -currentNormal; }
                if (t1 > tmin) { tmin = t1; intersectionNormal = currentNormal; }
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Y slab
            if (MathF.Abs(entityLocalRayDirection.Y) < Vector3.Epsilon)
            {
                if (entityLocalRayOrigin.Y < boxMin.Y || entityLocalRayOrigin.Y > boxMax.Y) return false;
            }
            else
            {
                float ood = 1.0f / entityLocalRayDirection.Y;
                float t1 = (boxMin.Y - entityLocalRayOrigin.Y) * ood;
                float t2 = (boxMax.Y - entityLocalRayOrigin.Y) * ood;
                Vector3 currentNormal = entityLocalRayDirection.Y < 0 ? Vector3.Up : Vector3.Down;
                 if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; currentNormal = -currentNormal; }
                if (t1 > tmin) { tmin = t1; intersectionNormal = currentNormal; }
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Z slab
            if (MathF.Abs(entityLocalRayDirection.Z) < Vector3.Epsilon)
            {
                if (entityLocalRayOrigin.Z < boxMin.Z || entityLocalRayOrigin.Z > boxMax.Z) return false;
            }
            else
            {
                float ood = 1.0f / entityLocalRayDirection.Z;
                float t1 = (boxMin.Z - entityLocalRayOrigin.Z) * ood;
                float t2 = (boxMax.Z - entityLocalRayOrigin.Z) * ood;
                Vector3 currentNormal = entityLocalRayDirection.Z < 0 ? Vector3.Forward : Vector3.Back;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; currentNormal = -currentNormal; }
                if (t1 > tmin) { tmin = t1; intersectionNormal = currentNormal; }
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // If tmin is positive (intersection is in front of ray origin) and within maxDistance
            if (tmin > 0 && tmin <= maxDistance)
            {
                hitDistance = tmin;
                Vector3 localHitPoint = entityLocalRayOrigin + entityLocalRayDirection * hitDistance;
                hitPoint = OwnerEntity.Position + OwnerEntity.Rotation * localHitPoint;
                hitNormal = (OwnerEntity.Rotation * intersectionNormal).Normalized; // Normal is in entity local space, transform to world
                return true;
            }
            return false;
        }
        
        public override RectFloat GetWorldAABB_XZ()
        {
            if (OwnerEntity == null) return new RectFloat(LocalOffset.X - HalfSize.X, LocalOffset.Z - HalfSize.Z, Size.X, Size.Z);

            // Get the 8 corners of the box in its own local space (centered at origin for this calculation step)
            Vector3[] localCorners = new Vector3[8];
            localCorners[0] = new Vector3(-HalfSize.X, -HalfSize.Y, -HalfSize.Z);
            localCorners[1] = new Vector3( HalfSize.X, -HalfSize.Y, -HalfSize.Z);
            localCorners[2] = new Vector3( HalfSize.X,  HalfSize.Y, -HalfSize.Z);
            localCorners[3] = new Vector3(-HalfSize.X,  HalfSize.Y, -HalfSize.Z);
            localCorners[4] = new Vector3(-HalfSize.X, -HalfSize.Y,  HalfSize.Z);
            localCorners[5] = new Vector3( HalfSize.X, -HalfSize.Y,  HalfSize.Z);
            localCorners[6] = new Vector3( HalfSize.X,  HalfSize.Y,  HalfSize.Z);
            localCorners[7] = new Vector3(-HalfSize.X,  HalfSize.Y,  HalfSize.Z);

            // Transform corners to world space
            Vector3 worldCenter = WorldCenterPosition; // OwnerEntity.Position + OwnerEntity.Rotation * LocalOffset;
            Quaternion worldRotation = OwnerEntity.Rotation;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            // Not needed for RectFloat, but for 3D AABB: float minY = float.MaxValue, maxY = float.MinValue;


            for (int i = 0; i < 8; i++)
            {
                // Transform corner relative to collider's local offset, then apply owner's rotation, then add owner's position
                Vector3 worldCorner = worldRotation * (localCorners[i] + LocalOffset) + OwnerEntity.Position;
                // Simpler: transform local-to-collider-origin point to world space directly
                // Vector3 worldCorner = TransformLocalPointToWorld(localCorners[i]); 
                // Ah, the localCorners are already relative to collider's origin.
                // So, we need to transform them to world relative to the *collider's center*.
                // World corner = ColliderWorldCenter + OwnerEntity.Rotation * localCorner (where localCorner is relative to collider's origin)
                // Vector3 rotatedLocalCorner = worldRotation * localCorners[i];
                // Vector3 worldCorner = worldCenter + rotatedLocalCorner;
                // Correct logic for transforming box corners to world:
                // 1. Corners relative to collider's local frame (centered at LocalOffset within OwnerEntity's frame)
                // Vector3 cornerInOwnerFrame = LocalOffset + localCorners[i]; // error in localCorners definition above, they should be just +/- HalfSize
                // Corrected localCorners:
                // localCorners[0] = new Vector3(-HalfSize.X, -HalfSize.Y, -HalfSize.Z);
                // These are already relative to the collider's *own* center, which is at LocalOffset from owner.
                // So: WorldCorner = Owner.Pos + Owner.Rot * (LocalOffset + Owner.Rot.Inverse * Owner.Rot * localCorner)
                // WorldCorner = Owner.Pos + Owner.Rot * LocalOffset + Owner.Rot * localCorner
                // WorldCorner = ColliderWorldCenter + Owner.Rot * localCorner
                
                Vector3 worldTransformedCorner = worldCenter + worldRotation * localCorners[i];


                if (worldTransformedCorner.X < minX) minX = worldTransformedCorner.X;
                if (worldTransformedCorner.X > maxX) maxX = worldTransformedCorner.X;
                // if (worldTransformedCorner.Y < minY) minY = worldTransformedCorner.Y;
                // if (worldTransformedCorner.Y > maxY) maxY = worldTransformedCorner.Y;
                if (worldTransformedCorner.Z < minZ) minZ = worldTransformedCorner.Z;
                if (worldTransformedCorner.Z > maxZ) maxZ = worldTransformedCorner.Z;
            }
            return new RectFloat(minX, minZ, maxX - minX, maxZ - minZ);
        }
    }
}