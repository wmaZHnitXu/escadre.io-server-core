// File: Scripts/Server/Core/Session/ClientConnection.cs
using System;
using Core.Model;
using Core.Primitives;
using Core.Visibility; // For IClientView
using Core.Logging;

namespace Core.Session
{
    public enum ClientState
    {
        Connecting,   // Initial state before fully joining
        Spectating,   // Observing the game, no escadre
        InSea,        // Actively playing with an escadre
        Destroyed     // Escadre lost, awaiting respawn or spectate option
    }

    public class ClientConnection : IClientView
    {
        public int ClientId { get; }
        public ClientState CurrentState { get; private set; }

        // IClientView properties
        public Vector3 Position => EscadreInstance?.CalculateCenterPoint() ?? _lastKnownCameraPosition; // PVS center
        public float RadiusOfInterest { get; set; } // Can be configured

        // Link to the player's escadre. Null if not InSea.
        public Escadre EscadreInstance { get; private set; }
        private Vector3 _lastKnownCameraPosition = Vector3.Zero; // Fallback if no escadre

        public ClientConnection(int clientId, float initialRadiusOfInterest = 100f) // Default PVS radius
        {
            ClientId = clientId;
            RadiusOfInterest = initialRadiusOfInterest;
            CurrentState = ClientState.Connecting; // Or Spectating by default
            Logger.Log($"[ClientConnection {ClientId}] Created. Initial State: {CurrentState}");
        }

        public void SetState(ClientState newState)
        {
            if (CurrentState == newState) return;
            Logger.Log($"[ClientConnection {ClientId}] State changing from {CurrentState} to {newState}");
            CurrentState = newState;
            // TODO: Add logic for state transitions (e.g., on entering Destroyed, clear escadre)
            if (newState == ClientState.Destroyed || newState == ClientState.Spectating)
            {
                 ClearEscadreReference(); // If escadre is destroyed, client loses it
            }
        }

        /// <summary>
        /// Assigns an escadre to this client connection. Typically called when client enters "InSea" state.
        /// </summary>
        public void AssignEscadre(Escadre escadre)
        {
            if (escadre == null || escadre.OwnerClientId != ClientId)
            {
                Logger.LogError($"[ClientConnection {ClientId}] Attempted to assign invalid escadre. Owner mismatch or null.");
                EscadreInstance = null;
                return;
            }
            EscadreInstance = escadre;
            SetState(ClientState.InSea);
            Logger.Log($"[ClientConnection {ClientId}] Assigned Escadre.");
        }

        /// <summary>
        /// Clears the reference to the escadre.
        /// </summary>
        public void ClearEscadreReference()
        {
            // Note: This does NOT destroy the escadre's ships. That should be handled
            // by Escadre.Disband() or normal game mechanics.
            // This method is primarily for when the client connection is no longer associated with it.
            if (EscadreInstance != null)
            {
                Logger.Log($"[ClientConnection {ClientId}] Clearing escadre reference.");
                EscadreInstance = null;
                // If state not already Destroyed/Spectating, might need to update it
                if(CurrentState == ClientState.InSea) SetState(ClientState.Spectating); // Or some other default
            }
        }

        /// <summary>
        /// Used by external systems (like VisibilityManager) if the client's view changes
        /// independently of its escadre (e.g., free-look camera in spectate mode).
        /// For this IO game, PVS is escadre-centered, so this is more of a fallback or for spectating.
        /// </summary>
        public void UpdateSpectatorCameraPosition(Vector3 newPosition)
        {
             if (CurrentState == ClientState.Spectating || EscadreInstance == null)
             {
                 _lastKnownCameraPosition = newPosition;
             }
             // If InSea, Position property uses Escadre's center.
        }


        // --- Player Command Requests (Delegated to Escadre) ---

        public bool RequestSetCourse(Vector2 destination)
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Cannot SetCourse. State: {CurrentState} or No Escadre.");
                return false;
            }
            EscadreInstance.SetCourse(destination);
            return true;
        }

        public bool RequestAttackEscadre(int targetOwnerClientId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Cannot AttackEscadre. State: {CurrentState} or No Escadre.");
                return false;
            }
            EscadreInstance.OrderAttack(targetOwnerClientId);
            return true;
        }

        public bool RequestCancelAttack()
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Cannot CancelAttack. State: {CurrentState} or No Escadre.");
                return false;
            }
            EscadreInstance.OrderCancelAttack();
            return true;
        }

        public bool RequestUpgradeShip(int shipId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Cannot UpgradeShip. State: {CurrentState} or No Escadre.");
                return false;
            }
            EscadreInstance.RequestUpgradeShip(shipId);
            return true;
        }

        public bool RequestBuyShip(int shipDesignId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null)
            {
                Logger.LogWarning($"[ClientConnection {ClientId}] Cannot BuyShip. State: {CurrentState} or No Escadre.");
                return false;
            }
            EscadreInstance.RequestBuyShip(shipDesignId);
            return true;
        }
    }
}