#if TOOLS
#nullable enable
using System.Net;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Response writers for the HTTP listener. Centralized so every endpoint emits the same
    /// <c>application/json; charset=utf-8</c> content type and the same error-envelope shape, and
    /// so a single try/catch owns the <see cref="HttpListenerResponse.Close"/> that finalizes the
    /// response to the client. Adapted from Unity's <c>BridgeHttpResponse</c>; error-envelope
    /// helpers grow here as endpoints are added (405 method_not_allowed in P2.1, 401 unauthorized
    /// in P5.2, etc.).
    /// </summary>
    internal static class BridgeHttpResponse
    {
        /// <summary>
        /// Write <paramref name="json"/> as the response body with the given HTTP status and close
        /// the response. The caller is responsible for assembling valid JSON; this method only
        /// frames it on the wire. UTF-8 encoded; content length is set so the client can read a
        /// deterministic body (chunked encoding would also work but adds a framing variable the
        /// readiness probes do not need).
        /// </summary>
        internal static void SendJson(HttpListenerContext context, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Client disconnected before we could write — the body is lost either way; the
                // HandleRequest finally will Close() the response and the listener moves on. We do
                // NOT rethrow: a half-written response is the same to the client as no response
                // once the connection drops, and a throw here would only mask the real cause.
            }
        }

        /// <summary>
        /// 404 envelope for an unknown path. The body shape (<c>{"error":{"code":"not_found",...}}</c>)
        /// matches Unity's so an MCP client reading the error sees a consistent contract across
        /// paths the bridge does not serve.
        /// </summary>
        internal static void SendNotFound(HttpListenerContext context, string path)
        {
            var json = "{\"error\":{\"code\":\"not_found\",\"message\":\"Unknown path: "
                + BridgeJson.EscapeStringContent(path) + "\"}}";
            SendJson(context, 404, json);
        }

        /// <summary>
        /// 404 envelope for an unknown tool name on <c>POST /tools/{name}</c> (P2.1). The body
        /// shape (<c>{"error":{"code":"tool_not_found",...}}</c>) matches Unity's so an MCP
        /// client reading the error sees a consistent contract across tools the bridge does not
        /// serve. Adapted from Unity's <c>SendToolNotFound</c>.
        /// </summary>
        internal static void SendToolNotFound(HttpListenerContext context, string toolName)
        {
            var json = "{\"error\":{\"code\":\"tool_not_found\",\"message\":\"Unknown tool: "
                + BridgeJson.EscapeStringContent(toolName) + "\"}}";
            SendJson(context, 404, json);
        }

        /// <summary>
        /// 405 envelope for a wrong HTTP method on a known route (P2.1). Used when a client GETs
        /// <c>/tools/{name}</c> (POST required) or POSTs <c>/ping</c> (GET required). The body
        /// shape (<c>{"error":{"code":"method_not_allowed",...}}</c>) matches Unity's. Adapted
        /// from Unity's <c>SendJsonError(context, 405, "method_not_allowed", ...)</c>.
        /// </summary>
        internal static void SendMethodNotAllowed(HttpListenerContext context, string message)
        {
            var json = "{\"error\":{\"code\":\"method_not_allowed\",\"message\":\""
                + BridgeJson.EscapeStringContent(message) + "\"}}";
            SendJson(context, 405, json);
        }

        /// <summary>
        /// 400 envelope for a malformed request body on <c>POST /tools/{name}</c> (P2.1). Used
        /// when the request body cannot be read or is structurally invalid (e.g. not a JSON
        /// object). Distinct from <c>tool_not_found</c> (the tool exists, the body is bad) and
        /// from a handler-level <c>invalid_request</c> (the body parsed but a field was wrong).
        /// Adapted from Unity's <c>SendJsonError(context, 400, "invalid_request", ...)</c>.
        /// </summary>
        internal static void SendInvalidRequest(HttpListenerContext context, string message)
        {
            var json = "{\"error\":{\"code\":\"invalid_request\",\"message\":\""
                + BridgeJson.EscapeStringContent(message) + "\"}}";
            SendJson(context, 400, json);
        }
    }
}
#endif
