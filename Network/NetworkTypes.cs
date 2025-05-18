// File: Scripts/Server/Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model; // For EntityTypeEnum
using Core.Primitives; // For Vector3, Quaternion

namespace Core.Network
{
    public enum MessageType : byte
    {
        // S->C Lifecycle & State
        CreateEntity = 1,
        DestroyEntity = 2,  // Means "Loud Destruction, play effects"
        VanishEntity = 3,   // Means "Entity removed from PVS or silently destroyed, cleanup proxy"
        UpdateState = 4,
        EntityEvent = 5,

        // C->S State Sync & View Updates
        _ClientSyncState = 6,
        _UpdateViewPosition = 7,

        // C->S Player Commands
        _SetCourse = 10,
        _AttackEscadre = 11,
        _CancelAttack = 12,
        _UpgradeShip = 13,
        _BuyShip = 14,
    }

    public interface IClientProxy
    {
        int EntityId { get; }
        Entity.EntityTypeEnum EntityType { get; }
        Vector3 Position { get; }
        Quaternion Rotation { get; }

        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed(); // Called when VanishEntity is received

        /// <summary>
        /// Invoked when the proxy is fully destroyed and should be cleaned up
        /// (typically after receiving a VanishEntity message).
        /// </summary>
        event Action OnDestroyed;

        /// <summary>
        /// Invoked when a "Loud Destruction" signal (DestroyEntity message) is received from the server,
        /// signaling that destruction effects should be played.
        /// </summary>
        event Action OnLoudDestructionSignaled; // <<< THIS WAS MISSING

        /// <summary>
        /// Invoked when the proxy's position changes.
        /// </summary>
        event Action<Vector3> PositionChanged; // Added for completeness and explicit contract

        /// <summary>
        /// Invoked when the proxy's rotation changes.
        /// </summary>
        event Action<Quaternion> RotationChanged; // Added for completeness and explicit contract
    }


    // --- IServerProxy Interface ---
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

    // --- Network Layer Interfaces ---
    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds);
        event Action<int, int, MessageType, BinaryReader> OnClientMessageReceived;
    }
    public interface IClientNetworkLayer
    {
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int, MessageType, BinaryReader> OnMessageReceived;
    }
}