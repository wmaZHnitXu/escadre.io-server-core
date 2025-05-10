// File: Scripts/Server/Core/Visibility/IClientView.cs
using Core.Primitives; // Vector3

namespace Core.Visibility
{
    /// <summary>
    /// Represents a client's view properties relevant for visibility determination on the server.
    /// </summary>
    public interface IClientView
    {
        /// <summary>
        /// Unique identifier for the client connection.
        /// </summary>
        int ClientId { get; }

        /// <summary>
        /// Current position of the client's view in the world.
        /// </summary>
        Vector3 Position { get; }

        /// <summary>
        /// Radius or extent defining the client's area of interest.
        /// Entities within this range might be considered visible.
        /// </summary>
        float RadiusOfInterest { get; }

        // Optional: Could add Orientation/Frustum later for more precise culling
    }
}