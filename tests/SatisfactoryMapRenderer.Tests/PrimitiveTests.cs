using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.Tests.TestSupport;
using SatisfactoryMapRenderer.World;

namespace SatisfactoryMapRenderer.Tests;

public sealed class BinaryDataReaderTests
{
    [Fact]
    public void Reads_ascii_utf16_and_empty_strings()
    {
        var bytes = SaveWriter.Build(w => w.Str("Persistent_Level").Str("Zürich ✓").Str(""));
        var r = new BinaryDataReader(bytes);

        Assert.Equal("Persistent_Level", r.ReadString());
        Assert.Equal("Zürich ✓", r.ReadString());
        Assert.Equal("", r.ReadString());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void Repeated_strings_are_interned()
    {
        var r = new BinaryDataReader(SaveWriter.Build(w => w.Str("/Game/Same/Path").Str("/Game/Same/Path")));
        Assert.Same(r.ReadString(), r.ReadString());
    }

    [Theory]
    [InlineData(1000)]       // longer than the buffer
    [InlineData(-1000)]      // UTF-16, longer than the buffer
    [InlineData(int.MinValue)]
    public void Invalid_string_length_throws_format_error(int length)
    {
        var r = new BinaryDataReader(SaveWriter.Build(w => w.I32(length).Zeros(8)));
        Assert.Throws<SaveFormatException>(() => r.ReadString());
    }

    [Fact]
    public void Reading_past_the_end_throws_format_error()
    {
        var r = new BinaryDataReader([1, 2, 3]);
        Assert.Throws<SaveFormatException>(() => r.ReadInt32());
    }
}

public sealed class MathTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(-135)]
    [InlineData(179)]
    public void Yaw_of_z_rotation_round_trips(double degrees)
    {
        double half = degrees * Math.PI / 360;
        var q = new Quaternion(0, 0, Math.Sin(half), Math.Cos(half));
        Assert.Equal(degrees * Math.PI / 180, q.Yaw, 9);
    }

    [Fact]
    public void Transform_rotates_scales_and_translates()
    {
        double half = Math.PI / 4; // 90° yaw
        var t = new Transform(new Quaternion(0, 0, Math.Sin(half), Math.Cos(half)), new Vector3(100, 200, 0), new Vector3(2, 2, 2));

        var p = t.TransformPosition(new Vector3(10, 0, 5));

        Assert.Equal(100, p.X, 9);
        Assert.Equal(220, p.Y, 9);  // local +X points to world +Y after 90° yaw
        Assert.Equal(10, p.Z, 9);
    }
}

public sealed class CatalogTests
{
    [Theory]
    [InlineData("Build_ConveyorAttachmentSplitter_C", "Conveyor Attachment Splitter")]
    [InlineData("Build_Foundation_8x4_01_C", "Foundation 8x4 01")]
    [InlineData("BP_WAT1_C", "WAT 1")]
    [InlineData("Desc_IronPlateReinforced_C", "Iron Plate Reinforced")]
    [InlineData("Char_SpaceRabbit_C", "Space Rabbit")]
    public void Prettify_splits_class_names(string input, string expected) =>
        Assert.Equal(expected, Catalog.Prettify(input));

    [Fact]
    public void Recipe_and_resource_names()
    {
        Assert.Equal("Alt: Ingot Steel 1", Catalog.RecipeName("/Game/Recipes/Recipe_Alternate_IngotSteel_1.Recipe_Alternate_IngotSteel_1_C"));
        Assert.Equal("Iron Plate", Catalog.RecipeName("/Game/Recipes/Recipe_IronPlate.Recipe_IronPlate_C"));
        Assert.Equal("Caterium Ore", Catalog.ResourceName("/Game/Resource/Desc_OreGold.Desc_OreGold_C"));
        Assert.Equal("Iron Rod", Catalog.ItemName("/Game/Resource/Desc_IronRod.Desc_IronRod_C"));
    }

    [Fact]
    public void Known_buildings_have_fixed_entries()
    {
        var info = Catalog.GetBuildable("/Game/FactoryGame/Buildable/Factory/ConstructorMk1/Build_ConstructorMk1.Build_ConstructorMk1_C");
        Assert.Equal(new BuildInfo("Constructor", BuildCategory.Production, 800, 1000), info);
    }

    [Fact]
    public void Unknown_structures_are_sized_from_their_names()
    {
        var foundation = Catalog.GetBuildable("/Game/FactoryGame/Buildable/Building/Foundation/Build_Foundation_Asphalt_4x1.Build_Foundation_Asphalt_4x1_C");
        Assert.Equal((BuildCategory.Foundation, 400.0, 400.0), (foundation!.Category, foundation.SizeX, foundation.SizeY));

        // Walls are thin along local X (verified against real saves).
        var wall = Catalog.GetBuildable("/Game/FactoryGame/Buildable/Building/Wall/Build_Wall_Window_8x4_03.Build_Wall_Window_8x4_03_C");
        Assert.Equal((BuildCategory.Wall, 50.0, 800.0), (wall!.Category, wall.SizeX, wall.SizeY));
    }

    [Fact]
    public void Non_buildables_are_not_in_the_catalog()
    {
        Assert.Null(Catalog.GetBuildable("/Game/FactoryGame/Resource/BP_ResourceNode.BP_ResourceNode_C"));
        Assert.Null(Catalog.GetBuildable("/Script/FactoryGame.FGFoliageRemoval"));
    }
}
