using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SatisfactoryMapRenderer.Save;

namespace SatisfactoryMapRenderer.Serve;

/// <summary>
/// Downloads a save from a running dedicated server through its HTTPS API
/// (see CommunityResources/DedicatedServerAPIDocs.md in the game folder).
/// </summary>
/// <summary>What is known about a dedicated server and whether its saves can be rendered.</summary>
/// <param name="Compatibility">"ok", "untested" (newer than this tool), "unsupported" or "unknown".</param>
public sealed record ServerCheck(string Compatibility, string Message, string? ServerName = null, int? Build = null,
    int? SaveVersion = null, string? GameVersion = null, string? Session = null, string? Health = null)
{
    /// <summary>Shown next to the form so the user knows what works before trying.</summary>
    public static string SupportedVersions =>
        $"The HTTPS API exists on dedicated servers from game version 1.0 on; 1.0 up to {UnrealVersions.NewestTestedGame} are supported " +
        $"(save versions {(int)SaveCustomVersion.Version1}–{UnrealVersions.NewestSupportedSave}). " +
        "Older servers (Update 5 to 8) have no HTTPS API: use SSH or upload the save file instead.";
}

public static class DedicatedServerClient
{
    /// <summary>
    /// Asks the server what it is running. The HTTPS API has no version function, so the version comes from
    /// the newest save of the current session (needs the token) and the build from the UDP query API.
    /// </summary>
    public static async Task<ServerCheck> CheckAsync(SaveSource source, CancellationToken cancel)
    {
        if (Uri.CheckHostName(source.Host) == UriHostNameType.Unknown || source.Port is < 1 or > 65535)
            throw new SaveSourceException("Enter the server's host name or IP address and its port.");

        var query = QueryAsync(source.Host, source.Port, cancel);
        using var http = CreateClient(source, TimeSpan.FromSeconds(15));
        string? health;
        try
        {
            using var response = await CallAsync(http, "HealthCheck", new { ClientCustomData = "" }, cancel);
            var body = await ReadJsonAsync(response, cancel);
            health = Property(Property(body, "data") ?? body, "health")?.GetString();
        }
        catch (Exception ex) when (Translate(ex, source, cancel) is { } problem)
        {
            var (build, name) = await query;
            // A server that answers the UDP query but has no HTTPS API at all is older than 1.0.
            bool noApi = ex is HttpRequestException { InnerException: not AuthenticationException } or TaskCanceledException;
            return build is null || !noApi ? throw problem : new ServerCheck("unsupported",
                $"The server runs build {build}, but its HTTPS API does not answer ({problem.Message}) " + ServerCheck.SupportedVersions, name, build);
        }

        var (serverBuild, serverName) = await query;
        var check = new ServerCheck("unknown", "", serverName, serverBuild, Health: health);
        if (source.Token.Length == 0)
            return check with { Message = "The server's HTTPS API answers. Enter an API token to check its game version." };

        try
        {
            using var sessions = await CallAsync(http, "EnumerateSessions", new { }, cancel);
            var newest = Newest(await ReadJsonAsync(sessions, cancel));
            if (newest is not { } save)
                return check with { Compatibility = "ok", Message = "The server's HTTPS API answers and the token is accepted. It has no save yet, so its game version is not known." };

            int version = save.SaveVersion;
            check = check with { SaveVersion = version, Build = check.Build ?? save.Build, GameVersion = UnrealVersions.GameVersionName(version), Session = save.Session };
            return version > UnrealVersions.NewestSupportedSave
                ? check with { Compatibility = "untested", Message = $"The server is newer than this tool (save version {version}, tested up to {UnrealVersions.NewestSupportedSave}, game version {UnrealVersions.NewestTestedGame}). The map may be incomplete." }
                : check with { Compatibility = "ok", Message = $"Compatible: game version {check.GameVersion} (save version {version})." };
        }
        catch (Exception ex) when (Translate(ex, source, cancel) is { } problem)
        {
            throw problem;
        }
    }

