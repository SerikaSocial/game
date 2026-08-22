// Mirror3D shader (MIT, adapted from Joy-less/Mirror3D)
// Renders the SubViewport texture with horizontal flip and optional distortion/mask.
shader_type spatial;
render_mode unshaded;
render_mode cull_disabled;

uniform vec4 color : source_color = vec4(0.9, 0.97, 0.94, 1.0);
uniform sampler2D mirror_texture : source_color, filter_linear_mipmap;

void vertex() {
	UV.x = 1.0 - UV.x;
}

void fragment() {
	vec2 uv = FRONT_FACING ? UV : vec2(1.0 - UV.x, UV.y);
	vec4 sample_col = texture(mirror_texture, uv);
	ALBEDO = sample_col.rgb * color.rgb;
	ALPHA = sample_col.a * color.a;
}
