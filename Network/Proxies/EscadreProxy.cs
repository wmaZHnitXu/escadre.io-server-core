// File: Core/Network/Proxies/EscadreProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;
using Core.Client;

namespace Core.Network.Proxies
{
    internal enum EscadreEventType : byte
    {
        ShopInfoUpdated = 200,
        FleetSpeedChanged = 201,
        CurrentDestinationUpdated = 202, // New
        TargetEscadreEntityIdsUpdated = 203 // New
    }

    public static class EscadreProxy
    {
        public class ServerProxy : BaseServerProxy<Escadre>
        {
            private float _lastSentFleetSpeed = -1f;

            public ServerProxy(Escadre entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer)
            {
                _entity.CurrentFleetSpeedChanged += HandleModelFleetSpeedChanged;
            }

            protected override float CalculateChecksum()
            {
                // Checksum is now only for properties NOT handled by dedicated events:
                // Nickname, Resources, Formation layout, FleetMaxSpeed, FormationIntegrityFactor
                // Position/Rotation are handled by BaseServerProxy checksum.
                // CurrentDestination and TargetEscadreEntityIds are event-driven.

                int baseHashAsInt = base.CalculateChecksum().GetHashCode();

                int formationHash = 0;
                if (_entity.CurrentFormation != null && _entity.CurrentFormation.Slots != null)
                {
                    foreach (var slot in _entity.CurrentFormation.Slots.OrderBy(s => s.ShipEntityId ?? -1))
                    {
                        formationHash = HashCode.Combine(formationHash, slot.ShipEntityId, slot.RelativeOffset);
                    }
                }
                
                int combinedHash = baseHashAsInt;
                combinedHash = HashCode.Combine(combinedHash, _entity.Nickname);
                combinedHash = HashCode.Combine(combinedHash, _entity.Resources);
                combinedHash = HashCode.Combine(combinedHash, formationHash);
                combinedHash = HashCode.Combine(combinedHash, _entity.FleetMaxSpeed);
                // CurrentFleetSpeed is event driven for frequent changes, but include in checksum for initial sync
                // and potential rare correction if an event is missed (though with TCP this is less likely).
                // However, per user's request to avoid checksum for things that change,
                // let's rely on FleetSpeedChanged event and initial state only.
                // combinedHash = HashCode.Combine(combinedHash, _entity.CurrentFleetSpeed);
                combinedHash = HashCode.Combine(combinedHash, _entity.FormationIntegrityFactor);
                
                return (float)combinedHash;
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                writer.Write(_entity.OwnerClientId);
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }

                var designs = _entity.Level.GameShop.AvailableShipDesigns;
                writer.Write(designs.Count);
                foreach (var design in designs)
                {
                    writer.Write(design.DesignId);
                    writer.Write(design.Name);
                    writer.Write(design.Cost);
                    writer.Write((byte)design.ShipEntityType);
                }

                writer.Write(_entity.FleetMaxSpeed);
                writer.Write(_entity.CurrentFleetSpeed); // Send initial current speed
                writer.Write(_entity.FormationIntegrityFactor);
                _lastSentFleetSpeed = _entity.CurrentFleetSpeed;

                // Serialize CurrentDestination (Initial State)
                writer.Write(_entity.CurrentDestination.HasValue);
                if (_entity.CurrentDestination.HasValue)
                {
                    SerializationUtils.WriteVector2(writer, _entity.CurrentDestination.Value);
                }

                // Serialize TargetEscadreEntityIds (Initial State)
                writer.Write(_entity.TargetEscadreEntityIds.Count);
                foreach (int targetId in _entity.TargetEscadreEntityIds)
                {
                    writer.Write(targetId);
                }
            }

            // This will now only serialize properties that are part of the simplified checksum
            // (i.e., not CurrentDestination, not TargetEscadreEntityIds, and arguably not CurrentFleetSpeed).
            // For now, let's make it match the checksummed properties.
            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                writer.Write(_entity.OwnerClientId); // Owner should not change, but for completeness if it was checksummed
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }
                writer.Write(_entity.FleetMaxSpeed);
                // writer.Write(_entity.CurrentFleetSpeed); // CurrentFleetSpeed is event-driven
                writer.Write(_entity.FormationIntegrityFactor);

