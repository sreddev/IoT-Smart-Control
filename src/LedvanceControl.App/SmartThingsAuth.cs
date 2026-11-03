using System.Diagnostics;
using System.Net;
using System.Text;
using LedvanceControl.Core.Cloud;

namespace LedvanceControl.App;

/// <summary>
/// Runs the SmartThings OAuth 2.0 authorization-code flow for a desktop app:
/// opens the consent page in the browser and catches the redirect on a loopback HTTP listener.
/// </summary>
public static class SmartThingsAuth
{
    public static async Task ConnectAsync(SmartThingsClient client, string clientId, string redirectUri, CancellationToken ct = default)
    {
        var uri = new Uri(redirectUri);
        var prefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Could not listen on {prefix}. Pick a free port in the redirect URI, or use the manual option. ({ex.Message})");
        }

        var state = Guid.NewGuid().ToString("N");
        var authUrl = SmartThingsClient.BuildAuthorizeUrl(clientId, redirectUri, state);
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        try
        {
            var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
            var q = ctx.Request.QueryString;
            Respond(ctx, q["error"] != null
                ? $"Authorization failed: {q["error"]}. You can close this window."
                : "LEDVANCE Control is now connected to SmartThings. You can close this window.");

            if (q["error"] != null) throw new InvalidOperationException($"SmartThings denied authorization: {q["error"]}");
            var code = q["code"] ?? throw new InvalidOperationException("No authorization code was returned.");
            if (q["state"] != state) throw new InvalidOperationException("OAuth state mismatch (possible tampering); aborted.");
            await client.ExchangeCodeAsync(code, redirectUri, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Fallback for environments where the loopback redirect can't be registered: the user pastes the redirected URL (or the code).</summary>
    public static Task ConnectManualAsync(SmartThingsClient client, string pastedUrlOrCode, string redirectUri, CancellationToken ct = default)
    {
        var text = pastedUrlOrCode.Trim();
        string code = text;
        int idx = text.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            code = text[(idx + 5)..];
            int amp = code.IndexOf('&');
            if (amp >= 0) code = code[..amp];
            code = Uri.UnescapeDataString(code);
        }
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("No code found in the pasted text.");
        return client.ExchangeCodeAsync(code, redirectUri, ct);
    }

    private static void Respond(HttpListenerContext ctx, string message)
    {
        var html = $"<!doctype html><html><head><meta charset='utf-8'><title>SmartThings</title></head>" +
                   $"<body style='font-family:Segoe UI,Arial,sans-serif;padding:40px;color:#1F2329'>" +
                   $"<h2 style='color:#FF6A00'>LEDVANCE Control</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf);
        ctx.Response.Close();
    }
}
