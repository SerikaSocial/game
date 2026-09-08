using System;
using System.Collections.Generic;
using Godot;
namespace SerikaSocial.Avatar;

public sealed partial class AnimRetargeter
{
    // Offline concert baking solves contacts against the supplied VRM's exact proportions.
    // Running those tracks through the direction retargeter a second time discards wrists,
    // finger articulation and the solved pelvis. Only an exact matching skeleton uses this path.
    private readonly List<(int Source, int Target, bool Position)> _bakedConcertBones = new();
    public bool UsesBakedConcertPose => _bakedConcertBones.Count > 0;
    private void ConfigureBakedConcert(Node source)
    {
        if (source.Name != "SERIKA_CONCERT_BAKED_V2" && source.FindChild("SERIKA_CONCERT_BAKED_V2", true, false) == null) return;
        var candidate = new List<(int Source, int Target, bool Position)>();
        foreach (var (role, target) in _targetRoleToBone) {
            if (role is "leftEye" or "rightEye") continue;
            int src = _srcSkeleton.FindBone(_target.GetBoneName(target));
            if (src < 0) { GD.PrintErr($"concert bake: missing exact bone {role}; using general retargeting"); return; }
            var sourceRest = _srcSkeleton.GetBoneRest(src);
            var targetRest = _target.GetBoneRest(target);
            if (sourceRest.Origin.DistanceTo(targetRest.Origin) > .0002f ||
                sourceRest.Basis.GetRotationQuaternion().AngleTo(targetRest.Basis.GetRotationQuaternion()) > .001f) {
                GD.PrintErr($"concert bake: rest mismatch at {role}; using general retargeting"); return;
            }
            candidate.Add((src, target, role == "hips"));
        }
        if (candidate.Count < 20) return;
        _bakedConcertBones.AddRange(candidate);
        GD.Print($"concert bake: exact supplied VRM pose enabled, {candidate.Count} bones; native wrists and contact-solved pelvis");
    }
    private void SampleBakedConcert()
    {
        foreach (var bone in _bakedConcertBones) {
            _target.SetBonePoseRotation(bone.Target, _srcSkeleton.GetBonePoseRotation(bone.Source).Normalized());
            if (bone.Position) _target.SetBonePosePosition(bone.Target, _srcSkeleton.GetBonePosePosition(bone.Source));
        }
    }
}
