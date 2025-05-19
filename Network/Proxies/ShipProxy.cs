// File: Core/Network/Proxies/ShipProxy.cs
using System;
using System.IO;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Primitives;

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
                writer.Write(_entity.CurrentSpeed); // Server's current actual speed
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
                    SerializationUtils.WriteVector3(writer, _entity.Position);
                    SerializationUtils.WriteQuaternion(writer, _entity.Rotation);
                    writer.Write(serverTime);
                });
            }
        }

        public class ClientProxy : DestructibleEntityProxy.ClientProxy
        {
            public int OwningEscadreClientId { get; private set; }
            private float _clientSimulatedSpeed; // Internal state for simulation
            public float ClientSimulatedSpeed => _clientSimulatedSpeed; // Read-only public accessor
            public event Action<float> CurrentSpeedChanged;

            public float MaxSpeed { get; private set; } public float TurnRate { get; private set; }
            public float AttackDamage { get; private set; } public float AttackRange { get; private set; }
            public float AttackCooldown { get; private set; }
            public event Action StatsChanged;

            private Vector2? _currentMovementTarget;
            // No need for _serverAuthPositionAtCommand, _serverAuthRotationAtCommand, _serverTimeAtCommand here
            // as the HandleSetMovementTargetCommandPayload will directly snap the _simulatedPosition/_simulatedRotation

            private bool _isMovingClientSide = false; // Flag to indicate if client is simulating movement

            public ClientProxy(int entityId, Entity.EntityTypeEnum concreteType)
                : base(entityId, concreteType) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader) {
                base.DeserializeSpecificInitialState(reader); // Handles _simulatedPosition/Rotation from base
                OwningEscadreClientId = reader.ReadInt32();
                _clientSimulatedSpeed = reader.ReadSingle(); // Server sends initial speed
                MaxSpeed = reader.ReadSingle(); TurnRate = reader.ReadSingle();
                AttackDamage = reader.ReadSingle(); AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();

                CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed); // Invoke after setting
                StatsChanged?.Invoke();
            }

            protected override void DeserializeSpecificState(BinaryReader reader) { // For MessageType.UpdateState
                base.DeserializeSpecificState(reader); // Snaps _simulatedPosition/_simulatedRotation via base, handles Health
                
                float serverAuthoritativeSpeed = reader.ReadSingle(); // Server's current speed
                // We don't directly set _clientSimulatedSpeed to serverAuthoritativeSpeed here,
                // because _clientSimulatedSpeed is a result of *our* simulation.
                // However, if our simulation is off, this UpdateState (which includes pos/rot snap)
                // effectively corrects us. If the ship *should* be stopped due to server logic,
                // the server might also send a SetMovementTarget event with no target.

                var oldMaxSpeed = MaxSpeed;
                MaxSpeed = reader.ReadSingle(); TurnRate = reader.ReadSingle();
                AttackDamage = reader.ReadSingle(); AttackRange = reader.ReadSingle();
                AttackCooldown = reader.ReadSingle();
                if (Math.Abs(MaxSpeed - oldMaxSpeed) > float.Epsilon /* || other stats */) {
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
                    base.HandleSpecificEvent(specificEventType, reader); // Pass to DestructibleEntityProxy for its events
                }
            }

            private void HandleSetMovementTargetEventPayload(BinaryReader reader)
            {
                bool hasTarget = reader.ReadBoolean();
                _currentMovementTarget = hasTarget ? SerializationUtils.ReadVector2(reader) : (Vector2?)null;
                
                // Snap to server's provided state AT THE TIME OF THE COMMAND
                Vector3 serverPosAtCommand = SerializationUtils.ReadVector3(reader);
                Quaternion serverRotAtCommand = SerializationUtils.ReadQuaternion(reader);
                float serverTimeOfCommand = reader.ReadSingle(); // Store if needed for advanced prediction logic

                SetSimulatedPositionAndRotation(serverPosAtCommand, serverRotAtCommand); // Snap and invoke events
                
                _isMovingClientSide = hasTarget;

                if (!_isMovingClientSide) { // If command is to stop
                    if (Math.Abs(_clientSimulatedSpeed) > float.Epsilon) {
                        _clientSimulatedSpeed = 0f; CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                    }
                }
                // Logger.Log($"[ShipProxy.Client {EntityId}] Rcvd SetMovementTarget Event. Target: {_currentMovementTarget?.ToString() ?? "None"}. Snapped to server state: Pos={serverPosAtCommand}, Rot={serverRotAtCommand} @ ServerTime={serverTimeOfCommand}");
            }

            public override void Update(float clientSimulatedServerTime, float deltaTime)
            {
                // This method is called by ClientLevel.DoUpdate()
                base.Update(clientSimulatedServerTime, deltaTime); // Base currently does nothing
                if (deltaTime <= 0f || !_isMovingClientSide || !_currentMovementTarget.HasValue)
                {
                    // If not supposed to be moving client-side, ensure speed reflects that
                    if (Math.Abs(_clientSimulatedSpeed) > float.Epsilon && !_isMovingClientSide)
                    {
                        _clientSimulatedSpeed = 0f;
                        CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                    }
                    return;
                }

                // --- Client-Side Movement Simulation ---
                Vector3 currentSimPos = _simulatedPosition; // Use local copies for calculation
                Quaternion currentSimRot = _simulatedRotation;
                float currentSimSpeed = _clientSimulatedSpeed;

                Vector2 currentPos2D = new Vector2(currentSimPos.X, currentSimPos.Z);
                Vector2 targetPos2D = _currentMovementTarget.Value;
                Vector2 toTarget = targetPos2D - currentPos2D;

                if (toTarget.SqrMagnitude < 0.01f * 0.01f) { // Close enough to target
                    currentSimPos = new Vector3(targetPos2D.X, currentSimPos.Y, targetPos2D.Y);
                    _isMovingClientSide = false;
                    _currentMovementTarget = null;
                    currentSimSpeed = 0f;
                } else {
                    currentSimSpeed = this.MaxSpeed;
                    Vector2 directionToTarget = toTarget.Normalized;
                    Vector3 targetForward = new Vector3(directionToTarget.X, 0, directionToTarget.Y);

                    if (targetForward.SqrMagnitude > Vector3.Epsilon) {
                        Quaternion desiredRotation = Quaternion.LookRotation(targetForward, Vector3.Up);
                        currentSimRot = Quaternion.RotateTowards(currentSimRot, desiredRotation, this.TurnRate * deltaTime);
                    }
                    Vector3 velocity = currentSimRot * Vector3.Forward * currentSimSpeed * deltaTime;
                    currentSimPos += velocity;
                }

                // Update the authoritative simulated state and invoke events if changed
                SetSimulatedPositionAndRotation(currentSimPos, currentSimRot);
                if (Math.Abs(_clientSimulatedSpeed - currentSimSpeed) > float.Epsilon) {
                    _clientSimulatedSpeed = currentSimSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                }
            }

            protected override void CleanupEvents() {
                base.CleanupEvents(); CurrentSpeedChanged = null; StatsChanged = null;
            }
            protected override void InvokeSpecificStateChangedEvents() { // Called by base after UpdateState
                base.InvokeSpecificStateChangedEvents();
            }
        }
    }
}