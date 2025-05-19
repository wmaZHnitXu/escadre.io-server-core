// File: Core/Time/IClock.cs
namespace Core.Time
{
    /// <summary>
    /// Provides an abstraction for accessing current time.
    /// </summary>
    public interface IClock
    {
        /// <summary>
        /// Gets the current time, typically in seconds since the start of the application or session.
        /// On the client, this should be a synchronized time with the server.
        /// On the server, this is the authoritative server time.
        /// </summary>
        float CurrentTime { get; }

        // DeltaTime is usually provided by the game loop directly to Update methods,
        // so it might not be strictly necessary here unless the clock also tracks its own deltas.
        // For now, focusing on CurrentTime.
    }
}