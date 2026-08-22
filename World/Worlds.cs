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

        // Walls (plaster). Front wall has a gap acting as a doorway to the portal alcove.
        var wallMat = Mat(new Color(0.62f, 0.55f, 0.48f), 0.95f);
        root.AddChild(Box(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));       // back
        root.AddChild(Box(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));       // left
        root.AddChild(Box(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));        // right
        root.AddChild(Box(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));        // front

        // Rug.
        var rug = Box(new Vector3(3.2f, 0.02f, 2.2f), new Vector3(0, 0.02f, 0.5f), Mat(new Color(0.5f, 0.15f, 0.18f), 0.95f));
        root.AddChild(rug);

        // Couch — base + backrest + two armrests.
        var couchMat = Mat(new Color(0.3f, 0.36f, 0.42f), 0.9f);
        root.AddChild(Box(new Vector3(2.6f, 0.5f, 0.9f), new Vector3(0, 0.25f, 2.3f), couchMat));
        root.AddChild(Box(new Vector3(2.6f, 0.7f, 0.2f), new Vector3(0, 0.6f, 2.72f), couchMat));
        root.AddChild(Box(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(-1.2f, 0.55f, 2.3f), couchMat));
        root.AddChild(Box(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(1.2f, 0.55f, 2.3f), couchMat));

        // Coffee table.
        root.AddChild(Box(new Vector3(1.4f, 0.08f, 0.7f), new Vector3(0, 0.42f, 1.1f), Mat(new Color(0.28f, 0.18f, 0.11f), 0.5f, 0.1f)));
        foreach (var (lx, lz) in new[] { (-0.6f, -0.28f), (0.6f, -0.28f), (-0.6f, 0.28f), (0.6f, 0.28f) })
            root.AddChild(Box(new Vector3(0.08f, 0.42f, 0.08f), new Vector3(lx, 0.21f, 1.1f + lz), Mat(new Color(0.2f, 0.13f, 0.08f))));

        // Bookshelf against the left wall.
        var shelfMat = Mat(new Color(0.26f, 0.17f, 0.10f), 0.7f);
        root.AddChild(Box(new Vector3(0.4f, 2.4f, 1.8f), new Vector3(-w / 2 + 0.3f, 1.2f, -1.5f), shelfMat));
        for (int i = 0; i < 4; i++)
        {
            var books = Box(new Vector3(0.32f, 0.28f, 1.6f), new Vector3(-w / 2 + 0.32f, 0.5f + i * 0.55f, -1.5f),
                Mat(Color.FromHsv((i * 0.21f) % 1f, 0.4f, 0.55f)));
            root.AddChild(books);
        }

        // Fireplace on the back wall — hearth + emissive fire + firelight.
        root.AddChild(Box(new Vector3(2.0f, 1.4f, 0.5f), new Vector3(0, 0.7f, -d / 2 + 0.3f), Mat(new Color(0.22f, 0.20f, 0.19f), 0.9f)));
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
                Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
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

        foreach (float x in new[] { -15f, 15f })
            foreach (float z in new[] { -15f, 15f })
                root.AddChild(Box(new Vector3(1, 4, 1), new Vector3(x, 2, z), Mat(new Color(0.12f, 0.13f, 0.16f), 0.8f)));

        var platform = new MeshInstance3D
        {
            Mesh = new CylinderMesh { Height = 0.2f, TopRadius = 3, BottomRadius = 3 },
            Position = new Vector3(0, 0.1f, 0),
        };
        platform.MaterialOverride = Mat(new Color(0.22f, 0.24f, 0.28f), 0.7f);
        root.AddChild(platform);

        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            root.AddChild(Box(new Vector3(2, 0.4f, 0.6f), new Vector3(Mathf.Cos(a) * 5, 0.2f, Mathf.Sin(a) * 5),
                Mat(new Color(0.15f, 0.16f, 0.19f), 0.85f), -a * 180f / Mathf.Pi));
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

        var sign = new Label3D { Text = "The Commons", Position = new Vector3(0, 3.5f, -15), FontSize = 96, PixelSize = 0.01f };
        sign.Modulate = new Color(0.7f, 0.75f, 0.85f);
        root.AddChild(sign);

        foreach (float edge in new[] { -19f, 19f })
        {
            root.AddChild(Box(new Vector3(0.5f, 1.5f, 40), new Vector3(edge, 0.75f, 0), Mat(new Color(0.1f, 0.11f, 0.14f))));
            root.AddChild(Box(new Vector3(40, 1.5f, 0.5f), new Vector3(0, 0.75f, edge), Mat(new Color(0.1f, 0.11f, 0.14f))));
        }
    }
}
