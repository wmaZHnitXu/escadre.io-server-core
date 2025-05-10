// File: Scripts/Server/Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model; // For EntityTypeEnum
using Core.Primitives; // For Vector3, Quaternion

namespace Core.Network
{
    // --- Enums and Client Proxy Interface remain the same ---
    public enum MessageType : byte
    {
        CreateEntity = 1,
        DestroyEntity = 2,
        UpdateState = 3,
        EntityEvent = 4,
        ClientSyncState = 5,
    }
    public interface IClientProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        void HandleNetworkMessage(MessageType messageType, BinaryReader reader); void NotifyDestroyed(); Vector3 Position { get; }
        Quaternion Rotation { get; }
        event Action OnDestroyed;
    }


    // --- UPDATED IServerProxy Interface ---
    public interface IServerProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; } // Added: To get the type for CreateEntity
        Vector3 Position { get; }               // Added: To get current common state for CreateEntity
        Quaternion Rotation { get; }            // Added: To get current common state for CreateEntity

        void StartReplicating();
        void StopReplicating();

        /// <summary> Checks client checksum. Returns true if correction needed. </summary>
        bool CheckClientSyncState(BinaryReader reader);

        /// <summary> Serializes specific *initial* state for CreateEntity message. </summary>
        void SerializeSpecificInitialState(BinaryWriter writer); // Renamed for clarity

        /// <summary> Serializes full state for UpdateState correction message. </summary>
        void SerializeCorrectionState(BinaryWriter writer);

        /// <summary> Sends final destroy command to specified clients. </summary>
        void SendDestroyMessage(IEnumerable<int> targetClientIds);
    }

    // --- Network Layer Interfaces remain the same ---
    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendDestroyCommand(int entityId, IEnumerable<int> targetClientIds);
        event Action<int, int, MessageType, BinaryReader> OnClientMessageReceived;
    }
    public interface IClientNetworkLayer
    {
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int, MessageType, BinaryReader> OnMessageReceived;
    }
}