// Small opaque inverted hull, bounded in both pixels and world space. Uses each eye's
// projection, so live screens and VR eyes do not inherit the audience camera's thickness.
shader_type spatial;
render_mode cull_front, unshaded, shadows_disabled;
uniform vec4 outline_color : source_color = vec4(0.07, 0.07, 0.105, 1.0);
uniform float outline_pixels = 0.6;
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool has_texture = true;
uniform float alpha_scissor = 0.0;
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
void vertex() {
	float distance_to_eye = max(0.1, -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z);
	float pixel_world = 2.0 * distance_to_eye / max(1.0, VIEWPORT_SIZE.y * abs(PROJECTION_MATRIX[1][1]));
	float model_scale = max(0.001, length(MODEL_MATRIX[0].xyz));
	VERTEX += normalize(NORMAL) * min(pixel_world * outline_pixels, 0.0035) / model_scale;
}
void fragment() {
	if (alpha_scissor > 0.0 && has_texture && texture(albedo_texture, UV * uv_scale + uv_offset).a < alpha_scissor) discard;
	ALBEDO = outline_color.rgb;
}
