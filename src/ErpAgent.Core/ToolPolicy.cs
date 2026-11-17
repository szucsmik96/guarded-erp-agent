using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ErpAgent.Core;

public static class ToolPolicy
{
    /// <summary>
    /// Fail closed: only MCP tools the server explicitly annotates as read-only run without a human.
    /// A missing annotation, a tool nobody classified, or a non-MCP function always needs approval.
    /// </summary>
    /// <remarks>
    /// Annotations are hints from the server. That is fine for a server you own; for third-party
    /// servers, pair this with a local allow-list (see roadmap).
    /// </remarks>
    public static bool RequiresApproval(AIFunction tool) =>
        tool is not McpClientTool mcp || mcp.ProtocolTool.Annotations?.ReadOnlyHint != true;
}
