using SatisfactoryMapRenderer.Save.Properties;

namespace SatisfactoryMapRenderer.Save;

/// <summary>Power line / wire endpoints (connection components of the two poles or machines).</summary>
public sealed record WireConnection(ObjectReference Source, ObjectReference Target, Vector3? SourcePosition = null, Vector3? TargetPosition = null);

/// <summary>Power circuit id → circuit object.</summary>
public sealed record CircuitList(IReadOnlyList<(int Id, ObjectReference Circuit)> Circuits);

public sealed record ObjectList(IReadOnlyList<ObjectReference> Objects);

/// <summary>1.0+: foundations, walls etc. are stored as instances inside FGLightweightBuildableSubsystem.</summary>
public sealed record LightweightBuildables(int RuntimeVersion, IReadOnlyList<LightweightBuildableGroup> Groups);

public sealed record LightweightBuildableGroup(string ClassName, IReadOnlyList<LightweightBuildableInstance> Instances);

public sealed record LightweightBuildableInstance(
    Transform Transform,
    string SwatchSlot,
    string Material,
    string Pattern,
    string Skin,
    LinearColor PrimaryColor,
    LinearColor SecondaryColor,
    string PaintFinish,
    byte PatternRotation,
    string Recipe,
    string BlueprintProxy,
    DynamicStructValue? TypeSpecificData,
    PlayerInfoHandleValue? BuiltBy);

public sealed record ConveyorChainData(
    ObjectReference FirstBelt,
    ObjectReference LastBelt,
    IReadOnlyList<ObjectReference> Belts,
    float TotalLength,
    int ItemCount)
{
    /// <summary>Per belt of the chain, in the order of the belts: where on the chain it starts and ends.</summary>
    public IReadOnlyList<ConveyorChainSegment> Segments { get; init; } = [];

    /// <summary>The items on the chain: what they are and where on the chain they are.</summary>
    public IReadOnlyList<ConveyorChainItem> Items { get; init; } = [];
}

public sealed record ConveyorChainSegment(ObjectReference Belt, float OffsetAtStart, float StartsAtLength, float EndsAtLength, int FirstItem, int LastItem, int IndexInChain);

public sealed record ConveyorChainItem(string Item, float Position);

/// <summary>Parses the class specific data that follows the property list of some objects.</summary>
public static class SpecialDataReader
{
    public static object? Read(BinaryDataReader r, ReadContext ctx, string className, int remaining)
    {
        if (remaining <= 0)
            return null;

        switch (className)
        {
            case "/Game/FactoryGame/Buildable/Factory/PowerLine/Build_PowerLine.Build_PowerLine_C":
            case "/Game/FactoryGame/Events/Christmas/Buildings/PowerLineLights/Build_XmassLightsLine.Build_XmassLightsLine_C":
            {
                int start = r.Position;
                var source = ObjectReference.Read(r);
                var target = ObjectReference.Read(r);
                // Update 7+ caches the world positions of both wire ends.
                if (remaining - (r.Position - start) >= 24)
                    return new WireConnection(source, target, Vector3.ReadFloat(r), Vector3.ReadFloat(r));
                return new WireConnection(source, target);
            }

            case "/Game/FactoryGame/-Shared/Blueprint/BP_CircuitSubsystem.BP_CircuitSubsystem_C":
            {
                int count = r.ReadInt32();
                var list = new List<(int, ObjectReference)>(count);
                for (int i = 0; i < count; i++)
                    list.Add((r.ReadInt32(), ObjectReference.Read(r)));
                return new CircuitList(list);
            }

            case "/Game/FactoryGame/-Shared/Blueprint/BP_GameState.BP_GameState_C":
            case "/Game/FactoryGame/-Shared/Blueprint/BP_GameMode.BP_GameMode_C":
            {
                int count = r.ReadInt32();
                var list = new List<ObjectReference>(count);
                for (int i = 0; i < count; i++)
                    list.Add(ObjectReference.Read(r));
                return new ObjectList(list);
            }

            case "/Script/FactoryGame.FGLightweightBuildableSubsystem":
                return ReadLightweightBuildables(r, ctx);

            case "/Script/FactoryGame.FGConveyorChainActor":
            case "/Script/FactoryGame.FGConveyorChainActor_RepSizeMedium":
            case "/Script/FactoryGame.FGConveyorChainActor_RepSizeLarge":
            case "/Script/FactoryGame.FGConveyorChainActor_RepSizeHuge":
            case "/Script/FactoryGame.FGConveyorChainActor_RepSizeNoCull":
                return ReadConveyorChain(r, ctx);

            default:
                return null;
        }
    }

