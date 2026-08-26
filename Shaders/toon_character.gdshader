// Serika character/world toon shader.
//
// Applied at load over the flat PBR materials that VRM and PMX import as (see ToonShading.cs).
// Unlike the procedural-world `ultimate_toon.gdshader`, this one is built for *imported* meshes:
// it preserves the source albedo texture and colour, carries alpha through for the hair and
// clothing that VRM/PMX render with cutout or blend, and keeps a soft two-to-three band ramp
// with a rim light for the anime read. Outlines are a separate inverted-hull pass (outline.gdshader).
//
// `depth_prepass_alpha` gives correct sorting for the semi-transparent hair cards without the
// halos a plain blend produces; `cull_disabled` because MMD and a lot of VRM clothing is authored
// single-sided-but-viewed-from-both.
shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha, diffuse_toon, specular_disabled;

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool has_texture = true;

// Alpha: 0 = opaque, >0 = alpha-scissor cutout at that threshold. Hair cards use ~0.5.
uniform float alpha_scissor : hint_range(0.0, 1.0, 0.01) = 0.0;

// Toon ramp.
uniform float steps : hint_range(1.0, 5.0, 1.0) = 3.0;
uniform float step_smoothness : hint_range(0.01, 0.5, 0.01) = 0.08;
uniform vec4 shadow_tint : source_color = vec4(0.62, 0.58, 0.72, 1.0);
uniform float shadow_tint_amount : hint_range(0.0, 1.0, 0.05) = 0.35;

// Rim light — the bright edge that separates the silhouette from the background.
uniform vec4 rim_color : source_color = vec4(1.0, 0.98, 1.0, 1.0);
uniform float rim_amount : hint_range(0.0, 8.0, 0.1) = 2.5;
uniform float rim_smoothness : hint_range(0.0, 1.0, 0.02) = 0.25;
uniform float rim_strength : hint_range(0.0, 1.0, 0.05) = 0.35;

float fresnel(float amount, vec3 normal, vec3 view) {
	return pow(1.0 - clamp(dot(normalize(normal), normalize(view)), 0.0, 1.0), amount);
}

void fragment() {
	vec4 tex = has_texture ? texture(albedo_texture, UV) : vec4(1.0);
	vec4 col = tex * albedo_color;
	ALBEDO = col.rgb;
	ALPHA = col.a;
	if (alpha_scissor > 0.0) {
		ALPHA_SCISSOR_THRESHOLD = alpha_scissor;
	}
}

void light() {
	// Banded diffuse: quantize N·L into `steps` levels with a soft edge, so lighting reads as
	// flat cels rather than a smooth gradient.
	float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION;
	float m = ndotl * steps;
	float base = floor(m);
	float f = smoothstep(0.5 - step_smoothness, 0.5 + step_smoothness, m - base);
	float banded = (base + f) / steps;

	// Tint the shadowed side toward a cool colour instead of pure black — the classic anime
	// shadow. Lit side keeps the light's own colour.
	//
	// The tint MUST be scaled by ATTENUATION. `light()` runs once per light, so without it every
	// light in the scene adds a constant `shadow_tint * shadow_tint_amount` to every surface no
	// matter how far away it is or which way the surface faces. On a character lit by one sun
	// that is invisible; in a room it is catastrophic. The Cinema has nine lights, which added
	// roughly (1.95, 1.83, 2.27) of flat violet to every pixel — swamping the sun, the geometry
	// and SSAO alike, and turning a 26-mesh room into uniform purple papercraft. Ambient
	// occlusion at eight times normal strength was literally undetectable underneath it.
	float reach = ATTENUATION;
	vec3 lit = banded * (LIGHT_COLOR / PI);
	vec3 shade = shadow_tint.rgb * (1.0 - banded) * shadow_tint_amount * reach;
	DIFFUSE_LIGHT += lit + shade;

	// Rim, masked by light so it only glows on lit edges (an unlit rim looks like fog).
	float rim = fresnel(rim_amount, NORMAL, VIEW);
	rim = smoothstep(0.5 - rim_smoothness * 0.5, 0.5 + rim_smoothness * 0.5, rim);
	DIFFUSE_LIGHT += rim_color.rgb * rim * rim_strength * banded;
}
