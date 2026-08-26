shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque;

uniform vec4 portal_color : source_color = vec4(0.49, 0.23, 0.93, 1.0);
uniform vec4 glow_color : source_color = vec4(0.67, 0.55, 1.0, 1.0);

varying vec2 v_uv;

void vertex() {
	v_uv = UV;
}

void fragment() {
	vec2 uv = v_uv - 0.5;
	float dist = length(uv);

	// Discard everything outside the unit circle — makes the rectangular plane an oval
	if (dist > 0.5) discard;

	// Flat purple fill, like a PNG
	vec3 col = portal_color.rgb * 0.4;

	// Bright rim glow at the edge
	float rim = smoothstep(0.42, 0.5, dist);
	col += glow_color.rgb * rim * 1.5;

	// Slight inner glow
	col += glow_color.rgb * smoothstep(0.35, 0.0, dist) * 0.15;

	ALBEDO = col;
	ALPHA = 1.0;
	EMISSION = col * 1.2;
}
