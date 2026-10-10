using SatisfactoryMapRenderer.Save.Properties;

namespace SatisfactoryMapRenderer.Save;

public abstract class SaveObject
{
    public required string ClassName { get; init; }
    public required string RootObject { get; init; }
    public required string InstanceName { get; init; }
    public uint ObjectFlags { get; init; }

    public int ObjectSaveVersion { get; set; }
    public bool ShouldMigrateObjectRefsToPersistent { get; set; }
    public PropertyList Properties { get; set; } = [];
    public Guid? ObjectGuid { get; set; }

    /// <summary>Class specific binary data after the property list (see <see cref="SpecialData"/>).</summary>
    public object? SpecialData { get; set; }

    /// <summary>Bytes that could not be interpreted.</summary>
    public byte[]? TrailingData { get; set; }
    public string? ParseError { get; set; }

    /// <summary>Last segment of the class path, e.g. "Build_ConstructorMk1_C".</summary>
    public string ShortClassName
    {
        get
        {
            int dot = ClassName.LastIndexOf('.');
            return dot >= 0 ? ClassName[(dot + 1)..] : ClassName;
        }
    }
}

public sealed class SaveEntity : SaveObject
{
    public bool NeedTransform { get; init; }
    public Transform Transform { get; init; }
    public bool WasPlacedInLevel { get; init; }
    public ObjectReference Parent { get; set; }
    public List<ObjectReference> Components { get; } = [];
}

public sealed class SaveComponent : SaveObject
{
    public required string ParentEntityName { get; init; }
}

public sealed class SaveLevel
{
    public required string Name { get; init; }
    public bool IsPersistent { get; init; }
    public int? LevelSaveVersion { get; set; }
    public List<SaveObject> Objects { get; } = [];
    public List<ObjectReference> Collectables { get; } = [];
    public Dictionary<string, List<ObjectReference>> DestroyedActors { get; } = [];
}

public sealed record SaveGrid(string Name, int CellSize, uint Hash, IReadOnlyDictionary<string, uint> Cells);

public sealed class SaveGame
{
    public required string FilePath { get; init; }
    public required SaveHeader Header { get; init; }
    public long UncompressedBodySize { get; set; }
    public int ChunkCount { get; set; }
    public List<SaveGrid> Grids { get; } = [];
    public List<SaveLevel> Levels { get; } = [];
    public List<ObjectReference> UnresolvedWorldSaveData { get; } = [];
    public List<string> Warnings { get; } = [];
    public int PropertiesDecoded { get; set; }
    public int PropertiesFailed { get; set; }
    public Dictionary<string, int> PropertyUnderflows { get; } = [];

    public IEnumerable<SaveObject> AllObjects => Levels.SelectMany(l => l.Objects);
}
