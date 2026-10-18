using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shared.Blocks;
using Shared.Items;

namespace Shared.Content;

/// <summary>Один JSON-файл контента.</summary>
/// <param name="Path">
/// Путь относительно корня контента: <c>&lt;пакет&gt;/…/файл.json</c> (разделитель <c>/</c> или <c>\</c>).
/// Первая папка — пакет; она же пространство имён его определений. Путь попадает в сообщения об ошибках.
/// </param>
public sealed record ContentJsonFile(string Path, string Json);

/// <summary>Реестры, построенные из одного набора определений.</summary>
/// <param name="BlockTextures">
/// Текстуры граней по имени блока. Логике мира они не нужны (реестр блоков их не хранит), их берёт только клиент.
/// Блока без поля <c>textures</c> здесь нет.
/// </param>
public sealed record ContentRegistries(BlockRegistry Blocks, ItemRegistry Items,
    ImmutableDictionary<string, BlockTextures> BlockTextures);

/// <summary>Имена текстур граней блока: файлы <c>Textures/Blocks/{имя}.png</c> клиента.</summary>
public sealed record BlockTextures(string Top, string Side, string Bottom);

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
/// <remarks>
/// Контент разбит на пакеты: папка верхнего уровня в корне контента — пакет, её имя — пространство имён,
/// и пакет объявляет только имена <c>&lt;пакет&gt;:…</c> (ссылаться, например через <c>placesBlock</c>, может на любые).
/// Так два пакета не могут занять одно имя, а по имени сразу видно, какой пакет его объявил.
/// </remarks>
public static partial class ContentLoader
{
    /// <summary>Имя текстуры — имя файла без расширения: только a–z, 0–9 и _, чтобы из JSON нельзя было указать путь.</summary>
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex TextureNamePattern();

    public static ContentRegistries LoadDirectory(string directory) => LoadDirectories([directory]);

    /// <summary>
    /// Загружает пакеты из нескольких корней контента (игра, позже — папка модов) в одни реестры.
    /// Пакет с одним именем в двух корнях — ошибка: иначе неясно, какой из них настоящий.
    /// </summary>
    public static ContentRegistries LoadDirectories(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var packRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<ContentJsonFile>();
        foreach (var root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Каталог контента не найден: '{root}'.");

            if (Directory.GetFiles(root, "*.json").FirstOrDefault() is { } loose)
                throw new ContentLoadException($"Файл '{loose}': контент должен лежать в папке пакета, а не в корне '{root}'.");

            foreach (var packDirectory in Directory.GetDirectories(root))
            {
                var pack = Path.GetFileName(packDirectory);
                if (!packRoots.TryAdd(pack, root))
                    throw new ContentLoadException($"Пакет '{pack}' найден дважды: в '{packRoots[pack]}' и в '{root}'.");

                files.AddRange(Directory.GetFiles(packDirectory, "*.json", SearchOption.AllDirectories)
                    .Select(path => new ContentJsonFile(
                        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path))));
            }
        }

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

            var pack = PackOf(file);
            using var document = Parse(file);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Error(file, "корень JSON должен быть объектом.");
            // До чтения полей: иначе при повторе 'type' читается последнее значение и ошибка указывает не туда.
            CheckNoDuplicates(root, file);

            var type = RequiredString(root, "type", file);
            var name = RequiredString(root, "name", file);
            if (!ContentNames.IsValid(name))
                throw Error(file, $"имя '{name}' должно иметь вид 'namespace:name' (a–z, 0–9, _).");
            if (name[..name.IndexOf(':')] != pack)
                throw Error(file, $"имя '{name}' не из пространства имён пакета '{pack}'; пакет объявляет только '{pack}:…'.");

