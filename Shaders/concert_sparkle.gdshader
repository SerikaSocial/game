shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform bool sky_mode = false;
uniform float event_age = 0.0;
uniform float event_duration = 3.0;
uniform float strength : hint_range(0.0,1.0) = 0.0;
uniform float cue_seed = 0.0;
uniform float show_time = 0.0;
varying float sparkle_alpha;
varying float sparkle_tone;
float hash1(float p) { return fract(sin(p*127.1)*43758.5453); }
vec3 sky_path(float t) {
    return vec3(-27.0+54.0*t,26.0+14.0*t+15.0*pow(t,8.0)+2.0*sin(t*3.14159265),-24.0-1.5*sin(t*3.14159265));
}
void vertex() {
    float id=UV2.x;
    float seed=id*5.19+cue_seed;
    float r=hash1(seed),r2=hash1(seed+1.0),r3=hash1(seed+2.0);
    vec3 offset=vec3(0.0);
    vec2 card_axis=vec2(1.0,0.0);
    float size;
    if(sky_mode) {
        // One ascending comet current in the clear sky in front of the stage roof.
        float t=fract(UV2.y+show_time*.040);
        float reveal=smoothstep(r*.85,r*.85+.15,strength);
        float shimmer=.72+.28*sin(show_time*(.8+r2*.6)+seed);
        sparkle_alpha=reveal*strength*shimmer*smoothstep(0.0,.055,t)*(1.0-smoothstep(.94,1.0,t));
        offset=sky_path(t)-sky_path(UV2.y);
        vec3 tangent=vec3(54.0,14.0+120.0*pow(t,7.0)+6.2831853*cos(t*3.14159265),-4.71238898*cos(t*3.14159265));
        vec2 view_tangent=(MODELVIEW_MATRIX*vec4(tangent,0.0)).xy;
        card_axis=length(view_tangent)>.001?normalize(view_tangent):vec2(1.0,0.0);
        size=.38+pow(r3,5.0)*.85;
    } else {
        // Longer cues remain a finite fountain: stagger births instead of leaving
        // an apparently active bank empty after every initial spark has fallen.
        float delay=r*max(.22,event_duration-3.8);
        float age=max(0.0,event_age-delay);
        float life=max(.25,min(3.8,event_duration-delay));
        float phi=r2*6.2831853;
        float horizontal=.45+r3*1.8;
        vec3 velocity=vec3(cos(phi)*horizontal,3.7+r*3.2,sin(phi)*horizontal);
        offset=velocity*age+vec3(.11*age*age,-1.25*age*age,0.0);
        float fade=smoothstep(0.0,.06,age)*(1.0-smoothstep(life*.35,life,age));
        sparkle_alpha=fade*step(delay,event_age)*strength*step(.03,offset.y);
        size=.12+r2*.13;
    }
    sparkle_tone=r3;
    vec3 view_center=(MODELVIEW_MATRIX*vec4(VERTEX+offset,1.0)).xyz;
    vec2 corner=(UV-.5)*2.0;
    vec2 card=sky_mode?(card_axis*corner.x*1.45+vec2(-card_axis.y,card_axis.x)*corner.y):corner;
    VERTEX=(inverse(MODELVIEW_MATRIX)*vec4(view_center+vec3(card*size,0.0),1.0)).xyz;
}
void fragment() {
    vec2 signed_p=(UV-.5)*2.0;
    vec2 p=abs(signed_p);
    if(sky_mode) p=abs(vec2((signed_p.x-.24)*1.45,signed_p.y));
    float core=exp(-dot(p,p)*32.0);
    float ray_x=pow(max(0.0,1.0-p.x),3.0)*exp(-p.y*p.y*400.0);
    float ray_y=pow(max(0.0,1.0-p.y),3.0)*exp(-p.x*p.x*400.0);
    float tail=sky_mode?exp(-signed_p.y*signed_p.y*180.0)*smoothstep(-1.0,.18,signed_p.x)*(1.0-smoothstep(.1,.38,signed_p.x))*.16:0.0;
    ALBEDO=mix(vec3(.48,.76,1.0),vec3(.95,.98,1.0),sparkle_tone)*(sky_mode?2.4:1.9);
    ALPHA=clamp(core+(ray_x+ray_y)*.24+tail,0.0,1.0)*sparkle_alpha;
}
