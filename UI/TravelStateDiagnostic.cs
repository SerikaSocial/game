using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Serika.Net;

namespace SerikaSocial.UI;

/// Checks the actual Main failure/deferred handlers without logging in or changing a world.
public partial class TravelStateDiagnostic : Node
{
    private readonly List<object> _checks = new();
    private int _failures;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public override async void _Ready()
    {
        Main main = new(); // Deliberately unmounted: do not boot auth, updater or a world.
        try
        {
            var travel = (WorldTravelOperation)Get(main, "_travel");
            Check("Home acquires shared travel", travel.TryBegin(WorldTravelOperation.Destination.Home, out int home));
            Check("join cannot overlap Home", !travel.TryBegin(WorldTravelOperation.Destination.Instance, out _));
            Set(main, "_api", new ApiClient("http://127.0.0.1:1"));
            await (Task)Call(main, "JoinWorldById", "9608b154-97f5-4d78-9096-ecbb99ce72d5", false);
            await (Task)Call(main, "JoinExactInstance", "11111111-1111-4111-8111-111111111111", null);
            Check("both join entry points preserve in-flight Home", travel.IsCurrent(home, WorldTravelOperation.Destination.Home));
            travel.Complete(home); travel.TryBegin(WorldTravelOperation.Destination.Instance, out int join);
            Call(main, "EnterHome");
            Check("Home entry cannot overlap instance join", Get(main, "_enterHomeTask") == null);
            Set(main, "_preparedHomeVersion", home);
            Call(main, "EnterPreparedHome");
            Check("late Home callback cannot replace instance", travel.IsCurrent(join, WorldTravelOperation.Destination.Instance) && !(bool)Get(main, "_inHome"));
            InputMode.ReleaseAll(); InputMode.SetPlayable(false);
            Call(main, "OnJoinFailed", "stale error", home);
            Check("late failure cannot release newer travel", travel.IsCurrent(join, WorldTravelOperation.Destination.Instance) && !InputMode.Playable);
            Call(main, "OnJoinReady", home);
            Check("late ready callback cannot change scene", travel.IsCurrent(join, WorldTravelOperation.Destination.Instance) && Get(main,"_currentWorldId") == null);
            Set(main, "_inHome", true);
            Call(main, "OnJoinFailed", "invite_required", join);
            Check("denied join restores existing Home controls", InputMode.ControlsLive && !travel.Active);
            Set(main, "_inHome", false); Set(main, "_inWorld", true);
            travel.TryBegin(WorldTravelOperation.Destination.Instance, out join);
            InputMode.SetPlayable(false);
            Call(main, "OnJoinFailed", "download_failed", join);
            Check("download failure restores existing multiplayer controls", InputMode.ControlsLive && !travel.Active);
            travel.TryBegin(WorldTravelOperation.Destination.Instance, out join);
            InputMode.Hold(InputMode.Chat); InputMode.SetPlayable(false);
            Call(main, "OnJoinFailed", "download_failed", join);
            Check("failure preserves another input owner", InputMode.Playable && InputMode.HasHold(InputMode.Chat) && !InputMode.ControlsLive);
            InputMode.ReleaseAll();
            travel.TryBegin(WorldTravelOperation.Destination.Instance, out join);
            Set(main, "_transportTravelVersion", join - 1);
            Call(main, "OnTransportRejected", "old session timed out");
            Check("old relay loss cannot start competing Home", travel.IsCurrent(join, WorldTravelOperation.Destination.Instance) && Get(main,"_enterHomeTask") == null);
            travel.Complete(join);
            Set(main, "_worldDetailRequest", 3);
            Call(main, "CloseOpenMenu");
            Check("closing menus invalidates pending world detail", (int)Get(main,"_worldDetailRequest") == 4);
            Set(main, "_api", new ApiClient("http://127.0.0.1:1"));
            Call(main, "OnDiscordJoinRequested", "9608b154-97f5-4d78-9096-ecbb99ce72d5");
            Check("Discord join waits for completed login", ((DeepLink.Intent)Get(main,"_pendingIntent")).Kind == DeepLink.Kind.World && !travel.Active);
            foreach (var (uri, kind) in new[] {
                ("serikasocial://instance/11111111-1111-4111-8111-111111111111", DeepLink.Kind.Instance),
                ("SERIKASOCIAL://instance/11111111-1111-4111-8111-111111111111/", DeepLink.Kind.Instance),
                ("serikasocial://world/9608b154-97f5-4d78-9096-ecbb99ce72d5", DeepLink.Kind.World),
                ("serikasocial://home", DeepLink.Kind.Home),
                ("serikasocial://instance/not-an-instance", DeepLink.Kind.None),
                ("xxxxxxxxxxxxxxxinstance/11111111-1111-4111-8111-111111111111", DeepLink.Kind.None),
                ("serikasocial://instance/11111111-1111-4111-8111-111111111111/other", DeepLink.Kind.None),
                ("serikasocial://home/other", DeepLink.Kind.None),
            }) Check("link grammar " + uri, DeepLink.Parse(uri).Kind == kind);
        }
        catch(Exception error) { Check("diagnostic completed", false, error.ToString()); }
        finally
        {
            InputMode.ReleaseAll(); InputMode.SetPlayable(false); main.Free();
            string path = System.Environment.GetEnvironmentVariable("SERIKA_TRAVEL_REPORT");
            if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                new { failures = _failures, checks = _checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"TRAVELSTATE complete checks={_checks.Count} failures={_failures}");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
    }
    private static object Get(object o, string name) => o.GetType().GetField(name, Private).GetValue(o);
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, Private).SetValue(o, value);
    private static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Private).Invoke(o, args);
    private void Check(string name, bool pass, string detail = "")
    {
        _checks.Add(new { name, pass, detail }); if (!pass) _failures++;
        GD.Print($"TRAVELSTATE {(pass ? "PASS" : "FAIL")} {name} {detail}");
    }
}
