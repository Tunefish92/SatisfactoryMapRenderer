using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SatisfactoryMapRenderer.Save;

namespace SatisfactoryMapRenderer.Serve;

/// <summary>
/// Serves a rendered map on localhost and lets the viewer replace the save: by upload, from a dedicated
/// server's HTTPS API, or over SSH. Every new save is rendered into the same output directory.
/// </summary>
/// <param name="render">
/// Renders the save at the given path into <paramref name="outputDirectory"/> and reports what it is doing
/// (stage, percent of the rendering done).
/// </param>
/// <param name="bindAddress">"localhost" (default), "*" for every interface, or one address of this machine.</param>
/// <param name="adminPassword">
/// Needed to see and use the save source panel. Without one everybody who reaches the page is an admin,
/// which is only allowed while the server listens on localhost.
/// </param>
public sealed class MapServer(string outputDirectory, string stateDirectory, int port, Action<string, Action<string, int>> render, Action<string> log,
    string bindAddress = MapServer.Localhost, string? adminPassword = null, string? baseMapsDirectory = null)
{
    public const string Localhost = "localhost";
    private const string SessionCookie = "smr_session";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12), LoginLockout = TimeSpan.FromMinutes(1);

    public static bool IsLoopback(string bindAddress) =>
        bindAddress is Localhost || (IPAddress.TryParse(bindAddress, out var address) && IPAddress.IsLoopback(address));

    /// <summary>Share of the progress bar that belongs to fetching the save; the rest is rendering.</summary>
    private const int FetchShare = 25;

    private sealed record Progress(string Stage, int Percent);
    private volatile Progress _progress = new("", 0);

    private void Report(string stage, int percent) => _progress = new Progress(stage, Math.Clamp(percent, 0, 100));

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly object _loginLock = new(), _settingsLock = new();
    private int _failedLogins;
    private DateTime _loginsBlockedUntil;

    private const long MaxUploadBytes = 2L << 30;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".svg"] = "image/svg+xml",
        [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".txt"] = "text/plain; charset=utf-8",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
    private readonly string? _baseMaps = baseMapsDirectory is null ? null : Path.Combine(Path.GetFullPath(baseMapsDirectory), "tiles") + Path.DirectorySeparatorChar;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private SaveSource _source = SaveSource.Load(Path.Combine(stateDirectory, "source.json"));

    private string SourceFile => Path.Combine(stateDirectory, "source.json");
    private string SavesDirectory => Path.Combine(stateDirectory, "saves");

    /// <summary>Remembers a save given on the command line as the current source.</summary>
    public void SetFileSource(string savePath)
    {
        _source = Keep(new SaveSource { Kind = SaveSource.File, Name = Path.GetFileName(savePath), LoadedAt = DateTime.UtcNow });
        _source.Save(SourceFile);
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        using var listener = new HttpListener();
        if (bindAddress == Localhost)
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
        }
        else
            listener.Prefixes.Add($"http://{(bindAddress is "*" or "0.0.0.0" or "::" ? "*" : bindAddress)}:{port}/");
        listener.Start();
        log($"Serving {outputDirectory} at http://{(bindAddress is Localhost or "*" or "0.0.0.0" or "::" ? "localhost" : bindAddress)}:{port}/ (Ctrl+C to stop)");
        log(adminPassword is null
            ? "  Reachable from this machine only; everyone who opens the page can change the save game."
            : $"  {(IsLoopback(bindAddress) ? "Reachable from this machine only" : "Reachable from other machines")}; changing the save game needs the admin password.");
        using var stop = cancel.Register(listener.Stop);
        while (!cancel.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context, cancel), cancel);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancel)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            string path = request.Url!.AbsolutePath;
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
                ServeFile(request, response, path);
            else if (path == "/api/health" && request.HttpMethod == "GET")
                await WriteJsonAsync(response, 200, new { Status = "ok" });
            else if (path == "/api/source" && request.HttpMethod == "GET")
                // Visitors only learn how the save got here and when (shown in the status bar): no host, no path.
                await WriteJsonAsync(response, 200, IsAdmin(request) ? Describe() : new { Admin = false, LoginRequired = true, _source.Kind, _source.LoadedAt });
            else if (path == "/api/progress" && request.HttpMethod == "GET")
            {
                // How far the save that is being loaded has come; polled by the admin's page for its progress bar.
                var progress = _progress;
                await WriteJsonAsync(response, 200, IsAdmin(request)
                    ? new { Busy = _busy.CurrentCount == 0, progress.Stage, progress.Percent }
                    : new { Busy = false, Stage = "", Percent = 0 });
            }
            else if (request.HttpMethod != "POST")
                await WriteJsonAsync(response, 405, new { error = "Method not allowed." });
            else if (!IsSameOrigin(request))
                await WriteJsonAsync(response, 403, new { error = "Requests from other sites are not accepted." });
            else if (path == "/api/login")
                await LoginAsync(request, response, cancel);
            else if (path == "/api/logout")
            {
                if (request.Cookies[SessionCookie]?.Value is { } session)
                    _sessions.TryRemove(session, out _);
                response.Headers["Set-Cookie"] = $"{SessionCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict";
                await WriteJsonAsync(response, 200, new { Admin = false });
            }
            else if (!IsAdmin(request))
                await WriteJsonAsync(response, 401, new { error = "Please log in as admin." });
            else if (path == "/api/server/check")
                await CheckServerAsync(request, response, cancel);
            else if (path is "/api/server/settings" or "/api/ssh/settings")
                await SaveSettingsAsync(request, response, path, cancel);
            else
                await ChangeSourceAsync(request, response, path, cancel);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // The browser went away; nothing to answer.
            log($"Request dropped: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Request failed: {ex}");
            try { await WriteJsonAsync(response, 500, new { error = ex.Message }); } catch (Exception) { /* response already started */ }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* connection already gone */ }
        }
    }

    /// <summary>
    /// Other web pages must not be able to drive this server from a visitor's browser: the custom header
    /// cannot be sent cross-origin without a preflight (which is never granted), and the Origin has to be
    /// the address the page itself was loaded from. On localhost the Host is checked against DNS rebinding.
    /// </summary>
    private bool IsSameOrigin(HttpListenerRequest request)
    {
        string? host = request.Headers["Host"];
        if (host is null || request.Headers["X-Map-Renderer"] != "1")
            return false;
        if (bindAddress == Localhost && !new[] { $"127.0.0.1:{port}", $"localhost:{port}" }.Contains(host, StringComparer.OrdinalIgnoreCase))
            return false;
        string? origin = request.Headers["Origin"];
        return origin is null || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ admin login

    private bool IsAdmin(HttpListenerRequest request)
    {
        if (adminPassword is null)
            return true;
        if (request.Cookies[SessionCookie]?.Value is not { Length: > 0 } session || !_sessions.TryGetValue(session, out var expires))
            return false;
        if (expires > DateTime.UtcNow)
            return true;
        _sessions.TryRemove(session, out _);
        return false;
    }

    private sealed record LoginRequest(string? Password);

    private async Task LoginAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        if (adminPassword is null)
        {
            await WriteJsonAsync(response, 200, Describe());
            return;
        }
        bool blocked;
        lock (_loginLock)
            blocked = DateTime.UtcNow < _loginsBlockedUntil;
        if (blocked)
        {
            await WriteJsonAsync(response, 429, new { error = "Too many wrong passwords. Try again in a minute." });
            return;
        }

        LoginRequest? body = null;
        try { body = await JsonSerializer.DeserializeAsync<LoginRequest>(request.InputStream, Json, cancel); } catch (JsonException) { }
        // Compared as hashes: equal length, constant time.
        bool correct = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(body?.Password ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword)));
        if (!correct)
        {
            lock (_loginLock)
                if (++_failedLogins >= MaxFailedLogins)
                {
                    _failedLogins = 0;
                    _loginsBlockedUntil = DateTime.UtcNow + LoginLockout;
                    log($"Admin login blocked for {LoginLockout.TotalSeconds:0} s after {MaxFailedLogins} wrong passwords (last from {request.RemoteEndPoint?.Address}).");
                }
            await Task.Delay(500, cancel);
            await WriteJsonAsync(response, 401, new { error = "Wrong password." });
            return;
        }

        lock (_loginLock)
            _failedLogins = 0;
        foreach (var (key, expires) in _sessions)
            if (expires <= DateTime.UtcNow)
                _sessions.TryRemove(key, out _);
        string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[session] = DateTime.UtcNow + SessionLifetime;
        // Secure when a TLS-terminating proxy is in front; the listener itself only speaks HTTP.
        bool https = string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        response.Headers["Set-Cookie"] = $"{SessionCookie}={session}; Path=/; Max-Age={(int)SessionLifetime.TotalSeconds}; HttpOnly; SameSite=Strict{(https ? "; Secure" : "")}";
        log($"Admin logged in from {request.RemoteEndPoint?.Address}.");
        await WriteJsonAsync(response, 200, Describe());
    }

    // ------------------------------------------------------------------ static files

    private void ServeFile(HttpListenerRequest request, HttpListenerResponse response, string urlPath)
    {
        string relative = Uri.UnescapeDataString(urlPath).TrimStart('/');
        string root = _root;
        // Base maps that came cut into tiles are not part of a map's folder; every map uses the same ones.
        bool shared = _baseMaps is not null && relative.StartsWith(Rendering.BaseMapTiles.UrlPrefix, StringComparison.Ordinal);
        if (shared)
            (root, relative) = (_baseMaps!, relative[Rendering.BaseMapTiles.UrlPrefix.Length..]);
        string file = Path.GetFullPath(Path.Combine(root, relative.Length == 0 ? "index.html" : relative));
        if (!file.StartsWith(root, StringComparison.Ordinal) || !File.Exists(file))
        {
            response.StatusCode = 404;
            return;
        }
        if (shared)
        {
            // they only change with the app itself
            response.Headers["Cache-Control"] = "public, max-age=86400";
            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            using var tile = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            response.ContentLength64 = tile.Length;
            if (request.HttpMethod != "HEAD")
                tile.CopyTo(response.OutputStream);
            return;
        }

        // A new render replaces files under the same names, so the browser has to ask every time.
        var modified = File.GetLastWriteTimeUtc(file);
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Last-Modified"] = modified.ToString("R");
        if (DateTime.TryParse(request.Headers["If-Modified-Since"], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var since)
            && modified <= since.AddSeconds(1))
        {
            response.StatusCode = 304;
            return;
        }
        response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        response.ContentLength64 = stream.Length;
        if (request.HttpMethod != "HEAD")
            stream.CopyTo(response.OutputStream);
    }

    // ------------------------------------------------------------------ API

    private object Describe() => new
    {
        Admin = true, LoginRequired = adminPassword is not null,
        _source.Kind, _source.Name, _source.LoadedAt,
        _source.Host, _source.Port, HasToken = _source.Token.Length > 0, _source.VerifyCertificate, _source.SaveFirst,
        _source.SshTarget, _source.SshPort, _source.SshPath,
        _source.ServerInfo, _source.Compatibility, ServerCheck.SupportedVersions,
        Busy = _busy.CurrentCount == 0,
    };

    private sealed record ServerRequest(string? Host, int? Port, string? Token, bool? VerifyCertificate, bool? SaveFirst);
    private sealed record SshRequest(string? Target, int? Port, string? Path);

    private async Task ChangeSourceAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken cancel)
    {
        if (!await _busy.WaitAsync(0, cancel))
        {
            await WriteJsonAsync(response, 409, new { error = "Another save is being loaded right now." });
            return;
        }
        try
        {
            Directory.CreateDirectory(SavesDirectory);
            Report("Starting", 0);
            SaveSource source;
            string saveFile;
            switch (path)
            {
                case "/api/upload":
                {
                    if (request.ContentLength64 > MaxUploadBytes)
                        throw new SaveSourceException("The file is too large to be a save game.");
                    string name = Path.GetFileName(request.QueryString["name"] ?? "");
                    if (!name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                        throw new SaveSourceException("Please choose a Satisfactory save file (.sav).");
                    saveFile = Path.Combine(SavesDirectory, name);
                    Report("Receiving the file", 10);
                    await using (var file = File.Create(saveFile))
                        await request.InputStream.CopyToAsync(file, cancel);
                    source = Keep(new SaveSource { Kind = SaveSource.File, Name = name });
                    break;
                }
                case "/api/server":
                {
                    var body = await JsonSerializer.DeserializeAsync<ServerRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing server settings.");
                    source = FromRequest(body);
                    Remember(source);
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                }
                case "/api/ssh":
                {
                    var body = await JsonSerializer.DeserializeAsync<SshRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing SSH settings.");
                    source = FromRequest(body);
                    Remember(source);
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                }
                case "/api/refresh":
                    if (_source.Kind is not (SaveSource.Server or SaveSource.Ssh))
                        throw new SaveSourceException("Only a save from a server can be refreshed.");
                    source = Keep(new SaveSource { Kind = _source.Kind });
                    (saveFile, source.Name) = await FetchAsync(source, cancel);
                    break;
                default:
                    await WriteJsonAsync(response, 404, new { error = "Unknown function." });
                    return;
            }

            await Task.Run(() => render(saveFile, (stage, percent) => Report(stage, FetchShare + percent * (100 - FetchShare) / 100)), cancel);
            Report("Done", 100);
            source.LoadedAt = DateTime.UtcNow;
            lock (_settingsLock)
            {
                // Settings entered while this save was loading win; the server's version was just determined.
                var version = (source.ServerInfo, source.Compatibility);
                _source = Keep(source);
                if (source.Kind == SaveSource.Server)
                    (_source.ServerInfo, _source.Compatibility) = version;
                _source.Save(SourceFile);
            }
            await WriteJsonAsync(response, 200, Describe());
        }
        catch (Exception ex) when (ex is SaveSourceException or SaveFormatException or JsonException)
        {
            string message = ex is SaveFormatException ? $"This is not a readable save game: {ex.Message}" : ex is JsonException ? "Invalid request." : ex.Message;
            log($"Could not load the save: {message}");
            await WriteJsonAsync(response, 400, new { error = message });
        }
        finally
        {
            _busy.Release();
        }
    }

    private SaveSource FromRequest(ServerRequest body)
    {
        var source = Keep(new SaveSource { Kind = SaveSource.Server });
        string host = (body.Host ?? "").Trim();
        // An empty token field means "keep the stored one", but never for a different server.
        source.Token = !string.IsNullOrWhiteSpace(body.Token) ? body.Token.Trim()
            : string.Equals(host, _source.Host, StringComparison.OrdinalIgnoreCase) && (body.Port ?? 7777) == _source.Port ? _source.Token : "";
        source.Host = host;
        source.Port = body.Port ?? 7777;
        source.VerifyCertificate = body.VerifyCertificate ?? false;
        source.SaveFirst = body.SaveFirst ?? true;
        return source;
    }

    private SaveSource FromRequest(SshRequest body)
    {
        var source = Keep(new SaveSource { Kind = SaveSource.Ssh });
        source.SshTarget = (body.Target ?? "").Trim();
        source.SshPort = body.Port ?? 22;
        source.SshPath = (body.Path ?? "").Trim();
        return source;
    }

    /// <summary>
    /// Connection settings are stored as soon as they are entered (saved, checked or used), whether or not
    /// the connection works, so they never have to be typed twice. The save that is shown stays the same.
    /// </summary>
    private void Remember(SaveSource entered)
    {
        lock (_settingsLock)
        {
            if (entered.Kind == SaveSource.Server)
                (_source.Host, _source.Port, _source.Token, _source.VerifyCertificate, _source.SaveFirst) =
                    (entered.Host, entered.Port, entered.Token, entered.VerifyCertificate, entered.SaveFirst);
            else
                (_source.SshTarget, _source.SshPort, _source.SshPath) = (entered.SshTarget, entered.SshPort, entered.SshPath);
            _source.Save(SourceFile);
        }
    }

    private async Task SaveSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken cancel)
    {
        try
        {
            Remember(path == "/api/server/settings"
                ? FromRequest(await JsonSerializer.DeserializeAsync<ServerRequest>(request.InputStream, Json, cancel) ?? throw new JsonException())
                : FromRequest(await JsonSerializer.DeserializeAsync<SshRequest>(request.InputStream, Json, cancel) ?? throw new JsonException()));
            await WriteJsonAsync(response, 200, Describe());
        }
        catch (JsonException)
        {
            await WriteJsonAsync(response, 400, new { error = "Invalid request." });
        }
    }

    /// <summary>Reports the server's version and compatibility without loading anything.</summary>
    private async Task CheckServerAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancel)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<ServerRequest>(request.InputStream, Json, cancel) ?? throw new SaveSourceException("Missing server settings.");
            var entered = FromRequest(body);
            Remember(entered);
            await WriteJsonAsync(response, 200, await DedicatedServerClient.CheckAsync(entered, cancel));
        }
        catch (Exception ex) when (ex is SaveSourceException or JsonException)
        {
            await WriteJsonAsync(response, 400, new { error = ex is JsonException ? "Invalid request." : ex.Message });
        }
    }

    private static string Summarize(ServerCheck check) => string.Join(" · ", new[]
    {
        check.ServerName is { Length: > 0 } name ? $"Server \"{name}\"" : null,
        check.GameVersion is { } version ? $"game version {version}" : null,
        check.SaveVersion is { } save ? $"save version {save}" : null,
        check.Build is { } build ? $"build {build}" : null,
    }.OfType<string>());

    /// <summary>A new source keeps the connection settings of the other kinds, so their forms stay filled in.</summary>
    private SaveSource Keep(SaveSource next)
    {
        (next.Host, next.Port, next.Token, next.VerifyCertificate, next.SaveFirst) = (_source.Host, _source.Port, _source.Token, _source.VerifyCertificate, _source.SaveFirst);
        (next.SshTarget, next.SshPort, next.SshPath) = (_source.SshTarget, _source.SshPort, _source.SshPath);
        (next.ServerInfo, next.Compatibility) = (_source.ServerInfo, _source.Compatibility);
        return next;
    }

    private async Task<(string File, string Name)> FetchAsync(SaveSource source, CancellationToken cancel)
    {
        if (source.Kind == SaveSource.Server)
        {
            // Know what the server runs before touching it: an unsupported one is not asked to save.
            Report("Checking the server", 3);
            var check = await DedicatedServerClient.CheckAsync(source, cancel);
            (source.ServerInfo, source.Compatibility) = (Summarize(check), check.Compatibility);
            log($"{source.Host}: {source.ServerInfo} - {check.Message}");
            if (check.Compatibility == "unsupported")
                throw new SaveSourceException(check.Message);
            string file = Path.Combine(SavesDirectory, "server-snapshot.sav");
            string saveName = await DedicatedServerClient.DownloadAsync(source, file, log, Report, cancel);
            return (file, $"{saveName} on {source.Host}");
        }
        else
        {
            string file = Path.Combine(SavesDirectory, "ssh-snapshot.sav");
            await SshDownloader.DownloadAsync(source, file, log, Report, cancel);
            return (file, $"{source.SshTarget}:{source.SshPath}");
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
