// Mirror3D shader (MIT, adapted from Joy-less/Mirror3D)
// Renders the SubViewport texture aligned to screen space for planar reflection.
//
// The `1.0 - SCREEN_UV.x` flip un-mirrors the reflection camera's image: that camera is the
// reflected viewer with its local X negated (see World/Mirror.cs), which renders the true
// reflection mirrored once horizontally. Sampling by SCREEN_UV ties one texel to one screen
// pixel, which is only correct while the render target matches the main viewport's size and
// the reflection camera's projection equals the viewer's — both of which Mirror.cs maintains.
shader_type spatial;
// Opaque: glass is not a window. ALPHA is deliberately never written — writing it at all puts
// the quad in the TRANSPARENT queue, where it sorts against the very room it is reflecting, and
// taking it from the texture additionally made the mirror a see-through HOLE wherever the
// reflection camera rendered nothing opaque (a world with no sky, or a reflected view off the
// edge of the level).
render_mode unshaded, cull_disabled;

uniform vec4 color : source_color = vec4(0.95, 0.97, 1.0, 1.0);
uniform sampler2D mirror_texture : source_color, filter_linear_mipmap;

void fragment() {
	vec2 uv = vec2(1.0 - SCREEN_UV.x, SCREEN_UV.y);
	ALBEDO = texture(mirror_texture, uv).rgb * color.rgb;
}
