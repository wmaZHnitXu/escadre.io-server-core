// File: Scripts/Server/Core/Network/NetworkTypes.cs
using System;
using System.IO;
using System.Collections.Generic;
using Core.Model;
using Core.Primitives;

namespace Core.Network
{
    public enum MessageType : byte
    {
        // S->C Lifecycle & State
        CreateEntity = 1,
        DestroyEntity = 2,  // Now specifically means "Loud Destruction, play effects"
        VanishEntity = 3,   // Means "Entity removed from PVS or silently destroyed, cleanup proxy"
        UpdateState = 4,
        EntityEvent = 5,    // Note: Shifted EntityEvent down due to VanishEntity insertion

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
        void HandleNetworkMessage(MessageType messageType, BinaryReader reader);
        void NotifyDestroyed(); // Called when VanishEntity is received
        Vector3 Position { get; }
        Quaternion Rotation { get; }
        event Action OnDestroyed; // For final cleanup
        event Action OnLoudDestructionSignaled; // For effects
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

        /// <summary> Sends Vanish command to specified clients for proxy cleanup. </summary>
        void SendVanishMessage(IEnumerable<int> targetClientIds); // Renamed
    }

    public interface IServerNetworkLayer
    {
        void BroadcastRelevant(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendToClient(int clientId, int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        void SendVanishCommand(int entityId, IEnumerable<int> targetClientIds); // Added
        event Action<int, int, MessageType, BinaryReader> OnClientMessageReceived;
    }
    public interface IClientNetworkLayer
    {
        void SendToServer(int entityId, MessageType messageType, Action<BinaryWriter> serializePayloadAction);
        event Action<int, MessageType, BinaryReader> OnMessageReceived;
    }
}