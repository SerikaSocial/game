shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;

// Twelve authored puff cards per nozzle; deterministic birth, expansion and wind.
uniform float event_age = 0.0;
uniform float event_duration = 6.0;
uniform float strength : hint_range(0.0,1.0) = 0.0;
uniform float cue_seed = 0.0;
uniform float show_time = 0.0;
varying float puff_opacity;
varying highp float puff_seed;
varying float puff_time;
float hash1(float p) { return fract(sin(p*127.1)*43758.5453); }
float hash2(vec2 p) {
    // Keep the hash calculation in full precision; large sine arguments and
    // reduced-precision interpolation expose square cells on mobile hardware.
    highp vec3 q=fract(vec3(p.x,p.y,p.x)*.1031);
    q+=dot(q,q.yzx+19.19);
    return fract((q.x+q.y)*q.z);
}
float noise2(vec2 p) {
    highp vec2 i=floor(p),f=fract(p);f=f*f*f*(f*(f*6.0-15.0)+10.0);
    return mix(mix(hash2(i),hash2(i+vec2(1,0)),f.x),mix(hash2(i+vec2(0,1)),hash2(i+vec2(1,1)),f.x),f.y);
}
void vertex() {
    float id=UV2.x;
    puff_seed=mod(id*3.17+cue_seed,23.0);
    float delay=id/11.0*min(2.2,event_duration*.35);
    float age=max(0.0,event_age-delay);
    puff_time=age;
    float life=max(.2,event_duration-delay);
    float fade=smoothstep(0.0,.30,age)*(1.0-smoothstep(life*.52,life,age));
    puff_opacity=fade*step(delay,event_age)*strength;
    float speed=1.1+hash1(puff_seed)*.65;
    vec3 offset=vec3(age*(.25+hash1(puff_seed+1.0)*.25),age*speed,age*.08);
    offset.x+=sin(age*.6+puff_seed)*age*.14;
    offset.z+=cos(age*.5+puff_seed)*age*.17;
    float size=.38+age*(.43+hash1(puff_seed+2.0)*.18);
    vec3 view_center=(MODELVIEW_MATRIX*vec4(VERTEX+offset,1.0)).xyz;
    vec2 corner=(UV-.5)*2.0;
    float turn=puff_seed+age*.08;
    corner=mat2(vec2(cos(turn),sin(turn)),vec2(-sin(turn),cos(turn)))*corner;
    VERTEX=(inverse(MODELVIEW_MATRIX)*vec4(view_center+vec3(corner*size,0.0),1.0)).xyz;
}
void fragment() {
    vec2 p=(UV-.5)*2.0;
    vec2 rotated=mat2(vec2(.8,.6),vec2(-.6,.8))*p;
    vec2 drift=vec2(puff_seed,puff_seed*.43+puff_time*.13);
    float n=noise2(p*2.6+drift)*.55+noise2(rotated*5.4+drift.yx)*.30+noise2(p*9.3-drift)*.15;
    float edge=(1.0-smoothstep(.50,1.0,length(p)+(n-.5)*.34))*(1.0-smoothstep(.80,.98,length(p)));
    float cloud=edge*(.35+.65*n);
    // Soft silver-grey smoke with a slightly brighter upper edge, not luminous discs.
    ALBEDO=mix(vec3(.19,.24,.29),vec3(.40,.45,.49),clamp(n*.65+UV.y*.35,0.0,1.0));
    ALPHA=cloud*puff_opacity*.22;
}
