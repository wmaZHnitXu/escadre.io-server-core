// File: Core/Network/Proxies/ShipProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;
using Core.Client; 
using Core.Ocean; 

namespace Core.Network.Proxies
{
    public static class ShipProxy
    {
        internal enum ShipEventType : byte
        {
            SetMovementTarget = 101,
        }

        public class ServerProxy : DestructibleEntityProxy.ServerProxy<Ship>
        {
            public ServerProxy(Ship entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum() {
                int baseHash = base.CalculateChecksum().GetHashCode();
                return HashCode.Combine(baseHash, _entity.CurrentSpeed.GetHashCode(), _entity.MaxSpeed.GetHashCode());
            }
            public override void SerializeSpecificInitialState(BinaryWriter writer) {
                base.SerializeSpecificInitialState(writer);
                writer.Write(_entity.OwningEscadreClientId);
                writer.Write(_entity.CurrentSpeed);
                writer.Write(_entity.MaxSpeed); 
                writer.Write(_entity.TurnRate); 
            }
            protected override void SerializeSpecificCorrectionState(BinaryWriter writer) {
                base.SerializeSpecificCorrectionState(writer);
                writer.Write(_entity.CurrentSpeed); 
                writer.Write(_entity.MaxSpeed); 
                writer.Write(_entity.TurnRate); 
            }

            protected override void StartReplicatingInternal() {
                base.StartReplicatingInternal();
                if (_entity != null) {
                    _entity.OnMovementTargetProgrammed += HandleModelMovementTargetProgrammed;
                }
            }
            protected override void StopReplicatingInternal() {
                base.StopReplicatingInternal();
                if (_entity != null) {
                    _entity.OnMovementTargetProgrammed -= HandleModelMovementTargetProgrammed;
                }
            }

            private void HandleModelMovementTargetProgrammed(Ship ship, Vector2? newTarget, float serverTime) {
                if (ship.Id != _entity.Id) return;
                SendEvent((byte)ShipEventType.SetMovementTarget, writer => {
                    bool hasTarget = newTarget.HasValue;
                    writer.Write(hasTarget);
                    if (hasTarget) {
                        SerializationUtils.WriteVector2(writer, newTarget.Value);
                    }
                    SerializationUtils.WriteVector3(writer, _entity.Position); 
                    SerializationUtils.WriteQuaternion(writer, _entity.Rotation); 
                    writer.Write(_entity.CurrentSpeed); 
                    writer.Write(serverTime); 
                });
            }
        }

        public class ClientProxy : DestructibleEntityProxy.ClientProxy
        {
            public int OwningEscadreClientId { get; private set; }
            private float _clientSimulatedSpeed; 
            public float ClientSimulatedSpeed => _clientSimulatedSpeed; 
            public event Action<float> CurrentSpeedChanged;

            public float MaxSpeed { get; private set; } 
            public float TurnRate { get; private set; }
            public event Action StatsChanged; 

            private Vector2? _currentMovementTarget;
            private bool _isMovingClientSide = false; 

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, concreteType, clientLevel) 
            {
                 // ShipProxy's constructor now explicitly sets its FloatingBehavior
                _clientFloatingBehavior = new DefaultFloatingBehavior();
            }

            protected override void DeserializeSpecificInitialState(BinaryReader reader) {
                base.DeserializeSpecificInitialState(reader); 
                OwningEscadreClientId = reader.ReadInt32();
                _clientSimulatedSpeed = reader.ReadSingle(); 
                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                
                CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed); 
                StatsChanged?.Invoke(); 
            }

