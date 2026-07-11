#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_resource_modify</c> (P4.2). Extracts the resource
    /// path and the non-empty <c>patches</c> array straight off the raw JSON body using the same
    /// hand-rolled <c>IndexOf</c>-substring style as <see cref="ResourceFindBody"/> /
    /// <see cref="NodeModifyBody"/> — the bridge deliberately carries no typed JSON DOM dependency
    /// on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// The <c>patches</c> array delegates to <see cref="ResourcePropertyPatch.ParseList"/> so the
    /// path grammar + value extraction are shared with <see cref="ResourceCreateBody"/>. A
    /// missing/malformed array yields an empty list (the handler rejects this with
    /// <c>missing_parameter</c>).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ResourceModifyBody
    {
        /// <summary>Canonical <c>res://</c> path (or <c>uid://</c>) of the resource to modify.
        /// Null when unset. Required.</summary>
        internal string? ResourcePath { get; private set; }

        /// <summary>Parsed patches. May be empty when the <c>patches</c> key is absent/malformed
        /// (the handler rejects this with <c>missing_parameter</c>). Each patch's path is already
        /// classified into segments or carries a <see cref="ResourcePropertyPatch.ParseError"/>.
        /// </summary>
        internal System.Collections.Generic.List<ResourcePropertyPatch> Patches { get; private set; }
            = new System.Collections.Generic.List<ResourcePropertyPatch>();

        /// <summary>Hard cap on the number of patches in a single request. Mirrors the execution
        /// plan's "remain under hard count/size limits" requirement.</summary>
        internal const int MaxPatches = 100;

        /// <summary>True when <see cref="ResourcePath"/> is present.</summary>
        internal bool HasResourcePath => !string.IsNullOrWhiteSpace(ResourcePath);

        /// <summary>True when at least one patch was parsed.</summary>
        internal bool HasPatches => Patches.Count > 0;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ResourceModifyBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler rejects this with <c>missing_parameter</c>).
        /// </summary>
        internal static ResourceModifyBody Parse(string? body)
        {
            var parsed = new ResourceModifyBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.ResourcePath = ExtractNullableStringValue(body, "resource_path");
            if (string.IsNullOrWhiteSpace(parsed.ResourcePath))
                parsed.ResourcePath = ExtractNullableStringValue(body, "resourcePath");

            // Primary key is "patches"; accept "properties" as an alias (resource_create uses it).
            parsed.Patches = ResourcePropertyPatch.ParseList(body, "patches");
            if (parsed.Patches.Count == 0)
                parsed.Patches = ResourcePropertyPatch.ParseList(body, "properties");

            // Clamp to the hard cap — extra entries are dropped (the handler sees a partial list and
            // can warn the agent).
            if (parsed.Patches.Count > MaxPatches)
                parsed.Patches = parsed.Patches.GetRange(0, MaxPatches);

            return parsed;
        }

        ResourceModifyBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors ResourceFindBody.ExtractNullableStringValue / NodeFindBody: locate `"key"`, walk
        // past the colon, read the scalar. Strings are unwrapped from quotes and unescaped; a field
        // present-but-null is treated as absent. Duplicated per the bridge's no-typed-JSON
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
