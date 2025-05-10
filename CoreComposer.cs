// File: Scripts/Server/Core/CoreComposer.cs
using System;
using Core.Model;
using Core.Network;
using Core.Logging;
using Core.Visibility; // Required

namespace Core
{
    public class CoreComposer : IDisposable
    {
        public Level ServerLevel { get; }
        private readonly VisibilityManager _visibilityManager; // Now directly taken
        private readonly ServerReplicationManager _replicationManager;
        private bool _isDisposed = false;

        // Constructor now accepts a VisibilityManager instance
        public CoreComposer(IServerNetworkLayer networkLayer, VisibilityManager visibilityManager)
        {
            if (networkLayer == null) throw new ArgumentNullException(nameof(networkLayer));
            _visibilityManager = visibilityManager ?? throw new ArgumentNullException(nameof(visibilityManager)); // Use injected instance

            Logger.Log("[CoreComposer] Initializing Core Systems...");

            // 1. Initialize Model
            ServerLevel = new Level();
            Logger.Log("[CoreComposer] Level created.");

            // 2. Visibility System is already provided
            Logger.Log("[CoreComposer] VisibilityManager provided.");

            // 3. Initialize Networking (now depends on the provided VisibilityManager)
            _replicationManager = new ServerReplicationManager(ServerLevel, networkLayer, _visibilityManager);
            Logger.Log("[CoreComposer] ServerReplicationManager created.");

            Logger.Log("[CoreComposer] Initialization Complete.");
        }

        /// <summary>
        /// Provides access to the Visibility Manager.
        /// </summary>
        public VisibilityManager VisibilityManager => _visibilityManager;


        public void Update(float deltaTime)
        {
            if (_isDisposed) return;
            try
            {
                ServerLevel.DoUpdate(deltaTime);

                // Entity positions should ideally be updated in VisibilityManager via events from Entities.
                // If not, and polling is used, it should happen here.
                // Assuming entity positions are updated within Level.DoUpdate() or by entities themselves,
                // and if event-driven updates to VisibilityManager (Problem 3 solution) are implemented,
                // this explicit loop might not be needed or would be different.
                // For now, keeping the loop for entities whose positions might have changed
                // and need to be registered/updated in the visibility system if not event-driven.
                foreach (var entity in ServerLevel.GetAllEntities())
                {
                     if (!entity.IsDead) // Only update active entities
                     {
                         _visibilityManager.UpdateEntityPosition(entity); // Ensures strategy has up-to-date pos
                     }
                }

                _visibilityManager.UpdateAllClientVisibility();
            }
            catch(Exception ex) { Logger.LogError($"[CoreComposer] Error during Update loop: {ex.Message}\nStackTrace: {ex.StackTrace}"); }
        }

        public void Dispose()
        {
            if (_isDisposed) return; _isDisposed = true;
            Logger.Log("[CoreComposer] Disposing...");
            _replicationManager?.Dispose();
            // _visibilityManager is managed externally if injected, so its Dispose might be called by its owner (e.g., ServerComposer)
            // However, if CoreComposer has operations that rely on it during its lifetime, it might still be relevant to unhook things.
            // For now, assuming owner handles _visibilityManager.Dispose().
            // If _visibilityManager was created here from a strategy, then _strategy.Dispose() would be here.
            ServerLevel?.Destroy();
            Logger.Log("[CoreComposer] Dispose complete.");
        }
    }
}