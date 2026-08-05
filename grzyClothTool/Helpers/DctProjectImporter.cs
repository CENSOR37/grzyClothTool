using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using grzyClothTool.Models;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using MessagePack;

namespace grzyClothTool.Helpers;
#nullable enable

internal sealed class DctProjectImport
{
    internal required DctProjectData Project { get; init; }
    internal required string SourcePath { get; init; }

    public string SuggestedProjectName =>
        string.IsNullOrWhiteSpace(Project.ProjectName)
            ? Path.GetFileNameWithoutExtension(SourcePath)
            : Project.ProjectName.Trim();

    public int DrawableCount => Project.ClothData.Count;
    public int UnsupportedItemCount => Project.DecorationData.Count + Project.FacialoverlayData.Count;
}

internal sealed class DctPreparedProject
{
    internal required DctProjectImport Import { get; init; }
    internal required IReadOnlyDictionary<string, string> PersistedPaths { get; init; }
    internal required bool IsExternalProject { get; init; }
}

internal sealed class DctConvertedProject
{
    internal required IReadOnlyList<Addon> Addons { get; init; }
    internal required IReadOnlyList<string> Tags { get; init; }
    public int UnsupportedAlternateModelCount { get; init; }
}

internal static class DctProjectImporter
{
    private const int DctPropTypeOffset = 12;

