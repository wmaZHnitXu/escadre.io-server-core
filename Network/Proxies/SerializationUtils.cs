// File: Core/Network/Proxies/SerializationUtils.cs
using System.IO;
using Core.Primitives;
using Core.Model; 
using Core.Ocean; 

namespace Core.Network.Proxies
{
    internal static class SerializationUtils
    {
        public static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        public static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static void WriteQuaternion(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.X);
            writer.Write(q.Y);
            writer.Write(q.Z);
            writer.Write(q.W);
        }

        public static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static void WriteVector2(BinaryWriter writer, Vector2 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
        }

        public static Vector2 ReadVector2(BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        public static void WriteDamageInfo(BinaryWriter writer, DamageInfo info)
        {
            writer.Write(info.Amount);
            writer.Write((byte)info.Type);
            WriteVector3(writer, info.HitPoint);
            WriteVector3(writer, info.Direction);
            writer.Write(info.AttackerId.HasValue);
            if (info.AttackerId.HasValue) writer.Write(info.AttackerId.Value);
            writer.Write(info.AttackerOwnerClientId.HasValue);
            if (info.AttackerOwnerClientId.HasValue) writer.Write(info.AttackerOwnerClientId.Value);
        }

        public static DamageInfo ReadDamageInfo(BinaryReader reader)
        {
            float amount = reader.ReadSingle();
            DamageType type = (DamageType)reader.ReadByte();
            Vector3 hitPoint = ReadVector3(reader);
            Vector3 direction = ReadVector3(reader);
            int? attackerId = null;
            if (reader.ReadBoolean()) attackerId = reader.ReadInt32();
            int? attackerOwnerClientId = null;
            if (reader.ReadBoolean()) attackerOwnerClientId = reader.ReadInt32();
            return new DamageInfo(amount, type, hitPoint, direction, attackerId, attackerOwnerClientId);
        }

        public static void WriteOceanSettings(BinaryWriter writer, OceanSettings settings)
        {
            writer.Write(settings.DisplacementScale);
            writer.Write(settings.TextureTileWorldSize);
            writer.Write(settings.TextureTimeLoopDuration);
            writer.Write(settings.TextureResolutionXZ);
            writer.Write(settings.TextureResolutionTime);
        }

        public static OceanSettings ReadOceanSettings(BinaryReader reader)
        {
            float displacementScale = reader.ReadSingle();
            float textureTileWorldSize = reader.ReadSingle();
            float textureTimeLoopDuration = reader.ReadSingle();
            int resolutionXZ = reader.ReadInt32();
            int resolutionTime = reader.ReadInt32();
            return new OceanSettings(displacementScale, textureTileWorldSize, textureTimeLoopDuration, resolutionXZ, resolutionTime);
        }
    }
}