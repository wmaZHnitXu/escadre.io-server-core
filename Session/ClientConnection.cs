// File: Core/Session/ClientConnection.cs
using System;
using Core.Model;
using Core.Primitives;
using Core.Visibility;
using Core.Logging;

namespace Core.Session
{
    public enum ClientState
    {
        Connecting,
        Spectating,
        InSea,
        Destroyed
    }

    public class ClientConnection : IClientView
    {
        public int ClientId { get; }
        public ClientState CurrentState { get; private set; }

        public Vector3 Position => EscadreInstance?.CalculateCenterPoint() ?? _lastKnownCameraPosition;
        public float RadiusOfInterest { get; set; }

        public Escadre EscadreInstance { get; private set; }
        private Vector3 _lastKnownCameraPosition = Vector3.Zero;

        public ClientConnection(int clientId, float initialRadiusOfInterest = 100f)
        {
            ClientId = clientId;
            RadiusOfInterest = initialRadiusOfInterest;
            CurrentState = ClientState.Connecting;
            // Logger.Log($"[ClientConnection {ClientId}] Created. Initial State: {CurrentState}");
        }

        // Helper to get current server time.
        // This needs to be a consistent source for the server.
        // For a Unity-hosted server, Time.time is okay.
        // For a dedicated server, use a Stopwatch or similar.
        private float GetCurrentServerTime()
        {
            #if UNITY_EDITOR || UNITY_SERVER || UNITY_STANDALONE // If server runs in Unity context
            return UnityEngine.Time.time;
            #else
            // Basic fallback for non-Unity server context (e.g. console app)
            // This is a very rough approximation and not suitable for precise timing.
            // A proper server would use a high-resolution timer or a game loop clock.
            return (float)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            #endif
        }

        public void SetState(ClientState newState)
        {
            if (CurrentState == newState) return;
            // Logger.Log($"[ClientConnection {ClientId}] State changing from {CurrentState} to {newState}");
            CurrentState = newState;
            if (newState == ClientState.Destroyed || newState == ClientState.Spectating) {
                 ClearEscadreReference();
            }
        }

        public void AssignEscadre(Escadre escadre)
        {
            if (escadre == null || escadre.OwnerClientId != ClientId) {
                Logger.LogError($"[ClientConnection {ClientId}] Attempted to assign invalid escadre.");
                EscadreInstance = null;
                return;
            }
            EscadreInstance = escadre;
            SetState(ClientState.InSea);
            // Logger.Log($"[ClientConnection {ClientId}] Assigned Escadre.");
        }

        public void ClearEscadreReference()
        {
            if (EscadreInstance != null) {
                // Logger.Log($"[ClientConnection {ClientId}] Clearing escadre reference.");
                EscadreInstance = null;
                if(CurrentState == ClientState.InSea) SetState(ClientState.Spectating);
            }
        }

        public void UpdateSpectatorCameraPosition(Vector3 newPosition)
        {
             if (CurrentState == ClientState.Spectating || EscadreInstance == null) {
                 _lastKnownCameraPosition = newPosition;
             }
        }

        public bool RequestSetCourse(Vector2 destination)
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null) { /* ... log warning ... */ return false; }
            EscadreInstance.SetCourse(destination, GetCurrentServerTime()); // Pass server time
            return true;
        }

        public bool RequestAttackEscadre(int targetOwnerClientId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { /* ... log warning ... */ return false; }
            EscadreInstance.OrderAttack(targetOwnerClientId, GetCurrentServerTime()); // Pass server time
            return true;
        }

        public bool RequestCancelAttack()
        {
            if (CurrentState != ClientState.InSea || EscadreInstance == null) { /* ... log warning ... */ return false; }
            EscadreInstance.OrderCancelAttack(); // If this implies stopping ships, it might need serverTime too
            return true;
        }

        public bool RequestUpgradeShip(int shipId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { /* ... log warning ... */ return false; }
            EscadreInstance.RequestUpgradeShip(shipId);
            return true;
        }

        public bool RequestBuyShip(int shipDesignId)
        {
             if (CurrentState != ClientState.InSea || EscadreInstance == null) { /* ... log warning ... */ return false; }
            EscadreInstance.RequestBuyShip(shipDesignId, GetCurrentServerTime()); // Pass server time
            return true;
        }
    }
}