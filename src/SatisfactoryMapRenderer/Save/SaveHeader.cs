namespace SatisfactoryMapRenderer.Save;

public sealed class SaveHeader
{
    public int HeaderVersion { get; init; }
    public int SaveVersion { get; init; }
    public int BuildVersion { get; init; }
    public string SaveName { get; init; } = "";
    public string MapName { get; init; } = "";
    public string MapOptions { get; init; } = "";
    public string SessionName { get; init; } = "";
    public TimeSpan PlayTime { get; init; }
    public DateTime SaveDateTimeUtc { get; init; }
    public byte SessionVisibility { get; init; }
    public int EditorObjectVersion { get; init; }
    public string ModMetadata { get; init; } = "";
    public bool IsModded { get; init; }
    public string SaveIdentifier { get; init; } = "";
    public bool IsPartitionedWorld { get; init; }
    public byte[]? SaveDataHash { get; init; }
    public bool IsCreativeModeEnabled { get; init; }

    public string GameVersionName => UnrealVersions.GameVersionName(SaveVersion);

    public static SaveHeader Read(BinaryDataReader r)
    {
        int headerVersion = r.ReadInt32();
        int saveVersion = r.ReadInt32();
        int buildVersion = r.ReadInt32();
        string saveName = headerVersion >= (int)SaveHeaderType.AddedSaveName ? r.ReadString() : "";
        string mapName = r.ReadString();
        string mapOptions = r.ReadString();
        string sessionName = r.ReadString();
        int playSeconds = r.ReadInt32();
        long ticks = r.ReadInt64();
        byte visibility = r.ReadByte();

        int editorVersion = headerVersion >= (int)SaveHeaderType.UE425EngineUpdate ? r.ReadInt32() : 0;
        string modMetadata = "";
        bool isModded = false;
        if (headerVersion >= (int)SaveHeaderType.AddedModdingParams)
        {
            modMetadata = r.ReadString();
            isModded = r.ReadInt32() != 0;
        }

        string saveIdentifier = headerVersion >= (int)SaveHeaderType.AddedSaveIdentifier ? r.ReadString() : "";
        bool partitioned = headerVersion >= (int)SaveHeaderType.AddedWorldPartitionSupport && r.ReadInt32() == 1;

        byte[]? hash = null;
        if (headerVersion >= (int)SaveHeaderType.AddedSaveModificationChecksum && r.ReadInt32() == 1)
            hash = r.ReadBytes(16);

        bool creative = headerVersion >= (int)SaveHeaderType.AddedIsCreativeModeEnabled && r.ReadInt32() == 1;

        return new SaveHeader
        {
            HeaderVersion = headerVersion,
            SaveVersion = saveVersion,
            BuildVersion = buildVersion,
            SaveName = saveName,
            MapName = mapName,
            MapOptions = mapOptions,
            SessionName = sessionName,
            PlayTime = TimeSpan.FromSeconds(playSeconds),
            SaveDateTimeUtc = ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : default,
            SessionVisibility = visibility,
            EditorObjectVersion = editorVersion,
            ModMetadata = modMetadata,
            IsModded = isModded,
            SaveIdentifier = saveIdentifier,
            IsPartitionedWorld = partitioned,
            SaveDataHash = hash,
            IsCreativeModeEnabled = creative,
        };
    }
}
