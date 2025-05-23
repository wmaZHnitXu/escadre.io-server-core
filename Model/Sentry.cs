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
        public float TurnRateDegreesSec { get; protected set; } = 180f; // Changed from 90 to 180 for faster turning

        protected Entity _currentTarget;
        public Predicate<Entity> IsTargetValidPredicate { get; set; }

        // Stores the initial local rotation offset of the sentry model itself, before dynamic aiming.
        protected Quaternion _baseLocalRotationOffset;

        protected Sentry(
            Level level, 
            TOwner owner, 
            Vector3 localPositionOffset, 
            Quaternion localRotationOffset, // This is the initial orientation of the sentry model
            float attackRange)
            : base(level, owner, localPositionOffset, localRotationOffset)
        {
            AttackRange = attackRange;
            _baseLocalRotationOffset = localRotationOffset; // Store the initial setup rotation

            // Default target validation: not null, not dead, not self, not owner.
            IsTargetValidPredicate = potentialTarget =>
            {
                if (potentialTarget == null || potentialTarget.IsDead) return false;
                if (potentialTarget == this) return false; // Cannot target self (the Sentry)
                if (potentialTarget == this.Owner) return false; // Cannot target its own owner

                // Avoid targeting other attachments of the same owner
                if (potentialTarget is AttachedEntity<TOwner> attachedSibling && attachedSibling.Owner == this.Owner)
                {
                    return false;
                }

                // If the Sentry's owner is a Ship, then the Sentry should only target enemy Ships.
                if (this.Owner is Ship ownerShip)
                {
                    if (potentialTarget is Ship targetShip)
                    {
                        // Target ship must belong to a different Escadre's client ID
                        return targetShip.OwningEscadreClientId != ownerShip.OwningEscadreClientId;
                    }
                    else
                    {
                        // If Sentry is on a Ship, it only targets other Ships.
                        return false;
                    }
                }
                // If the Sentry's owner is NOT a Ship (e.g., a static base defense), 
                // then the above Ship-specific logic doesn't apply.
                // It can target any other valid entity that passed the initial checks.
                return true; 
            };
        }

        public override void Update(float delta)
        {
            base.Update(delta); // Handles owner death, etc.
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
                // Validate current target (is it still valid, in range, and in arc?)
                float distanceSqToCurrentTarget = (_currentTarget.Position - this.Position).SqrMagnitude;
                if (!IsTargetValidPredicate(_currentTarget) || distanceSqToCurrentTarget > AttackRange * AttackRange || !IsPositionInFiringArc(_currentTarget.Position))
                {
                    // Logger.Log($"[Sentry {Id}] Target {_currentTarget.Id} lost (Invalid/OOR/Out of Arc).");
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
                // Firing logic will be handled by derived classes (e.g., Cannon)
            }
            else
            {
                // Optional: Return to a default orientation or sweep
                AimAtTarget(delta, this.Position + Owner.Rotation * Vector3.Forward); // Aim forward relative to owner
            }
        }

        protected virtual void FindNewTarget()
        {
            Entity bestTarget = null;
            float bestTargetScore = float.MaxValue; // Lower is better (e.g., distance)

            foreach (Entity potentialTarget in _level.GetAllEntities().Where(entity => IsTargetValidPredicate(entity)))
            {
                float distanceSq = (potentialTarget.Position - this.Position).SqrMagnitude;
                if (distanceSq <= AttackRange * AttackRange)
                {
                    if (IsPositionInFiringArc(potentialTarget.Position))
                    {
                        // Prioritize closer targets
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
                // Logger.Log($"[Sentry {Id}] New target acquired: {bestTarget.Id} at distance {MathF.Sqrt(bestTargetScore)}");
            }
        }

        protected virtual void AimAtTarget(float delta, Vector3 targetWorldPosition)
        {
            if (Owner == null || Owner.IsDead) return;

            // --- Aiming Logic ---
            // 1. Direction to target in World Space
            Vector3 directionToTargetWorld = (targetWorldPosition - this.Position).Normalized;

            if (directionToTargetWorld.SqrMagnitude < Vector3.Epsilon) // Target is at our position
            {
                // No specific direction to aim, maintain current aim or aim owner's forward
                directionToTargetWorld = Owner.Rotation * Vector3.Forward;
            }
            
            // 2. Transform this world direction into the Sentry's parent (Owner) local space.
            // The Sentry aims relative to its Owner's orientation.
            Quaternion ownerInverseRotation = Owner.Rotation.Inverse;
            Vector3 directionToTargetOwnerLocal = ownerInverseRotation * directionToTargetWorld;

            // 3. Calculate desired Yaw and Pitch from this Owner-local direction vector.
            // Yaw is rotation around Owner's Up vector (local Y).
            // Pitch is rotation around Owner's Right vector (local X), after yaw is applied.

            // Yaw: angle in the Owner's XZ plane.
            float desiredYaw = MathF.Atan2(directionToTargetOwnerLocal.X, directionToTargetOwnerLocal.Z) * MathUtils.Rad2Deg;

            // Pitch: angle relative to Owner's XY plane (after yaw).
            // To get pitch correctly, we can project onto the plane defined by Owner's forward (after yaw) and Owner's up.
            // Or, more simply, use the Y component and the length of the projection on XZ plane.
            float horizontalDistOwnerLocal = MathF.Sqrt(directionToTargetOwnerLocal.X * directionToTargetOwnerLocal.X + directionToTargetOwnerLocal.Z * directionToTargetOwnerLocal.Z);
            float desiredPitch = MathF.Atan2(-directionToTargetOwnerLocal.Y, horizontalDistOwnerLocal) * MathUtils.Rad2Deg;
            
            // 4. Clamp Yaw and Pitch to Sentry's limits
            desiredYaw = Math.Clamp(desiredYaw, MinYawDegrees, MaxYawDegrees);
            desiredPitch = Math.Clamp(desiredPitch, MinPitchDegrees, MaxPitchDegrees);

            // 5. Smoothly update CurrentYaw and CurrentPitch
            float maxAngleChange = TurnRateDegreesSec * delta;
            CurrentYaw = MathUtils.MoveTowardsAngle(CurrentYaw, desiredYaw, maxAngleChange);
            CurrentPitch = MathUtils.MoveTowardsAngle(CurrentPitch, desiredPitch, maxAngleChange);

            // 6. Update the Sentry's LocalRotationOffset
            // This combines the base model rotation with the dynamic aiming rotation.
            // The aiming rotation (yaw, pitch) is applied *relative to the sentry's base orientation on the owner*.
            // If _baseLocalRotationOffset is how the cannon model is naturally oriented on the ship,
            // then yaw and pitch are applied to that.
            Quaternion aimingRotation = Quaternion.Euler(CurrentPitch, CurrentYaw, 0f);
            this.LocalRotationOffset = _baseLocalRotationOffset * aimingRotation;

            // Note: The base AttachedEntity.Rotation setter will be called if this.Rotation is set.
            // We are directly setting LocalRotationOffset, and the AttachedEntity.Rotation getter will use it.
            // We must also ensure the internal _rotation of Entity is updated for things like OnTeleported.
            // The base.Rotation property in Entity should reflect the final world rotation.
            // This happens implicitly because AttachedEntity.Rotation getter calculates it.
            // And if anyone sets AttachedEntity.Rotation, it updates LocalRotationOffset.
            // We need to ensure the Entity._rotation field is also consistent.
            // The most straightforward way is to update the base entity's rotation directly after computing the final world rotation.
            // However, AttachedEntity already handles this by having its set_Rotation update the LocalRotationOffset
            // and also assign to base.Rotation. Here, we directly manipulate LocalRotationOffset.
            // The getter for `this.Rotation` will reflect the change.
            // For events or systems reading `Entity._rotation` directly, we should ensure it's also correct.
            // The `base.Rotation` in `Entity.cs` is a `Quaternion _rotation; public virtual Quaternion Rotation { get => _rotation; protected set => _rotation = value; }`
            // Our `AttachedEntity.Rotation` overrides this. The `get` computes it. The `set` updates local and calls `base.Rotation = value;`.
            // By setting `this.LocalRotationOffset`, the next `get_Rotation` will be correct.
            // To be absolutely safe that `_rotation` in `Entity` is updated (if it's not already by some magic),
            // we could explicitly do: (though it might be redundant)
            // base.Rotation = Owner.Rotation * this.LocalRotationOffset; 
            // This is actually important for the Entity's internal state if other systems bypass the overridden getter.
            // Let's add it to be safe for TeleportTo event and other internal Entity mechanics.
            // Entity's internal _rotation must match the visible Rotation property.
            // _level.SetEntityRotation(this.Id, Owner.Rotation * this.LocalRotationOffset);


        }

        public virtual bool IsPositionInFiringArc(Vector3 targetWorldPosition)
        {
            if (Owner == null || Owner.IsDead) return false;

            Vector3 directionToTargetWorld = (targetWorldPosition - this.Position).Normalized;
            if (directionToTargetWorld.SqrMagnitude < Vector3.Epsilon) return true; // Target is at our position, considered in arc

            Quaternion ownerInverseRotation = Owner.Rotation.Inverse;
            Vector3 directionToTargetOwnerLocal = ownerInverseRotation * directionToTargetWorld;
            
            float yawToTarget = MathF.Atan2(directionToTargetOwnerLocal.X, directionToTargetOwnerLocal.Z) * MathUtils.Rad2Deg;
            float pitchToTarget = MathF.Atan2(-directionToTargetOwnerLocal.Y, 
                                            MathF.Sqrt(directionToTargetOwnerLocal.X * directionToTargetOwnerLocal.X + directionToTargetOwnerLocal.Z * directionToTargetOwnerLocal.Z)) * MathUtils.Rad2Deg;

            return yawToTarget >= MinYawDegrees && yawToTarget <= MaxYawDegrees &&
                   pitchToTarget >= MinPitchDegrees && pitchToTarget <= MaxPitchDegrees;
        }
        
        // This method should be called by Level when an entity's rotation is set directly
        // to ensure the Sentry's internal representation is consistent if its world rotation is changed externally.
        public void UpdateAimFromWorldRotation(Quaternion newWorldRotation)
        {
            if (Owner == null || Owner.IsDead)
            {
                _baseLocalRotationOffset = newWorldRotation; // Or some other default behavior
                CurrentYaw = 0;
                CurrentPitch = 0;
                return;
            }

            // This is the new local rotation offset based on the new world rotation
            Quaternion newLocalOffset = Owner.Rotation.Inverse * newWorldRotation;
            _baseLocalRotationOffset = newLocalOffset; // Assume this world rotation now defines the new "base" aim direction

            // We need to decompose newLocalOffset into a "base" (if we preserve an original model orientation)
            // and a "yaw/pitch" component. Or, more simply, consider the newLocalOffset as the target aim.
            // For now, let's reset Yaw/Pitch and make newLocalOffset the effective _baseLocalRotationOffset.
            // This means external setting of world rotation re-calibrates the sentry's zero.
            
            // A more sophisticated approach would be to try and extract Yaw/Pitch from newLocalOffset
            // relative to an *original* _baseLocalRotationOffset (e.g. the model's default forward).
            // For now, keep it simpler: external set of Rotation redefines the zero point for yaw/pitch.
            CurrentYaw = 0; // Resetting, as the new rotation defines the current aim.
            CurrentPitch = 0; // Or extract from newLocalOffset if a stable frame is defined.
            
            // If we want to maintain the concept of _baseLocalRotationOffset as the "model's own rotation"
            // and CurrentYaw/Pitch as dynamic adjustments, then:
            // newLocalOffset = _baseLocalRotationOffset * Quaternion.Euler(newPitch, newYaw, 0);
            // We'd need to solve for newPitch, newYaw.
            // For now, the above AimAtTarget correctly calculates LocalRotationOffset from CurrentYaw/Pitch and _baseLocalRotationOffset.
            // If TeleportTo or direct set of Position/Rotation on Sentry is called, AttachedEntity handles it.
            // This method is more for "something else rotated me, how do I interpret my aim now?"
            // Let's assume for now that direct manipulation of Sentry's rotation via TeleportTo or its own Position/Rotation setters
            // correctly updates LocalRotationOffset (as handled by AttachedEntity), and AimAtTarget will then work from there.
            // This function might be more relevant if Level directly sets Entity._rotation without going through property.

            // Re-evaluate: AttachedEntity.Rotation setter does:
            // LocalRotationOffset = Owner.Rotation.Inverse * value;
            // base.Rotation = value;
            // So, if world rotation is set externally, LocalRotationOffset is updated.
            // Then, when AimAtTarget runs, it uses this new LocalRotationOffset as its basis if we don't correctly re-initialize CurrentYaw/Pitch
            // from it.
            // The current AimAtTarget uses _baseLocalRotationOffset and CurrentYaw/Pitch to *set* LocalRotationOffset.
            // This means _baseLocalRotationOffset should be the "zero-aim" rotation.
            // If world rotation is set, then LocalRotationOffset changes. We need to update CurrentYaw/Pitch to match this.
            
            Vector3 forwardVectorOfNewLocalOffset = newLocalOffset * Vector3.Forward;
            CurrentYaw = MathF.Atan2(forwardVectorOfNewLocalOffset.X, forwardVectorOfNewLocalOffset.Z) * MathUtils.Rad2Deg;
            CurrentPitch = MathF.Atan2(-forwardVectorOfNewLocalOffset.Y, MathF.Sqrt(forwardVectorOfNewLocalOffset.X * forwardVectorOfNewLocalOffset.X + forwardVectorOfNewLocalOffset.Z * forwardVectorOfNewLocalOffset.Z)) * MathUtils.Rad2Deg;

            // Clamp them to ensure they are within sentry's operational limits
            CurrentYaw = Math.Clamp(CurrentYaw, MinYawDegrees, MaxYawDegrees);
            CurrentPitch = Math.Clamp(CurrentPitch, MinPitchDegrees, MaxPitchDegrees);
            
            // And then ensure _baseLocalRotationOffset is set such that when these yaw/pitch are applied, we get newLocalOffset
            // _baseLocalRotationOffset = newLocalOffset * Quaternion.Euler(-CurrentPitch, -CurrentYaw, 0f).Inverse; // This is complex.
            // Simpler: Assume _baseLocalRotationOffset is fixed. If world rotation is set, it implies a new aim.
            // The current logic in AimAtTarget should be fine if LocalRotationOffset is changed by AttachedEntity's setters.
            // It will just try to aim from whatever the current CurrentYaw/Pitch is.
            // The crucial part is that _baseLocalRotationOffset must be the true "model zero" rotation.
        }
    }

    // Helper Math class (can be moved to a separate file like Core.Utils.MathUtils)
    public static class MathUtils
    {
        public const float Deg2Rad = MathF.PI / 180.0f;
        public const float Rad2Deg = 180.0f / MathF.PI;

        public static float MoveTowardsAngle(float current, float target, float maxDelta)
        {
            float deltaAngle = NormalizeAngle(target - current);
            if (MathF.Abs(deltaAngle) <= maxDelta)
            {
                return target;
            }
            return NormalizeAngle(current + MathF.Sign(deltaAngle) * maxDelta);
        }

        public static float NormalizeAngle(float degrees)
        {
            degrees = degrees % 360;
            if (degrees > 180)
            {
                degrees -= 360;
            }
            else if (degrees < -180)
            {
                degrees += 360;
            }
            return degrees;
        }
    }
} 