using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace Alfred.Core;

/// <summary>
/// Receives one OAuth redirect on http://127.0.0.1:&lt;port&gt;&lt;path&gt;, then
/// stops. Bound to the loopback addresses only (spec §11), and alive only
/// for the length of a sign-in. A raw socket rather than HttpListener, which
/// needs an admin URL reservation on Windows.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    private readonly TcpListener _v4;
    private readonly TcpListener? _v6;
    private readonly string _service;
    private readonly string _path;

    public int Port { get; }

    /// <param name="port">0 lets the system choose one.</param>
    public LoopbackRedirect(string service, string path = "/", int port = 0)
    {
        _service = service;
        _path = path;
        _v4 = new TcpListener(IPAddress.Loopback, port);
        try
        {
            _v4.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new ToolException($"Another app is using the port {service} sign-in needs; quit it and try again");
        }
        Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
        // The same port on IPv6, for browsers that resolve "localhost" to ::1
        // first (Microsoft's redirect is http://localhost). Best effort.
        try
        {
            _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            _v6.Start();
        }
        catch (SocketException)
        {
            _v6 = null;
        }
    }

    /// <summary>Waits for the browser to arrive at the callback path. Returns its query.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CallbackAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(limit.Token).AsTask() };
                if (_v6 is not null) accepts.Add(_v6.AcceptTcpClientAsync(limit.Token).AsTask());
                using var client = await await Task.WhenAny(accepts);
                if (await Handle(client, limit.Token) is { } query) return query;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException($"{_service} sign-in timed out");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> Handle(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[16_384];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        // Request line: "GET /callback?code=…&state=… HTTP/1.1"
        var requestLine = Encoding.UTF8.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ');
        var target = requestLine.Length >= 2 ? requestLine[1] : "";
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var isCallback = path == _path || (_path == "/" && path.Length == 0);
        var body = isCallback
            ? $"<html><body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>{WebUtility.HtmlEncode(_service)} is connected.</h2><p>You can close this tab.</p></body></html>"
            : "Not found";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {(isCallback ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        if (!isCallback) return null;
        var query = HttpUtility.ParseQueryString(question < 0 ? "" : target[(question + 1)..]);
        return query.AllKeys.OfType<string>().ToDictionary(k => k, k => query[k] ?? "");
    }

    public void Dispose()
    {
        _v4.Stop();
        _v6?.Stop();
    }
}

public static class Pkce
{
    public static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// OAuth 2 sign-in for a desktop app: Authorization Code with PKCE, the
/// browser redirecting to a loopback address on this PC. Used for Spotify,
/// Outlook and Google. Tokens live in the secret store (Credential Manager).
/// </summary>
public sealed class OAuthSession(OAuthSession.Configuration configuration, Func<(string Id, string? Secret)?> credentials, ISecretStore secrets, ILauncher launcher)
{
    public sealed record Configuration(
        string Service,
        string AuthorizeUrl,
        string TokenUrl,
        string Scopes,
        /// <summary>The secret store key holding the tokens.</summary>
        string TokenKey,
        /// <summary>Registered redirect host: Google and Spotify take 127.0.0.1, Microsoft localhost.</summary>
        string RedirectHost,
        IReadOnlyDictionary<string, string> ExtraParameters,
        /// <summary>Microsoft wants the scopes again when refreshing; the others don't.</summary>
        bool RefreshSendsScope,
        /// <summary>A fixed port and path, for providers that need the redirect registered exactly (Spotify).</summary>
        int FixedPort = 0,
        string RedirectPath = "/");

    private sealed record Token(string AccessToken, string RefreshToken, DateTime ExpiresAt);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Service => configuration.Service;

    public bool IsSignedIn => credentials() is not null && secrets.Get(configuration.TokenKey) is not null;

    public string RedirectUri(int port) => $"http://{configuration.RedirectHost}:{port}{(configuration.RedirectPath == "/" ? "" : configuration.RedirectPath)}";

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        var name = configuration.Service;
        if (credentials() is not { } creds) throw new ToolException($"Enter the {name} client ID first");
        var (clientId, secret) = creds;
        var verifier = Pkce.RandomUrlSafe(64);
        var state = Pkce.RandomUrlSafe(16);
        using var loopback = new LoopbackRedirect(name, configuration.RedirectPath, configuration.FixedPort);
        var redirectUri = RedirectUri(loopback.Port);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Pkce.Challenge(verifier),
            ["state"] = state,
            ["scope"] = configuration.Scopes,
        };
        foreach (var (key, value) in configuration.ExtraParameters) query[key] = value;
        var authorize = configuration.AuthorizeUrl + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        var redirected = loopback.CallbackAsync(TimeSpan.FromMinutes(5), cancellationToken);
        await launcher.OpenUrlAsync(authorize, null, cancellationToken);
        var reply = await redirected;
        if (reply.GetValueOrDefault("state") != state) throw new ToolException($"{name} sign-in was interrupted; try again");
        if (reply.GetValueOrDefault("error") is { Length: > 0 } error)
            throw new ToolException(error == "access_denied" ? $"{name} sign-in was cancelled" : $"{name} refused sign-in ({error})");
        var code = reply.GetValueOrDefault("code") ?? throw new ToolException($"{name} didn't return a sign-in code");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };
        if (secret is not null) form["client_secret"] = secret;
        await RequestToken(form, null, cancellationToken);
    }

    public void SignOut() => secrets.Delete(configuration.TokenKey);

    /// <summary>A request with a fresh access token, retried once after a refresh. Returns the status and body.</summary>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, string? jsonBody = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessToken(forceRefresh: attempt > 0, cancellationToken);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new("Bearer", token);
            foreach (var (key, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(key, value);
            if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await Http.Guard(configuration.Service, () => Http.Client.SendAsync(request, cancellationToken));
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        throw new ToolException($"{configuration.Service} sign-in has expired; sign in again in Settings");
    }

    /// <summary>A GET that must succeed.</summary>
    public async Task<string> GetAsync(string url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url, null, headers, cancellationToken);
        return status == HttpStatusCode.OK ? body : throw new ToolException($"{configuration.Service} answered with an error ({(int)status})");
    }

    private async Task<string> AccessToken(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (credentials() is not { } creds || secrets.Get(configuration.TokenKey) is not { } stored
                || Deserialize(stored) is not { } token)
                throw new ToolException($"{configuration.Service} isn't connected; sign in under Settings");
            var (clientId, secret) = creds;
            if (!forceRefresh && token.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(1)) return token.AccessToken;
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
                ["client_id"] = clientId,
            };
            if (secret is not null) form["client_secret"] = secret;
            if (configuration.RefreshSendsScope) form["scope"] = configuration.Scopes;
            return await RequestToken(form, token.RefreshToken, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RequestToken(Dictionary<string, string> form, string? previousRefreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.Guard(configuration.Service, () => Http.Client.PostAsync(configuration.TokenUrl, content, cancellationToken));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TokenResponse? reply = null;
        if (response.IsSuccessStatusCode)
        {
            try { reply = JsonSerializer.Deserialize<TokenResponse>(body); }
            catch (JsonException) { }
        }
        if (reply?.AccessToken is null)
        {
            if (previousRefreshToken is not null) SignOut();
            string? reason = null;
            try { reason = JsonSerializer.Deserialize<ErrorResponse>(body)?.Error; }
            catch (JsonException) { }
            throw new ToolException($"{configuration.Service} sign-in failed{(reason is null ? "" : $" ({reason})")}; check the client ID and sign in again in Settings");
        }
        var token = new Token(reply.AccessToken, reply.RefreshToken ?? previousRefreshToken ?? "", DateTime.UtcNow.AddSeconds(reply.ExpiresIn));
        secrets.Set(configuration.TokenKey, JsonSerializer.Serialize(token));
        return token.AccessToken;
    }

    private static Token? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Token>(json); }
        catch (JsonException) { return null; }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);
}
