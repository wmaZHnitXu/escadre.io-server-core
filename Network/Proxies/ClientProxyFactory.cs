// File: Core/Network/Proxies/ClientProxyFactory.cs
using System;
using System.IO;
using Core.Logging;
using Core.Model;
using Core.Network;
using Core.Client; 

namespace Core.Network.Proxies
{
    public static class ClientProxyFactory
    {
        // This factory is for IClientProxy instances tied to Entities.
        // EscadreProxy.ClientProxy creation will be handled by ClientEntityManager.
        public static IClientProxy CreateClientProxy(int entityId, Entity.EntityTypeEnum entityType, ClientLevel clientLevel, BinaryReader reader)
        {
            if (clientLevel == null) throw new ArgumentNullException(nameof(clientLevel));

            BaseClientProxy proxy; // All entity proxies derive from BaseClientProxy
            switch (entityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    proxy = new DebugEntityProxy.ClientProxy(entityId, clientLevel);
                    break;
                case Entity.EntityTypeEnum.DefaultShip:
                    proxy = new ShipProxy.ClientProxy(entityId, entityType, clientLevel);
                    break;
                // case Entity.EntityTypeEnum.SomeDestructibleTerrain:
                //    proxy = new DestructibleEntityProxy.ClientProxy(entityId, entityType, clientLevel);
                //    break;
                default:
                    Logger.LogError($"[ClientProxyFactory] No ClientProxy registered for EntityType: {entityType}. Entity ID: {entityId}. Cannot create proxy.");
                    throw new ArgumentException($"No ClientProxy registered for EntityType: {entityType}");
            }

            // Initialize common state after specific proxy construction
            proxy.Initialize(reader); 
            return proxy;
        }
    }
}