#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_editor_application_set_state</c> (P4.5). Extracts
    /// <c>is_playing</c> (boolean), <c>scene</c> (the selector — <c>main</c> / <c>current</c> / a
    /// <c>res://...tscn</c> path), and a bounded <c>timeout_ms</c> straight off the raw JSON body
    /// using the same hand-rolled <c>IndexOf</c>-substring style as
    /// <see cref="FileSystemReimportBody"/> / <see cref="ResourceMoveBody"/> — the bridge deliberately
    /// carries no typed JSON DOM dependency on the hot path (<c>packages/bridge/AGENTS.md</c>
    /// §Transport).
    ///
    /// <para>
    /// <b>Selector semantics.</b> <c>scene</c> resolves as follows (only meaningful when
    /// <see cref="IsPlaying"/> is true):
    /// <list type="bullet">
    /// <item><description><c>"main"</c> (default) → <c>EditorInterface.PlayMainScene</c>.</description></item>
    /// <item><description><c>"current"</c> → <c>EditorInterface.PlayCurrentScene</c> (requires an edited scene).</description></item>
    /// <item><description>a <c>res://</c> <c>.tscn</c>/<c>.scn</c> path → <c>EditorInterface.PlayCustomScene</c>.</description></item>
    /// </list>
    /// The handler owns the selector resolution + validation (it touches the editor). This parser
    /// owns only the raw field extraction + the timeout clamp.
    /// </para>
    ///
    /// <para>
    /// The handler reads <c>paths_hint</c>/<c>gate</c> via <see cref="BridgeRequestBody"/> (the
    /// dispatcher-level scalars); this parser owns only the set-state-specific fields.
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class EditorApplicationSetStateBody
    {
        /// <summary>True to start a play process; false to stop. Defaults to <c>false</c> (stop) when
        /// absent — a stop-while-stopped is an idempotent no-op success.</summary>
        internal bool IsPlaying { get; private set; }

        /// <summary>True when the <c>is_playing</c> field was explicitly present in the body. Used to
        /// distinguish "omitted → default stop" from an explicit value. An explicit false is a
        /// legitimate stop request.</summary>
        internal bool HasIsPlayingField { get; private set; }

        /// <summary>Scene selector: <c>"main"</c> (default), <c>"current"</c>, or a <c>res://</c>
        /// scene path. Null when unset (the handler treats null as <c>"main"</c> when starting).
        /// Ignored when stopping.</summary>
        internal string? Scene { get; private set; }

        /// <summary>True when <see cref="Scene"/> is present.</summary>
        internal bool HasScene => !string.IsNullOrWhiteSpace(Scene);

        /// <summary>Bounded state-transition timeout in milliseconds. Defaults to
        /// <see cref="DefaultTimeoutMs"/>; clamped to
        /// [<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>]. Caps the observation loop that
        /// waits for the play process to start (or stop) before surfacing
        /// <c>state_transition_timeout</c>.</summary>
        internal int TimeoutMs { get; private set; } = DefaultTimeoutMs;

        /// <summary>Default transition timeout when the caller omits <c>timeout_ms</c>.</summary>
        internal const int DefaultTimeoutMs = 5_000;

        /// <summary>Minimum clamped timeout. A sub-second value is clamped up so the play process is
        /// given a genuine chance to be observed.</summary>
        internal const int MinTimeoutMs = 1_000;

        /// <summary>Maximum clamped timeout. Caps a runaway caller value so a wedged start/stop
        /// surfaces as <c>settled:false</c> within a bounded wait rather than hanging the
        /// dispatch.</summary>
        internal const int MaxTimeoutMs = 60_000;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="EditorApplicationSetStateBody"/>. Never
        /// throws — a missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (stop, default timeout — the handler treats a stop-while-stopped as an
        /// idempotent success).
        /// </summary>
        internal static EditorApplicationSetStateBody Parse(string? body)
        {
            var parsed = new EditorApplicationSetStateBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            // is_playing — accept both snake_case and camelCase.
            parsed.HasIsPlayingField = TryExtractBool(body, "is_playing", out var isPlayingSnake);
            if (parsed.HasIsPlayingField)
            {
                parsed.IsPlaying = isPlayingSnake;
            }
            else if (TryExtractBool(body, "isPlaying", out var isPlayingCamel))
            {
                parsed.HasIsPlayingField = true;
                parsed.IsPlaying = isPlayingCamel;
            }

            // scene — string selector.
            parsed.Scene = ExtractNullableStringValue(body, "scene");
            if (string.IsNullOrWhiteSpace(parsed.Scene))
                parsed.Scene = ExtractNullableStringValue(body, "scenePath");

            // timeout_ms — bounded.
            var raw = ExtractIntValue(body, "timeout_ms", int.MinValue);
            if (raw == int.MinValue)
                raw = ExtractIntValue(body, "timeoutMs", DefaultTimeoutMs);
            parsed.TimeoutMs = Math.Clamp(raw, MinTimeoutMs, MaxTimeoutMs);

            return parsed;
        }

        EditorApplicationSetStateBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors ResourceMoveBody / FileSystemReimportBody: locate `"key"`, walk past the colon, read
        // the scalar. Duplicated per the bridge's no-typed-JSON convention (isolated parsers mean a bug
        // in one cannot regress another).

        static bool TryExtractBool(string body, string key, out bool value)
        {
            value = false;
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return false;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return false;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return false;
            // "true" / "false" literals only — anything else is treated as absent.
            if (start + 4 <= body.Length && body.Substring(start, 4) == "true")
            {
                value = true;
                return true;
            }
            if (start + 5 <= body.Length && body.Substring(start, 5) == "false")
            {
                value = false;
                return true;
            }
            return false;
        }

        static string? ExtractNullableStringValue(string body, string key)
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
            if (body[start] == '"')
                return SliceQuotedString(body, start);
            return null;
        }

        static string SliceQuotedString(string body, int start)
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

        static int ExtractIntValue(string body, string key, int defaultValue)
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
    }
}
