// File: Core/Client/ClientGameActions.cs
// This class will provide methods for the client UI/Input layer to call,
// which will then use IClientNetworkLayer to send messages to the server.
using Core.Network;
using Core.Primitives;
using Core.Logging;
using System.Collections.Generic; // For List
using System;
using Core.Network.Proxies; // For Tuple

namespace Core.Client
{
    public class ClientGameActions
    {
        private readonly IClientNetworkLayer _networkLayer;
        private readonly int _localClientId; // To know which EscadreProxy to interact with locally if needed for UI state before server ack.

        public ClientGameActions(IClientNetworkLayer networkLayer, int localClientId)
        {
            _networkLayer = networkLayer ?? throw new ArgumentNullException(nameof(networkLayer));
            _localClientId = localClientId; // Store the ID of the client this instance is for
        }

        // --- Escadre General Commands ---
        public void SendSetCourse(Vector2 destination)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _SetCourse to {destination}");
            _networkLayer.SendToServer(
                0, // Context ID 0 for global/self-escadre commands
                MessageType._SetCourse,
                writer => SerializationUtils.WriteVector2(writer, destination)
            );
        }

        public void SendAttackEscadre(int targetOwnerClientId)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _AttackEscadre on Client {targetOwnerClientId}");
            _networkLayer.SendToServer(
                0, // Context ID 0 for global/self-escadre commands
                MessageType._AttackEscadre,
                writer => writer.Write(targetOwnerClientId)
            );
        }

        public void SendCancelAttack()
        {
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _CancelAttack");
            _networkLayer.SendToServer(
                0, // Context ID 0 for global/self-escadre commands
                MessageType._CancelAttack,
                writer => { /* No payload */ }
            );
        }


        // --- Shop Interactions ---
        public void RequestBuyShip(int shipDesignId, Vector2 preferredFormationOffset)
        {
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _RequestBuyShip: DesignID={shipDesignId}, Offset={preferredFormationOffset}");
            _networkLayer.SendToServer(
                0, // Context ID 0 for shop/self-escadre commands
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
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _RequestUpgradeShip for ShipID={shipEntityIdToUpgrade}");
            _networkLayer.SendToServer(
                0, // Context ID 0 for shop/self-escadre commands
                MessageType._RequestUpgradeShip,
                writer => writer.Write(shipEntityIdToUpgrade)
            );
        }

        // --- Formation Management ---
        public void RequestSetFormation(List<Tuple<int, Vector2>> newFormationLayout)
        {
            if (newFormationLayout == null)
            {
                Logger.LogWarning($"[ClientGameActions CId:{_localClientId}] RequestSetFormation called with null layout.");
                return;
            }
            Logger.Log($"[ClientGameActions CId:{_localClientId}] Sending _RequestSetFormation with {newFormationLayout.Count} slots.");
            _networkLayer.SendToServer(
                0, // Context ID 0 for formation/self-escadre commands
                MessageType._RequestSetFormation,
                writer =>
                {
                    writer.Write(newFormationLayout.Count);
                    foreach (var slotData in newFormationLayout)
                    {
                        writer.Write(slotData.Item1); // shipId
                        SerializationUtils.WriteVector2(writer, slotData.Item2); // relativeOffset
                    }
                }
            );
        }
    }
}