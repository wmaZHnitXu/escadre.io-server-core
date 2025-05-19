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
        UpdateState = 4,            
        EntityEvent = 5,            

        // C->S State Sync & View Updates
        _ClientSyncState = 10,
        _UpdateViewPosition = 11,

        // C->S Player Commands
        _SetCourse = 20,
        _AttackEscadre = 21,
        _CancelAttack = 22,
        _UpgradeShip = 23,
        _BuyShip = 24,

        // C->S Session Management
        _ClientConnectRequest = 30,
        // _ClientDisconnect = 31, // Future consideration

        // S->C Session Management
        // _ClientConnectResponse = 40, // Optional: For now, successful connection implies server starts sending data
    }

    public interface IClientProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; }
        Quaternion Rotation { get; }
        ClientLevel OwningClientLevel { get; } 

        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed();
        void Update(float deltaTime); 

        event Action OnDestroyed;
        event Action OnLoudDestructionSignaled;
        event Action<Vector3> PositionChanged;
        event Action<Quaternion> RotationChanged;
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

    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds);
        // Parameters: sendingClientId, entityId (context, 0 for global commands like connect), messageType, payloadReader
        event Action<int /*sendingClientId*/, int /*entityId*/, MessageType, BinaryReader> OnClientMessageReceived;
    }

    public interface IClientNetworkLayer
    {
        // Parameters: entityId (context, 0 for global commands like connect), messageType, payloadAction
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int, MessageType, BinaryReader> OnMessageReceived;
    }
}