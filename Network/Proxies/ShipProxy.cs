// File: Core/Network/Proxies/ShipProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;
using Core.Client; 
using Core.Ocean; 
using System.Collections.Generic; // Required for List

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
                return HashCode.Combine(baseHash, 
                                        _entity.CurrentSpeed.GetHashCode(), 
                                        _entity.FormationThreshold.GetHashCode(),
                                        _entity.AccelerationRate.GetHashCode(),
                                        _entity.DecelerationRate.GetHashCode());
            }
            public override void SerializeSpecificInitialState(BinaryWriter writer) {
                base.SerializeSpecificInitialState(writer);
                writer.Write(_entity.OwningEscadreClientId);
                writer.Write(_entity.CurrentSpeed);
                writer.Write(_entity.MaxSpeed); 
                writer.Write(_entity.TurnRate); 
                writer.Write(_entity.SlowingDistance);
                writer.Write(_entity.StoppingDistance);
                writer.Write(_entity.FormationThreshold);
                writer.Write(_entity.AccelerationRate);
                writer.Write(_entity.DecelerationRate);
            }
            protected override void SerializeSpecificCorrectionState(BinaryWriter writer) {
                base.SerializeSpecificCorrectionState(writer);
                writer.Write(_entity.CurrentSpeed); 
                writer.Write(_entity.MaxSpeed); 
                writer.Write(_entity.TurnRate); 
                writer.Write(_entity.SlowingDistance);
                writer.Write(_entity.StoppingDistance);
                writer.Write(_entity.FormationThreshold);
                writer.Write(_entity.AccelerationRate);
                writer.Write(_entity.DecelerationRate);
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
            public float SlowingDistance { get; private set; }
            public float StoppingDistance { get; private set; }
            public float FormationThreshold { get; private set; } // Not directly used by client sim, but available
            public float AccelerationRate { get; private set; }
            public float DecelerationRate { get; private set; }
            public event Action StatsChanged; 

            private Vector2? _currentMovementTarget; 
            private bool _isMovingClientSide = false;

            private readonly List<Vector3> _clientShipFloatingPoints = new List<Vector3>
            {
                new Vector3(0f, 0f, 2.0f),   
                new Vector3(0f, 0f, -2.0f),  
                new Vector3(0.5f, 0f, 0f),   
                new Vector3(-0.5f, 0f, 0f)   
            };

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, concreteType, clientLevel)
            {
            }

            protected override void DeserializeSpecificInitialState(BinaryReader reader) {
                base.DeserializeSpecificInitialState(reader); 
                OwningEscadreClientId = reader.ReadInt32();
                _clientSimulatedSpeed = reader.ReadSingle(); 
                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                SlowingDistance = reader.ReadSingle();
                StoppingDistance = reader.ReadSingle();
                FormationThreshold = reader.ReadSingle();
                AccelerationRate = reader.ReadSingle();
                DecelerationRate = reader.ReadSingle();
                
                CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed); 
                StatsChanged?.Invoke(); 
            }

            protected override void DeserializeSpecificState(BinaryReader reader) { 
                base.DeserializeSpecificState(reader); 
                
                var oldMaxSpeed = MaxSpeed;
                var oldTurnRate = TurnRate; 
                var oldSlowingDist = SlowingDistance;
                var oldStoppingDist = StoppingDistance;
                var oldFormationThresh = FormationThreshold;
                var oldAccel = AccelerationRate;
                var oldDecel = DecelerationRate;

                float serverAuthoritativeSpeed = reader.ReadSingle();
                 if (Math.Abs(_clientSimulatedSpeed - serverAuthoritativeSpeed) > 0.01f && !_isMovingClientSide)
                 {
                     _clientSimulatedSpeed = serverAuthoritativeSpeed;
                 }

                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                SlowingDistance = reader.ReadSingle();
                StoppingDistance = reader.ReadSingle();
                FormationThreshold = reader.ReadSingle();
                AccelerationRate = reader.ReadSingle();
                DecelerationRate = reader.ReadSingle();
                
                if (Math.Abs(MaxSpeed - oldMaxSpeed) > float.Epsilon || 
                    Math.Abs(TurnRate - oldTurnRate) > float.Epsilon ||
                    Math.Abs(SlowingDistance - oldSlowingDist) > float.Epsilon ||
                    Math.Abs(StoppingDistance - oldStoppingDist) > float.Epsilon ||
                    Math.Abs(FormationThreshold - oldFormationThresh) > float.Epsilon ||
                    Math.Abs(AccelerationRate - oldAccel) > float.Epsilon ||
                    Math.Abs(DecelerationRate - oldDecel) > float.Epsilon) 
                {
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
                Vector2? serverCommandedTarget = hasTarget ? SerializationUtils.ReadVector2(reader) : (Vector2?)null;
                
                Vector3 serverPosAtCommand = SerializationUtils.ReadVector3(reader);
                Quaternion serverRotAtCommand = SerializationUtils.ReadQuaternion(reader);
                float serverSpeedAtCommand = reader.ReadSingle();
                float serverTimeOfCommand = reader.ReadSingle(); 

                float clientTimeNow = OwningClientLevel.CurrentTime;
                float catchUpDeltaTime = Math.Max(0, clientTimeNow - serverTimeOfCommand);

                Vector3 predictedPos = serverPosAtCommand;
                Quaternion predictedRot = serverRotAtCommand;
                float predictedSpeed = serverSpeedAtCommand;
                bool stillMovingAfterCatchUp = hasTarget;

                if (catchUpDeltaTime > 0.001f && serverCommandedTarget.HasValue) 
                {
                    var catchUpResult = SimulatePlanarMovementStep(
                        serverPosAtCommand, serverRotAtCommand, serverSpeedAtCommand,
                        serverCommandedTarget, catchUpDeltaTime, true 
                    );
                    predictedPos = catchUpResult.newPos; 
                    predictedRot = catchUpResult.newRot; 
                    predictedSpeed = catchUpResult.newSpeed;
                    stillMovingAfterCatchUp = catchUpResult.stillMoving;
                }
                
                _currentMovementTarget = serverCommandedTarget; 
                _isMovingClientSide = stillMovingAfterCatchUp && serverCommandedTarget.HasValue;

                if (!_isMovingClientSide && _currentMovementTarget.HasValue) 
                {
                    float distSq = (_currentMovementTarget.Value - new Vector2(predictedPos.X, predictedPos.Z)).SqrMagnitude;
                    // Use a slightly more generous stopping distance check for clearing the target on client
                    if(distSq < (StoppingDistance + 0.3f) * (StoppingDistance + 0.3f)) 
                    {
                         _currentMovementTarget = null;
                    }
                }


                SetSimulatedPositionAndRotation(predictedPos, predictedRot);
                if (Math.Abs(_clientSimulatedSpeed - predictedSpeed) > 0.01f)
                {
                    _clientSimulatedSpeed = predictedSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
            }
            
            private (Vector3 newPos, Quaternion newRot, float newSpeed, bool stillMoving) SimulatePlanarMovementStep(
                Vector3 currentFullPosition, Quaternion currentFullRotation, float currentSpeedParam,
                Vector2? targetSlotPos, float deltaTime, bool hasExternalMoveOrder)
            {
                float targetSpeedThisFrame;
                bool stillNeedsToMove = hasExternalMoveOrder && targetSlotPos.HasValue;

                if (!stillNeedsToMove)
                {
                    targetSpeedThisFrame = 0f;
                }
                else
                {
                    Vector2 currentPos2D = new Vector2(currentFullPosition.X, currentFullPosition.Z);
                    Vector2 targetPos2D = targetSlotPos.Value;
                    Vector2 toTarget = targetPos2D - currentPos2D;
                    float distanceToTarget = toTarget.Magnitude;
                    
                    float finalDecelRate = DecelerationRate; 
                    if (finalDecelRate < Vector3.Epsilon) finalDecelRate = 1f;
                    float predictiveStopDist = (currentSpeedParam * currentSpeedParam) / (2f * finalDecelRate);
                    float actualStoppingThreshold = Math.Max(predictiveStopDist, StoppingDistance) + 0.1f; 


                    if (distanceToTarget < actualStoppingThreshold)
                    {
                        targetSpeedThisFrame = 0f;
                    }
                    else if (distanceToTarget < SlowingDistance)
                    {
                        targetSpeedThisFrame = MaxSpeed * Math.Clamp(distanceToTarget / Math.Max(SlowingDistance,0.1f), 0.1f, 1.0f);
                        targetSpeedThisFrame = Math.Max(0, targetSpeedThisFrame);
                    }
                    else
                    {
                        targetSpeedThisFrame = MaxSpeed;
                    }
                    targetSpeedThisFrame = Math.Min(targetSpeedThisFrame, MaxSpeed); 
                }

                float nextSpeed;
                 if (targetSpeedThisFrame > currentSpeedParam)
                {
                    nextSpeed = MathUtils.MoveTowards(currentSpeedParam, targetSpeedThisFrame, AccelerationRate * deltaTime);
                }
                else
                {
                    nextSpeed = MathUtils.MoveTowards(currentSpeedParam, targetSpeedThisFrame, DecelerationRate * deltaTime);
                }

                if (nextSpeed < 0.01f)
                {
                    nextSpeed = 0f;
                    if (!targetSlotPos.HasValue) {
                        stillNeedsToMove = false;
                    } else {
                        Vector2 currentPos2D = new Vector2(currentFullPosition.X, currentFullPosition.Z);
                        if((targetSlotPos.Value - currentPos2D).SqrMagnitude < (StoppingDistance + 0.3f) * (StoppingDistance + 0.3f))
                        {
                            stillNeedsToMove = false;
                        } else {
                            stillNeedsToMove = true; 
                        }
                    }
                } else {
                    stillNeedsToMove = targetSlotPos.HasValue; 
                }


                Quaternion finalCombinedRotation = currentFullRotation;
                Vector3 nextPosWithPreservedY = currentFullPosition;

                if (stillNeedsToMove || nextSpeed > 0.001f) 
                {
                    Vector3 previousForwardFull_client = currentFullRotation * Vector3.Forward;
                    Vector3 previousForwardPlanar_client = new Vector3(previousForwardFull_client.X, 0f, previousForwardFull_client.Z).NormalizedSafe(Vector3.Forward);
                    Quaternion currentPureYawOrientation_client = Quaternion.LookRotation(previousForwardPlanar_client, Vector3.Up);

                    Quaternion desiredPureYawRotation_client = currentPureYawOrientation_client; 
                    if (targetSlotPos.HasValue)
                    {
                        Vector2 currentPos2D_client_local = new Vector2(currentFullPosition.X, currentFullPosition.Z);
                        Vector2 toTargetVec2D_client = targetSlotPos.Value - currentPos2D_client_local;
                        if (toTargetVec2D_client.SqrMagnitude > 0.01f) 
                        {
                            Vector3 targetForwardPlanarVec_client = new Vector3(toTargetVec2D_client.X, 0f, toTargetVec2D_client.Y).Normalized;
                            if (targetForwardPlanarVec_client.SqrMagnitude > Vector3.Epsilon)
                            {
                                desiredPureYawRotation_client = Quaternion.LookRotation(targetForwardPlanarVec_client, Vector3.Up);
                            }
                        }
                    }

                    Quaternion nextPureYaw_client = Quaternion.RotateTowards(currentPureYawOrientation_client, desiredPureYawRotation_client, TurnRate * deltaTime);
                    Quaternion yawDeltaRotation_client = nextPureYaw_client * currentPureYawOrientation_client.Inverse; 
                    finalCombinedRotation = (yawDeltaRotation_client * currentFullRotation).Normalized; 


                    if (nextSpeed > 0f)
                    {
                        Vector3 movementPlanarForward = (nextPureYaw_client * Vector3.Forward); 
                        Vector3 planarVelocityDelta = movementPlanarForward * nextSpeed * deltaTime;
                        nextPosWithPreservedY = new Vector3(
                            currentFullPosition.X + planarVelocityDelta.X,
                            currentFullPosition.Y, 
                            currentFullPosition.Z + planarVelocityDelta.Z
                        );
                    }
                }
                return (nextPosWithPreservedY, finalCombinedRotation, nextSpeed, stillNeedsToMove);
            }


            public override void Update(float deltaTime)
            {
                if (deltaTime <= 0f || _isDestroyed) return;

                Vector3 currentSimPos = _simulatedPosition;
                Quaternion currentSimRot = _simulatedRotation; 
                float currentSimSpeed = _clientSimulatedSpeed;
                
                var planarSimResult = SimulatePlanarMovementStep(
                    currentSimPos, currentSimRot, currentSimSpeed,
                    _currentMovementTarget, deltaTime, _isMovingClientSide
                );

                currentSimPos = planarSimResult.newPos;
                currentSimRot = planarSimResult.newRot;
                currentSimSpeed = planarSimResult.newSpeed;
                _isMovingClientSide = planarSimResult.stillMoving;

                if (!_isMovingClientSide && _currentMovementTarget.HasValue) {
                    float distSq = (_currentMovementTarget.Value - new Vector2(currentSimPos.X, currentSimPos.Z)).SqrMagnitude;
                    if(distSq < (StoppingDistance + 0.3f) * (StoppingDistance + 0.3f)) 
                    {
                         _currentMovementTarget = null;
                    }
                }      
                
                if (OwningClientLevel.IsOceanInitialized)
                {
                    if (_clientFloatingBehavior == null) 
                    {
                        if (OwningClientLevel.OceanDataProvider != null)
                        {
                            _clientFloatingBehavior = new MultiPointFloatingBehavior(_clientShipFloatingPoints, OwningClientLevel.OceanDataProvider);
                            Logger.Log($"[ShipProxy.Client {EntityId}] Initialized MultiPointFloatingBehavior.");
                        }
                        else
                        {
                            Logger.LogWarning($"[ShipProxy.Client {EntityId}] Cannot initialize FloatingBehavior: OceanDataProvider is null in ClientLevel.");
                        }
                    }
                    
                    if (_clientFloatingBehavior != null)
                    {
                        _clientFloatingBehavior.ApplyFloating(
                            currentSimPos, currentSimRot, OwningClientLevel.CurrentTime, deltaTime, 
                            out Vector3 finalPosWithFloat, out Quaternion finalRotWithFloat
                        );
                        currentSimPos = finalPosWithFloat; 
                        currentSimRot = finalRotWithFloat; 
                    }
                }
                
                SetSimulatedPositionAndRotation(currentSimPos, currentSimRot);
                
                if (Math.Abs(_clientSimulatedSpeed - currentSimSpeed) > 0.01f)
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
                StatsChanged?.Invoke(); 
            }
        }
    }
}