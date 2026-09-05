using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

namespace SerikaSocial.World;

/// Load the final bundle and test authored visitor routes against the shipping physics path.
/// SERIKA_WORLD_BUNDLE=/absolute/world.serikaworld
/// SERIKA_WORLD_ROUTES=/absolute/routes.json
/// Godot --headless --path game res://World/WorldAccessDiagnostic.tscn
/// Route JSON: {"routes":[{"name":"Approach","points":[[x,y,z], ...]}]}; Godot Y-up metres.
/// Points describe walking-surface height. Each segment tests a 0.6 m wide, 1.7 m tall capsule
/// plus ground continuity at 0.25 m intervals; doors, corners and ramps need route points.
/// Floor-following capsule placement and upper-body sweeps distinguish walkable slope contacts
/// from obstructed doors, furniture and low beams. One long diagonal sweep would cut through
/// the floor at any change from a flat landing to a descending ramp.
public partial class WorldAccessDiagnostic : Node3D
{
    public override async void _Ready()
    {
        int checks = 0, failures = 0;
        void Check(bool ok, string message)
        {
            checks++;
            if (!ok) failures++;
            GD.Print($"WORLD_ACCESS_TEST: {(ok ? "PASS" : "FAIL")} {message}");
        }
        try
        {
            string bundle = System.Environment.GetEnvironmentVariable("SERIKA_WORLD_BUNDLE");
            string routesPath = System.Environment.GetEnvironmentVariable("SERIKA_WORLD_ROUTES");
            if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(routesPath))
                throw new InvalidOperationException("Set SERIKA_WORLD_BUNDLE and SERIKA_WORLD_ROUTES");
            var spawn = WorldLoader.LoadFromPath(bundle, "access-test", this);
            Check(spawn.HasValue, "bundle loaded with a spawn");
            var nodes = Descendants(this).ToList();
            var proxies = nodes.OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("COL_")).ToList();
            Check(proxies.Count > 0 && proxies.All(m => !m.Visible), $"{proxies.Count} collision proxies hidden");
            var statics = nodes.OfType<StaticBody3D>().ToList();
            Check(statics.Count == proxies.Count, $"{statics.Count} static bodies use only authored proxies");
            var visuals = nodes.OfType<MeshInstance3D>().Where(m => !m.Name.ToString().StartsWith("COL_") && m.Mesh != null && !IsRuntimeSurface(m)).ToList();
            Check(visuals.All(m => Enumerable.Range(0, m.Mesh.GetSurfaceCount()).All(s => m.GetActiveMaterial(s) is BaseMaterial3D)),
                  $"{visuals.Count} visual meshes retain imported materials");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var space = GetWorld3D().DirectSpaceState;
            var capsule = new CapsuleShape3D { Radius = .30f, Height = 1.70f };
            var upperBody = new CapsuleShape3D { Radius = .30f, Height = 1.30f };
            using var document = JsonDocument.Parse(File.ReadAllText(routesPath));
            int routes = 0;
            foreach (var route in document.RootElement.GetProperty("routes").EnumerateArray())
            {
                string name = route.GetProperty("name").GetString();
                var points = route.GetProperty("points").EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToArray();
                if (points.Length < 2) throw new InvalidOperationException($"Route {name} needs at least two points");
                bool groundOk = true, clearanceOk = true;
                string groundIssue = "", clearanceIssue = "";
                Vector3? previousGround = null;
                for (int i = 1; i < points.Length; i++)
                {
                    var from = points[i - 1];
                    var to = points[i];
                    int count = Mathf.Max(1, Mathf.CeilToInt(from.DistanceTo(to) / .25f));
                    for (int j = 0; j <= count; j++)
                    {
                        var p = from.Lerp(to, (float)j / count);
                        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * .30f, p - Vector3.Up * .30f));
                        if (hit.Count == 0 || Mathf.Abs(hit["position"].AsVector3().Y - p.Y) > .12f || hit["normal"].AsVector3().Y < .70f)
                        {
                            groundOk = false;
                            var broad = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 4, p - Vector3.Up * 4));
                            groundIssue = $" at segment {i}, {p}; nearby floor=" + (broad.Count > 0
                                ? $"{broad["position"].AsVector3()}, normal={broad["normal"].AsVector3()}, body={((Node)broad["collider"].AsGodotObject()).Name}"
                                : "none within 4m");
                            previousGround = null; continue;
                        }
                        var floor = hit["position"].AsVector3();
                        var standing = new PhysicsShapeQueryParameters3D
                        {
                            Shape = capsule,
                            Transform = new Transform3D(Basis.Identity, floor + Vector3.Up * .89f),
                            CollisionMask = 1, Margin = .005f,
                        };
                        var overlap = space.GetRestInfo(standing);
                        if (overlap.Count > 0 && overlap["normal"].AsVector3().Y < .70f)
                        {
                            clearanceOk = false;
                            clearanceIssue = $" standing at segment {i}, {floor}, normal {overlap["normal"].AsVector3()}";
                        }
                        if (previousGround.HasValue && floor.DistanceTo(previousGround.Value) > .001f)
                        {
                            var start = previousGround.Value;
                            var query = new PhysicsShapeQueryParameters3D
                            {
                                Shape = upperBody,
                                Transform = new Transform3D(Basis.Identity, start + Vector3.Up * 1.10f),
                                Motion = floor - start, CollisionMask = 1, Margin = .005f,
                            };
                            var motion = space.CastMotion(query);
                            if (motion.Length < 2 || motion[0] < .995f)
                            {
                                clearanceOk = false;
                                clearanceIssue = $" upper-body sweep at segment {i} ({start} -> {floor}), free fraction {(motion.Length > 0 ? motion[0] : -1):0.000}";
                            }
                        }
                        previousGround = floor;
                    }
                }
                Check(groundOk, $"{name}: continuous floor" + groundIssue);
                Check(clearanceOk, $"{name}: standing-player clearance" + clearanceIssue);
                routes++;
            }
            Check(routes > 0, $"{routes} visitor routes tested");
            GD.Print($"WORLD_ACCESS_TEST: {(failures == 0 ? "PASS" : "FAIL")} ({checks} checks, {failures} failures)");
            GetTree().Quit(failures == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            GD.PrintErr($"WORLD_ACCESS_TEST: {e.Message}");
            GetTree().Quit(1);
        }
    }

    private static bool IsRuntimeSurface(Node node)
    {
        // These components own shaders generated after imported-material validation. The
        // source GLB meshes still must retain core PBR materials; an arbitrary shader fails.
        for (Node parent = node; parent != null; parent = parent.GetParent())
            if (parent is Portal || parent is Mirror) return true;
        return false;
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
