using System.Text.Json;
using Shared.Blocks;
using Shared.Items;

namespace Shared.Content;

/// <summary>Один JSON-файл контента с путём для диагностики ошибок.</summary>
public sealed record ContentJsonFile(string Path, string Json);

/// <summary>Реестры, построенные из одного набора определений.</summary>
public sealed record ContentRegistries(BlockRegistry Blocks, ItemRegistry Items);

/// <summary>Ошибка описания контента. Сообщение всегда содержит путь к исходному файлу.</summary>
public sealed class ContentLoadException : Exception
{
    public ContentLoadException(string message) : base(message) { }
    public ContentLoadException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Загружает JSON-описания блоков и предметов в реестры. Числовые ID назначаются по строковым именам
/// в ordinal-порядке, поэтому не зависят от порядка файлов в каталоге.
/// </summary>
public static class ContentLoader
{
    public static ContentRegistries LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Каталог контента не найден: '{directory}'.");

        var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ContentJsonFile(Path.GetRelativePath(directory, path), File.ReadAllText(path)));

        return Load(files);
    }

    public static ContentRegistries Load(IEnumerable<ContentJsonFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var orderedFiles = files.ToArray();
        if (orderedFiles.Any(file => file is null))
            throw new ArgumentException("Список файлов контента содержит null.", nameof(files));

        var blocks = new List<BlockJson>();
        var items = new List<ItemJson>();
        var blockNames = new HashSet<string>(StringComparer.Ordinal);
        var itemNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in orderedFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new ArgumentException("У файла контента должен быть указан путь.", nameof(files));

            using var document = Parse(file);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Error(file, "корень JSON должен быть объектом.");

            var type = RequiredString(root, "type", file);
            var name = RequiredString(root, "name", file);
            if (!ContentNames.IsValid(name))
                throw Error(file, $"имя '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).");

            switch (type)
            {
                case "block":
                    CheckProperties(root, file, "type", "name", "isOpaque", "isSolid");
                    if (name == BlockRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для воздуха и не должно объявляться в JSON.");
                    if (!blockNames.Add(name))
                        throw Error(file, $"блок '{name}' объявлен более одного раза.");
                    blocks.Add(new BlockJson(file.Path, name,
                        RequiredBoolean(root, "isOpaque", file),
                        OptionalBoolean(root, "isSolid", defaultValue: true, file)));
                    break;

                case "item":
                    CheckProperties(root, file, "type", "name", "maxStackSize", "placesBlock");
                    if (name == ItemRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для пустого предмета и не должно объявляться в JSON.");
                    if (!itemNames.Add(name))
                        throw Error(file, $"предмет '{name}' объявлен более одного раза.");
                    var maxStackSize = RequiredInt32(root, "maxStackSize", file);
                    if (maxStackSize < 1)
                        throw Error(file, $"maxStackSize должен быть положительным числом, получено {maxStackSize}.");
                    var placesBlock = OptionalString(root, "placesBlock", file);
                    if (placesBlock is not null && !ContentNames.IsValid(placesBlock))
                        throw Error(file, $"placesBlock '{placesBlock}' должен иметь вид 'namespace:name'.");
                    items.Add(new ItemJson(file.Path, name, maxStackSize, placesBlock));
                    break;

                default:
                    throw Error(file, $"неизвестный type '{type}'; допустимы 'block' и 'item'.");
            }
        }

        var blockRegistry = new BlockRegistry();
        foreach (var block in blocks.OrderBy(block => block.Name, StringComparer.Ordinal))
            blockRegistry.Register(block.Name, block.IsOpaque, block.IsSolid);

        var itemRegistry = new ItemRegistry();
        foreach (var item in items.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            BlockId? placedBlock = null;
            if (item.PlacesBlock is not null)
            {
                if (!blockRegistry.TryGet(item.PlacesBlock, out var definition))
                    throw new ContentLoadException(
                        $"Файл '{item.Path}': предмет '{item.Name}' ссылается на неизвестный блок '{item.PlacesBlock}'.");
                if (definition.Id.IsAir)
                    throw new ContentLoadException($"Файл '{item.Path}': предмет '{item.Name}' не может ставить воздух.");
                placedBlock = definition.Id;
            }

            itemRegistry.Register(item.Name, item.MaxStackSize, placedBlock);
        }

        blockRegistry.Freeze();
        itemRegistry.Freeze();
        return new ContentRegistries(blockRegistry, itemRegistry);
    }

    private static JsonDocument Parse(ContentJsonFile file)
    {
        try
        {
            return JsonDocument.Parse(file.Json);
        }
        catch (JsonException exception)
        {
            throw new ContentLoadException($"Файл '{file.Path}': некорректный JSON: {exception.Message}", exception);
        }
    }

    private static string RequiredString(JsonElement root, string property, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw Error(file, $"обязательное поле '{property}' должно быть строкой.");
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw Error(file, $"поле '{property}' не может быть пустым.");
        return result;
    }

    private static string? OptionalString(JsonElement root, string property, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw Error(file, $"поле '{property}' должно быть непустой строкой.");
        return value.GetString();
    }

    private static bool RequiredBoolean(JsonElement root, string property, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(file, $"обязательное поле '{property}' должно быть логическим значением.");
        return value.GetBoolean();
    }

    private static bool OptionalBoolean(JsonElement root, string property, bool defaultValue, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value))
            return defaultValue;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(file, $"поле '{property}' должно быть логическим значением.");
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement root, string property, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value) || !value.TryGetInt32(out var result))
            throw Error(file, $"обязательное поле '{property}' должно быть целым числом.");
        return result;
    }

    private static void CheckProperties(JsonElement root, ContentJsonFile file, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw Error(file, $"поле '{property.Name}' указано несколько раз.");
            if (!allowedSet.Contains(property.Name))
                throw Error(file, $"неизвестное поле '{property.Name}'.");
        }
    }

    private static ContentLoadException Error(ContentJsonFile file, string message) =>
        new($"Файл '{file.Path}': {message}");

    private sealed record BlockJson(string Path, string Name, bool IsOpaque, bool IsSolid);
    private sealed record ItemJson(string Path, string Name, int MaxStackSize, string? PlacesBlock);
}
