using System;
using System.IO;
using Core.Logging;
using Core.Model;
using Core.Network;
using Core.Client; // For ClientLevel

namespace Core.Network.Proxies
{
    public static class ClientProxyFactory
    {
        // Signature changed to include ClientLevel
        public static IClientProxy CreateClientProxy(int entityId, Entity.EntityTypeEnum entityType, ClientLevel clientLevel, BinaryReader reader)
        {
            if (clientLevel == null) throw new ArgumentNullException(nameof(clientLevel));

            BaseClientProxy proxy;
            switch (entityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    // Pass clientLevel to the constructor
                    proxy = new DebugEntityProxy.ClientProxy(entityId, clientLevel);
                    break;
                case Entity.EntityTypeEnum.DefaultShip:
                    // Pass clientLevel and the concrete entityType to the ShipProxy.ClientProxy constructor
                    proxy = new ShipProxy.ClientProxy(entityId, entityType, clientLevel);
                    break;
                // case Entity.EntityTypeEnum.SomeDestructibleTerrain:
                //    // Pass clientLevel and entityType
                //    proxy = new DestructibleEntityProxy.ClientProxy(entityId, entityType, clientLevel);
                //    break;
                default:
                    Logger.LogWarning($"[ClientProxyFactory] No ClientProxy registered for EntityType: {entityType}. Entity ID: {entityId}. Attempting fallback.");
                    throw new ArgumentException($"No ClientProxy registered for EntityType: {entityType}");
            }

            if (proxy != null)
            {
                proxy.Initialize(reader); // Initialize reads from the reader
            }
            return proxy;
        }
    }
}