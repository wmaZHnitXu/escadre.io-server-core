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
        CurrentDestinationUpdated = 202, 
        TargetEscadreEntityIdsUpdated = 203,
        FormationLayoutUpdated = 204 // New event for explicit formation updates
    }

    public static class EscadreProxy
    {
        public class ServerProxy : BaseServerProxy<Escadre>
        {
            private float _lastSentFleetSpeed = -1f;

            public ServerProxy(Escadre entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer)
            {
                // Model events related to checksummed properties (like Resources) are handled implicitly by checksum.
                // Events for non-checksummed or immediately-needed-on-client properties are handled explicitly.
                _entity.CurrentFleetSpeedChanged += HandleModelFleetSpeedChanged;
                _entity.OnFormationChanged += HandleModelFormationChanged; // Keep this subscription
                _entity.CurrentDestinationChangedEvent += HandleModelCurrentDestinationChanged; 
                _entity.TargetEscadreEntityIdsChangedEvent += HandleModelTargetEscadreEntityIdsChanged;
            }

            protected override float CalculateChecksum()
            {
                int baseHashAsInt = base.CalculateChecksum().GetHashCode();

                // Formation is now primarily event-driven for updates.
                // However, keeping it in checksum ensures consistency during initial sync or rare full corrections.
                int formationHash = 0;
                if (_entity.CurrentFormation != null && _entity.CurrentFormation.Slots != null)
                {
                    foreach (var slot in _entity.CurrentFormation.Slots.OrderBy(s => s.ShipEntityId ?? -1))
                    {
                        formationHash = HashCode.Combine(formationHash, slot.ShipEntityId, slot.RelativeOffset);
                    }
                }
                
                int combinedHash = baseHashAsInt;
                combinedHash = HashCode.Combine(combinedHash, _entity.OwnerClientId); // Owner Client ID
                combinedHash = HashCode.Combine(combinedHash, _entity.Nickname);
                combinedHash = HashCode.Combine(combinedHash, _entity.Resources);
                combinedHash = HashCode.Combine(combinedHash, formationHash); // Keep for full sync
                combinedHash = HashCode.Combine(combinedHash, _entity.FleetMaxSpeed);
                combinedHash = HashCode.Combine(combinedHash, _entity.FormationIntegrityFactor);
                
                return (float)combinedHash;
            }

            public override void SerializeSpecificInitialState(BinaryWriter writer)
            {
                writer.Write(_entity.OwnerClientId);
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                // Serialize Formation (Initial State)
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
                writer.Write(_entity.CurrentFleetSpeed); 
                writer.Write(_entity.FormationIntegrityFactor);
                _lastSentFleetSpeed = _entity.CurrentFleetSpeed;

                writer.Write(_entity.CurrentDestination.HasValue);
                if (_entity.CurrentDestination.HasValue)
                {
                    SerializationUtils.WriteVector2(writer, _entity.CurrentDestination.Value);
                }

                writer.Write(_entity.TargetEscadreEntityIds.Count);
                foreach (int targetId in _entity.TargetEscadreEntityIds)
                {
                    writer.Write(targetId);
                }
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                writer.Write(_entity.OwnerClientId); 
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                // Serialize Formation (for correction via checksum if an event was missed)
                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }

                writer.Write(_entity.FleetMaxSpeed);
                writer.Write(_entity.FormationIntegrityFactor);
            }

            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal();
                // _entity.OnResourcesChanged += HandleModelResourcesChanged; // This is checksummed
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Subscribed to relevant Escadre model events.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                // Unsubscribe from events specific to this proxy's direct handling if any were added
                // (CurrentFleetSpeedChanged, OnFormationChanged, etc. are handled by the constructor subscription)
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Unsubscribed from Escadre model events (or confirmed they are handled by base/constructor).");
            }

            // Handler for Escadre model's OnFormationChanged event
            private void HandleModelFormationChanged(Formation formation)
            {
                Logger.Log($"[EscadreProxy.Server {EntityId}] Formation changed. Sending FormationLayoutUpdated event with {formation.Slots.Count} slots.");
                SendEvent((byte)EscadreEventType.FormationLayoutUpdated, writer =>
                {
                    writer.Write(formation.Slots.Count);
                    foreach (var slot in formation.Slots)
                    {
                        writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                        SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                    }
                });
            }
            
            private void HandleModelFleetSpeedChanged(float newSpeed)
            {
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

            public void SendShopDesignsUpdate() 
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
            public event Action OnFleetParamsChanged; 
            public event Action<float> OnCurrentFleetSpeedChanged; 
            public event Action OnCurrentDestinationChanged;
            public event Action OnTargetEscadreEntityIdsChanged;


            public ClientProxy(int entityId, ClientLevel clientLevel)
                : base(entityId, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                DeserializeFormationSlots(reader); // Use helper

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

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Initialized. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FormationSlots: {FormationSlots.Count}, FleetSpd:{CurrentFleetSpeed}/{FleetMaxSpeed}, Dest: {CurrentDestination}, Targets: {TargetEscadreEntityIds.Count}");
                InvokeAllChangedEvents();
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                var oldOwner = OwnerClientId;
                var oldNickname = Nickname;
                var oldResources = Resources;
                var oldFleetMaxSpeed = FleetMaxSpeed;
                var oldFormationIntegrityFactor = FormationIntegrityFactor;

                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                // Formation is now primarily event-driven for updates.
                // However, if it's part of the checksummed UpdateState, we deserialize it here too.
                // The DeserializeFormationSlots helper will compare and only fire event if different.
                bool formationActuallyChangedInStateMsg = DeserializeFormationSlots(reader);

                FleetMaxSpeed = reader.ReadSingle();
                FormationIntegrityFactor = reader.ReadSingle();

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] State Updated (non-event part). Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FormationSlots: {FormationSlots.Count}, FleetMaxSpd:{FleetMaxSpeed}");

                if (OwnerClientId != oldOwner) Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] OwnerClientId changed from {oldOwner} to {OwnerClientId}, this is unusual."); // Should not happen
                if (Nickname != oldNickname) OnNicknameChanged?.Invoke();
                if (Resources != oldResources) OnResourcesChanged?.Invoke();
                if (formationActuallyChangedInStateMsg) OnFormationChanged?.Invoke(); // Only if state message caused change
                if (Math.Abs(FleetMaxSpeed - oldFleetMaxSpeed) > Core.Primitives.Vector3.Epsilon || Math.Abs(FormationIntegrityFactor - oldFormationIntegrityFactor) > Core.Primitives.Vector3.Epsilon) OnFleetParamsChanged?.Invoke();
            }
            
            // Helper to deserialize formation slots and return true if changed
            private bool DeserializeFormationSlots(BinaryReader reader)
            {
                int formationCount = reader.ReadInt32();
                bool formationStructureChanged = formationCount != FormationSlots.Count;
                var tempNewSlots = new List<FormationSlot>(formationCount);
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Core.Primitives.Vector2 offset = SerializationUtils.ReadVector2(reader);
                    var newSlot = new FormationSlot(offset, shipId == -1 ? (int?)null : shipId);
                    tempNewSlots.Add(newSlot);
                    if (!formationStructureChanged && i < FormationSlots.Count &&
                        (FormationSlots[i].ShipEntityId != newSlot.ShipEntityId || FormationSlots[i].RelativeOffset != newSlot.RelativeOffset))
                    {
                        formationStructureChanged = true;
                    }
                }

                if (formationStructureChanged)
                {
                    FormationSlots.Clear();
                    FormationSlots.AddRange(tempNewSlots);
                    return true;
                }
                return false;
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
                        case EscadreEventType.CurrentDestinationUpdated: 
                            bool hasDest = reader.ReadBoolean();
                            var oldDest = CurrentDestination;
                            CurrentDestination = hasDest ? SerializationUtils.ReadVector2(reader) : (Core.Primitives.Vector2?)null;
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: CurrentDestinationUpdated to {CurrentDestination}");
                            if (oldDest.HasValue != CurrentDestination.HasValue || (oldDest.HasValue && CurrentDestination.HasValue && oldDest.Value != CurrentDestination.Value))
                            {
                                OnCurrentDestinationChanged?.Invoke();
                            }
                            break;
                        case EscadreEventType.TargetEscadreEntityIdsUpdated: 
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
                        case EscadreEventType.FormationLayoutUpdated: // New event handler
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: FormationLayoutUpdated received.");
                            if (DeserializeFormationSlots(reader)) // Use helper, returns true if changed
                            {
                                OnFormationChanged?.Invoke();
                                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Formation updated via event. New slot count: {FormationSlots.Count}");
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
            }

            private void InvokeAllChangedEvents() 
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
                Core.Primitives.Quaternion averageRotationAccumulator = _simulatedRotation; 
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
                else 
                {
                    newSimulatedPosition = _simulatedPosition; 
                    newSimulatedRotation = _simulatedRotation; 
                }

                SetSimulatedPositionAndRotation(newSimulatedPosition, newSimulatedRotation);
            }
        }
    }
}