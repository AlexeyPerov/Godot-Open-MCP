# Healthy demo fixture script — a minimal valid GDScript attached to a child
# of the main scene. Deterministic property `fixture_marker` exists so an
# offline `scene_get_data` round-trip has a stable, assertable value.
# See demo/README.md.
#
# Part of the Godot Open MCP demo integration fixture (P9.4). NOT shipped in
# the addon — lives under demo/Scripts/.
extends Node

const fixture_marker := "godot-open-mcp-demo-valid-gdscript"
