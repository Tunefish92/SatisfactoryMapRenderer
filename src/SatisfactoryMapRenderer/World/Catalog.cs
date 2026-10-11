using System.Text;
using System.Text.RegularExpressions;

namespace SatisfactoryMapRenderer.World;

public enum BuildCategory
{
    Production, Extraction, Power, Storage, Logistics, Transport, Special, Decor,
    Foundation, Wall, Walkway,
    Belt, Lift, BeltSupport, Pipe, PipeAttachment, Hypertube, Railway, PowerLine, PowerPole,
    Other,
}

/// <summary>Display information for a buildable. Sizes are in centimetres (Unreal units): X = local X, Y = local Y.</summary>
public sealed record BuildInfo(string Name, BuildCategory Category, double SizeX, double SizeY);

/// <summary>
/// Static knowledge about Satisfactory classes. Footprints are approximate; they only need to look right on a map.
/// Verified against save data: walls are thin along local X, train platforms are 16 m long along local X.
/// </summary>
public static partial class Catalog
{
    private static readonly Dictionary<string, BuildInfo> Buildables = new(StringComparer.Ordinal)
    {
        // Production
        ["Build_ConstructorMk1_C"] = new("Constructor", BuildCategory.Production, 800, 1000),
        ["Build_AssemblerMk1_C"] = new("Assembler", BuildCategory.Production, 1000, 1500),
        ["Build_ManufacturerMk1_C"] = new("Manufacturer", BuildCategory.Production, 1800, 2000),
        ["Build_SmelterMk1_C"] = new("Smelter", BuildCategory.Production, 600, 900),
        ["Build_FoundryMk1_C"] = new("Foundry", BuildCategory.Production, 1000, 900),
        ["Build_OilRefinery_C"] = new("Refinery", BuildCategory.Production, 1000, 2000),
        ["Build_Blender_C"] = new("Blender", BuildCategory.Production, 1800, 1600),
        ["Build_Packager_C"] = new("Packager", BuildCategory.Production, 800, 800),
        ["Build_HadronCollider_C"] = new("Particle Accelerator", BuildCategory.Production, 3800, 2400),
        ["Build_Converter_C"] = new("Converter", BuildCategory.Production, 1600, 1600),
        ["Build_QuantumEncoder_C"] = new("Quantum Encoder", BuildCategory.Production, 2200, 4800),
        ["Build_WorkBench_C"] = new("Crafting Bench", BuildCategory.Production, 600, 400),
        ["Build_WorkBenchIntegrated_C"] = new("HUB Crafting Bench", BuildCategory.Production, 400, 400),
        ["Build_Workshop_C"] = new("Equipment Workshop", BuildCategory.Production, 1000, 700),
        ["Build_TetrominoGame_Computer_C"] = new("Tetromino Computer", BuildCategory.Special, 200, 200),

        // Extraction
        ["Build_MinerMk1_C"] = new("Miner Mk.1", BuildCategory.Extraction, 600, 1400),
        ["Build_MinerMk2_C"] = new("Miner Mk.2", BuildCategory.Extraction, 600, 1400),
        ["Build_MinerMk3_C"] = new("Miner Mk.3", BuildCategory.Extraction, 600, 1400),
        ["Build_OilPump_C"] = new("Oil Extractor", BuildCategory.Extraction, 2000, 2000),
        ["Build_WaterPump_C"] = new("Water Extractor", BuildCategory.Extraction, 2000, 1950),
        ["Build_FrackingSmasher_C"] = new("Resource Well Pressurizer", BuildCategory.Extraction, 1000, 1000),
        ["Build_FrackingExtractor_C"] = new("Resource Well Extractor", BuildCategory.Extraction, 500, 500),

        // Power
        ["Build_GeneratorBiomass_C"] = new("Biomass Burner", BuildCategory.Power, 800, 800),
        ["Build_GeneratorBiomass_Automated_C"] = new("Biomass Burner", BuildCategory.Power, 800, 800),
        ["Build_GeneratorIntegratedBiomass_C"] = new("HUB Biomass Burner", BuildCategory.Power, 400, 400),
        ["Build_GeneratorCoal_C"] = new("Coal-Powered Generator", BuildCategory.Power, 1000, 2600),
        ["Build_GeneratorFuel_C"] = new("Fuel-Powered Generator", BuildCategory.Power, 2000, 2000),
        ["Build_GeneratorNuclear_C"] = new("Nuclear Power Plant", BuildCategory.Power, 3600, 4300),
        ["Build_GeneratorGeoThermal_C"] = new("Geothermal Generator", BuildCategory.Power, 2000, 2000),
        ["Build_AlienPowerBuilding_C"] = new("Alien Power Augmenter", BuildCategory.Power, 2000, 2000),
        ["Build_PowerStorageMk1_C"] = new("Power Storage", BuildCategory.Power, 600, 1200),
        ["Build_PowerSwitch_C"] = new("Power Switch", BuildCategory.PowerPole, 100, 100),
        ["Build_PriorityPowerSwitch_C"] = new("Priority Power Switch", BuildCategory.PowerPole, 100, 100),
        ["Build_PowerPoleMk1_C"] = new("Power Pole Mk.1", BuildCategory.PowerPole, 100, 100),
        ["Build_PowerPoleMk2_C"] = new("Power Pole Mk.2", BuildCategory.PowerPole, 100, 100),
        ["Build_PowerPoleMk3_C"] = new("Power Pole Mk.3", BuildCategory.PowerPole, 100, 100),
        ["Build_PowerTower_C"] = new("Power Tower", BuildCategory.PowerPole, 200, 200),
        ["Build_PowerTowerPlatform_C"] = new("Power Tower Platform", BuildCategory.PowerPole, 200, 200),

        // Storage
        ["Build_StorageContainerMk1_C"] = new("Storage Container", BuildCategory.Storage, 500, 1000),
        ["Build_StorageContainerMk2_C"] = new("Industrial Storage Container", BuildCategory.Storage, 600, 1200),
        ["Build_StoragePlayer_C"] = new("Personal Storage Box", BuildCategory.Storage, 200, 100),
        ["Build_StorageIntegrated_C"] = new("HUB Storage", BuildCategory.Storage, 200, 200),
        ["Build_PipeStorageTank_C"] = new("Fluid Buffer", BuildCategory.Storage, 800, 800),
        ["Build_IndustrialTank_C"] = new("Industrial Fluid Buffer", BuildCategory.Storage, 1200, 1200),
        ["Build_CentralStorage_C"] = new("Dimensional Depot Uploader", BuildCategory.Storage, 500, 500),

        // Logistics
        ["Build_ConveyorAttachmentSplitter_C"] = new("Conveyor Splitter", BuildCategory.Logistics, 400, 400),
        ["Build_ConveyorAttachmentMerger_C"] = new("Conveyor Merger", BuildCategory.Logistics, 400, 400),
        ["Build_ConveyorAttachmentSplitterSmart_C"] = new("Smart Splitter", BuildCategory.Logistics, 400, 400),
        ["Build_ConveyorAttachmentSplitterProgrammable_C"] = new("Programmable Splitter", BuildCategory.Logistics, 400, 400),
        ["Build_ConveyorAttachmentMergerPriority_C"] = new("Priority Merger", BuildCategory.Logistics, 400, 400),
        ["Build_FoundationPassthrough_Lift_C"] = new("Conveyor Floor Hole", BuildCategory.Logistics, 200, 200),
        ["Build_FoundationPassthrough_Pipe_C"] = new("Pipeline Floor Hole", BuildCategory.PipeAttachment, 200, 200),
        ["Build_FoundationPassthrough_Hypertube_C"] = new("Hypertube Floor Hole", BuildCategory.PipeAttachment, 200, 200),

        // Transport
        ["Build_TrainStation_C"] = new("Train Station", BuildCategory.Transport, 1600, 2400),
        ["Build_TrainDockingStation_C"] = new("Freight Platform", BuildCategory.Transport, 1600, 2400),
        ["Build_TrainDockingStationLiquid_C"] = new("Fluid Freight Platform", BuildCategory.Transport, 1600, 2400),
        ["Build_TrainPlatformEmpty_C"] = new("Empty Platform", BuildCategory.Transport, 1600, 2400),
        ["Build_TrainPlatformEmpty_02_C"] = new("Empty Platform (Catwalk)", BuildCategory.Transport, 1600, 2400),
        ["Build_TruckStation_C"] = new("Truck Station", BuildCategory.Transport, 1400, 2000),
        ["Build_DroneStation_C"] = new("Drone Port", BuildCategory.Transport, 1200, 1200),
        ["Build_RailroadBlockSignal_C"] = new("Block Signal", BuildCategory.Transport, 100, 100),
        ["Build_RailroadPathSignal_C"] = new("Path Signal", BuildCategory.Transport, 100, 100),
        ["Build_RailroadSwitchControl_C"] = new("Railroad Switch", BuildCategory.Transport, 100, 100),
        ["Build_RailroadEndStop_C"] = new("Buffer Stop", BuildCategory.Transport, 300, 300),
        ["Build_PipeHyperStart_C"] = new("Hypertube Entrance", BuildCategory.Hypertube, 400, 800),
        ["Build_JumpPad_C"] = new("Jump Pad", BuildCategory.Transport, 300, 300),
        ["Build_JumpPadAdjustable_C"] = new("Jump Pad", BuildCategory.Transport, 300, 300),
        ["Build_JumpPadTilted_C"] = new("Jump Pad", BuildCategory.Transport, 300, 300),
        ["Build_LandingPad_C"] = new("U-Jelly Landing Pad", BuildCategory.Transport, 600, 600),

        // Special
        ["Build_TradingPost_C"] = new("The HUB", BuildCategory.Special, 1000, 1600),
        ["Build_HubTerminal_C"] = new("HUB Terminal", BuildCategory.Special, 200, 200),
        ["Build_SpaceElevator_C"] = new("Space Elevator", BuildCategory.Special, 5400, 5400),
        ["Build_Mam_C"] = new("M.A.M.", BuildCategory.Special, 600, 800),
        ["Build_ResourceSink_C"] = new("AWESOME Sink", BuildCategory.Special, 1300, 1600),
        ["Build_ResourceSinkShop_C"] = new("AWESOME Shop", BuildCategory.Special, 400, 600),
        ["Build_LookoutTower_C"] = new("Lookout Tower", BuildCategory.Other, 400, 400),
        ["Build_RadarTower_C"] = new("Radar Tower", BuildCategory.Special, 800, 800),
        ["Build_Portal_C"] = new("Main Portal", BuildCategory.Special, 1600, 1600),
        ["Build_PortalSatellite_C"] = new("Satellite Portal", BuildCategory.Special, 1200, 1200),
        ["Build_BlueprintDesigner_C"] = new("Blueprint Designer Mk.1", BuildCategory.Special, 3200, 3200),
        ["Build_BlueprintDesigner_Mk2_C"] = new("Blueprint Designer Mk.2", BuildCategory.Special, 4000, 4000),
        ["Build_BlueprintDesigner_Mk3_C"] = new("Blueprint Designer Mk.3", BuildCategory.Special, 4800, 4800),

        // Belts, pipes, rails (drawn from their splines)
        ["Build_ConveyorBeltMk1_C"] = new("Conveyor Belt Mk.1", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorBeltMk2_C"] = new("Conveyor Belt Mk.2", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorBeltMk3_C"] = new("Conveyor Belt Mk.3", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorBeltMk4_C"] = new("Conveyor Belt Mk.4", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorBeltMk5_C"] = new("Conveyor Belt Mk.5", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorBeltMk6_C"] = new("Conveyor Belt Mk.6", BuildCategory.Belt, 0, 0),
        ["Build_ConveyorLiftMk1_C"] = new("Conveyor Lift Mk.1", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorLiftMk2_C"] = new("Conveyor Lift Mk.2", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorLiftMk3_C"] = new("Conveyor Lift Mk.3", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorLiftMk4_C"] = new("Conveyor Lift Mk.4", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorLiftMk5_C"] = new("Conveyor Lift Mk.5", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorLiftMk6_C"] = new("Conveyor Lift Mk.6", BuildCategory.Lift, 200, 200),
        ["Build_ConveyorPole_C"] = new("Conveyor Pole", BuildCategory.BeltSupport, 60, 60),
        ["Build_ConveyorPoleStackable_C"] = new("Stackable Conveyor Pole", BuildCategory.BeltSupport, 60, 60),
        ["Build_ConveyorPoleWall_C"] = new("Conveyor Wall Mount", BuildCategory.BeltSupport, 60, 60),
        ["Build_ConveyorCeilingAttachment_C"] = new("Conveyor Ceiling Mount", BuildCategory.BeltSupport, 60, 60),
        ["Build_Pipeline_C"] = new("Pipeline Mk.1", BuildCategory.Pipe, 0, 0),
        ["Build_Pipeline_NoIndicator_C"] = new("Pipeline Mk.1", BuildCategory.Pipe, 0, 0),
        ["Build_PipelineMK2_C"] = new("Pipeline Mk.2", BuildCategory.Pipe, 0, 0),
        ["Build_PipelineMK2_NoIndicator_C"] = new("Pipeline Mk.2", BuildCategory.Pipe, 0, 0),
        ["Build_PipelineJunction_Cross_C"] = new("Pipeline Junction", BuildCategory.PipeAttachment, 200, 200),
        ["Build_PipelinePump_C"] = new("Pipeline Pump Mk.1", BuildCategory.PipeAttachment, 200, 300),
        ["Build_PipelinePumpMk2_C"] = new("Pipeline Pump Mk.2", BuildCategory.PipeAttachment, 200, 300),
        ["Build_Valve_C"] = new("Valve", BuildCategory.PipeAttachment, 150, 150),
        ["Build_PipelineFlowIndicator_C"] = new("Flow Indicator", BuildCategory.PipeAttachment, 60, 60),
        ["Build_PipelineSupport_C"] = new("Pipeline Support", BuildCategory.PipeAttachment, 60, 60),
        ["Build_PipeSupportStackable_C"] = new("Stackable Pipeline Support", BuildCategory.PipeAttachment, 60, 60),
        ["Build_PipelineSupportWall_C"] = new("Pipeline Wall Support", BuildCategory.PipeAttachment, 60, 60),
        ["Build_PipelineSupportWallHole_C"] = new("Pipeline Wall Hole", BuildCategory.PipeAttachment, 60, 60),
        ["Build_PipeHyper_C"] = new("Hypertube", BuildCategory.Hypertube, 0, 0),
        ["Build_PipeHyperSupport_C"] = new("Hypertube Support", BuildCategory.Hypertube, 80, 80),
        ["Build_HyperPoleStackable_C"] = new("Stackable Hypertube Support", BuildCategory.Hypertube, 80, 80),
        ["Build_RailroadTrack_C"] = new("Railway", BuildCategory.Railway, 0, 0),
        ["Build_RailroadTrackIntegrated_C"] = new("Railway (Station)", BuildCategory.Railway, 0, 0),
        ["Build_PowerLine_C"] = new("Power Line", BuildCategory.PowerLine, 0, 0),
        ["Build_XmassLightsLine_C"] = new("FICSMAS Lights", BuildCategory.PowerLine, 0, 0),
    };

    private static readonly Dictionary<string, string> ResourceNames = new(StringComparer.Ordinal)
    {
        ["Desc_OreIron_C"] = "Iron Ore",
        ["Desc_OreCopper_C"] = "Copper Ore",
        ["Desc_Stone_C"] = "Limestone",
        ["Desc_Coal_C"] = "Coal",
        ["Desc_OreGold_C"] = "Caterium Ore",
        ["Desc_RawQuartz_C"] = "Raw Quartz",
        ["Desc_Sulfur_C"] = "Sulfur",
        ["Desc_OreBauxite_C"] = "Bauxite",
        ["Desc_OreUranium_C"] = "Uranium",
        ["Desc_SAM_C"] = "SAM",
        ["Desc_LiquidOil_C"] = "Crude Oil",
        ["Desc_NitrogenGas_C"] = "Nitrogen Gas",
        ["Desc_Water_C"] = "Water",
        ["Desc_Geyser_C"] = "Geyser",
    };

    public static readonly IReadOnlyDictionary<string, string> ResourceColors = new Dictionary<string, string>
    {
        ["Iron Ore"] = "#b4b9d6",
        ["Copper Ore"] = "#e0833a",
        ["Limestone"] = "#e8dcc0",
        ["Coal"] = "#4a4a4a",
        ["Caterium Ore"] = "#f2c230",
        ["Raw Quartz"] = "#f59fd8",
        ["Sulfur"] = "#d9e021",
        ["Bauxite"] = "#c1573a",
        ["Uranium"] = "#57e35a",
        ["SAM"] = "#a45de3",
        ["Crude Oil"] = "#181818",
        ["Nitrogen Gas"] = "#9fd8ef",
        ["Water"] = "#3a8ee0",
        ["Geyser"] = "#ff6b6b",
        ["Unknown"] = "#9aa0a6",
    };

    private static readonly Dictionary<string, string> CreatureNames = new(StringComparer.Ordinal)
    {
        ["Char_SpaceRabbit_C"] = "Lizard Doggo",
        ["Char_Hog_C"] = "Fluffy-tailed Hog",
        ["Char_AlphaHog_C"] = "Alpha Hog",
        ["Char_CliffHog_C"] = "Cliff Hog",
        ["Char_NuclearHog_C"] = "Nuclear Hog",
        ["Char_Spitter_C"] = "Plasma Spitter",
        ["Char_Spitter_Small_C"] = "Small Plasma Spitter",
        ["Char_Spitter_Alpha_C"] = "Alpha Plasma Spitter",
        ["Char_Spitter_Alternative_C"] = "Plasma Spitter",
        ["Char_Stinger_C"] = "Stinger",
        ["Char_Stinger_Small_C"] = "Small Stinger",
        ["Char_EliteStinger_C"] = "Elite Stinger",
        ["Char_BabyCrab_C"] = "Flying Crab",
        ["Char_CrabHatcher_C"] = "Flying Crab Hatcher",
        ["Char_Hatcher_C"] = "Hatcher",
        ["Char_SpaceGiraffe_C"] = "Space Giraffe-Tick-Penguin-Whale Thing",
        ["Char_NonFlyingBird_C"] = "Non-flying Bird",
        ["Char_Manta_C"] = "Flying Manta",
        // 1.x variants
        ["Char_SpitterForestSmall_C"] = "Plasma Spitter (forest)",
        ["Char_SpitterForestAlpha_C"] = "Alpha Plasma Spitter (forest)",
        ["Char_SpitterForestRedSmall_C"] = "Plasma Spitter (red forest)",
        ["Char_SpitterForestRedAlpha_C"] = "Alpha Plasma Spitter (red forest)",
        ["Char_SpitterDesertSmall_C"] = "Plasma Spitter (desert)",
        ["Char_SpitterDesertAlpha_C"] = "Alpha Plasma Spitter (desert)",
        ["Char_SpitterAquaticSmall_C"] = "Plasma Spitter (aquatic)",
        ["Char_SpitterAquaticAlpha_C"] = "Alpha Plasma Spitter (aquatic)",
        ["Char_Stinger_Child_C"] = "Small Stinger",
        ["Char_BigCrabHatcher_C"] = "Large Flying Crab Hatcher",
        ["Char_Beetle_C"] = "Beetle",
    };

    private static readonly Dictionary<string, string> VehicleNames = new(StringComparer.Ordinal)
    {
        ["BP_Tractor_C"] = "Tractor",
        ["BP_Truck_C"] = "Truck",
        ["BP_Explorer_C"] = "Explorer",
        ["BP_Golfcart_C"] = "Factory Cart",
        ["BP_GolfcartGold_C"] = "Golden Factory Cart",
        ["Testa_BP_WB_C"] = "Cyber Wagon",
        ["BP_Locomotive_C"] = "Electric Locomotive",
        ["BP_FreightWagon_C"] = "Freight Car",
        ["BP_DroneTransport_C"] = "Drone",
    };

    public static bool IsVehicle(string shortClass) => VehicleNames.ContainsKey(shortClass);
    public static string VehicleName(string shortClass) => VehicleNames.GetValueOrDefault(shortClass) ?? Prettify(shortClass);
    public static string CreatureName(string shortClass) => CreatureNames.GetValueOrDefault(shortClass) ?? Prettify(shortClass);

    public static string ResourceName(string descriptorPath)
    {
        string shortName = ShortName(descriptorPath);
        return ResourceNames.GetValueOrDefault(shortName) ?? ItemName(descriptorPath);
    }

    public static string ResourceColor(string resource) => ResourceColors.GetValueOrDefault(resource) ?? ResourceColors["Unknown"];

    /// <summary>"/Game/.../Desc_IronPlateReinforced.Desc_IronPlateReinforced_C" → "Iron Plate Reinforced".</summary>
    public static string ItemName(string path)
    {
        string s = ShortName(path);
        if (ResourceNames.TryGetValue(s, out var known))
            return known;
        return Prettify(s);
    }

    /// <summary>"Recipe_Alternate_IngotSteel_1_C" → "Alt: Ingot Steel 1".</summary>
    public static string RecipeName(string path)
    {
        string s = ShortName(path);
        if (s.Length == 0)
            return "";
        s = s.StartsWith("Recipe_", StringComparison.Ordinal) ? s[7..] : s;
        bool alternate = s.StartsWith("Alternate_", StringComparison.Ordinal);
        if (alternate)
            s = s[10..];
        return (alternate ? "Alt: " : "") + Prettify(s);
    }

    public static string ShortName(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path[(dot + 1)..] : path;
    }

    /// <summary>Approximate heights (cm) of buildings for the 3D view, by a part of the class name. First match wins.</summary>
    private static readonly (string Part, double Height)[] Heights =
    [
        ("SpaceElevator", 11000), ("RadarTower", 11800), ("HadronCollider", 3200), ("QuantumEncoder", 2100),
        ("GeneratorNuclear", 3900), ("GeneratorCoal", 3600), ("GeneratorFuel", 2700), ("GeneratorGeoThermal", 2400),
        ("GeneratorBiomass", 700), ("GeneratorIntegratedBiomass", 300), ("OilRefinery", 3100), ("Blender", 1500),
        ("Converter", 1300), ("Packager", 800), ("ManufacturerMk1", 1000), ("AssemblerMk1", 500), ("ConstructorMk1", 500),
        ("FoundryMk1", 900), ("SmelterMk1", 700), ("MinerMk", 1800), ("OilPump", 2000), ("WaterPump", 2600),
        ("FrackingSmasher", 2300), ("FrackingExtractor", 500), ("IndustrialTank", 1200), ("PipeStorageTank", 600),
        ("StorageContainerMk2", 800), ("StorageContainerMk1", 400), ("CentralStorage", 600), ("StoragePlayer", 200),
        ("ResourceSinkShop", 600), ("ResourceSink", 2400), ("TradingPost", 800), ("Mam", 500), ("Workshop", 300),
        ("BlueprintDesigner", 3300), ("PowerStorage", 1200), ("PowerTower", 3000), ("PowerSwitch", 300),
        ("TrainStation", 1500), ("TrainDocking", 1500), ("TrainPlatform", 200), ("RailroadSwitchControl", 200),
        ("DroneStation", 1200), ("TruckStation", 1200), ("Portal", 1600), ("AlienPowerBuilding", 2200),
        ("JumpPad", 200), ("LandingPad", 100), ("LookoutTower", 2400), ("Ladder", 400), ("XmassTree", 2400),
        ("ConveyorAttachment", 200), ("PipelinePump", 250), ("PipelineJunction", 150), ("Valve", 150),
        ("PipelineSupport", 200), ("PipeHyperSupport", 200), ("ConveyorPole", 200), ("PowerPoleWall", 100), ("PowerPole", 800),
        ("Sign", 200), ("Beam", 100), ("Pillar", 400),
    ];

    /// <summary>
    /// How tall a buildable is and where its bottom lies relative to its position. Foundations, ramps and
    /// walls carry their height in the class name ("8x4" = 4 m); foundations are positioned at their middle.
    /// </summary>
    public static (double Height, double BaseOffset) HeightOf(string className, BuildCategory category)
    {
        if (category is BuildCategory.Foundation or BuildCategory.Wall)
        {
            var size = FootprintSize().Match(className);
            double height = size.Success ? Math.Clamp(int.Parse(size.Groups[2].Value), 1, 8) * 100.0 : category == BuildCategory.Wall ? 400 : 100;
            return category == BuildCategory.Foundation ? (height, -height / 2) : (height, 0);
        }
        foreach (var (part, height) in Heights)
            if (className.Contains(part, StringComparison.Ordinal))
                return (height, 0);
        return (category switch
        {
            BuildCategory.Production => 800, BuildCategory.Extraction => 1500, BuildCategory.Power => 1200,
            BuildCategory.Storage => 400, BuildCategory.Transport => 1000, BuildCategory.Special => 800,
            BuildCategory.Walkway => 50, BuildCategory.Decor => 200, BuildCategory.PowerPole => 800,
            _ => 200,
        }, 0);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"_(\d+)x(\d+)")]
    private static partial System.Text.RegularExpressions.Regex FootprintSize();

    public static BuildInfo? GetBuildable(string classPath)
    {
        string shortName = ShortName(classPath);
        if (Buildables.TryGetValue(shortName, out var info))
            return info;
        if (!classPath.StartsWith("/Game/FactoryGame/Buildable/", StringComparison.Ordinal) || !shortName.StartsWith("Build_", StringComparison.Ordinal))
            return null;
        return Guess(classPath, shortName);
    }

    /// <summary>Heuristics for the hundreds of structural and decorative pieces.</summary>
    private static BuildInfo Guess(string classPath, string shortName)
    {
        string name = Prettify(shortName);
        double size = SizeFromName(shortName) ?? 800;

        if (classPath.Contains("/Building/Wall/") || classPath.Contains("/Doors/") || shortName.Contains("_Wall_") || shortName.Contains("Gate"))
            return new(name, BuildCategory.Wall, 50, size);
        if (classPath.Contains("/Building/Foundation/") && shortName.Contains("Pillar"))
            return new(name, BuildCategory.Foundation, 200, 200);
        if (classPath.Contains("/Building/Foundation/") || classPath.Contains("/Building/Ramp/") || classPath.Contains("/Building/Roof/")
            || classPath.Contains("/Building/Stair") || shortName.Contains("QuarterPipe"))
            return new(name, BuildCategory.Foundation, size, size);
        if (classPath.Contains("/Building/Catwalk/") || classPath.Contains("/Building/Walkway/") || classPath.Contains("/Building/Ladder/"))
            return new(name, BuildCategory.Walkway, 400, 400);
        if (classPath.Contains("/Building/Beam/"))
            return new(name, BuildCategory.Walkway, 400, 50);
        if (classPath.Contains("/Building/Fence/") || classPath.Contains("Railing") || classPath.Contains("/Barrier/"))
            return new(name, BuildCategory.Decor, 50, size);
        if (classPath.Contains("/Building/"))
            return new(name, BuildCategory.Foundation, size, size);
        if (classPath.Contains("Light") || classPath.Contains("/Sign") || classPath.Contains("/Decor") || classPath.Contains("Billboard"))
            return new(name, BuildCategory.Decor, 200, 200);
        if (classPath.Contains("PowerPole") || classPath.Contains("PowerTower"))
            return new(name, BuildCategory.PowerPole, 100, 100);
        if (classPath.Contains("ConveyorPole") || classPath.Contains("ConveyorWall"))
            return new(name, BuildCategory.BeltSupport, 60, 60);
        if (classPath.Contains("Pipe"))
            return new(name, BuildCategory.PipeAttachment, 150, 150);
        return new(name, BuildCategory.Other, 400, 400);
    }

    [GeneratedRegex(@"_(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    private static double? SizeFromName(string shortName)
    {
        var m = SizeRegex().Match(shortName);
        return m.Success && int.TryParse(m.Groups[1].Value, out int meters) && meters is > 0 and <= 64 ? meters * 100 : null;
    }

    /// <summary>"Build_ConveyorAttachmentSplitter_C" → "Conveyor Attachment Splitter".</summary>
    public static string Prettify(string shortName)
    {
        string s = shortName;
        foreach (var prefix in new[] { "Build_", "BP_", "Desc_", "Char_", "Equip_" })
            if (s.StartsWith(prefix, StringComparison.Ordinal))
            {
                s = s[prefix.Length..];
                break;
            }
        if (s.EndsWith("_C", StringComparison.Ordinal))
            s = s[..^2];

        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_')
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                continue;
            }
            if (i > 0 && sb.Length > 0 && sb[^1] != ' ')
            {
                char prev = s[i - 1];
                bool boundary = (char.IsUpper(c) && (char.IsLower(prev) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(prev))))
                                || (char.IsDigit(c) && char.IsLetter(prev) && !(i >= 2 && char.IsDigit(s[i - 2]))); // keep "8x4" together
                if (boundary)
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
