// File: Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model;
using Core.Primitives;
using Core.Client; 

namespace Core.Network
{
    public enum MessageType : byte
    {
        // S->C Lifecycle & State
        CreateEntity = 1,
        DestroyEntity = 2,          
        VanishEntity = 3,           
        UpdateState = 4,            // For individual entity state updates
        EntityEvent = 5,            

        // C->S State Sync & View Updates
        _ClientSyncState = 10,
        _UpdateViewPosition = 11,

        // C->S Player Commands (Escadre General)
        _SetCourse = 20,
        _AttackEscadre = 21,
        _CancelAttack = 22,
        // Note: _UpgradeShip was C->S, now S->C will update stats if needed after C->S _RequestUpgradeShip
        // Note: _BuyShip was C->S, now S->C will send CreateEntity after C->S _RequestBuyShip

        // C->S Player Commands (Shop & Formation) - Context: Client's Own Escadre
        _RequestBuyShip = 24,       // Payload: designId, preferredRelativeOffsetX, preferredRelativeOffsetY
        _RequestUpgradeShip = 25,   // Payload: shipEntityIdToUpgrade
        _RequestSetFormation = 26,  // Payload: count, then [shipId, relX, relY] for each ship

        // C->S Session Management
        _ClientConnectRequest = 30,
        _ClientDisconnect = 31,

        // S->C Escadre-Level State (Not tied to a single PVS entity, context is ClientID)
        UpdateEscadreInfo = 40,       // Payload: resources, formation_count, [shipId, relX, relY]...
                                        // Could be split into UpdateEscadreResources, UpdateEscadreFormation
        ShopShipDesignsInfo = 41,   // Payload: count, then [designId, name, cost, entityType]...
        
        // S->C Command Results (Optional - for explicit success/failure feedback if not implicit)
        // _BuyShipResult = 50,
        // _UpgradeShipResult = 51,
        // _SetFormationResult = 52,
    }

    public interface IClientProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; } // Only for Entity-based proxies
        Vector3 Position { get; } // Only for Entity-based proxies
        Quaternion Rotation { get; } // Only for Entity-based proxies
        ClientLevel OwningClientLevel { get; } 

        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed();
        void Update(float deltaTime); 

        event Action OnDestroyed;
        event Action OnLoudDestructionSignaled; // Only for Entity-based proxies
        event Action<Vector3> PositionChanged; // Only for Entity-based proxies
        event Action<Quaternion> RotationChanged; // Only for Entity-based proxies
    }
    
    // New interface for non-Entity proxies like EscadreProxy
    public interface IClientStateProxy // Could be merged or kept separate from IClientProxy for clarity
    {
        int OwnerId { get; } // e.g., ClientId for EscadreProxy
        ClientLevel OwningClientLevel { get; }
        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyRemoved(); // Different lifecycle than entity destruction
        event Action OnRemoved;
    }


    public interface IServerProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; }
        Quaternion Rotation { get; }
        void StartReplicating();
        void StopReplicating();
        bool CheckClientSyncState(BinaryReader reader);
        void SerializeSpecificInitialState(BinaryWriter writer);
        void SerializeCorrectionState(BinaryWriter writer);
        void SendVanishMessage(IEnumerable<int> targetClientIds);
    }

    // New interface for non-Entity server proxies
    public interface IServerStateProxy
    {
        int OwnerId { get; }
        void StartReplicatingToOwner(); // Or just StartReplicating(int ownerClientId)
        void StopReplicating();
        // No CheckClientSyncState or initial/correction state like entities. State is pushed.
    }


    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int contextId, MessageType messageType, Action<BinaryWriter> serializePayloadAction); // contextId can be entityId or ownerId
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds);
        event Action<int /*sendingNetworkSourceId*/, int /*entityId_or_ContextId*/, MessageType, BinaryReader> OnClientMessageReceived;
    }

    public interface IClientNetworkLayer
    {
        void SendToServer(int contextId, MessageType messageType, Action<BinaryWriter> serializePayloadAction); // contextId can be entityId or 0 for global
        event Action<int /*contextId*/, MessageType, BinaryReader> OnMessageReceived;
    }
}