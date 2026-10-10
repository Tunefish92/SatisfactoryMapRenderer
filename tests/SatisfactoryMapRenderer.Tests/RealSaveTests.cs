using SatisfactoryMapRenderer.Save;
using SatisfactoryMapRenderer.World;

namespace SatisfactoryMapRenderer.Tests;

/// <summary>
/// Checks real saves for full decoding. Set SMR_TEST_SAVES to a ';'-separated list of .sav files;
/// without it the test is skipped, e.g. on CI.
/// </summary>
public sealed class RealSaveTests
{
    private static SaveGame LoadOrSkip(string path)
    {
        Assert.SkipUnless(File.Exists(path), $"Save not available: {path}");
        return SaveGameReader.Load(path);
    }

    private static void AssertFullyDecoded(SaveGame save)
    {
        Assert.Empty(save.Warnings);
        Assert.Equal(0, save.PropertiesFailed);
        Assert.Empty(save.PropertyUnderflows);
        Assert.All(save.AllObjects, o => Assert.Null(o.ParseError));
    }

    public static TheoryData<string> ExtraSaves()
    {
        var data = new TheoryData<string>();
        foreach (var path in (Environment.GetEnvironmentVariable("SMR_TEST_SAVES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            data.Add(path);
        if (data.Count == 0)
            data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(ExtraSaves))]
    public void Extra_saves_decode_fully(string path)
    {
        Assert.SkipWhen(path.Length == 0, "Set SMR_TEST_SAVES to test additional save files.");
        var save = LoadOrSkip(path);
        AssertFullyDecoded(save);
        Assert.NotEmpty(WorldExtractor.Extract(save).Features);
    }
}
