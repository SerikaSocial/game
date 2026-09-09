using System;
using System.Collections.Generic;
using Godot;

namespace SerikaSocial.Events;

public partial class EventShowPlayer
{
    private readonly List<(MeshInstance3D Node, Mesh Original, Material Material, float Margin)> _crowdMotionMeshes = new();
    private readonly List<ShaderMaterial> _crowdBodies = new();
    private readonly record struct CrowdPerson(Vector3 Head, float Ground, float Seed);
    public int CrowdPersonCount { get; private set; }
    public int CrowdBodyBatchCount => _crowdBodies.Count;

    private void SetupCrowdMotion(Node3D world)
    {
        var cells = new Dictionary<Vector2I, List<CrowdPerson>>();
        var shader = GD.Load<Shader>("res://Shaders/concert_crowd_body.gdshader");
        var bodies = world.FindChildren("SERIKA_EVENT_CROWD_BODIES_*", "MeshInstance3D", true, false);
        foreach (MeshInstance3D body in bodies) {
            if (body.Mesh is not ArrayMesh mesh) continue;
            bool seated = body.Name.ToString().Contains("TRIBUNE");
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++) {
                var arrays = mesh.SurfaceGetArrays(surface);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                var parent = new int[vertices.Length];
                var unique = new Dictionary<Vector3, int>();
                for (int i = 0; i < vertices.Length; i++) {
                    parent[i] = i;
                    if (unique.TryGetValue(vertices[i], out int first)) parent[i] = first;
                    else unique.Add(vertices[i], i);
                }
                int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                for (int i = 0; i + 2 < indices.Length; i += 3) {
                    int root = Root(indices[i]);
                    parent[Root(indices[i + 1])] = root;
                    parent[Root(indices[i + 2])] = root;
                }
                var components = new Dictionary<int, List<Vector3>>();
                foreach (var pair in unique) {
                    int root = Root(pair.Value);
                    if (!components.TryGetValue(root, out var points)) components[root] = points = new();
                    points.Add(pair.Key);
                }
                foreach (var points in components.Values) {
                    if (points.Count != 6) continue;
                    var bounds = new Aabb(points[0], Vector3.Zero);
                    foreach (var point in points) bounds = bounds.Expand(point);
                    if (bounds.Size.Y < .27f || bounds.Size.Y > .38f) continue;
                    var head = body.ToGlobal(bounds.GetCenter());
                    float scale = bounds.Size.Y / .322f;
                    float seed = Mathf.PosMod(head.X * .7548777f + head.Z * .5698403f, 1f) + .001f;
                    var person = new CrowdPerson(head, head.Y - (seated ? 1.07f : 1.51f) * scale, seated ? -seed : seed);
                    var cell = CrowdCell(head);
                    if (!cells.TryGetValue(cell, out var people)) cells[cell] = people = new();
                    people.Add(person); CrowdPersonCount++;
                }
            }
        }
        if (CrowdPersonCount == 0) { GD.PushError("Concert crowd has no identifiable spectator heads"); return; }
        foreach (MeshInstance3D body in bodies) {
            if (body.Mesh is not ArrayMesh mesh) continue;
            var material = new ShaderMaterial { Shader = shader };
            _crowdBodies.Add(material);
            _crowdMotionMeshes.Add((body, mesh, body.MaterialOverride, body.ExtraCullMargin));
            body.Mesh = AnnotateCrowdMesh(body, mesh, cells, false);
            body.MaterialOverride = material;
            body.ExtraCullMargin = Math.Max(body.ExtraCullMargin, .8f);
        }
        foreach (MeshInstance3D sticks in world.FindChildren("SERIKA_EVENT_CROWD_STICKS_*", "MeshInstance3D", true, false)) {
            if (sticks.Mesh is not ArrayMesh mesh) continue;
            _crowdMotionMeshes.Add((sticks, mesh, sticks.MaterialOverride, sticks.ExtraCullMargin));
            sticks.Mesh = AnnotateCrowdMesh(sticks, mesh, cells, true);
        }
        GD.Print($"CONCERT_CROWD_MOTION people={CrowdPersonCount} body_batches={_crowdBodies.Count}");
    }

    private static Vector2I CrowdCell(Vector3 p) => new(Mathf.FloorToInt(p.X / 2), Mathf.FloorToInt(p.Z / 2));

    private static ArrayMesh AnnotateCrowdMesh(MeshInstance3D node, ArrayMesh source, Dictionary<Vector2I, List<CrowdPerson>> cells, bool sticks)
    {
        var result = new ArrayMesh();
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++) {
            var arrays = source.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var colors = sticks ? arrays[(int)Mesh.ArrayType.Color].AsColorArray() : new Color[vertices.Length];
            var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (uv.Length != vertices.Length) uv = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) {
                var p = sticks ? new Vector3(colors[i].R * 160 - 80, colors[i].G * 16, colors[i].B * 160 - 20) : vertices[i];
                p = node.ToGlobal(p);
                var cell = CrowdCell(p); float distance = float.PositiveInfinity; CrowdPerson nearest = default;
                for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) {
                    if (!cells.TryGetValue(cell + new Vector2I(x, z), out var people)) continue;
                    foreach (var person in people) {
                        float d = new Vector2(p.X - person.Head.X, p.Z - person.Head.Z).LengthSquared();
                        if (p.Y < person.Ground - .2f || p.Y > person.Head.Y + .8f || d >= distance) continue;
                        distance = d; nearest = person;
                    }
                }
                if (!float.IsFinite(distance)) continue;
                var ground = node.ToLocal(new Vector3(nearest.Head.X, nearest.Ground, nearest.Head.Z));
                colors[i] = sticks ? new Color(colors[i].R, colors[i].G, colors[i].B, ground.Y / 16)
                    : new Color((ground.X + 80) / 160, ground.Y / 16, (ground.Z + 20) / 160, 1);
                uv[i].X = nearest.Seed;
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(surface), arrays);
            result.SurfaceSetMaterial(surface, source.SurfaceGetMaterial(surface));
        }
        return result;
    }

    private void RestoreCrowdMotion()
    {
        foreach (var entry in _crowdMotionMeshes) if (IsInstanceValid(entry.Node)) {
            entry.Node.Mesh = entry.Original; entry.Node.MaterialOverride = entry.Material; entry.Node.ExtraCullMargin = entry.Margin;
        }
        _crowdMotionMeshes.Clear(); _crowdBodies.Clear(); CrowdPersonCount = 0;
    }
}
