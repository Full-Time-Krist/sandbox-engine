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

/// <summary>Пакет контента: имя (оно же пространство имён его определений) и папка с его JSON-файлами.</summary>
/// <param name="Dependencies">
/// Пакеты, от которых этот зависит, вместе с их зависимостями (транзитивно). Заменять (<c>"override": true</c>)
/// можно только их определения. null — ни от кого не зависит.
/// </param>
public sealed record ContentPack(string Name, string Directory, IReadOnlyCollection<string>? Dependencies = null);

/// <summary>Реестры, построенные из одного набора определений.</summary>
/// <param name="BlockTextures">
/// Текстуры граней по имени блока. Логике мира они не нужны (реестр блоков их не хранит), их берёт только клиент.
/// Блока без поля <c>textures</c> здесь нет.
/// </param>
/// <param name="ItemDisplayNames">Названия предметов для игрока (поле <c>displayName</c>); предмета без названия здесь нет.</param>
/// <param name="ItemIcons">
/// Картинки предметов (поле <c>icon</c>) — полные имена текстур <c>пакет:имя</c>, как у блоков; предмета без картинки
/// здесь нет (предмет-блок тогда рисуется кубиком).
/// </param>
public sealed record ContentRegistries(BlockRegistry Blocks, ItemRegistry Items,
    ImmutableDictionary<string, BlockTextures> BlockTextures, ImmutableDictionary<string, string> ItemDisplayNames,
    ImmutableDictionary<string, string> ItemIcons);

/// <summary>
/// Текстуры граней блока — полные имена <c>пакет:имя</c>. В JSON можно писать коротко (<c>"stone"</c>): имя дополняется
/// пакетом, где описан блок. Файл ищет клиент по пакету (ванильные — в своих ассетах, мода — в папке мода).
/// </summary>
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
/// <para>
/// Исключение — явная замена: определение с <c>"override": true</c> целиком заменяет одноимённое определение того же
/// типа из пакета, от которого заменяющий зависит. Если одно имя заменяют несколько пакетов, каждый следующий
/// (по порядку пакетов) должен зависеть от предыдущего — тогда побеждает последний, и это видно из зависимостей.
/// Имя не меняется, поэтому замена не затрагивает сохранения.
/// </para>
/// </remarks>
public static partial class ContentLoader
{
    /// <summary>Имя текстуры — имя файла без расширения: только a–z, 0–9 и _, чтобы из JSON нельзя было указать путь.</summary>
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex TextureNamePattern();

    public static ContentRegistries LoadDirectory(string directory) => LoadDirectories([directory]);

    /// <summary>Загружает пакеты из нескольких корней контента (в корне — папки пакетов) в одни реестры.</summary>
    public static ContentRegistries LoadDirectories(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return LoadPacks(roots.SelectMany(PacksIn));
    }

    /// <summary>Пакеты корня контента: каждая папка верхнего уровня — пакет. JSON прямо в корне — ошибка.</summary>
    public static IEnumerable<ContentPack> PacksIn(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Каталог контента не найден: '{root}'.");
        if (Directory.GetFiles(root, "*.json").FirstOrDefault() is { } loose)
            throw new ContentLoadException($"Файл '{loose}': контент должен лежать в папке пакета, а не в корне '{root}'.");

        return Directory.GetDirectories(root).Order(StringComparer.Ordinal)
            .Select(directory => new ContentPack(Path.GetFileName(directory), directory))
            .ToArray();
    }

    /// <summary>
    /// Загружает пакеты (папки игры и моды) в одни реестры. Пакет с одним именем дважды — ошибка: иначе неясно,
    /// какой из них настоящий. Папки пакета может не быть (мод без контента).
    /// </summary>
    public static ContentRegistries LoadPacks(IEnumerable<ContentPack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        var packList = packs.ToArray();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<ContentJsonFile>();
        foreach (var pack in packList)
        {
            if (!seen.TryAdd(pack.Name, pack.Directory))
                throw new ContentLoadException($"Пакет '{pack.Name}' найден дважды: в '{seen[pack.Name]}' и в '{pack.Directory}'.");
            if (!Directory.Exists(pack.Directory))
                continue;

            files.AddRange(Directory.GetFiles(pack.Directory, "*.json", SearchOption.AllDirectories)
                .Select(path => new ContentJsonFile(
                    pack.Name + "/" + Path.GetRelativePath(pack.Directory, path).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllText(path))));
        }

        return Load(files, packList);
    }

