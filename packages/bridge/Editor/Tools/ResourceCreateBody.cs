#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_resource_create</c> (P4.2). Extracts the
    /// destination resource path, the optional instantiable <c>Resource</c> class name, and the
    /// optional initial-property array straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="ResourceFindBody"/> /
    /// <see cref="NodeCreateBody"/> — the bridge deliberately carries no typed JSON DOM dependency
    /// on the hot path (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// The <c>properties</c> array delegates to <see cref="ResourcePropertyPatch.ParseList"/> so the
    /// path grammar + value extraction are shared with <see cref="ResourceModifyBody"/>. Create can
    /// apply validated initial properties before the first save, avoiding a second gated call (P4.2
    /// intentional delta #3).
    /// </para>
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is
    /// unit-testable in the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal sealed class ResourceCreateBody
    {
        /// <summary>Destination <c>res://</c> path for the new <c>.tres</c>/<c>.res</c> file.
        /// Null when unset. Required.</summary>
        internal string? ResourcePath { get; private set; }

        /// <summary>Godot class name to instantiate. Defaults to <see cref="DefaultTypeClassName"/>
        /// (<c>Resource</c>) when unset. Must be an instantiable <c>Resource</c> subclass
        /// (validated by the handler via <c>ClassDB</c>).</summary>
        internal string? TypeClassName { get; private set; }

        /// <summary>Parsed initial-property patches. May be empty. Each patch's path is already
        /// classified into segments or carries a <see cref="ResourcePropertyPatch.ParseError"/>.
        /// </summary>
        internal System.Collections.Generic.List<ResourcePropertyPatch> Properties { get; private set; }
            = new System.Collections.Generic.List<ResourcePropertyPatch>();

        /// <summary>Default class name when <see cref="TypeClassName"/> is absent — the Godot base
        /// <c>Resource</c> class.</summary>
        internal const string DefaultTypeClassName = "Resource";

        /// <summary>True when <see cref="ResourcePath"/> is present.</summary>
        internal bool HasResourcePath => !string.IsNullOrWhiteSpace(ResourcePath);

        /// <summary>The effective class name to instantiate: <see cref="TypeClassName"/> when
        /// present, otherwise <see cref="DefaultTypeClassName"/>.</summary>
        internal string EffectiveTypeClassName
            => string.IsNullOrWhiteSpace(TypeClassName) ? DefaultTypeClassName : TypeClassName!;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="ResourceCreateBody"/>. Never throws — a
        /// missing or malformed field falls back to its default. Empty/null body returns an
        /// all-default instance (the handler rejects this with <c>missing_parameter</c>).
        /// </summary>
        internal static ResourceCreateBody Parse(string? body)
        {
            var parsed = new ResourceCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.ResourcePath = ExtractNullableStringValue(body, "resource_path");
            if (string.IsNullOrWhiteSpace(parsed.ResourcePath))
                parsed.ResourcePath = ExtractNullableStringValue(body, "resourcePath");

            parsed.TypeClassName = ExtractNullableStringValue(body, "type_class_name");
            if (string.IsNullOrWhiteSpace(parsed.TypeClassName))
                parsed.TypeClassName = ExtractNullableStringValue(body, "typeClassName");

            parsed.Properties = ResourcePropertyPatch.ParseList(body, "properties");
            // Accept "patches" as an alias for parity with resource_modify.
            if (parsed.Properties.Count == 0)
                parsed.Properties = ResourcePropertyPatch.ParseList(body, "patches");

            return parsed;
        }

        ResourceCreateBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors ResourceFindBody.ExtractNullableStringValue / NodeCreateBody: locate `"key"`,
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
