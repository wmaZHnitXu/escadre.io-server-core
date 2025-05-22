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
            SetMovementTarget = 1,
        }

        public class ServerProxy : DestructibleEntityProxy.ServerProxy<Ship>
        {
            public ServerProxy(Ship entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer) { }

            protected override float CalculateChecksum() {
                int baseHash = base.CalculateChecksum().GetHashCode();
                return HashCode.Combine(baseHash, _entity.CurrentSpeed.GetHashCode(), _entity.MaxSpeed.GetHashCode(), _entity.AttackRange.GetHashCode());
            }
            public override void SerializeSpecificInitialState(BinaryWriter writer) {
                base.SerializeSpecificInitialState(writer);
                writer.Write(_entity.OwningEscadreClientId);
                writer.Write(_entity.CurrentSpeed);
                writer.Write(_entity.MaxSpeed); writer.Write(_entity.TurnRate); writer.Write(_entity.AttackDamage);
                writer.Write(_entity.AttackRange); writer.Write(_entity.AttackCooldown);
            }
            protected override void SerializeSpecificCorrectionState(BinaryWriter writer) {
                base.SerializeSpecificCorrectionState(writer);
                writer.Write(_entity.CurrentSpeed); 
                writer.Write(_entity.MaxSpeed); writer.Write(_entity.TurnRate); writer.Write(_entity.AttackDamage);
                writer.Write(_entity.AttackRange); writer.Write(_entity.AttackCooldown);
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
                    SerializationUtils.WriteVector3(writer, _entity.Position); // Server pos at command
                    SerializationUtils.WriteQuaternion(writer, _entity.Rotation); // Server rot at command
                    writer.Write(_entity.CurrentSpeed); // Server speed at command
                    writer.Write(serverTime); // Server time of command
                });
            }
        }

        public class ClientProxy : DestructibleEntityProxy.ClientProxy
        {
            public int OwningEscadreClientId { get; private set; }
            private float _clientSimulatedSpeed; 
            public float ClientSimulatedSpeed => _clientSimulatedSpeed; 
            public event Action<float> CurrentSpeedChanged;

            public float MaxSpeed { get; private set; } public float TurnRate { get; private set; }
            public float AttackDamage { get; private set; } public float AttackRange { get; private set; }
            public float AttackCooldown { get; private set; }
            public event Action StatsChanged;

            private Vector2? _currentMovementTarget;
            private bool _isMovingClientSide = false; 

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, concreteType, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader) {
                base.DeserializeSpecificInitialState(reader); 
                OwningEscadreClientId = reader.ReadInt32();
                _clientSimulatedSpeed = reader.ReadSingle(); 
                MaxSpeed = reader.ReadSingle(); TurnRate = reader.ReadSingle();
                AttackDamage = reader.ReadSingle(); AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();

                CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed); 
                StatsChanged?.Invoke();
            }

            protected override void DeserializeSpecificState(BinaryReader reader) { 
                base.DeserializeSpecificState(reader); 
                
                float serverAuthoritativeSpeed = reader.ReadSingle(); 
                // If we are doing full state correction, we might snap _clientSimulatedSpeed too.
                // For now, this is mainly for full stat overrides (e.g. after upgrade)
                // If Math.Abs(_clientSimulatedSpeed - serverAuthoritativeSpeed) > some_threshold then adjust.
                // However, this can cause jitter if server speed fluctuates differently than client prediction.

                var oldMaxSpeed = MaxSpeed;
                MaxSpeed = reader.ReadSingle(); TurnRate = reader.ReadSingle();
                AttackDamage = reader.ReadSingle(); AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();
                if (Math.Abs(MaxSpeed - oldMaxSpeed) > float.Epsilon /* || other stats changed significantly */) {
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
                    // Pass to base if this proxy doesn't handle it (e.g. DestructibleEntity events)
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

                if (catchUpDeltaTime > 0.001f && _currentMovementTarget.HasValue) // Only catch up if time has passed and there's a target
                {
                    // Simulate movement for the catchUpDeltaTime
                    // To avoid duplicating logic, we can call a helper or a stripped-down version of UpdateMovement
                    // For simplicity here, let's assume a single step catch-up.
                    // A more accurate catch-up might involve multiple small steps if catchUpDeltaTime is large.
                    var catchUpResult = SimulateMovementStep(
                        serverPosAtCommand, serverRotAtCommand, serverSpeedAtCommand,
                        _currentMovementTarget, MaxSpeed, TurnRate, catchUpDeltaTime, true // Assume wants to move
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
                
                _isMovingClientSide = stillMovingAfterCatchUp && hasTarget; // If catch-up reached target, stop
                if (!_isMovingClientSide) _currentMovementTarget = null;


                // Logger.Log($"[ShipProxy.Client {EntityId}] Rcvd SetMovementTarget. Target: {_currentMovementTarget?.ToString() ?? "None"}. ServerTime: {serverTimeOfCommand}, ClientTime: {clientTimeNow}, CatchUpDT: {catchUpDeltaTime}. Snapped/Predicted to Pos={predictedPos}, Rot={predictedRot}, Spd={predictedSpeed}");
            }
            
            /// <summary>
            /// Simulates one step of movement.
            /// </summary>
            /// <returns>Tuple of newPos, newRot, newSpeed, stillMoving</returns>
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
                    if (nextSpeed > 0) nextSpeed = Math.Max(0, nextSpeed - (currentMaxSpeed * 2f * deltaTime)); // Decelerate
                    else nextSpeed = 0f;
                    stillNeedsToMove = false;
                    return (nextPos, nextRot, nextSpeed, stillNeedsToMove);
                }

                Vector2 currentPos2D = new Vector2(currentPosition.X, currentPosition.Z);
                Vector2 targetPos2D = target.Value;
                Vector2 toTarget = targetPos2D - currentPos2D;

                float distanceToTargetSq = toTarget.SqrMagnitude;
                
                // Adjusted stopping condition: try to match server's dynamic threshold principle
                float speedForStoppingCalc = currentMaxSpeed; // Use MaxSpeed for threshold calculation
                float stoppingDistance = speedForStoppingCalc * deltaTime * 0.75f; // A bit more generous factor for client
                float stoppingDistanceSq = stoppingDistance * stoppingDistance;
                stoppingDistanceSq = Math.Max(0.01f * 0.01f, stoppingDistanceSq); // Min threshold

                if (distanceToTargetSq < stoppingDistanceSq)
                {
                    nextPos = new Vector3(targetPos2D.X, currentPosition.Y, targetPos2D.Y); // Snap to target XZ
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

                var result = SimulateMovementStep(
                    _simulatedPosition, _simulatedRotation, _clientSimulatedSpeed,
                    _currentMovementTarget, this.MaxSpeed, this.TurnRate, deltaTime, _isMovingClientSide
                );

                SetSimulatedPositionAndRotation(result.newPos, result.newRot);
                if (Math.Abs(_clientSimulatedSpeed - result.newSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = result.newSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
                
                _isMovingClientSide = result.stillMoving;
                if (!_isMovingClientSide && _currentMovementTarget.HasValue) // If simulation stopped it, clear target
                {
                    _currentMovementTarget = null; 
                    // Logger.Log($"[ShipProxy.Client {EntityId}] Movement target reached/cleared by simulation step.");
                }
            }

            protected override void CleanupEvents() {
                base.CleanupEvents(); CurrentSpeedChanged = null; StatsChanged = null;
            }
            protected override void InvokeSpecificStateChangedEvents() { 
                base.InvokeSpecificStateChangedEvents();
            }
        }
    }
}