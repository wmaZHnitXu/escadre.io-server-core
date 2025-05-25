// File: Core/Ocean/ServerOceanDataProvider.cs
namespace Core.Ocean
{
    using Core.Primitives;
    using Core.Logging;
    using System;

    // This is a server-side equivalent of ClientTextureBasedOceanDataProvider.
    // It would load/generate the texture data. For now, it uses dummy data.
    public class ServerOceanDataProvider : IOceanDataProvider, IDisposable
    {
        public OceanSettings Settings { get; }
        private byte[,,,] _textureData; // [timeIndex, zIndex, xIndex, channelIndex]
        private const float MAX_BYTE_TO_DISPLACEMENT_UNSCALED = 2.0f; // How much +/- 128 byte range maps to in displacement units before main scale.

        public ServerOceanDataProvider(OceanSettings settings, byte[] rawTextureDataFlat = null)
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
                        _textureData[t, z, x, c] = rawTextureDataFlat[flatIndex++];
                    }
                    Logger.Log("[ServerOceanDataProvider] Initialized with provided raw texture data.");
                }
                else
                {
                     Logger.LogWarning($"[ServerOceanDataProvider] Provided raw texture data size mismatch. Expected {expectedSize}, got {rawTextureDataFlat.Length}. Using dummy data.");
                     InitializeDummyData();
                }
            }
            else
            {
                Logger.LogWarning("[ServerOceanDataProvider] No raw texture data provided. Initializing with DUMMY ocean data.");
                InitializeDummyData();
            }
        }

        private void InitializeDummyData()
        {
            // Fill with neutral displacement (128 for each byte channel -> 0 displacement before scaling)
            for (int t = 0; t < Settings.TextureResolutionTime; t++)
            for (int z = 0; z < Settings.TextureResolutionXZ; z++)
            for (int x = 0; x < Settings.TextureResolutionXZ; x++)
            {
                _textureData[t, z, x, 0] = 128; // X displacement
                _textureData[t, z, x, 1] = 128; // Y displacement
                _textureData[t, z, x, 2] = 128; // Z displacement
            }
            // Add a simple wave for testing
            if (Settings.TextureResolutionXZ >= 16 && Settings.TextureResolutionTime > 0)
            {
                for (int t = 0; t < Settings.TextureResolutionTime; t++)
                {
                    float timePhase = (float)t / Settings.TextureResolutionTime * 2f * MathF.PI;
                    for (int z = 0; z < Settings.TextureResolutionXZ; z++)
                    {
                        for (int x = 0; x < Settings.TextureResolutionXZ; x++)
                        {
                            float waveVal = MathF.Sin(x / (float)Settings.TextureResolutionXZ * 4f * MathF.PI + timePhase); // Simple X-wave
                            _textureData[t, z, x, 1] = (byte)Math.Clamp(128 + waveVal * 30f, 0, 255); // Y displacement (vertical)
                        }
                    }
                }
            }
        }
        
        // Helper for trilinear interpolation (same as client-side)
        private Vector3 SampleTexture(int tIdx, int zIdx, int xIdx)
        {
            // Clamp indices to be safe, though modulo arithmetic in GetDisplacement should handle tiling.
            tIdx = Math.Clamp(tIdx, 0, Settings.TextureResolutionTime - 1);
            zIdx = Math.Clamp(zIdx, 0, Settings.TextureResolutionXZ - 1);
            xIdx = Math.Clamp(xIdx, 0, Settings.TextureResolutionXZ - 1);

            // Convert byte (0-255) to a displacement value (e.g., -1 to 1 range, then scaled)
            // Assuming 128 is zero displacement.
            float dx = (_textureData[tIdx, zIdx, xIdx, 0] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;
            float dy = (_textureData[tIdx, zIdx, xIdx, 1] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;
            float dz = (_textureData[tIdx, zIdx, xIdx, 2] - 128f) / 128f * MAX_BYTE_TO_DISPLACEMENT_UNSCALED;

            return new Vector3(dx, dy, dz) * Settings.DisplacementScale;
        }

        public Vector3 GetDisplacement(float worldX, float worldZ, float time)
        {
            // Normalize world coordinates and time to texture space (0-1 range for one tile/loop)
            float timeNormalized = (time % Settings.TextureTimeLoopDuration) / Settings.TextureTimeLoopDuration;
            
            // For X and Z, we want tiling, so use modulo for world coordinates that are outside the first tile.
            float xNormalized = (worldX / Settings.TextureTileWorldSize);
            xNormalized = xNormalized - MathF.Floor(xNormalized); // Ensures positive 0-1 range for tiling

            float zNormalized = (worldZ / Settings.TextureTileWorldSize);
            zNormalized = zNormalized - MathF.Floor(zNormalized); // Ensures positive 0-1 range for tiling

            // Convert normalized 0-1 coordinates to texture indices
            float texTime = timeNormalized * (Settings.TextureResolutionTime -1 ); // Max index is Res-1
            float texX = xNormalized * (Settings.TextureResolutionXZ -1 );
            float texZ = zNormalized * (Settings.TextureResolutionXZ -1 );

            // Get base indices and fractional parts for interpolation
            int t0 = (int)MathF.Floor(texTime); float ft = texTime - t0;
            int x0 = (int)MathF.Floor(texX);    float fx = texX - x0;
            int z0 = (int)MathF.Floor(texZ);    float fz = texZ - z0;
            
            // Wrap indices for tiling (texture lookup)
            // Since texTime, texX, texZ are derived from normalized 0-1 ranges multiplied by (Res-1),
            // their integer parts t0,x0,z0 should already be within [0, Res-2].
            // The +1 indices (t1,x1,z1) need to wrap around correctly.
            t0 = (t0 % Settings.TextureResolutionTime + Settings.TextureResolutionTime) % Settings.TextureResolutionTime; // Ensure positive modulo
            x0 = (x0 % Settings.TextureResolutionXZ + Settings.TextureResolutionXZ) % Settings.TextureResolutionXZ;
            z0 = (z0 % Settings.TextureResolutionXZ + Settings.TextureResolutionXZ) % Settings.TextureResolutionXZ;
            
            int t1 = (t0 + 1) % Settings.TextureResolutionTime;
            int x1 = (x0 + 1) % Settings.TextureResolutionXZ;
            int z1 = (z0 + 1) % Settings.TextureResolutionXZ;

            // Sample 8 corners of the cube for trilinear interpolation
            Vector3 c000 = SampleTexture(t0, z0, x0); Vector3 c100 = SampleTexture(t0, z0, x1);
            Vector3 c010 = SampleTexture(t0, z1, x0); Vector3 c110 = SampleTexture(t0, z1, x1);
            Vector3 c001 = SampleTexture(t1, z0, x0); Vector3 c101 = SampleTexture(t1, z0, x1);
            Vector3 c011 = SampleTexture(t1, z1, x0); Vector3 c111 = SampleTexture(t1, z1, x1);

            // Interpolate along X
            Vector3 c00 = Vector3.Lerp(c000, c100, fx); Vector3 c01 = Vector3.Lerp(c001, c101, fx);
            Vector3 c10 = Vector3.Lerp(c010, c110, fx); Vector3 c11 = Vector3.Lerp(c011, c111, fx);
            // Interpolate along Z
            Vector3 c0 = Vector3.Lerp(c00, c10, fz);    Vector3 c1 = Vector3.Lerp(c01, c11, fz);
            // Interpolate along Time
            return Vector3.Lerp(c0, c1, ft);
        }

        public Vector3 GetNormal(float worldX, float worldZ, float time)
        {
            // Calculate normal using central differences (more stable than one-sided)
            // Epsilon should be small relative to texel size to capture local gradient.
            float epsilon = Settings.TextureTileWorldSize / (Settings.TextureResolutionXZ * 4f); // e.g., quarter of a texel world size

            // Get displacements at neighboring points
            Vector3 px1 = GetDisplacement(worldX + epsilon, worldZ, time); // Point slightly in +X
            Vector3 px0 = GetDisplacement(worldX - epsilon, worldZ, time); // Point slightly in -X
            Vector3 pz1 = GetDisplacement(worldX, worldZ + epsilon, time); // Point slightly in +Z
            Vector3 pz0 = GetDisplacement(worldX, worldZ - epsilon, time); // Point slightly in -Z

            // Construct points on the displaced surface
            // The points include the original world coordinates AND their displacement
            Vector3 point_x_plus  = new Vector3(worldX + epsilon + px1.X, px1.Y, worldZ + px1.Z);
            Vector3 point_x_minus = new Vector3(worldX - epsilon + px0.X, px0.Y, worldZ + px0.Z);
            Vector3 point_z_plus  = new Vector3(worldX + pz1.X, pz1.Y, worldZ + epsilon + pz1.Z);
            Vector3 point_z_minus = new Vector3(worldX + pz0.X, pz0.Y, worldZ - epsilon + pz0.Z);

            // Tangents along X and Z axes on the surface
            Vector3 tangentX = (point_x_plus - point_x_minus).Normalized;
            Vector3 tangentZ = (point_z_plus - point_z_minus).Normalized;

            // Normal is the cross product of the tangents
            Vector3 normal = Vector3.Cross(tangentZ, tangentX).Normalized; // Order matters for direction (Z x X for Y-up normal)

            // Ensure normal points generally upwards (Y > 0)
            if (normal.Y < 0) normal = -normal;
            
            return normal;
        }

        public void Dispose()
        {
            _textureData = null; // Allow GC
            Logger.Log("[ServerOceanDataProvider] Disposed.");
        }
    }
}