    /// <param name="packs">
    /// Пакеты по порядку загрузки (порядок и зависимости нужны замене определений). null или пакета файла нет в списке —
    /// пакет ни от кого не зависит и идёт после перечисленных.
    /// </param>
    public static ContentRegistries Load(IEnumerable<ContentJsonFile> files, IReadOnlyList<ContentPack>? packs = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        var orderedFiles = files.ToArray();
        if (orderedFiles.Any(file => file is null))
            throw new ArgumentException("Список файлов контента содержит null.", nameof(files));

        var packOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var packDependencies = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var (pack, index) in (packs ?? []).Select((pack, index) => (pack, index)))
        {
            packOrder.TryAdd(pack.Name, index);
            packDependencies.TryAdd(pack.Name, pack.Dependencies ?? []);
        }

        var blocks = new List<BlockJson>();
        var items = new List<ItemJson>();
        var blockOverrides = new List<BlockJson>();
        var itemOverrides = new List<ItemJson>();
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
            var isOverride = OptionalBoolean(root, "override", defaultValue: false, file);
            var nameSpace = name[..name.IndexOf(':')];
            if (isOverride)
            {
                if (nameSpace == pack)
                    throw Error(file, $"'override' — для замены чужих определений, а '{name}' из своего пакета '{pack}'.");
                if (!packDependencies.GetValueOrDefault(pack, []).Contains(nameSpace))
                    throw Error(file, $"пакет '{pack}' заменяет '{name}', но не зависит от пакета '{nameSpace}' (добавьте зависимость в mod.json).");
            }
            else if (nameSpace != pack)
            {
                throw Error(file, $"имя '{name}' не из пространства имён пакета '{pack}'; пакет объявляет только '{pack}:…' " +
                    "(чужое определение заменяется только явно: \"override\": true).");
            }

            switch (type)
            {
                case "block":
                    CheckProperties(root, file, "type", "name", "isOpaque", "isSolid", "textures", "override");
                    if (name == BlockRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для воздуха и не должно объявляться в JSON.");
                    if (!isOverride && !blockNames.Add(name))
                        throw Error(file, $"блок '{name}' объявлен более одного раза.");
                    (isOverride ? blockOverrides : blocks).Add(new BlockJson(file.Path, pack, name,
                        RequiredBoolean(root, "isOpaque", file),
                        OptionalBoolean(root, "isSolid", defaultValue: true, file),
                        OptionalTextures(root, pack, file)));
                    break;

                case "item":
                    CheckProperties(root, file, "type", "name", "maxStackSize", "placesBlock", "behavior", "displayName", "icon", "override");
                    if (name == ItemRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для пустого предмета и не должно объявляться в JSON.");
                    if (!isOverride && !itemNames.Add(name))
                        throw Error(file, $"предмет '{name}' объявлен более одного раза.");
                    var maxStackSize = RequiredInt32(root, "maxStackSize", file);
                    if (maxStackSize < 1)
                        throw Error(file, $"maxStackSize должен быть положительным числом, получено {maxStackSize}.");
                    var placesBlock = OptionalString(root, "placesBlock", file);
                    if (placesBlock is not null && !ContentNames.IsValid(placesBlock))
                        throw Error(file, $"placesBlock '{placesBlock}' должен иметь вид 'namespace:name'.");
                    var behavior = OptionalString(root, "behavior", file);
                    if (behavior is not null && !ContentNames.IsValid(behavior))
                        throw Error(file, $"behavior '{behavior}' должен иметь вид 'namespace:name'.");
                    var icon = root.TryGetProperty("icon", out var iconValue) ? TextureName(iconValue, "icon", pack, file) : null;
                    (isOverride ? itemOverrides : items).Add(new ItemJson(file.Path, pack, name, maxStackSize, placesBlock, behavior,
                        OptionalString(root, "displayName", file), icon));
                    break;

                default:
                    throw Error(file, $"неизвестный type '{type}'; допустимы 'block' и 'item'.");
            }
        }

        ApplyOverrides(blocks, blockOverrides, "блок", block => block.Name, block => block.Pack, block => block.Path, packOrder, packDependencies);
        ApplyOverrides(items, itemOverrides, "предмет", item => item.Name, item => item.Pack, item => item.Path, packOrder, packDependencies);

        var blockRegistry = new BlockRegistry();
        var textures = ImmutableDictionary.CreateBuilder<string, BlockTextures>(StringComparer.Ordinal);
        foreach (var block in blocks.OrderBy(block => block.Name, StringComparer.Ordinal))
        {
            blockRegistry.Register(block.Name, block.IsOpaque, block.IsSolid);
            if (block.Textures is not null)
                textures.Add(block.Name, block.Textures);
        }

        var itemRegistry = new ItemRegistry();
        var displayNames = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var icons = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
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

            itemRegistry.Register(item.Name, item.MaxStackSize, placedBlock, item.Behavior);
            if (item.DisplayName is not null)
                displayNames.Add(item.Name, item.DisplayName);
            if (item.Icon is not null)
                icons.Add(item.Name, item.Icon);
        }

