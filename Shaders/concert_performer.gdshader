// License for the adapted MToon ramp (retained here so every export carries the notice).
// MIT License
// Copyright (c) 2020-2022 V-Sekai Contributors (see credits in README.md)
// Copyright (c) 2018 Masataka SUMI
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// Concert-only adaptation of the MToon shade-shift/toony ramp.
// Reference: V-Sekai/godot-vrm, MIT. See ThirdPartyLicenses/ConcertMToon.txt.
// No screen/depth reads, compositor effects or native plugins: stereo and GL compatible.
shader_type spatial;
render_mode cull_back, diffuse_toon, specular_toon;

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool has_texture = true;
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
uniform float alpha_scissor = 0.0;
uniform vec4 shade_color : source_color = vec4(0.72, 0.80, 0.94, 1.0);
uniform float shade_shift = 0.0;
uniform float shade_toony = 0.8;
uniform float shade_depth = 0.65;
uniform float light_wrap = 0.22;
uniform float highlight_strength = 0.0;
uniform float rim_strength = 0.05;
uniform float surface_roughness = 0.85;
uniform sampler2D normal_texture : hint_normal;
uniform bool has_normal_texture = false;
uniform float normal_strength = 0.2;

void fragment() {
	vec2 uv = UV * uv_scale + uv_offset;
	vec4 color = (has_texture ? texture(albedo_texture, uv) : vec4(1.0)) * albedo_color;
	// Alpha-tested opaque queue: a white alpha must not send the entire performer through
	// the transparent queue, lose shadows, or sort her face behind her own hair.
	if (alpha_scissor > 0.0 && color.a < alpha_scissor) discard;
	ALBEDO = color.rgb;
	ROUGHNESS = surface_roughness;
	SPECULAR = highlight_strength;
	if (has_normal_texture) {
		NORMAL_MAP = texture(normal_texture, uv).rgb;
		NORMAL_MAP_DEPTH = normal_strength;
	}
}

void light() {
	float nl = dot(normalize(NORMAL), LIGHT);
	// MToon's authored ramp; attenuation stays outside it so a moving spotlight fades
	// smoothly, rather than cutting a painted disc across the face or the clothes.
	float upper = mix(1.0, shade_shift, shade_toony);
	float width = max(0.16, upper - shade_shift);
	float ramp = smoothstep(shade_shift, shade_shift + width, nl);
	vec3 tint = mix(shade_color.rgb * shade_depth, vec3(1.0), ramp);
	float facing = smoothstep(-light_wrap, 0.12, nl);
	vec3 energy = (LIGHT_COLOR / PI) * ATTENUATION;
	DIFFUSE_LIGHT += tint * facing * energy;
	// Every term follows the real light energy, colour and shadows. An off fixture cannot
	// add white rim or grey fill, however many moving heads the venue contains.
	float edge = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.5);
	float edge_lit = smoothstep(-0.1, 0.45, nl);
	DIFFUSE_LIGHT += energy * edge * edge_lit * rim_strength;
	vec3 half_dir = normalize(LIGHT + VIEW);
	float shine = smoothstep(0.35, 0.80, pow(max(dot(NORMAL, half_dir), 0.0), 32.0));
	SPECULAR_LIGHT += energy * shine * highlight_strength * facing;
}
