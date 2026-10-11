using SatisfactoryMapRenderer.Save;

namespace SatisfactoryMapRenderer.World;

public enum LayerKind
{
    /// <summary>Rendered into image tiles (dense geometry).</summary>
    Raster,
    /// <summary>Exported as interactive markers with popups.</summary>
    Marker,
}

public sealed record LayerDef(string Id, string Title, string Group, LayerKind Kind, bool VisibleByDefault, string Color, string Description);

public static class Layers
{
    public static readonly LayerDef Background = new("background", "Game map", "Base", LayerKind.Raster, true, "#34495e", "The in-game map from your installation, or the --background image");
    public static readonly LayerDef Ground = new("ground", "Ground map", "Base", LayerKind.Raster, false, "#6f8f5a", "The world from above without any factory, made from the game's ground textures, water and heights");
    public static readonly LayerDef Relief = new("relief", "Relief map", "Base", LayerKind.Raster, false, "#7a8f6a", "The in-game map with the terrain shaded from the game's height data");
    public static readonly LayerDef Realistic = new("realistic", "Realistic map", "Base", LayerKind.Raster, false, "#3f6b4f", "The --realistic image, e.g. a satellite-style render of the world");
    public static readonly LayerDef Explored = new("explored", "Explored area", "Base", LayerKind.Raster, true, "#3d5a45", "Fog of war from the in-game map");
    public static readonly LayerDef FogOfWar = new("fog", "Fog of war", "Base", LayerKind.Raster, false, "#10151b", "Darkens the areas not explored yet");
    public static readonly LayerDef Foundations = new("foundations", "Foundations & walls", "Factory", LayerKind.Raster, true, "#8b939c", "Foundations, ramps, walls, walkways");
    public static readonly LayerDef Buildings = new("buildings", "Buildings", "Factory", LayerKind.Raster, true, "#5b9bd5", "Machines, generators, storage – click for details");
    public static readonly LayerDef Pipes = new("pipes", "Pipes & hypertubes", "Factory", LayerKind.Raster, true, "#3aa3e3", "Pipelines, pumps, hypertubes");
    public static readonly LayerDef Belts = new("belts", "Conveyor belts", "Factory", LayerKind.Raster, true, "#f0a030", "Belts, lifts, splitters' connections");
    public static readonly LayerDef Railways = new("railways", "Railways", "Factory", LayerKind.Raster, true, "#d7dbdd", "Train tracks");
    public static readonly LayerDef Power = new("power", "Power grid", "Factory", LayerKind.Raster, false, "#f7dc6f", "Power lines, poles, switches");
    public static readonly LayerDef Foliage = new("foliage", "Cut trees & foliage", "World", LayerKind.Raster, false, "#6ab04c", "Vegetation removed by chainsaw or building");

    public static readonly LayerDef Players = new("players", "Players", "Actors", LayerKind.Marker, true, "#ff4d4d", "Pioneers");
    public static readonly LayerDef Vehicles = new("vehicles", "Vehicles & trains", "Actors", LayerKind.Marker, true, "#c56cf0", "Trucks, tractors, explorers, trains, drones");
    public static readonly LayerDef Creatures = new("creatures", "Creatures", "Actors", LayerKind.Marker, true, "#ff9f43", "Animals and enemies alive at save time");
    public static readonly LayerDef Spawners = new("spawners", "Creature spawners", "Actors", LayerKind.Marker, false, "#ee5a24", "Creature spawn locations");
    public static readonly LayerDef Landmarks = new("landmarks", "Landmarks", "Factory", LayerKind.Marker, true, "#e74c3c", "HUB, Space Elevator, M.A.M., AWESOME Sink");
    public static readonly LayerDef Stations = new("stations", "Stations", "Factory", LayerKind.Marker, true, "#a29bfe", "Train, truck and drone stations");
    public static readonly LayerDef Beacons = new("beacons", "Beacons & map markers", "Factory", LayerKind.Marker, true, "#00d2d3", "Player placed markers");
    public static readonly LayerDef Resources = new("resources", "Resource nodes", "Resources", LayerKind.Marker, true, "#b4b9d6", "Ore nodes, oil, wells, geysers");
    public static readonly LayerDef Deposits = new("deposits", "Resource deposits", "Resources", LayerKind.Marker, false, "#95a5a6", "Hand-mineable rock piles");
    public static readonly LayerDef Slugs = new("slugs", "Power slugs", "Collectibles", LayerKind.Marker, false, "#48dbfb", "Uncollected power slugs");
    public static readonly LayerDef Artifacts = new("artifacts", "Somersloops & Mercer spheres", "Collectibles", LayerKind.Marker, false, "#ff6b81", "Uncollected alien artifacts");
    public static readonly LayerDef DropPods = new("droppods", "Crash sites", "Collectibles", LayerKind.Marker, false, "#feca57", "Drop pods with hard drives");
    public static readonly LayerDef Plants = new("plants", "Berries, nuts & mushrooms", "Collectibles", LayerKind.Marker, false, "#1dd1a1", "Edible plants");
    public static readonly LayerDef Pickups = new("pickups", "Items on the ground", "Collectibles", LayerKind.Marker, false, "#f8a5c2", "Dropped items, crates, creature remains");
    public static readonly LayerDef Hazards = new("hazards", "Hazards & obstacles", "World", LayerKind.Marker, false, "#b33939", "Spore flowers, destructible rocks");

