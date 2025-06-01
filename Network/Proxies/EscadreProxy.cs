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
        FleetSpeedChanged = 201 
    }

    public static class EscadreProxy
    {
        public class ServerProxy : BaseServerProxy<Escadre> 
        {
            private float _lastSentFleetSpeed = -1f; 

            public ServerProxy(Escadre entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer)
            {
                // Subscribe to the new CurrentFleetSpeedChanged event from the Escadre model
                entity.CurrentFleetSpeedChanged += HandleModelFleetSpeedChanged;
            }

            protected override float CalculateChecksum()
            {
                int baseHash = base.CalculateChecksum().GetHashCode(); 
                int formationHash = 0;
                foreach(var slot in _entity.CurrentFormation.Slots)
                {
                    formationHash = HashCode.Combine(formationHash, slot.ShipEntityId, slot.RelativeOffset);
                }
                // CurrentFleetSpeed is now handled by an event, so not in checksum for frequent corrections.
                return HashCode.Combine(baseHash, 
                                        _entity.Nickname, 
                                        _entity.Resources, 
                                        formationHash,
                                        _entity.FleetMaxSpeed.GetHashCode(),
                                        _entity.FormationIntegrityFactor.GetHashCode());
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
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
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
                writer.Write(_entity.FleetMaxSpeed);
                writer.Write(_entity.CurrentFleetSpeed); // Also send in correction for robustness
                _lastSentFleetSpeed = _entity.CurrentFleetSpeed;
                writer.Write(_entity.FormationIntegrityFactor);
            }
            
            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal(); 
                _entity.OnResourcesChanged += HandleModelResourcesChanged;
                _entity.OnFormationChanged += HandleModelFormationChanged;
                // Escadre.CurrentFleetSpeedChanged subscription moved to constructor/StopReplicatingInternal
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Subscribed to Escadre model events.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnResourcesChanged -= HandleModelResourcesChanged;
                _entity.OnFormationChanged -= HandleModelFormationChanged;
                _entity.CurrentFleetSpeedChanged -= HandleModelFleetSpeedChanged;
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Unsubscribed from Escadre model events.");
            }
            
            private void HandleModelFleetSpeedChanged(float newSpeed)
            {
                // Only send if changed significantly enough to warrant an event, or always send.
                // The event from the model implies it's a notable change.
                if (Math.Abs(newSpeed - _lastSentFleetSpeed) > 0.05f || (newSpeed == 0 && _lastSentFleetSpeed !=0) || (newSpeed == _entity.FleetMaxSpeed && _lastSentFleetSpeed != _entity.FleetMaxSpeed) )
                {
                    _lastSentFleetSpeed = newSpeed;
                    SendEvent((byte)EscadreEventType.FleetSpeedChanged, writer => 
                    {
                        writer.Write(newSpeed);
                    });
                }
            }


            private void HandleModelResourcesChanged(int newAmount)
            {
                 Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Resources changed. Checksum will reflect this for next client sync.");
            }

            private void HandleModelFormationChanged(Formation formation)
            {
                 Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Formation changed. Checksum will reflect this for next client sync.");
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


            public event Action OnNicknameChanged;
            public event Action OnResourcesChanged;
            public event Action OnFormationChanged;
            public event Action OnShopDesignsChanged;
            public event Action OnFleetParamsChanged; 
            public event Action<float> OnCurrentFleetSpeedChanged;


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
                    Vector2 offset = SerializationUtils.ReadVector2(reader);
                    FormationSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId ));
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

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Initialized. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FleetSpd:{CurrentFleetSpeed}/{FleetMaxSpeed}");
                InvokeAllChangedEvents(); 
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                var oldOwner = OwnerClientId; 
                var oldNickname = Nickname;
                var oldResources = Resources;
                var oldFleetMaxSpeed = FleetMaxSpeed;
                var oldCurrentFleetSpeed = CurrentFleetSpeed; // Will be updated by event too
                var oldFormationIntegrityFactor = FormationIntegrityFactor;
                
                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                bool formationStructureChanged = formationCount != FormationSlots.Count; 
                var tempNewSlots = new List<FormationSlot>();
                for (int i = 0; i < formationCount; i++)
                {
                    int shipId = reader.ReadInt32();
                    Vector2 offset = SerializationUtils.ReadVector2(reader);
                    tempNewSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId ));
                    if (!formationStructureChanged && i < FormationSlots.Count &&
                        (FormationSlots[i].ShipEntityId != tempNewSlots[i].ShipEntityId || FormationSlots[i].RelativeOffset != tempNewSlots[i].RelativeOffset))
                    {
                        formationStructureChanged = true;
                    }
                }
                if (formationStructureChanged)
                {
                    FormationSlots.Clear();
                    FormationSlots.AddRange(tempNewSlots);
                }

                FleetMaxSpeed = reader.ReadSingle();
                float serverCurrentFleetSpeed = reader.ReadSingle(); // From correction state
                FormationIntegrityFactor = reader.ReadSingle();

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] State Updated. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, FleetSpd:{serverCurrentFleetSpeed}/{FleetMaxSpeed}");

                if(OwnerClientId != oldOwner) Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] OwnerClientId changed from {oldOwner} to {OwnerClientId}, this is unusual.");
                if(Nickname != oldNickname) OnNicknameChanged?.Invoke();
                if(Resources != oldResources) OnResourcesChanged?.Invoke();
                if(formationStructureChanged) OnFormationChanged?.Invoke(); 
                if(Math.Abs(FleetMaxSpeed - oldFleetMaxSpeed) > float.Epsilon || Math.Abs(FormationIntegrityFactor - oldFormationIntegrityFactor) > float.Epsilon) OnFleetParamsChanged?.Invoke();
                
                // If correction state provides a speed different from event-driven one, update and notify
                if(Math.Abs(CurrentFleetSpeed - serverCurrentFleetSpeed) > float.Epsilon) 
                {
                    CurrentFleetSpeed = serverCurrentFleetSpeed;
                    OnCurrentFleetSpeedChanged?.Invoke(CurrentFleetSpeed);
                }
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
                            if(Math.Abs(CurrentFleetSpeed - oldSpeed) > 0.01f) // Use a small epsilon for float comparison
                            {
                                OnCurrentFleetSpeedChanged?.Invoke(CurrentFleetSpeed);
                            }
                            Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Event: FleetSpeedChanged to {CurrentFleetSpeed}");
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
                // Events are invoked within DeserializeSpecificState based on actual changes.
            }

            private void InvokeAllChangedEvents()
            {
                OnNicknameChanged?.Invoke();
                OnResourcesChanged?.Invoke();
                OnFormationChanged?.Invoke();
                OnShopDesignsChanged?.Invoke();
                OnFleetParamsChanged?.Invoke();
                OnCurrentFleetSpeedChanged?.Invoke(CurrentFleetSpeed);
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
            }

            public override void Update(float deltaTime)
            {
                if (OwningClientLevel == null) return;

                Vector3 sumPositions = Vector3.Zero;
                int visibleShipCount = 0;
                Quaternion averageRotationAccumulator = _simulatedRotation; 
                bool firstShip = true;

                foreach (var proxy in OwningClientLevel.ActiveProxies.Values)
                {
                    if (proxy is ShipProxy.ClientProxy shipProxy && shipProxy.OwningEscadreClientId == this.OwnerClientId)
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
                            averageRotationAccumulator = Quaternion.Slerp(averageRotationAccumulator, shipProxy.Rotation, 1.0f / visibleShipCount);
                        }
                    }
                }

                Vector3 newSimulatedPosition;
                Quaternion newSimulatedRotation;

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