// File: Scripts/Server/Core/CoreComposer.cs
using System;
using Server.Core.Model;
using Core.Network;
using Core.Logging;

namespace Core
{
    public class CoreComposer : IDisposable
    {
        public Level ServerLevel { get; }
        // Keep reference for Dispose, but Update responsibility changes
        private readonly ServerReplicationManager _replicationManager;
        private bool _isDisposed = false;

        public CoreComposer(IServerNetworkLayer networkLayer)
        {
            if (networkLayer == null) throw new ArgumentNullException(nameof(networkLayer));
            Logger.Log("[CoreComposer] Initializing Core Systems...");
            ServerLevel = new Level();
            Logger.Log("[CoreComposer] Level created.");
            _replicationManager = new ServerReplicationManager(ServerLevel, networkLayer);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");
            Logger.Log("[CoreComposer] Initialization Complete.");
        }

        /// <summary>
        /// Executes the core update loop for the server model.
        /// Network updates are now reactive to client messages via ServerReplicationManager.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                // Update Entity Logic ONLY
                ServerLevel.DoUpdate(deltaTime);

                // Network Replication Manager NO LONGER needs explicit Update() for state.
                // It reacts to network events (_networkLayer.OnClientMessageReceived)
                // and entity events (OnDeathEvent).
            }
            catch(Exception ex)
            {
                Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");
            _replicationManager?.Dispose();
            ServerLevel?.Destroy();
            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}