using System;
using System.Linq;
using Core.Primitives;
using Core.Logging;

namespace Core.Model
{
    public abstract class Sentry<TOwner> : AttachedEntity<TOwner> where TOwner : Entity
    {
        public abstract float AttackRange { get; protected set; }
        public float CurrentYaw { get; protected set; }
        public float CurrentPitch { get; protected set; }

        public float MaxYawDegrees { get; protected set; } = 180f;
        public float MinYawDegrees { get; protected set; } = -180f;
        public float MaxPitchDegrees { get; protected set; } = 60f;
        public float MinPitchDegrees { get; protected set; } = -30f;
        public float TurnRateDegreesSec { get; protected set; } = 180f; 

        protected Entity _currentTarget;
        public Predicate<Entity> IsTargetValidPredicate { get; set; }
        
        protected Quaternion _baseLocalRotationOffset;

        protected Sentry(
            Level level, 
            TOwner owner, 
            Vector3 localPositionOffset, 
            Quaternion localRotationOffset,
            float attackRange)
            : base(level, owner, localPositionOffset, localRotationOffset)
        {
            AttackRange = attackRange;
            _baseLocalRotationOffset = localRotationOffset; 

            IsTargetValidPredicate = potentialTarget =>
            {
                if (potentialTarget == null || potentialTarget.IsDead) return false;
                if (potentialTarget == this) return false; 
                if (potentialTarget == this.Owner) return false; 

                if (potentialTarget is AttachedEntity<TOwner> attachedSibling && attachedSibling.Owner == this.Owner)
                {
                    return false;
                }
                
                if (this.Owner is Ship ownerShip)
                {
                    if (potentialTarget is Ship targetShip)
                    {
                        return targetShip.OwningEscadreClientId != ownerShip.OwningEscadreClientId;
                    }
                    else if (potentialTarget is Escadre targetEscadre) // Cannons can target Escadre entities (e.g. if they are targetable structures)
                    {
                        return targetEscadre.OwnerClientId != ownerShip.OwningEscadreClientId;
                    }
                    else
                    {
                        return false; // Ship cannons only target other Ships or Escadres by default
                    }
                }
                return true; 
            };
        }

        public override void Update(float delta)
        {
            base.Update(delta); 
            if (IsDead || Owner == null || Owner.IsDead)
            {
                _currentTarget = null;
                return;
            }

            AcquireAndTrackTarget(delta);
        }

        protected virtual void AcquireAndTrackTarget(float delta)
        {
            if (_currentTarget != null)
            {
                float distanceSqToCurrentTarget = (_currentTarget.Position - this.Position).SqrMagnitude;
                if (!IsTargetValidPredicate(_currentTarget) || distanceSqToCurrentTarget > AttackRange * AttackRange || !IsPositionInFiringArc(_currentTarget.Position))
                {
                    _currentTarget = null;
                }
            }

            if (_currentTarget == null)
            {
                FindNewTarget();
            }

            if (_currentTarget != null)
            {
                AimAtTarget(delta, _currentTarget.Position);
            }
            else
            {
                AimAtTarget(delta, this.Position + Owner.Rotation * Vector3.Forward); 
            }
        }

        protected virtual void FindNewTarget()
        {
            Entity bestTarget = null;
            float bestTargetScore = float.MaxValue; 

            // Use spatial query from Level
            var potentialTargets = _level.GetEntitiesInRadius(
                new Vector2(this.Position.X, this.Position.Z), 
                this.AttackRange,
                entity => IsTargetValidPredicate(entity) // Pre-filter with predicate
            );

            foreach (Entity potentialTarget in potentialTargets)
            {
                // IsTargetValidPredicate already checked, but double check just in case or if filter was null
                if (!IsTargetValidPredicate(potentialTarget)) continue;

                float distanceSq = (potentialTarget.Position - this.Position).SqrMagnitude;
                // distanceSq check is somewhat redundant if QueryRadius is accurate, but good for safety
                if (distanceSq <= AttackRange * AttackRange) 
                {
                    if (IsPositionInFiringArc(potentialTarget.Position))
                    {
                        if (distanceSq < bestTargetScore)
                        {
                            bestTargetScore = distanceSq;
                            bestTarget = potentialTarget;
                        }
                    }
                }
            }

            if (bestTarget != null)
            {
                _currentTarget = bestTarget;
            }
        }

