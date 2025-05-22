// File: Core/Client/ClientGameActions.cs
using Core.Network;
using Core.Primitives;
using Core.Logging;
using System.Collections.Generic; 
using System;
using Core.Network.Proxies;

namespace Core.Client
{
    public class ClientGameActions
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly int _localClientInstanceId; // This ID is used as sendingNetworkSourceId

        public ClientGameActions(IClientNetworkLayer networkLayer, int localClientInstanceId)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _localClientInstanceId = localClientInstanceId; 
        }

        // --- Escadre General Commands ---
        public void SendSetCourse(Vector2 destination)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _SetCourse to {destination}");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, // Context ID 0 for commands related to the client's own escadre
                MessageType._SetCourse,
                writer => SerializationUtils.WriteVector2(writer, destination)
            );
        }

        public void SendAttackEscadre(int targetEscadreEntityId)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _AttackEscadre on Escadre Entity ID {targetEscadreEntityId}");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, 
                MessageType._AttackEscadre,
                writer => writer.Write(targetEscadreEntityId)
            );
        }

        public void SendCancelAttack()
        {
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _CancelAttack");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, 
                MessageType._CancelAttack,
                writer => { /* No payload */ }
            );
        }


        // --- Shop Interactions ---
        public void RequestBuyShip(int shipDesignId, Vector2 preferredFormationOffset)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _RequestBuyShip: DesignID={shipDesignId}, Offset={preferredFormationOffset}");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, 
                MessageType._RequestBuyShip,
                writer =>
                {
                    writer.Write(shipDesignId);
                    SerializationUtils.WriteVector2(writer, preferredFormationOffset);
                }
            );
        }

        public void RequestUpgradeShip(int shipEntityIdToUpgrade)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _RequestUpgradeShip for ShipID={shipEntityIdToUpgrade}");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, 
                MessageType._RequestUpgradeShip,
                writer => writer.Write(shipEntityIdToUpgrade)
            );
        }

        // --- Formation Management ---
        public void RequestSetFormation(List<Tuple<int, Vector2>> newFormationLayout)
        {
            if (newFormationLayout == null)
            {
                Logger.LogWarning($"[ClientGameActions CId:{_localClientInstanceId}] RequestSetFormation called with null layout.");
                return;
            }
            Logger.Log($"[ClientGameActions CId:{_localClientInstanceId}] Sending _RequestSetFormation with {newFormationLayout.Count} slots.");
            _networkLayer.SendToServer(
                _localClientInstanceId, // Pass as sendingNetworkSourceId
                0, 
                MessageType._RequestSetFormation,
                writer =>
                {
                    writer.Write(newFormationLayout.Count);
                    foreach (var slotData in newFormationLayout)
                    {
                        writer.Write(slotData.Item1); 
                        SerializationUtils.WriteVector2(writer, slotData.Item2); 
                    }
                }
            );
        }
    }
}