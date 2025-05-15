using System;
using System.IO;
using Core.Logging;
using Core.Model;
using Core.Network;

namespace Core.Network.Proxies
{
    public static class ClientProxyFactory
    {
        public static IClientProxy CreateClientProxy(int entityId, Entity.EntityTypeEnum entityType, BinaryReader reader)
        {
             BaseClientProxy proxy;
            switch (entityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    proxy = new DebugEntityProxy.ClientProxy(entityId);
                    break;
                case Entity.EntityTypeEnum.DefaultShip:
                    // Pass the concrete entityType to the ShipProxy.ClientProxy constructor
                    proxy = new ShipProxy.ClientProxy(entityId, entityType);
                    break;
                // case Entity.EntityTypeEnum.SomeDestructibleTerrain:
                //    proxy = new DestructibleEntityProxy.ClientProxy(entityId, entityType);
                //    break;
                default:
                    Logger.LogWarning($"[ClientProxyFactory] No ClientProxy registered for EntityType: {entityType}. Entity ID: {entityId}. Attempting fallback.");
                    // Attempt fallback for unknown types based on hierarchy. This is more complex on client.
                    // For now, strict matching. If we had a way to know on client if it's a "Ship" vs "Destructible"
                    // without full type enum, we could. But EntityTypeEnum is the key.
                    throw new ArgumentException($"No ClientProxy registered for EntityType: {entityType}");
            }

            if (proxy != null)
            {
                proxy.Initialize(reader);
            }
            return proxy;
        }
    }
}