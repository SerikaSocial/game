using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Serika.Auth;

/// Authorization-code + PKCE login for a public client. The game holds no secret; security
/// comes from the code_verifier never leaving this process.
///
/// The loopback port is FIXED at 34517 because serika-accounts matches the redirect URI
/// exactly against the registered value — we cannot bind :0 and register whatever we got.
public sealed class PkceFlow
{
    public const int LoopbackPort = 34517;
    public const string RedirectUri = "http://127.0.0.1:34517/callback";

    public string Verifier { get; }
    public string Challenge { get; }
    public string State { get; }

    public PkceFlow()
    {
        Verifier = Base64Url(RandomBytes(32));
        Challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
        State = Base64Url(RandomBytes(16));
    }

    /// Build the browser URL. `accountsBaseUrl` + clientId identify the provider/client.
    public string AuthorizeUrl(string accountsBaseUrl, string clientId)
    {
        var b = new StringBuilder($"{accountsBaseUrl.TrimEnd('/')}/api/oauth/authorize");
        b.Append("?client_id=").Append(Uri.EscapeDataString(clientId));
        b.Append("&redirect_uri=").Append(Uri.EscapeDataString(RedirectUri));
        b.Append("&response_type=code");
        b.Append("&scope=").Append(Uri.EscapeDataString("profile email"));
        b.Append("&state=").Append(Uri.EscapeDataString(State));
        b.Append("&code_challenge=").Append(Uri.EscapeDataString(Challenge));
        b.Append("&code_challenge_method=S256");
        return b.ToString();
    }

    /// Start the loopback listener and wait for the browser redirect. Returns the auth code.
    /// Validates `state` to defend against a forged callback. Times out to avoid hanging
    /// forever if the user closes the browser.
    public async Task<string> WaitForCodeAsync(TimeSpan timeout)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{LoopbackPort}/");
        listener.Start();

        var getContext = listener.GetContextAsync();
        var completed = await Task.WhenAny(getContext, Task.Delay(timeout));
        if (completed != getContext)
            throw new TimeoutException("login timed out waiting for browser redirect");

        var ctx = await getContext;
        var query = ctx.Request.QueryString;
        string code = query["code"];
        string state = query["state"];
        string error = query["error"];

        // Respond so the browser tab shows something friendly, whatever happened.
        Respond(ctx, error == null && code != null
            ? "Signed in to Serika Social. You can close this tab."
            : $"Login failed: {error ?? "no code"}. You can close this tab.");

        if (error != null) throw new InvalidOperationException($"authorize error: {error}");
        if (code == null) throw new InvalidOperationException("no authorization code in callback");
        if (state != State) throw new InvalidOperationException("state mismatch — possible CSRF");
        return code;
    }

    private static void Respond(HttpListenerContext ctx, string message)
    {
        var html = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=utf-8><body style='font-family:sans-serif;padding:3rem'>{message}</body>");
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = html.Length;
        ctx.Response.OutputStream.Write(html, 0, html.Length);
        ctx.Response.Close();
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
