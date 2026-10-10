using System.Globalization;
using SatisfactoryMapRenderer.Game;
using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Save.Properties;

namespace SatisfactoryMapRenderer.World;

/// <summary>
/// Turns decoded save objects into drawable map features. With game assets, level actors (resource nodes,
/// collectibles, spawners) are enriched with data the save does not store (type, purity, creature) and –
/// for 1.0+ saves – actors the save does not mention at all are added unless the save lists them as collected.
/// </summary>
public sealed class WorldExtractor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Level actors are static, so a save object this close to one is the same actor.</summary>
    private const double SameActorDistance = 1000;

    private readonly SaveGame _save;
    private readonly GameAssets? _game;
    private readonly MapWorld _world;
    private readonly Dictionary<string, SaveObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeResource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeExtractor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stationNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _playerNames = new(StringComparer.Ordinal);
    /// <summary>Pawn → index into the game state's player table (1.2+), where the platform names are kept.</summary>
    private readonly Dictionary<string, int> _playerInfoIndex = new(StringComparer.Ordinal);
    private readonly List<string> _playerInfoNames = [];
    private readonly Dictionary<string, string> _trainNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LevelActor> _levelByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LevelActor> _levelByLabel = new(StringComparer.Ordinal);
    private readonly HashSet<LevelActor> _levelUsed = [];
    private readonly HashSet<string> _removedFromLevel = new(StringComparer.Ordinal);
    private double _beltLength, _pipeLength, _railLength, _hypertubeLength;

    /// <summary>A world object from the save, from the game's level data, or both.</summary>
    private sealed record WorldActor(string Class, string Name, Vector3 Position, PropertyList Properties, LevelActor? Level, SaveEntity? Entity)
    {
        public string? LevelProp(string key) => Level?.Prop(key);
    }

    private WorldExtractor(SaveGame save, GameAssets? game)
    {
        _save = save;
        _game = game;
        _world = new MapWorld { Save = save };
    }

    public static MapWorld Extract(SaveGame save, GameAssets? game = null)
    {
        var extractor = new WorldExtractor(save, game);
        extractor.Run();
        return extractor._world;
    }

    private void Run()
    {
        foreach (var obj in _save.AllObjects)
            _objects.TryAdd(obj.InstanceName, obj);

        CollectLookups();
        IndexLevelActors();

        foreach (var obj in _save.AllObjects)
        {
            if (obj is not SaveEntity entity)
                continue;
            try
            {
                HandleEntity(entity);
            }
            catch (Exception ex) when (ex is InvalidCastException or NullReferenceException or ArgumentException)
            {
                _save.Warnings.Add($"Could not map {entity.InstanceName}: {ex.Message}");
            }
        }

        AddLevelActorsMissingFromSave();
        BuildSummary();
    }

    // ------------------------------------------------------------------ level actors (game data)

    private void IndexLevelActors()
    {
        if (_game is null)
            return;
        foreach (var actor in _game.Data.Actors)
        {
            _levelByName.TryAdd(actor.Name, actor);
            if (actor.Label != actor.Name)
                _levelByLabel.TryAdd(actor.Label, actor);
        }
        // Collected pickups, destroyed rocks etc. are listed by path in the save.
        foreach (var level in _save.Levels)
            foreach (var reference in level.Collectables.Concat(level.DestroyedActors.Values.SelectMany(v => v)))
                _removedFromLevel.Add(ShortObjectName(reference.PathName));
    }

    private static string ShortObjectName(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path[(dot + 1)..] : path;
    }

    /// <summary>
    /// Finds the level actor a save object refers to: by name (1.0+ saves) or by editor label (older saves),
    /// accepted only if it stands at the same place – older game versions had a different world.
    /// </summary>
    private LevelActor? MatchLevelActor(string instanceName, Vector3 position)
    {
        string name = ShortObjectName(instanceName);
        foreach (var index in new[] { _levelByName, _levelByLabel })
        {
            if (!index.TryGetValue(name, out var actor))
                continue;
            double dx = actor.X - position.X, dy = actor.Y - position.Y, dz = actor.Z - position.Z;
            if (dx * dx + dy * dy + dz * dz <= SameActorDistance * SameActorDistance)
            {
                _levelUsed.Add(actor);
                return actor;
            }
        }
        return null;
    }

    /// <summary>
    /// 1.0+ saves only store level actors that changed. Everything else still exists in the world unless the
    /// save lists it as collected/destroyed. Older saves come from a different world version, so nothing is added.
    /// </summary>
    private void AddLevelActorsMissingFromSave()
    {
        if (_game is null || _save.Header.SaveVersion < (int)SaveCustomVersion.Version1)
            return;
        foreach (var actor in _game.Data.Actors)
        {
            if (_levelUsed.Contains(actor) || _removedFromLevel.Contains(actor.Name) || _removedFromLevel.Contains(actor.Label))
                continue;
            HandleWorldActor(new WorldActor(actor.Class, actor.Name, new Vector3(actor.X, actor.Y, actor.Z), [], actor, null));
        }
    }

    // ------------------------------------------------------------------ lookups

    private void CollectLookups()
    {
        foreach (var obj in _save.AllObjects)
        {
            var props = obj.Properties;
            switch (obj.ShortClassName)
            {
                case "FGTrainStationIdentifier":
                    if (props.GetReference("mStation") is { } station)
                        _stationNames[station.PathName] = props.GetText("mStationName") ?? "";
                    continue;
                case "FGDroneStationInfo":
                    if (props.GetReference("mStation") is { } drone)
                        _stationNames[drone.PathName] = props.GetText("mBuildingTag") ?? "";
                    continue;
                case "FGDockingStationInfo":
                    if (props.GetReference("mStation") is { } truck)
                        _stationNames[truck.PathName] = props.GetText("mBuildingTag") ?? "";
                    continue;
                case "BP_PlayerState_C":
                    if (props.GetReference("mOwnedPawn") is { } pawn)
                    {
                        _playerNames[pawn.PathName] = props.GetText("mCachedPlayerName") ?? "";
                        if (props.Get("mPlatformPlayerInfoHandle") is StructValue { Data: PlayerInfoHandleValue { PlayerInfoTableIndex: >= 0 } handle })
                            _playerInfoIndex[pawn.PathName] = handle.PlayerInfoTableIndex;
                    }
                    continue;
                case "BP_GameState_C":
                    // 1.2+: every player that ever joined, with the name of their Epic/Steam account.
                    foreach (var entry in props.GetArray("mPlayerInfoCache")?.Items ?? [])
                    {
                        var platforms = (entry as StructValue)?.Data is PropertyList info ? info.GetArray("PerPlatformInfo")?.Items : null;
                        _playerInfoNames.Add(platforms?.Select(p => (p as StructValue)?.Data is PropertyList platform ? platform.GetText("PlayerName") : null)
                            .FirstOrDefault(n => n is { Length: > 0 }) ?? "");
                    }
                    break;
                case "BP_Train_C":
                case "FGTrain":
                    if (props.GetReference("FirstVehicle") is { } first)
                        _trainNames[first.PathName] = props.GetText("mTrainName") ?? "";
                    continue;
            }

            // Extractors reference the node they sit on; their output inventory tells the resource type.
            if (props.GetReference("mExtractableResource") is { IsEmpty: false } node)
            {
                _nodeExtractor[node.PathName] = Catalog.GetBuildable(obj.ClassName)?.Name ?? obj.ShortClassName;
                if (FindOutputResource(obj) is { } resource)
                    _nodeResource[node.PathName] = resource;
            }
        }
    }

    /// <summary>Descriptor path of what an extractor produces.</summary>
    private string? FindOutputResource(SaveObject extractor)
    {
        if (extractor.Properties.GetReference("mOutputInventory") is not { } invRef || !_objects.TryGetValue(invRef.PathName, out var inventory))
            return null;

        if (inventory.Properties.GetArray("mAllowedItemDescriptors") is { } allowed)
            foreach (var item in allowed.Items)
                if (item is ObjectReference { IsEmpty: false } descriptor)
                    return descriptor.PathName;

        foreach (var (item, _) in InventoryItems(inventory))
            return item;
        return null;
    }

    private static IEnumerable<(string Item, int Count)> InventoryItems(SaveObject inventory)
    {
        if (inventory.Properties.GetArray("mInventoryStacks") is not { } stacks)
            yield break;
        foreach (var stack in stacks.Items)
        {
            if (stack is not StructValue { Data: PropertyList pl })
                continue;
            if (pl.Get("Item") is StructValue { Data: InventoryItemValue { Item.IsEmpty: false } item })
                yield return (item.Item.PathName, (int)(pl.GetNumber("NumItems") ?? 0));
        }
    }

    // ------------------------------------------------------------------ dispatch

    private void HandleEntity(SaveEntity e)
    {
        string shortName = e.ShortClassName;
        string cls = e.ClassName;

        switch (cls)
        {
            case "/Script/FactoryGame.FGLightweightBuildableSubsystem":
                AddLightweightBuildables(e);
                return;
            case "/Script/FactoryGame.FGFoliageRemoval":
                AddFoliage(e);
                return;
            case "/Script/FactoryGame.FGMapManager":
                ReadMapManager(e);
                return;
        }

        if (shortName == "Char_Player_C")
        {
            AddPlayer(e);
            return;
        }
        if (Catalog.IsVehicle(shortName))
        {
            AddVehicle(e);
            return;
        }
        if (shortName.StartsWith("Char_", StringComparison.Ordinal))
        {
            string creature = Catalog.CreatureName(shortName);
            Count(creature);
            AddMarker(Layers.Creatures, creature, e.Transform.Translation, "#ff9f43", creature, icon: _game?.CreatureIcon(shortName));
            return;
        }

        if (cls.Contains("/AnimalParts/", StringComparison.Ordinal))
        {
            AddPoint(e, Layers.Pickups, Catalog.Prettify(shortName).Replace(" Parts", " Remains"), "#c8d6e5", group: "Creature remains");
            return;
        }

        var actor = new WorldActor(shortName, e.InstanceName, e.Transform.Translation, e.Properties,
            MatchLevelActor(e.InstanceName, e.Transform.Translation), e);
        if (HandleWorldActor(actor))
            return;

        if (Catalog.GetBuildable(cls) is { } info)
            AddBuildable(e, info);
    }

    /// <summary>World objects that may come from the save, the level data or both. Returns false for other classes.</summary>
    private bool HandleWorldActor(WorldActor a)
    {
        switch (a.Class)
        {
            case "BP_CreatureSpawner_C":
            case "BP_EnemySpawner_C":
                AddSpawner(a);
                return true;
            case "BP_ResourceNode_C":
                AddResourceNode(a, "Resource Node");
                return true;
            case "BP_ResourceNodeGeyser_C":
                AddResourceNode(a, "Geyser", "Geyser");
                return true;
            case "BP_FrackingCore_C":
                AddResourceNode(a, "Resource Well (core)");
                return true;
            case "BP_FrackingSatellite_C":
                AddResourceNode(a, "Resource Well Satellite");
                return true;
            case "BP_ResourceDeposit_C":
                AddDeposit(a);
                return true;
            case "BP_Crystal_C":
                AddCollectible(a, Layers.Slugs, "Blue Power Slug", "#48dbfb", "Blue", "Desc_Crystal_C");
                return true;
            case "BP_Crystal_mk2_C":
                AddCollectible(a, Layers.Slugs, "Yellow Power Slug", "#feca57", "Yellow", "Desc_Crystal_mk2_C");
                return true;
            case "BP_Crystal_mk3_C":
                AddCollectible(a, Layers.Slugs, "Purple Power Slug", "#a55eea", "Purple", "Desc_Crystal_mk3_C");
                return true;
            case "BP_WAT1_C":
                AddCollectible(a, Layers.Artifacts, "Somersloop", "#ff4757", "Somersloop", "Desc_WAT1_C");
                return true;
            case "BP_WAT2_C":
                AddCollectible(a, Layers.Artifacts, "Mercer Sphere", "#8854d0", "Mercer Sphere", "Desc_WAT2_C");
                return true;
            case "BP_DropPod_C":
                AddDropPod(a);
                return true;
            case "BP_BerryBush_C":
                AddPlant(a, "Paleberry", "#c56cf0", "Desc_Berry_C");
                return true;
            case "BP_NutBush_C":
                AddPlant(a, "Beryl Nut", "#b8a07e", "Desc_Nut_C");
                return true;
            case "BP_Shroom_01_C":
                AddPlant(a, "Bacon Agaric", "#e17055", "Desc_Shroom_C");
                return true;
            case "FGItemPickup_Spawnable":
            case "BP_ItemPickup_Spawnable_C":
                AddItemPickup(a);
                return true;
            case "BP_Crate_C":
            case "BP_WreckCrate_C":
                AddCrate(a);
                return true;
            case "BP_SporeFlower_C":
                AddCollectible(a, Layers.Hazards, "Spore Flower", "#b33939", "Spore Flower", null);
                return true;
            case "BP_GasPillar_01_C" or "BP_GasPillar_02_C" or "BP_GasPillar_03_C" or "BP_GasPillar_04_C" or "BP_GasPillar_05_C":
                AddCollectible(a, Layers.Hazards, "Gas Pillar", "#6ab04c", "Gas Pillar", null);
                return true;
            case "BP_DestructibleLargeRock_C":
                AddCollectible(a, Layers.Hazards, "Destructible Rock (large)", "#84817a", "Destructible Rock", null);
                return true;
            case "BP_DestructibleSmallRock_C" or "BP_DestructibleFlatRock_C":
                AddCollectible(a, Layers.Hazards, "Destructible Rock (small)", "#84817a", "Destructible Rock", null);
                return true;
            case "BP_Beacon_C":
                Count("Beacon");
                AddMarker(Layers.Beacons, a.Properties.GetText("mCompassText") is { Length: > 0 } text ? text : "Beacon", a.Position, "#00d2d3",
                    "Beacon", icon: _game?.MapIcon("beacon"));
                return true;
            case "BP_MercerShrine_C" or "BP_SomerSloopShrine_C":
                return true; // pedestals of the artifacts above; nothing to show
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ buildables

    private void AddBuildable(SaveEntity e, BuildInfo info)
    {
        if (_game?.DisplayName(e.ShortClassName) is { } gameName)
            info = info with { Name = gameName };
        Count(info.Name);
        switch (info.Category)
        {
            case BuildCategory.Belt:
                _beltLength += AddSpline(e, info, Layers.Belts, 120, BeltColor(e.ShortClassName));
                return;
            case BuildCategory.Pipe:
                _pipeLength += AddSpline(e, info, Layers.Pipes, 110, e.ShortClassName.Contains("MK2") ? "#2e86de" : "#7fc4ef");
                return;
            case BuildCategory.Railway:
                _railLength += AddSpline(e, info, Layers.Railways, 320, "#dfe4ea");
                return;
            case BuildCategory.Hypertube when e.Properties.Find("mSplineData") is not null:
                _hypertubeLength += AddSpline(e, info, Layers.Pipes, 170, "#e056fd");
                return;
            case BuildCategory.PowerLine:
                AddWire(e, info);
                return;
            case BuildCategory.Lift:
                AddBox(e, info, Layers.Belts, BeltColor(e.ShortClassName.Replace("Lift", "Belt")));
                return;
            case BuildCategory.BeltSupport:
                AddBox(e, info, Layers.Belts, "#8e6b3d");
                return;
            case BuildCategory.PipeAttachment:
            case BuildCategory.Hypertube:
                AddBox(e, info, Layers.Pipes, info.Category == BuildCategory.Hypertube ? "#b83dd1" : "#1b6fb5");
                return;
            case BuildCategory.PowerPole:
                AddBox(e, info, Layers.Power, "#f7dc6f");
                return;
            case BuildCategory.Foundation:
            case BuildCategory.Wall:
            case BuildCategory.Walkway:
            case BuildCategory.Decor:
                AddBox(e, info, Layers.Foundations, StructureColor(info.Category));
                return;
        }

        string? buildingIcon = _game?.BuildingIcon(e.ShortClassName);
        var feature = AddBox(e, info, Layers.Buildings, CategoryColor(info.Category), BuildingDetails(e, info), buildingIcon);

        if (info.Category == BuildCategory.Special && e.ShortClassName is not ("Build_HubTerminal_C" or "Build_TetrominoGame_Computer_C"))
            AddMarker(Layers.Landmarks, info.Name, e.Transform.Translation, "#e74c3c", info.Name, details: feature.Details, icon: e.ShortClassName switch
            {
                "Build_TradingPost_C" => _game?.MapIcon("hub"),
                "Build_SpaceElevator_C" => _game?.MapIcon("spelevator"),
                "Build_RadarTower_C" => _game?.MapIcon("radartower"),
                _ => null,
            } ?? buildingIcon);

        if (e.ShortClassName is "Build_TrainStation_C" or "Build_TruckStation_C" or "Build_DroneStation_C")
        {
            string name = _stationNames.GetValueOrDefault(e.InstanceName) is { Length: > 0 } n ? n : info.Name;
            AddMarker(Layers.Stations, name, e.Transform.Translation, "#a29bfe", info.Name,
                details: [new("Type", info.Name), .. feature.Details ?? []], icon: buildingIcon);
        }
    }

    private List<KeyValuePair<string, string>> BuildingDetails(SaveEntity e, BuildInfo info)
    {
        var p = e.Properties;
        var details = new List<KeyValuePair<string, string>>();
        if (p.GetReference("mCurrentRecipe") is { IsEmpty: false } recipe)
            details.Add(new("Recipe", Catalog.RecipeName(recipe.PathName)));
        if (p.GetNumber("mCurrentPotential") is { } potential)
            details.Add(new("Clock speed", $"{potential * 100:0.#} %"));
        if (p.GetNumber("mCurrentProductionBoost") is { } boost && boost > 1)
            details.Add(new("Production boost", $"{boost * 100:0.#} %"));
        if (p.GetBool("mIsProducing") is { } producing)
            details.Add(new("Producing", producing ? "yes" : "no"));
        if (p.GetReference("mExtractableResource") is { IsEmpty: false } node && _nodeResource.TryGetValue(node.PathName, out var resource))
            details.Add(new("Extracting", Catalog.ResourceName(resource)));
        if (_stationNames.TryGetValue(e.InstanceName, out var stationName) && stationName.Length > 0)
            details.Add(new("Name", stationName));
        if (info.Category == BuildCategory.Storage && FindInventory(e) is { } inventory)
            AddInventorySummary(details, inventory);
        return details;
    }

    private SaveObject? FindInventory(SaveEntity e)
    {
        foreach (var name in new[] { "mStorageInventory", "mInventory", "mInputInventory" })
            if (e.Properties.GetReference(name) is { IsEmpty: false } r && _objects.TryGetValue(r.PathName, out var inv))
                return inv;
        return null;
    }

    private static void AddInventorySummary(List<KeyValuePair<string, string>> details, SaveObject inventory)
    {
        var totals = new Dictionary<string, int>();
        foreach (var (item, count) in InventoryItems(inventory))
            totals[item] = totals.GetValueOrDefault(item) + count;
        foreach (var (item, count) in totals.OrderByDescending(t => t.Value).Take(6))
            details.Add(new(Catalog.ItemName(item), count.ToString("N0", Inv)));
        if (totals.Count == 0)
            details.Add(new("Contents", "empty"));
    }

    private MapFeature AddBox(SaveEntity e, BuildInfo info, LayerDef layer, string color, List<KeyValuePair<string, string>>? details = null, string? icon = null)
    {
        var t = e.Transform;
        var feature = new MapFeature
        {
            Layer = layer,
            Shape = Shape.Box,
            Name = info.Name,
            Class = e.ShortClassName,
            Position = t.Translation,
            Yaw = t.Rotation.Yaw,
            SizeX = info.SizeX * Math.Abs(t.Scale.X),
            SizeY = info.SizeY * Math.Abs(t.Scale.Y),
            Color = color,
            Group = info.Category.ToString(),
            Details = details,
            Icon = icon,
        };
        _world.Features.Add(feature);
        return feature;
    }

    private void AddLightweightBuildables(SaveEntity e)
    {
        if (e.SpecialData is not LightweightBuildables data)
            return;
        foreach (var group in data.Groups)
        {
            var info = Catalog.GetBuildable(group.ClassName) ?? new BuildInfo(Catalog.Prettify(Catalog.ShortName(group.ClassName)), BuildCategory.Foundation, 800, 800);
            if (_game?.DisplayName(group.ClassName) is { } gameName)
                info = info with { Name = gameName };
            var layer = info.Category is BuildCategory.Foundation or BuildCategory.Wall or BuildCategory.Walkway or BuildCategory.Decor
                ? Layers.Foundations
                : Layers.Buildings;
            string color = layer == Layers.Foundations ? StructureColor(info.Category) : CategoryColor(info.Category);
            foreach (var instance in group.Instances)
            {
                Count(info.Name);
                var t = instance.Transform;
                _world.Features.Add(new MapFeature
                {
                    Layer = layer,
                    Shape = Shape.Box,
                    Name = info.Name,
                    Class = Catalog.ShortName(group.ClassName),
                    Position = t.Translation,
                    Yaw = t.Rotation.Yaw,
                    SizeX = info.SizeX * Math.Abs(t.Scale.X),
                    SizeY = info.SizeY * Math.Abs(t.Scale.Y),
                    Color = color,
                    Group = info.Category.ToString(),
                });
            }
        }
    }

    /// <summary>Converts the Hermite spline in mSplineData to a world space polyline. Returns its length.</summary>
    private double AddSpline(SaveEntity e, BuildInfo info, LayerDef layer, double width, string color)
    {
        if (e.Properties.GetArray("mSplineData") is not { Items.Count: >= 2 } spline)
        {
            AddBox(e, info with { SizeX = width, SizeY = width }, layer, color);
            return 0;
        }

        var points = new List<(Vector3 Location, Vector3 Arrive, Vector3 Leave)>(spline.Items.Count);
        foreach (var item in spline.Items)
            if (item is StructValue { Data: PropertyList pl })
                points.Add((Vec(pl, "Location"), Vec(pl, "ArriveTangent"), Vec(pl, "LeaveTangent")));

        var path = new List<double>(points.Count * 8);
        double length = 0;
        Vector3? previous = null;
        void Emit(Vector3 local)
        {
            var world = e.Transform.TransformPosition(local);
            if (previous is { } prev)
            {
                var d = world - prev;
                length += Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            }
            previous = world;
            path.Add(world.X);
            path.Add(world.Y);
        }

        Emit(points[0].Location);
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var (p0, _, m0) = points[i];
            var (p1, m1, _) = points[i + 1];
            var chord = p1 - p0;
            double chordLength = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
            int steps = Math.Clamp((int)(chordLength / 250), 1, 32);
            for (int s = 1; s <= steps; s++)
                Emit(Hermite(p0, m0, p1, m1, (double)s / steps));
        }

        _world.Features.Add(new MapFeature
        {
            Layer = layer,
            Shape = Shape.Line,
            Name = info.Name,
            Class = e.ShortClassName,
            Position = e.Transform.Translation,
            Path = [.. path],
            Thickness = width,
            Color = color,
            Group = info.Name,
        });
        return length;
    }

    private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
        return p0 * h00 + m0 * h10 + p1 * h01 + m1 * h11;
    }

    private void AddWire(SaveEntity e, BuildInfo info)
    {
        if (e.SpecialData is not WireConnection wire)
            return;
        var from = wire.SourcePosition ?? ActorPosition(wire.Source.PathName);
        var to = wire.TargetPosition ?? ActorPosition(wire.Target.PathName);
        if (from is not { } a || to is not { } b)
            return;
        _world.Features.Add(new MapFeature
        {
            Layer = Layers.Power,
            Shape = Shape.Line,
            Name = info.Name,
            Class = e.ShortClassName,
            Position = a,
            Path = [a.X, a.Y, b.X, b.Y],
            Thickness = 40,
            Color = e.ShortClassName.Contains("Xmass") ? "#ff7675" : "#f7dc6f",
            Group = info.Name,
        });
    }

    /// <summary>Position of the actor owning a component path ("...Build_PowerPoleMk1_C_123.PowerConnection").</summary>
    private Vector3? ActorPosition(string componentPath)
    {
        string path = componentPath;
        while (true)
        {
            if (_objects.TryGetValue(path, out var obj) && obj is SaveEntity entity)
                return entity.Transform.Translation;
            int dot = path.LastIndexOf('.');
            int colon = path.LastIndexOf(':');
            if (dot <= colon)
                return null;
            path = path[..dot];
        }
    }

    // ------------------------------------------------------------------ world objects

    private void AddPlayer(SaveEntity e)
    {
        int index = _world.Features.Count(f => f.Layer == Layers.Players) + 1;
        string name = PlayerName(e) ?? $"Player {index}";
        var details = new List<KeyValuePair<string, string>>();
        if (e.Properties.GetReference("mHealthComponent") is { } hc && _objects.TryGetValue(hc.PathName, out var health)
            && health.Properties.GetNumber("mCurrentHealth") is { } hp)
            details.Add(new("Health", hp.ToString("0", Inv)));
        if (e.Properties.GetReference("mInventory") is { } inv && _objects.TryGetValue(inv.PathName, out var inventory))
            details.Add(new("Inventory stacks", InventoryItems(inventory).Count().ToString(Inv)));
        AddMarker(Layers.Players, name, e.Transform.Translation, "#ff4d4d", "Player", details: details, icon: _game?.MapIcon("player"));
    }

    private void AddVehicle(SaveEntity e)
    {
        string name = Catalog.VehicleName(e.ShortClassName);
        var details = new List<KeyValuePair<string, string>>();
        if (_trainNames.TryGetValue(e.InstanceName, out var train) && train.Length > 0)
        {
            details.Add(new("Train", train));
            name = $"{name} ({train})";
        }
        if (e.Properties.GetBool("mIsSelfDriving") is true || e.Properties.Find("mTargetList") is not null)
            details.Add(new("Autopilot", e.Properties.GetBool("mIsSelfDriving") is true ? "on" : "off"));
        if (FindInventory(e) is { } inventory)
            AddInventorySummary(details, inventory);
        Count(Catalog.VehicleName(e.ShortClassName));
        AddMarker(Layers.Vehicles, name, e.Transform.Translation, "#c56cf0", Catalog.VehicleName(e.ShortClassName), details: details,
            icon: VehicleIcon(e.ShortClassName));
    }

    private string? VehicleIcon(string shortClass) => shortClass switch
    {
        "BP_Tractor_C" => _game?.MapIcon("tractor"),
        "BP_Truck_C" => _game?.MapIcon("truck"),
        "BP_Explorer_C" => _game?.MapIcon("explorer"),
        "BP_Locomotive_C" or "BP_FreightWagon_C" => _game?.MapIcon("train"),
        _ => null,
    };

    private void AddSpawner(WorldActor a)
    {
        int points = 0, killed = 0;
        string? creatureClass = a.LevelProp("creature") is { } levelCreature ? Catalog.ShortName(levelCreature) : null;
        if (a.Properties.GetArray("mSpawnData") is { } spawnData)
        {
            foreach (var item in spawnData.Items)
            {
                if (item is not StructValue { Data: PropertyList pl })
                    continue;
                points++;
                if (pl.GetBool("WasKilled") is true)
                    killed++;
                if (pl.Get("CreatureClassOverride") is ObjectReference { IsEmpty: false } overrideClass)
                    creatureClass = Catalog.ShortName(overrideClass.PathName);
            }
        }
        string? creature = creatureClass is null ? null : Catalog.CreatureName(creatureClass);
        var details = new List<KeyValuePair<string, string>>();
        if (creature is not null)
            details.Add(new("Creature", creature));
        if (points > 0)
        {
            details.Add(new("Spawn points", points.ToString(Inv)));
            details.Add(new("Killed", killed.ToString(Inv)));
        }
        bool cleared = killed > 0 && killed >= points;
        Count(creature is null ? "Creature spawner" : $"Spawner: {creature}");
        AddMarker(Layers.Spawners, creature is null ? "Creature spawner" : $"{creature} spawner", a.Position, "#ee5a24",
            creature ?? "Unknown creature", details: details, dimmed: cleared,
            icon: creatureClass is null ? null : _game?.CreatureIcon(creatureClass));
    }

    private void AddResourceNode(WorldActor a, string kind, string? fixedResource = null)
    {
        string? resourcePath = a.LevelProp("resource")
            ?? _nodeResource.GetValueOrDefault(a.Name)
            ?? (a.Properties.Get("mResourceClass") is ObjectReference { IsEmpty: false } rc ? rc.PathName : null);
        string? resource = fixedResource ?? (resourcePath is null ? null : Catalog.ResourceName(resourcePath));
        string? purity = PurityName(a.LevelProp("purity") ?? a.Properties.GetText("mPurity"));

        var details = new List<KeyValuePair<string, string>> { new("Type", kind), new("Resource", resource ?? "unknown (not stored in save)") };
        if (purity is not null)
            details.Add(new("Purity", purity));
        bool occupied = _nodeExtractor.TryGetValue(a.Name, out var extractor);
        details.Add(new("Occupied", occupied ? extractor! : "no"));
        if (a.Properties.GetText("mState") is { } state)
            details.Add(new("State", state.Split("::").Last().Replace("FSS_", "")));

        string label = resource ?? "Unknown type (not stored in save)";
        string group = purity is null ? label : $"{label} · {purity}";
        Count("Resource: " + group);
        AddMarker(Layers.Resources, resource is null ? kind : $"{group} ({kind})", a.Position, Catalog.ResourceColor(resource ?? "Unknown"), group,
            details: details, dimmed: resource is null,
            icon: resourcePath is null ? null : _game?.ItemIcon(resourcePath), ring: PurityColor(purity), badge: occupied);
    }

    /// <summary>The game spells the lowest purity "RP_Inpure".</summary>
    private static string? PurityName(string? purity) => purity?.Split("::").Last() switch
    {
        null or "" => null,
        "RP_Inpure" or "RP_Impure" => "Impure",
        "RP_Normal" => "Normal",
        "RP_Pure" => "Pure",
        var other => other.Replace("RP_", ""),
    };

    private static string? PurityColor(string? purity) => purity switch
    {
        "Impure" => "#e74c3c",
        "Normal" => "#f39c12",
        "Pure" => "#2ecc71",
        _ => null,
    };

    private void AddDeposit(WorldActor a)
    {
        var p = a.Properties;
        var details = new List<KeyValuePair<string, string>>();
        string? resourcePath = a.LevelProp("resource");
        if (resourcePath is not null)
            details.Add(new("Resource", Catalog.ResourceName(resourcePath)));
        if (p.GetNumber("mResourcesLeft") is { } left)
            details.Add(new("Resources left", left.ToString(Inv)));
        if (p.GetNumber("mMineAmount") is { } amount)
            details.Add(new("Mine amount", amount.ToString(Inv)));
        bool empty = p.GetBool("mIsEmptied") is true || p.GetNumber("mResourcesLeft") is 0;
        Count("Resource deposit");
        AddMarker(Layers.Deposits, "Resource deposit", a.Position, "#95a5a6", empty ? "Emptied" : "Available", details: details, dimmed: empty,
            icon: resourcePath is null ? null : _game?.ItemIcon(resourcePath));
    }

    /// <summary>Power slugs, artifacts, hazards: present means not collected/destroyed yet.</summary>
    private void AddCollectible(WorldActor a, LayerDef layer, string name, string color, string group, string? itemIcon)
    {
        Count(name);
        AddMarker(layer, name, a.Position, color, group, icon: itemIcon is null ? null : _game?.ItemIcon(itemIcon));
    }

    private void AddDropPod(WorldActor a)
    {
        bool opened = a.Properties.GetBool("mHasBeenOpened") is true;
        var details = new List<KeyValuePair<string, string>> { new("Opened", opened ? "yes" : "no") };
        Count("Crash site");
        AddMarker(Layers.DropPods, "Crash site", a.Position, "#feca57", opened ? "Opened" : "Unopened", details: details, dimmed: opened,
            icon: _game?.ItemIcon("Desc_HardDrive_C") ?? _game?.MapIcon("pod"));
    }

    private void AddPlant(WorldActor a, string name, string color, string itemIcon)
    {
        var p = a.Properties;
        double? remaining = p.GetNumber("mSavedNumItems")
            ?? (p.Get("mPickupItems") is StructValue { Data: PropertyList pl } ? pl.GetNumber("NumItems") : null);
        var details = remaining is { } r ? new List<KeyValuePair<string, string>> { new("Items", r.ToString(Inv)) } : null;
        Count(name);
        AddMarker(Layers.Plants, name, a.Position, color, name, details: details, dimmed: remaining is 0, icon: _game?.ItemIcon(itemIcon));
    }

    private void AddItemPickup(WorldActor a)
    {
        string name = "Item";
        string? itemPath = null;
        int count = int.TryParse(a.LevelProp("amount"), out int levelAmount) ? levelAmount : 0;
        if (a.Properties.Get("mPickupItems") is StructValue { Data: PropertyList pl })
        {
            if (pl.Get("Item") is StructValue { Data: InventoryItemValue { Item.IsEmpty: false } item })
            {
                itemPath = item.Item.PathName;
                name = _game?.DisplayName(itemPath) ?? Catalog.ItemName(itemPath);
            }
            count = (int)(pl.GetNumber("NumItems") ?? count);
        }
        Count("Item pickup");
        AddMarker(Layers.Pickups, count > 1 ? $"{name} ×{count}" : name, a.Position, "#f8a5c2", "Item",
            details: [new("Item", name), new("Amount", count.ToString(Inv))], icon: itemPath is null ? null : _game?.ItemIcon(itemPath));
    }

    private void AddCrate(WorldActor a)
    {
        var details = new List<KeyValuePair<string, string>>();
        if (a.Entity is { } entity && FindInventory(entity) is { } inventory)
            AddInventorySummary(details, inventory);
        Count("Crate");
        AddMarker(Layers.Pickups, "Crate", a.Position, "#e15f41", "Crate", details: details, icon: _game?.MapIcon("deathcrate"));
    }

    private void AddFoliage(SaveEntity e)
    {
        string type = e.Properties.GetText("mFoliageTypeName") ?? "";
        (string group, string color, double size) = ClassifyFoliage(type);
        string name = Catalog.Prettify(type.Replace("_FoliageType", ""));

        IEnumerable<Vector3> locations = [];
        if (e.Properties.Get("mRemovalLocations") is SetValue set)
            locations = set.Items.OfType<Vector3>();
        else if (e.Properties.Get("mRemovedInstances") is StructValue { Data: PropertyList removed } && removed.GetArray("Items") is { } items)
            locations = items.Items.Select(TranslationOf).OfType<Vector3>();

        foreach (var location in locations)
        {
            Count("Removed foliage: " + group);
            _world.Features.Add(new MapFeature
            {
                Layer = Layers.Foliage,
                Shape = Shape.Point,
                Name = name,
                Position = location,
                Thickness = size,
                Color = color,
                Group = group,
            });
        }
    }

    private static Vector3? TranslationOf(object? item) =>
        item is StructValue { Data: PropertyList pl }
        && pl.Get("Transform") is StructValue { Data: PropertyList transform }
        && transform.Get("Translation") is StructValue { Data: Vector3 v }
            ? v
            : null;

    private static (string Group, string Color, double Size) ClassifyFoliage(string type)
    {
        if (type.Contains("Tree", StringComparison.OrdinalIgnoreCase) || type.Contains("Trunk", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Palm", StringComparison.OrdinalIgnoreCase) || type.Contains("Pine", StringComparison.OrdinalIgnoreCase))
            return ("Trees", "#2ecc71", 500);
        if (type.Contains("Bush", StringComparison.OrdinalIgnoreCase) || type.Contains("Shrub", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Fern", StringComparison.OrdinalIgnoreCase))
            return ("Bushes", "#a3cb38", 250);
        if (type.Contains("Rock", StringComparison.OrdinalIgnoreCase) || type.Contains("Stone", StringComparison.OrdinalIgnoreCase))
            return ("Rocks", "#95a5a6", 250);
        return ("Other vegetation", "#6ab04c", 150);
    }

    private void ReadMapManager(SaveEntity e)
    {
        if (e.Properties.GetArray("mFogOfWarRawData") is { } fog && fog.Items.Count > 0)
        {
            var bytes = new byte[fog.Items.Count];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = fog.Items[i] is byte b ? b : (byte)0;
            int size = (int)Math.Sqrt(bytes.Length / 4.0);
            if (size * size * 4 == bytes.Length)
            {
                _world.FogOfWar = bytes;
                _world.FogOfWarSize = size;
            }
        }

        if (e.Properties.GetArray("mMapMarkers") is { } markers)
        {
            foreach (var item in markers.Items)
            {
                if (item is not StructValue { Data: PropertyList pl } || AsVector(pl.Get("Location")) is not { } location)
                    continue;
                string name = pl.GetText("Name") is { Length: > 0 } n ? n : "Map marker";
                string color = pl.Get("Color") is StructValue { Data: LinearColor c } ? ToHex(c) : "#00d2d3";
                var details = new List<KeyValuePair<string, string>>();
                if (pl.GetText("CategoryName") is { Length: > 0 } category)
                    details.Add(new("Category", category));
                AddMarker(Layers.Beacons, name, location, color, "Map marker", details: details);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private void AddPoint(SaveEntity e, LayerDef layer, string name, string color, string group, string? icon = null)
    {
        Count(name);
        AddMarker(layer, name, e.Transform.Translation, color, group, icon: icon);
    }

    private void AddMarker(LayerDef layer, string name, Vector3 position, string color, string group,
        List<KeyValuePair<string, string>>? details = null, bool dimmed = false, string? icon = null, string? ring = null, bool badge = false)
    {
        _world.Features.Add(new MapFeature
        {
            Layer = layer,
            Shape = Shape.Point,
            Name = name,
            Position = position,
            Color = color,
            Group = group,
            Dimmed = dimmed,
            Details = details,
            Icon = icon,
            Ring = ring,
            Badge = badge,
        });
    }

    private void Count(string key) => _world.Counts[key] = _world.Counts.GetValueOrDefault(key) + 1;

    /// <summary>
    /// The player's account name. Where it is kept changed over time: on the player state (up to 1.1),
    /// on the character itself (1.2), and for everyone who ever joined in the game state's player table.
    /// </summary>
    private string? PlayerName(SaveEntity pawn)
    {
        if (pawn.Properties.GetText("mCachedPlayerName") is { Length: > 0 } cached)
            return cached;
        if (_playerNames.GetValueOrDefault(pawn.InstanceName) is { Length: > 0 } fromState)
            return fromState;
        return _playerInfoIndex.TryGetValue(pawn.InstanceName, out int index) && index < _playerInfoNames.Count && _playerInfoNames[index].Length > 0
            ? _playerInfoNames[index]
            : null;
    }

    private static Vector3 Vec(PropertyList pl, string name) => AsVector(pl.Get(name)) ?? default;

    /// <summary>A vector struct in its binary form, or (some 1.2 structs) as a tagged X/Y/Z property list.</summary>
    private static Vector3? AsVector(object? value) => value switch
    {
        StructValue { Data: Vector3 v } => v,
        StructValue { Data: PropertyList xyz } when xyz.GetNumber("X") is { } x && xyz.GetNumber("Y") is { } y =>
            new Vector3(x, y, xyz.GetNumber("Z") ?? 0),
        _ => null,
    };

    private static string ToHex(LinearColor c)
    {
        static int Channel(float v) => Math.Clamp((int)Math.Round(Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2) * 255), 0, 255);
        return $"#{Channel(c.R):x2}{Channel(c.G):x2}{Channel(c.B):x2}";
    }

    private static string BeltColor(string shortName) => shortName switch
    {
        _ when shortName.Contains("Mk1") => "#d9b36c",
        _ when shortName.Contains("Mk2") => "#e8a33d",
        _ when shortName.Contains("Mk3") => "#f08c2a",
        _ when shortName.Contains("Mk4") => "#f06d2a",
        _ when shortName.Contains("Mk5") => "#e8452c",
        _ when shortName.Contains("Mk6") => "#d42a5a",
        _ => "#f0a030",
    };

    public static string CategoryColor(BuildCategory category) => category switch
    {
        BuildCategory.Production => "#4f8fd6",
        BuildCategory.Extraction => "#c9a227",
        BuildCategory.Power => "#f1c40f",
        BuildCategory.Storage => "#27ae60",
        BuildCategory.Logistics => "#e67e22",
        BuildCategory.Transport => "#9b59b6",
        BuildCategory.Special => "#e74c3c",
        _ => "#7f8c8d",
    };

    private static string StructureColor(BuildCategory category) => category switch
    {
        BuildCategory.Wall => "#5c636b",
        BuildCategory.Walkway => "#a4adb7",
        BuildCategory.Decor => "#6c7a89",
        _ => "#80888f",
    };

    private static string GamePhaseName(string phase) => phase.Split("::").Last() switch
    {
        "EGP_EarlyGame" => "Phase 1 (Distribution Platform)",
        "EGP_MidGame" => "Phase 2 (Construction Dock)",
        "EGP_LateGame" => "Phase 3 (Main Body)",
        "EGP_EndGame" => "Phase 4 (Propulsion)",
        "EGP_FoodCourt" => "All phases completed",
        "EGP_Victory" => "Project Assembly completed",
        var other => other.Replace("EGP_", ""),
    };

    private void BuildSummary()
    {
        var h = _save.Header;
        var s = _world.Summary;
        s.Add(new("Session", h.SessionName));
        if (h.SaveName.Length > 0)
            s.Add(new("Save name", h.SaveName));
        s.Add(new("Game version", $"{h.GameVersionName} (build {h.BuildVersion}, save v{h.SaveVersion})"));
        s.Add(new("Play time", $"{(int)h.PlayTime.TotalHours}h {h.PlayTime.Minutes:00}m"));
        if (h.SaveDateTimeUtc != default)
            s.Add(new("Saved", h.SaveDateTimeUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv)));
        if (h.IsModded)
            s.Add(new("Modded", "yes"));
        if (h.IsCreativeModeEnabled)
            s.Add(new("Creative mode", "yes"));

        string? phase = _save.AllObjects.FirstOrDefault(o => o.ShortClassName == "BP_GamePhaseManager_C")?.Properties.GetText("mGamePhase");
        if (phase is not null)
            s.Add(new("Game phase", GamePhaseName(phase)));

        s.Add(new("Objects in save", _save.AllObjects.Count().ToString("N0", Inv)));
        s.Add(new("Buildings", _world.Features.Count(f => f.Layer == Layers.Buildings).ToString("N0", Inv)));
        s.Add(new("Foundations & walls", _world.Features.Count(f => f.Layer == Layers.Foundations).ToString("N0", Inv)));
        s.Add(new("Conveyor belts", $"{_beltLength / 100000:0.00} km"));
        s.Add(new("Pipelines", $"{_pipeLength / 100000:0.00} km"));
        s.Add(new("Railways", $"{_railLength / 100000:0.00} km"));
        if (_hypertubeLength > 0)
            s.Add(new("Hypertubes", $"{_hypertubeLength / 100000:0.00} km"));
        s.Add(new("Power lines", _world.Features.Count(f => f.Layer == Layers.Power && f.Shape == Shape.Line).ToString("N0", Inv)));
    }
}