        blockRegistry.Freeze();
        itemRegistry.Freeze();
        return new ContentRegistries(blockRegistry, itemRegistry, textures.ToImmutable(), displayNames.ToImmutable(), icons.ToImmutable());
    }

    /// <summary>
    /// Заменяет определения из <paramref name="definitions"/> последними по порядку пакетов заменами. Заменять можно только
    /// существующее определение того же типа; несколько замен одного имени должны идти по цепочке зависимостей.
    /// </summary>
    private static void ApplyOverrides<T>(List<T> definitions, List<T> overrides, string kind, Func<T, string> nameOf,
        Func<T, string> packOf, Func<T, string> pathOf, Dictionary<string, int> packOrder,
        Dictionary<string, IReadOnlyCollection<string>> packDependencies)
    {
        foreach (var group in overrides.GroupBy(nameOf, StringComparer.Ordinal))
        {
            var chain = group.OrderBy(d => packOrder.GetValueOrDefault(packOf(d), int.MaxValue))
                .ThenBy(packOf, StringComparer.Ordinal).ToArray();
            var index = definitions.FindIndex(d => nameOf(d) == group.Key);
            if (index < 0)
                throw new ContentLoadException($"Файл '{pathOf(chain[0])}': {kind} '{group.Key}' заменяется, но такого {kind}а нет.");

            for (var i = 1; i < chain.Length; i++)
            {
                var (earlier, later) = (packOf(chain[i - 1]), packOf(chain[i]));
                if (earlier == later)
                    throw new ContentLoadException($"Файл '{pathOf(chain[i])}': пакет '{later}' заменяет {kind} '{group.Key}' дважды.");
                if (!packDependencies.GetValueOrDefault(later, []).Contains(earlier))
                    throw new ContentLoadException(
                        $"{char.ToUpper(kind[0])}{kind[1..]} '{group.Key}' заменяют пакеты '{earlier}' и '{later}', не зависящие друг от друга: " +
                        "неясно, чья замена главнее. Пусть один мод зависит от другого.");
            }

            definitions[index] = chain[^1];
        }
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
    /// (все три обязательны). Короткие имена дополняются пакетом <paramref name="pack"/>.
    /// </summary>
    private static BlockTextures? OptionalTextures(JsonElement root, string pack, ContentJsonFile file)
    {
        if (!root.TryGetProperty("textures", out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var uniform = TextureName(value, "textures", pack, file);
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
            ? TextureName(texture, $"textures.{face}", pack, file)
            : throw Error(file, $"в 'textures' нет обязательного поля '{face}'.");
    }

    /// <summary>Полное имя текстуры: <c>пакет:имя</c> как есть или короткое <c>имя</c> из своего пакета.</summary>
    private static string TextureName(JsonElement value, string property, string pack, ContentJsonFile file)
    {
        var name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (name is not null && ContentNames.IsValid(name))
            return name;
        if (name is null || !TextureNamePattern().IsMatch(name))
            throw Error(file, $"поле '{property}' должно быть именем текстуры: 'имя' или 'пакет:имя' (a–z, 0–9, _).");
        return $"{pack}:{name}";
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

    private sealed record BlockJson(string Path, string Pack, string Name, bool IsOpaque, bool IsSolid, BlockTextures? Textures);
    private sealed record ItemJson(string Path, string Pack, string Name, int MaxStackSize, string? PlacesBlock, string? Behavior,
        string? DisplayName, string? Icon);
}
