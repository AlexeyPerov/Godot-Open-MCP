# Healthy demo fixture resource — a minimal custom Godot `Resource` carrying
# deterministic scalar/vector/color/array values that an offline read or a
# `resource_get_data` round-trip can pin. See demo/README.md.
#
# Part of the Godot Open MCP demo integration fixture (P9.4). NOT shipped in
# the addon — lives under demo/Resources/.
@tool
extends Resource
class_name DemoData

@export var title: String = "godot-open-mcp-demo-data"
@export var count: int = 7
@export var ratio: float = 1.5
@export var enabled: bool = true
@export var tint: Color = Color(0.2, 0.4, 0.6, 1.0)
@export var position: Vector2 = Vector2(12, 34)
@export var tags: PackedStringArray = PackedStringArray(["demo", "fixture"])
