shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_front, shadows_disabled, fog_disabled;
uniform vec3 tint : source_color = vec3(.24,.48,.8);
uniform float intensity = 0.0;
varying vec3 local_position;
varying vec3 local_eye;
void vertex() {
    local_position = VERTEX;
    // EYE_OFFSET is zero in mono, and supplies the eye displacement in XR multiview.
    local_eye = (inverse(MODELVIEW_MATRIX) * vec4(EYE_OFFSET,1.0)).xyz;
}
void fragment() {
    vec3 ray = normalize(local_position-local_eye);
    float begin=0.0;
    float end=10000.0;
    // Intersect the finite optical volume. Nearly axial rays must see the interior,
    // even when the closest point on the unbounded beam axis lies outside its caps.
    if (abs(ray.y)<.00001) {
        if (local_eye.y<.32 || local_eye.y>30.0) discard;
    } else {
        float a=(.32-local_eye.y)/ray.y;
        float b=(30.0-local_eye.y)/ray.y;
        begin=max(begin,min(a,b)); end=min(end,max(a,b));
    }
    float slope=.017689;
    float origin_radius=.08934+slope*local_eye.y;
    float aa=dot(ray.xz,ray.xz)-slope*slope*ray.y*ray.y;
    float bb=2.0*(dot(local_eye.xz,ray.xz)-origin_radius*slope*ray.y);
    float cc=dot(local_eye.xz,local_eye.xz)-origin_radius*origin_radius;
    if(abs(aa)<.0000001) {
        if(abs(bb)<.0000001) { if(cc>0.0) discard; }
        else if(bb>0.0) end=min(end,-cc/bb);
        else begin=max(begin,-cc/bb);
    } else {
        float discriminant=bb*bb-4.0*aa*cc;
        if(discriminant<0.0) { if(aa>0.0) discard; }
        else {
            float d=sqrt(discriminant);
            float a=(-bb-d)/(2.0*aa), b=(-bb+d)/(2.0*aa);
            float near_root=min(a,b), far_root=max(a,b);
            if(aa>0.0) { begin=max(begin,near_root);end=min(end,far_root); }
            else if(begin<near_root) end=min(end,near_root);
            else begin=max(begin,far_root);
        }
    }
    if(end<=begin) discard;
    float step_length=(end-begin)/8.0;
    float optical_depth=0.0;
    // Eight stable quadrature samples; no screen/depth textures or animated noise.
    for(int i=0;i<8;i++) {
        vec3 p=local_eye+ray*(begin+(float(i)+.5)*step_length);
        float radius=.08934+slope*p.y;
        float r=clamp(length(p.xz)/radius,0.0,1.0);
        float radial=exp(-r*r*3.5)*(1.0-smoothstep(.72,1.0,r));
        float ends=smoothstep(.32,1.0,p.y)*(1.0-smoothstep(20.0,30.0,p.y));
        optical_depth+=radial*ends*step_length*.10;
    }
    ALBEDO=tint;
    ALPHA=clamp((1.0-exp(-optical_depth))*intensity,0.0,.14);
}
