using Godot;

namespace SerikaSocial.World;

/// C# fallback builders for built-in worlds. These are used when no cloud-hosted
/// .serikaworld asset is available for a given world ID. When a cloud asset exists
/// (downloadUrl set in the API), WorldLoader downloads and loads that instead.
public static partial class Worlds
{
    // ── Mirror Gallery ─────────────────────────────────────────────────────────────

    public static Vector3 BuildMirrorWorld(Node3D root)
    {
        const float w = 16f, d = 16f, h = 4.5f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.10f, 0.08f, 0.12f),
                AmbientLightColor = new Color(0.7f, 0.68f, 0.78f),
                AmbientLightEnergy = 0.5f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0),
            Mat(new Color(0.06f, 0.05f, 0.08f), 0.1f, 0.4f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        root.AddChild(Box(new Vector3(6f, 0.02f, 6f), new Vector3(0, 0.01f, 0),
            Mat(new Color(0.12f, 0.10f, 0.15f), 0.05f, 0.6f)));

        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.14f, 0.12f, 0.16f))));

        var wallMat = Mat(new Color(0.16f, 0.13f, 0.20f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        const float mirrorOffset = 0.35f;
        const float mw = 2.2f, mh = 3.2f;

        root.AddChild(Mirror.Create(new Vector3(-3.5f, 0.05f, -d / 2 + mirrorOffset), 0f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(3.5f, 0.05f, -d / 2 + mirrorOffset), 0f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(-3.5f, 0.05f, d / 2 - mirrorOffset), 180f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(3.5f, 0.05f, d / 2 - mirrorOffset), 180f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(-w / 2 + mirrorOffset, 0.05f, -3.5f), 90f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(-w / 2 + mirrorOffset, 0.05f, 3.5f), 90f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(w / 2 - mirrorOffset, 0.05f, -3.5f), -90f, mw, mh));
        root.AddChild(Mirror.Create(new Vector3(w / 2 - mirrorOffset, 0.05f, 3.5f), -90f, mw, mh));

        foreach (float x in new[] { -5f, 0f, 5f })
            foreach (float z in new[] { -5f, 0f, 5f })
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.3f, z),
                    LightColor = new Color(0.95f, 0.93f, 1f),
                    LightEnergy = 0.8f,
                    OmniRange = 9f,
                    ShadowEnabled = true,
                });

        root.AddChild(CollidableCylinder(1.8f, 0.15f, new Vector3(0, 0.075f, 0),
            Mat(new Color(0.14f, 0.11f, 0.17f), 0.2f, 0.3f)));
        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(0, h - 0.3f, 0),
            LightColor = new Color(1f, 0.95f, 0.9f),
            LightEnergy = 2.5f,
            SpotRange = 14f,
            SpotAngle = 50f,
            ShadowEnabled = true,
        });

        var colMat = Mat(new Color(0.13f, 0.10f, 0.16f), 0.6f);
        foreach (var (cx, cz) in new[] { (-w / 2 + 1f, -d / 2 + 1f), (w / 2 - 1f, -d / 2 + 1f), (-w / 2 + 1f, d / 2 - 1f), (w / 2 - 1f, d / 2 - 1f) })
            root.AddChild(CollidableBox(new Vector3(0.6f, h, 0.6f), new Vector3(cx, h / 2, cz), colMat));

        return new Vector3(0, 1f, 0);
    }

    // ── Cinema ──────────────────────────────────────────────────────────────────────

    public const string DefaultYoutubeVideoId = "01cgf4eb7us";

    public static Vector3 BuildVideoWorld(Node3D root)
    {
        const float w = 22f, d = 26f, h = 9f;

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
                AmbientLightColor = new Color(0.12f, 0.10f, 0.16f),
                AmbientLightEnergy = 0.25f,
                FogEnabled = false,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.04f, 0.03f, 0.05f), 0.95f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        root.AddChild(Box(new Vector3(1.5f, 0.02f, d), new Vector3(0, 0.01f, 0), Mat(new Color(0.08f, 0.06f, 0.10f), 0.9f)));
        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.06f, 0.05f, 0.08f))));

        var wallMat = Mat(new Color(0.08f, 0.06f, 0.10f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        // Large screen on the back wall.
        var screen = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(14f, 7f) },
            Position = new Vector3(0, 4.5f, -d / 2 + 0.15f),
        };
        screen.MaterialOverride = new StandardMaterial3D
        {
            Emission = new Color(0.9f, 0.9f, 0.95f),
            EmissionEnergyMultiplier = 2.0f,
            AlbedoColor = new Color(0.9f, 0.9f, 0.95f),
            Roughness = 0.08f,
        };
        screen.Name = "VideoScreen";
        root.AddChild(screen);

        // YouTube thumbnail loader.
        root.AddChild(new YouTubeScreen(screen, DefaultYoutubeVideoId));

        // Screen frame.
        var frameMat = Mat(new Color(0.03f, 0.02f, 0.04f), 0.4f);
        root.AddChild(Box(new Vector3(14.4f, 0.3f, 0.1f), new Vector3(0, 8.0f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(14.4f, 0.3f, 0.1f), new Vector3(0, 1.0f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(0.3f, 7.4f, 0.1f), new Vector3(-7.15f, 4.5f, -d / 2 + 0.12f), frameMat));
        root.AddChild(Box(new Vector3(0.3f, 7.4f, 0.1f), new Vector3(7.15f, 4.5f, -d / 2 + 0.12f), frameMat));

        // Curtains.
        var curtainMat = Mat(new Color(0.25f, 0.05f, 0.08f), 0.85f);
        root.AddChild(Box(new Vector3(1.5f, h - 0.5f, 0.15f), new Vector3(-8.5f, (h - 0.5f) / 2, -d / 2 + 0.1f), curtainMat));
        root.AddChild(Box(new Vector3(1.5f, h - 0.5f, 0.15f), new Vector3(8.5f, (h - 0.5f) / 2, -d / 2 + 0.1f), curtainMat));
        root.AddChild(Box(new Vector3(w, 1.0f, 0.15f), new Vector3(0, h - 0.5f, -d / 2 + 0.1f), curtainMat));

        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(0, 4.5f, -d / 2 + 0.5f),
            LightColor = new Color(0.7f, 0.7f, 0.8f),
            LightEnergy = 2.0f,
            SpotRange = 24f,
            SpotAngle = 70f,
        });

        // Tiered seating with SeatNodes.
        var seatMat = Mat(new Color(0.12f, 0.08f, 0.15f), 0.9f);
        for (int row = 0; row < 5; row++)
        {
            float z = -2f + row * 4.5f;
            float y = 0.3f + row * 0.35f;
            root.AddChild(CollidableBox(new Vector3(8f, 0.5f, 1.4f), new Vector3(-5f, y, z), seatMat));
            root.AddChild(CollidableBox(new Vector3(8f, 0.5f, 1.4f), new Vector3(5f, y, z), seatMat));
            root.AddChild(CollidableBox(new Vector3(8f, 0.8f, 0.2f), new Vector3(-5f, y + 0.35f, z + 0.6f), seatMat));
            root.AddChild(CollidableBox(new Vector3(8f, 0.8f, 0.2f), new Vector3(5f, y + 0.35f, z + 0.6f), seatMat));

            // Place seat interaction nodes on each row.
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sc = 0; sc < 3; sc++)
                {
                    var seat = new SeatNode
                    {
                        Position = new Vector3(sx * (2f + sc * 2.5f), y + 0.25f, z),
                        SeatLabel = $"Row {row + 1} Seat {sc + 1}",
                    };
                    root.AddChild(seat);
                }
            }
        }

        foreach (float z in new[] { -8f, 0f, 8f })
        {
            root.AddChild(new OmniLight3D
            {
                Position = new Vector3(-w / 2 + 1f, h - 1f, z),
                LightColor = new Color(0.2f, 0.15f, 0.25f),
                LightEnergy = 0.3f,
                OmniRange = 6f,
            });
            root.AddChild(new OmniLight3D
            {
                Position = new Vector3(w / 2 - 1f, h - 1f, z),
                LightColor = new Color(0.2f, 0.15f, 0.25f),
                LightEnergy = 0.3f,
                OmniRange = 6f,
            });
        }

        return new Vector3(0, 1f, 8f);
    }

    // ── Backrooms ───────────────────────────────────────────────────────────────────

    public static Vector3 BuildBackrooms(Node3D root)
    {
        const float w = 36f, d = 36f, h = 3f;

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
                FogDensity = 0.015f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });

        var floor = new StaticBody3D { Name = "Floor" };
        var floorMesh = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d) } };
        floorMesh.MaterialOverride = Mat(new Color(0.35f, 0.32f, 0.12f), 0.95f);
        floor.AddChild(floorMesh);
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.30f, 0.28f, 0.10f), 0.95f)));

        var wallMat = Mat(new Color(0.40f, 0.36f, 0.14f), 0.9f);

        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        for (int row = -2; row <= 2; row++)
        {
            float z = row * 8f;
            for (int col = -2; col <= 2; col++)
            {
                if (row == 0 && col == 0) continue;
                bool gapLeft = (row + col) % 2 == 0;
                float wallLen = 5f;
                float gapX = gapLeft ? -3f : 3f;
                root.AddChild(CollidableBox(new Vector3(wallLen, h, 0.2f),
                    new Vector3(col * 8f + gapX, h / 2, z), wallMat));
            }
        }

        for (float x = -12f; x <= 12f; x += 12f)
            for (float z = -12f; z <= 12f; z += 12f)
            {
                root.AddChild(Box(new Vector3(2f, 0.1f, 0.4f), new Vector3(x, h - 0.05f, z),
                    new StandardMaterial3D
                    {
                        Emission = new Color(1f, 0.95f, 0.6f),
                        EmissionEnergyMultiplier = 2f,
                        AlbedoColor = new Color(1f, 0.95f, 0.6f),
                    }));
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.2f, z),
                    LightColor = new Color(1f, 0.95f, 0.6f),
                    LightEnergy = 0.8f,
                    OmniRange = 10f,
                });
            }

        var tileMat = Mat(new Color(0.25f, 0.22f, 0.08f), 0.95f);
        for (float x = -15f; x <= 15f; x += 6f)
            for (float z = -15f; z <= 15f; z += 6f)
                root.AddChild(Box(new Vector3(5.8f, 0.02f, 5.8f), new Vector3(x, h - 0.06f, z), tileMat));

        return new Vector3(0, 1f, 0);
    }

    // ── Gryffindor Common Room ──────────────────────────────────────────────────────

    public static Vector3 BuildGryffindor(Node3D root)
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

        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, -0.05f, 0), Mat(new Color(0.20f, 0.12f, 0.06f), 0.6f)));
        floor.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
        root.AddChild(floor);

        var plankMat = Mat(new Color(0.15f, 0.09f, 0.04f), 0.6f);
        for (int i = 0; i < 8; i++)
            root.AddChild(Box(new Vector3(0.05f, 0.011f, d), new Vector3(-w / 2 + 2f + i * 2f, 0.005f, 0), plankMat));

        root.AddChild(Box(new Vector3(w, 0.1f, d), new Vector3(0, h, 0), Mat(new Color(0.12f, 0.08f, 0.04f))));
        var beamMat = Mat(new Color(0.10f, 0.06f, 0.03f), 0.8f);
        for (int i = 0; i < 5; i++)
            root.AddChild(Box(new Vector3(0.2f, 0.2f, d), new Vector3(-w / 2 + 2f + i * 3f, h - 0.1f, 0), beamMat));

        var wallMat = Mat(new Color(0.35f, 0.15f, 0.10f), 0.95f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        var stoneMat = Mat(new Color(0.15f, 0.10f, 0.06f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(4f, 3f, 1f), new Vector3(0, 1.5f, -d / 2 + 0.6f), stoneMat));
        root.AddChild(CollidableBox(new Vector3(4.5f, 0.3f, 1.2f), new Vector3(0, 3.1f, -d / 2 + 0.7f), stoneMat));
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

        var sofaMat = Mat(new Color(0.25f, 0.08f, 0.05f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(4f, 0.6f, 1.2f), new Vector3(-4f, 0.3f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.6f, 1.2f), new Vector3(4f, 0.3f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.9f, 0.2f), new Vector3(-4f, 0.7f, -3.6f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.9f, 0.2f), new Vector3(4f, 0.7f, -3.6f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.7f, 1.2f), new Vector3(-6f, 0.5f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.7f, 1.2f), new Vector3(6f, 0.5f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.7f, 1.2f), new Vector3(-2f, 0.5f, -3f), sofaMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.7f, 1.2f), new Vector3(2f, 0.5f, -3f), sofaMat));

        // Seat nodes on the sofas.
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sc = -1; sc <= 1; sc++)
            {
                var seat = new SeatNode
                {
                    Position = new Vector3(sx * (4f + sc * 1.3f), 0.3f, -3f),
                    SeatLabel = "Sofa",
                    SitYaw = sx > 0 ? 180f : 180f,
                };
                root.AddChild(seat);
            }
        }

        root.AddChild(CollidableCylinder(1.5f, 0.1f, new Vector3(0, 0.5f, 2f), Mat(new Color(0.18f, 0.10f, 0.05f), 0.5f)));
        root.AddChild(CollidableCylinder(0.3f, 0.5f, new Vector3(0, 0.25f, 2f), Mat(new Color(0.15f, 0.08f, 0.04f), 0.7f)));

        foreach (var p in new[] { new Vector3(-5f, 3f, -d / 2 + 0.5f), new Vector3(5f, 3f, -d / 2 + 0.5f) })
        {
            root.AddChild(Sphere(0.15f, p, new StandardMaterial3D
            {
                Emission = new Color(1f, 0.7f, 0.3f), EmissionEnergyMultiplier = 3f, AlbedoColor = new Color(1f, 0.7f, 0.3f),
            }));
            root.AddChild(new OmniLight3D { Position = p, LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 1f, OmniRange = 6f });
        }

        var shelfMat = Mat(new Color(0.15f, 0.08f, 0.04f), 0.7f);
        root.AddChild(CollidableBox(new Vector3(0.5f, 3f, 4f), new Vector3(-w / 2 + 0.3f, 1.5f, 3f), shelfMat));
        root.AddChild(CollidableBox(new Vector3(0.5f, 3f, 4f), new Vector3(w / 2 - 0.3f, 1.5f, 3f), shelfMat));
        for (int s = 0; s < 3; s++)
        {
            float sy = 0.5f + s * 1f;
            root.AddChild(Box(new Vector3(0.52f, 0.05f, 4f), new Vector3(-w / 2 + 0.3f, sy, 3f), shelfMat));
            root.AddChild(Box(new Vector3(0.52f, 0.05f, 4f), new Vector3(w / 2 - 0.3f, sy, 3f), shelfMat));
        }

        var stairMat = Mat(new Color(0.16f, 0.10f, 0.05f), 0.7f);
        for (int i = 0; i < 6; i++)
            root.AddChild(CollidableBox(new Vector3(2f, 0.2f, 0.8f),
                new Vector3(w / 2 - 3f, 0.1f + i * 0.4f, d / 2 - 2f - i * 1f), stairMat));

        root.AddChild(Box(new Vector3(5f, 0.02f, 3f), new Vector3(0, 0.01f, -1f),
            Mat(new Color(0.35f, 0.12f, 0.08f), 0.95f)));

        return new Vector3(0, 1f, 5f);
    }

    // ── Serika Home as multiplayer world ────────────────────────────────────────────

    public static Vector3 BuildHomeAsWorld(Node3D root)
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

        root.AddChild(Box(new Vector3(3.2f, 0.02f, 2.2f), new Vector3(0, 0.02f, 0.5f), Mat(new Color(0.5f, 0.15f, 0.18f), 0.95f)));

        var couchMat = Mat(new Color(0.3f, 0.36f, 0.42f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.5f, 0.9f), new Vector3(0, 0.25f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(2.6f, 0.7f, 0.2f), new Vector3(0, 0.6f, 2.72f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(-1.2f, 0.55f, 2.3f), couchMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, 0.6f, 0.9f), new Vector3(1.2f, 0.55f, 2.3f), couchMat));

        // Seat nodes on the couch.
        for (int sc = -1; sc <= 1; sc++)
        {
            var seat = new SeatNode
            {
                Position = new Vector3(sc * 0.8f, 0.25f, 2.3f),
                SeatLabel = "Couch",
                SitYaw = 180f,
            };
            root.AddChild(seat);
        }

        root.AddChild(CollidableBox(new Vector3(1.4f, 0.08f, 0.7f), new Vector3(0, 0.42f, 1.1f), Mat(new Color(0.28f, 0.18f, 0.11f), 0.5f, 0.1f)));

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

        foreach (var p in new[] { new Vector3(-2.5f, 2.6f, -1f), new Vector3(2.5f, 2.6f, 1.5f) })
            root.AddChild(new OmniLight3D { Position = p, LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 0.9f, OmniRange = 5.5f });

        root.AddChild(Box(new Vector3(0.05f, 1.4f, 1.8f), new Vector3(w / 2 - 0.1f, 1.7f, -0.5f),
            new StandardMaterial3D { Emission = new Color(0.6f, 0.7f, 0.95f), EmissionEnergyMultiplier = 1.2f, AlbedoColor = new Color(0.6f, 0.7f, 0.95f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(w / 2 - 0.8f, 1.7f, -0.5f), LightColor = new Color(0.7f, 0.8f, 1f), LightEnergy = 0.7f, OmniRange = 5f });

        root.AddChild(Box(new Vector3(0.03f, 0.8f, 0.03f), new Vector3(0, h - 0.4f, 1.1f), Mat(new Color(0.15f, 0.12f, 0.08f))));
        root.AddChild(Sphere(0.18f, new Vector3(0, h - 1.2f, 1.1f),
            new StandardMaterial3D { Emission = new Color(1f, 0.9f, 0.7f), EmissionEnergyMultiplier = 2f, AlbedoColor = new Color(1f, 0.9f, 0.7f) }));
        root.AddChild(new OmniLight3D { Position = new Vector3(0, h - 1.4f, 1.1f), LightColor = new Color(1f, 0.9f, 0.75f), LightEnergy = 1.2f, OmniRange = 7f, ShadowEnabled = true });

        root.AddChild(Mirror.Create(new Vector3(-2.8f, 0.05f, -d / 2 + 0.26f), 0f, 1.2f, 2.2f));

        root.AddChild(CollidableSphere(0.3f, new Vector3(w / 2 - 1f, 0.3f, 1.5f), Mat(new Color(0.35f, 0.22f, 0.15f), 0.8f)));
        root.AddChild(Sphere(0.5f, new Vector3(w / 2 - 1f, 0.8f, 1.5f), Mat(new Color(0.2f, 0.45f, 0.2f), 0.9f)));
        root.AddChild(Sphere(0.35f, new Vector3(w / 2 - 1.2f, 1.1f, 1.3f), Mat(new Color(0.25f, 0.5f, 0.25f), 0.9f)));
        root.AddChild(Sphere(0.3f, new Vector3(w / 2 - 0.8f, 1.0f, 1.7f), Mat(new Color(0.22f, 0.48f, 0.22f), 0.9f)));

        return new Vector3(0, 1f, 1.2f);
    }

    // ── Test worlds ─────────────────────────────────────────────────────────────────

    public static Vector3 BuildTestEmpty(Node3D root)
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

        var gridMat = Mat(new Color(0.4f, 0.4f, 0.45f), 0.8f);
        for (int i = -8; i <= 8; i++)
        {
            root.AddChild(Box(new Vector3(w, 0.011f, 0.05f), new Vector3(0, 0.005f, i * 2f), gridMat));
            root.AddChild(Box(new Vector3(0.05f, 0.011f, d), new Vector3(i * 2f, 0.005f, 0), gridMat));
        }

        root.AddChild(Sphere(0.5f, new Vector3(0, 0.05f, 0), new StandardMaterial3D
        {
            Emission = new Color(0.4f, 0.6f, 1f), EmissionEnergyMultiplier = 1.5f,
            AlbedoColor = new Color(0.4f, 0.6f, 1f), Roughness = 0.3f,
        }));

        return new Vector3(0, 1f, 0);
    }

    public static Vector3 BuildTestPillars(Node3D root)
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

        var pillarMat = Mat(new Color(0.35f, 0.32f, 0.38f), 0.7f);
        for (int x = -3; x <= 3; x++)
            for (int z = -3; z <= 3; z++)
            {
                if (x == 0 && z >= -1 && z <= 1) continue;
                root.AddChild(CollidableBox(new Vector3(0.8f, h, 0.8f),
                    new Vector3(x * 6f, h / 2, z * 6f), pillarMat));
            }

        return new Vector3(0, 1f, 0);
    }

    public static Vector3 BuildTestRamps(Node3D root)
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

        var platMat = Mat(new Color(0.3f, 0.28f, 0.33f), 0.8f);
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(-8f, 0.15f, -8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(8f, 2.15f, -8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(0f, 4.15f, 8f), platMat));
        root.AddChild(CollidableBox(new Vector3(4f, 0.3f, 4f), new Vector3(-8f, 6.15f, 8f), platMat));

        var rampMat = Mat(new Color(0.25f, 0.22f, 0.28f), 0.8f);
        var ramp1 = new StaticBody3D { Position = new Vector3(-8f, 1f, -4f), RotationDegrees = new Vector3(30, 0, 0) };
        var ramp1Mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3f, 0.2f, 6f) } };
        ramp1Mesh.MaterialOverride = rampMat;
        ramp1.AddChild(ramp1Mesh);
        ramp1.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3f, 0.2f, 6f) } });
        root.AddChild(ramp1);

        var ramp2 = new StaticBody3D { Position = new Vector3(0f, 2f, -8f), RotationDegrees = new Vector3(0, 0, -25) };
        var ramp2Mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(10f, 0.2f, 3f) } };
        ramp2Mesh.MaterialOverride = rampMat;
        ramp2.AddChild(ramp2Mesh);
        ramp2.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(10f, 0.2f, 3f) } });
        root.AddChild(ramp2);

        return new Vector3(0, 1f, -12f);
    }

    public static Vector3 BuildTestColors(Node3D root)
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

        var wallMat = Mat(new Color(0.15f, 0.13f, 0.18f), 0.9f);
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, -d / 2), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(-w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(0.2f, h, d), new Vector3(w / 2, h / 2, 0), wallMat));
        root.AddChild(CollidableBox(new Vector3(w, h, 0.2f), new Vector3(0, h / 2, d / 2), wallMat));

        foreach (float x in new[] { -8f, 0f, 8f })
            foreach (float z in new[] { -8f, 0f, 8f })
                root.AddChild(new OmniLight3D
                {
                    Position = new Vector3(x, h - 0.5f, z),
                    LightColor = new Color(1f, 1f, 1f),
                    LightEnergy = 0.8f,
                    OmniRange = 10f,
                });

        return new Vector3(0, 1f, 0);
    }

    public static Vector3 BuildTestSpheres(Node3D root)
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

        var rng = new System.Random(42);
        for (int i = 0; i < 30; i++)
        {
            float x = (float)(rng.NextDouble() - 0.5) * 24f;
            float z = (float)(rng.NextDouble() - 0.5) * 24f;
            if (x * x + z * z < 9f) continue;
            float r = 0.3f + (float)rng.NextDouble() * 1.5f;
            var color = Color.FromHsv((float)rng.NextDouble(), 0.4f, 0.6f);
            root.AddChild(CollidableSphere(r, new Vector3(x, r, z), Mat(color, 0.5f, 0.1f)));
        }

        root.AddChild(Sphere(3f, new Vector3(0, 3f, 0), Mat(new Color(0.6f, 0.5f, 0.7f, 0.5f), 0.1f, 0.3f)));
        root.AddChild(Sphere(2f, new Vector3(-10f, 2f, -10f), Mat(new Color(0.5f, 0.6f, 0.5f, 0.5f), 0.1f, 0.3f)));
        root.AddChild(Sphere(2f, new Vector3(10f, 2f, 10f), Mat(new Color(0.7f, 0.5f, 0.5f, 0.5f), 0.1f, 0.3f)));

        return new Vector3(0, 1f, 0);
    }
}

