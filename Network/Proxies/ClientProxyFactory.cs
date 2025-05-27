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
        public static IClientProxy CreateClientProxy(int entityId, Entity.EntityTypeEnum entityType, ClientLevel clientLevel, BinaryReader reader)
        {
            if (clientLevel == null) throw new ArgumentNullException(nameof(clientLevel));

            BaseClientProxy proxy; 
            switch (entityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    proxy = new DebugEntityProxy.ClientProxy(entityId, clientLevel);
                    break;
                case Entity.EntityTypeEnum.DefaultShip:
                    proxy = new ShipProxy.ClientProxy(entityId, entityType, clientLevel);
                    break;
                case Entity.EntityTypeEnum.Escadre: 
                    proxy = new EscadreProxy.ClientProxy(entityId, clientLevel);
                    break;
                case Entity.EntityTypeEnum.ResourceBox: // Added
                    proxy = new CollectableFloatingEntityProxy.ClientProxy(entityId, entityType, clientLevel);
                    break;
                // DefaultCannon is an AttachedEntity, client-side representation would be part of the Ship's presentation.
                // It typically doesn't have its own standalone proxy unless it's a fully independent entity.
                default:
                    Logger.LogError($"[ClientProxyFactory] No ClientProxy registered for EntityType: {entityType}. Entity ID: {entityId}. Cannot create proxy.");
                    throw new ArgumentException($"No ClientProxy registered for EntityType: {entityType}");
            }
            
            proxy.Initialize(reader); 
            return proxy;
        }
    }
}