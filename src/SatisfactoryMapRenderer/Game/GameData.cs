using System.Text.Json;
using System.Text.Json.Serialization;

namespace SatisfactoryMapRenderer.Game;

/// <summary>An actor placed in the game's level (resource node, power slug, spawner...).</summary>
/// <param name="Class">Short class name, e.g. "BP_ResourceNode_C".</param>
/// <param name="Name">Object name as used in save paths ("Persistent_Level:PersistentLevel.{Name}").</param>
/// <param name="Label">Editor label; equals the object name older (pre world partition) saves used.</param>
/// <param name="Props">Selected properties: resource, purity, creature, amount...</param>
public sealed record LevelActor(string Class, string Name, string Label, double X, double Y, double Z, Dictionary<string, string>? Props)
{
    public string? Prop(string key) => Props?.GetValueOrDefault(key);

    /// <summary>How the actor is turned (quaternion x y z w); null when it is not.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Rotation { get; init; }

    /// <summary>How the actor is scaled (x y z); null when it is not.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Scale { get; init; }
}

/// <summary>Everything the renderer takes from the installed game. Cached as JSON per game version.</summary>
public sealed class GameData
{
    public const int FormatVersion = 5;

    public int Version { get; set; } = FormatVersion;
    public string GameVersionKey { get; set; } = "";
    public string Engine { get; set; } = "";

    /// <summary>File name (in the cache directory) of the stitched in-game map.</summary>
    public string? MapImage { get; set; }

    public List<LevelActor> Actors { get; set; } = [];

    /// <summary>Creature class ("Char_Hog_C") → icon key.</summary>
    public Dictionary<string, string> CreatureIcons { get; set; } = [];

    /// <summary>Class name ("Desc_IronPlateReinforced_C", "Build_ConstructorMk1_C") → English display name.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static GameData? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var data = JsonSerializer.Deserialize<GameData>(File.ReadAllText(path), Json);
            return data?.Version == FormatVersion ? data : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
