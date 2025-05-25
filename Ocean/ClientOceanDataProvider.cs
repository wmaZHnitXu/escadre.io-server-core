// File: Core/Ocean/ClientTextureBasedOceanDataProvider.cs
namespace Core.Client // Or Core.Ocean
{
    using Core.Primitives;
    using Core.Ocean;
    using Core.Logging;
    using System; // For MathF, Math.Clamp, ArgumentNullException, IDisposable

    public class ClientTextureBasedOceanDataProvider : IOceanDataProvider, IDisposable
    {
        public OceanSettings Settings { get; }
        private byte[,,,] _textureData; // [timeIndex, zIndex, xIndex, channelIndex]
        private const float MAX_BYTE_TO_DISPLACEMENT_UNSCALED = 2.0f;

        public ClientTextureBasedOceanDataProvider(OceanSettings settings, byte[] rawTextureDataFlat = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _textureData = new byte[Settings.TextureResolutionTime, Settings.TextureResolutionXZ, Settings.TextureResolutionXZ, 3];

            if (rawTextureDataFlat != null)
            {
                int expectedSize = Settings.TextureResolutionTime * Settings.TextureResolutionXZ * Settings.TextureResolutionXZ * 3;
                if (rawTextureDataFlat.Length == expectedSize)
                {
                    int flatIndex = 0;
                    for (int t = 0; t < Settings.TextureResolutionTime; t++)
                    for (int z = 0; z < Settings.TextureResolutionXZ; z++)
                    for (int x = 0; x < Settings.TextureResolutionXZ; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        // Data layout from problem: Slices (time), Rows (Z), Columns (X), Components (RGB)
                        // Corresponds to _textureData[t, z, x, c]
                        _textureData[t, z, x, c] = rawTextureDataFlat[flatIndex++];
                    }
                    Logger.Log("[ClientTextureBasedOceanDataProvider] Initialized with provided raw texture data.");
                }
                else
                {
                     Logger.LogWarning($"[ClientTextureBasedOceanDataProvider] Provided raw texture data size mismatch. Expected {expectedSize}, got {rawTextureDataFlat.Length}. Using dummy data.");
                     InitializeDummyData();
                }
            }
            else
            {
                Logger.LogWarning("[ClientTextureBasedOceanDataProvider] No raw texture data provided. Initializing with DUMMY ocean data.");
                InitializeDummyData();
            }
        }
        
        private void InitializeDummyData()
        {
            for (int t = 0; t < Settings.TextureResolutionTime; t++)
            for (int z = 0; z < Settings.TextureResolutionXZ; z++)
            for (int x = 0; x < Settings.TextureResolutionXZ; x++)
            {
                _textureData[t, z, x, 0] = 128; 
                _textureData[t, z, x, 1] = 128; 
                _textureData[t, z, x, 2] = 128;
            }
            // Add a simple wave for testing if using dummy data
            if (Settings.TextureResolutionXZ >= 16 && Settings.TextureResolutionTime > 0)
            {
                for (int t = 0; t < Settings.TextureResolutionTime; t++)
                {
                    float timePhase = (float)t / Settings.TextureResolutionTime * 2f * MathF.PI;
                    for (int z = 0; z < Settings.TextureResolutionXZ; z++)
                    {
                        for (int x = 0; x < Settings.TextureResolutionXZ; x++)
                        {
                            float waveVal = MathF.Sin(x / (float)Settings.TextureResolutionXZ * 4f * MathF.PI + timePhase); 
                            _textureData[t, z, x, 1] = (byte)Math.Clamp(128 + waveVal * 30f, 0, 255); 
                        }
                    }
                }
                 Logger.Log("[ClientTextureBasedOceanDataProvider] Dummy data includes a simple test wave.");
            }
        }

