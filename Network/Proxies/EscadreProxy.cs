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
        ShopInfoUpdated = 200
    }

    public static class EscadreProxy
    {
        public class ServerProxy : BaseServerProxy<Escadre> 
        {
            public ServerProxy(Escadre entity, IServerNetworkLayer networkLayer)
                : base(entity, networkLayer)
            {
            }

            protected override float CalculateChecksum()
            {
                // Position is now dynamic (average of ships), so it changes frequently.
                // Rotation is also dynamic.
                // Nickname, Resources, and Formation structure are the primary state items for checksum
                // beyond the base (which includes Pos/Rot).
                int baseHash = base.CalculateChecksum().GetHashCode(); // Includes Pos, Rot
                int formationHash = 0;
                foreach(var slot in _entity.CurrentFormation.Slots)
                {
                    formationHash = HashCode.Combine(formationHash, slot.ShipEntityId, slot.RelativeOffset);
                }
                return HashCode.Combine(baseHash, _entity.Nickname, _entity.Resources, formationHash);
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
            }

            protected override void SerializeSpecificCorrectionState(BinaryWriter writer)
            {
                // Position and Rotation are handled by BaseServerProxy.SerializeCorrectionState
                writer.Write(_entity.OwnerClientId); 
                writer.Write(_entity.Nickname ?? string.Empty);
                writer.Write(_entity.Resources);

                writer.Write(_entity.CurrentFormation.Slots.Count);
                foreach (var slot in _entity.CurrentFormation.Slots)
                {
                    writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1);
                    SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                }
            }
            
            protected override void StartReplicatingInternal()
            {
                base.StartReplicatingInternal(); 
                _entity.OnResourcesChanged += HandleModelResourcesChanged;
                _entity.OnFormationChanged += HandleModelFormationChanged;
                // Assuming Nickname doesn't change or if it does, it would also trigger checksum change.
                // If Nickname has its own event: _entity.OnNicknameChanged += HandleModelNicknameChanged;
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Subscribed to Escadre model events.");
            }

            protected override void StopReplicatingInternal()
            {
                base.StopReplicatingInternal();
                _entity.OnResourcesChanged -= HandleModelResourcesChanged;
                _entity.OnFormationChanged -= HandleModelFormationChanged;
                // If Nickname has its own event: _entity.OnNicknameChanged -= HandleModelNicknameChanged;
                Logger.Log($"[EscadreProxy.Server EntityId:{EntityId}] Unsubscribed from Escadre model events.");
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

            public event Action OnNicknameChanged;
            public event Action OnResourcesChanged;
            public event Action OnFormationChanged;
            public event Action OnShopDesignsChanged;


            public ClientProxy(int entityId, ClientLevel clientLevel)
                : base(entityId, clientLevel) { }

            protected override void DeserializeSpecificInitialState(BinaryReader reader)
            {
                // BaseClientProxy.Initialize handles Position, Rotation from the CreateEntity message
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
                
                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] Initialized. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, Slots:{FormationSlots.Count}, ShopDesigns:{AvailableShopDesigns.Count}");
                InvokeAllChangedEvents(); 
            }

            protected override void DeserializeSpecificState(BinaryReader reader)
            {
                // BaseClientProxy.DeserializeAndUpdateState handles Position, Rotation updates from UpdateState message
                var oldOwner = OwnerClientId; 
                var oldNickname = Nickname;
                var oldResources = Resources;
                
                OwnerClientId = reader.ReadInt32();
                Nickname = reader.ReadString();
                Resources = reader.ReadInt32();

                int formationCount = reader.ReadInt32();
                bool formationStructureChanged = formationCount != FormationSlots.Count; // Basic check
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

                Logger.Log($"[EscadreProxy.Client EntityId:{EntityId}] State Updated. Owner:{OwnerClientId}, Nick:{Nickname}, Res:{Resources}, Slots:{FormationSlots.Count}");

                if(OwnerClientId != oldOwner) Logger.LogWarning($"[EscadreProxy.Client EntityId:{EntityId}] OwnerClientId changed from {oldOwner} to {OwnerClientId}, this is unusual.");
                if(Nickname != oldNickname) OnNicknameChanged?.Invoke();
                if(Resources != oldResources) OnResourcesChanged?.Invoke();
                if(formationStructureChanged) OnFormationChanged?.Invoke(); 
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
            }
            
            protected override void CleanupEvents()
            {
                base.CleanupEvents(); 
                OnNicknameChanged = null;
                OnResourcesChanged = null;
                OnFormationChanged = null;
                OnShopDesignsChanged = null;
            }

            public override void Update(float deltaTime)
            {
                // BaseClientProxy.Update does not exist or does nothing by default.
                // This proxy now calculates its position based on its visible ships.

                if (OwningClientLevel == null) return;

                Vector3 sumPositions = Vector3.Zero;
                int visibleShipCount = 0;
                Quaternion averageRotationAccumulator = Quaternion.Identity; // For averaging rotation, simple approach
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
                            // Simplistic rotation averaging: Slerp towards the current ship's rotation.
                            // A more robust method might average quaternion components or use a more sophisticated algorithm.
                            averageRotationAccumulator = Quaternion.Slerp(averageRotationAccumulator, shipProxy.Rotation, 1.0f / visibleShipCount);
                        }
                    }
                }

                Vector3 newSimulatedPosition;
                Quaternion newSimulatedRotation;

                if (visibleShipCount > 0)
                {
                    newSimulatedPosition = sumPositions / visibleShipCount;
                    newSimulatedRotation = averageRotationAccumulator.Normalized; // Ensure it's normalized
                }
                else
                {
                    // If no ships are visible, keep the last known server position,
                    // or an interpolated position if it was moving.
                    // For simplicity, we'll just hold the last known position.
                    // The _simulatedPosition from BaseClientProxy is already the last server update or interpolated.
                    newSimulatedPosition = _simulatedPosition; 
                    newSimulatedRotation = _simulatedRotation;
                }
                
                // Update the base class's simulated position and rotation
                // This will trigger PositionChanged/RotationChanged events if they differ.
                SetSimulatedPositionAndRotation(newSimulatedPosition, newSimulatedRotation);
            }
        }
    }
}