            protected override void DeserializeSpecificState(BinaryReader reader) { 
                base.DeserializeSpecificState(reader); 
                
                float serverAuthoritativeSpeed = reader.ReadSingle(); 

                var oldMaxSpeed = MaxSpeed;
                var oldTurnRate = TurnRate; 
                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                
                if (Math.Abs(MaxSpeed - oldMaxSpeed) > float.Epsilon || Math.Abs(TurnRate - oldTurnRate) > float.Epsilon) {
                    StatsChanged?.Invoke();
                }
                // Note: serverAuthoritativeSpeed is read but not directly used to snap _clientSimulatedSpeed here.
                // _clientSimulatedSpeed is managed by the client-side simulation and re-baselined by SetMovementTarget events.
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(ShipEventType), specificEventType))
                {
                    ShipEventType eventType = (ShipEventType)specificEventType;
                    switch (eventType)
                    {
                        case ShipEventType.SetMovementTarget:
                            HandleSetMovementTargetEventPayload(reader);
                            break;
                        default:
                            Logger.LogWarning($"[ShipProxy.Client {EntityId}] Received unhandled ShipEventType: {eventType}");
                            // Pass to base if DestructibleEntityProxy might handle it
                            base.HandleSpecificEvent(specificEventType, reader);
                            break;
                    }
                }
                else {
                    base.HandleSpecificEvent(specificEventType, reader); 
                }
            }

            private void HandleSetMovementTargetEventPayload(BinaryReader reader)
            {
                bool hasTarget = reader.ReadBoolean();
                _currentMovementTarget = hasTarget ? SerializationUtils.ReadVector2(reader) : (Vector2?)null;
                
                Vector3 serverPosAtCommand = SerializationUtils.ReadVector3(reader);
                Quaternion serverRotAtCommand = SerializationUtils.ReadQuaternion(reader);
                float serverSpeedAtCommand = reader.ReadSingle();
                float serverTimeOfCommand = reader.ReadSingle(); 

                float clientTimeNow = OwningClientLevel.CurrentTime;
                float catchUpDeltaTime = clientTimeNow - serverTimeOfCommand;

                Vector3 predictedPos = serverPosAtCommand;
                Quaternion predictedRot = serverRotAtCommand;
                float predictedSpeed = serverSpeedAtCommand;
                bool stillMovingAfterCatchUp = hasTarget;

                if (catchUpDeltaTime > 0.001f && _currentMovementTarget.HasValue) 
                {
                    var catchUpResult = SimulateMovementStep(
                        serverPosAtCommand, serverRotAtCommand, serverSpeedAtCommand,
                        _currentMovementTarget, MaxSpeed, TurnRate, catchUpDeltaTime, true 
                    );
                    predictedPos = catchUpResult.newPos;
                    predictedRot = catchUpResult.newRot;
                    predictedSpeed = catchUpResult.newSpeed;
                    stillMovingAfterCatchUp = catchUpResult.stillMoving;
                }
                // Note: serverPosAtCommand and serverRotAtCommand already include ocean effects up to serverTimeOfCommand.
                // The SimulateMovementStep for catch-up primarily handles XZ planar movement and Yaw.
                // The ocean effect will be applied in the regular Update() loop based on the new predictedPos/Rot.

                SetSimulatedPositionAndRotation(predictedPos, predictedRot);
                if (Math.Abs(_clientSimulatedSpeed - predictedSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = predictedSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
                
                _isMovingClientSide = stillMovingAfterCatchUp && hasTarget; 
                if (!_isMovingClientSide) _currentMovementTarget = null;
            }
            
            // SimulateMovementStep primarily handles planar XZ movement and Yaw adjustments for the ship.
            // It returns a new planar position and yaw-based rotation.
            // Floating behavior (Y position, Pitch, Roll) is handled by the main Update loop.
            private (Vector3 newPos, Quaternion newRot, float newSpeed, bool stillMoving) SimulateMovementStep(
                Vector3 currentPosition, Quaternion currentYawOrientation, float currentSpeedParam,
                Vector2? target, float currentMaxSpeed, float currentTurnRate, float deltaTime, bool hasExternalMoveOrder)
            {
                Vector3 nextPosXZ = new Vector3(currentPosition.X, 0, currentPosition.Z); // Work in XZ plane for now
                Quaternion nextYaw = currentYawOrientation; // This should be predominantly yaw
                float nextSpeed = currentSpeedParam;
                bool stillNeedsToMove = hasExternalMoveOrder;

                if (!hasExternalMoveOrder || !target.HasValue)
                {
                    if (nextSpeed > 0) nextSpeed = Math.Max(0, nextSpeed - (currentMaxSpeed * 2f * deltaTime)); 
                    else nextSpeed = 0f;
                    stillNeedsToMove = false;
                     // Return XZ pos, current Y is kept until ocean applied, same for full rotation
                    return (new Vector3(nextPosXZ.X, currentPosition.Y, nextPosXZ.Z), nextYaw, nextSpeed, stillNeedsToMove);
                }

                Vector2 currentPos2D = new Vector2(currentPosition.X, currentPosition.Z);
                Vector2 targetPos2D = target.Value;
                Vector2 toTarget = targetPos2D - currentPos2D;

                float distanceToTargetSq = toTarget.SqrMagnitude;
                
                float speedForStoppingCalc = currentMaxSpeed; 
                float stoppingDistance = speedForStoppingCalc * deltaTime * 0.75f; 
                float stoppingDistanceSq = stoppingDistance * stoppingDistance;
                stoppingDistanceSq = Math.Max(0.01f * 0.01f, stoppingDistanceSq); 

                if (distanceToTargetSq < stoppingDistanceSq)
                {
                    nextPosXZ = new Vector3(targetPos2D.X, 0, targetPos2D.Y); 
                    nextSpeed = 0f;
                    stillNeedsToMove = false;
                }
                else
                {
                    if (nextSpeed < currentMaxSpeed) nextSpeed = Math.Min(currentMaxSpeed, nextSpeed + (currentMaxSpeed * 1.0f * deltaTime));
                    else nextSpeed = currentMaxSpeed;

                    Vector2 directionToTarget = toTarget.Normalized;
                    Vector3 targetForwardPlanar = new Vector3(directionToTarget.X, 0, directionToTarget.Y);

                    if (targetForwardPlanar.SqrMagnitude > Vector3.Epsilon)
                    {
                        Quaternion desiredYawRotation = Quaternion.LookRotation(targetForwardPlanar, Vector3.Up);
                        // currentYawOrientation might have pitch/roll from ocean if not careful.
                        // It's better to extract planar forward from currentYawOrientation first.
                        Vector3 currentPlanarForward = (currentYawOrientation * Vector3.Forward);
                        currentPlanarForward = new Vector3(currentPlanarForward.X, 0f, currentPlanarForward.Z);
                        Quaternion actualCurrentYaw = Quaternion.LookRotation(currentPlanarForward.NormalizedSafe(Vector3.Forward), Vector3.Up);

                        nextYaw = Quaternion.RotateTowards(actualCurrentYaw, desiredYawRotation, currentTurnRate * deltaTime);
                    }
                    Vector3 velocity = nextYaw * Vector3.Forward * nextSpeed * deltaTime;
                    nextPosXZ += velocity;
                }
                 // Return XZ pos, current Y is kept until ocean applied, nextYaw for orientation
                return (new Vector3(nextPosXZ.X, currentPosition.Y, nextPosXZ.Z), nextYaw, nextSpeed, stillNeedsToMove);
            }


            public override void Update(float deltaTime)
            {
                // Do NOT call base.Update(deltaTime) here if we are fully overriding.
                // BaseClientProxy.Update() has its own simple floating logic which we don't want for ships
                // as ships have combined planar and floating movement.
                // If base.Update() had other critical logic (it doesn't currently), we'd reconsider.

                if (deltaTime <= 0f || _isDestroyed) return;

                Vector3 currentSimPos = _simulatedPosition;
                Quaternion currentSimRot = _simulatedRotation; // This includes ocean effects from last frame.
                float currentSimSpeed = _clientSimulatedSpeed;
                bool isStillMovingPlanar = _isMovingClientSide;


                // 1. Simulate Planar (XZ) Movement and Yaw
                if (!_isMovingClientSide || !_currentMovementTarget.HasValue)
                {
                    // Decelerate planar speed if no target
                    if (currentSimSpeed > 0)
                    {
                        currentSimSpeed = Math.Max(0, currentSimSpeed - (MaxSpeed * 2f * deltaTime));
                    }
                    else { currentSimSpeed = 0f; }
                    isStillMovingPlanar = false;
                }
                else // Has a planar target
                {
                    var planarSimResult = SimulateMovementStep(
                        currentSimPos,      // Current full position
                        currentSimRot,      // Current full rotation (includes ocean tilt)
                        currentSimSpeed,    // Current planar speed
                        _currentMovementTarget, 
                        MaxSpeed, 
                        TurnRate, 
                        deltaTime, 
                        _isMovingClientSide
                    );

                    currentSimPos = new Vector3(planarSimResult.newPos.X, currentSimPos.Y, planarSimResult.newPos.Z); // Update XZ from planar sim, keep Y
                    currentSimRot = planarSimResult.newRot; // This newRot is primarily YAW based. Pitch/Roll will be applied by ocean.
                    currentSimSpeed = planarSimResult.newSpeed;
                    isStillMovingPlanar = planarSimResult.stillMoving;
                }
                
                _isMovingClientSide = isStillMovingPlanar;
                if (!_isMovingClientSide) {
                     _currentMovementTarget = null; 
                }      

                // 2. Apply Floating Behavior (adjusts Y position, and Pitch/Roll to the currentSimRot which is mainly Yaw)
                if (_clientFloatingBehavior != null && OwningClientLevel.IsOceanInitialized)
                {
                    float sampleX = currentSimPos.X;
                    float sampleZ = currentSimPos.Z;

                    Vector3 oceanDisplacement = OwningClientLevel.OceanDataProvider.GetDisplacement(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                    Vector3 oceanNormal = OwningClientLevel.OceanDataProvider.GetNormal(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                    Vector3 targetSurfacePoint = new Vector3(
                        sampleX + oceanDisplacement.X,
                        oceanDisplacement.Y, 
                        sampleZ + oceanDisplacement.Z
                    );
                    
                    // Use the TempEntity hack from BaseClientProxy or make ApplyFloating take raw values
                    var tempEntity = new TempEntityForFloatingLogic(currentSimPos, currentSimRot);
                    _clientFloatingBehavior.ApplyFloating(tempEntity, targetSurfacePoint, oceanNormal, deltaTime);
                    
                    currentSimPos = tempEntity.Position; // Position now has correct Y and ocean-induced XZ drift
                    currentSimRot = tempEntity.Rotation; // Rotation now has ocean-induced Pitch/Roll added to the Yaw from planar sim
                }
                
                // 3. Set final simulated state and invoke events
                SetSimulatedPositionAndRotation(currentSimPos, currentSimRot);
                
                if (Math.Abs(_clientSimulatedSpeed - currentSimSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = currentSimSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
            }

            protected override void CleanupEvents() {
                base.CleanupEvents(); 
                CurrentSpeedChanged = null; 
                StatsChanged = null;
            }
            protected override void InvokeSpecificStateChangedEvents() { 
                base.InvokeSpecificStateChangedEvents();
            }
        }
    }
}