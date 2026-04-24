namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Classifies whether a command or endpoint can be safely retried on connection failure.
    /// </summary>
    public enum McpIdempotency
    {
        /// <summary>
        /// The operation has no side effects and can be retried freely.
        /// Read-only queries fall into this category.
        /// </summary>
        Safe,

        /// <summary>
        /// The operation may have side effects and must not be retried automatically
        /// after a post-handshake connection failure.
        /// </summary>
        Unsafe
    }
}
