#nullable enable
using System;
using System.Globalization;
using System.Text;
using GodotOpenMcp.Bridge.Runtime.Screenshot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// The bridge image envelope (P4.8). A pure-managed builder that assembles the JSON the three
    /// screenshot tool handlers return on success. The envelope carries the base64 PNG under
    /// <c>data</c> alongside structured metadata (media type, dimensions, byte length, caption,
    /// capture mode, clamped flag). The MCP server's <c>live-client.ts</c> detects the
    /// <c>image/png</c> media type and converts this envelope into an MCP image content block plus
    /// a short text metadata block — the base64 payload never appears inside a text JSON block on
    /// the success path.
    ///
    /// <para>
    /// <b>Fidelity: copy.</b> The envelope shape mirrors Unity Open MCP's
    /// <c>CaptureInlineTool.BuildSuccessJson</c> (<c>mediaType</c> + base64 <c>data</c> + metadata
    /// fields), adapted to the Godot tool's three capture modes and named fields per the execution
    /// plan's "Shared image contract". Error responses stay structured text errors via
    /// <see cref="ToolDispatchResult.Fail(string, string)"/> — this builder is success-only.
    /// </para>
    /// </summary>
    internal static class ScreenshotResult
    {
        /// <summary>The MCP image media type every screenshot tool produces.</summary>
        public const string MediaType = "image/png";

        /// <summary>
        /// Build the success envelope JSON for a screenshot capture. <paramref name="base64Png"/>
        /// is the already-base64-encoded PNG (never the raw bytes — the caller encodes). The
        /// metadata fields describe what was captured without duplicating the payload.
        /// </summary>
        /// <param name="base64Png">Base64-encoded PNG string (non-empty).</param>
        /// <param name="width">Final encoded image width (px, post-clamp).</param>
        /// <param name="height">Final encoded image height (px, post-clamp).</param>
        /// <param name="byteLength">Raw PNG byte length (pre-base64).</param>
        /// <param name="mode">Capture mode: <c>viewport</c>, <c>camera</c>, or <c>isolated</c>.</param>
        /// <param name="caption">Human-readable one-line caption for the image.</param>
        /// <param name="clamped">True when the encoded dimensions differ from the requested
        /// dimensions (transport-limit downscale was applied).</param>
        /// <param name="source">Optional source descriptor (node path, camera name, or viewport
        /// mode). Serialized as a string when non-null.</param>
        /// <param name="extraMetadata">Optional callback to append extra metadata fields (e.g.
        /// computed bounds for isolated capture) as raw JSON key-value pairs (without the
        /// enclosing object). Invoked after the standard fields; should NOT include a leading
        /// comma. May be null.</param>
        /// <returns>The JSON string for the envelope, ready for
        /// <see cref="ToolDispatchResult.Ok(string?)"/>.</returns>
        internal static string BuildImageSuccess(
            string base64Png,
            int width,
            int height,
            int byteLength,
            string mode,
            string caption,
            bool clamped,
            string? source,
            Action<StringBuilder>? extraMetadata = null)
        {
            if (string.IsNullOrEmpty(base64Png))
                throw new ArgumentException("base64Png must be non-empty", nameof(base64Png));

            var sb = new StringBuilder(base64Png.Length + 512);
            sb.Append('{');
            sb.Append("\"mediaType\":\"").Append(MediaType).Append("\",");
            sb.Append("\"data\":\"").Append(base64Png).Append("\",");
            sb.Append("\"width\":").Append(width.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"height\":").Append(height.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"byteLength\":").Append(byteLength.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"mode\":").Append(EscapeJsonString(mode)).Append(',');
            sb.Append("\"caption\":").Append(EscapeJsonString(caption)).Append(',');
            sb.Append("\"clamped\":").Append(clamped ? "true" : "false");
            if (!string.IsNullOrEmpty(source))
            {
                sb.Append(",\"source\":").Append(EscapeJsonString(source));
            }
            if (extraMetadata != null)
            {
                // The callback appends its own leading commas for each extra field.
                extraMetadata(sb);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Escape a string for inclusion in a JSON string literal (including the surrounding
        /// quotes). Mirrors the inline escaper in <c>EditorSelectionData</c> /
        /// <c>CaptureInlineTool</c> — no dependency on the editor-only <c>BridgeJson</c>.
        /// </summary>
        internal static string EscapeJsonString(string? s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
