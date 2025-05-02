namespace Core.Logging
{
    /// <summary>
    /// Partial class for logging. Allows implementation outside the Core library.
    /// Provides methods for different log levels.
    /// </summary>
    public static partial class Logger
    {
        // Define the partial methods. Implementation will be provided elsewhere.
        public static partial void Log(string message);
        public static partial void LogWarning(string message);
        public static partial void LogError(string message);

        // Optional: Add methods with context/tags if needed later
        // public static partial void Log(string tag, string message);
    }
}