/// Fetches a YouTube video thumbnail via HTTPRequest and applies it to a MeshInstance3D
/// as an emissive texture.
internal sealed partial class YouTubeScreen : Node
{
    private readonly MeshInstance3D _screen;
    private readonly string _videoId;

    public YouTubeScreen(MeshInstance3D screen, string videoId)
    {
        _screen = screen;
        _videoId = videoId;
    }

    public override void _Ready()
    {
        var http = new HttpRequest();
        AddChild(http);
        http.RequestCompleted += OnThumbnailLoaded;
        http.Request($"https://img.youtube.com/vi/{_videoId}/maxresdefault.jpg");
        GD.Print($"YouTubeScreen: fetching thumbnail for video {_videoId}");
    }

    private void OnThumbnailLoaded(long result, long responseCode, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success || body.Length == 0)
        {
            GD.PrintErr($"YouTubeScreen: thumbnail fetch failed (result={result} code={responseCode})");
            return;
        }

        var image = new Image();
        var err = image.LoadJpgFromBuffer(body);
        if (err != Error.Ok)
        {
            GD.PrintErr($"YouTubeScreen: image decode failed ({err})");
            return;
        }

        var texture = ImageTexture.CreateFromImage(image);
        _screen.MaterialOverride = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Emission = new Color(1f, 1f, 1f),
            EmissionEnergyMultiplier = 2.5f,
            EmissionTexture = texture,
            Roughness = 0.08f,
        };
        GD.Print("YouTubeScreen: thumbnail applied to screen");
    }
}
