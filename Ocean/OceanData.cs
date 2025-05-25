// File: Core/Ocean/OceanData.cs
using System;
using Core.Primitives;
using Core.Logging;

namespace Core.Ocean
{
    /// <summary>
    /// Holds the raw 3D texture data for ocean displacement.
    /// The data is a tensor of 3-byte vectors (X, Y, Z displacement components).
    /// Assumes byte values 0-255 map to a normalized range, e.g., -1.0 to 1.0.
    /// </summary>
    public class OceanData
    {
        private readonly byte[,,,] _data; // [time, x, z, channel] where channel 0=X, 1=Y, 2=Z
        public int SlicesTime { get; }
        public int SlicesX { get; }
        public int SlicesZ { get; }

        private const float BYTE_TO_NORMALIZED_FLOAT_SCALE = 2.0f / 255.0f; // (1 - (-1)) / 255
        private const float BYTE_TO_NORMALIZED_FLOAT_OFFSET = -1.0f;      // Maps 0 to -1

        /// <summary>
        /// Initializes OceanData with the raw byte tensor.
        /// </summary>
        /// <param name="rawData">The raw byte data expecting dimensions [time, x, z, 3].</param>
        public OceanData(byte[,,,] rawData)
        {
            if (rawData == null) throw new ArgumentNullException(nameof(rawData));
            if (rawData.Rank != 4) throw new ArgumentException("Raw data must be a 4-dimensional array.", nameof(rawData));
            if (rawData.GetLength(3) != 3) throw new ArgumentException("Raw data must have 3 channels (X,Y,Z) in the last dimension.", nameof(rawData));

            _data = rawData;
            SlicesTime = rawData.GetLength(0);
            SlicesX = rawData.GetLength(1);
            SlicesZ = rawData.GetLength(2);

            if (SlicesTime == 0 || SlicesX == 0 || SlicesZ == 0)
            {
                throw new ArgumentException("Ocean data dimensions cannot be zero.");
            }
            Logger.Log($"[OceanData] Initialized with dimensions T:{SlicesTime}, X:{SlicesX}, Z:{SlicesZ}");
        }

        /// <summary>
        /// Gets the normalized displacement Vector3 (components typically in -1 to 1 range) 
        /// from the raw byte data at the given integer indices.
        /// Performs no interpolation or wrapping; indices must be valid.
        /// </summary>
        /// <param name="timeIdx">Time slice index.</param>
        /// <param name="xIdx">X slice index.</param>
        /// <param name="zIdx">Z slice index.</param>
        /// <returns>A Vector3 with normalized displacement components.</returns>
        public Vector3 GetNormalizedDisplacementAtIndices(int timeIdx, int xIdx, int zIdx)
        {
            if (timeIdx < 0 || timeIdx >= SlicesTime ||
                xIdx < 0 || xIdx >= SlicesX ||
                zIdx < 0 || zIdx >= SlicesZ)
            {
                // This should ideally be handled by the caller ensuring wrapped/clamped indices
                // or this method could implement clamping/wrapping based on a policy.
                // For raw access, an exception or a zero vector might be appropriate.
                // Logger.LogWarning($"[OceanData] Out of bounds access: T:{timeIdx}, X:{xIdx}, Z:{zIdx}");
                // Fallback for safety, actual behavior might need to be more sophisticated (e.g. clamping to edge)
                timeIdx = Math.Clamp(timeIdx, 0, SlicesTime -1);
                xIdx = Math.Clamp(xIdx, 0, SlicesX -1);
                zIdx = Math.Clamp(zIdx, 0, SlicesZ -1);
            }

            float dx = (_data[timeIdx, xIdx, zIdx, 0] * BYTE_TO_NORMALIZED_FLOAT_SCALE) + BYTE_TO_NORMALIZED_FLOAT_OFFSET;
            float dy = (_data[timeIdx, xIdx, zIdx, 1] * BYTE_TO_NORMALIZED_FLOAT_SCALE) + BYTE_TO_NORMALIZED_FLOAT_OFFSET; // Assuming Y is vertical
            float dz = (_data[timeIdx, xIdx, zIdx, 2] * BYTE_TO_NORMALIZED_FLOAT_SCALE) + BYTE_TO_NORMALIZED_FLOAT_OFFSET;

            return new Vector3(dx, dy, dz);
        }
    }
}