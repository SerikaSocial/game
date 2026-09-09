shader_type spatial;
render_mode cull_back;
#include "concert_crowd_motion.gdshaderinc"
uniform float crowd_time = 0.0;
uniform float hype = 0.0;
uniform float movement = 0.0;

void vertex() {
    float ground = COLOR.g * 16.0;
    VERTEX += concert_body_motion(VERTEX.y - ground, UV.x, crowd_time, hype, movement);
}

void fragment() {
    ALBEDO = vec3(0.013, 0.019, 0.029);
    ROUGHNESS = 0.98;
}
