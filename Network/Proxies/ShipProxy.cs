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
                _clientFloatingBehavior = new DefaultFloatingBehavior();
                _clientFloatingBehavior.HorizontalInfluence = 0f;
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
                    var catchUpResult = SimulatePlanarMovementStep( // Renamed for clarity
                        serverPosAtCommand, serverRotAtCommand, serverSpeedAtCommand,
                        _currentMovementTarget, MaxSpeed, TurnRate, catchUpDeltaTime, true 
                    );
                    predictedPos = catchUpResult.newPos; // This is XZ updated, Y from server
                    predictedRot = catchUpResult.newRot; // This is primarily Yaw
                    predictedSpeed = catchUpResult.newSpeed;
                    stillMovingAfterCatchUp = catchUpResult.stillMoving;
                }

                SetSimulatedPositionAndRotation(predictedPos, predictedRot);
                if (Math.Abs(_clientSimulatedSpeed - predictedSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = predictedSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
                
                _isMovingClientSide = stillMovingAfterCatchUp && hasTarget; 
                if (!_isMovingClientSide) _currentMovementTarget = null;
            }
            
            private (Vector3 newPos, Quaternion newRot, float newSpeed, bool stillMoving) SimulatePlanarMovementStep(
                Vector3 currentFullPosition, Quaternion currentFullRotation, float currentSpeedParam,
                Vector2? target, float currentMaxSpeed, float currentTurnRate, float deltaTime, bool hasExternalMoveOrder)
            {
                Vector3 nextPosXZ = new Vector3(currentFullPosition.X, 0, currentFullPosition.Z); 
                
                // Extract current planar forward from full rotation to get current yaw for ships
                Vector3 currentPlanarForwardVec = (currentFullRotation * Vector3.Forward);
                currentPlanarForwardVec = new Vector3(currentPlanarForwardVec.X, 0.0f, currentPlanarForwardVec.Z);
                Quaternion currentYawOrientation = Quaternion.LookRotation(currentPlanarForwardVec.NormalizedSafe(Vector3.Forward), Vector3.Up);
                Quaternion nextYaw = currentYawOrientation;

                float nextSpeed = currentSpeedParam;
                bool stillNeedsToMove = hasExternalMoveOrder;

                if (!hasExternalMoveOrder || !target.HasValue)
                {
                    if (nextSpeed > 0) nextSpeed = Math.Max(0, nextSpeed - (currentMaxSpeed * 2f * deltaTime)); 
                    else nextSpeed = 0f;
                    stillNeedsToMove = false;
                    return (new Vector3(nextPosXZ.X, currentFullPosition.Y, nextPosXZ.Z), nextYaw, nextSpeed, stillNeedsToMove);
                }

                Vector2 currentPos2D = new Vector2(currentFullPosition.X, currentFullPosition.Z);
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
                        nextYaw = Quaternion.RotateTowards(currentYawOrientation, desiredYawRotation, currentTurnRate * deltaTime);
                    }
                    Vector3 velocity = nextYaw * Vector3.Forward * nextSpeed * deltaTime;
                    nextPosXZ += velocity;
                }
                return (new Vector3(nextPosXZ.X, currentFullPosition.Y, nextPosXZ.Z), nextYaw, nextSpeed, stillNeedsToMove);
            }


            public override void Update(float deltaTime)
            {
                if (deltaTime <= 0f || _isDestroyed) return;

                Vector3 newSimPos = _simulatedPosition;
                Quaternion newSimRot = _simulatedRotation; 
                float newSimSpeed = _clientSimulatedSpeed;
                bool stillMovingPlanar = _isMovingClientSide;


                // 1. Simulate Planar (XZ) Movement and Yaw
                if (!_isMovingClientSide || !_currentMovementTarget.HasValue)
                {
                    if (newSimSpeed > 0)
                    {
                        newSimSpeed = Math.Max(0, newSimSpeed - (MaxSpeed * 2f * deltaTime));
                    }
                    else { newSimSpeed = 0f; }
                    stillMovingPlanar = false;
                }
                else 
                {
                    var planarSimResult = SimulatePlanarMovementStep(
                        newSimPos,      
                        newSimRot,      
                        newSimSpeed,    
                        _currentMovementTarget, 
                        MaxSpeed, 
                        TurnRate, 
                        deltaTime, 
                        _isMovingClientSide
                    );

                    newSimPos = new Vector3(planarSimResult.newPos.X, newSimPos.Y, planarSimResult.newPos.Z); 
                    newSimRot = planarSimResult.newRot; 
                    newSimSpeed = planarSimResult.newSpeed;
                    stillMovingPlanar = planarSimResult.stillMoving;
                }
                
                _isMovingClientSide = stillMovingPlanar;
                if (!_isMovingClientSide) {
                     _currentMovementTarget = null; 
                }      

                // 2. Apply Floating Behavior 
                if (_clientFloatingBehavior != null && OwningClientLevel.IsOceanInitialized)
                {
                    float sampleX = newSimPos.X; // Use the XZ from planar sim
                    float sampleZ = newSimPos.Z;

                    Vector3 oceanDisplacement = OwningClientLevel.OceanDataProvider.GetDisplacement(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                    Vector3 oceanNormal = OwningClientLevel.OceanDataProvider.GetNormal(sampleX, sampleZ, OwningClientLevel.CurrentTime);
                    Vector3 targetSurfacePoint = new Vector3(
                        sampleX + oceanDisplacement.X,
                        oceanDisplacement.Y, 
                        sampleZ + oceanDisplacement.Z
                    );
                    
                    Vector3 finalPos;
                    Quaternion finalRot;
                    _clientFloatingBehavior.ApplyFloating(newSimPos, newSimRot, targetSurfacePoint, oceanNormal, deltaTime, out finalPos, out finalRot);
                    
                    newSimPos = finalPos; 
                    newSimRot = finalRot; 
                }
                
                SetSimulatedPositionAndRotation(newSimPos, newSimRot);
                
                if (Math.Abs(_clientSimulatedSpeed - newSimSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = newSimSpeed;
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