    /// <summary>Lightweight Query API: one UDP packet each way. Best effort, UDP is often filtered.</summary>
    private static async Task<(int? Build, string? Name)> QueryAsync(string host, int port, CancellationToken cancel)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, port);
            byte[] request = new byte[13];
            BinaryPrimitives.WriteUInt16LittleEndian(request, 0xF6D5);
            request[2] = 0;     // Poll Server State
            request[3] = 1;     // protocol version
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(4), Environment.TickCount64);
            request[12] = 1;    // terminator
            await udp.SendAsync(request, cancel);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] reply = (await udp.ReceiveAsync(timeout.Token)).Buffer;
            if (reply.Length < 26 || BinaryPrimitives.ReadUInt16LittleEndian(reply) != 0xF6D5 || reply[2] != 1)
                return (null, null);
            int build = (int)BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(13));
            int nameAt = 26 + reply[25] * 3;     // sub states: id (1 byte) + version (2 bytes)
            string? name = null;
            if (nameAt + 2 <= reply.Length)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(nameAt));
                if (nameAt + 2 + length <= reply.Length)
                    name = Encoding.UTF8.GetString(reply, nameAt + 2, length);
            }
            return (build, name);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return (null, null);
        }
    }

    private static HttpClient CreateClient(SaveSource source, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (!source.VerifyCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new UriBuilder("https", source.Host, source.Port, "/api/v1").Uri, Timeout = timeout };
        if (source.Token.Length > 0)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.Token.Trim());
        return http;
    }

    /// <summary>Turns network failures into messages for the user; null for anything unexpected.</summary>
    private static SaveSourceException? Translate(Exception ex, SaveSource source, CancellationToken cancel) => ex switch
    {
        SaveSourceException known => known,
        HttpRequestException { InnerException: AuthenticationException } =>
            new("The server's certificate is not trusted. Dedicated servers use a self-signed certificate by default; turn off \"Verify certificate\" to accept it."),
        HttpRequestException http => new($"Could not reach {source.Host}:{source.Port} ({http.Message})."),
        TaskCanceledException when !cancel.IsCancellationRequested => new($"{source.Host}:{source.Port} did not answer in time."),
        JsonException => new($"{source.Host}:{source.Port} did not answer like a Satisfactory dedicated server."),
        _ => null,
    };

    /// <summary>Save file created on the server when a fresh snapshot is requested; overwritten each time.</summary>
    public const string SnapshotName = "MapRenderer_snapshot";

    /// <summary>
    /// Downloads the session's save: a fresh one if <see cref="SaveSource.SaveFirst"/> is set, otherwise the
    /// newest existing one.
    /// </summary>
    /// <returns>Name of the downloaded save.</returns>
    public static async Task<string> DownloadAsync(SaveSource source, string targetFile, Action<string> log, Action<string, int> progress, CancellationToken cancel)
    {
        if (Uri.CheckHostName(source.Host) == UriHostNameType.Unknown || source.Port is < 1 or > 65535)
            throw new SaveSourceException("Enter the server's host name or IP address and its port.");
        if (source.Token.Length == 0)
            throw new SaveSourceException("An API token is required. Create one with server.GenerateAPIToken in the server console.");

        using var http = CreateClient(source, TimeSpan.FromMinutes(3));

        try
        {
            string saveName;
            if (source.SaveFirst)
            {
                log($"Saving the running session on {source.Host} as {SnapshotName}");
                progress("Saving the game on the server", 8);
                using var saved = await CallAsync(http, "SaveGame", new { SaveName = SnapshotName }, cancel);
                saveName = SnapshotName;
            }
            else
            {
                using var sessions = await CallAsync(http, "EnumerateSessions", new { }, cancel);
                saveName = Newest(await ReadJsonAsync(sessions, cancel))?.Name
                    ?? throw new SaveSourceException("The server's current session has no save files yet.");
            }

            log($"Downloading {saveName} from {source.Host}");
            progress("Downloading the save game", 16);
            using var download = await CallAsync(http, "DownloadSaveGame", new { SaveName = saveName }, cancel);
            if (download.Content.Headers.ContentDisposition is null && download.Content.Headers.ContentType?.MediaType == "application/json")
                throw new SaveSourceException("The server did not return a save file.");
            await using var file = File.Create(targetFile);
            await download.Content.CopyToAsync(file, cancel);
            return saveName;
        }
        catch (Exception ex) when (Translate(ex, source, cancel) is { } problem)
        {
            throw problem;
        }
    }

    /// <summary>
    /// Calls one function of the server's HTTPS API and returns what it answers ("data"); null if it answers
    /// without content.
    /// </summary>
    public static async Task<JsonElement?> FunctionAsync(SaveSource source, string function, object data, TimeSpan timeout, CancellationToken cancel)
    {
        try
        {
            using var http = CreateClient(source, timeout);
            using var response = await CallAsync(http, function, data, cancel);
            if (response.Content.Headers.ContentLength is 0 || (int)response.StatusCode == 204)
                return null;
            var body = await ReadJsonAsync(response, cancel);
            return body.ValueKind == JsonValueKind.Object && Property(body, "data") is { } answer ? answer.Clone() : null;
        }
        catch (Exception ex) when (Translate(ex, source, cancel) is { } friendly)
        {
            throw friendly;
        }
    }

    /// <summary>The body as JSON; it is buffered, so it can be read again after the error check.</summary>
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancel) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync(cancel));

    private static async Task<HttpResponseMessage> CallAsync(HttpClient http, string function, object data, CancellationToken cancel)
    {
        // Sent with a Content-Length; the game's HTTP server is not known to accept chunked request bodies.
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { function, data }));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var response = await http.PostAsync("", content, cancel);
        bool isJson = response.Content.Headers.ContentType?.MediaType == "application/json" && response.Content.Headers.ContentDisposition is null;
        if (isJson && response.Content.Headers.ContentLength is not 0)
        {
            // Errors come as JSON with any status code, even 200.
            JsonElement body = default;
            try { body = await ReadJsonAsync(response, cancel); } catch (JsonException) { }
            if (body.ValueKind == JsonValueKind.Object && Property(body, "errorCode") is { } code)
            {
                using (response)
                    throw new SaveSourceException($"The server refused {function}: {Property(body, "errorMessage")?.GetString() ?? code.GetString()}");
            }
        }
        if (!response.IsSuccessStatusCode)
        {
            using (response)
                throw new SaveSourceException((int)response.StatusCode switch
                {
                    401 => "The server rejected the API token (invalid or revoked).",
                    403 => "The API token is not allowed to do this; it needs to be generated with server.GenerateAPIToken.",
                    404 => $"The server does not know {function}, or the save was not found. The HTTPS API needs game version 1.0 or newer.",
                    _ => $"The server answered {function} with HTTP {(int)response.StatusCode}.",
                });
        }
        return response;
    }

    /// <summary>Newest save of the session that is currently loaded; null if there is none.</summary>
    private static (string Name, int SaveVersion, int? Build, string? Session)? Newest(JsonElement body)
    {
        var data = Property(body, "data") ?? body;
        if (Property(data, "sessions") is not { ValueKind: JsonValueKind.Array } sessions || sessions.GetArrayLength() == 0)
            return null;
        int current = Property(data, "currentSessionIndex") is { ValueKind: JsonValueKind.Number } index ? index.GetInt32() : 0;
        var session = sessions[Math.Clamp(current, 0, sessions.GetArrayLength() - 1)];
        var newest = (Property(session, "saveHeaders") is { ValueKind: JsonValueKind.Array } headers ? headers.EnumerateArray() : [])
            .Where(h => Property(h, "saveName")?.GetString() is { Length: > 0 })
            .OrderByDescending(h => Property(h, "saveDateTime")?.GetString() ?? "", StringComparer.Ordinal)
            .Cast<JsonElement?>()
            .FirstOrDefault();
        if (newest is not { } header)
            return null;
        int? Number(string name) => Property(header, name) is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : null;
        return (Property(header, "saveName")!.Value.GetString()!, Number("saveVersion") ?? 0, Number("buildVersion"), Property(session, "sessionName")?.GetString());
    }

    /// <summary>The API documents PascalCase names but answers in camelCase.</summary>
    /// <summary>A property of an answer, whatever case the server writes its name in.</summary>
    internal static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }
}
