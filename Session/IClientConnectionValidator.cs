// File: Core/Session/IClientConnectionValidator.cs
using System.IO;

namespace Core.Session
{
    /// <summary>
    /// Defines a strategy for validating client connection requests on the server,
    /// typically by validating an authentication token like a JWT.
    /// </summary>
    public interface IClientConnectionValidator
    {
        /// <summary>
        /// Validates a client's connection request based on the provided payload (expected to contain an auth token).
        /// </summary>
        /// <param name="payload">The binary payload of the connection request message, expected to contain the authentication token (e.g., JWT string).</param>
        /// <returns>A populated ClientIdentity struct if the token is valid and the client is authorized; otherwise, null.</returns>
        ClientIdentity? ValidateTokenAndGetIdentity(BinaryReader payload);
    }
}