// Mirror3D shader (MIT, adapted from Joy-less/Mirror3D)
// Renders the SubViewport texture aligned to screen space for planar reflection.
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform vec4 color : source_color = vec4(0.95, 0.97, 1.0, 1.0);
uniform sampler2D mirror_texture : source_color, filter_linear_mipmap;

void fragment() {
	vec2 uv = vec2(1.0 - SCREEN_UV.x, SCREEN_UV.y);
	vec4 sample_col = texture(mirror_texture, uv);
	ALBEDO = sample_col.rgb * color.rgb;
	ALPHA = sample_col.a * color.a;
}
