vec3 concert_body_motion(float height, float seed, float clock, float energy, float amount) {
    float phase = abs(seed) * 6.2831853;
    float eager = smoothstep(0.25, 0.8, 0.5 + 0.5 * sin(clock * 0.31 + phase * 5.0));
    float cycle = fract(clock * (1.55 + 0.22 * abs(seed)) + abs(seed) * 4.0);
    float flight = clamp(cycle / 0.62, 0.0, 1.0);
    float arc = sin(flight * 3.14159265);
    float jump = arc * arc * (0.20 + 0.18 * abs(seed)) * eager * smoothstep(0.45, 0.95, energy);
    if (seed < 0.0) jump = 0.0;
    float upper = clamp(height / 1.5, 0.0, 1.25);
    float sway = sin(clock * 1.6 + phase) * (0.025 + 0.055 * energy) * upper;
    float lean = sin(clock * 1.15 + phase * 2.0) * 0.04 * upper;
    return vec3(sway, jump, lean) * amount;
}
