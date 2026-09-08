shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

// Original deterministic crowd penlight animation. The author stores a wrist pivot
// in legal glTF vertex colors and a stable phase/brightness pair in UV2.
uniform float crowd_time = 0.0;
uniform float brightness : hint_range(0.0, 1.0) = 0.12;
uniform float movement : hint_range(0.0, 1.0) = 0.35;
uniform float beat : hint_range(0.0, 1.0) = 0.0;
varying float individual_brightness;
varying float tube_height;

void vertex() {
    vec3 pivot = vec3(COLOR.r * 160.0 - 80.0, COLOR.g * 16.0,
                      COLOR.b * 160.0 - 20.0);
    vec3 local = VERTEX - pivot;
    float phase = UV2.x * 6.28318530718;
    float sway = sin(crowd_time * 2.1 + phase) * 0.20 * movement * (0.82 + 0.18 * beat);
    float tip = sin(crowd_time * 1.37 + phase * 1.7) * 0.12 * movement;
    float cz = cos(sway), sz = sin(sway);
    local.xy = mat2(vec2(cz, sz), vec2(-sz, cz)) * local.xy;
    float cx = cos(tip), sx = sin(tip);
    local.yz = mat2(vec2(cx, sx), vec2(-sx, cx)) * local.yz;
    VERTEX = pivot + local;
    individual_brightness = UV2.y;
    tube_height = UV.y;
}

void fragment() {
    // Opaque, emissive physical tubes work for both eyes without billboards,
    // transparency sorting, scene copies, screen textures or thousands of lights.
    vec3 blue = mix(vec3(0.018, 0.16, 0.82), vec3(0.065, 0.48, 1.0),
                    smoothstep(0.0, 0.18, tube_height));
    float level = brightness * individual_brightness * (0.88 + 0.12 * beat);
    ALBEDO = blue * level;
    EMISSION = blue * level * 0.55;
}
