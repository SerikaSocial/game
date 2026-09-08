using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SerikaSocial.Avatar;

public partial class ConcertCheerDiagnostic : Node3D
{
    public override void _Ready() => Callable.From(Run).CallDeferred();
    private void Run()
    {
        string[] args = OS.GetCmdlineUserArgs();
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var cheer = AvatarLibrary.InstantiateOrDefault(Arg("--ska"));
        var control = AvatarLibrary.InstantiateOrDefault(Arg("--ska"));
        AddChild(cheer); AddChild(control);
        var checks = new List<object>(); bool passed = true;
        void Check(bool ok, string name) { passed &= ok; checks.Add(new { name, passed = ok }); GD.Print($"CHEER {(ok ? "PASS" : "FAIL")} {name}"); }
        var roles = cheer.RoleToBoneForDiagnostics();
        var armNames = new HashSet<string>(new[] {"leftShoulder","leftUpperArm","leftLowerArm","leftHand","rightShoulder","rightUpperArm","rightLowerArm","rightHand"});
        int head = cheer.BoneOf("head"), left = cheer.BoneOf("leftHand"), right = cheer.BoneOf("rightHand");
        float preservedMax = 0, maxStep = 0, maxLiftLeft = -100, maxLiftRight = -100, minSeparation=100;
        // Where the hands sit along the FACING axis. A humanoid rig faces -Z, so a hand in front
        // of the chest has a MORE NEGATIVE Z than the chest. The reported failure is the pump
        // throwing the arms behind the back, which nothing here measured.
        float worstBehind = -100; int chestBone = cheer.BoneOf("chest"); if (chestBone < 0) chestBone = cheer.BoneOf("spine");
        string preservedRole="", stepRole="";double stepTime=0;int overhead = 0; var previous = new Quaternion[cheer.Skeleton.GetBoneCount()];
        Transform3D[] Globals(AvatarInstance avatar) {
            var result = new Transform3D[avatar.Skeleton.GetBoneCount()];
            for (int j = 0; j < result.Length; j++) { int p = avatar.Skeleton.GetBoneParent(j); result[j] = p < 0 ? avatar.Skeleton.GetBonePose(j) : result[p] * avatar.Skeleton.GetBonePose(j); }
            return result;
        }
        cheer.SetConcertCheerEnabled(true);
        for (int i = 0; i < 240; i++) {
            cheer.SetConcertCheerClock(i / 60.0);
            cheer.Animate(1.0 / 60, 0, true); control.Animate(1.0 / 60, 0, true);
            foreach (var role in roles) {
                var q = cheer.Skeleton.GetBonePoseRotation(role.Value);
                if (!armNames.Contains(role.Key) && !role.Key.EndsWith("Eye") && !role.Key.Contains("Thumb") && !role.Key.Contains("Index") && !role.Key.Contains("Middle") && !role.Key.Contains("Ring") && !role.Key.Contains("Little")) {float angle=q.AngleTo(control.Skeleton.GetBonePoseRotation(role.Value));if(angle>preservedMax){preservedMax=angle;preservedRole=role.Key;}}
                if (i > 0 && armNames.Contains(role.Key)) {float angle=q.AngleTo(previous[role.Value]);if(angle>maxStep){maxStep=angle;stepRole=role.Key;stepTime=i/60.0;}}
                previous[role.Value] = q;
            }
            var g = Globals(cheer); float l = g[left].Origin.Y - g[head].Origin.Y, r = g[right].Origin.Y - g[head].Origin.Y;
            if (l > .1f && r > .1f) overhead++;
            if(i>20)minSeparation=Math.Min(minSeparation,g[left].Origin.DistanceTo(g[right].Origin));
            if (chestBone >= 0) {
                float cz = g[chestBone].Origin.Z;
                worstBehind = Math.Max(worstBehind, Math.Max(g[left].Origin.Z - cz, g[right].Origin.Z - cz));
            }
            maxLiftLeft = Math.Max(maxLiftLeft, l); maxLiftRight = Math.Max(maxLiftRight, r);
        }
        Check(cheer.ConcertCheerReady, "existing Victory arm cache is available on the supplied avatar");
        Check(overhead > 30 && maxLiftLeft > .2f && maxLiftRight > .2f, "both animated arms rise above the head together");
        Check(minSeparation>.18f,"mirrored hands retain clear separation across the complete pump");
        // Compare against the SAME avatar standing idle. The chest bone sits forward of the arms
        // at rest, so a hand "behind the chest origin" is normal and says nothing on its own.
        var cg = Globals(control);
        float idleBehind = chestBone >= 0
            ? Math.Max(cg[left].Origin.Z - cg[chestBone].Origin.Z, cg[right].Origin.Z - cg[chestBone].Origin.Z) : 0;
        GD.Print($"CHEER_REACH cheering={worstBehind:F3} m idle={idleBehind:F3} m (positive = behind the chest bone)");
        // The bar is the avatar's own resting arms, not an absolute number: cheering must not put
        // the hands further back than simply standing there does.
        Check(worstBehind <= idleBehind + .02f, "cheering never reaches further behind the body than standing at rest");
        int leftIndex=cheer.BoneOf("leftIndexProximal"),rightIndex=cheer.BoneOf("rightIndexProximal");
        bool hasFingerRig=leftIndex>=0&&rightIndex>=0;
        float leftCurl=hasFingerRig?cheer.Skeleton.GetBonePoseRotation(leftIndex).AngleTo(cheer.Skeleton.GetBoneRest(leftIndex).Basis.GetRotationQuaternion()):0;
        float rightCurl=hasFingerRig?cheer.Skeleton.GetBonePoseRotation(rightIndex).AngleTo(cheer.Skeleton.GetBoneRest(rightIndex).Basis.GetRotationQuaternion()):0;
        Check(!hasFingerRig||(leftCurl>.3f&&rightCurl>.3f),"available fingers curl around both grips during cheering");
        Check(preservedMax < .002f, "hips legs torso and head retain the ordinary standing pose");
        Check(maxStep < .35f, "looped mirrored arm tracks remain continuous at 60 Hz");
        for (int i = 0; i < 90; i++) { cheer.SetConcertCheerClock(4 + i / 60.0); cheer.Animate(1.0 / 60, 2, true); control.Animate(1.0 / 60, 2, true); }
        float walkingDifference = roles.Where(r=>!r.Key.EndsWith("Eye")).Select(r=>r.Value).Max(b => cheer.Skeleton.GetBonePoseRotation(b).AngleTo(control.Skeleton.GetBonePoseRotation(b)));
        Check(cheer.ConcertCheerEnabled && walkingDifference < .002f, "walking suppresses the arm overlay while retaining the automatic-cheer request");
        cheer.SetConcertCheerEnabled(false);
        for (int i = 0; i < 60; i++) { cheer.Animate(1.0 / 60, 0, true); control.Animate(1.0 / 60, 0, true); }
        float stoppedDifference = roles.Where(r=>!r.Key.EndsWith("Eye")).Select(r=>r.Value).Max(b => cheer.Skeleton.GetBonePoseRotation(b).AngleTo(control.Skeleton.GetBonePoseRotation(b)));
        Check(stoppedDifference < .002f, "disabling concert cheer leaves no residual arm pose");
        string path = Arg("--out");
        if (path != null) File.WriteAllText(path, JsonSerializer.Serialize(new { passed, checks, sourceClip="Victory", sourceDurationSeconds=1.666667, adaptation="mirrored existing right-arm track; lower body retained", overheadFrames=overhead,preservedRole,stepRole,stepTime, hasFingerRig,leftIndexCurlDegrees=Mathf.RadToDeg(leftCurl),rightIndexCurlDegrees=Mathf.RadToDeg(rightCurl),minimumHandSeparation=minSeparation,maxHandAboveHeadLeft=maxLiftLeft, maxHandAboveHeadRight=maxLiftRight, preservedBoneMaximumDifferenceDegrees=Mathf.RadToDeg(preservedMax), maxArmStepDegrees=Mathf.RadToDeg(maxStep), walkingMaximumDifferenceDegrees=Mathf.RadToDeg(walkingDifference), stoppedMaximumDifferenceDegrees=Mathf.RadToDeg(stoppedDifference) }, new JsonSerializerOptions { WriteIndented = true }));
        GetTree().Quit(passed ? 0 : 1);
    }
}
