namespace SatisfactoryMapRenderer.Save;

/// <summary>FGSaveManagerInterface / SaveCustomVersion.h – the "save version" field of the header.</summary>
public enum SaveCustomVersion
{
    SaveFileIsCompressed = 21,
    TrainBlueprintClassAdded = 28,
    AddedSublevelStreaming = 29,                     // U6: body is split into levels
    NativizedShoppingList = 36,
    UnrealEngine5 = 37,                              // U8: 64-bit blob sizes
    IntroducedWorldPartition = 38,                   // grids + per-object save version
    DroneActionRefactor = 39,
    MultipleWireMeshRefactor = 40,
    SwitchTo64BitSaveArchive = 41,                   // vectors in structs become doubles
    ResetBrokenBlueprintSplines = 42,
    RefactoredInventoryItemState = 43,
    AddedPaintFinishes = 44,
    NormalizeChainSplineArriveAndLeave = 45,
    Version1 = 46,                                   // 1.0 release
    PoleRefactor = 47,
    LightweightBuildableSubsystemWritesRuntimeVersion = 48,
    SerializeObjectFlags = 49,
    BackToBackRailroadSwitches = 50,
    SerializePerStreamableLevelTOCVersion = 51,      // 1.1
    RailroadTrackConnectionCleanup = 52,
    SerializeDataPackageVersionAndCustomVersions = 53,
    RailroadSubsystemThirdRailConnectionsCleanupRedone = 55,
    UnlockAvailableItemDescriptorsForPickedUpItems = 56,
    NewPlayerInfoHandleSerializationFormat = 57,
    FixNewPlayerInfoHandleSerializationFormat = 58,  // 1.2
    FixedUpInvalidPatternRotationsBlueprintSupport = 59,
    FixedMissingFICSITMaterials = 60,                // 1.2.4 and 1.2.4.1; 59 and 60 only migrate data on load
}

/// <summary>The "save header version" – controls which header fields exist.</summary>
public enum SaveHeaderType
{
    UE425EngineUpdate = 7,
    AddedModdingParams = 8,
    UE426EngineUpdate = 9,
    AddedSaveIdentifier = 10,
    AddedWorldPartitionSupport = 11,
    AddedSaveModificationChecksum = 12,
    AddedIsCreativeModeEnabled = 13,
    AddedSaveName = 14,
}

public static class UnrealVersions
{
    /// <summary>EUnrealEngineObjectUE5Version.PROPERTY_TAG_COMPLETE_TYPE_NAME (UE 5.4+ property tags).</summary>
    public const int PropertyTagCompleteTypeName = 1012;

    /// <summary>Oldest and newest save version the parser was written and tested for.</summary>
    public const int OldestSupportedSave = (int)SaveCustomVersion.SaveFileIsCompressed;
    public const int NewestSupportedSave = (int)SaveCustomVersion.FixedMissingFICSITMaterials;

    /// <summary>The newest game version whose saves were tested: 1.2.4.1, build 513885.</summary>
    public const string NewestTestedGame = "1.2.4.1";

    public static string GameVersionName(int saveVersion) => saveVersion switch
    {
        > NewestSupportedSave => "newer than 1.2",
        >= 57 => "1.2",
        >= 51 => "1.1",
        >= 46 => "1.0",
        >= 37 => "Update 8",
        >= 29 => "Update 6/7",
        >= 21 => "Update 5 or older",
        _ => "Early Access (uncompressed)",
    };
}
