using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Save.Properties;
using SatisfactoryMapRenderer.Tests.TestSupport;

namespace SatisfactoryMapRenderer.Tests;

/// <summary>Round-trips synthetic saves of every format generation through the reader.</summary>
public sealed class SaveFormatTests
{
    /// <summary>Header version / save version pairs covering every layout change the reader handles.</summary>
    public static TheoryData<int, int> Versions => new()
    {
        { 9, 28 },   // Update 5: single object list
        { 10, 29 },  // Update 6: levels
        { 10, 36 },  // Update 7
        { 11, 38 },  // Update 8: UE5, 64-bit blobs, grids, per-object versions
        { 13, 42 },  // Update 8: double vectors
        { 13, 46 },  // 1.0: destroyed actors map, lightweight buildables
        { 13, 49 },  // 1.0: object flags, lightweight runtime version
        { 14, 51 },  // 1.1: per-level versions, save name
        { 14, 58 },  // 1.2: package versions, UE 5.4+ property tags, new player info handle
        { 14, 60 },  // 1.2.4: newest supported
    };

    private static SaveGame Load(SyntheticSave synthetic)
    {
        string path = synthetic.WriteToTempFile();
        try
        {
            return SaveGameReader.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Reads_header_and_all_objects_without_errors(int headerVersion, int saveVersion)
    {
        var synthetic = TestScene.Create(headerVersion, saveVersion);
        var save = Load(synthetic);

        Assert.Equal(headerVersion, save.Header.HeaderVersion);
        Assert.Equal(saveVersion, save.Header.SaveVersion);
        Assert.Equal("Synthetic Session", save.Header.SessionName);
        Assert.Equal("Persistent_Level", save.Header.MapName);
        Assert.Equal(TimeSpan.FromMinutes(3 * 60 + 25), save.Header.PlayTime);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), save.Header.SaveDateTimeUtc);
        Assert.Equal(headerVersion >= 14 ? "Synthetic Save" : "", save.Header.SaveName);

        Assert.Equal(synthetic.IsLegacy ? 1 : 2, save.Levels.Count);
        Assert.Equal(synthetic.Persistent.Count + synthetic.Sublevel.Count, save.AllObjects.Count());
        Assert.Empty(save.Warnings);
        Assert.Equal(0, save.PropertiesFailed);
        Assert.Empty(save.PropertyUnderflows);
        Assert.All(save.AllObjects, o => Assert.Null(o.ParseError));
        Assert.All(save.AllObjects, o => Assert.Null(o.TrailingData));
        Assert.Equal(saveVersion >= 38 ? 1 : 0, save.Grids.Count);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Decodes_transforms_and_properties(int headerVersion, int saveVersion)
    {
        var save = Load(TestScene.Create(headerVersion, saveVersion));
        var objects = save.AllObjects.ToDictionary(o => o.InstanceName);

        var constructor = Assert.IsType<SaveEntity>(objects[TestScene.Constructor]);
        Assert.Equal(new Vector3(10000, 20000, 100), constructor.Transform.Translation);
        Assert.Equal(Math.PI / 2, constructor.Transform.Rotation.Yaw, 4);
        Assert.Equal(TestScene.IronPlateRecipe, constructor.Properties.GetReference("mCurrentRecipe")?.PathName);
        Assert.Equal(1.5, constructor.Properties.GetNumber("mCurrentPotential"));
        Assert.True(constructor.Properties.GetBool("mIsProducing"));

        var spline = objects[TestScene.Belt].Properties.GetArray("mSplineData");
        Assert.NotNull(spline);
        Assert.Equal(2, spline.Items.Count);
        var second = Assert.IsType<StructValue>(spline.Items[1]);
        Assert.Equal("SplinePointData", second.StructType);
        var location = Assert.IsType<StructValue>(((PropertyList)second.Data!).Get("Location"));
        Assert.Equal(new Vector3(1000, 0, 0), location.Data);

        var inventory = Assert.IsType<SaveComponent>(objects[TestScene.MinerInventory]);
        Assert.Equal(TestScene.Miner, inventory.ParentEntityName);
        Assert.Equal(TestScene.MinerInventory, Assert.IsType<SaveEntity>(objects[TestScene.Miner]).Components.Single().PathName);

        var removal = Assert.IsType<SetValue>(objects[TestScene.Level + "FGFoliageRemoval_11"].Properties.Get("mRemovalLocations"));
        Assert.Equal([new Vector3(1000, 2000, 0), new Vector3(3000, 4000, 0)], removal.Items.Cast<Vector3>());

        var state = objects[TestScene.Level + "BP_PlayerState_C_9"];
        Assert.Equal("Pioneer Ünicode", state.Properties.GetText("mCachedPlayerName"));
        Assert.Equal("Central Station", objects[TestScene.Level + "FGTrainStationIdentifier_10"].Properties.GetText("mStationName"));
        Assert.Equal("EGP_MidGame", objects[TestScene.Level + "GamePhaseManager"].Properties.GetText("mGamePhase"));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void Decodes_special_data_and_collectables(int headerVersion, int saveVersion)
    {
        var save = Load(TestScene.Create(headerVersion, saveVersion));
        var objects = save.AllObjects.ToDictionary(o => o.InstanceName);

        var wire = Assert.IsType<WireConnection>(objects[TestScene.Wire].SpecialData);
        Assert.Equal(TestScene.PoleA + ".PowerConnection", wire.Source.PathName);
        Assert.Equal(TestScene.PoleB + ".PowerConnection", wire.Target.PathName);

        var collected = save.Levels.SelectMany(l => l.Collectables.Concat(l.DestroyedActors.Values.SelectMany(v => v)));
        Assert.Contains(collected, r => r.PathName == TestScene.CollectedCrystal);

        if (saveVersion >= 46)
        {
            var lightweight = Assert.IsType<LightweightBuildables>(objects[TestScene.Level + "LightweightBuildableSubsystem"].SpecialData);
            var group = Assert.Single(lightweight.Groups);
            Assert.EndsWith("Build_Foundation_8x1_01_C", group.ClassName);
            Assert.Equal(2, group.Instances.Count);
            Assert.Equal(new Vector3(50800, 50000, 0), group.Instances[1].Transform.Translation);
            Assert.Equal(Math.PI / 2, group.Instances[1].Transform.Rotation.Yaw, 6);
            Assert.EndsWith("Recipe_Foundation_8x1_01_C", group.Instances[0].Recipe);
        }
    }

    [Fact]
    public void Save_spanning_several_compressed_chunks_is_reassembled()
    {
        var synthetic = TestScene.Create(14, 58);
        // ~400 KB of fog data forces at least four 128 KB chunks.
        var big = Enumerable.Range(0, 320 * 320 * 4).Select(i => (byte)(i % 251)).ToArray();
        synthetic.Persistent.Add(new TestObject
        {
            Class = "/Script/FactoryGame.FGSomething",
            Instance = TestScene.Level + "Big",
            Properties = [new PByteArray("mBlob", big)],
        });

        var save = Load(synthetic);

        Assert.True(save.ChunkCount >= 4, $"expected several chunks, got {save.ChunkCount}");
        var blob = save.AllObjects.Single(o => o.InstanceName.EndsWith("Big")).Properties.GetArray("mBlob");
        Assert.Equal(big, blob!.Items.Cast<byte>());
    }

    [Fact]
    public void Rejects_uncompressed_early_access_saves()
    {
        var bytes = SaveWriter.Build(w => w.I32(6).I32(18).I32(1000).Str("Persistent_Level").Str("").Str("x").I32(0).I64(0).Byte(0));
        string path = Path.Combine(Path.GetTempPath(), $"smr-test-{Guid.NewGuid():N}.sav");
        File.WriteAllBytes(path, bytes);
        try
        {
            var ex = Assert.Throws<SaveFormatException>(() => SaveGameReader.Load(path));
            Assert.Contains("too old", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_compressed_data_fails_with_a_format_error()
    {
        byte[] file = TestScene.Create(14, 58).Build();
        int tag = file.AsSpan().IndexOf(new byte[] { 0xC1, 0x83, 0x2A, 0x9E });
        file.AsSpan(tag + 49 + 2, 32).Fill(0xFF); // garbage right after the zlib header
        string path = Path.Combine(Path.GetTempPath(), $"smr-test-{Guid.NewGuid():N}.sav");
        File.WriteAllBytes(path, file);
        try
        {
            Assert.Throws<SaveFormatException>(() => SaveGameReader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Truncated_file_fails_with_a_format_error()
    {
        byte[] full = TestScene.Create(14, 58).Build();
        string path = Path.Combine(Path.GetTempPath(), $"smr-test-{Guid.NewGuid():N}.sav");
        File.WriteAllBytes(path, full[..(full.Length / 2)]);
        try
        {
            // The CLI reports SaveFormatException as a readable error instead of a crash.
            Assert.Throws<SaveFormatException>(() => SaveGameReader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