        protected virtual void AimAtTarget(float delta, Vector3 targetWorldPosition)
        {
            if (Owner == null || Owner.IsDead) return;

            Vector3 directionToTargetWorld = (targetWorldPosition - this.Position).Normalized;

            if (directionToTargetWorld.SqrMagnitude < Vector3.Epsilon) 
            {
                directionToTargetWorld = Owner.Rotation * Vector3.Forward;
            }
            
            Quaternion ownerInverseRotation = Owner.Rotation.Inverse;
            Vector3 directionToTargetOwnerLocal = ownerInverseRotation * directionToTargetWorld;

            float desiredYaw = MathF.Atan2(directionToTargetOwnerLocal.X, directionToTargetOwnerLocal.Z) * Core.Primitives.MathUtils.Rad2Deg;
            float horizontalDistOwnerLocal = MathF.Sqrt(directionToTargetOwnerLocal.X * directionToTargetOwnerLocal.X + directionToTargetOwnerLocal.Z * directionToTargetOwnerLocal.Z);
            float desiredPitch = MathF.Atan2(-directionToTargetOwnerLocal.Y, horizontalDistOwnerLocal) * Core.Primitives.MathUtils.Rad2Deg;
            
            desiredYaw = Math.Clamp(desiredYaw, MinYawDegrees, MaxYawDegrees);
            desiredPitch = Math.Clamp(desiredPitch, MinPitchDegrees, MaxPitchDegrees);

            float maxAngleChange = TurnRateDegreesSec * delta;
            CurrentYaw = Core.Primitives.MathUtils.MoveTowardsAngle(CurrentYaw, desiredYaw, maxAngleChange);
            CurrentPitch = Core.Primitives.MathUtils.MoveTowardsAngle(CurrentPitch, desiredPitch, maxAngleChange);

            Quaternion aimingRotation = Quaternion.Euler(CurrentPitch, CurrentYaw, 0f);
            this.LocalRotationOffset = _baseLocalRotationOffset * aimingRotation;
            // AttachedEntity.Rotation setter ensures base.Rotation (Entity._rotation) is updated.
        }

        public virtual bool IsPositionInFiringArc(Vector3 targetWorldPosition)
        {
            if (Owner == null || Owner.IsDead) return false;

            Vector3 directionToTargetWorld = (targetWorldPosition - this.Position).Normalized;
            if (directionToTargetWorld.SqrMagnitude < Vector3.Epsilon) return true; 

            Quaternion ownerInverseRotation = Owner.Rotation.Inverse;
            Vector3 directionToTargetOwnerLocal = ownerInverseRotation * directionToTargetWorld;
            
            float yawToTarget = MathF.Atan2(directionToTargetOwnerLocal.X, directionToTargetOwnerLocal.Z) * Core.Primitives.MathUtils.Rad2Deg;
            float pitchToTarget = MathF.Atan2(-directionToTargetOwnerLocal.Y, 
                                            MathF.Sqrt(directionToTargetOwnerLocal.X * directionToTargetOwnerLocal.X + directionToTargetOwnerLocal.Z * directionToTargetOwnerLocal.Z)) * Core.Primitives.MathUtils.Rad2Deg;

            return yawToTarget >= MinYawDegrees && yawToTarget <= MaxYawDegrees &&
                   pitchToTarget >= MinPitchDegrees && pitchToTarget <= MaxPitchDegrees;
        }
        
        public void UpdateAimFromWorldRotation(Quaternion newWorldRotation)
        {
            if (Owner == null || Owner.IsDead)
            {
                _baseLocalRotationOffset = newWorldRotation; 
                CurrentYaw = 0;
                CurrentPitch = 0;
                return;
            }
            
            Quaternion newLocalOffset = Owner.Rotation.Inverse * newWorldRotation;
            // Assuming _baseLocalRotationOffset is the "model's zero aim" relative to owner.
            // We need to find the yaw/pitch that, when applied to _baseLocalRotationOffset, results in newLocalOffset.
            // Solved by: aimingRotation = newLocalOffset * _baseLocalRotationOffset.Inverse
            Quaternion aimingRotation = newLocalOffset * _baseLocalRotationOffset.Inverse;
            Vector3 eulerAngles = aimingRotation.ToEulerAngles(); // This gives X(Pitch), Y(Yaw), Z(Roll)

            CurrentYaw = Core.Primitives.MathUtils.NormalizeAngle(eulerAngles.Y);
            CurrentPitch = Core.Primitives.MathUtils.NormalizeAngle(eulerAngles.X);
            // We assume roll is not part of sentry aiming (typically 0) or is part of _baseLocalRotationOffset

            CurrentYaw = Math.Clamp(CurrentYaw, MinYawDegrees, MaxYawDegrees);
            CurrentPitch = Math.Clamp(CurrentPitch, MinPitchDegrees, MaxPitchDegrees);
            
            // Recalculate LocalRotationOffset to ensure consistency with clamped values
            this.LocalRotationOffset = _baseLocalRotationOffset * Quaternion.Euler(CurrentPitch, CurrentYaw, 0f);
        }
    }
}