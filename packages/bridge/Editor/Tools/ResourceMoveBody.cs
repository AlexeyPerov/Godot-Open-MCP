#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_resource_move</c> (P4.3). Extracts the source and
    /// destination <c>res://</c> paths straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="ResourceCreateBody"/> /
    /// <see cref="ResourceModifyBody"/> — the bridge deliberately carries no typed JSON DOM dependency
    /// on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ResourceMoveBody
    {
        /// <summary>Source <c>res://</c> path (or <c>uid://</c>) of the resource to move. Null when
        /// unset. Required.</summary>
        internal string? SourcePath { get; private set; }

        /// <summary>Destination <c>res://</c> path for the resource file. Null when unset.
        /// Required. Must not already exist.</summary>
        internal string? DestinationPath { get; private set; }

        /// <summary>True when <see cref="SourcePath"/> is present.</summary>
        internal bool HasSourcePath => !string.IsNullOrWhiteSpace(SourcePath);

        /// <summary>True when <see cref="DestinationPath"/> is present.</summary>
        internal bool HasDestinationPath => !string.IsNullOrWhiteSpace(DestinationPath);

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ResourceMoveBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler rejects this with <c>missing_parameter</c>).
        /// </summary>
        internal static ResourceMoveBody Parse(string? body)
        {
            var parsed = new ResourceMoveBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.SourcePath = ExtractNullableStringValue(body, "source_path");
            if (string.IsNullOrWhiteSpace(parsed.SourcePath))
                parsed.SourcePath = ExtractNullableStringValue(body, "sourcePath");

            parsed.DestinationPath = ExtractNullableStringValue(body, "destination_path");
            if (string.IsNullOrWhiteSpace(parsed.DestinationPath))
                parsed.DestinationPath = ExtractNullableStringValue(body, "destinationPath");

            return parsed;
        }

        ResourceMoveBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors ResourceCreateBody.ExtractNullableStringValue / NodeCreateBody: locate `"key"`,
        // walk past the colon, read the scalar. Strings are unwrapped from quotes and unescaped; a
        // field present-but-null is treated as absent. Duplicated per the bridge's no-typed-JSON
        // convention (isolated parsers mean a bug in one cannot regress another).

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
    }
}