    private static LightweightBuildables ReadLightweightBuildables(BinaryDataReader r, ReadContext ctx)
    {
        int runtimeVersion = ctx.SaveVersion >= (int)SaveCustomVersion.LightweightBuildableSubsystemWritesRuntimeVersion
            ? r.ReadInt32()
            : 1;
        var propertyReader = new PropertyReader(r, ctx);
        int groupCount = r.ReadInt32();
        var groups = new List<LightweightBuildableGroup>(groupCount);
        for (int g = 0; g < groupCount; g++)
        {
            var type = ObjectReference.Read(r);
            int count = r.ReadInt32();
            var instances = new List<LightweightBuildableInstance>(count);
            for (int i = 0; i < count; i++)
            {
                var transform = Transform.ReadDouble(r);
                string swatch = ObjectReference.Read(r).PathName;
                string material = ObjectReference.Read(r).PathName;
                string pattern = ObjectReference.Read(r).PathName;
                string skin = ObjectReference.Read(r).PathName;
                var primary = new LinearColor(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var secondary = new LinearColor(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                string finish = ObjectReference.Read(r).PathName;
                byte patternRotation = r.ReadByte();
                string recipe = ObjectReference.Read(r).PathName;
                string blueprintProxy = ObjectReference.Read(r).PathName;
                var typeSpecific = runtimeVersion >= 2 ? propertyReader.ReadDynamicStruct() : null;
                var builtBy = runtimeVersion >= 3 ? PropertyReader.ReadPlayerInfoHandle(r, ctx) : (PlayerInfoHandleValue?)null;
                instances.Add(new LightweightBuildableInstance(transform, swatch, material, pattern, skin, primary, secondary,
                    finish, patternRotation, recipe, blueprintProxy, typeSpecific, builtBy));
            }
            groups.Add(new LightweightBuildableGroup(type.PathName, instances));
        }
        return new LightweightBuildables(runtimeVersion, groups);
    }

    private static ConveyorChainData ReadConveyorChain(BinaryDataReader r, ReadContext ctx)
    {
        var propertyReader = new PropertyReader(r, ctx);
        var lastBelt = ObjectReference.Read(r);
        var firstBelt = ObjectReference.Read(r);
        int segmentCount = r.ReadInt32();
        var belts = new List<ObjectReference>(segmentCount);
        var segments = new List<ConveyorChainSegment>(segmentCount);
        for (int i = 0; i < segmentCount; i++)
        {
            ObjectReference.Read(r); // chain actor
            var belt = ObjectReference.Read(r);
            belts.Add(belt);
            int points = r.ReadInt32();
            r.Skip(points * 9 * 8); // location, arrive + leave tangent (doubles)
            segments.Add(new ConveyorChainSegment(belt, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        }
        float totalLength = r.ReadSingle();
        r.ReadInt32(); // number of items
        r.ReadInt32(); // first chain item index
        r.ReadInt32(); // last chain item index
        int itemCount = r.ReadInt32();
        var items = new List<ConveyorChainItem>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            string item = ObjectReference.Read(r).PathName;
            propertyReader.ReadDynamicStruct(); // item state
            items.Add(new ConveyorChainItem(item, r.ReadSingle()));
        }
        return new ConveyorChainData(firstBelt, lastBelt, belts, totalLength, itemCount) { Segments = segments, Items = items };
    }
}
