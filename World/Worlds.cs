using System;
using Godot;

namespace SerikaSocial.World;

/// Builders for the client's built-in spaces. Each adds its geometry under a caller-owned root
/// node so Main can swap worlds by freeing the root's children. Home is a cosy single-player
/// house (with a mirror + a portal to the Commons); Commons is the multiplayer gathering hall.
public static class Worlds
{
    public readonly struct Home
    {
        public readonly Vector3 Spawn;
        public readonly Portal CommonsPortal;
        public readonly Mirror Mirror;
        public Home(Vector3 spawn, Portal portal, Mirror mirror)
        {
            Spawn = spawn; CommonsPortal = portal; Mirror = mirror;
        }
    }

    private static StandardMaterial3D Mat(Color c, float rough = 0.9f, float metal = 0f) =>
        new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    private static MeshInstance3D Box(Vector3 size, Vector3 pos, StandardMaterial3D mat, float yaw = 0)
    {
        var m = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = pos,
            RotationDegrees = new Vector3(0, yaw, 0),
        };
        m.MaterialOverride = mat;
        return m;
    }

    /// A box that is also a collider — wraps a StaticBody3D around the mesh so the player
    /// can't walk through walls, furniture, pillars, etc.
    private static StaticBody3D CollidableBox(Vector3 size, Vector3 pos, StandardMaterial3D mat, float yaw = 0)
    {
        var body = new StaticBody3D
        {
            Position = pos,
            RotationDegrees = new Vector3(0, yaw, 0),
        };
        var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = size } };
        mesh.MaterialOverride = mat;
        body.AddChild(mesh);
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = size },
        });
        return body;
    }

    /// A cylinder collider (for the central platform in the Commons).
    private static StaticBody3D CollidableCylinder(float radius, float height, Vector3 pos, StandardMaterial3D mat)
    {
        var body = new StaticBody3D { Position = pos };
        var mesh = new MeshInstance3D { Mesh = new CylinderMesh { Height = height, TopRadius = radius, BottomRadius = radius } };
        mesh.MaterialOverride = mat;
        body.AddChild(mesh);
        body.AddChild(new CollisionShape3D
        {
            Shape = new CylinderShape3D { Radius = radius, Height = height },
        });
        return body;
    }

    /// A sphere mesh (decorative — no collision).
    private static MeshInstance3D Sphere(float radius, Vector3 pos, StandardMaterial3D mat)
    {
        var m = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f },
            Position = pos,
        };
        m.MaterialOverride = mat;
        return m;
    }

    /// A collidable sphere (for planters, decorative boulders, etc.).
    private static StaticBody3D CollidableSphere(float radius, Vector3 pos, StandardMaterial3D mat)
    {
        var body = new StaticBody3D { Position = pos };
        var mesh = new MeshInstance3D { Mesh = new SphereMesh { Radius = radius, Height = radius * 2f } };
        mesh.MaterialOverride = mat;
        body.AddChild(mesh);
        body.AddChild(new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = radius },
        });
        return body;
    }

    /// A torus mesh for decorative rings (no collision).
    private static MeshInstance3D Torus(float outerRadius, float tubeRadius, Vector3 pos, StandardMaterial3D mat, float pitchDeg = 90f)
    {
        var m = new MeshInstance3D
        {
            Mesh = new TorusMesh { OuterRadius = outerRadius, InnerRadius = outerRadius - tubeRadius },
            Position = pos,
            RotationDegrees = new Vector3(pitchDeg, 0, 0),
        };
        m.MaterialOverride = mat;
        return m;
    }

    // Preload shaders once — they're shared across all materials that use them.
    private static Shader _toonShader;
    private static Shader ToonShader => _toonShader ??= ResourceLoader.Load<Shader>("res://Shaders/ultimate_toon.gdshader");
    private static Shader _skyShader;
    private static Shader SkyShader => _skyShader ??= ResourceLoader.Load<Shader>("res://Shaders/customizable_sky.gdshader");

    /// A toon-shaded material — stepped shadows with rim light, tuned for the Serika look.
    private static ShaderMaterial ToonMat(Color c, float steps = 3f, float rim = 2f)
    {
        var m = new ShaderMaterial { Shader = ToonShader };
        m.SetShaderParameter("albedo_color", c);
        m.SetShaderParameter("use_stepped", true);
        m.SetShaderParameter("steps", steps);
        m.SetShaderParameter("step_smoothness", 0.3f);
        m.SetShaderParameter("shadow_tint", new Color(0.3f, 0.25f, 0.35f));
        m.SetShaderParameter("shadow_tint_amount", 0.4f);
        m.SetShaderParameter("use_rim", true);
        m.SetShaderParameter("rim_color", new Color(0.8f, 0.75f, 0.9f));
        m.SetShaderParameter("rim_amount", rim);
        m.SetShaderParameter("rim_smoothness", 0.2f);
        m.SetShaderParameter("use_pattern", false);
        m.SetShaderParameter("specular", 0.0f);
        return m;
    }

    // ── The cosy house ────────────────────────────────────────────────────────────────

    /// A warm, enclosed living room: wood floor, plaster walls, a fireplace, a couch and rug, a
    /// bookshelf, soft lamp light, plus a full-length mirror and a portal to the Commons.
    public static Home BuildHome(Node3D root)
    {
        const float w = 9f, d = 8f, h = 3.2f;

        // Ambient + environment: warm, indoor.
        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.06f, 0.05f, 0.07f),
                AmbientLightColor = new Color(0.5f, 0.42f, 0.36f),
                AmbientLightEnergy = 0.35f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        // Floor — warm wood.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.34f, 0.22f, 0.13f), 0.7f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Ceiling.
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.2f, 0.17f, 0.15f))));

        // Ceiling beams — exposed wood, adds architectural depth.
        var beamMat = Mat(new Color(0.22f, 0.14f, 0.08f), 0.8f);
        for (int i = 0; i < 4; i++)
            root.AddChild(Box(new Vector3(0.15f, 0.15f, d), new Vector3(-w / 2 + 1.5f + i * 2f, h - 0.1f, 0), beamMat));

        // Walls (plaster) — all collidable so you can't walk through them.
        var wallMat = Mat(new Color(0.62f, 0.55f, 0.48f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));       // back
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));       // left
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));        // right
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));        // front

        // Wall art — framed paintings on the left wall using real artwork.
        var frameMat = Mat(new Color(0.15f, 0.10f, 0.06f), 0.5f);
        // Portrait painting (453×680) — tall, upper position.
        var portraitTex = ResourceLoader.Load<Texture2D>("res://Assets/Art/wallart_portrait.jpg");
        var portraitMat = new StandardMaterial3D
        {
            AlbedoTexture = portraitTex,
            Roughness = 0.6f,
        };
        // Frame: 0.9m tall × 0.65m wide, on the left wall (X = -w/2 + 0.12).
        root.AddChild(Box(new Vector3(0.06f, 0.9f, 0.65f), new Vector3(-w / 2 + 0.12f, 1.7f, -2.5f), frameMat));
        // Art: QuadMesh facing +X (into the room), slightly proud of the frame.
        var portraitArt = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(0.55f, 0.8f) },
            Position = new Vector3(-w / 2 + 0.16f, 1.7f, -2.5f),
            RotationDegrees = new Vector3(0, 90, 0),
        };
        portraitArt.MaterialOverride = portraitMat;
        root.AddChild(portraitArt);

        // Landscape painting (739×415) — wide, lower position.
        var landscapeTex = ResourceLoader.Load<Texture2D>("res://Assets/Art/wallart_landscape.jpg");
        var landscapeMat = new StandardMaterial3D
        {
            AlbedoTexture = landscapeTex,
            Roughness = 0.6f,
        };
        // Frame: 0.6m tall × 1.0m wide.
        root.AddChild(Box(new Vector3(0.06f, 0.6f, 1.0f), new Vector3(-w / 2 + 0.12f, 1.5f, 0.8f), frameMat));
        var landscapeArt = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(0.9f, 0.5f) },
            Position = new Vector3(-w / 2 + 0.16f, 1.5f, 0.8f),
            RotationDegrees = new Vector3(0, 90, 0),
        };
        landscapeArt.MaterialOverride = landscapeMat;
        root.AddChild(landscapeArt);

        // Rug.
        var rug = Box(new Vector3(3.2f, 0.02f, 2.2f), new Vector3(0, 0.02f, 0.5f), Mat(new Color(0.5f, 0.15f, 0.18f), 0.95f));
        root.AddChild(rug);

        // Couch — base + backrest + two armrests (collidable). Cushions are decorative only.
        var couchMat = Mat(new Color(0.3f, 0.36f, 0.42f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.5f, 0.9f), new Vector3(0, 0.25f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.7f, 0.2f), new Vector3(0, 0.6f, 2.72f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(-1.2f, 0.55f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(1.2f, 0.55f, 2.3f), couchMat));
        // Throw cushions (decorative — no collision).
        root.AddChild(Sphere(0.25f, new Vector3(-0.7f, 0.55f, 2.2f), Mat(new Color(0.6f, 0.4f, 0.5f), 0.95f)));
        root.AddChild(Sphere(0.25f, new Vector3(0.7f, 0.55f, 2.2f), Mat(new Color(0.4f, 0.5f, 0.6f), 0.95f)));

        // Coffee table top (collidable). Legs are decorative only.
        root.AddChild(CollidableBox(new Vector3(1.4f, 0.08f, 0.7f), new Vector3(0, 0.42f, 1.1f), Mat(new Color(0.28f, 0.18f, 0.11f), 0.5f, 0.1f)));
        foreach (var (lx, lz) in new[] { (-0.6f, -0.28f), (0.6f, -0.28f), (-0.6f, 0.28f), (0.6f, 0.28f) })
            root.AddChild(Box(new Vector3(0.08f, 0.42f, 0.08f), new Vector3(lx, 0.21f, 1.1f + lz), Mat(new Color(0.2f, 0.13f, 0.08f))));

        // Bookshelf against the left wall (collidable). Books are decorative only.
        var shelfMat = Mat(new Color(0.26f, 0.17f, 0.10f), 0.7f);
        root.AddChild(CollidableBox(new Vector3(0.4f, 2.4f, 1.8f), new Vector3(-w / 2 + 0.3f, 1.2f, -1.5f), shelfMat));
        for (int i = 0; i < 4; i++)
        {
            var books = Box(new Vector3(0.32f, 0.28f, 1.6f), new Vector3(-w / 2 + 0.32f, 0.5f + i * 0.55f, -1.5f),
                Mat(Color.FromHsv((i * 0.21f) % 1f, 0.4f, 0.55f)));
            root.AddChild(books);
        }

        // Fireplace on the back wall — hearth (collidable) + emissive fire + firelight.
        root.AddChild(CollidableBox(new Vector3(2.0f, 1.4f, 0.5f), new Vector3(0, 0.7f, -d / 2 + 0.3f), Mat(new Color(0.22f, 0.20f, 0.19f), 0.9f)));
        var fire = Box(new Vector3(1.4f, 0.7f, 0.2f), new Vector3(0, 0.5f, -d / 2 + 0.45f),
            new StandardMaterial3D { Emission = new Color(1f, 0.5f, 0.15f), EmissionEnergyMultiplier = 3f, AlbedoColor = new Color(1f, 0.5f, 0.15f) });
        fire.Name = "Fire";
        root.AddChild(fire);
        root.AddChild(new OmniLight3D
        {
            Position = new Vector3(0, 0.8f, -d / 2 + 0.8f),
            LightColor = new Color(1f, 0.6f, 0.3f),
            LightEnergy = 1.6f, OmniRange = 6f, ShadowEnabled = true,
        });

        // Warm lamp lights.
        foreach (var p in new[] { new Vector3(-2.5f, 2.6f, -1f), new Vector3(2.5f, 2.6f, 1.5f) })
            root.AddChild(new OmniLight3D { Position = p, LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 0.9f, OmniRange = 5.5f });

        // A window on the right wall letting in cool daylight (contrast against the warm interior).
        root.AddChild(Box(new Vector3(0.05f, 1.4f, 1.8f), new Vector3(w / 2 - 0.1f, 1.7f, -0.5f),
            new StandardMaterial3D { Emission = new Color(0.6f, 0.7f, 0.95f), EmissionEnergyMultiplier = 1.2f, AlbedoColor = new Color(0.6f, 0.7f, 0.95f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(w / 2 - 0.8f, 1.7f, -0.5f), LightColor = new Color(0.7f, 0.8f, 1f), LightEnergy = 0.7f, OmniRange = 5f });

        // Potted plant near the window.
        root.AddChild(CollidableSphere(0.3f, new Vector3(w / 2 - 1f, 0.3f, 1.5f), Mat(new Color(0.35f, 0.22f, 0.15f), 0.8f)));
        root.AddChild(Sphere(0.5f, new Vector3(w / 2 - 1f, 0.8f, 1.5f), Mat(new Color(0.2f, 0.45f, 0.2f), 0.9f)));
        root.AddChild(Sphere(0.35f, new Vector3(w / 2 - 1.2f, 1.1f, 1.3f), Mat(new Color(0.25f, 0.5f, 0.25f), 0.9f)));
        root.AddChild(Sphere(0.3f, new Vector3(w / 2 - 0.8f, 1.0f, 1.7f), Mat(new Color(0.22f, 0.48f, 0.22f), 0.9f)));

        // Ceiling lamp fixture — a hanging sphere over the coffee table.
        root.AddChild(Box(new Vector3(0.03f, 0.8f, 0.03f), new Vector3(0, h - 0.4f, 1.1f), Mat(new Color(0.15f, 0.12f, 0.08f))));
        root.AddChild(Sphere(0.18f, new Vector3(0, h - 1.2f, 1.1f),
            new StandardMaterial3D { Emission = new Color(1f, 0.9f, 0.7f), EmissionEnergyMultiplier = 2f, AlbedoColor = new Color(1f, 0.9f, 0.7f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(0, h - 1.4f, 1.1f), LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 1.2f, OmniRange = 7f, ShadowEnabled = true });

        // Full-length mirror on the back wall (offset from the fireplace).
        var mirror = Mirror.Create(new Vector3(-2.8f, 0.05f, -d / 2 + 0.22f), 0f, 1.2f, 2.2f);
        root.AddChild(mirror);

        // Portal to the Commons, in the front doorway.
        var portal = Portal.Create("The Commons", new Color(0.4f, 0.6f, 1f), new Vector3(2.6f, 0.05f, d / 2 - 0.6f), 180f);
        root.AddChild(portal);

        return new Home(new Vector3(0, 1f, 1.2f), portal, mirror);
    }

    // ── The Commons (multiplayer) ─────────────────────────────────────────────────────

    /// The multiplayer gathering hall — an open, softly-lit space with a central platform,
    /// seating, pillars and a boundary. (Moved out of Main so worlds are swappable.)
    public static void BuildCommons(Node3D root)
    {
        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky
                {
                    SkyMaterial = new ShaderMaterial
                    {
                        Shader = SkyShader,
                    },
                },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = new Color(0.4f, 0.45f, 0.55f),
                AmbientLightEnergy = 0.5f,
                FogEnabled = true,
                FogLightColor = new Color(0.5f, 0.55f, 0.65f),
                FogLightEnergy = 0.3f,
                FogDensity = 0.001f,
            },
        });

        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f };
        sun.RotationDegrees = new Vector3(-50, -30, 0);
        root.AddChild(sun);

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(40, 40) } };
        floorMesh.MaterialOverride = Mat(new Color(0.18f, 0.20f, 0.24f), 0.9f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Corner pillars — collidable.
        foreach (float x in new[] { -15f, 15f })
            foreach (float z in new[] { -15f, 15f })
                root.AddChild(CollidableBox(new Vector3(1, 4, 1), new Vector3(x, 2, z), Mat(new Color(0.12f, 0.13f, 0.16f), 0.8f)));

        // Central platform — collidable cylinder.
        root.AddChild(CollidableCylinder(3f, 0.2f, new Vector3(0, 0.1f, 0), Mat(new Color(0.22f, 0.24f, 0.28f), 0.7f)));

        // Decorative ring around the central platform.
        root.AddChild(Torus(3.5f, 0.1f, new Vector3(0, 0.22f, 0), Mat(new Color(0.5f, 0.45f, 0.55f), 0.4f, 0.3f), 90f));

        // Fountain in the center — a tiered basin.
        root.AddChild(CollidableCylinder(1.2f, 0.6f, new Vector3(0, 0.4f, 0), Mat(new Color(0.35f, 0.38f, 0.42f), 0.3f, 0.1f)));
        root.AddChild(Sphere(0.5f, new Vector3(0, 0.9f, 0),
            new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.6f, 0.8f, 0.7f), Roughness = 0.1f, Metallic = 0f, Emission = new Color(0.3f, 0.5f, 0.7f), EmissionEnergyMultiplier = 0.5f }));
        root.AddChild(new OmniLight3D { Position = new Vector3(0, 1.2f, 0), LightColor = new Color(0.5f, 0.7f, 1f), LightEnergy = 0.8f, OmniRange = 6f });

        // Seating benches around the platform — curved arrangement.
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            var benchMat = Mat(new Color(0.15f, 0.16f, 0.19f), 0.85f);
            root.AddChild(CollidableBox(new Vector3(2, 0.4f, 0.6f), new Vector3(Mathf.Cos(a) * 5, 0.2f, Mathf.Sin(a) * 5),
                benchMat, -a * 180f / Mathf.Pi));
            // Cushion on bench.
            root.AddChild(Box(new Vector3(1.8f, 0.08f, 0.5f), new Vector3(Mathf.Cos(a) * 5, 0.44f, Mathf.Sin(a) * 5),
                Mat(new Color(0.3f, 0.25f, 0.35f), 0.9f), -a * 180f / Mathf.Pi));
        }

        // Decorative arches between corner pillars.
        var archMat = Mat(new Color(0.25f, 0.22f, 0.30f), 0.8f);
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Tau / 4f + Mathf.Pi / 4f;
            float x = Mathf.Cos(a) * 15f;
            float z = Mathf.Sin(a) * 15f;
            // Arch beam connecting adjacent pillars at top.
            root.AddChild(Box(new Vector3(0.3f, 0.3f, 12f), new Vector3(x, 3.8f, z), archMat, -a * 180f / Mathf.Pi));
        }

        // Domed roof — a large hemisphere over the entire space.
        var domeMat = Mat(new Color(0.15f, 0.14f, 0.20f, 0.8f), 0.9f);
        var dome = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 22f, Height = 22f },
            Position = new Vector3(0, 0, 0),
            MaterialOverride = domeMat,
        };
        // Clip the dome to only show the top half by positioning it below floor level.
        dome.Position = new Vector3(0, -22f, 0);
        root.AddChild(dome);

        // Decorative hanging lanterns from the dome.
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Tau / 4f + Mathf.Pi / 4f;
            float lx = Mathf.Cos(a) * 10f;
            float lz = Mathf.Sin(a) * 10f;
            root.AddChild(Box(new Vector3(0.02f, 3f, 0.02f), new Vector3(lx, 5f, lz), Mat(new Color(0.1f, 0.08f, 0.06f))));
            root.AddChild(Sphere(0.2f, new Vector3(lx, 3.5f, lz),
                new StandardMaterial3D { Emission = new Color(1f, 0.8f, 0.5f), EmissionEnergyMultiplier = 3f, AlbedoColor = new Color(1f, 0.8f, 0.5f) }));
            root.AddChild(new OmniLight3D { Position = new Vector3(lx, 3.5f, lz), LightColor = new Color(1f, 0.85f, 0.6f), LightEnergy = 1.5f, OmniRange = 12f, ShadowEnabled = true });
        }

        foreach (float x in new[] { -8f, 0f, 8f })
            foreach (float z in new[] { -8f, 0f, 8f })
            {
                if (x == 0 && z == 0) continue;
                root.AddChild(Box(new Vector3(0.1f, 5, 0.1f), new Vector3(x, 2.5f, z), Mat(new Color(0.1f, 0.1f, 0.12f))));
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, 4.8f, z),
                    LightColor = new Color(0.9f, 0.88f, 0.75f),
                    LightEnergy = 0.6f, OmniRange = 8f, OmniAttenuation = 1.5f, ShadowEnabled = true,
                });
                var glow = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f }, Position = new Vector3(x, 4.8f, z) };
                glow.MaterialOverride = new StandardMaterial3D
                {
                    EmissionEnergyMultiplier = 2f,
                    Emission = new Color(0.9f, 0.88f, 0.75f),
                    AlbedoColor = new Color(0.9f, 0.88f, 0.75f),
                };
                root.AddChild(glow);
            }

        // Potted plants in corners.
        var planterMat = Mat(new Color(0.3f, 0.25f, 0.2f), 0.8f);
        var leafMat = Mat(new Color(0.2f, 0.4f, 0.2f), 0.9f);
        foreach (float x in new[] { -14f, 14f })
            foreach (float z in new[] { -14f, 14f })
            {
                root.AddChild(CollidableSphere(0.5f, new Vector3(x, 0.5f, z), planterMat));
                root.AddChild(Sphere(0.8f, new Vector3(x, 1.2f, z), leafMat));
                root.AddChild(Sphere(0.5f, new Vector3(x + 0.4f, 1.5f, z), leafMat));
                root.AddChild(Sphere(0.5f, new Vector3(x - 0.4f, 1.5f, z), leafMat));
            }

        // Boundary walls — collidable so players can't fall off the edge.
        foreach (float edge in new[] { -19f, 19f })
        {
            root.AddChild(CollidableBox(new Vector3(0.5f, 1.5f, 40), new Vector3(edge, 0.75f, 0), Mat(new Color(0.1f, 0.11f, 0.14f))));
            root.AddChild(CollidableBox(new Vector3(40, 1.5f, 0.5f), new Vector3(0, 0.75f, edge), Mat(new Color(0.1f, 0.11f, 0.14f))));
        }
    }

    // ── World ID routing ─────────────────────────────────────────────────────────────

    /// Built-in world IDs — must match seed.ts.
    public const string IdCommons    = "00000000-0000-0000-0000-0000000000e0";
    public const string IdMirror     = "00000000-0000-0000-0000-0000000000e1";
    public const string IdHome       = "00000000-0000-0000-0000-0000000000e2";
    public const string IdVideo      = "00000000-0000-0000-0000-0000000000e3";
    public const string IdTestEmpty  = "00000000-0000-0000-0000-0000000000e4";
    public const string IdTestPillar = "00000000-0000-0000-0000-0000000000e5";
    public const string IdTestRamp   = "00000000-0000-0000-0000-0000000000e6";
    public const string IdTestColor  = "00000000-0000-0000-0000-0000000000e7";
    public const string IdTestSphere = "00000000-0000-0000-0000-0000000000e8";
    public const string IdBackrooms  = "00000000-0000-0000-0000-0000000000e9";
    public const string IdGryffindor = "00000000-0000-0000-0000-0000000000ea";

    /// Route a world ID to its builder. Falls back to Commons for unknown IDs.
    public static void BuildWorldForId(string worldId, Node3D root)
    {
        switch (worldId)
        {
            case IdMirror:     BuildMirrorWorld(root); break;
            case IdHome:       BuildHomeAsWorld(root); break;
            case IdVideo:      BuildVideoWorld(root); break;
            case IdTestEmpty:  BuildTestEmpty(root); break;
            case IdTestPillar: BuildTestPillars(root); break;
            case IdTestRamp:   BuildTestRamps(root); break;
            case IdTestColor:  BuildTestColors(root); break;
            case IdTestSphere: BuildTestSpheres(root); break;
            case IdBackrooms:  BuildBackrooms(root); break;
            case IdGryffindor: BuildGryffindor(root); break;
            default:           BuildCommons(root); break;
        }
    }

    // ── Mirror Gallery ──────────────────────────────────────────────────────────────

    /// A room lined with mirrors on every wall — a gallery for checking your avatar from
    /// every angle. Bright, even lighting so reflections are clear.
    public static void BuildMirrorWorld(Node3D root)
    {
        const float w = 14f, d = 14f, h = 4f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.12f, 0.10f, 0.14f),
                AmbientLightColor = new Color(0.8f, 0.78f, 0.85f),
                AmbientLightEnergy = 0.6f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        // Floor — polished dark.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0),
            Mat(new Color(0.08f, 0.07f, 0.09f), 0.15f, 0.3f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Ceiling.
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.15f, 0.13f, 0.17f))));

        // Walls — dark purple.
        var wallMat = Mat(new Color(0.18f, 0.15f, 0.22f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Mirrors on all four walls — two per wall for full coverage.
        // Back wall (-Z).
        root.AddChild(Mirror.Create(new Vector3(-3f, 0.05f, -d / 2 + 0.22f), 0f, 2.0f, 3.0f));
        root.AddChild(Mirror.Create(new Vector3(3f, 0.05f, -d / 2 + 0.22f), 0f, 2.0f, 3.0f));
        // Front wall (+Z).
        root.AddChild(Mirror.Create(new Vector3(-3f, 0.05f, d / 2 - 0.22f), 180f, 2.0f, 3.0f));
        root.AddChild(Mirror.Create(new Vector3(3f, 0.05f, d / 2 - 0.22f), 180f, 2.0f, 3.0f));
        // Left wall (-X).
        root.AddChild(Mirror.Create(new Vector3(-w / 2 + 0.22f, 0.05f, -3f), 90f, 2.0f, 3.0f));
        root.AddChild(Mirror.Create(new Vector3(-w / 2 + 0.22f, 0.05f, 3f), 90f, 2.0f, 3.0f));
        // Right wall (+X).
        root.AddChild(Mirror.Create(new Vector3(w / 2 - 0.22f, 0.05f, -3f), -90f, 2.0f, 3.0f));
        root.AddChild(Mirror.Create(new Vector3(w / 2 - 0.22f, 0.05f, 3f), -90f, 2.0f, 3.0f));

        // Bright even lighting from ceiling.
        foreach (float x in new[] { -4f, 0f, 4f })
            foreach (float z in new[] { -4f, 0f, 4f })
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.3f, z),
                    LightColor = new Color(0.95f, 0.93f, 1f),
                    LightEnergy = 1.0f,
                    OmniRange = 8f,
                    ShadowEnabled = true,
                });

        // Central pedestal with a spotlight — gives you something to stand on and see yourself.
        root.AddChild(CollidableCylinder(1.5f, 0.2f, new Vector3(0, 0.1f, 0), Mat(new Color(0.15f, 0.12f, 0.18f), 0.3f, 0.2f)));
        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(0, h - 0.3f, 0),
            LightColor = new Color(1f, 0.95f, 0.9f),
            LightEnergy = 2.0f,
            SpotRange = 12f,
            SpotAngle = 45f,
            ShadowEnabled = true,
        });
    }

    // ── Home as a multiplayer world ──────────────────────────────────────────────────

    /// The cosy Home house as a joinable multiplayer world — same geometry as the
    /// single-player Home but without the Commons portal (no travel from here).
    public static void BuildHomeAsWorld(Node3D root)
    {
        const float w = 9f, d = 8f, h = 3.2f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.06f, 0.05f, 0.07f),
                AmbientLightColor = new Color(0.5f, 0.42f, 0.36f),
                AmbientLightEnergy = 0.35f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.34f, 0.22f, 0.13f), 0.7f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.2f, 0.17f, 0.15f))));

        var beamMat = Mat(new Color(0.22f, 0.14f, 0.08f), 0.8f);
        for (int i = 0; i < 4; i++)
            root.AddChild(Box(new Vector3(0.15f, 0.15f, d), new Vector3(-w / 2 + 1.5f + i * 2f, h - 0.1f, 0), beamMat));

        var wallMat = Mat(new Color(0.62f, 0.55f, 0.48f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Rug.
        root.AddChild(Box(new Vector3(3.2f, 0.02f, 2.2f), new Vector3(0, 0.02f, 0.5f), Mat(new Color(0.5f, 0.15f, 0.18f), 0.95f)));

        // Couch.
        var couchMat = Mat(new Color(0.3f, 0.36f, 0.42f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.5f, 0.9f), new Vector3(0, 0.25f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.7f, 0.2f), new Vector3(0, 0.6f, 2.72f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(-1.2f, 0.55f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(1.2f, 0.55f, 2.3f), couchMat));

        // Coffee table.
        root.AddChild(CollidableBox(new Vector3(1.4f, 0.08f, 0.7f), new Vector3(0, 0.42f, 1.1f), Mat(new Color(0.28f, 0.18f, 0.11f), 0.5f, 0.1f)));

        // Fireplace.
        root.AddChild(CollidableBox(new Vector3(2.0f, 1.4f, 0.5f), new Vector3(0, 0.7f, -d / 2 + 0.3f), Mat(new Color(0.22f, 0.20f, 0.19f), 0.9f)));
        var fire = Box(new Vector3(1.4f, 0.7f, 0.2f), new Vector3(0, 0.5f, -d / 2 + 0.45f),
            new StandardMaterial3D { Emission = new Color(1f, 0.5f, 0.15f), EmissionEnergyMultiplier = 3f, AlbedoColor = new Color(1f, 0.5f, 0.15f) });
        fire.Name = "Fire";
        root.AddChild(fire);
        root.AddChild(new OmniLight3D
        {
            Position = new Vector3(0, 0.8f, -d / 2 + 0.8f),
            LightColor = new Color(1f, 0.6f, 0.3f),
            LightEnergy = 1.6f, OmniRange = 6f, ShadowEnabled = true,
        });

        // Warm lamp lights.
        foreach (var p in new[] { new Vector3(-2.5f, 2.6f, -1f), new Vector3(2.5f, 2.6f, 1.5f) })
            root.AddChild(new OmniLight3D { Position = p, LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 0.9f, OmniRange = 5.5f });

        // Window.
        root.AddChild(Box(new Vector3(0.05f, 1.4f, 1.8f), new Vector3(w / 2 - 0.1f, 1.7f, -0.5f),
            new StandardMaterial3D { Emission = new Color(0.6f, 0.7f, 0.95f), EmissionEnergyMultiplier = 1.2f, AlbedoColor = new Color(0.6f, 0.7f, 0.95f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(w / 2 - 0.8f, 1.7f, -0.5f), LightColor = new Color(0.7f, 0.8f, 1f), LightEnergy = 0.7f, OmniRange = 5f });

        // Ceiling lamp.
        root.AddChild(Box(new Vector3(0.03f, 0.8f, 0.03f), new Vector3(0, h - 0.4f, 1.1f), Mat(new Color(0.15f, 0.12f, 0.08f))));
        root.AddChild(Sphere(0.18f, new Vector3(0, h - 1.2f, 1.1f),
            new StandardMaterial3D { Emission = new Color(1f, 0.9f, 0.7f), EmissionEnergyMultiplier = 2f, AlbedoColor = new Color(1f, 0.9f, 0.7f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(0, h - 1.4f, 1.1f), LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 1.2f, OmniRange = 7f, ShadowEnabled = true });

        // Mirror on the back wall.
        root.AddChild(Mirror.Create(new Vector3(-2.8f, 0.05f, -d / 2 + 0.22f), 0f, 1.2f, 2.2f));
    }

    // ── Video Player World ───────────────────────────────────────────────────────────

    /// A cinema-style world with a large screen at one end and tiered seating.
    /// The screen uses an emissive material — a video stream can be wired in later.
    public static void BuildVideoWorld(Node3D root)
    {
        const float w = 20f, d = 24f, h = 8f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
                AmbientLightColor = new Color(0.15f, 0.12f, 0.18f),
                AmbientLightEnergy = 0.3f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        // Floor — dark carpet.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.05f, 0.04f, 0.06f), 0.95f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Ceiling.
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.08f, 0.07f, 0.10f))));

        // Walls.
        var wallMat = Mat(new Color(0.10f, 0.08f, 0.12f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Large screen on the back wall — emissive white for now.
        var screenMat = new StandardMaterial3D
        {
            Emission = new Color(0.9f, 0.9f, 0.95f),
            EmissionEnergyMultiplier = 1.5f,
            AlbedoColor = new Color(0.9f, 0.9f, 0.95f),
            Roughness = 0.1f,
        };
        var screen = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(12f, 6f) },
            Position = new Vector3(0, 4f, -d / 2 + 0.15f),
        };
        screen.MaterialOverride = screenMat;
        screen.Name = "VideoScreen";
        root.AddChild(screen);

        // Screen frame.
        var frameMat = Mat(new Color(0.04f, 0.03f, 0.05f), 0.5f);
        root.AddChild(Box(new Vector3(12.4f, 0.2f, 0.1f), new Vector3(0, 7.1f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(12.4f, 0.2f, 0.1f), new Vector3(0, 0.9f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(0.2f, 6.4f, 0.1f), new Vector3(-6.1f, 4f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(0.2f, 6.4f, 0.1f), new Vector3(6.1f, 4f, -d / 2 + 0.12f), frameMat));

        // Screen glow light.
        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(0, 4f, -d / 2 + 0.5f),
            LightColor = new Color(0.8f, 0.8f, 0.9f),
            LightEnergy = 1.5f,
            SpotRange = 20f,
            SpotAngle = 60f,
        });

        // Tiered seating — three rows of benches at increasing height.
        var seatMat = Mat(new Color(0.15f, 0.10f, 0.18f), 0.9f);
        for (int row = 0; row < 4; row++)
        {
            float z = -4f + row * 4f;
            float y = 0.3f + row * 0.4f;
            root.AddChild(CollidableBox(new Vector3(14f, 0.5f, 1.2f), new Vector3(0, y, z), seatMat));
        }

        // Ambient ceiling lights — dim, cinema vibe.
        foreach (float x in new[] { -7f, 0f, 7f })
            root.AddChild(new OmniLight3D
            {
                Position = new Vector3(x, h - 0.5f, 0),
                LightColor = new Color(0.3f, 0.25f, 0.35f),
                LightEnergy = 0.4f,
                OmniRange = 10f,
            });
    }

    // ── Test worlds ──────────────────────────────────────────────────────────────────

    /// Test: Empty Room — just a floor and four walls. Minimal.
    public static void BuildTestEmpty(Node3D root)
    {
        const float w = 20f, d = 20f, h = 6f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = SkyShader } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
                AmbientLightEnergy = 0.5f,
            },
        });

        root.AddChild(new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f, RotationDegrees = new Vector3(-50, -30, 0) });

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.25f, 0.25f, 0.28f), 0.9f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        var wallMat = Mat(new Color(0.3f, 0.3f, 0.33f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Grid lines on the floor for spatial reference.
        var gridMat = Mat(new Color(0.4f, 0.4f, 0.45f), 0.8f);
        for (int i = -8; i <= 8; i++)
        {
            root.AddChild(Box(new Vector3(w, 0.011f, 0.05f), new Vector3(0, 0.005f, i * 2f), gridMat));
            root.AddChild(Box(new Vector3(0.05f, 0.011f, d), new Vector3(i * 2f, 0.005f, 0), gridMat));
        }
    }

    /// Test: Pillar Room — floor with a grid of collidable pillars.
    public static void BuildTestPillars(Node3D root)
    {
        const float w = 24f, d = 24f, h = 8f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = SkyShader } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
                AmbientLightEnergy = 0.5f,
            },
        });

        root.AddChild(new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f, RotationDegrees = new Vector3(-50, -30, 0) });

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.2f, 0.2f, 0.23f), 0.9f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        var wallMat = Mat(new Color(0.25f, 0.25f, 0.28f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Grid of pillars.
        var pillarMat = Mat(new Color(0.35f, 0.32f, 0.38f), 0.7f);
        for (int x = -3; x <= 3; x++)
            for (int z = -3; z <= 3; z++)
            {
                if (x == 0 && z == 0) continue;
                root.AddChild(CollidableBox(new Vector3(0.8f, h, 0.8f),
                    new Vector3(x * 6f, h / 2, z * 6f), pillarMat));
            }
    }

    /// Test: Ramps — platforms connected by ramps at various angles.
    public static void BuildTestRamps(Node3D root)
    {
        const float w = 30f, d = 30f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = SkyShader } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
                AmbientLightEnergy = 0.5f,
            },
        });

        root.AddChild(new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f, RotationDegrees = new Vector3(-50, -30, 0) });

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.2f, 0.2f, 0.23f), 0.9f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Platforms at different heights.
        var platMat = Mat(new Color(0.3f, 0.28f, 0.33f), 0.8f);
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(-8f, 0.15f, -8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(8f, 2.15f, -8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(0f, 4.15f, 8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(-8f, 6.15f, 8f), platMat));

        // Ramps connecting them (rotated boxes approximating ramps).
        var rampMat = Mat(new Color(0.25f, 0.22f, 0.28f), 0.8f);
        // Ramp from floor to platform 1.
        var ramp1 = new StaticBody3D { Position = new Vector3(-8f, 1f, -4f), RotationDegrees = new Vector3(30, 0, 0) };
        var ramp1Mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3f, 0.2f, 6f) } };
        ramp1Mesh.MaterialOverride = rampMat;
        ramp1.AddChild(ramp1Mesh);
        ramp1.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3f, 0.2f, 6f) } });
        root.AddChild(ramp1);
    }

    /// Test: Color Grid — a floor of differently colored tiles.
    public static void BuildTestColors(Node3D root)
    {
        const float w = 24f, d = 24f, h = 6f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.05f, 0.05f, 0.08f),
                AmbientLightColor = new Color(0.6f, 0.6f, 0.65f),
                AmbientLightEnergy = 0.5f,
            },
        });

        // Floor with colored tiles.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        const int grid = 10;
        const float tileSize = 2f;
        for (int x = 0; x < grid; x++)
            for (int z = 0; z < grid; z++)
            {
                float px = -grid * tileSize / 2 + x * tileSize + tileSize / 2;
                float pz = -grid * tileSize / 2 + z * tileSize + tileSize / 2;
                var color = Color.FromHsv((float)(x + z) / (grid * 2), 0.5f, 0.6f);
                var tile = Box(new Vector3(tileSize, 0.1f, tileSize), new Vector3(px, -0.05f, pz), Mat(color, 0.6f));
                floor.AddChild(tile);
            }

        // Walls.
        var wallMat = Mat(new Color(0.15f, 0.13f, 0.18f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Ceiling lights.
        foreach (float x in new[] { -8f, 0f, 8f })
            foreach (float z in new[] { -8f, 0f, 8f })
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.5f, z),
                    LightColor = new Color(1f, 1f, 1f),
                    LightEnergy = 0.8f,
                    OmniRange = 10f,
                });
    }

    /// Test: Sphere Garden — lots of decorative spheres at various sizes/colors.
    public static void BuildTestSpheres(Node3D root)
    {
        const float w = 30f, d = 30f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = SkyShader } },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
                AmbientLightEnergy = 0.5f,
            },
        });

        root.AddChild(new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 0.8f, RotationDegrees = new Vector3(-50, -30, 0) });

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.15f, 0.17f, 0.20f), 0.9f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Spheres in a scattered pattern.
        var rng = new Random(42);
        for (int i = 0; i < 30; i++)
        {
            float x = (float)(rng.NextDouble() - 0.5) * 24f;
            float z = (float)(rng.NextDouble() - 0.5) * 24f;
            float r = 0.3f + (float)rng.NextDouble() * 1.5f;
            var color = Color.FromHsv((float)rng.NextDouble(), 0.4f, 0.6f);
            root.AddChild(CollidableSphere(r, new Vector3(x, r, z), Mat(color, 0.5f, 0.1f)));
        }

        // A few large decorative spheres (no collision).
        root.AddChild(Sphere(3f, new Vector3(0, 3f, 0), Mat(new Color(0.6f, 0.5f, 0.7f, 0.5f), 0.1f, 0.3f)));
        root.AddChild(Sphere(2f, new Vector3(-10f, 2f, -10f), Mat(new Color(0.5f, 0.6f, 0.5f, 0.5f), 0.1f, 0.3f)));
        root.AddChild(Sphere(2f, new Vector3(10f, 2f, 10f), Mat(new Color(0.7f, 0.5f, 0.5f, 0.5f), 0.1f, 0.3f)));
    }

    // ── Backrooms (placeholder) ──────────────────────────────────────────────────────

    /// A placeholder for the Backrooms VR world — yellow wallpaper maze vibe.
    /// Will be replaced with the actual 3D model when asset loading is implemented.
    public static void BuildBackrooms(Node3D root)
    {
        const float w = 30f, d = 30f, h = 3f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.15f, 0.13f, 0.05f),
                AmbientLightColor = new Color(0.6f, 0.55f, 0.2f),
                AmbientLightEnergy = 0.4f,
                FogEnabled = true,
                FogLightColor = new Color(0.2f, 0.18f, 0.08f),
                FogDensity = 0.01f,
            },
        });

        // Floor — yellowish carpet.
        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.35f, 0.32f, 0.12f), 0.95f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Ceiling — yellowish.
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.30f, 0.28f, 0.10f), 0.95f)));

        // Maze-like walls — yellow wallpaper.
        var wallMat = Mat(new Color(0.40f, 0.36f, 0.14f), 0.9f);
        // Outer boundary.
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Interior maze walls.
        for (int i = -2; i <= 2; i++)
        {
            root.AddChild(CollidableBox(new Vector3(0.2f, h, 8f), new Vector3(i * 8f, h / 2, -8f), wallMat));
            root.AddChild(CollidableBox(new Vector3(8f, h, 0.2f), new Vector3(-8f, h / 2, i * 8f), wallMat));
        }

        // Flickering fluorescent lights.
        foreach (float x in new[] { -10f, 0f, 10f })
            foreach (float z in new[] { -10f, 0f, 10f })
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.2f, z),
                    LightColor = new Color(1f, 0.95f, 0.6f),
                    LightEnergy = 0.7f,
                    OmniRange = 8f,
                });
    }

    // ── Gryffindor Common Room (placeholder) ─────────────────────────────────────────

    /// A placeholder for the Gryffindor Common Room — warm, cozy, fireplace vibe.
    /// Will be replaced with the actual 3D model when asset loading is implemented.
    public static void BuildGryffindor(Node3D root)
    {
        const float w = 16f, d = 14f, h = 5f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.08f, 0.05f, 0.03f),
                AmbientLightColor = new Color(0.5f, 0.35f, 0.2f),
                AmbientLightEnergy = 0.3f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        // Floor — dark wood.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.20f, 0.12f, 0.06f), 0.6f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        // Ceiling — dark with exposed beams.
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.12f, 0.08f, 0.04f))));
        var beamMat = Mat(new Color(0.10f, 0.06f, 0.03f), 0.8f);
        for (int i = 0; i < 5; i++)
            root.AddChild(Box(new Vector3(0.2f, 0.2f, d), new Vector3(-w / 2 + 2f + i * 3f, h - 0.1f, 0), beamMat));

        // Walls — warm red/gold.
        var wallMat = Mat(new Color(0.35f, 0.15f, 0.10f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Large fireplace.
        root.AddChild(CollidableBox(new Vector3(4f, 3f, 1f), new Vector3(0, 1.5f, -d / 2 + 0.6f), Mat(new Color(0.15f, 0.10f, 0.06f), 0.9f)));
        var fire = Box(new Vector3(2.5f, 1.5f, 0.2f), new Vector3(0, 1f, -d / 2 + 0.8f),
            new StandardMaterial3D { Emission = new Color(1f, 0.4f, 0.1f), EmissionEnergyMultiplier = 4f, AlbedoColor = new Color(1f, 0.4f, 0.1f) });
        fire.Name = "Fire";
        root.AddChild(fire);
        root.AddChild(new OmniLight3D
        {
            Position = new Vector3(0, 1.5f, -d / 2 + 1.5f),
            LightColor = new Color(1f, 0.5f, 0.2f),
            LightEnergy = 2.5f, OmniRange = 10f, ShadowEnabled = true,
        });

        // Seating — two long sofas facing the fire.
        var sofaMat = Mat(new Color(0.25f, 0.08f, 0.05f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(4f, 0.6f, 1.2f), new Vector3(-4f, 0.3f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.6f, 1.2f), new Vector3(4f, 0.3f, -3f), sofaMat));
        // Sofa backs.
        root.AddChild(CollidableBox(new Vector3(4f, 0.9f, 0.2f), new Vector3(-4f, 0.7f, -3.6f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.9f, 0.2f), new Vector3(4f, 0.7f, -3.6f), sofaMat));

        // Round table in the center.
        root.AddChild(CollidableCylinder(1.5f, 0.1f, new Vector3(0, 0.5f, 2f), Mat(new Color(0.18f, 0.10f, 0.05f), 0.5f)));

        // Warm wall sconces.
        foreach (var p in new[] { new Vector3(-5f, 3f, -d / 2 + 0.5f), new Vector3(5f, 3f, -d / 2 + 0.5f) })
        {
            root.AddChild(Sphere(0.15f, p, new StandardMaterial3D
            {
                Emission = new Color(1f, 0.7f, 0.3f), EmissionEnergyMultiplier = 3f, AlbedoColor = new Color(1f, 0.7f, 0.3f),
            }));
            root.AddChild(new OmniLight3D { Position = p, LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 1f, OmniRange = 6f });
        }

        // Bookshelves on side walls.
        var shelfMat = Mat(new Color(0.15f, 0.08f, 0.04f), 0.7f);
        root.AddChild(CollidableBox(new Vector3(0.5f, 3f, 4f), new Vector3(-w / 2 + 0.3f, 1.5f, 3f), shelfMat));
        root.AddChild(CollidableBox(new Vector3(0.5f, 3f, 4f), new Vector3(w / 2 - 0.3f, 1.5f, 3f), shelfMat));

        // Staircase hint on the right wall.
        var stairMat = Mat(new Color(0.16f, 0.10f, 0.05f), 0.7f);
        for (int i = 0; i < 6; i++)
            root.AddChild(CollidableBox(new Vector3(2f, 0.2f, 0.8f),
                new Vector3(w / 2 - 3f, 0.1f + i * 0.4f, d / 2 - 2f - i * 1f), stairMat));
    }
}
