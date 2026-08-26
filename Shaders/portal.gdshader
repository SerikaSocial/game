shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque;

uniform vec4 portal_color : source_color = vec4(0.49, 0.23, 0.93, 1.0);
uniform vec4 glow_color : source_color = vec4(0.67, 0.55, 1.0, 1.0);
uniform float time_scale = 1.0;
uniform float swirl_intensity = 3.0;

varying vec2 v_uv;

void vertex() {
	v_uv = UV;
}

float noise(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

float smooth_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = noise(i);
	float b = noise(i + vec2(1.0, 0.0));
	float c = noise(i + vec2(0.0, 1.0));
	float d = noise(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void fragment() {
	vec2 uv = v_uv - 0.5;
	float dist = length(uv);
	float angle = atan(uv.y, uv.x);

	// Swirling vortex effect
	float t = TIME * time_scale;
	float swirl = angle + dist * swirl_intensity - t * 2.0;
	float pattern = sin(swirl * 5.0) * 0.5 + 0.5;
	pattern *= smooth_noise(uv * 6.0 + vec2(t * 0.5, -t * 0.3));

	// Radial fade — darker at edges, bright at center
	float radial = 1.0 - smoothstep(0.0, 0.55, dist);

	// Base portal color with swirl
	vec3 col = mix(portal_color.rgb * 0.3, portal_color.rgb, pattern);
	col += glow_color.rgb * pattern * 0.4 * radial;

	// Bright center glow
	col += glow_color.rgb * smoothstep(0.4, 0.0, dist) * 0.5;

	// Edge rim — purple glow at the border
	float rim = smoothstep(0.42, 0.5, dist);
	col += glow_color.rgb * rim * 1.5;

	// Alpha: opaque in center, fades slightly at very edge
	float alpha = 1.0 - smoothstep(0.48, 0.52, dist);

	ALBEDO = col;
	ALPHA = alpha * 0.95;
	EMISSION = col * 1.5;
}
