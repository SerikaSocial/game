shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never, shadows_disabled, fog_disabled;

// Original finite jet volume. Every sample is driven by the synchronized show clock.
uniform float event_age = 0.0;
uniform float event_duration = 1.0;
uniform float strength : hint_range(0.0, 1.0) = 0.0;
uniform float cue_seed = 0.0;
uniform float show_time = 0.0;
varying vec3 local_position;
varying vec3 local_eye;

float hash3(vec3 p) { p=fract(p*.1031); p+=dot(p,p.yzx+33.33); return fract((p.x+p.y)*p.z); }
float noise3(vec3 p) {
    vec3 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(mix(hash3(i),hash3(i+vec3(1,0,0)),f.x),mix(hash3(i+vec3(0,1,0)),hash3(i+vec3(1,1,0)),f.x),f.y),
        mix(mix(hash3(i+vec3(0,0,1)),hash3(i+vec3(1,0,1)),f.x),mix(hash3(i+vec3(0,1,1)),hash3(i+vec3(1,1,1)),f.x),f.y),f.z);
}
void vertex() {
    local_position=VERTEX;
    local_eye=(inverse(MODELVIEW_MATRIX)*vec4(EYE_OFFSET,1.0)).xyz;
}
void fragment() {
    vec3 rd=normalize(local_position-local_eye);
    vec3 safe_rd=mix(vec3(.00001),rd,greaterThan(abs(rd),vec3(.00001)));
    vec3 ta=(vec3(-.9,0,-.9)-local_eye)/safe_rd;
    vec3 tb=(vec3(.9,4.4,.9)-local_eye)/safe_rd;
    vec3 mn=min(ta,tb),mx=max(ta,tb);
    float near_t=max(0.0,max(mn.x,max(mn.y,mn.z)));
    float far_t=min(mx.x,min(mx.y,mx.z));
    float attack=smoothstep(0.0,min(.13,event_duration*.2),event_age);
    float release=1.0-smoothstep(event_duration*.62,event_duration,event_age);
    float gate=attack*release*strength;
    float height=3.5*sqrt(max(gate,0.0));
    vec3 radiance=vec3(0.0); float density_sum=0.0;
    float step_size=max(0.0,far_t-near_t)/12.0;
    for(int i=0;i<12;i++) {
        vec3 p=local_eye+rd*(near_t+(float(i)+.5)*step_size);
        float h=max(p.y/max(height,.01),0.0);
        vec3 np=p*vec3(5.2,2.3,5.2)+vec3(cue_seed,-event_age*10.5,0);
        float turbulent=noise3(np)*.60+noise3(np*2.03)*.28+noise3(np*4.01)*.12;
        vec2 bend=vec2(sin(p.y*2.1-event_age*6.0+cue_seed),cos(p.y*1.7-event_age*4.0+cue_seed))*.045*h;
        float radius=mix(.13,.40,sin(min(h,1.0)*2.4))*(1.0-.64*min(h,1.0));
        float radial=length(p.xz-bend)/max(radius,.015);
        float body=1.0-smoothstep(.10,1.0,radial+(turbulent-.45)*1.3+h*.20);
        float tip=1.0-smoothstep(.58,1.0,h+(turbulent-.5)*.36);
        float tongues=smoothstep(.26,.64,turbulent+(1.0-h)*.34);
        float density=body*tip*tongues*step(0.0,p.y);
        vec3 heat=mix(vec3(1.0,.13,.006),vec3(1.0,.91,.64),pow(max(0.0,1.0-radial),1.4)*max(0.0,1.0-h*.5));
        radiance+=heat*density*step_size;
        density_sum+=density*step_size;
    }
    ALBEDO=radiance/max(density_sum,.001)*1.8;
    ALPHA=(1.0-exp(-density_sum*6.0))*gate;
}
