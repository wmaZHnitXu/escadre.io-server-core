namespace Core.Session
{
    /// <summary>
    /// Specifies the type of authentication or account associated with a client identity.
    /// </summary>
    public enum AuthTypeEnum : byte 
    {
        /// <summary>
        /// A temporary or guest account without persistent storage or full features.
        /// </summary>
        NoAccount = 0, 

        /// <summary>
        /// A registered user account with persistent data.
        /// </summary>
        Account = 1,
    }
    /// <summary>
    /// Contains validated identity information for a client, extracted from an authentication token (e.g., JWT).
    /// </summary>
    public struct ClientIdentity
    {
        /// <summary>
        /// The Client ID as determined from the validated token. This ID is authoritative.
        /// </summary>
        public int ClientId;
        public string Nickname;
        public bool IsAdmin;
        /// <summary>
        /// The type of authentication or account, as derived from the token.
        /// </summary>
        public AuthTypeEnum AuthType;

        public ClientIdentity(int clientId, string nickname, bool isAdmin, AuthTypeEnum authType)
        {
            ClientId = clientId;
            Nickname = nickname;
            IsAdmin = isAdmin;
            AuthType = authType;
        }
    }
}