        private Vector3 SampleTexture(int tIdx, int zIdx, int xIdx)
        {
            tIdx = Math.Clamp(tIdx, 0, Settings.TextureResolutionTime - 1);
            zIdx = Math.Clamp(zIdx, 0, Settings.TextureResolutionXZ - 1);
            xIdx = Math.Clamp(xIdx, 0, Settings.TextureResolutionXZ - 1);

            float dx = (_textureData[tIdx, zIdx, xIdx, 0] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;
            float dy = (_textureData[tIdx, zIdx, xIdx, 1] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;
            float dz = (_textureData[tIdx, zIdx, xIdx, 2] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;
            return new Vector3(dx, dy, dz) * Settings.DisplacementScale;
        }

        public Vector3 GetDisplacement(float worldX, float worldZ, float time)
        {
            float timeNormalized = (time % Settings.TextureTimeLoopDuration) / Settings.TextureTimeLoopDuration;
            float xNormalized = (worldX / Settings.TextureTileWorldSize); xNormalized -= MathF.Floor(xNormalized);
            float zNormalized = (worldZ / Settings.TextureTileWorldSize); zNormalized -= MathF.Floor(zNormalized);

            float texTime = timeNormalized * (Settings.TextureResolutionTime -1 );
            float texX = xNormalized * (Settings.TextureResolutionXZ -1);
            float texZ = zNormalized * (Settings.TextureResolutionXZ -1);

            int t0 = (int)MathF.Floor(texTime); float ft = texTime - t0;
            int x0 = (int)MathF.Floor(texX);    float fx = texX - x0;
            int z0 = (int)MathF.Floor(texZ);    float fz = texZ - z0;

            t0 = (t0 % Settings.TextureResolutionTime + Settings.TextureResolutionTime) % Settings.TextureResolutionTime;
            x0 = (x0 % Settings.TextureResolutionXZ + Settings.TextureResolutionXZ) % Settings.TextureResolutionXZ;
            z0 = (z0 % Settings.TextureResolutionXZ + Settings.TextureResolutionXZ) % Settings.TextureResolutionXZ;
            
            int t1 = (t0 + 1) % Settings.TextureResolutionTime;
            int x1 = (x0 + 1) % Settings.TextureResolutionXZ;
            int z1 = (z0 + 1) % Settings.TextureResolutionXZ;

            Vector3 c000 = SampleTexture(t0, z0, x0); Vector3 c100 = SampleTexture(t0, z0, x1);
            Vector3 c010 = SampleTexture(t0, z1, x0); Vector3 c110 = SampleTexture(t0, z1, x1);
            Vector3 c001 = SampleTexture(t1, z0, x0); Vector3 c101 = SampleTexture(t1, z0, x1);
            Vector3 c011 = SampleTexture(t1, z1, x0); Vector3 c111 = SampleTexture(t1, z1, x1);

            Vector3 c00 = Vector3.Lerp(c000, c100, fx); Vector3 c01 = Vector3.Lerp(c001, c101, fx);
            Vector3 c10 = Vector3.Lerp(c010, c110, fx); Vector3 c11 = Vector3.Lerp(c011, c111, fx);
            Vector3 c0 = Vector3.Lerp(c00, c10, fz);    Vector3 c1 = Vector3.Lerp(c01, c11, fz);
            return Vector3.Lerp(c0, c1, ft);
        }
        public Vector3 GetNormal(float worldX, float worldZ, float time)
        {
            float epsilon = Settings.TextureTileWorldSize / (Settings.TextureResolutionXZ * 4f); 
            Vector3 px1_disp = GetDisplacement(worldX + epsilon, worldZ, time);
            Vector3 px0_disp = GetDisplacement(worldX - epsilon, worldZ, time);
            Vector3 pz1_disp = GetDisplacement(worldX, worldZ + epsilon, time);
            Vector3 pz0_disp = GetDisplacement(worldX, worldZ - epsilon, time);

            Vector3 point_x_plus  = new Vector3(worldX + epsilon + px1_disp.X, px1_disp.Y, worldZ + px1_disp.Z);
            Vector3 point_x_minus = new Vector3(worldX - epsilon + px0_disp.X, px0_disp.Y, worldZ + px0_disp.Z);
            Vector3 point_z_plus  = new Vector3(worldX + pz1_disp.X, pz1_disp.Y, worldZ + epsilon + pz1_disp.Z);
            Vector3 point_z_minus = new Vector3(worldX + pz0_disp.X, pz0_disp.Y, worldZ - epsilon + pz0_disp.Z);

            Vector3 tangentX = (point_x_plus - point_x_minus).Normalized;
            Vector3 tangentZ = (point_z_plus - point_z_minus).Normalized;
            Vector3 normal = Vector3.Cross(tangentZ, tangentX).Normalized;
            if (normal.Y < 0) normal = -normal;
            return normal;
        }
        public void Dispose() 
        { 
            _textureData = null; 
            Logger.Log("[ClientTextureBasedOceanDataProvider] Disposed."); 
        }
    }
}