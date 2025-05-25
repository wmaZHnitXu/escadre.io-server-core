// File: Core/Ocean/OceanSettings.cs
namespace Core.Ocean
{
    public class OceanSettings
    {
        /// <summary>
        /// Factor to scale the raw displacement values from the texture.
        /// Affects dx, dy, dz equally unless overridden by more specific scales.
        /// </summary>
        public float DisplacementScale { get; set; }

        /// <summary>
        /// The real-world size (in game units) that one tile of the X-Z plane of the texture covers.
        /// For example, if TextureResolutionXZ is 64, and this is 128.0f, then each texel covers 2 world units.
        /// </summary>
        public float TextureTileWorldSize { get; set; }

        /// <summary>
        /// The real-world time (in seconds) that one full loop of the time slices in the texture represents.
        /// For example, if TextureResolutionTime is 64, and this is 20.0f, then each time slice effectively lasts 20/64 seconds.
        /// </summary>
        public float TextureTimeLoopDuration { get; set; }

        /// <summary>
        /// The resolution of the texture in the X and Z dimensions (assumed square).
        /// </summary>
        public int TextureResolutionXZ { get; }

        /// <summary>
        /// The resolution of the texture in the time dimension (number of time slices).
        /// </summary>
        public int TextureResolutionTime { get; }

        public OceanSettings(
            float displacementScale = 1.0f, 
            float textureTileWorldSize = 64.0f, 
            float textureTimeLoopDuration = 10.0f,
            int textureResolutionXZ = 64, // Default to problem spec
            int textureResolutionTime = 64) // Default to problem spec
        {
            DisplacementScale = displacementScale;
            TextureTileWorldSize = textureTileWorldSize;
            TextureTimeLoopDuration = textureTimeLoopDuration;
            TextureResolutionXZ = textureResolutionXZ;
            TextureResolutionTime = textureResolutionTime;
        }
    }
}