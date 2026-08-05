using System.Text.Json;
using grzyClothTool.Helpers;
using MessagePack;
using MessagePack.Resolvers;
using static grzyClothTool.Enums;

namespace grzyClothTool.UnitTests.Helpers;

public class DctProjectImporterTests
{
    [Fact]
    public async Task LoadAsync_ReadsCompressedDctContainer()
    {
        using var temp = new TestTempDirectory();
        var projectPath = temp.FilePath("compressed.dctproj");
        const string json = """
            {
              "SaveVersion": 5,
              "ProjectName": "compressed-test",
              "ClothData": [],
              "DecorationData": [],
              "FacialoverlayData": []
            }
            """;
        var plainMessagePack = MessagePackSerializer.ConvertFromJson(json);
        var payload = MessagePackSerializer.Deserialize<object>(
            plainMessagePack,
            ContractlessStandardResolver.Options);
        var compressed = MessagePackSerializer.Serialize(
            payload,
            ContractlessStandardResolver.Options.WithCompression(MessagePackCompression.Lz4BlockArray));
        File.WriteAllBytes(projectPath, compressed);

        var import = await DctProjectImporter.LoadAsync(projectPath);

        Assert.Equal("compressed-test", import.SuggestedProjectName);
        Assert.Equal(0, import.DrawableCount);
    }

