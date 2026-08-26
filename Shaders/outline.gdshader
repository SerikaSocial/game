// Inverted-hull outline pass.
//
// Used as the `next_pass` of a toon material (see ToonShading.cs): the mesh is drawn a second
// time, expanded along its normals and with front faces culled, so only the back-facing shell
// pokes out past the silhouette as a solid line. This is the standard, cheap anime outline —
// no post-process, no edge-detection, works on any mesh and in VR.
//
// Thickness is scaled by view distance so a line that reads as one pixel up close doesn't become
// a slab across the screen when the avatar is far away, and vice versa.
shader_type spatial;
render_mode cull_front, unshaded, depth_prepass_alpha;

uniform vec4 outline_color : source_color = vec4(0.12, 0.10, 0.16, 1.0);
uniform float outline_width : hint_range(0.0, 10.0, 0.05) = 1.4;   // in "screen-ish" units

// Alpha carried from the base material so a cutout hair card doesn't get a solid outline box
// around its transparent quad.
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool has_texture = true;
uniform float alpha_scissor : hint_range(0.0, 1.0, 0.01) = 0.0;

void vertex() {
	// Expand along the normal in view space, scaled by the vertex's distance from the camera so
	// the on-screen thickness stays roughly constant.
	vec3 n = normalize(NORMAL);
	float view_dist = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
	float scale = clamp(view_dist, 0.2, 40.0) * 0.0015 * outline_width;
	VERTEX += n * scale;
}

void fragment() {
	ALBEDO = outline_color.rgb;
	if (alpha_scissor > 0.0) {
		float a = has_texture ? texture(albedo_texture, UV).a : 1.0;
		ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
		ALPHA = a;
	}
}
