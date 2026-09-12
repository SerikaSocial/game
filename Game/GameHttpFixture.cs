using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
namespace SerikaSocial.Game;
/// Loopback diagnostic only; production sessions always use their configured API.
internal sealed class GameHttpFixture : IDisposable
{
    private readonly HttpListener _listener = new();
    public readonly string Url;
    public volatile int Polls, Mode = 1, Role = -1, Round = 1, Place, LastTask = -1;
    public volatile int Phase = 1, Outcome, Count = 2, FinishReports, LastFinishRound;
    public long LastFinishStart;
    public long RoundStarted, MeetingEnds;
    public bool Running => _running;
    public long Started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10000;
    private bool _running;
    public GameHttpFixture()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url+"/"); _listener.Start(); _ = Serve();
    }
    private async Task Serve()
    {
        try
        {
            while (_listener.IsListening)
            {
                var ctx = await _listener.GetContextAsync();
                string route = ctx.Request.Url.AbsolutePath.Split('/')[^1];
                object result = new { ok = true };
                if (route == "start")
                {
                    using var json = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    Mode = json.RootElement.GetProperty("mode").GetInt32();
                    _running = true; Round = 1; Place = 0; Phase = 1; Outcome = 0; Started++;
                }
                else if (route == "state")
                {
                    Polls++;
                    if (!_running) { ctx.Response.StatusCode = 404; result = new { error = "no_session" }; }
                    else result = new { mode=Mode, phase=Phase, outcome=Outcome, round=Round, maxRounds=Mode==1?3:1,
                        startedAt=Started, roundStartedAt=RoundStarted>0?RoundStarted:Started, alive=Count==1?new[]{"me"}:new[]{"me","peer"},
                        aliveCount=Count,totalPlayers=Count, finishedCount=Place, meetingEndsAt=MeetingEnds, events=Phase==3?new object[]{new {type="ended",outcome=Outcome,winners=new[]{"me"}}}:Array.Empty<object>() };
                }
                else if (route == "me") result = new { alive=true, role=Role<0 ? (int?)null : Role,
                    tasks=LastTask<0?0:1,tasksRequired=5, completedTasks=LastTask<0?Array.Empty<int>():new[]{LastTask},place=Place };
                else if (route == "finish")
                {
                    using var json = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    LastFinishRound=json.RootElement.GetProperty("round").GetInt32();
                    LastFinishStart=json.RootElement.GetProperty("startedAt").GetInt64();
                    FinishReports++; Place=1; result=new {ok=true,place=1};
                    if(Mode==2 && Count==1) { Phase=3; Outcome=5; }
                }
                else if (route == "end-round")
                { if(Round>=3) { Phase=3; Outcome=3; } else { Round++; Place=0; } }
                else if (route == "abort") { Phase=3; Outcome=4; }
                else if (route == "report") { Phase=2; MeetingEnds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+45000; }
                else if (route == "close-meeting") { Phase=1; MeetingEnds=0; }
                else if (route == "task")
                {
                    using var json = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    LastTask=json.RootElement.GetProperty("taskId").GetInt32();
                }
                byte[] data=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
                ctx.Response.ContentType="application/json";ctx.Response.ContentLength64=data.Length;
                await ctx.Response.OutputStream.WriteAsync(data); ctx.Response.Close();
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() => _listener.Close();
}
