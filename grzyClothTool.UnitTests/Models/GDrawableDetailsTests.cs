using grzyClothTool.Helpers;
using grzyClothTool.Models.Drawable;

namespace grzyClothTool.UnitTests.Models;

public class GDrawableDetailsTests
{
    [Fact]
    public void Validate_WarnsForMissingModelsTexturesAndEmbeddedTextures()
    {
        var details = new GDrawableDetails();

        details.Validate();

        Assert.True(details.IsWarning);
        Assert.Contains("Missing LOD model", details.Tooltip);
        Assert.Contains("Missing Specular texture", details.Tooltip);
        Assert.Contains("Drawable has no textures", details.Tooltip);
    }

    [Fact]
    public void Validate_ClearsWarningsWhenIgnoreWarningsIsTrue()
    {
        var details = new GDrawableDetails();

        details.Validate(ignoreWarnings: true);

        Assert.False(details.IsWarning);
        Assert.False(details.HasTextureWarnings);
        Assert.False(details.HasEmbeddedTextureWarnings);
        Assert.Equal(string.Empty, details.Tooltip);
    }

    [Fact]
    public void Validate_WarnsWhenHighHeelCheckIsRecommendedButDisabled()
    {
        var details = new GDrawableDetails
        {
            TexturesCount = 1,
            ShouldCheckHighHeels = true
        };
        FillModelsWithinLimits(details);

        details.Validate(enableHighHeels: false);

        Assert.True(details.HasHighHeelsWarning);
        Assert.Contains("High heels", details.Tooltip);
    }

    [Fact]
    public void Validate_DoesNotWarnForCompleteDetailsWithinLimits()
    {
        var details = new GDrawableDetails { TexturesCount = 1 };
        FillModelsWithinLimits(details);
        FillEmbeddedTexturesWithoutWarnings(details);

        details.Validate();

        Assert.False(details.IsWarning);
        Assert.Equal(string.Empty, details.Tooltip);
    }

    [Fact]
    public void Validate_WarnsWhenModelExceedsConfiguredPolygonLimit()
    {
        var details = new GDrawableDetails { TexturesCount = 1 };
        FillModelsWithinLimits(details);
        FillEmbeddedTexturesWithoutWarnings(details);
        details.AllModels[GDrawableDetails.DetailLevel.High] = new GDrawableModel
        {
            PolyCount = SettingsHelper.Instance.PolygonLimitHigh + 1
        };

        details.Validate();

        Assert.True(details.IsWarning);
        Assert.Contains("Polygon count", details.Tooltip);
    }

    [Fact]
    public void RestorePersistedEmbeddedState_CarriesReplacementAndOptimizationOver()
    {
        var persisted = new GDrawableDetails();
        persisted.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Specular] = new grzyClothTool.Models.Texture.GTextureEmbedded
        {
            HasOriginalTexture = true,
            OriginalName = "jbib_spec",
            ReplacementFilePath = "assets/replacement.dds",
            IsOptimizedDuringBuild = true,
            OptimizeDetails = new grzyClothTool.Models.Texture.GTextureDetails { Width = 256, Height = 256 },
            Details = new grzyClothTool.Models.Texture.GTextureDetails { Name = "my_replacement", Width = 1024, Height = 1024 }
        };

        var rebuilt = new GDrawableDetails();
        rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Specular] = new grzyClothTool.Models.Texture.GTextureEmbedded
        {
            HasOriginalTexture = true,
            OriginalName = "jbib_spec",
            Details = new grzyClothTool.Models.Texture.GTextureDetails { Name = "jbib_spec", Width = 2048, Height = 2048 }
        };

        rebuilt.RestorePersistedEmbeddedState(persisted);

        var restored = rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Specular];
        Assert.NotNull(restored);
        Assert.Equal("assets/replacement.dds", restored.ReplacementFilePath);
        Assert.True(restored.HasReplacement);
        Assert.True(restored.IsOptimizedDuringBuild);
        Assert.NotNull(restored.OptimizeDetails);
        Assert.Equal("my_replacement", restored.Details.Name);
        Assert.Equal(1024, restored.Details.Width);
    }

    [Fact]
    public void RestorePersistedEmbeddedState_CarriesRenameOverWithoutReplacement()
    {
        var persisted = new GDrawableDetails();
        persisted.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Normal] = new grzyClothTool.Models.Texture.GTextureEmbedded
        {
            HasOriginalTexture = true,
            OriginalName = "jbib_normal",
            Details = new grzyClothTool.Models.Texture.GTextureDetails { Name = "renamed_normal" }
        };

        var rebuilt = new GDrawableDetails();
        rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Normal] = new grzyClothTool.Models.Texture.GTextureEmbedded
        {
            HasOriginalTexture = true,
            OriginalName = "jbib_normal",
            Details = new grzyClothTool.Models.Texture.GTextureDetails { Name = "jbib_normal", Width = 512, Height = 512 }
        };

        rebuilt.RestorePersistedEmbeddedState(persisted);

        var restored = rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Normal];
        Assert.NotNull(restored);
        Assert.Equal("renamed_normal", restored.Details.Name);
        Assert.Equal(512, restored.Details.Width); // rename must not clobber the real dimensions
        Assert.False(restored.HasReplacement);
    }

    [Fact]
    public void RestorePersistedEmbeddedState_LeavesRebuiltUntouchedWhenNothingPersisted()
    {
        var rebuilt = new GDrawableDetails();
        rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Specular] = new TestEmbeddedTexture();

        rebuilt.RestorePersistedEmbeddedState(null);
        rebuilt.RestorePersistedEmbeddedState(new GDrawableDetails());

        var texture = rebuilt.EmbeddedTextures[GDrawableDetails.EmbeddedTextureType.Specular];
        Assert.NotNull(texture);
        Assert.False(texture.HasReplacement);
        Assert.False(texture.IsOptimizedDuringBuild);
    }

    private static void FillModelsWithinLimits(GDrawableDetails details)
    {
        details.AllModels[GDrawableDetails.DetailLevel.High] = new GDrawableModel { PolyCount = 0 };
        details.AllModels[GDrawableDetails.DetailLevel.Med] = new GDrawableModel { PolyCount = 0 };
        details.AllModels[GDrawableDetails.DetailLevel.Low] = new GDrawableModel { PolyCount = 0 };
    }

    private static void FillEmbeddedTexturesWithoutWarnings(GDrawableDetails details)
    {
        foreach (var textureType in details.EmbeddedTextures.Keys.ToList())
        {
            details.EmbeddedTextures[textureType] = new TestEmbeddedTexture();
        }
    }

    private sealed class TestEmbeddedTexture : grzyClothTool.Models.Texture.GTextureEmbedded
    {
        public TestEmbeddedTexture()
        {
            HasOriginalTexture = true;
            Details = new grzyClothTool.Models.Texture.GTextureDetails
            {
                Width = 512,
                Height = 512,
                MipMapCount = 10,
                Compression = "Dxt1"
            };
        }
    }
}