                // CurrentDestination and TargetEscadreEntityIds are NOT part of correction state anymore.
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal();
                _entity.OnResourcesChanged += HandleModelResourcesChanged; // Example for checksummed property
                _entity.OnFormationChanged += HandleModelFormationChanged; // Example for checksummed property
                
                _entity.CurrentDestinationChangedEvent += HandleModelCurrentDestinationChanged; // New
                _entity.TargetEscadreEntityIdsChangedEvent += HandleModelTargetEscadreEntityIdsChanged; // New

                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Subscribed to Escadre model events.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnResourcesChanged -= HandleModelResourcesChanged;
                _entity.OnFormationChanged -= HandleModelFormationChanged;
                _entity.CurrentFleetSpeedChanged -= HandleModelFleetSpeedChanged; // This was already here

                _entity.CurrentDestinationChangedEvent -= HandleModelCurrentDestinationChanged; // New
                _entity.TargetEscadreEntityIdsChangedEvent -= HandleModelTargetEscadreEntityIdsChanged; // New

                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Unsubscribed from Escadre model events.");
            }

            private void HandleModelFleetSpeedChanged(float newSpeed)
            {
                // Send if significantly changed, or if it starts/stops
                if (Math.Abs(newSpeed - _lastSentFleetSpeed) > 0.05f || 
                    (newSpeed == 0 && _lastSentFleetSpeed != 0) || 
                    (newSpeed != 0 && _lastSentFleetSpeed == 0) ||
                    (Math.Abs(newSpeed - _entity.FleetMaxSpeed) < 0.01f && Math.Abs(_lastSentFleetSpeed - _entity.FleetMaxSpeed) > 0.01f) )
                {
                    _lastSentFleetSpeed = newSpeed;
                    SendEvent((byte)EscadreEventType.FleetSpeedChanged, writer =>
                    {
                        writer.Write(newSpeed);
                    });
                }
            }

            // These handlers for Nickname, Resources, Formation are for checksum-driven updates.
            // If these properties also need to be purely event-driven, they'd need their own network events.
            // For now, they contribute to the checksum, and if it mismatches, SerializeSpecificCorrectionState is sent.
            private void HandleModelResourcesChanged(int newAmount)
            {
                // Checksum will reflect this. No explicit event needed if using checksum for this.
                // If checksum is removed for these too, an event would be sent:
                // SendEvent((byte)EscadreEventType.ResourcesUpdated, writer => writer.Write(newAmount));
            }

            private void HandleModelFormationChanged(Formation formation)
            {
                // Checksum will reflect this.
            }
            
            private void HandleModelCurrentDestinationChanged(Vector2? newDestination)
            {
                Logger.Log($"[EscadreProxy.Server {EntityId}] CurrentDestination changed to {newDestination}. Sending event.");
                SendEvent((byte)EscadreEventType.CurrentDestinationUpdated, writer =>
                {
                    writer.Write(newDestination.HasValue);
                    if (newDestination.HasValue)
                    {
                        SerializationUtils.WriteVector2(writer, newDestination.Value);
                    }
                });
            }

            private void HandleModelTargetEscadreEntityIdsChanged(IReadOnlyCollection<int> newTargetIds)
            {
                Logger.Log($"[EscadreProxy.Server {EntityId}] TargetEscadreEntityIds changed. Count: {newTargetIds.Count}. Sending event.");
                SendEvent((byte)EscadreEventType.TargetEscadreEntityIdsUpdated, writer =>
                {
                    writer.Write(newTargetIds.Count);
                    foreach(int id in newTargetIds)
                    {
                        writer.Write(id);
                    }
                });
            }


            public void SendShopDesignsUpdate() // This is already an event
            {
                var designs = _entity.Level.GameShop.AvailableShipDesigns;
                Logger.Log($"[EscadreProxy.Server {EntityId}] Sending ShopInfoUpdated event. Count: {designs.Count}");
                SendEvent((byte)EscadreEventType.ShopInfoUpdated, writer =>
                {
                    writer.Write(designs.Count);
                    foreach (var design in designs)
                    {
                        writer.Write(design.DesignId);
                        writer.Write(design.Name);
                        writer.Write(design.Cost);
                        writer.Write((byte)design.ShipEntityType);
                    }
                });
            }
        }

        public class ClientProxy : BaseClientProxy
        {
            public override Entity.EntityTypeEnum EntityType => Entity.EntityTypeEnum.Escadre;

            public int OwnerClientId { get; private set; }
            public string Nickname { get; private set; }
            public int Resources { get; private set; }
            public List<FormationSlot> FormationSlots { get; } = new List<FormationSlot>();
            public List<ShipDesign> AvailableShopDesigns { get; } = new List<ShipDesign>();

            public float FleetMaxSpeed { get; private set; }
            public float CurrentFleetSpeed { get; private set; }
            public float FormationIntegrityFactor { get; private set; }

            public Core.Primitives.Vector2? CurrentDestination { get; private set; }
            private List<int> _targetEscadreEntityIdsList = new List<int>();
            public IReadOnlyCollection<int> TargetEscadreEntityIds => _targetEscadreEntityIdsList.AsReadOnly();


            public event Action OnNicknameChanged;
            public event Action OnResourcesChanged;
            public event Action OnFormationChanged;
            public event Action OnShopDesignsChanged;
            public event Action OnFleetParamsChanged; // For MaxSpeed, IntegrityFactor
            public event Action<float> OnCurrentFleetSpeedChanged; // Specifically for CurrentFleetSpeed
            public event Action OnCurrentDestinationChanged;
            public event Action OnTargetEscadreEntityIdsChanged;


            public ClientProxy(int entityId, ClientLevel clientLevel)
                : base(entityId, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                FormationSlots.Clear();
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Core.Primitives.Vector2 offset = SerializationUtils.ReadVector2(reader);
                    FormationSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId));
                }

                int designCount = reader.ReadInt32();
                AvailableShopDesigns.Clear();
                for (int i = 0; i < designCount; i++)
                {
                    int designId = reader.ReadInt32();
                    string name = reader.ReadString();
                    int cost = reader.ReadInt32();
                    Entity.EntityTypeEnum shipEntityType = (Entity.EntityTypeEnum)reader.ReadByte();
                    AvailableShopDesigns.Add(new ShipDesign(designId, name, cost, shipEntityType));
                }

                FleetMaxSpeed = reader.ReadSingle();
                CurrentFleetSpeed = reader.ReadSingle();
                FormationIntegrityFactor = reader.ReadSingle();

                bool hasDest = reader.ReadBoolean();
                CurrentDestination = hasDest ? SerializationUtils.ReadVector2(reader) : (Core.Primitives.Vector2?)null;

                int targetCount = reader.ReadInt32();
                _targetEscadreEntityIdsList.Clear();
                for (int i = 0; i < targetCount; i++)
                {
                    _targetEscadreEntityIdsList.Add(reader.ReadInt32());
                }

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Initialized. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FleetSpd:{CurrentFleetSpeed}/{FleetMaxSpeed}, Dest: {CurrentDestination}, Targets: {TargetEscadreEntityIds.Count}");
                InvokeAllChangedEvents(); // Fire all events on initial setup
            }

            // DeserializeSpecificState now only handles properties that are part of the simplified UpdateState message.
            // CurrentDestination and TargetEscadreEntityIds are updated by specific events.
            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                var oldOwner = OwnerClientId;
                var oldNickname = Nickname;
                var oldResources = Resources;
                var oldFleetMaxSpeed = FleetMaxSpeed;
                // CurrentFleetSpeed updated by event
                var oldFormationIntegrityFactor = FormationIntegrityFactor;
                // CurrentDestination updated by event
                // TargetEscadreEntityIds updated by event

                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                bool formationStructureChanged = formationCount != FormationSlots.Count;
                var tempNewSlots = new List<FormationSlot>();
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Core.Primitives.Vector2 offset = SerializationUtils.ReadVector2(reader);
                    tempNewSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId));
                    if (!formationStructureChanged && i < FormationSlots.Count &&
                        (FormationSlots[i].ShipEntityId != tempNewSlots[i].ShipEntityId || FormationSlots[i].RelativeOffset != tempNewSlots[i].RelativeOffset))
                    {
                        formationStructureChanged = true;
                    }
                }
                if (formationStructureChanged || FormationSlots.Count != tempNewSlots.Count) // Also check count change
                {
                    FormationSlots.Clear();
                    FormationSlots.AddRange(tempNewSlots);
                }

                FleetMaxSpeed = reader.ReadSingle();
                // CurrentFleetSpeed is NOT read here, it's event driven
                FormationIntegrityFactor = reader.ReadSingle();

                // CurrentDestination and TargetEscadreEntityIds are NOT read here

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] State Updated (non-event part). Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FleetMaxSpd:{FleetMaxSpeed}");

                if (OwnerClientId != oldOwner) Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] OwnerClientId changed from {oldOwner} to {OwnerClientId}, this is unusual.");
                if (Nickname != oldNickname) OnNicknameChanged?.Invoke();
                if (Resources != oldResources) OnResourcesChanged?.Invoke();
                if (formationStructureChanged) OnFormationChanged?.Invoke();
                if (Math.Abs(FleetMaxSpeed - oldFleetMaxSpeed) > Core.Primitives.Vector3.Epsilon || Math.Abs(FormationIntegrityFactor - oldFormationIntegrityFactor) > Core.Primitives.Vector3.Epsilon) OnFleetParamsChanged?.Invoke();
            }

            protected override void HandleSpecificEvent(byte specificEventType, BinaryReader reader)
            {
                if (Enum.IsDefined(typeof(EscadreEventType), specificEventType))
                {
                    EscadreEventType eventType = (EscadreEventType)specificEventType;
                    switch (eventType)
                    {
                        case EscadreEventType.ShopInfoUpdated:
                            int designCount = reader.ReadInt32();
                            AvailableShopDesigns.Clear();
                            for (int i = 0; i < designCount; i++)
                            {
                                int designId = reader.ReadInt32();
                                string name = reader.ReadString();
                                int cost = reader.ReadInt32();
                                Entity.EntityTypeEnum shipEntityType = (Entity.EntityTypeEnum)reader.ReadByte();
                                AvailableShopDesigns.Add(new ShipDesign(designId, name, cost, shipEntityType));
                            }
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Processed ShopInfoUpdated event. Count: {AvailableShopDesigns.Count}");
                            OnShopDesignsChanged?.Invoke();
                            break;
                        case EscadreEventType.FleetSpeedChanged:
                            var oldSpeed = CurrentFleetSpeed;
                            CurrentFleetSpeed = reader.ReadSingle();
                            if (Math.Abs(CurrentFleetSpeed - oldSpeed) > 0.01f || (oldSpeed == 0 && CurrentFleetSpeed != 0) || (oldSpeed !=0 && CurrentFleetSpeed == 0) )
                            {
                                OnCurrentFleetSpeedChanged?.Invoke(CurrentFleetSpeed);
                            }
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: FleetSpeedChanged to {CurrentFleetSpeed}");
                            break;
                        case EscadreEventType.CurrentDestinationUpdated: // New
                            bool hasDest = reader.ReadBoolean();
                            var oldDest = CurrentDestination;
                            CurrentDestination = hasDest ? SerializationUtils.ReadVector2(reader) : (Core.Primitives.Vector2?)null;
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: CurrentDestinationUpdated to {CurrentDestination}");
                            if (oldDest.HasValue != CurrentDestination.HasValue || (oldDest.HasValue && CurrentDestination.HasValue && oldDest.Value != CurrentDestination.Value))
                            {
                                OnCurrentDestinationChanged?.Invoke();
                            }
                            break;
                        case EscadreEventType.TargetEscadreEntityIdsUpdated: // New
                            int targetCount = reader.ReadInt32();
                            var oldTargetsSet = new HashSet<int>(_targetEscadreEntityIdsList);
                            _targetEscadreEntityIdsList.Clear();
                            for (int i = 0; i < targetCount; i++)
                            {
                                _targetEscadreEntityIdsList.Add(reader.ReadInt32());
                            }
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: TargetEscadreEntityIdsUpdated. Count: {_targetEscadreEntityIdsList.Count}, IDs: [{string.Join(",", _targetEscadreEntityIdsList)}]");
                            var newTargetsSet = new HashSet<int>(_targetEscadreEntityIdsList);
                            if (!oldTargetsSet.SetEquals(newTargetsSet))
                            {
                                OnTargetEscadreEntityIdsChanged?.Invoke();
                            }
                            break;
                        default:
                            Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] Unhandled EscadreEventType: {eventType}");
                            break;
                    }
                }
                else
                {
                    Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] Received unhandled specific event type byte: {specificEventType}. Proxy does not call base for this.");
                }
            }

            protected override void InvokeSpecificStateChangedEvents()
            {
                // Most events are invoked directly within DeserializeSpecificState or HandleSpecificEvent based on actual changes.
            }

            private void InvokeAllChangedEvents() // Call this after DeserializeSpecificInitialState
            {
                OnNicknameChanged?.Invoke();
                OnResourcesChanged?.Invoke();
                OnFormationChanged?.Invoke();
                OnShopDesignsChanged?.Invoke();
                OnFleetParamsChanged?.Invoke();
                OnCurrentFleetSpeedChanged?.Invoke(CurrentFleetSpeed);
                OnCurrentDestinationChanged?.Invoke();
                OnTargetEscadreEntityIdsChanged?.Invoke();
            }

            protected override void CleanupEvents()
            {
                base.CleanupEvents();
                OnNicknameChanged = null;
                OnResourcesChanged = null;
                OnFormationChanged = null;
                OnShopDesignsChanged = null;
                OnFleetParamsChanged = null;
                OnCurrentFleetSpeedChanged = null;
                OnCurrentDestinationChanged = null;
                OnTargetEscadreEntityIdsChanged = null;
            }

            public override void Update(float deltaTime)
            {
                if (OwningClientLevel == null || IsDestroyed) return;

                Core.Primitives.Vector3 sumPositions = Core.Primitives.Vector3.Zero;
                int visibleShipCount = 0;
                Core.Primitives.Quaternion averageRotationAccumulator = _simulatedRotation; // Start with current escadre rotation
                bool firstShip = true;

                foreach (var proxy in OwningClientLevel.ActiveProxies.Values)
                {
                    if (proxy is ShipProxy.ClientProxy shipProxy && !shipProxy.IsDestroyed && shipProxy.OwningEscadreClientId == this.OwnerClientId)
                    {
                        sumPositions += shipProxy.Position;
                        visibleShipCount++;
                        if (firstShip)
                        {
                            averageRotationAccumulator = shipProxy.Rotation;
                            firstShip = false;
                        }
                        else
                        {
                            // Iterative slerp might be heavy for many ships.
                            // A simpler approach: if escadre is moving, its rotation is towards target.
                            // If holding, it's average of ships or a default.
                            // For now, keeping iterative slerp.
                            averageRotationAccumulator = Core.Primitives.Quaternion.Slerp(averageRotationAccumulator, shipProxy.Rotation, 1.0f / visibleShipCount);
                        }
                    }
                }

                Core.Primitives.Vector3 newSimulatedPosition;
                Core.Primitives.Quaternion newSimulatedRotation;

                if (visibleShipCount > 0)
                {
                    newSimulatedPosition = sumPositions / visibleShipCount;
                    newSimulatedRotation = averageRotationAccumulator.Normalized;
                }
                else // No visible ships, maintain last known position/rotation or snap to anchor if available
                {
                    newSimulatedPosition = _simulatedPosition; // Hold last known average
                    newSimulatedRotation = _simulatedRotation; // Hold last known average
                }

                SetSimulatedPositionAndRotation(newSimulatedPosition, newSimulatedRotation);
            }
        }
    }
}