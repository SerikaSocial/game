// Each eye renders an off-axis view through the physical mirror rectangle. Local quad UVs
// map that view back onto the glass, with X reversed for the reflected camera's handedness.
shader_type spatial;
// Opaque: glass is not a window. ALPHA is deliberately never written — writing it at all puts
// the quad in the TRANSPARENT queue, where it sorts against the very room it is reflecting, and
// taking it from the texture additionally made the mirror a see-through HOLE wherever the
// reflection camera rendered nothing opaque (a world with no sky, or a reflected view off the
// edge of the level).
render_mode unshaded, cull_disabled;

uniform vec4 color : source_color = vec4(0.95, 0.97, 1.0, 1.0);
// View 0 — the mono/desktop reflection, and the LEFT eye in stereo.
uniform sampler2D mirror_texture : source_color, filter_linear_mipmap;
// View 1 — the RIGHT eye. Bound only once a mirror has run a stereo frame; on desktop it is
// never sampled, because VIEW_INDEX is 0 outside multiview.
uniform sampler2D mirror_texture_r : source_color, filter_linear_mipmap;

void fragment() {
	vec2 uv = vec2(1.0 - UV.x, UV.y);
	// In XR the scene is rendered once per eye with two DIFFERENT, asymmetric projections, so
	// one reflection cannot serve both: Mirror.cs renders one per view and this picks between
	// them. VIEW_INDEX is 0 for mono, so desktop samples the first texture.
	// The cast is load-bearing, and neither `VIEW_INDEX == 0` nor `VIEW_INDEX == 0u` works.
	//
	// Godot's shading language types `VIEW_INDEX` as a signed int, so `== 0u` fails its own parser
	// with "Invalid arguments to operator '==': 'int, uint'". Its GLES3 backend then emits that
	// same builtin as an *unsigned* int, so `== 0` fails on device with the mirror-image complaint
	// from the Adreno compiler: "no operation '==' exists that takes a left-hand operand of type
	// 'const uint' and a right operand of type 'const int'". The two disagree, and only an explicit
	// cast satisfies both.
	//
	// This is not a mirror bug when it goes wrong — it is a *scene shader* compile failure, and
	// every material sharing that pipeline dies with it, so the headset renders a black world.
	// Desktop cannot catch it by default: the project runs Forward+/Vulkan while
	// `renderer/rendering_method.mobile` is `gl_compatibility`. Run diagnostics with
	// `--rendering-method gl_compatibility` to compile shaders the way the Quest will.
	vec3 reflected = int(VIEW_INDEX) == 0
		? texture(mirror_texture, uv).rgb
		: texture(mirror_texture_r, uv).rgb;
	ALBEDO = reflected * color.rgb;
}
