# Godot Open MCP — global engine log sink (P18.3, Godot 4.5+ ONLY).
#
# This script subclasses Godot's `Logger` class (introduced in Godot 4.5 via PR #91006) and is
# registered with `OS.add_logger()` at runtime by the bridge's C# `GlobalLogHook`. It forwards every
# `print` / `push_error` / `push_warning` line the engine emits into the C# log collector so
# `godot_open_mcp_console_get_logs` returns the full editor Output, not just the addon's own
# activity.
#
# DELIBERATELY HAS NO `class_name`:
#   Godot 4.3 (the addon's compile floor) does not know the `Logger` type. If this file declared a
#   `class_name`, the editor's startup script-class scan would parse it on 4.3 and emit a parse
#   error. Without `class_name`, the file is invisible to the scanner and is parsed ONLY when it is
#   loaded — which the C# installer does exclusively on 4.5+ (`GD.Load` + version gate). So on 4.3
#   this file is never parsed, never loaded, and never registered; behavior is exactly the pre-P18.3
#   addon-only capture.
#
# GDScript ↔ C# interop:
#   The C# installer creates an `EngineLogSink : RefCounted` instance and assigns it to `sink`
#   before registering this logger. `EngineLogSink`'s public C# methods (`OnMessage`/`OnError`) are
#   auto-exposed to GDScript as `on_message`/`on_error` (Godot bridges PascalCase → snake_case for
#   user-defined C# methods on GodotObject/RefCounted subclasses). If `sink` is null (disarmed), the
#   overrides short-circuit — the logger stays registered with no effect.
#
# Threading:
#   Godot invokes `_log_message` / `_log_error` from arbitrary threads (documented). The C# sink
#   appends to a lock-guarded collector, so these overrides need no mutex on the GDScript side.
#   Per the Godot docs, do NOT call `print()` / `push_error()` / `push_warning()` inside these
#   overrides — that would recurse infinitely. They only call back into the C# sink.
extends Logger

# Set by the C# installer (GlobalLogHook) before registration. Null after disarm → overrides
# become no-ops (the logger stays registered but does nothing).
var sink: RefCounted = null


func _log_message(message: String, error: bool) -> void:
	if sink != null:
		sink.on_message(message, error)


func _log_error(
		function: String,
		file: String,
		line: int,
		code: String,
		rationale: String,
		editor_notify: bool,
		error_type: int,
		script_backtraces: Array
) -> void:
	if sink != null:
		sink.on_error(rationale, error_type)
