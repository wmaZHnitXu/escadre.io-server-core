// File: Scripts/Server/Core/Network/NetworkTypes.cs
using System;
using System.IO;

namespace Core.Network
{
    public enum MessageType : byte
    {
        CreateEntity = 1,
        DestroyEntity = 2,
        UpdateState = 3,
        EntityEvent = 4,
        ClientSyncState = 5,
    }

    // --- Base Proxy Interfaces ---
    public interface IServerProxy
    {
        int EntityId { get; }
        void StartReplicating();
        void StopReplicating();
        // Interface now takes reader and returns if correction needed
        bool CheckClientSyncState(BinaryReader reader);
        // Method to provide data for the correction message
        void SerializeCorrectionState(BinaryWriter writer);
        void SendDestroyMessage(); // Remains the same
    }

    public interface IClientProxy // No changes needed here
    {
        int EntityId { get; }
        Server.Core.Model.Entity.EntityTypeEnum EntityType { get; }
        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed();
        Server.Core.Primitives.Vector3 Position { get; }
        Server.Core.Primitives.Quaternion Rotation { get; }
        event Action OnDestroyed;
    }

    // --- Network Layer Interfaces ---
    public interface IServerNetworkLayer
    {
        // S->C Methods
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        // ADDED: Send specifically to one client
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendDestroyCommand(int entityId); // Could also be SendToClient/BroadcastRelevant

        // C->S Message Handling
        // UPDATED: Include clientId
        event Action<int /*clientId*/, int /*entityId*/, MessageType, BinaryReader /*payloadReader*/> OnClientMessageReceived;
    }

    public interface IClientNetworkLayer // No changes needed here
    {
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int /*entityId*/, MessageType, BinaryReader /*reader*/> OnMessageReceived;
    }
}