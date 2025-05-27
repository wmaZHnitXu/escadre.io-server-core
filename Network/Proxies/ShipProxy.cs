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

            // Define floating points for client-side ship visuals
            // These should match the conceptual points of the DefaultShip model on the server.
            private readonly List<Vector3> _clientShipFloatingPoints = new List<Vector3>
            {
                new Vector3(0f, 0f, 2.0f),   // Bow
                new Vector3(0f, 0f, -2.0f),  // Stern
                new Vector3(0.5f, 0f, 0f),   // Starboard mid
                new Vector3(-0.5f, 0f, 0f)   // Port mid
            };
            // _clientFloatingBehavior is inherited from BaseClientProxy (IFloatingBehavior _clientFloatingBehavior)
            // It will be initialized in Update() if ocean is available.

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType, ClientLevel clientLevel)
                : base(entityId, concreteType, clientLevel)
            {
                // _clientFloatingBehavior (from BaseClientProxy) will be lazy-initialized in Update()
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
                
                float serverAuthoritativeSpeed = reader.ReadSingle(); // This was from an older version, CurrentSpeed is now part of correction state
                                                                    // but not directly used here for setting _clientSimulatedSpeed, as it's simulated.

                var oldMaxSpeed = MaxSpeed;
                var oldTurnRate = TurnRate; 
                MaxSpeed = reader.ReadSingle(); 
                TurnRate = reader.ReadSingle();
                
                // If CurrentSpeed was part of the UpdateState payload and we wanted to snap to it:
                // if (Math.Abs(_clientSimulatedSpeed - serverAuthoritativeSpeed) > float.Epsilon)
                // {
                //     _clientSimulatedSpeed = serverAuthoritativeSpeed;
                //     CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                // }
                // For now, CurrentSpeed is part of specific correction state, but client mostly simulates its own speed.
                // The server's CurrentSpeed might be useful if client needs to exactly match server for some reason beyond position/rotation updates.

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
                            // If DestructibleEntityProxy's base.HandleSpecificEvent is needed for some reason, call it.
                            // But BaseClientProxy.HandleSpecificEvent is abstract.
                            // DestructibleEntityProxy itself might handle some events.
                            // For now, if it's not a ShipEventType, it's an error or unhandled for this proxy.
                            break;
                    }
                }
                else {
                    // If the event is not a ShipEventType, it might be for DestructibleEntityProxy.
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
                    // SimulatePlanarMovementStep expects the current Y to be passed for preservation
                    // and only modifies XZ and Yaw.
                    var catchUpResult = SimulatePlanarMovementStep(
                        serverPosAtCommand, // serverPosAtCommand already includes ocean-affected Y from server
                        serverRotAtCommand, // serverRotAtCommand includes ocean-affected pitch/roll from server
                        serverSpeedAtCommand,
                        _currentMovementTarget, MaxSpeed, TurnRate, catchUpDeltaTime, true 
                    );
                    predictedPos = catchUpResult.newPos; 
                    predictedRot = catchUpResult.newRot; 
                    predictedSpeed = catchUpResult.newSpeed;
                    stillMovingAfterCatchUp = catchUpResult.stillMoving;
                }

                // SetSimulatedPositionAndRotation will update _simulatedPosition and _simulatedRotation
                // These values will then be used as the input for the next frame's Update() which includes floating.
                SetSimulatedPositionAndRotation(predictedPos, predictedRot);
                if (Math.Abs(_clientSimulatedSpeed - predictedSpeed) > float.Epsilon)
                {
                    _clientSimulatedSpeed = predictedSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
                
                _isMovingClientSide = stillMovingAfterCatchUp && hasTarget; 
                if (!_isMovingClientSide) _currentMovementTarget = null;
            }
            
            // Simulates XZ movement and Yaw only. Preserves input Y.
            private (Vector3 newPos, Quaternion newRot, float newSpeed, bool stillMoving) SimulatePlanarMovementStep(
                Vector3 currentFullPosition, Quaternion currentFullRotation, float currentSpeedParam,
                Vector2? target, float currentMaxSpeed, float currentTurnRate, float deltaTime, bool hasExternalMoveOrder)
            {
                // --- Yaw Component ---
                // Extract current planar yaw from the full rotation (which might include ocean pitch/roll)
                Vector3 currentWorldForwardFull = currentFullRotation * Vector3.Forward;
                Vector3 currentPlanarForwardVec = new Vector3(currentWorldForwardFull.X, 0.0f, currentWorldForwardFull.Z).NormalizedSafe(Vector3.Forward);
                Quaternion currentPureYawOrientation = Quaternion.LookRotation(currentPlanarForwardVec, Vector3.Up);
                Quaternion nextPureYaw = currentPureYawOrientation;

                // --- XZ Position Component ---
                Vector3 nextPosWithPreservedY = currentFullPosition; // Start with current full pos to preserve Y

                float nextSpeed = currentSpeedParam;
                bool stillNeedsToMove = hasExternalMoveOrder;

                if (!hasExternalMoveOrder || !target.HasValue)
                {
                    if (nextSpeed > 0) nextSpeed = Math.Max(0, nextSpeed - (currentMaxSpeed * 2f * deltaTime)); 
                    else nextSpeed = 0f;
                    stillNeedsToMove = false;
                }
                else
                {
                    Vector2 currentPos2D = new Vector2(currentFullPosition.X, currentFullPosition.Z);
                    Vector2 targetPos2D = target.Value;
                    Vector2 toTarget = targetPos2D - currentPos2D;
                    float distanceToTargetSq = toTarget.SqrMagnitude;
                    
                    float stoppingDistance = currentMaxSpeed * deltaTime * 0.75f; 
                    stoppingDistance = Math.Max(0.01f, stoppingDistance); 
                    float stoppingDistanceSq = stoppingDistance * stoppingDistance;

                    if (distanceToTargetSq < stoppingDistanceSq)
                    {
                        nextPosWithPreservedY = new Vector3(targetPos2D.X, currentFullPosition.Y, targetPos2D.Y); 
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
                            nextPureYaw = Quaternion.RotateTowards(currentPureYawOrientation, desiredYawRotation, currentTurnRate * deltaTime);
                        }
                        Vector3 velocity = nextPureYaw * Vector3.Forward * nextSpeed * deltaTime;
                        nextPosWithPreservedY = new Vector3(
                            currentFullPosition.X + velocity.X, 
                            currentFullPosition.Y, // Preserve original Y
                            currentFullPosition.Z + velocity.Z
                        );
                    }
                }
                
                // --- Combine Yaw with Original Pitch/Roll ---
                // Calculate the change in pure yaw
                Quaternion yawChange = nextPureYaw * currentPureYawOrientation.Inverse;
                // Apply this yaw change to the original full rotation (that includes ocean effects)
                Quaternion finalCombinedRotation = (yawChange * currentFullRotation).Normalized;

                return (nextPosWithPreservedY, finalCombinedRotation, nextSpeed, stillNeedsToMove);
            }


            public override void Update(float deltaTime)
            {
                if (deltaTime <= 0f || _isDestroyed) return;

                // Current simulated state before this frame's logic
                Vector3 currentSimPos = _simulatedPosition;
                Quaternion currentSimRot = _simulatedRotation; 
                float currentSimSpeed = _clientSimulatedSpeed;
                bool isCurrentlyMovingPlanar = _isMovingClientSide;

                // 1. Simulate Planar (XZ) Movement and Yaw based on movement target
                // This step updates XZ position and applies commanded Yaw while preserving existing Pitch/Roll.
                if (!isCurrentlyMovingPlanar || !_currentMovementTarget.HasValue)
                {
                    if (currentSimSpeed > 0)
                    {
                        currentSimSpeed = Math.Max(0, currentSimSpeed - (MaxSpeed * 2f * deltaTime));
                    }
                    else { currentSimSpeed = 0f; }
                    isCurrentlyMovingPlanar = false;
                    // No change to currentSimPos.X/Z or currentSimRot's yaw component if not moving.
                    // Y, Pitch, Roll will be handled by floating behavior next.
                }
                else 
                {
                    var planarSimResult = SimulatePlanarMovementStep(
                        currentSimPos,      
                        currentSimRot,      
                        currentSimSpeed,    
                        _currentMovementTarget, 
                        MaxSpeed, 
                        TurnRate, 
                        deltaTime, 
                        isCurrentlyMovingPlanar
                    );

                    currentSimPos = planarSimResult.newPos; // XZ updated, Y preserved from input
                    currentSimRot = planarSimResult.newRot; // Yaw updated, Pitch/Roll preserved from input
                    currentSimSpeed = planarSimResult.newSpeed;
                    isCurrentlyMovingPlanar = planarSimResult.stillMoving;
                }
                
                _isMovingClientSide = isCurrentlyMovingPlanar;
                if (!_isMovingClientSide) {
                     _currentMovementTarget = null; 
                }      

                // At this point:
                // currentSimPos has updated XZ from planar movement, and Y from previous frame's floating.
                // currentSimRot has updated Yaw from planar movement, and Pitch/Roll from previous frame's floating.
                // currentSimSpeed is updated.

                // 2. Apply Floating Behavior 
                // This will adjust Y, Pitch, and Roll based on the ocean, using the results from planar sim as input.
                if (OwningClientLevel.IsOceanInitialized)
                {
                    if (_clientFloatingBehavior == null) // Lazy initialization
                    {
                        // Ensure IOceanDataProvider is available
                        if (OwningClientLevel.OceanDataProvider != null)
                        {
                            _clientFloatingBehavior = new MultiPointFloatingBehavior(_clientShipFloatingPoints, OwningClientLevel.OceanDataProvider);
                            // Configure floating parameters if needed (e.g., less aggressive than server for smoother visuals)
                            // _clientFloatingBehavior.VerticalInterpolationSpeed = 1.5f; 
                            // _clientFloatingBehavior.RotationalInterpolationSpeed = 40.0f;
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
                            currentSimPos, 
                            currentSimRot, 
                            OwningClientLevel.CurrentTime, 
                            deltaTime, 
                            out Vector3 finalPosWithFloat, 
                            out Quaternion finalRotWithFloat
                        );
                        
                        currentSimPos = finalPosWithFloat; 
                        currentSimRot = finalRotWithFloat; 
                    }
                }
                // If ocean is not initialized or behavior not set, currentSimPos and currentSimRot remain as after planar sim.
                
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
                // If MaxSpeed or TurnRate had their own events, they'd be invoked here after deserialization.
                // StatsChanged event already covers them.
            }
        }
    }
}