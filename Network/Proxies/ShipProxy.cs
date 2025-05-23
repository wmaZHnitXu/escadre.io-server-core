// File: Core/Network/Proxies/ShipProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;
using Core.Client; // For ClientLevel

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
            public event Action StatsChanged; // Still useful for MaxSpeed, TurnRate

            private Vector2? _currentMovementTarget;
            private bool _isMovingClientSide = false; 

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, concreteType, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader) {
                base.DeserializeSpecificInitialState(reader); 
                OwningEscadreClientId = reader.ReadInt32();
                _clientSimulatedSpeed = reader.ReadSingle(); 
                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                
                CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed); 
                StatsChanged?.Invoke(); // For MaxSpeed, TurnRate
            }

            protected override void DeserializeSpecificState(BinaryReader reader) { 
                base.DeserializeSpecificState(reader); 
                
                float serverAuthoritativeSpeed = reader.ReadSingle(); 

                var oldMaxSpeed = MaxSpeed;
                var oldTurnRate = TurnRate; // Store old TurnRate to check for changes
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

                SetSimulatedPositionAndRotation(predictedPos, predictedRot);
                if (Math.Abs(_clientSimulatedSpeed - predictedSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = predictedSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
                
                _isMovingClientSide = stillMovingAfterCatchUp && hasTarget; 
                if (!_isMovingClientSide) _currentMovementTarget = null;
            }
            
            private (Vector3 newPos, Quaternion newRot, float newSpeed, bool stillMoving) SimulateMovementStep(
                Vector3 currentPosition, Quaternion currentRotation, float currentSpeedParam,
                Vector2? target, float currentMaxSpeed, float currentTurnRate, float deltaTime, bool hasExternalMoveOrder)
            {
                Vector3 nextPos = currentPosition;
                Quaternion nextRot = currentRotation;
                float nextSpeed = currentSpeedParam;
                bool stillNeedsToMove = hasExternalMoveOrder;

                if (!hasExternalMoveOrder || !target.HasValue)
                {
                    if (nextSpeed > 0) nextSpeed = Math.Max(0, nextSpeed - (currentMaxSpeed * 2f * deltaTime)); 
                    else nextSpeed = 0f;
                    stillNeedsToMove = false;
                    return (nextPos, nextRot, nextSpeed, stillNeedsToMove);
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
                    nextPos = new Vector3(targetPos2D.X, currentPosition.Y, targetPos2D.Y); 
                    nextSpeed = 0f;
                    stillNeedsToMove = false;
                }
                else
                {
                    if (nextSpeed < currentMaxSpeed) nextSpeed = Math.Min(currentMaxSpeed, nextSpeed + (currentMaxSpeed * 1.0f * deltaTime));
                    else nextSpeed = currentMaxSpeed;

                    Vector2 directionToTarget = toTarget.Normalized;
                    Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);

                    if (targetForward.SqrMagnitude > Vector3.Epsilon)
                    {
                        Quaternion desiredRotation = Quaternion.LookRotation(targetForward, Vector3.Up);
                        nextRot = Quaternion.RotateTowards(currentRotation, desiredRotation, currentTurnRate * deltaTime);
                    }
                    Vector3 velocity = nextRot * Vector3.Forward * nextSpeed * deltaTime;
                    nextPos += velocity;
                }
                return (nextPos, nextRot, nextSpeed, stillNeedsToMove);
            }


            public override void Update(float deltaTime)
            {
                base.Update(deltaTime); 
                if (deltaTime <= 0f) return;

                if (!_isMovingClientSide || !_currentMovementTarget.HasValue)
                {
                    if (_clientSimulatedSpeed > 0)
                    {
                        _clientSimulatedSpeed = Math.Max(0, _clientSimulatedSpeed - (MaxSpeed * 2f * deltaTime)); 
                        CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                    }
                    else { _clientSimulatedSpeed = 0f; }
                    return; 
                }

                var simResult = SimulateMovementStep(
                    _simulatedPosition, 
                    _simulatedRotation, 
                    _clientSimulatedSpeed, 
                    _currentMovementTarget, 
                    MaxSpeed, 
                    TurnRate, 
                    deltaTime, 
                    _isMovingClientSide
                );

                SetSimulatedPositionAndRotation(simResult.newPos, simResult.newRot);
                
                if (Math.Abs(_clientSimulatedSpeed - simResult.newSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = simResult.newSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }

                _isMovingClientSide = simResult.stillMoving;
                if (!_isMovingClientSide) {
                     _currentMovementTarget = null; 
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