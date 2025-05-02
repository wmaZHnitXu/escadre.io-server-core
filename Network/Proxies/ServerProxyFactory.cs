// File: Core/Network/Proxies/ServerProxyFactory.cs
using System;
using Server.Core.Model;
using Core.Network;

namespace Core.Network.Proxies
{
    public static class ServerProxyFactory
    {
        public static IServerProxy CreateServerProxy(Entity entity, IServerNetworkLayer networkLayer)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            switch (entity.EntityType)
            {
                case Entity.EntityTypeEnum.Debug:
                    return new DebugEntityProxy.ServerProxy(entity as DebugEntity, networkLayer);
                // Add cases for other entity types here
                // case Entity.EntityTypeEnum.Player:
                //    return new PlayerEntityProxy.ServerProxy(entity as PlayerEntity, networkLayer);
                default:
                    throw new ArgumentException($"No ServerProxy registered for EntityType: {entity.EntityType}");
            }
        }
    }
}