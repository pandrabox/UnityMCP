using System.Collections.Generic;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Describes the idempotency of a single sub-action of an IMcpCommandHandler.
    /// Used by /health to publish per-action classification for mixed handlers
    /// (e.g. console.getLogs=safe vs console.clear=unsafe).
    /// </summary>
    public readonly struct McpHandlerAction
    {
        /// <summary>
        /// The action name as received by <see cref="IMcpCommandHandler.Execute"/>
        /// (e.g. "getLogs", "clear"). Case-sensitivity follows the handler's own
        /// dispatch logic; /health publishes it verbatim.
        /// </summary>
        public string Action { get; }

        /// <summary>Per-action idempotency classification.</summary>
        public McpIdempotency Idempotency { get; }

        public McpHandlerAction(string action, McpIdempotency idempotency)
        {
            this.Action = action;
            this.Idempotency = idempotency;
        }
    }

    /// <summary>
    /// Defines the contract for command handlers that process MCP requests.
    /// </summary>
    public interface IMcpCommandHandler
    {
        /// <summary>
        /// Gets the name of the command prefix this handler is responsible for.
        /// </summary>
        string CommandPrefix { get; }

        /// <summary>
        /// Gets a description of the command handler.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the idempotency classification for this handler.
        /// Handlers with mixed safe/unsafe actions should return Unsafe (conservative default)
        /// and additionally declare per-action granularity via <see cref="Actions"/>.
        /// </summary>
        McpIdempotency Idempotency { get; }

        /// <summary>
        /// Optional per-action idempotency overrides. When non-null and non-empty,
        /// <c>/health</c> emits one entry per action (named
        /// <c>/command:&lt;prefix&gt;.&lt;action&gt;</c>) in place of the class-level
        /// <c>/command:&lt;prefix&gt;</c> entry. Return an empty list or <c>null</c>
        /// to fall back to <see cref="Idempotency"/>.
        /// </summary>
        IReadOnlyList<McpHandlerAction> Actions => null;

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute within this command prefix.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the command result.</returns>
        /// <exception cref="System.ArgumentException">Thrown when action or parameters are invalid.</exception>
        JObject Execute(string action, JObject parameters);
    }
}
