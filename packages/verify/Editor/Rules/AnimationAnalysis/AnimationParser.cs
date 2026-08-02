#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.AnimationAnalysis
{
    // -----------------------------------------------------------------------
    // Parsed-model types
    // -----------------------------------------------------------------------

    /// <summary>
    /// One clip a player references by library + name, as parsed offline from a <c>.tres</c>
    /// <c>AnimationPlayer</c>. Godot serializes a player's libraries as <c>"&lt;libname&gt;" = { ... }</c>
    /// blocks (or as <c>[ext_resource]</c> references to an <c>AnimationLibrary</c> <c>.tres</c>); the
    /// parser extracts the library name and whether the reference is a <c>SubResource</c>/<c>ExtResource</c>
    /// id (resolved against the project tree for <c>missing_clip</c>), plus the clip names inside each.
    /// </summary>
    internal sealed class PlayerClipRef
    {
        /// <summary>The <c>res://</c> path of the AnimationPlayer <c>.tres</c> being scanned.</summary>
        public string PlayerPath { get; }
        /// <summary>The library name (Godot uses "" for the default library).</summary>
        public string Library { get; }
        /// <summary>The clip name inside the library.</summary>
        public string Clip { get; }
        /// <summary>The ExtResource("id") the library reference uses, when the library is an external file.</summary>
        public string? LibraryUsageId { get; }
        /// <summary>The declared path of that ext_resource, when known (for resolution).</summary>
        public string? LibraryPath { get; }
        /// <summary>The declared uid of that ext_resource, when known.</summary>
        public string? LibraryUid { get; }

        public PlayerClipRef(string playerPath, string library, string clip,
            string? libraryUsageId, string? libraryPath, string? libraryUid)
        {
            PlayerPath = playerPath;
            Library = library;
            Clip = clip;
            LibraryUsageId = libraryUsageId;
            LibraryPath = libraryPath;
            LibraryUid = libraryUid;
        }
    }

    /// <summary>
    /// An <c>Animation</c> clip resource parsed from a <c>.tres</c>, for the <c>empty_clip</c> /
    /// <c>duplicate_clip</c> signals. Godot serializes an <c>Animation</c> as a <c>[sub_resource]</c> with
    /// a <c>tracks</c> array; <see cref="TrackCount"/> is the count of <c>[sub_resource]</c> track entries
    /// (each track is itself a nested sub-resource).
    /// </summary>
    internal sealed class ClipData
    {
        public string ClipPath { get; }      // the .tres path the clip lives in
        public string? ClipName { get; }     // resource name when present
        public int TrackCount { get; set; }
        /// <summary>
        /// A stable, normalized fingerprint of the clip's serialized body (the lines between its
        /// <c>[sub_resource]</c> header and the next section), used for <c>duplicate_clip</c>. Two clips
        /// with identical bodies are duplicates. Null when the body could not be captured.
        /// </summary>
        public string? Fingerprint { get; set; }

        public ClipData(string clipPath, string? clipName)
        {
            ClipPath = clipPath;
            ClipName = clipName;
        }
    }

    /// <summary>
    /// An <c>AnimationNodeStateMachine</c> parsed from a <c>.tres</c>, for the <c>unreachable_state</c>
    /// and <c>parameter_mismatch</c> signals. Godot serializes a state machine as a
    /// <c>[sub_resource type="AnimationNodeStateMachine"]</c> whose body lists states (by name → node) and
    /// transitions (from → to, each carrying an optional <c>advance_condition</c> / <c>advance_expression</c>).
    /// </summary>
    internal sealed class StateMachineData
    {
        public string MachinePath { get; }
        /// <summary>The state names declared in the machine (the keys of the <c>states</c> array). Excludes the synthetic Start/End.</summary>
        public List<string> States { get; } = new();
        /// <summary>Edges: (from, to) state names. From may be the synthetic "Start" (Godot's entry state).</summary>
        public List<(string From, string To)> Transitions { get; } = new();
        /// <summary>The machine's start/entry state name, when Godot serialized one (the transition from "Start" target, or an explicit start).</summary>
        public string? StartState { get; set; }
        /// <summary>Parameter names referenced by transition <c>advance_condition</c>s (the blackboard inputs the machine expects).</summary>
        public List<string> ReferencedParameters { get; } = new();

        public StateMachineData(string machinePath) { MachinePath = machinePath; }
    }

    // -----------------------------------------------------------------------
    // Parser
    // -----------------------------------------------------------------------

    /// <summary>
    /// Line-oriented text parser for the <c>animation_analysis</c> rule. Pure-managed, no Godot API
    /// surface — same binary-less-test discipline as <see cref="MaterialsShaderHealth.MaterialsShaderParser"/>
    /// and <see cref="ScriptAudit.ScriptClassParser"/>. Adapted from Unity Open MCP's
    /// <c>AnimationAnalysis.Scanner</c> — but Unity resolves controllers/clips through
    /// <c>AssetDatabase</c> + the <c>AnimatorController</c> API, whereas Godot's <c>.tres</c> text format
    /// for <c>AnimationPlayer</c> / <c>AnimationLibrary</c> / <c>Animation</c> / <c>AnimationNodeStateMachine</c>
    /// is parseable offline.
    ///
    /// <para>
    /// <b>What this parser does</b>
    /// <list type="bullet">
    ///   <item>From an <c>AnimationPlayer</c> <c>.tres</c>: extract the library references (each
    ///     <c>"&lt;libname&gt;" = { ... }</c> block or <c>[ext_resource]</c> library) so the rule can probe
    ///     whether the referenced library/clip resolves (<c>missing_clip</c>).</item>
    ///   <item>From any <c>.tres</c>: extract every <c>[sub_resource type="Animation"]</c> with its track
    ///     count (<c>empty_clip</c>) and a body fingerprint (<c>duplicate_clip</c>).</item>
    ///   <item>From a <c>.tres</c> carrying an <c>AnimationNodeStateMachine</c>: extract the declared state
    ///     names, the transition edges (from→to), the start/entry state, and the parameters referenced by
    ///     transition <c>advance_condition</c>s.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed input.
    /// A truncated header, a stray bracket, or a body without the expected section yields whatever facts it
    /// could extract — never an exception.
    /// </para>
    /// </summary>
    internal static class AnimationParser
    {
        // ---- AnimationPlayer library references ------------------------------

        /// <summary>
        /// Parse an <c>AnimationPlayer</c> <c>.tres</c> into the library references it declares. Godot
        /// serializes the player's <c>libraries</c> dictionary in two shapes — a multi-line
        /// <c>libraries = {</c> block of <c>"&lt;name&gt;" = ExtResource("id")</c> entries, or a
        /// single-line <c>libraries = { "": ExtResource("1") }</c>. Both wrap one or more
        /// <c>[ext_resource type="AnimationLibrary"]</c> declarations. Rather than track brace depth across
        /// those shapes, this extractor first finds the <c>[ext_resource]</c> headers whose declared type is
        /// <c>AnimationLibrary</c>, then scans the body for any <c>ExtResource("id")</c> token whose id
        /// matches such a header — that is exactly the set of library references the player carries. Never
        /// throws.
        /// </summary>
        public static List<PlayerClipRef> ParsePlayerLibraries(
            string? text, string playerPath,
            IReadOnlyDictionary<string, (string? Path, string? Uid, string? Type)> extById)
        {
            var refs = new List<PlayerClipRef>();
            if (string.IsNullOrEmpty(text)) return refs;

            var lines = text!.Split('\n');

            // First pass: which ext_resource ids are AnimationLibrary declarations? (path + uid + type
            // already captured by CollectExtResources.) These are the ids a player may reference.
            var libraryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decl in extById)
            {
                if (decl.Value.Type == "AnimationLibrary")
                    libraryIds.Add(decl.Key);
            }
            if (libraryIds.Count == 0) return refs;

            // Only scan inside a `libraries = { ... }` block (the player's library dict). Track entry so we
            // do not misread ExtResource tokens that appear elsewhere (e.g. an assigned animation). The dict
            // opens with a line starting `libraries` containing `{` (multi-line) or a single-line form, and
            // closes at the matching `}`.
            var inLibraries = false;
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;

                if (!inLibraries)
                {
                    if (trimmed.StartsWith("libraries", StringComparison.Ordinal) && trimmed.Contains('{'))
                    {
                        // Single-line form: `libraries = { "": ExtResource("1") }` — the entry is on this
                        // same line. The ExtResource scan below picks it up; flip the flag so the closing
                        // brace on the same line is handled.
                        inLibraries = true;
                    }
                    else continue;
                }

                // A closing brace ends the dict. `}` may be the whole line (multi-line form) or the tail of
                // the single-line form.
                var closeIdx = trimmed.IndexOf('}');
                if (closeIdx >= 0)
                {
                    // Any ExtResource before the brace (single-line form) is still captured below.
                    inLibraries = false;
                }

                // Collect every ExtResource("id") on this line whose id is a library declaration.
                foreach (var id in ExtractAllExtResourceIds(trimmed))
                {
                    if (!libraryIds.Contains(id)) continue;
                    extById.TryGetValue(id, out var decl);
                    // Library name: the quoted key before `=` when present (multi-line form), else "".
                    var lib = ExtractQuotedKey(trimmed) ?? "";
                    refs.Add(new PlayerClipRef(playerPath, lib, clip: "",
                        libraryUsageId: id, libraryPath: decl.Path, libraryUid: decl.Uid));
                }
            }
            return refs;
        }

        // ---- Animation clips (empty + duplicate) -----------------------------

        /// <summary>
        /// Parse every <c>[sub_resource type="Animation"]</c> in a <c>.tres</c> into a <see cref="ClipData"/>.
        /// Track count = number of <c>[sub_resource type="AnimationNodeTrack"]</c>-ish track sub-resources
        /// nested under it, or — when Godot serializes tracks as inline dict entries — the count of
        /// <c>tracks/</c> array-entry markers. The body fingerprint is the section text (normalized) for
        /// <c>duplicate_clip</c>. Never throws.
        /// </summary>
        public static List<ClipData> ParseClips(string? text, string assetPath)
        {
            var clips = new List<ClipData>();
            if (string.IsNullOrEmpty(text)) return clips;

            var lines = text!.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var header = lines[i].TrimStart();
                if (header.StartsWith("[sub_resource", StringComparison.Ordinal)
                    && ExtractAttribute(header, "type") == "Animation")
                {
                    var name = ExtractAttribute(header, "resource_name");
                    var clip = new ClipData(assetPath, name);
                    var body = new List<string>();
                    // A clip's tracks are serialized as a `tracks/N/<field>` array (one track per distinct
                    // index N, with several field lines each) OR as nested track sub-resources. Count
                    // distinct track indices, not every field line.
                    var trackIndices = new HashSet<int>();
                    int nestedTrackCount = 0;
                    i++;
                    while (i < lines.Length)
                    {
                        var line = lines[i];
                        var t = line.TrimStart();
                        // The next section header ends this clip's body.
                        if (t.Length > 0 && t[0] == '[')
                        {
                            // A nested track sub-resource still belongs to this clip until a NON-sub_resource
                            // section appears. Count track-shaped sub-resources; continue collecting body.
                            if (t.StartsWith("[sub_resource", StringComparison.Ordinal))
                            {
                                nestedTrackCount++;
                                body.Add(t);
                                i++;
                                continue;
                            }
                            break;
                        }
                        // Inline track entries: `tracks/N/<field>`. Count the distinct N (one track per
                        // index), ignoring the per-track field lines (type/path/interp/...).
                        if (t.StartsWith("tracks/", StringComparison.Ordinal))
                        {
                            var (idx, _, _) = TryParseArrayEntry(t, "tracks/");
                            if (idx >= 0) trackIndices.Add(idx);
                        }
                        body.Add(t);
                        i++;
                    }
                    clip.TrackCount = nestedTrackCount + trackIndices.Count;
                    clip.Fingerprint = NormalizeBody(body);
                    clips.Add(clip);
                    continue;
                }
                i++;
            }
            return clips;
        }

        // ---- AnimationNodeStateMachine (unreachable + parameter mismatch) ----

        /// <summary>
        /// Parse a <c>.tres</c> carrying an <c>AnimationNodeStateMachine</c> into a <see cref="StateMachineData"/>.
        /// Returns the first state machine found (a <c>.tres</c> typically holds one), or null when none is
        /// present. The extractor reads:
        /// <list type="bullet">
        ///   <item><c>states/N/node</c> / <c>states/N/name</c> markers → state names (the synthetic
        ///     <c>Start</c>/<c>End</c> are recognized but not counted as user states).</item>
        ///   <item><c>transitions/N/from</c> + <c>transitions/N/to</c> markers → edges. Godot writes the
        ///     state <i>names</i> (StringName) here, not node paths.</item>
        ///   <item><c>transitions/N/advance_condition</c> + <c>advance_expression</c> markers → referenced
        ///     parameters.</item>
        ///   <item>The transition from the synthetic <c>Start</c> state → the entry/start state.</item>
        /// </list>
        /// Never throws.
        /// </summary>
        public static StateMachineData? ParseStateMachine(string? text, string assetPath)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var lines = text!.Split('\n');
            var machine = new StateMachineData(assetPath);
            bool foundMachine = false;
            var stateNames = new HashSet<string>(StringComparer.Ordinal);
            var referencedParams = new HashSet<string>(StringComparer.Ordinal);

            // The Godot array-of-dicts serialization for `states` and `transitions` writes each field as
            // `states/N/<field> = <value>` and `transitions/N/<field> = <value>`. We track per-index state
            // names so a transition's `from`/`to` (which are names, not indices) map cleanly.
            var transitionFrom = new Dictionary<int, string>(/* ordinal keys as ints */);
            var transitionTo = new Dictionary<int, string>();
            var transitionCond = new Dictionary<int, string>();

            foreach (var raw in lines)
            {
                var t = raw.TrimStart();
                if (t.Length == 0 || t.StartsWith(";")) continue;

                // Detect a state-machine resource so we know this file is relevant. Godot serializes a
                // standalone AnimationNodeStateMachine as a [gd_resource type="AnimationNodeStateMachine"]
                // root; a nested one (inside an AnimationTree blend graph) appears as a
                // [sub_resource type="AnimationNodeStateMachine"]. Accept both.
                if (!foundMachine
                    && (t.StartsWith("[sub_resource", StringComparison.Ordinal)
                        || t.StartsWith("[gd_resource", StringComparison.Ordinal))
                    && ExtractAttribute(t, "type") == "AnimationNodeStateMachine")
                {
                    foundMachine = true;
                }

                // states/N/node = "NodeName"  or  states/N/node = SubResource("...")
                {
                    var (idx, field, value) = TryParseArrayEntry(t, "states/");
                    if (idx >= 0 && field == "node" && value != null)
                    {
                        var name = Unquote(value);
                        if (!string.IsNullOrEmpty(name) && name != "Start" && name != "End"
                            && stateNames.Add(name))
                        {
                            machine.States.Add(name);
                        }
                    }
                }

                // transitions/N/<field> = <value>
                {
                    var (idx, field, value) = TryParseArrayEntry(t, "transitions/");
                    if (idx >= 0 && value != null)
                    {
                        var v = Unquote(value);
                        if (field == "from") transitionFrom[idx] = v;
                        else if (field == "to") transitionTo[idx] = v;
                        else if (field == "advance_condition" && !string.IsNullOrEmpty(v))
                            transitionCond[idx] = v;
                    }
                }
            }

            if (!foundMachine) return null;

            // Materialize the transition edges. `to` may carry a trailing "conditions/..." in some Godot
            // dev builds; we already split on whitespace above, so v is the bare name.
            foreach (var kv in transitionFrom)
            {
                var from = kv.Value;
                if (!transitionTo.TryGetValue(kv.Key, out var to)) continue;
                machine.Transitions.Add((from, to));

                // The transition out of the synthetic Start state names the entry/start state.
                if (from == "Start" && to != "End" && machine.StartState == null)
                    machine.StartState = to;
            }

            // Referenced parameters = advance_condition values (Godot rejects '/' and ':' in a condition
            // name, so these are bare parameter names).
            foreach (var kv in transitionCond)
                if (referencedParams.Add(kv.Value))
                    machine.ReferencedParameters.Add(kv.Value);

            return machine;
        }

        // ---- ext_resource collection (id → path + uid) -----------------------

        /// <summary>
        /// Build an id → (path, uid, type) map from every <c>[ext_resource path= uid= id= type=]</c> header.
        /// The <c>type</c> is kept so the player-library scan can pick out <c>AnimationLibrary</c>
        /// declarations. Mirrors <c>ScriptAudit.ScriptClassParser.CollectExtResources</c> + the type capture
        /// <c>MaterialsShaderHealth.MaterialsShaderParser</c> does.
        /// </summary>
        public static Dictionary<string, (string? Path, string? Uid, string? Type)> CollectExtResources(string? text)
        {
            var map = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return map;
            var lines = text!.Split('\n');
            foreach (var raw in lines)
            {
                if (raw.Length == 0 || raw[0] != '[') continue;
                if (!raw.StartsWith("[ext_resource", StringComparison.Ordinal)) continue;
                var id = ExtractAttribute(raw, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var path = ExtractAttribute(raw, "path");
                var uid = ExtractAttribute(raw, "uid");
                var type = ExtractAttribute(raw, "type");
                map[id!] = (
                    string.IsNullOrEmpty(path) ? null : path,
                    string.IsNullOrEmpty(uid) ? null : uid,
                    string.IsNullOrEmpty(type) ? null : type);
            }
            return map;
        }

        // ---- helpers ---------------------------------------------------------

        private static string? ExtractAttribute(string line, string key)
        {
            // Leading-space probe so `script_class` is not matched inside another attribute. Mirrors the
            // sibling parsers.
            var probe = " " + key + "=\"";
            var idx = line.IndexOf(probe, StringComparison.Ordinal);
            if (idx < 0) return null;
            var valueStart = idx + probe.Length;
            var valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd < 0) return null;
            return line.Substring(valueStart, valueEnd - valueStart);
        }

        /// <summary>Extract the first double-quoted key of a `&lt;key&gt; = ...` line (the library name in a libraries dict).</summary>
        private static string? ExtractQuotedKey(string line)
        {
            var eq = line.IndexOf('=');
            if (eq < 0) return null;
            var head = line.Substring(0, eq);
            var open = head.IndexOf('"');
            if (open < 0) return null;
            var close = head.IndexOf('"', open + 1);
            if (close < 0) return null;
            return head.Substring(open + 1, close - open - 1);
        }

        /// <summary>Extract every id from <c>ExtResource("id")</c> tokens on a line, in order.</summary>
        private static List<string> ExtractAllExtResourceIds(string line)
        {
            var ids = new List<string>();
            var needle = "ExtResource(";
            var start = 0;
            while (true)
            {
                var paren = line.IndexOf(needle, start, StringComparison.Ordinal);
                if (paren < 0) break;
                var openQuote = line.IndexOf('"', paren + needle.Length);
                if (openQuote < 0) break;
                var closeQuote = line.IndexOf('"', openQuote + 1);
                if (closeQuote < 0) break;
                var id = line.Substring(openQuote + 1, closeQuote - openQuote - 1);
                if (id.Length > 0) ids.Add(id);
                start = closeQuote + 1;
            }
            return ids;
        }

        /// <summary>
        /// Parse an array-entry line <c>prefix/N/field = value</c>. Returns <c>(index, field, rawValue)</c>;
        /// index is -1 when the line does not match. The rawValue is the substring after the first
        /// <c>=</c> (trimmed, not unquoted — callers unquote as needed).
        /// </summary>
        private static (int index, string field, string? value) TryParseArrayEntry(string line, string prefix)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                return (-1, "", null);
            var rest = line.Substring(prefix.Length);
            // rest = "N/field = value"
            var slash = rest.IndexOf('/');
            if (slash <= 0) return (-1, "", null);
            var indexStr = rest.Substring(0, slash);
            if (!int.TryParse(indexStr, out var idx)) return (-1, "", null);
            var afterSlash = rest.Substring(slash + 1);
            var eq = afterSlash.IndexOf('=');
            if (eq <= 0) return (-1, "", null);
            var field = afterSlash.Substring(0, eq).Trim();
            var value = afterSlash.Substring(eq + 1).Trim();
            return (idx, field, value.Length > 0 ? value : null);
        }

        /// <summary>Strip one layer of surrounding double-quotes; leave other forms untouched.</summary>
        private static string Unquote(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var v = value!;
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2);
            return v;
        }

        /// <summary>
        /// Normalize a clip body into a stable fingerprint for <c>duplicate_clip</c>: trim each line, drop
        /// blanks, drop the <c>resource_name</c> line (two clips differing only by name are still duplicates),
        /// join with <c>\n</c>. Null when the body is empty.
        /// </summary>
        private static string? NormalizeBody(List<string> body)
        {
            if (body.Count == 0) return null;
            var kept = new List<string>(body.Count);
            foreach (var raw in body)
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("resource_name", StringComparison.Ordinal)) continue;
                kept.Add(t);
            }
            return kept.Count == 0 ? null : string.Join("\n", kept);
        }
    }
}
