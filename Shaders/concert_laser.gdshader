shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never, shadows_disabled, fog_disabled;

// Original analytical thin ray. Geometry is an authored closed cylinder with
// local Y in [0,1]; the runtime aims/scales it to a finite target above the crowd.
uniform vec4 laser_color : source_color = vec4(.035,.61,1.0,1.0);
uniform float beam_strength : hint_range(0.0,1.0) = 0.0;
varying highp vec3 ray_origin;
varying highp vec3 ray_end;
varying highp vec3 fragment_world;
varying highp vec3 eye_world;

void vertex() {
    ray_origin=(MODEL_MATRIX*vec4(0,0,0,1)).xyz;
    ray_end=(MODEL_MATRIX*vec4(0,1,0,1)).xyz;
    fragment_world=(MODEL_MATRIX*vec4(VERTEX,1)).xyz;
    eye_world=(INV_VIEW_MATRIX*vec4(EYE_OFFSET,1.0)).xyz;
}
void fragment() {
    highp vec3 rd=normalize(fragment_world-eye_world);
    highp vec3 axis=ray_end-ray_origin;
    highp float ray_length=length(axis);
    axis/=max(.001,ray_length);
    highp vec3 w=eye_world-ray_origin;
    highp float b=dot(rd,axis),d=dot(rd,w),e=dot(axis,w);
    highp float denominator=1.0-b*b;
    highp float along=denominator>.00001?clamp((e-b*d)/denominator,0.0,ray_length):clamp(e,0.0,ray_length);
    highp float distance_on_view=max(0.0,dot(ray_origin+axis*along-eye_world,rd));
    along=clamp(dot(eye_world+rd*distance_on_view-ray_origin,axis),0.0,ray_length);
    highp float distance_to_axis=length(eye_world+rd*distance_on_view-(ray_origin+axis*along));
    // At distance the optical trace retains a soft subpixel core; its physical
    // proxy stays under 19cm wide instead of becoming a glowing cone or plane.
    float world_per_pixel=2.0*length(eye_world-(ray_origin+axis*along))/max(1.0,VIEWPORT_SIZE.y*PROJECTION_MATRIX[1][1]);
    float radius=clamp(world_per_pixel*.38,.024,.055);
    float profile=exp(-pow(distance_to_axis/radius,2.0));
    float fade=smoothstep(.04,.35,along)*(1.0-smoothstep(ray_length*.78,ray_length,along));
    ALBEDO=laser_color.rgb*2.6;
    ALPHA=profile*fade*beam_strength*.74;
}
