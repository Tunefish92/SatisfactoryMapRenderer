namespace SatisfactoryMapRenderer.Tests.TestSupport;

/// <summary>A small factory with one object of every kind the map cares about.</summary>
public static class TestScene
{
    public const string Level = "Persistent_Level:PersistentLevel.";
    public const string Constructor = Level + "Build_ConstructorMk1_C_1";
    public const string Belt = Level + "Build_ConveyorBeltMk3_C_2";
    public const string Node = Level + "BP_ResourceNode7";
    public const string Miner = Level + "Build_MinerMk1_C_3";
    public const string MinerInventory = Miner + ".OutputInventory";
    public const string PoleA = Level + "Build_PowerPoleMk1_C_4";
    public const string PoleB = Level + "Build_PowerPoleMk1_C_5";
    public const string Wire = Level + "Build_PowerLine_C_6";
    public const string Player = Level + "Char_Player_C_7";
    public const string Station = Level + "Build_TrainStation_C_8";
    public const string Crystal = "Persistent_Exploration:PersistentLevel.BP_Crystal5";
    public const string CollectedCrystal = "Persistent_Exploration:PersistentLevel.BP_Crystal9";
    public const string IronPlateRecipe = "/Game/FactoryGame/Recipes/Recipe_IronPlate.Recipe_IronPlate_C";

    public static SyntheticSave Create(int headerVersion, int saveVersion)
    {
        var save = new SyntheticSave(headerVersion, saveVersion);
        var p = save.Persistent;

        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Buildable/Factory/ConstructorMk1/Build_ConstructorMk1.Build_ConstructorMk1_C",
            Instance = Constructor,
            X = 10000, Y = 20000, Z = 100, YawDegrees = 90,
            Properties =
            [
                new PObj("mCurrentRecipe", IronPlateRecipe),
                new PFloat("mCurrentPotential", 1.5f),
                new PBool("mIsProducing", true),
            ],
        });

        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Buildable/Factory/ConveyorBeltMk3/Build_ConveyorBeltMk3.Build_ConveyorBeltMk3_C",
            Instance = Belt,
            X = 5000, Y = 5000,
            Properties =
            [
                new PStructArray("mSplineData", "SplinePointData",
                    [new PVector("Location", 0, 0, 0), new PVector("ArriveTangent", 1000, 0, 0), new PVector("LeaveTangent", 1000, 0, 0)],
                    [new PVector("Location", 1000, 0, 0), new PVector("ArriveTangent", 1000, 0, 0), new PVector("LeaveTangent", 1000, 0, 0)]),
            ],
        });

        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Resource/BP_ResourceNode.BP_ResourceNode_C",
            Instance = Node,
            X = 30000, Y = 30000,
            Properties = [new PInt("mResourcesLeft", -1)],
        });
        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Buildable/Factory/MinerMK1/Build_MinerMk1.Build_MinerMk1_C",
            Instance = Miner,
            X = 30000, Y = 30000,
            Components = [MinerInventory],
            Properties = [new PObj("mExtractableResource", Node), new PObj("mOutputInventory", MinerInventory)],
        });
        p.Add(new TestObject
        {
            Class = "/Script/FactoryGame.FGInventoryComponent",
            Instance = MinerInventory,
            IsActor = false,
            ParentActor = Miner,
            Properties = [new PObjArray("mAllowedItemDescriptors", "/Game/FactoryGame/Resource/RawResources/OreIron/Desc_OreIron.Desc_OreIron_C")],
        });

        const string poleClass = "/Game/FactoryGame/Buildable/Factory/PowerPoleMk1/Build_PowerPoleMk1.Build_PowerPoleMk1_C";
        p.Add(new TestObject { Class = poleClass, Instance = PoleA, X = 0, Y = 10000 });
        p.Add(new TestObject { Class = poleClass, Instance = PoleB, X = 10000, Y = 10000 });
        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Buildable/Factory/PowerLine/Build_PowerLine.Build_PowerLine_C",
            Instance = Wire,
            Special = _ => SyntheticSave.Wire(PoleA + ".PowerConnection", PoleB + ".PowerConnection"),
        });

        p.Add(new TestObject { Class = "/Game/FactoryGame/Character/Player/Char_Player.Char_Player_C", Instance = Player, X = 100, Y = 200, Z = 300 });
        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Character/Player/BP_PlayerState.BP_PlayerState_C",
            Instance = Level + "BP_PlayerState_C_9",
            Properties = [new PObj("mOwnedPawn", Player), new PStr("mCachedPlayerName", "Pioneer Ünicode")],
        });

        p.Add(new TestObject { Class = "/Game/FactoryGame/Buildable/Factory/Train/Station/Build_TrainStation.Build_TrainStation_C", Instance = Station, X = 40000, Y = 0 });
        p.Add(new TestObject
        {
            Class = "/Script/FactoryGame.FGTrainStationIdentifier",
            Instance = Level + "FGTrainStationIdentifier_10",
            Properties = [new PObj("mStation", Station), new PText("mStationName", "Central Station")],
        });

        p.Add(new TestObject
        {
            Class = "/Script/FactoryGame.FGFoliageRemoval",
            Instance = Level + "FGFoliageRemoval_11",
            Properties =
            [
                new PVectorSet("mRemovalLocations", (1000, 2000, 0), (3000, 4000, 0)),
                new PStr("mFoliageTypeName", "Tree_Jungle_01_FoliageType"),
            ],
        });

        var fog = new byte[4 * 4 * 4];
        fog[2] = 255;
        p.Add(new TestObject
        {
            Class = "/Script/FactoryGame.FGMapManager",
            Instance = Level + "MapManager",
            Properties = [new PByteArray("mFogOfWarRawData", fog)],
        });
        p.Add(new TestObject
        {
            Class = "/Game/FactoryGame/Schematics/Progression/BP_GamePhaseManager.BP_GamePhaseManager_C",
            Instance = Level + "GamePhaseManager",
            Properties = [new PEnum("mGamePhase", "EGamePhase", "EGP_MidGame")],
        });

        if (saveVersion >= 46)
        {
            p.Add(new TestObject
            {
                Class = "/Script/FactoryGame.FGLightweightBuildableSubsystem",
                Instance = Level + "LightweightBuildableSubsystem",
                Special = s => s.LightweightBuildables(
                    "/Game/FactoryGame/Buildable/Building/Foundation/Build_Foundation_8x1_01.Build_Foundation_8x1_01_C",
                    (50000, 50000, 0), (50800, 50000, 90)),
            });
        }

        save.Sublevel.Add(new TestObject { Class = "/Game/FactoryGame/Resource/Environment/Crystal/BP_Crystal.BP_Crystal_C", Instance = Crystal, X = 1000, Y = 1000 });
        save.Collected.Add(CollectedCrystal);
        return save;
    }
}
