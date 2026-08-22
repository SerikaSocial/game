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
        root.AddChild(Box(new Vector3(0.06f, 1.0f, 0.7f), new Vector3(-w / 2 + 0.12f, 1.7f, -2.5f), frameMat));
        var portraitArt = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.8f, 0.55f) },
            Position = new Vector3(-w / 2 + 0.15f, 1.7f, -2.5f),
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
        root.AddChild(Box(new Vector3(0.06f, 0.65f, 1.1f), new Vector3(-w / 2 + 0.12f, 1.5f, 0.8f), frameMat));
        var landscapeArt = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.5f, 0.95f) },
            Position = new Vector3(-w / 2 + 0.15f, 1.5f, 0.8f),
        };
        landscapeArt.MaterialOverride = landscapeMat;
        root.AddChild(landscapeArt);

        // Rug.
        var rug = Box(new Vector3(3.2f, 0.02f, 2.2f), new Vector3(0, 0.02f, 0.5f), Mat(new Color(0.5f, 0.15f, 0.18f), 0.95f));
        root.AddChild(rug);

        // Couch — base + backrest + two armrests + cushions (all collidable).
        var couchMat = Mat(new Color(0.3f, 0.36f, 0.42f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.5f, 0.9f), new Vector3(0, 0.25f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.7f, 0.2f), new Vector3(0, 0.6f, 2.72f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(-1.2f, 0.55f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(1.2f, 0.55f, 2.3f), couchMat));
        // Throw cushions.
        root.AddChild(Sphere(0.25f, new Vector3(-0.7f, 0.55f, 2.2f), Mat(new Color(0.6f, 0.4f, 0.5f), 0.95f)));
        root.AddChild(Sphere(0.25f, new Vector3(0.7f, 0.55f, 2.2f), Mat(new Color(0.4f, 0.5f, 0.6f), 0.95f)));

        // Coffee table (collidable).
        root.AddChild(CollidableBox(new Vector3(1.4f, 0.08f, 0.7f), new Vector3(0, 0.42f, 1.1f), Mat(new Color(0.28f, 0.18f, 0.11f), 0.5f, 0.1f)));
        foreach (var (lx, lz) in new[] { (-0.6f, -0.28f), (0.6f, -0.28f), (-0.6f, 0.28f), (0.6f, 0.28f) })
            root.AddChild(CollidableBox(new Vector3(0.08f, 0.42f, 0.08f), new Vector3(lx, 0.21f, 1.1f + lz), Mat(new Color(0.2f, 0.13f, 0.08f))));

        // Bookshelf against the left wall (collidable).
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

        // A small welcome sign.
        root.AddChild(new Label3D
        {
            Text = "Home",
            Position = new Vector3(0, 2.9f, -d / 2 + 0.35f),
            FontSize = 72, PixelSize = 0.006f,
            Modulate = new Color(0.9f, 0.82f, 0.7f),
        });

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

        var sign = new Label3D { Text = "The Commons", Position = new Vector3(0, 3.5f, -15), FontSize = 96, PixelSize = 0.01f };
        sign.Modulate = new Color(0.7f, 0.75f, 0.85f);
        root.AddChild(sign);

        // Boundary walls — collidable so players can't fall off the edge.
        foreach (float edge in new[] { -19f, 19f })
        {
            root.AddChild(CollidableBox(new Vector3(0.5f, 1.5f, 40), new Vector3(edge, 0.75f, 0), Mat(new Color(0.1f, 0.11f, 0.14f))));
            root.AddChild(CollidableBox(new Vector3(40, 1.5f, 0.5f), new Vector3(0, 0.75f, edge), Mat(new Color(0.1f, 0.11f, 0.14f))));
        }
    }
}
