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
            private float _clientSimulatedSpeed; 
            public float ClientSimulatedSpeed => _clientSimulatedSpeed; 
            public event Action<float> CurrentSpeedChanged;

            public float MaxSpeed { get; private set; } public float TurnRate { get; private set; }
            public float AttackDamage { get; private set; } public float AttackRange { get; private set; }
            public float AttackCooldown { get; private set; }
            public event Action StatsChanged;

            private Vector2? _currentMovementTarget;
            private bool _isMovingClientSide = false; 

            // Constructor updated
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
                    base.HandleSpecificEvent(specificEventType, reader); 
                }
            }

            private void HandleSetMovementTargetEventPayload(BinaryReader reader)
            {
                bool hasTarget = reader.ReadBoolean();
                _currentMovementTarget = hasTarget ? SerializationUtils.ReadVector2(reader) : (Vector2?)null;
                
                Vector3 serverPosAtCommand = SerializationUtils.ReadVector3(reader);
                Quaternion serverRotAtCommand = SerializationUtils.ReadQuaternion(reader);
                float serverTimeOfCommand = reader.ReadSingle(); 

                SetSimulatedPositionAndRotation(serverPosAtCommand, serverRotAtCommand); 
                
                _isMovingClientSide = hasTarget;

                if (!_isMovingClientSide) { 
                    if (Math.Abs(_clientSimulatedSpeed) > float.Epsilon) {
                        _clientSimulatedSpeed = 0f; CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                    }
                }
                // Can use OwningClientLevel.CurrentTime here if needed to compare against serverTimeOfCommand for advanced prediction.
                // Logger.Log($"[ShipProxy.Client {EntityId}] Rcvd SetMovementTarget Event. Target: {_currentMovementTarget?.ToString() ?? "None"}. Snapped to server state: Pos={serverPosAtCommand}, Rot={serverRotAtCommand} @ ServerTime={serverTimeOfCommand}. ClientTime: {OwningClientLevel.CurrentTime}");
            }

            // Update signature changed
            public override void Update(float deltaTime)
            {
                base.Update(deltaTime); 
                if (deltaTime <= 0f || !_isMovingClientSide || !_currentMovementTarget.HasValue)
                {
                    if (Math.Abs(_clientSimulatedSpeed) > float.Epsilon && !_isMovingClientSide)
                    {
                        _clientSimulatedSpeed = 0f;
                        CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
                    }
                    return;
                }

                Vector3 currentSimPos = _simulatedPosition; 
                Quaternion currentSimRot = _simulatedRotation;
                float currentSimSpeed = _clientSimulatedSpeed;

                Vector2 currentPos2D = new Vector2(currentSimPos.X, currentSimPos.Z);
                Vector2 targetPos2D = _currentMovementTarget.Value;
                Vector2 toTarget = targetPos2D - currentPos2D;

                if (toTarget.SqrMagnitude < 0.01f * 0.01f) { 
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

                SetSimulatedPositionAndRotation(currentSimPos, currentSimRot);
                if (Math.Abs(_clientSimulatedSpeed - currentSimSpeed) > float.Epsilon) {
                    _clientSimulatedSpeed = currentSimSpeed;
                    CurrentSpeedChanged?.Invoke(_clientSimulatedSpeed);
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