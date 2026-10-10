using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SatisfactoryMapRenderer.Game;

/// <summary>A local Satisfactory installation (Steam or Epic) whose assets can be read.</summary>
public sealed partial record GameInstallation(string Directory)
{
    public string PaksDirectory => Path.Combine(Directory, "FactoryGame", "Content", "Paks");
    public string MappingsFile => Path.Combine(Directory, "CommunityResources", "FactoryGame.usmap");

    /// <summary>Official data dump for tool authors: every item and building with its English name.</summary>
    public string DocsFile => Path.Combine(Directory, "CommunityResources", "Docs", "en-US.json");

    public bool IsValid => System.IO.Directory.Exists(PaksDirectory) && File.Exists(MappingsFile);

    /// <summary>Changes whenever the game is updated; used to key the extracted-data cache.</summary>
    public string VersionKey
    {
        get
        {
            var toc = new FileInfo(Path.Combine(PaksDirectory, "FactoryGame-Windows.utoc"));
            if (!toc.Exists)
                toc = new DirectoryInfo(PaksDirectory).GetFiles("*.utoc").OrderByDescending(f => f.Length).First();
            return $"{toc.Length:x}-{toc.LastWriteTimeUtc.Ticks:x}";
        }
    }

    /// <summary>
    /// The game's own icon (256 px PNG), read from the icon resources of its launcher executable.
    /// Null if there is no such executable or its largest icon is not stored as PNG.
    /// </summary>
    public byte[]? TryReadIcon()
    {
        foreach (string exe in System.IO.Directory.GetFiles(Directory, "FactoryGame*.exe"))
        {
            try
            {
                if (LargestPngIcon(File.ReadAllBytes(exe)) is { } icon)
                    return icon;
            }
            catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // Not a readable executable; try the next one.
            }
        }
        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G'];

    /// <summary>Walks the PE resource tree (type → id → language) for RT_ICON entries.</summary>
    private static byte[]? LargestPngIcon(byte[] pe)
    {
        const int RtIcon = 3;
        int header = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(header)) != 0x00004550) // "PE\0\0"
            return null;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(header + 6));
        int sections = header + 24 + BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(header + 20));

        int resources = -1, resourcesRva = 0;
        for (int i = 0; i < sectionCount; i++)
        {
            var section = pe.AsSpan(sections + i * 40, 40);
            if (!section[..8].SequenceEqual(".rsrc\0\0\0"u8))
                continue;
            resourcesRva = BinaryPrimitives.ReadInt32LittleEndian(section[12..]);
            resources = BinaryPrimitives.ReadInt32LittleEndian(section[20..]);
        }
        if (resources < 0)
            return null;

        // (id, offset, isDirectory) of every entry of the directory at the given offset in the section.
        List<(int Id, int Offset, bool IsDirectory)> Entries(int directory)
        {
            var result = new List<(int, int, bool)>();
            int at = resources + directory;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(at + 12)) + BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(at + 14));
            for (int i = 0; i < count; i++)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 16 + i * 8));
                uint target = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(at + 20 + i * 8));
                result.Add(((int)id, (int)(target & 0x7FFFFFFF), (target & 0x80000000) != 0));
            }
            return result;
        }

        byte[]? best = null;
        foreach (var type in Entries(0).Where(e => e.Id == RtIcon && e.IsDirectory))
            foreach (var icon in Entries(type.Offset).Where(e => e.IsDirectory))
                foreach (var language in Entries(icon.Offset).Where(e => !e.IsDirectory))
                {
                    int entry = resources + language.Offset;
                    int start = resources + BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(entry)) - resourcesRva;
                    int size = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(entry + 4));
                    var data = pe.AsSpan(start, size);
                    if (!data.StartsWith(PngSignature))
                        continue;
                    if (best is null || size > best.Length)
                        best = data.ToArray();
                }
        return best;
    }

    /// <summary>Uses <paramref name="explicitDirectory"/> or looks for Epic and Steam installations.</summary>
    public static GameInstallation? Find(string? explicitDirectory)
    {
        if (explicitDirectory is not null)
        {
            var game = new GameInstallation(Path.GetFullPath(explicitDirectory));
            return game.IsValid ? game : throw new ArgumentException(
                $"'{explicitDirectory}' does not look like a Satisfactory installation (missing FactoryGame/Content/Paks or CommunityResources/FactoryGame.usmap).");
        }
        return Candidates().Select(d => new GameInstallation(d)).FirstOrDefault(g => g.IsValid);
    }

    private static IEnumerable<string> Candidates()
    {
        // Epic Games Launcher manifests.
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (System.IO.Directory.Exists(manifests))
        {
            foreach (var file in System.IO.Directory.GetFiles(manifests, "*.item"))
            {
                string? location = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("DisplayName", out var name) && name.GetString()?.StartsWith("Satisfactory", StringComparison.OrdinalIgnoreCase) == true
                        && doc.RootElement.TryGetProperty("InstallLocation", out var install))
                        location = install.GetString();
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                }
                if (location is not null)
                    yield return location;
            }
        }

        // Steam libraries.
        foreach (string steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam") })
        {
            string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries))
                continue;
            foreach (Match m in SteamLibraryPath().Matches(File.ReadAllText(libraries)))
                yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Satisfactory");
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex SteamLibraryPath();
}
