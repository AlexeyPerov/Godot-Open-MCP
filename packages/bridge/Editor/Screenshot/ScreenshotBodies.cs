#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_screenshot_viewport</c> (P4.8). Extracts the
    /// optional <c>mode</c> ("2d" / "3d", default "3d"). Pure-managed (no Godot API, no
    /// <c>#if TOOLS</c>), so the parsing logic is unit-testable in the binary-less xUnit host.
    /// Uses the same hand-rolled <c>IndexOf</c>-substring style as the other body parsers
    /// (<c>packages/bridge/AGENTS.md</c> §Transport — no typed JSON DOM on the hot path).
    /// </summary>
    internal sealed class ScreenshotViewportBody
    {
        /// <summary>
        /// Raw mode string from the body, NOT yet normalized. Empty when unset (the handler
        /// defaults to <c>"3d"</c>). The handler normalizes (trim + lower) and validates.
        /// </summary>
        internal string Mode { get; private set; } = string.Empty;

        /// <summary>True when the <c>mode</c> key was present in the body.</summary>
        internal bool HasMode { get; private set; }

        /// <summary>
        /// Parse <paramref name="body"/>. Never throws — a missing or malformed field falls back
        /// to its default. Empty/null body returns an all-default instance (mode unset → 3d).
        /// </summary>
        internal static ScreenshotViewportBody Parse(string? body)
        {
            var parsed = new ScreenshotViewportBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            var mode = ExtractStringValue(body, "mode");
            if (!string.IsNullOrEmpty(mode))
            {
                parsed.Mode = mode;
                parsed.HasMode = true;
            }
            return parsed;
        }

        ScreenshotViewportBody() { }

        // --- hand-rolled scalar extraction (same style as NodeFindBody) ----------------

        internal static string ExtractStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            return raw ?? string.Empty;
        }

        internal static string? ExtractRawValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] != '"') return null;
            return SliceQuotedString(body, start);
        }

        internal static string SliceQuotedString(string body, int start)
        {
            var sb = new System.Text.StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var next = body[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u' when i + 5 < body.Length:
                            if (int.TryParse(body.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(next); i += 2; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_screenshot_camera</c> (P4.8). Extracts the
    /// camera <c>node_ref</c> (<c>node_path</c> and/or <c>instance_id</c>) and the optional
    /// width/height. Pure-managed (no Godot API, no <c>#if TOOLS</c>).
    /// </summary>
    internal sealed class ScreenshotCameraBody
    {
        /// <summary>
        /// One node reference for the camera target. Carries <c>node_path</c> (priority 2) and/or
        /// <c>instance_id</c> (priority 1). Reuses the same precedence as
        /// <c>EditorSelectionSetBody.NodeRef</c> — instance_id wins when both are set.
        /// </summary>
        internal sealed class NodeRef
        {
            /// <summary>Godot instance id of the Node. 0 when unset. Priority 1 when non-zero.
            /// Accepts <c>instance_id</c> / <c>instanceId</c>.</summary>
            internal ulong InstanceId { get; set; }

            /// <summary>True when <see cref="InstanceId"/> was explicitly set (non-zero).</summary>
            internal bool HasInstanceId => InstanceId != 0;

            /// <summary>Scene-tree path. Empty when unset. Priority 2. Accepts
            /// <c>node_path</c> / <c>nodePath</c>.</summary>
            internal string NodePath { get; set; } = string.Empty;

            /// <summary>True when <see cref="NodePath"/> was explicitly set (non-empty).</summary>
            internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

            /// <summary>True when neither field is set — the handler rejects this with
            /// <c>node_not_found</c>.</summary>
            internal bool IsEmpty => !HasInstanceId && !HasNodePath;
        }

        /// <summary>The camera node reference. May be empty when the caller omitted both fields
        /// (handler surfaces <c>node_not_found</c>).</summary>
        internal NodeRef Camera { get; private set; } = new();

        /// <summary>Requested width (px). Defaults to <c>1920</c> when unset. Validated by
        /// <see cref="Runtime.Screenshot.ScreenshotMath.ValidateDimensions"/> in the handler.</summary>
        internal int Width { get; private set; } = 1920;

        /// <summary>Requested height (px). Defaults to <c>1080</c> when unset.</summary>
        internal int Height { get; private set; } = 1080;

        /// <summary>True when <c>width</c> was explicitly present.</summary>
        internal bool HasWidth { get; private set; }

        /// <summary>True when <c>height</c> was explicitly present.</summary>
        internal bool HasHeight { get; private set; }

        internal static ScreenshotCameraBody Parse(string? body)
        {
            var parsed = new ScreenshotCameraBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            // node_ref may be a nested object { node_path, instance_id } OR the fields may be
            // inlined at the top level. Support both — extract from the node_ref slice first,
            // then fall back to top-level keys.
            var nodeRefSlice = ExtractObjectSlice(body, "node_ref");
            var searchBody = nodeRefSlice ?? body;

            var id = ExtractUlong(searchBody, "instance_id") ?? ExtractUlong(searchBody, "instanceId");
            if (id.HasValue) parsed.Camera.InstanceId = id.Value;
            var path = ExtractStringValue(searchBody, "node_path");
            if (string.IsNullOrEmpty(path)) path = ExtractStringValue(searchBody, "nodePath");
            if (!string.IsNullOrEmpty(path)) parsed.Camera.NodePath = path;

            var w = ExtractInt(body, "width", 1920);
            var h = ExtractInt(body, "height", 1080);
            if (HasKey(body, "width")) { parsed.Width = w; parsed.HasWidth = true; }
            if (HasKey(body, "height")) { parsed.Height = h; parsed.HasHeight = true; }
            return parsed;
        }

        ScreenshotCameraBody() { }

        internal static bool HasKey(string body, string key) =>
            body.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Slice the object value for <paramref name="key"/> (a <c>{...}</c> substring). Returns
        /// null when the key is absent or the value is not an object. Reuses the brace-matching
        /// walk from <c>EditorSelectionSetBody</c>.
        /// </summary>
        internal static string? ExtractObjectSlice(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length || body[start] != '{') return null;

            int i = start;
            int depth = 0;
            while (i < body.Length)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}')
                {
                    depth--;
                    if (depth == 0) { i++; break; }
                }
                i++;
            }
            return body.Substring(start, i - start);
        }

        internal static int ExtractInt(string body, string key, int defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && char.IsDigit(body[end])) end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return defaultValue;
            return int.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        internal static ulong? ExtractUlong(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && char.IsDigit(body[end])) end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return null;
            return ulong.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        internal static string ExtractStringValue(string body, string key) =>
            ScreenshotViewportBody.ExtractStringValue(body, key);
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_screenshot_isolated</c> (P4.8). Extracts the
    /// target <c>node_ref</c>, the camera view, background mode/color, optics, padding, and square
    /// resolution. Pure-managed (no Godot API, no <c>#if TOOLS</c>).
    /// </summary>
    internal sealed class ScreenshotIsolatedBody
    {
        /// <summary>One node reference for the isolated target (a Node3D). Same shape and
        /// precedence as <see cref="ScreenshotCameraBody.NodeRef"/>.</summary>
        internal sealed class NodeRef
        {
            internal ulong InstanceId { get; set; }
            internal bool HasInstanceId => InstanceId != 0;
            internal string NodePath { get; set; } = string.Empty;
            internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
            internal bool IsEmpty => !HasInstanceId && !HasNodePath;
        }

        /// <summary>The target node reference. May be empty (handler surfaces
        /// <c>node_not_found</c>).</summary>
        internal NodeRef Target { get; private set; } = new();

        /// <summary>Raw camera view string. Default <c>"front"</c>. Validated by the handler via
        /// <see cref="Runtime.Screenshot.ScreenshotMath.TryParseView"/>.</summary>
        internal string CameraView { get; private set; } = "front";

        /// <summary>Raw background mode string: <c>"solid_color"</c> or <c>"transparent"</c>.
        /// Default <c>"solid_color"</c>.</summary>
        internal string Background { get; private set; } = "solid_color";

        /// <summary>Raw background color hex (e.g. <c>#404040</c>). Default <c>#404040</c>.
        /// Parsed by <see cref="Runtime.Screenshot.ScreenshotMath.TryParseHtmlColor"/>.</summary>
        internal string BackgroundColor { get; private set; } = "#404040";

        /// <summary>Field of view (degrees). Default 60.</summary>
        internal float FieldOfView { get; private set; } = 60f;

        /// <summary>Near clip plane (m). Default 0.05.</summary>
        internal float NearClipPlane { get; private set; } = 0.05f;

        /// <summary>Far clip plane (m). Default 4000.</summary>
        internal float FarClipPlane { get; private set; } = 4000f;

        /// <summary>Framing padding multiplier. Default 1.2.</summary>
        internal float Padding { get; private set; } = 1.2f;

        /// <summary>Square output resolution (px). Default 512.</summary>
        internal int Resolution { get; private set; } = 512;

        internal static ScreenshotIsolatedBody Parse(string? body)
        {
            var parsed = new ScreenshotIsolatedBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            var nodeRefSlice = ScreenshotCameraBody.ExtractObjectSlice(body, "node_ref");
            var searchBody = nodeRefSlice ?? body;

            var id = ScreenshotCameraBody.ExtractUlong(searchBody, "instance_id")
                     ?? ScreenshotCameraBody.ExtractUlong(searchBody, "instanceId");
            if (id.HasValue) parsed.Target.InstanceId = id.Value;
            var path = ScreenshotViewportBody.ExtractStringValue(searchBody, "node_path");
            if (string.IsNullOrEmpty(path))
                path = ScreenshotViewportBody.ExtractStringValue(searchBody, "nodePath");
            if (!string.IsNullOrEmpty(path)) parsed.Target.NodePath = path;

            var camView = ScreenshotViewportBody.ExtractStringValue(body, "camera_view");
            if (!string.IsNullOrEmpty(camView)) parsed.CameraView = camView;

            var bg = ScreenshotViewportBody.ExtractStringValue(body, "background");
            if (!string.IsNullOrEmpty(bg)) parsed.Background = bg;

            var bgColor = ScreenshotViewportBody.ExtractStringValue(body, "background_color");
            if (string.IsNullOrEmpty(bgColor))
                bgColor = ScreenshotViewportBody.ExtractStringValue(body, "backgroundColor");
            if (!string.IsNullOrEmpty(bgColor)) parsed.BackgroundColor = bgColor;

            parsed.FieldOfView = ExtractFloat(body, "field_of_view", 60f);
            parsed.NearClipPlane = ExtractFloat(body, "near_clip_plane", 0.05f);
            parsed.FarClipPlane = ExtractFloat(body, "far_clip_plane", 4000f);
            parsed.Padding = ExtractFloat(body, "padding", 1.2f);
            parsed.Resolution = ScreenshotCameraBody.ExtractInt(body, "resolution", 512);
            return parsed;
        }

        ScreenshotIsolatedBody() { }

        internal static float ExtractFloat(string body, string key, float defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            int end = start;
            if (end < body.Length && (body[end] == '-' || body[end] == '+')) end++;
            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '.' || body[end] == 'e' || body[end] == 'E'))
                end++;
            var token = body.AsSpan(start, end - start).Trim();
            if (token.Length == 0) return defaultValue;
            return float.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }
    }
}