            switch (type)
            {
                case "block":
                    CheckProperties(root, file, "type", "name", "isOpaque", "isSolid", "textures");
                    if (name == BlockRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для воздуха и не должно объявляться в JSON.");
                    if (!blockNames.Add(name))
                        throw Error(file, $"блок '{name}' объявлен более одного раза.");
                    blocks.Add(new BlockJson(file.Path, name,
                        RequiredBoolean(root, "isOpaque", file),
                        OptionalBoolean(root, "isSolid", defaultValue: true, file),
                        OptionalTextures(root, file)));
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
        var textures = ImmutableDictionary.CreateBuilder<string, BlockTextures>(StringComparer.Ordinal);
        foreach (var block in blocks.OrderBy(block => block.Name, StringComparer.Ordinal))
        {
            blockRegistry.Register(block.Name, block.IsOpaque, block.IsSolid);
            if (block.Textures is not null)
                textures.Add(block.Name, block.Textures);
        }

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
                    throw new ContentLoadException(
                        $"Файл '{item.Path}': предмет '{item.Name}' не может ставить воздух ('{item.PlacesBlock}').");
                placedBlock = definition.Id;
            }

            itemRegistry.Register(item.Name, item.MaxStackSize, placedBlock);
        }

        blockRegistry.Freeze();
        itemRegistry.Freeze();
        return new ContentRegistries(blockRegistry, itemRegistry, textures.ToImmutable());
    }

    /// <summary>Пакет файла — первая папка пути. Его имя — пространство имён, поэтому правила те же: a–z, 0–9, _.</summary>
    private static string PackOf(ContentJsonFile file)
    {
        var separator = file.Path.IndexOfAny(['/', '\\']);
        if (separator < 0)
            throw Error(file, "файл должен лежать в папке пакета: '<пакет>/…/файл.json'.");
        var pack = file.Path[..separator];
        if (!PackNamePattern().IsMatch(pack))
            throw Error(file, $"имя пакета '{pack}' должно состоять из a–z, 0–9 и _: оно же пространство имён.");
        return pack;
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex PackNamePattern();

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

    /// <summary>
    /// Поле <c>textures</c>: строка — одна текстура на все грани, объект — <c>top</c>, <c>side</c> и <c>bottom</c>
    /// (все три обязательны).
    /// </summary>
    private static BlockTextures? OptionalTextures(JsonElement root, ContentJsonFile file)
    {
        if (!root.TryGetProperty("textures", out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var uniform = TextureName(value, "textures", file);
            return new BlockTextures(uniform, uniform, uniform);
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw Error(file, "поле 'textures' должно быть строкой или объектом с полями top, side и bottom.");
        CheckNoDuplicates(value, file);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not ("top" or "side" or "bottom"))
                throw Error(file, $"неизвестное поле 'textures.{property.Name}'; допустимы top, side и bottom.");
        }

        return new BlockTextures(FaceTexture("top"), FaceTexture("side"), FaceTexture("bottom"));

        string FaceTexture(string face) => value.TryGetProperty(face, out var texture)
            ? TextureName(texture, $"textures.{face}", file)
            : throw Error(file, $"в 'textures' нет обязательного поля '{face}'.");
    }

    private static string TextureName(JsonElement value, string property, ContentJsonFile file)
    {
        var name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (name is null || !TextureNamePattern().IsMatch(name))
            throw Error(file, $"поле '{property}' должно быть именем текстуры (a–z, 0–9, _).");
        return name;
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
        // TryGetInt32 у не-числа не возвращает false, а бросает InvalidOperationException.
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
            throw Error(file, $"обязательное поле '{property}' должно быть целым числом.");
        return result;
    }

    private static void CheckProperties(JsonElement root, ContentJsonFile file, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
                throw Error(file, $"неизвестное поле '{property.Name}'.");
        }
    }

    private static void CheckNoDuplicates(JsonElement root, ContentJsonFile file)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw Error(file, $"поле '{property.Name}' указано несколько раз.");
        }
    }

    private static ContentLoadException Error(ContentJsonFile file, string message) =>
        new($"Файл '{file.Path}': {message}");

    private sealed record BlockJson(string Path, string Name, bool IsOpaque, bool IsSolid, BlockTextures? Textures);
    private sealed record ItemJson(string Path, string Name, int MaxStackSize, string? PlacesBlock);
}