    private static readonly MessagePackSerializerOptions DctOptions =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<DctProjectImport> LoadAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("A .dctproj path is required.", nameof(sourcePath));
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("The Durty Cloth Tool project does not exist.", fullSourcePath);
        }

        var input = await File.ReadAllBytesAsync(fullSourcePath);
        ValidateDctContainer(input);

        string json;
        try
        {
            json = MessagePackSerializer.ConvertToJson(input, DctOptions);
        }
        catch (MessagePackSerializationException exception)
        {
            throw new InvalidDataException("The selected file is not a supported Durty Cloth Tool project.", exception);
        }

        return ParseJson(json, fullSourcePath);
    }

    internal static DctProjectImport ParseJson(string json, string sourcePath)
    {
        DctProjectData project;
        try
        {
            project = JsonSerializer.Deserialize<DctProjectData>(json, JsonOptions)
                ?? throw new InvalidDataException("The Durty Cloth Tool project contains no project data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Durty Cloth Tool project data could not be read.", exception);
        }

        project.ClothData ??= [];
        project.DecorationData ??= [];
        project.FacialoverlayData ??= [];

        for (var index = 0; index < project.ClothData.Count; index++)
        {
            ValidateClothItem(project.ClothData[index], index);
        }

        return new DctProjectImport
        {
            Project = project,
            SourcePath = Path.GetFullPath(sourcePath)
        };
    }

    public static async Task<DctPreparedProject> PrepareAsync(
        DctProjectImport import,
        string projectFolder,
        bool isExternalProject)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (string.IsNullOrWhiteSpace(projectFolder))
        {
            throw new ArgumentException("A destination project folder is required.", nameof(projectFolder));
        }

        var storedPaths = EnumerateSupportedFilePaths(import.Project)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourcePaths = ResolveSourcePaths(import.SourcePath, storedPaths);
        var persistedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (isExternalProject)
        {
            foreach (var storedPath in storedPaths)
            {
                persistedPaths[storedPath] = sourcePaths[storedPath];
            }
        }
        else
        {
            var assetsFolder = Path.Combine(Path.GetFullPath(projectFolder), Constants.GlobalConstants.ASSETS_FOLDER_NAME);
            Directory.CreateDirectory(assetsFolder);

            var copyItems = BuildCopyItems(storedPaths, sourcePaths, assetsFolder, persistedPaths);
            await Parallel.ForEachAsync(
                copyItems,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
                (item, _) =>
                {
                    File.Copy(item.SourcePath, item.DestinationPath, overwrite: true);
                    return ValueTask.CompletedTask;
                });
        }

        return new DctPreparedProject
        {
            Import = import,
            PersistedPaths = persistedPaths,
            IsExternalProject = isExternalProject
        };
    }

    public static Task ValidateReferencedFilesAsync(DctProjectImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        return Task.Run(() =>
        {
            var storedPaths = EnumerateSupportedFilePaths(import.Project)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            ResolveSourcePaths(import.SourcePath, storedPaths);
        });
    }

    public static DctConvertedProject Convert(DctPreparedProject prepared, int maxDrawablesPerAddon)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (maxDrawablesPerAddon <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDrawablesPerAddon));
        }

        var indexedItems = prepared.Import.Project.ClothData
            .Select((item, index) => new IndexedClothItem(item, index))
            .ToList();
        var groups = indexedItems
            .GroupBy(indexed => GetItemKey(indexed.Item))
            .Select(group => group
                .OrderBy(indexed => indexed.Item.Position)
                .ThenBy(indexed => indexed.SourceIndex)
                .ToList())
            .ToList();

        var addonCount = Math.Max(1, groups.Count == 0
            ? 1
            : groups.Max(group => (group.Count + maxDrawablesPerAddon - 1) / maxDrawablesPerAddon));
        var addons = Enumerable.Range(1, addonCount)
            .Select(index => new Addon($"Addon {index}"))
            .ToList();
        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            for (var groupIndex = 0; groupIndex < group.Count; groupIndex++)
            {
                var addonIndex = groupIndex / maxDrawablesPerAddon;
                var number = groupIndex % maxDrawablesPerAddon;
                var drawable = ConvertDrawable(group[groupIndex].Item, number, prepared, allTags);
                addons[addonIndex].Drawables.Add(drawable);
            }
        }

        var alternateModelCount = prepared.Import.Project.ClothData.Count(item =>
            !string.IsNullOrWhiteSpace(item.AlternationModelFilePathTwo) ||
            !string.IsNullOrWhiteSpace(item.AlternationModelFilePathThree));

        return new DctConvertedProject
        {
            Addons = addons,
            Tags = allTags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
            UnsupportedAlternateModelCount = alternateModelCount
        };
    }

    private static GDrawable ConvertDrawable(
        DctClothData item,
        int number,
        DctPreparedProject prepared,
        HashSet<string> allTags)
    {
        var isProp = item.ClothType == 1;
        var typeNumeric = isProp ? item.DrawableType - DctPropTypeOffset : item.DrawableType;
        var sex = item.TargetGender == 0 ? Enums.SexType.male : Enums.SexType.female;
        var id = Guid.TryParse(item.Id, out var parsedId) ? parsedId : Guid.Empty;
        GDrawable drawable;

        if (item.IsDummy)
        {
            drawable = new GDrawableReserved(sex, isProp, typeNumeric, number)
            {
                Id = id == Guid.Empty ? Guid.NewGuid() : id
            };
        }
        else
        {
            var textures = new ObservableCollection<GTexture>();
            var orderedTextures = item.Textures
                .Select((texture, index) => new IndexedTexture(texture, index))
                .OrderBy(indexed => indexed.Texture.Position)
                .ThenBy(indexed => indexed.SourceIndex)
                .ToList();

            for (var textureIndex = 0; textureIndex < orderedTextures.Count; textureIndex++)
            {
                var sourceTexture = orderedTextures[textureIndex].Texture;
                var textureId = Guid.TryParse(sourceTexture.Id, out var parsedTextureId)
                    ? parsedTextureId
                    : Guid.Empty;
                var texturePath = sourceTexture.IsDummy || string.IsNullOrWhiteSpace(sourceTexture.FilePath)
                    ? Path.Combine(FileHelper.ReservedAssetsPath, "reservedTexture.ytd")
                    : prepared.PersistedPaths[sourceTexture.FilePath];

                textures.Add(new GTexture(
                    textureId,
                    texturePath,
                    typeNumeric,
                    number,
                    textureIndex,
                    item.DrawableSkinType == 1,
                    isProp));
            }

            drawable = new GDrawable(
                id,
                prepared.PersistedPaths[item.MainPath!],
                sex,
                isProp,
                typeNumeric,
                number,
                item.DrawableSkinType == 1,
                textures);
        }

        drawable.DisplayName = item.Name?.Trim() ?? string.Empty;
        drawable.IsNew = false;
        drawable.FirstPersonPath = GetOptionalPersistedPath(item.FirstPersonModelFilePath, prepared.PersistedPaths);
        drawable.ClothPhysicsPath = GetOptionalPersistedPath(item.MeshPhysicsFilePath, prepared.PersistedPaths);
        drawable.HidesHair = (item.PedComponentOptions?.HideHairs ?? false) ||
                             (item.PedComponentOptions?.CutHairs ?? false) ||
                             (item.PedPropOptions?.HideHairs ?? false) ||
                             (item.PedPropOptions?.CutHairs ?? false);

        if (item.PedComponentFlags?.Flag5 == true)
        {
            drawable.EnableHighHeels = true;
            drawable.HighHeelsValue = item.PedComponentFlags.HighHeelHeight;
        }

        var flags = isProp
            ? item.PedPropOptions?.PropFlags ?? 0
            : item.PedComponentOptions?.Flags ?? 0;
        drawable.SelectedFlags = new ObservableCollection<Controls.SelectableItem>(EnumHelper.GetFlags(flags));

        var audioOptions = isProp ? ["none"] : EnumHelper.GetAudioList(typeNumeric);
        drawable.Audio = item.AudioPreset >= 0 && item.AudioPreset < audioOptions.Count
            ? audioOptions[item.AudioPreset]
            : "none";

        foreach (var tag in item.ShopData?.RestrictionTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var trimmedTag = tag.Trim();
            if (!drawable.Tags.Contains(trimmedTag))
            {
                drawable.Tags.Add(trimmedTag);
            }
            allTags.Add(trimmedTag);
        }

        return drawable;
    }

    private static ItemKey GetItemKey(DctClothData item)
    {
        var isProp = item.ClothType == 1;
        return new ItemKey(
            item.TargetGender == 0 ? Enums.SexType.male : Enums.SexType.female,
            isProp,
            isProp ? item.DrawableType - DctPropTypeOffset : item.DrawableType);
    }

    private static string? GetOptionalPersistedPath(
        string? storedPath,
        IReadOnlyDictionary<string, string> persistedPaths)
    {
        return string.IsNullOrWhiteSpace(storedPath) ? null : persistedPaths[storedPath];
    }

    private static IEnumerable<string> EnumerateSupportedFilePaths(DctProjectData project)
    {
        foreach (var item in project.ClothData)
        {
            if (!item.IsDummy && !string.IsNullOrWhiteSpace(item.MainPath))
            {
                yield return item.MainPath;
            }

            foreach (var texture in item.Textures)
            {
                if (!texture.IsDummy && !string.IsNullOrWhiteSpace(texture.FilePath))
                {
                    yield return texture.FilePath;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.FirstPersonModelFilePath))
            {
                yield return item.FirstPersonModelFilePath;
            }

            if (!string.IsNullOrWhiteSpace(item.MeshPhysicsFilePath))
            {
                yield return item.MeshPhysicsFilePath;
            }
        }
    }

    private static Dictionary<string, string> ResolveSourcePaths(
        string projectPath,
        IEnumerable<string> storedPaths)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException("The Durty Cloth Tool project has no parent directory.");
        var dataDirectory = Path.Combine(projectDirectory, "data");
        var relativeBasePath = Directory.Exists(dataDirectory) ? dataDirectory : projectDirectory;
        var fullBasePath = Path.GetFullPath(relativeBasePath);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var storedPath in storedPaths)
        {
            string sourcePath;
            if (Path.IsPathRooted(storedPath))
            {
                sourcePath = Path.GetFullPath(storedPath);
            }
            else
            {
                sourcePath = Path.GetFullPath(Path.Combine(fullBasePath, storedPath));
                var relativePath = Path.GetRelativePath(fullBasePath, sourcePath);
                if (relativePath == ".." ||
                    relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relativePath))
                {
                    throw new InvalidDataException($"Project asset path escapes the Durty Cloth Tool data folder: {storedPath}");
                }
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"A file referenced by the Durty Cloth Tool project could not be found: {storedPath}",
                    sourcePath);
            }

            result[storedPath] = sourcePath;
        }

        return result;
    }

    private static List<CopyItem> BuildCopyItems(
        IReadOnlyList<string> storedPaths,
        IReadOnlyDictionary<string, string> sourcePaths,
        string assetsFolder,
        IDictionary<string, string> persistedPaths)
    {
        var usedFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copyItems = new List<CopyItem>();

        foreach (var storedPath in storedPaths)
        {
            var sourcePath = sourcePaths[storedPath];
            var fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidDataException($"Project asset has an invalid file name: {storedPath}");
            }

            if (usedFileNames.TryGetValue(fileName, out var existingSource) &&
                string.Equals(existingSource, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                persistedPaths[storedPath] = fileName;
                continue;
            }

            if (usedFileNames.ContainsKey(fileName))
            {
                fileName = $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}";
            }

            usedFileNames[fileName] = sourcePath;
            persistedPaths[storedPath] = fileName;
            copyItems.Add(new CopyItem(sourcePath, Path.Combine(assetsFolder, fileName)));
        }

        return copyItems;
    }

    private static void ValidateClothItem(DctClothData item, int index)
    {
        if (item.TargetGender is not (0 or 1))
        {
            throw new InvalidDataException($"Cloth item {index + 1} has an unsupported target gender ({item.TargetGender}).");
        }

        if (item.ClothType is not (0 or 1))
        {
            throw new InvalidDataException($"Cloth item {index + 1} has an unsupported cloth type ({item.ClothType}).");
        }

        var isProp = item.ClothType == 1;
        var typeNumeric = isProp ? item.DrawableType - DctPropTypeOffset : item.DrawableType;
        var typeIsValid = isProp
            ? Enum.IsDefined(typeof(Enums.PropNumbers), typeNumeric)
            : Enum.IsDefined(typeof(Enums.ComponentNumbers), typeNumeric);
        if (!typeIsValid)
        {
            throw new InvalidDataException($"Cloth item {index + 1} has an unsupported drawable type ({item.DrawableType}).");
        }

        if (!item.IsDummy && string.IsNullOrWhiteSpace(item.MainPath))
        {
            throw new InvalidDataException($"Cloth item {index + 1} does not reference a drawable file.");
        }

        item.Textures ??= [];
    }

    private static void ValidateDctContainer(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            throw new InvalidDataException("The selected Durty Cloth Tool project is empty.");
        }

        var reader = new MessagePackReader(input);
        if (reader.NextMessagePackType == MessagePackType.Array)
        {
            var itemCount = reader.ReadArrayHeader();
            if (itemCount < 2 || reader.NextMessagePackType != MessagePackType.Extension)
            {
                throw new InvalidDataException("The selected file has an invalid Durty Cloth Tool container.");
            }

            var header = reader.ReadExtensionFormatHeader();
            if (header.TypeCode != 98)
            {
                throw new InvalidDataException("The selected file is not a supported Durty Cloth Tool project.");
            }

            reader.ReadRaw(header.Length);
            for (var index = 1; index < itemCount; index++)
            {
                if (reader.NextMessagePackType != MessagePackType.Binary || reader.ReadBytes() is null)
                {
                    throw new InvalidDataException("The selected file has an invalid Durty Cloth Tool data block.");
                }
            }
        }
        else if (reader.NextMessagePackType == MessagePackType.Extension)
        {
            var header = reader.ReadExtensionFormatHeader();
            if (header.TypeCode != 99)
            {
                throw new InvalidDataException("The selected file is not a supported Durty Cloth Tool project.");
            }
            reader.ReadRaw(header.Length);
        }
        else
        {
            throw new InvalidDataException("The selected file is not a supported Durty Cloth Tool project.");
        }

        if (!reader.End)
        {
            throw new InvalidDataException("Unexpected data follows the Durty Cloth Tool project.");
        }
    }

    private sealed record IndexedClothItem(DctClothData Item, int SourceIndex);
    private sealed record IndexedTexture(DctTextureData Texture, int SourceIndex);
    private sealed record ItemKey(Enums.SexType Sex, bool IsProp, int TypeNumeric);
    private sealed record CopyItem(string SourcePath, string DestinationPath);
}

