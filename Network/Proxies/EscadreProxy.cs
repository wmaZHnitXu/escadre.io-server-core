// File: Core/Network/Proxies/EscadreProxy.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Model;
using Core.Primitives;
using Core.Network;
using Core.Logging;
using Core.Client; // For ClientLevel

namespace Core.Network.Proxies
{
    // Holds client-side representation of Escadre data (Resources, Formation)
    // and available shop designs.
    public class ClientEscadreState
    {
        public int OwnerClientId { get; }
        public int Resources { get; internal set; }
        public List<FormationSlot> FormationSlots { get; internal set; } = new List<FormationSlot>();
        public List<ShipDesign> AvailableShopDesigns { get; internal set; } = new List<ShipDesign>();

        public event Action OnResourcesChanged;
        public event Action OnFormationChanged;
        public event Action OnShopDesignsChanged;

        public ClientEscadreState(int ownerClientId)
        {
            OwnerClientId = ownerClientId;
        }

        internal void TriggerResourcesChanged() => OnResourcesChanged?.Invoke();
        internal void TriggerFormationChanged() => OnFormationChanged?.Invoke();
        internal void TriggerShopDesignsChanged() => OnShopDesignsChanged?.Invoke();
    }


    public static class EscadreProxy
    {
        public class ServerProxy : IServerStateProxy
        {
            public int OwnerId => _escadre.OwnerClientId;
            private readonly Escadre _escadre;
            private readonly IServerNetworkLayer _networkLayer;
            private readonly Level _level; 

            public ServerProxy(Escadre escadre, Level level, IServerNetworkLayer networkLayer)
            {
                _escadre = escadre ?? throw new ArgumentNullException(nameof(escadre));
                _level = level ?? throw new ArgumentNullException(nameof(level));
                _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            }

            public void StartReplicatingToOwner()
            {
                Logger.Log($"[EscadreProxy.Server {OwnerId}] StartReplicatingToOwner called.");
                _escadre.OnResourcesChanged += HandleResourcesChanged;
                _escadre.OnFormationChanged += HandleFormationChanged;
                
                // Send initial state immediately after subscribing.
                // This ensures the client gets a snapshot even if no events have fired yet for this session.
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Sending initial full escadre info and shop designs.");
                SendFullEscadreInfo(); // Captures current resources and formation
                SendShopDesigns();     // Captures current shop designs
                
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Finished initial state send. Now listening for events.");
            }

            public void StopReplicating()
            {
                _escadre.OnResourcesChanged -= HandleResourcesChanged;
                _escadre.OnFormationChanged -= HandleFormationChanged;
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Stopped replicating.");
            }

            private void HandleResourcesChanged(int newAmount)
            {
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Resources changed to {newAmount}. Sending update.");
                // Could send only resources, but SendFull is simpler for now and includes it.
                SendFullEscadreInfo(); 
            }

            private void HandleFormationChanged(Formation formation)
            {
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Formation changed. Sending update. Slot count: {formation.Slots.Count}");
                SendFullEscadreInfo();
            }

            public void SendFullEscadreInfo()
            {
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Preparing to send UpdateEscadreInfo. Res: {_escadre.Resources}, FormationSlots: {_escadre.CurrentFormation.Slots.Count}");
                _networkLayer.SendToClient(OwnerId, OwnerId, MessageType.UpdateEscadreInfo, writer =>
                {
                    writer.Write(_escadre.Resources);
                    writer.Write(_escadre.CurrentFormation.Slots.Count);
                    foreach (var slot in _escadre.CurrentFormation.Slots)
                    {
                        // ShipEntityId should not be null if it's in _escadre.CurrentFormation.Slots from Escadre.AddShip
                        writer.Write(slot.ShipEntityId.HasValue ? slot.ShipEntityId.Value : -1); // Defensive -1 for truly empty slot if design changes
                        SerializationUtils.WriteVector2(writer, slot.RelativeOffset);
                    }
                });
                 Logger.Log($"[EscadreProxy.Server {OwnerId}] Sent UpdateEscadreInfo.");
            }

            public void SendShopDesigns()
            {
                var designs = _level.GameShop.AvailableShipDesigns;
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Preparing to send ShopShipDesignsInfo. Count: {designs.Count}");
                if(designs.Count == 0) Logger.LogWarning($"[EscadreProxy.Server {OwnerId}] No shop designs available in Level.GameShop to send!");

                _networkLayer.SendToClient(OwnerId, OwnerId, MessageType.ShopShipDesignsInfo, writer =>
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
                Logger.Log($"[EscadreProxy.Server {OwnerId}] Sent ShopShipDesignsInfo.");
            }
        }
        
        public class ClientProxy : IClientStateProxy 
        {
            public int OwnerId { get; } 
            public ClientLevel OwningClientLevel { get; } 
            public ClientEscadreState State { get; }

            public event Action OnRemoved; 

            public ClientProxy(int ownerClientId, ClientLevel clientLevel)
            {
                OwnerId = ownerClientId;
                OwningClientLevel = clientLevel ?? throw new ArgumentNullException(nameof(clientLevel));
                State = new ClientEscadreState(ownerClientId);
            }

            public void HandleNetworkMessage(MessageType messageType, BinaryReader reader)
            {
                Logger.Log($"[EscadreProxy.Client {OwnerId}] HandleNetworkMessage called with Type: {messageType}");
                switch (messageType)
                {
                    case MessageType.UpdateEscadreInfo:
                        State.Resources = reader.ReadInt32();
                        int formationCount = reader.ReadInt32();
                        State.FormationSlots.Clear();
                        for (int i = 0; i < formationCount; i++)
                        {
                            int shipId = reader.ReadInt32();
                            Vector2 offset = SerializationUtils.ReadVector2(reader);
                            State.FormationSlots.Add(new FormationSlot(offset, shipId == -1 ? (int?)null : shipId ));
                        }
                        Logger.Log($"[EscadreProxy.Client {OwnerId}] Processed UpdateEscadreInfo. Res: {State.Resources}, Slots: {State.FormationSlots.Count}");
                        State.TriggerResourcesChanged(); 
                        State.TriggerFormationChanged();
                        break;

                    case MessageType.ShopShipDesignsInfo:
                        int designCount = reader.ReadInt32();
                        State.AvailableShopDesigns.Clear();
                        for (int i = 0; i < designCount; i++)
                        {
                            int designId = reader.ReadInt32();
                            string name = reader.ReadString();
                            int cost = reader.ReadInt32();
                            Entity.EntityTypeEnum entityType = (Entity.EntityTypeEnum)reader.ReadByte();
                            State.AvailableShopDesigns.Add(new ShipDesign(designId, name, cost, entityType));
                        }
                        Logger.Log($"[EscadreProxy.Client {OwnerId}] Processed ShopShipDesignsInfo. Count: {State.AvailableShopDesigns.Count}");
                        State.TriggerShopDesignsChanged();
                        break;
                    
                    default:
                        Logger.LogWarning($"[EscadreProxy.Client {OwnerId}] Unhandled message type: {messageType}");
                        break;
                }
            }
            
            public void NotifyRemoved()
            {
                OnRemoved?.Invoke();
            }
        }
    }
}