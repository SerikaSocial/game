shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled;

// Original deterministic crowd penlight animation. The author stores a wrist pivot
// in legal glTF vertex colors and a stable phase/brightness pair in UV2. Everything
// else a wild crowd needs -- an independent set of random draws per stick, the ~4.5 m
// knot of friends a spectator is standing in, and how far back from the stage they
// are -- is derived HERE from that same data, so 8,345 sticks stay in six batched
// meshes with no per-stick node, no per-stick CPU work and a fixed handful of
// uniforms per batch per frame.
uniform float crowd_time = 0.0;
uniform float brightness : hint_range(0.0, 1.0) = 0.12;
uniform float movement : hint_range(0.0, 1.0) = 0.35;
// How hard the room is going right now: 0 through a quiet verse, 1 through a chorus
// or a drop. Driven by the show's own musicEnergy envelope.
uniform float hype : hint_range(0.0, 1.0) = 0.0;
// The beat envelope sampled at now and at three evenly older instants (see
// EventShowPlayer.CrowdBeatTap; 0.21 s of spread today). A stick picks the tap matching
// its distance from the stage, so a hit RIPPLES back through the arena instead of firing
// everywhere on the same frame. Four taps is one vec4 uniform and no dynamic array
// indexing, which the compatibility renderer does not love.
uniform vec4 beat_wave = vec4(0.0);

varying float individual_brightness;
varying float tube_height;
varying float flare;

// Fold before scrambling. Arena coordinates reach +/-80 m and a raw sin() of numbers
// that large has almost no mantissa left, which bands the "random" draws into stripes
// across the pens -- exactly the synchronised look this is trying to break up.
float crowd_hash(vec2 v) {
    vec2 f = fract(v * vec2(0.3183099, 0.3678794)) + 0.61803399;
    f += dot(f, f.yx + 19.19);
    return fract(f.x * f.y * 37.77);
}

void vertex() {
    vec3 pivot = vec3(COLOR.r * 160.0 - 80.0, COLOR.g * 16.0,
                      COLOR.b * 160.0 - 20.0);
    vec3 local = VERTEX - pivot;
    float phase = UV2.x;
    float p = phase * 6.28318530718;
    // Three independent draws per stick. The authored phase is folded into every hash
    // so they stay distinct even if the color attribute is imported quantized.
    float r1 = crowd_hash(pivot.xz + vec2(phase * 17.31, phase * 8.93));
    float r2 = crowd_hash(pivot.zx + vec2(r1 * 29.70, phase * 5.11));
    float r3 = crowd_hash(vec2(r1, r2) * 13.70 + pivot.xz);
    // People who came together stand together and wave together. A 4.5 m cell is about
    // nine spectators on the authored 1.35 x 1.48 m standing grid.
    float cluster = crowd_hash(floor(pivot.xz * 0.2222)) * 6.28318530718;
    // Pens run z 18..104 and tribunes z -17..98; 0 is the stage end, 1 the back wall.
    float depth = clamp((pivot.z + 18.0) / 122.0, 0.0, 1.0);

    // Piecewise-linear pick across the four delayed beat taps.
    float w = depth * 3.0;
    float ripple = beat_wave.x;
    ripple = mix(ripple, beat_wave.y, clamp(w, 0.0, 1.0));
    ripple = mix(ripple, beat_wave.z, clamp(w - 1.0, 0.0, 1.0));
    ripple = mix(ripple, beat_wave.w, clamp(w - 2.0, 0.0, 1.0));

    // A hit lands as a real arm thrust. WHO is pumping drifts slowly rather than being
    // re-rolled on each beat, so an arm never snaps between jumping and not jumping.
    float appetite = 0.5 + 0.5 * sin(crowd_time * 0.37 + r1 * 31.4 + cluster);
    float pumping = smoothstep(0.62 - 0.78 * hype, 0.92 - 0.78 * hype, appetite);
    // The 0.15 floor also guarantees the thrust reaches zero between beats, so the
    // slow drift in `pumping` can never be caught holding an arm in the air.
    float pump = smoothstep(0.15, 0.85, ripple) * pumping * movement
                 * (0.06 + 0.12 * hype) * (0.70 + 0.60 * r3);

    float swing = movement * (0.70 + 0.30 * hype);
    // Sway carries a slow travelling wave across the arena (the pivot.x/pivot.z terms)
    // on top of the per-stick rate and the cluster offset, so no two areas of the pen
    // are ever in step for long.
    float sway = sin(crowd_time * (1.75 + 1.05 * r2 + 1.90 * hype)
                     + p + cluster * 0.8 + pivot.x * 0.09 + pivot.z * 0.07)
                 * swing * (0.30 + 0.22 * hype) * (0.72 + 0.56 * r1)
               + ripple * swing * 0.10 * (r2 * 2.0 - 1.0);
    float tip = sin(crowd_time * (1.20 + 0.85 * r3 + 1.15 * hype) + p * 1.7 + cluster * 0.5)
                 * swing * (0.16 + 0.14 * hype) * (0.72 + 0.56 * r3)
              + ripple * swing * (0.12 + 0.20 * hype) * (0.65 + 0.70 * r2);

    float cz = cos(sway), sz = sin(sway);
    local.xy = mat2(vec2(cz, sz), vec2(-sz, cz)) * local.xy;
    float cx = cos(tip), sx = sin(tip);
    local.yz = mat2(vec2(cx, sx), vec2(-sx, cx)) * local.yz;
    VERTEX = pivot + local + vec3(0.0, pump, 0.0);

    // Quiet verse sits near 0.62 of the authored level; a chorus hit reaches 1.14. The
    // gap between the two is the point -- a flat field reads as scenery, not a crowd.
    individual_brightness = UV2.y * (0.72 + 0.28 * ripple)
                          * (0.88 + 0.12 * sin(crowd_time * (2.30 + 1.70 * r2) + p * 3.1))
                          * (0.86 + 0.28 * hype);
    flare = ripple * (0.30 + 0.70 * hype) * (0.45 + 0.55 * r2);
    tube_height = UV.y;
}

void fragment() {
    // Opaque, emissive physical tubes work for both eyes without billboards,
    // transparency sorting, scene copies, screen textures or thousands of lights.
    vec3 blue = mix(vec3(0.018, 0.16, 0.82), vec3(0.065, 0.48, 1.0),
                    smoothstep(0.0, 0.18, tube_height));
    // A hit drives the tube toward a hot pale core. The palette stays the artist's blue.
    vec3 tint = mix(blue, vec3(0.42, 0.74, 1.0), flare * 0.5);
    float level = brightness * individual_brightness;
    ALBEDO = tint * level;
    EMISSION = tint * level * (0.55 + 0.35 * flare);
}
