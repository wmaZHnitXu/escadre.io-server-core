using System;
using System.IO;
using Server.Core.Model; // For EntityTypeEnum
using Core.Network;

namespace Core.Network.Proxies
{
    public static class ClientProxyFactory
    {
        // The stream reader is passed here to read the initial state directly
        public static IClientProxy CreateClientProxy(int entityId, Entity.EntityTypeEnum entityType, BinaryReader reader)
        {
             BaseClientProxy proxy;
            switch (entityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    proxy = new DebugEntityProxy.ClientProxy(entityId);
                    break;
                // Add cases for other entity types here
                // case Entity.EntityTypeEnum.Player:
                //    proxy = new PlayerEntityProxy.ClientProxy(entityId);
                //    break;
                default:
                    throw new ArgumentException($"No ClientProxy registered for EntityType: {entityType}");
            }

            // Initialize the proxy with the initial state from the stream
            proxy.Initialize(reader);
            return proxy;
        }
    }
}