    public static readonly IReadOnlyList<LayerDef> All =
    [
        Ground, Background, Relief, Realistic, Explored, FogOfWar,
        Foundations, Buildings, Pipes, Belts, Railways, Power, Landmarks, Stations, Beacons,
        Players, Vehicles, Creatures, Spawners,
        Resources, Deposits,
        Slugs, Artifacts, DropPods, Plants, Pickups,
        Foliage, Hazards,
    ];
}

public enum Shape { Point, Box, Line }

/// <summary>Something to draw on the map, in world coordinates (Unreal units = cm).</summary>
public sealed class MapFeature
{
    public required LayerDef Layer { get; init; }
    public required Shape Shape { get; init; }
    public required string Name { get; init; }
    public string Class { get; init; } = "";
    public Vector3 Position { get; init; }
    public double Yaw { get; init; }

    /// <summary>Full rotation and scale of a building, for placing its 3D model.</summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = new(1, 1, 1);

    /// <summary>Conveyor lifts: where the other end is, seen from the building, and how it is turned.</summary>
    public (Vector3 Offset, Quaternion Rotation)? Top { get; set; }
    public double SizeX { get; init; }
    public double SizeY { get; init; }

    /// <summary>World XY pairs for <see cref="Shape.Line"/>.</summary>
    public double[]? Path { get; init; }

    /// <summary>World Z of every point of <see cref="Path"/>, for the 3D view.</summary>
    public double[]? PathZ { get; init; }

    /// <summary>Line width (lines) or diameter (points) in world units; rendering enforces a minimum pixel size.</summary>
    public double Thickness { get; init; }

    public required string Color { get; init; }

    /// <summary>Sub-category used for legends and filtering (resource type, building category...).</summary>
    public string? Group { get; init; }

    /// <summary>Visually de-emphasised (opened drop pod, picked bush, killed creature...).</summary>
    public bool Dimmed { get; init; }

    public List<KeyValuePair<string, string>>? Details { get; set; }

    /// <summary>Icon key from the game assets (see GameAssets); markers without one are drawn as dots.</summary>
    public string? Icon { get; init; }

    /// <summary>Ring colour around the icon (resource purity).</summary>
    public string? Ring { get; init; }

    /// <summary>Small check mark on the marker (resource node is exploited).</summary>
    public bool Badge { get; init; }
}

public sealed class MapWorld
{
    public required SaveGame Save { get; init; }
    public List<MapFeature> Features { get; } = [];

    /// <summary>512×512 RGBA fog of war texture from the map manager, if present.</summary>
    public byte[]? FogOfWar { get; set; }
    public int FogOfWarSize { get; set; }

    public List<KeyValuePair<string, string>> Summary { get; } = [];

    /// <summary>Number of objects per display name (buildings, collectibles, resources...).</summary>
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public IEnumerable<MapFeature> In(LayerDef layer) => Features.Where(f => f.Layer == layer);
}

/// <summary>The in-game map covers this square (same bounds as the in-game map texture).</summary>
public static class WorldBounds
{
    public const double MinX = -324698.832031;
    public const double MinY = -375000;
    public const double Size = 750000;
}
