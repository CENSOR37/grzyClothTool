using System.Text.Json;
using grzyClothTool.Helpers;
using grzyClothTool.Models.Texture;

namespace grzyClothTool.UnitTests.Models;

public class GTextureEmbeddedTests
{
    [Fact]
    public void ReplacementFilePath_SurvivesSaveLoadAndReportsHasReplacement()
    {
        var texture = new GTextureEmbedded
        {
            HasOriginalTexture = true,
            OriginalName = "jbib_spec",
            ReplacementFilePath = "assets/some-guid.dds"
        };

        // A replacement (even before the pixel data is loaded) must report HasReplacement...
        Assert.True(texture.HasReplacement);

        // ...and the path must round-trip through the save file (this is the persistence bug).
        var json = JsonSerializer.Serialize(texture, SaveHelper.SerializerOptions);
        Assert.Contains("ReplacementFilePath", json);

        var restored = JsonSerializer.Deserialize<GTextureEmbedded>(json, SaveHelper.SerializerOptions);
        Assert.NotNull(restored);
        Assert.Equal("assets/some-guid.dds", restored.ReplacementFilePath);
        Assert.True(restored.HasReplacement);
    }

    [Fact]
    public void HasReplacement_FalseWhenNoReplacementSet()
    {
        var texture = new GTextureEmbedded { HasOriginalTexture = true };
        Assert.False(texture.HasReplacement);
    }

    [Fact]
    public void EnsureReplacementLoaded_ReturnsFalseWhenFileMissing()
    {
        var texture = new GTextureEmbedded
        {
            ReplacementFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dds")
        };

        // Missing replacement file must not throw - just report it couldn't load.
        Assert.False(texture.EnsureReplacementLoaded());
    }


    [Fact]
    public void DefaultConstructor_CreatesMissingTextureDetails()
    {
        var texture = new GTextureEmbedded();

        Assert.False(texture.HasOriginalTexture);
        Assert.Equal(string.Empty, texture.OriginalName);
        Assert.NotNull(texture.Details);
        Assert.True(texture.IsPreviewDisabled);
        Assert.Equal("Encrypted drawable", texture.PreviewDisabledTooltip);
    }

    [Fact]
    public async Task EnsureTextureDataLoadedAsync_ReturnsFalseWhenSourceIsUnavailable()
    {
        var texture = new GTextureEmbedded
        {
            HasOriginalTexture = true,
            SourceDrawablePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ydd")
        };

        Assert.False(await texture.EnsureTextureDataLoadedAsync());
    }

    [Fact]
    public void RenameTexture_IgnoresBlankNamesWithoutDisplayTextureData()
    {
        var texture = new GTextureEmbedded();

        texture.RenameTexture("renamed");
        texture.RenameTexture(" ");

        Assert.Equal(string.Empty, texture.Details.Name);
    }
}
