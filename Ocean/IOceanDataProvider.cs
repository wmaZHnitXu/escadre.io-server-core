// File: Core/Ocean/IOceanDataProvider.cs
namespace Core.Ocean
{
    using Core.Primitives;

    public interface IOceanDataProvider
    {
        /// <summary>
        /// Gets the settings associated with this ocean data provider.
        /// </summary>
        OceanSettings Settings { get; }

        /// <summary>
        /// Gets the ocean displacement vector at a given world X, Z coordinate and time.
        /// The displacement vector's X and Z components represent horizontal water movement,
        /// and the Y component represents vertical water movement from a base Y=0 plane.
        /// </summary>
        /// <param name="worldX">World X coordinate.</param>
        /// <param name="worldZ">World Z coordinate.</param>
        /// <param name="time">Current game time, used for animating the ocean.</param>
        /// <returns>A Vector3 representing the displacement (dx, dy, dz).</returns>
        Vector3 GetDisplacement(float worldX, float worldZ, float time);

        /// <summary>
        /// Gets the ocean surface normal vector at a given world X, Z coordinate and time.
        /// Useful for aligning floating objects to the water surface.
        /// </summary>
        /// <param name="worldX">World X coordinate.</param>
        /// <param name="worldZ">World Z coordinate.</param>
        /// <param name="time">Current game time.</param>
        /// <returns>A normalized Vector3 representing the surface normal.</returns>
        Vector3 GetNormal(float worldX, float worldZ, float time);
    }
}