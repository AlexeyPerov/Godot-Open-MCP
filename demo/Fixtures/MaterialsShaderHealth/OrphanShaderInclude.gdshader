shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Orphan-include fixture for the materials_shader_health verify rule.
// A .gdshader whose `#include "res://Fixtures/MaterialsShaderHealth/Missing.gdshaderinc"`
// directive resolves to no file on disk (and is not a uid:// include). Godot 4.x
// fails to compile a shader with an unresolved include. The rule MUST emit
// `materials_orphan_shader_include` (Warning) anchored on this file when scanned
// live. See demo/Fixtures/README.md.
#include "res://Fixtures/MaterialsShaderHealth/Missing.gdshaderinc"

void fragment() {
	ALBEDO = vec3(1.0, 0.0, 0.0);
}