    [Fact]
    public async Task Convert_MapsTypesMetadataOrderingAndAddonSplits()
    {
        using var temp = new TestTempDirectory();
        var projectPath = temp.FilePath("source", "sample.dctproj");
        var dataFolder = temp.FilePath("source", "data");
        Directory.CreateDirectory(dataFolder);

        WriteAsset(dataFolder, "late.ydd");
        WriteAsset(dataFolder, "early.ydd");
        WriteAsset(dataFolder, "prop.ydd");
        WriteAsset(dataFolder, "late-a.ytd");
        WriteAsset(dataFolder, "late-b.ytd");

        var lateId = Guid.NewGuid();
        var lateTextureAId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            SaveVersion = 5,
            ProjectName = "DCT sample",
            DecorationData = new[] { new { Id = "decoration" } },
            FacialoverlayData = Array.Empty<object>(),
            ClothData = new object[]
            {
                new
                {
                    Id = lateId,
                    MainPath = "late.ydd",
                    Name = "Late jacket",
                    Position = 5,
                    TargetGender = 0,
                    ClothType = 0,
                    DrawableType = 11,
                    DrawableSkinType = 1,
                    AudioPreset = 2,
                    IsDummy = false,
                    PedComponentFlags = new { Flag5 = true, HighHeelHeight = 0.35f },
                    PedComponentOptions = new { Flags = 16, HideHairs = true, CutHairs = false },
                    PedPropOptions = new { PropFlags = 0, HideHairs = false, CutHairs = false },
                    ShopData = new { RestrictionTags = new[] { "vip" } },
                    Textures = new object[]
                    {
                        new { Id = Guid.NewGuid(), Position = 1, FilePath = "late-b.ytd", IsDummy = false },
                        new { Id = lateTextureAId, Position = 0, FilePath = "late-a.ytd", IsDummy = false }
                    }
                },
                CreateBasicCloth("early.ydd", position: 2, targetGender: 0, clothType: 0, drawableType: 11),
                CreateBasicCloth("prop.ydd", position: 0, targetGender: 1, clothType: 1, drawableType: 12)
            }
        });

        var import = DctProjectImporter.ParseJson(json, projectPath);
        var prepared = await DctProjectImporter.PrepareAsync(import, temp.FilePath("output"), isExternalProject: true);

        // Avoid background model parsing of the test's placeholder asset bytes.
        Directory.Delete(dataFolder, recursive: true);

        var converted = DctProjectImporter.Convert(prepared, maxDrawablesPerAddon: 1);

        Assert.Equal("DCT sample", import.SuggestedProjectName);
        Assert.Equal(3, import.DrawableCount);
        Assert.Equal(1, import.UnsupportedItemCount);
        Assert.Equal(2, converted.Addons.Count);

        var firstAddon = converted.Addons[0];
        var secondAddon = converted.Addons[1];
        var early = Assert.Single(firstAddon.Drawables, drawable => drawable.IsComponent && drawable.TypeNumeric == 11);
        var late = Assert.Single(secondAddon.Drawables);
        var prop = Assert.Single(firstAddon.Drawables, drawable => drawable.IsProp);

        Assert.Equal("jbib_000_u", early.Name);
        Assert.Equal(lateId, late.Id);
        Assert.Equal("jbib_000_r", late.Name);
        Assert.Equal("Late jacket", late.DisplayName);
        Assert.Equal(SexType.male, late.Sex);
        Assert.True(late.EnableHighHeels);
        Assert.Equal(0.35f, late.HighHeelsValue);
        Assert.True(late.HidesHair);
        Assert.Equal(16, late.Flags);
        Assert.Equal("cloth_upper_bare", late.Audio);
        Assert.Equal(["vip"], late.Tags);
        Assert.Equal(lateTextureAId, late.Textures[0].Id);
        Assert.Equal('a', late.Textures[0].TxtLetter);
        Assert.Equal('b', late.Textures[1].TxtLetter);
        Assert.Equal(["vip"], converted.Tags);

        Assert.True(prop.IsProp);
        Assert.Equal(0, prop.TypeNumeric);
        Assert.Equal(SexType.female, prop.Sex);
        Assert.Equal("p_head_000", prop.Name);
    }

    [Fact]
    public async Task PrepareAsync_SelfContainedProjectCopiesAssetsAndStoresRelativePaths()
    {
        using var temp = new TestTempDirectory();
        var projectPath = temp.FilePath("source", "sample.dctproj");
        var dataFolder = temp.FilePath("source", "data");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllBytes(Path.Combine(dataFolder, "model.ydd"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dataFolder, "texture.ytd"), [4, 5, 6]);

        var json = JsonSerializer.Serialize(new
        {
            ProjectName = "copy-test",
            ClothData = new[]
            {
                new
                {
                    MainPath = "model.ydd",
                    Position = 0,
                    TargetGender = 0,
                    ClothType = 0,
                    DrawableType = 11,
                    Textures = new[]
                    {
                        new { Position = 0, FilePath = "texture.ytd", IsDummy = false }
                    }
                }
            }
        });

        var import = DctProjectImporter.ParseJson(json, projectPath);
        var outputFolder = temp.FilePath("output");
        var prepared = await DctProjectImporter.PrepareAsync(import, outputFolder, isExternalProject: false);

        Assert.Equal("model.ydd", prepared.PersistedPaths["model.ydd"]);
        Assert.Equal("texture.ytd", prepared.PersistedPaths["texture.ytd"]);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(outputFolder, "project_assets", "model.ydd")));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(Path.Combine(outputFolder, "project_assets", "texture.ytd")));
    }

    [Fact]
    public async Task PrepareAsync_RejectsRelativePathsOutsideDataFolder()
    {
        using var temp = new TestTempDirectory();
        var sourceFolder = temp.FilePath("source");
        var dataFolder = Path.Combine(sourceFolder, "data");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllBytes(Path.Combine(sourceFolder, "outside.ydd"), [1]);

        var json = JsonSerializer.Serialize(new
        {
            ProjectName = "unsafe",
            ClothData = new[]
            {
                new
                {
                    MainPath = "..\\outside.ydd",
                    Position = 0,
                    TargetGender = 0,
                    ClothType = 0,
                    DrawableType = 11,
                    Textures = Array.Empty<object>()
                }
            }
        });
        var import = DctProjectImporter.ParseJson(json, Path.Combine(sourceFolder, "sample.dctproj"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DctProjectImporter.PrepareAsync(import, temp.FilePath("output"), isExternalProject: true));

        Assert.Contains("escapes", exception.Message);
    }

    [Fact]
    public async Task ResolveMissingFilesFromFolderAsync_UsesFolderAndUniqueRecursiveMatches()
    {
        using var temp = new TestTempDirectory();
        var sourceFolder = temp.FilePath("source");
        Directory.CreateDirectory(sourceFolder);
        var searchFolder = temp.FilePath("recovered-assets");
        var nestedSearchFolder = Path.Combine(searchFolder, "textures", "stream");
        Directory.CreateDirectory(nestedSearchFolder);
        WriteAsset(searchFolder, "model.ydd");
        WriteAsset(nestedSearchFolder, "texture.ytd");

        var json = JsonSerializer.Serialize(new
        {
            ProjectName = "recovered",
            ClothData = new[]
            {
                new
                {
                    MainPath = "model.ydd",
                    Position = 0,
                    TargetGender = 0,
                    ClothType = 0,
                    DrawableType = 11,
                    Textures = new[]
                    {
                        new { Position = 0, FilePath = "texture.ytd", IsDummy = false }
                    }
                }
            }
        });
        var import = DctProjectImporter.ParseJson(json, Path.Combine(sourceFolder, "sample.dctproj"));

        var missingBeforeSearch = await DctProjectImporter.FindMissingReferencedFilesAsync(import);
        var resolvedCount = await DctProjectImporter.ResolveMissingFilesFromFolderAsync(import, searchFolder);
        var missingAfterSearch = await DctProjectImporter.FindMissingReferencedFilesAsync(import);
        var prepared = await DctProjectImporter.PrepareAsync(
            import,
            temp.FilePath("output"),
            isExternalProject: true);

        Assert.Equal(2, missingBeforeSearch.Count);
        Assert.Equal(2, resolvedCount);
        Assert.Empty(missingAfterSearch);
        Assert.Equal(Path.Combine(searchFolder, "model.ydd"), prepared.PersistedPaths["model.ydd"]);
        Assert.Equal(Path.Combine(nestedSearchFolder, "texture.ytd"), prepared.PersistedPaths["texture.ytd"]);
    }

    private static object CreateBasicCloth(
        string mainPath,
        int position,
        int targetGender,
        int clothType,
        int drawableType)
    {
        return new
        {
            MainPath = mainPath,
            Position = position,
            TargetGender = targetGender,
            ClothType = clothType,
            DrawableType = drawableType,
            IsDummy = false,
            Textures = Array.Empty<object>()
        };
    }

    private static void WriteAsset(string dataFolder, string fileName)
    {
        File.WriteAllBytes(Path.Combine(dataFolder, fileName), [0]);
    }
}