internal sealed class DctProjectData
{
    public int SaveVersion { get; set; }
    public string? ProjectName { get; set; }
    public string? DlcName { get; set; }
    public List<DctClothData> ClothData { get; set; } = [];
    public List<JsonElement> DecorationData { get; set; } = [];
    public List<JsonElement> FacialoverlayData { get; set; } = [];
}

internal sealed class DctClothData
{
    public List<DctTextureData> Textures { get; set; } = [];
    public DctPedComponentFlags? PedComponentFlags { get; set; }
    public DctPedComponentOptions? PedComponentOptions { get; set; }
    public DctPedPropOptions? PedPropOptions { get; set; }
    public DctShopData? ShopData { get; set; }
    public bool IsDummy { get; set; }
    public int ClothType { get; set; }
    public int DrawableSkinType { get; set; }
    public string? FirstPersonModelFilePath { get; set; }
    public string? AlternationModelFilePathTwo { get; set; }
    public string? AlternationModelFilePathThree { get; set; }
    public string? MeshPhysicsFilePath { get; set; }
    public int DrawableType { get; set; }
    public int AudioPreset { get; set; }
    public string? Id { get; set; }
    public int TargetGender { get; set; }
    public string? MainPath { get; set; }
    public string? Name { get; set; }
    public int Position { get; set; }
}

internal sealed class DctTextureData
{
    public int Position { get; set; }
    public string? Id { get; set; }
    public string? FilePath { get; set; }
    public bool IsDummy { get; set; }
}

internal sealed class DctPedComponentFlags
{
    public bool Flag5 { get; set; }
    public float HighHeelHeight { get; set; } = 1.0f;
}

internal sealed class DctPedComponentOptions
{
    public int Flags { get; set; }
    public bool CutHairs { get; set; }
    public bool HideHairs { get; set; }
}

internal sealed class DctPedPropOptions
{
    public int PropFlags { get; set; }
    public bool CutHairs { get; set; }
    public bool HideHairs { get; set; }
}

internal sealed class DctShopData
{
    public List<string> RestrictionTags { get; set; } = [];
}
