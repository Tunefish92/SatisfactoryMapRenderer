using System.Text.Json;

namespace SatisfactoryMapRenderer.Serve;

/// <summary>Where the save shown by the served map comes from. Persisted so "refresh" survives a restart.</summary>
public sealed class SaveSource
{
    public const string File = "file", Server = "server", Ssh = "ssh";

    public string Kind { get; set; } = "";

    /// <summary>Name shown in the viewer: the file name, or host and remote path.</summary>
    public string Name { get; set; } = "";
    public DateTime? LoadedAt { get; set; }

    // Dedicated server HTTPS API
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7777;
    public string Token { get; set; } = "";
    public bool VerifyCertificate { get; set; }
    public bool SaveFirst { get; set; } = true;

    /// <summary>Last known version of the server ("Server "x" · game version 1.1 · build 416835") and the verdict.</summary>
    public string ServerInfo { get; set; } = "";
    public string Compatibility { get; set; } = "";

    // SSH
    public string SshTarget { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshPath { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SaveSource Load(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<SaveSource>(System.IO.File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    /// <summary>The file holds the server's API token, so it is readable by the current user only.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>A problem the user can fix (wrong token, unreachable host...); its message is shown in the viewer.</summary>
public sealed class SaveSourceException(string message) : Exception(message);
