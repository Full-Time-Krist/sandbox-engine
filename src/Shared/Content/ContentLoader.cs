using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shared.Blocks;
using Shared.Items;
using Shared.Lighting;
using Shared.Mobs;
using Shared.Quests;

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
/// <param name="Recipes">Рецепты (<c>type: "recipe"</c>); ссылки на предметы и станки уже проверены.</param>
/// <param name="Quests">Задания (<c>type: "quest"</c>); цели и предшествующие задания проверены, циклов нет.</param>
/// <param name="Mobs">Виды мобов (<c>type: "mob"</c>, с шага 9.1); добыча проверена.</param>
/// <param name="MobDisplayNames">Названия мобов для игрока (поле <c>displayName</c>); моба без названия здесь нет.</param>
/// <param name="MobLooks">
/// Модель и текстура каждого моба (с шага 9.2) — как <paramref name="BlockTextures"/>: серверу не нужны, их берёт только клиент.
/// </param>
public sealed record ContentRegistries(BlockRegistry Blocks, ItemRegistry Items,
    ImmutableDictionary<string, BlockTextures> BlockTextures, ImmutableDictionary<string, string> ItemDisplayNames,
    ImmutableDictionary<string, string> ItemIcons, RecipeRegistry Recipes, QuestRegistry Quests,
    MobRegistry Mobs, ImmutableDictionary<string, string> MobDisplayNames, ImmutableDictionary<string, MobLook> MobLooks);

/// <summary>
/// Текстуры граней блока — полные имена <c>пакет:имя</c>. В JSON можно писать коротко (<c>"stone"</c>): имя дополняется
/// пакетом, где описан блок. Файл ищет клиент по пакету (ванильные — в своих ассетах, мода — в папке мода).
/// </summary>
public sealed record BlockTextures(string Top, string Side, string Bottom);

/// <summary>
/// Как выглядит моб: модель (файл <c>models/имя.json</c>) и текстура (<c>textures/mobs/имя.png</c>) — полные имена
/// <c>пакет:имя</c>, как у текстур блоков. Файлы ищет клиент; по умолчанию оба — имя самого моба.
/// </summary>
public sealed record MobLook(string Model, string Texture);

/// <summary>Ошибка описания контента. Сообщение всегда содержит путь к исходному файлу.</summary>
public sealed class ContentLoadException : Exception
{
    public ContentLoadException(string message) : base(message) { }
    public ContentLoadException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Загружает JSON-описания блоков, предметов, рецептов и заданий в реестры. Числовые ID назначаются по строковым именам
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
        var recipes = new List<RecipeJson>();
        var recipeOverrides = new List<RecipeJson>();
        var blockNames = new HashSet<string>(StringComparer.Ordinal);
        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        var recipeNames = new HashSet<string>(StringComparer.Ordinal);
        var quests = new List<QuestJson>();
        var questOverrides = new List<QuestJson>();
        var questNames = new HashSet<string>(StringComparer.Ordinal);
        var mobs = new List<MobJson>();
        var mobOverrides = new List<MobJson>();
        var mobNames = new HashSet<string>(StringComparer.Ordinal);

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
                    CheckProperties(root, file, "type", "name", "isOpaque", "isSolid", "textures", "hardness", "tool", "toolTier", "light",
                        "model", "shape", "needsSupport", "blocksLight", "override");
                    if (name == BlockRegistry.AirName)
                        throw Error(file, $"имя '{name}' зарезервировано для воздуха и не должно объявляться в JSON.");
                    if (!isOverride && !blockNames.Add(name))
                        throw Error(file, $"блок '{name}' объявлен более одного раза.");
                    var isOpaque = RequiredBoolean(root, "isOpaque", file);
                    var shape = ShapeOf(root, file);
                    // Непрозрачный блок закрывает грани соседей: у тонкого факела вокруг были бы дыры в мире.
                    if (!shape.IsFullCube && isOpaque)
                        throw Error(file, $"блок формы {shape} не может быть непрозрачным (isOpaque: false).");
                    (isOverride ? blockOverrides : blocks).Add(new BlockJson(file.Path, pack, name, isOpaque,
                        OptionalBoolean(root, "isSolid", defaultValue: true, file),
                        OptionalTextures(root, pack, file), BlockMiningOf(root, file), LightOf(root, file), shape,
                        OptionalBoolean(root, "needsSupport", defaultValue: false, file),
                        OptionalBoolean(root, "blocksLight", defaultValue: false, file)));
                    break;

                case "item":
                    CheckProperties(root, file, "type", "name", "maxStackSize", "placesBlock", "behavior", "displayName", "icon", "tool",
                        "spawnsMob", "override");
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
                    var spawnsMob = OptionalString(root, "spawnsMob", file);
                    if (spawnsMob is not null && !ContentNames.IsValid(spawnsMob))
                        throw Error(file, $"spawnsMob '{spawnsMob}' должен иметь вид 'namespace:name'.");
                    if (spawnsMob is not null && placesBlock is not null)
                        throw Error(file, "предмет не может ставить и блок (placesBlock), и моба (spawnsMob).");
                    (isOverride ? itemOverrides : items).Add(new ItemJson(file.Path, pack, name, maxStackSize, placesBlock, behavior,
                        OptionalString(root, "displayName", file), icon, ToolOf(root, file), spawnsMob));
                    break;

                case "recipe":
                    CheckProperties(root, file, "type", "name", "result", "ingredients", "station", "override");
                    if (!isOverride && !recipeNames.Add(name))
                        throw Error(file, $"рецепт '{name}' объявлен более одного раза.");
                    var station = OptionalString(root, "station", file);
                    if (station is not null && !ContentNames.IsValid(station))
                        throw Error(file, $"station '{station}' должен иметь вид 'namespace:name'.");
                    (isOverride ? recipeOverrides : recipes).Add(new RecipeJson(file.Path, pack, name,
                        root.TryGetProperty("result", out var result) ? StackOf(result, "result", file)
                            : throw Error(file, "нет обязательного поля 'result'."),
                        IngredientsOf(root, file), station));
                    break;

                case "quest":
                    CheckProperties(root, file, "type", "name", "title", "description", "goal", "requires", "override");
                    if (!isOverride && !questNames.Add(name))
                        throw Error(file, $"задание '{name}' объявлено более одного раза.");
                    (isOverride ? questOverrides : quests).Add(new QuestJson(file.Path, pack, name,
                        RequiredString(root, "title", file), RequiredString(root, "description", file),
                        root.TryGetProperty("goal", out var goal) ? GoalOf(goal, file) : throw Error(file, "нет обязательного поля 'goal'."),
                        RequiresOf(root, file)));
                    break;

                case "mob":
                    CheckProperties(root, file, "type", "name", "displayName", "health", "speed", "size", "behavior", "drops", "model",
                        "texture", "override");
                    if (!isOverride && !mobNames.Add(name))
                        throw Error(file, $"моб '{name}' объявлен более одного раза.");
                    (isOverride ? mobOverrides : mobs).Add(MobOf(root, file, pack, name));
                    break;

                default:
                    throw Error(file, $"неизвестный type '{type}'; допустимы 'block', 'item', 'recipe', 'quest' и 'mob'.");
            }
        }

        ApplyOverrides(blocks, blockOverrides, ("блок", "блока"), block => block.Name, block => block.Pack, block => block.Path, packOrder, packDependencies);
        ApplyOverrides(items, itemOverrides, ("предмет", "предмета"), item => item.Name, item => item.Pack, item => item.Path, packOrder, packDependencies);
        ApplyOverrides(recipes, recipeOverrides, ("рецепт", "рецепта"), recipe => recipe.Name, recipe => recipe.Pack, recipe => recipe.Path, packOrder, packDependencies);
        ApplyOverrides(quests, questOverrides, ("задание", "задания"), quest => quest.Name, quest => quest.Pack, quest => quest.Path, packOrder, packDependencies);
        ApplyOverrides(mobs, mobOverrides, ("моб", "моба"), mob => mob.Name, mob => mob.Pack, mob => mob.Path, packOrder, packDependencies);

        // Яйцо призыва ссылается на моба, а моб — на предметы-добычу. Id мобов раздаются по именам, как у блоков, поэтому
        // известны заранее: предметы получают их сразу, а мобы регистрируются после предметов, с проверенной добычей.
        var sortedMobs = mobs.OrderBy(mob => mob.Name, StringComparer.Ordinal).ToList();
        var mobIds = sortedMobs.Select((mob, index) => (mob.Name, Id: new MobId((ushort)(index + 1))))
            .ToDictionary(pair => pair.Name, pair => pair.Id, StringComparer.Ordinal);

        var blockRegistry = new BlockRegistry();
        var textures = ImmutableDictionary.CreateBuilder<string, BlockTextures>(StringComparer.Ordinal);
        foreach (var block in blocks.OrderBy(block => block.Name, StringComparer.Ordinal))
        {
            blockRegistry.Register(block.Name, block.IsOpaque, block.IsSolid, block.Mining, block.Light, block.NeedsSupport, block.Shape,
                block.BlocksLight);
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

            var spawnedMob = MobId.None;
            if (item.SpawnsMob is not null && !mobIds.TryGetValue(item.SpawnsMob, out spawnedMob))
                throw new ContentLoadException($"Файл '{item.Path}': предмет '{item.Name}' ставит неизвестного моба '{item.SpawnsMob}'.");

            itemRegistry.Register(item.Name, item.MaxStackSize, placedBlock, item.Behavior, item.Tool, spawnedMob);
            if (item.DisplayName is not null)
                displayNames.Add(item.Name, item.DisplayName);
            if (item.Icon is not null)
                icons.Add(item.Name, item.Icon);
        }

        blockRegistry.Freeze();
        itemRegistry.Freeze();

        var mobRegistry = new MobRegistry();
        var mobDisplayNames = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var mobLooks = ImmutableDictionary.CreateBuilder<string, MobLook>(StringComparer.Ordinal);
        foreach (var mob in sortedMobs)
        {
            var drops = mob.Drops.Select(drop => itemRegistry.TryGet(drop.Item, out var item)
                    ? new MobDrop(item.Id, drop.Min, drop.Max)
                    : throw new ContentLoadException($"Файл '{mob.Path}': моб '{mob.Name}' роняет неизвестный предмет '{drop.Item}'."))
                .ToImmutableArray();
            var definition = mobRegistry.Register(mob.Name, mob.Health, mob.Speed, mob.Width, mob.Height, mob.Behavior, drops);
            if (definition.Id != mobIds[mob.Name])
                throw new InvalidOperationException($"Id моба '{mob.Name}' разошёлся с заранее розданным.");
            if (mob.DisplayName is not null)
                mobDisplayNames.Add(mob.Name, mob.DisplayName);
            mobLooks.Add(mob.Name, mob.Look);
        }
        mobRegistry.Freeze();

        var recipeRegistry = new RecipeRegistry(recipes.Select(recipe => ResolveRecipe(recipe, blockRegistry, itemRegistry)));
        return new ContentRegistries(blockRegistry, itemRegistry, textures.ToImmutable(), displayNames.ToImmutable(), icons.ToImmutable(),
            recipeRegistry, ResolveQuests(quests, blockRegistry, itemRegistry), mobRegistry, mobDisplayNames.ToImmutable(),
            mobLooks.ToImmutable());
    }

    /// <summary>
    /// Моб: <c>health</c> (целое ≥ 1), <c>speed</c> (блоков в секунду, > 0), <c>size</c> (<c>{width, height}</c>, до 4 блоков),
    /// <c>behavior</c> (имя поведения), <c>drops</c> (<c>[{item, min, max}]</c>, по умолчанию пусто), <c>displayName</c>,
    /// <c>model</c> и <c>texture</c> (имена, как у текстур блоков; по умолчанию — имя моба).
    /// </summary>
    private static MobJson MobOf(JsonElement root, ContentJsonFile file, string pack, string name)
    {
        var health = RequiredInt32(root, "health", file);
        if (health < 1)
            throw Error(file, $"health должен быть не меньше 1, получено {health}.");
        if (!root.TryGetProperty("speed", out _))
            throw Error(file, "нет обязательного поля 'speed'.");
        var speed = OptionalNumber(root, "speed", 0, file);
        if (!(speed > 0))
            throw Error(file, $"speed должна быть больше 0, получено {speed}.");
        if (!root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Object)
            throw Error(file, "нет обязательного поля 'size' — объекта {width, height}.");
        CheckProperties(size, file, "width", "height");
        var width = OptionalNumber(size, "width", 0, file);
        var height = OptionalNumber(size, "height", 0, file);
        if (!(width > 0 && width <= 4 && height > 0 && height <= 4))
            throw Error(file, $"size: ширина и высота — от 0 до 4 блоков, получено {width} × {height}.");
        var behavior = RequiredString(root, "behavior", file);
        if (!ContentNames.IsValid(behavior))
            throw Error(file, $"behavior '{behavior}' должен иметь вид 'namespace:name'.");

        var drops = ImmutableArray.CreateBuilder<(string Item, int Min, int Max)>();
        if (root.TryGetProperty("drops", out var dropsValue))
        {
            if (dropsValue.ValueKind != JsonValueKind.Array)
                throw Error(file, "поле 'drops' должно быть массивом [{item, min, max}].");
            foreach (var drop in dropsValue.EnumerateArray())
            {
                if (drop.ValueKind != JsonValueKind.Object)
                    throw Error(file, "каждая добыча в 'drops' — объект {item, min, max}.");
                CheckProperties(drop, file, "item", "min", "max");
                var item = RequiredString(drop, "item", file);
                if (!ContentNames.IsValid(item))
                    throw Error(file, $"предмет добычи '{item}' должен иметь вид 'namespace:name'.");
                int min = RequiredInt32(drop, "min", file), max = RequiredInt32(drop, "max", file);
                if (min < 0 || max < min)
                    throw Error(file, $"у добычи '{item}' должно быть 0 ≤ min ≤ max, получено {min}–{max}.");
                drops.Add((item, min, max));
            }
        }
        // Своя модель или текстура нужна не каждому: зомби-утопленник мода может взять модель зомби с другой текстурой.
        var model = root.TryGetProperty("model", out var modelValue) ? TextureName(modelValue, "model", pack, file) : name;
        var texture = root.TryGetProperty("texture", out var textureValue) ? TextureName(textureValue, "texture", pack, file) : name;
        return new MobJson(file.Path, pack, name, OptionalString(root, "displayName", file), health, speed, width, height, behavior,
            drops.ToImmutable(), new MobLook(model, texture));
    }

    /// <summary>
    /// Проверяет задания, когда все блоки и предметы известны: цель ссылается на существующий предмет или блок,
    /// предшествующие задания существуют, и цепочки не замыкаются в круг (иначе задания никогда не стали бы доступны).
    /// </summary>
    private static QuestRegistry ResolveQuests(List<QuestJson> quests, BlockRegistry blocks, ItemRegistry items)
    {
        var byName = quests.ToDictionary(quest => quest.Name, StringComparer.Ordinal);
        var resolved = new List<Quest>(quests.Count);
        foreach (var quest in quests)
        {
            var (kind, target, count) = quest.Goal;
            var item = ItemId.Air;
            var block = BlockId.Air;
            if (kind is QuestGoalKind.Obtain or QuestGoalKind.Craft)
            {
                if (!items.TryGet(target, out var definition) || definition.Id.IsAir)
                    throw QuestError(quest, $"в цели неизвестный предмет '{target}'.");
                item = definition.Id;
            }
            else
            {
                if (!blocks.TryGet(target, out var definition) || definition.Id.IsAir)
                    throw QuestError(quest, $"в цели неизвестный блок '{target}'.");
                block = definition.Id;
            }

            foreach (var required in quest.Requires)
            {
                if (!byName.ContainsKey(required))
                    throw QuestError(quest, $"в requires неизвестное задание '{required}'.");
            }
            resolved.Add(new Quest(quest.Name, quest.Title, quest.Description, new QuestGoal(kind, item, block, count), quest.Requires));
        }

        // Поиск в глубину: серый — на пути, чёрный — проверен. Встретили серый — круг.
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var quest in quests)
            Visit(quest.Name, []);
        return new QuestRegistry(resolved);

        void Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out var done))
            {
                if (!done)
                    throw QuestError(byName[name], $"задания требуют друг друга по кругу: {string.Join(" → ", path.SkipWhile(n => n != name).Append(name))}.");
                return;
            }
            state[name] = false;
            path.Add(name);
            foreach (var required in byName[name].Requires)
                Visit(required, path);
            path.RemoveAt(path.Count - 1);
            state[name] = true;
        }

        static ContentLoadException QuestError(QuestJson quest, string message) =>
            new($"Файл '{quest.Path}': задание '{quest.Name}': {message}");
    }

    /// <summary>Поле <c>goal</c> задания: <c>{"type": "obtain"|"craft", "item": …}</c> или <c>{"type": "break"|"place", "block": …}</c>, <c>count</c> по умолчанию 1.</summary>
    private static (QuestGoalKind Kind, string Target, int Count) GoalOf(JsonElement value, ContentJsonFile file)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error(file, "поле 'goal' должно быть объектом {\"type\", \"item\" или \"block\", \"count\"}.");
        CheckNoDuplicates(value, file);
        var kind = RequiredString(value, "type", file) switch
        {
            "obtain" => QuestGoalKind.Obtain,
            "craft" => QuestGoalKind.Craft,
            "break" => QuestGoalKind.Break,
            "place" => QuestGoalKind.Place,
            var other => throw Error(file, $"неизвестный goal.type '{other}'; допустимы obtain, craft, break и place."),
        };
        var targetField = kind is QuestGoalKind.Obtain or QuestGoalKind.Craft ? "item" : "block";
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name != "type" && field.Name != "count" && field.Name != targetField)
                throw Error(file, $"неизвестное поле 'goal.{field.Name}'; у цели '{RequiredString(value, "type", file)}' допустимы type, {targetField} и count.");
        }

        var target = RequiredString(value, targetField, file);
        if (!ContentNames.IsValid(target))
            throw Error(file, $"goal.{targetField} '{target}' должен иметь вид 'namespace:name'.");
        var count = value.TryGetProperty("count", out _) ? RequiredInt32(value, "count", file) : 1;
        if (count < 1)
            throw Error(file, $"goal.count должен быть положительным, получено {count}.");
        return (kind, target, count);
    }

    /// <summary>Поле <c>requires</c>: массив имён заданий (без повторов и без себя); нет поля — пусто.</summary>
    private static ImmutableArray<string> RequiresOf(JsonElement root, ContentJsonFile file)
    {
        if (!root.TryGetProperty("requires", out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw Error(file, "поле 'requires' должно быть массивом имён заданий.");
        var names = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            var name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (name is null || !ContentNames.IsValid(name))
                throw Error(file, "в 'requires' — только имена заданий вида 'namespace:name'.");
            if (names.Contains(name))
                throw Error(file, $"задание '{name}' указано в 'requires' дважды.");
            names.Add(name);
        }
        var self = RequiredString(root, "name", file);
        if (names.Contains(self))
            throw Error(file, $"задание '{self}' требует само себя.");
        return [.. names];
    }

    /// <summary>
    /// Проверяет ссылки рецепта, когда все блоки и предметы (с заменами) уже известны: предметы и станок существуют,
    /// результат помещается в одну стопку, ингредиенты не повторяются.
    /// </summary>
    private static Recipe ResolveRecipe(RecipeJson recipe, BlockRegistry blocks, ItemRegistry items)
    {
        var result = Stack(recipe.Result, "результат");
        var maxStack = items.Get(result.Item).MaxStackSize;
        if (result.Count > maxStack)
            throw RecipeError($"результат {recipe.Result.Count} × '{recipe.Result.Item}' больше стопки этого предмета ({maxStack}).");

        var ingredients = recipe.Ingredients.Select(ingredient => Stack(ingredient, "ингредиент")).ToImmutableArray();
        if (ingredients.GroupBy(ingredient => ingredient.Item).FirstOrDefault(group => group.Count() > 1) is { } repeated)
            throw RecipeError($"ингредиент '{items.Get(repeated.Key).Name}' указан несколько раз; укажите его один раз с общим числом.");

        BlockId? station = null;
        if (recipe.Station is not null)
        {
            if (!blocks.TryGet(recipe.Station, out var block) || block.Id.IsAir)
                throw RecipeError($"станок '{recipe.Station}' — не блок.");
            station = block.Id;
        }

        return new Recipe(recipe.Name, result, ingredients, station);

        ItemStack Stack(StackJson stack, string role)
        {
            if (!items.TryGet(stack.Item, out var item) || item.Id.IsAir)
                throw RecipeError($"{role} '{stack.Item}' — неизвестный предмет.");
            return new ItemStack(item.Id, stack.Count);
        }

        ContentLoadException RecipeError(string message) => new($"Файл '{recipe.Path}': рецепт '{recipe.Name}': {message}");
    }

    /// <summary>Поле <c>ingredients</c>: непустой массив стопок <c>{"item", "count"}</c>.</summary>
    private static ImmutableArray<StackJson> IngredientsOf(JsonElement root, ContentJsonFile file)
    {
        if (!root.TryGetProperty("ingredients", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw Error(file, "обязательное поле 'ingredients' должно быть непустым массивом {\"item\", \"count\"}.");
        return [.. value.EnumerateArray().Select((ingredient, index) => StackOf(ingredient, $"ingredients[{index}]", file))];
    }

    /// <summary>Стопка в рецепте: <c>{"item": "base:planks", "count": 4}</c>, <c>count</c> по умолчанию 1.</summary>
    private static StackJson StackOf(JsonElement value, string property, ContentJsonFile file)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error(file, $"поле '{property}' должно быть объектом {{\"item\", \"count\"}}.");
        CheckNoDuplicates(value, file);
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name is not ("item" or "count"))
                throw Error(file, $"неизвестное поле '{property}.{field.Name}'; допустимы item и count.");
        }

        var item = RequiredString(value, "item", file);
        if (!ContentNames.IsValid(item))
            throw Error(file, $"{property}.item '{item}' должен иметь вид 'namespace:name'.");
        var count = value.TryGetProperty("count", out _) ? RequiredInt32(value, "count", file) : 1;
        if (count < 1)
            throw Error(file, $"{property}.count должен быть положительным, получено {count}.");
        return new StackJson(item, count);
    }

    /// <summary>
    /// Заменяет определения из <paramref name="definitions"/> последними по порядку пакетов заменами. Заменять можно только
    /// существующее определение того же типа; несколько замен одного имени должны идти по цепочке зависимостей.
    /// </summary>
    /// <param name="kind">Вид определения для сообщений: именительный и родительный падеж.</param>
    private static void ApplyOverrides<T>(List<T> definitions, List<T> overrides, (string Name, string Genitive) kind, Func<T, string> nameOf,
        Func<T, string> packOf, Func<T, string> pathOf, Dictionary<string, int> packOrder,
        Dictionary<string, IReadOnlyCollection<string>> packDependencies)
    {
        foreach (var group in overrides.GroupBy(nameOf, StringComparer.Ordinal))
        {
            var chain = group.OrderBy(d => packOrder.GetValueOrDefault(packOf(d), int.MaxValue))
                .ThenBy(packOf, StringComparer.Ordinal).ToArray();
            var index = definitions.FindIndex(d => nameOf(d) == group.Key);
            if (index < 0)
                throw new ContentLoadException($"Файл '{pathOf(chain[0])}': {kind.Name} '{group.Key}' заменяется, но такого {kind.Genitive} нет.");

            for (var i = 1; i < chain.Length; i++)
            {
                var (earlier, later) = (packOf(chain[i - 1]), packOf(chain[i]));
                if (earlier == later)
                    throw new ContentLoadException($"Файл '{pathOf(chain[i])}': пакет '{later}' заменяет {kind.Name} '{group.Key}' дважды.");
                if (!packDependencies.GetValueOrDefault(later, []).Contains(earlier))
                    throw new ContentLoadException(
                        $"{char.ToUpper(kind.Name[0])}{kind.Name[1..]} '{group.Key}' заменяют пакеты '{earlier}' и '{later}', не зависящие друг от друга: " +
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

    /// <summary>Вид инструмента: имя без пространства имён (<c>pickaxe</c>) — виды общие для всех модов.</summary>
    [GeneratedRegex("^[a-z_]+$")]
    private static partial Regex ToolTypePattern();

    /// <summary>
    /// Форма блока: <c>model</c> — готовая (<c>cube</c> по умолчанию, <c>torch</c>, <c>slab</c>) или <c>shape</c> — свои короба
    /// в пикселях: <c>[[x1, y1, z1, x2, y2, z2], …]</c>, каждый внутри блока (0–16) и с объёмом. Оба поля сразу — ошибка.
    /// </summary>
    private static BlockShape ShapeOf(JsonElement root, ContentJsonFile file)
    {
        var model = OptionalString(root, "model", file);
        if (root.TryGetProperty("shape", out var shape))
        {
            if (model is not null)
                throw Error(file, "у блока или model, или shape, не оба сразу.");
            return CustomShape(shape, file);
        }
        if (model is null)
            return BlockShape.Cube;
        return BlockShape.Named.FirstOrDefault(named => named.Name == model)
            ?? throw Error(file, $"неизвестная форма model '{model}'; допустимы " +
                string.Join(", ", BlockShape.Named.Select(named => $"'{named.Name}'")) + "; свои короба — поле shape.");
    }

    private static BlockShape CustomShape(JsonElement shape, ContentJsonFile file)
    {
        const string Format = "shape — массив коробов [x1, y1, z1, x2, y2, z2] в пикселях от 0 до 16";
        if (shape.ValueKind != JsonValueKind.Array || shape.GetArrayLength() == 0)
            throw Error(file, Format + ", хотя бы один.");
        var boxes = ImmutableArray.CreateBuilder<ShapeBox>();
        foreach (var box in shape.EnumerateArray())
        {
            if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 6
                || box.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || v.GetDouble() is < 0 or > BlockShape.Pixels))
                throw Error(file, $"{Format}; получено {box.GetRawText()}.");
            var v = box.EnumerateArray().Select(c => (float)c.GetDouble()).ToArray();
            if (v[0] >= v[3] || v[1] >= v[4] || v[2] >= v[5])
                throw Error(file, $"у короба shape {box.GetRawText()} начало должно быть меньше конца по каждой оси.");
            boxes.Add(ShapeBox.FromPixels(v[0], v[1], v[2], v[3], v[4], v[5]));
        }
        return new BlockShape(null, boxes.ToImmutable());
    }

    /// <summary>Свет, который блок излучает сам: <c>light</c>, целое 0–15, по умолчанию 0.</summary>
    private static int LightOf(JsonElement root, ContentJsonFile file)
    {
        if (!root.TryGetProperty("light", out _))
            return 0;
        var light = RequiredInt32(root, "light", file);
        if ((uint)light > ChunkLight.Max)
            throw Error(file, $"light должен быть от 0 до {ChunkLight.Max}, получено {light}.");
        return light;
    }

    /// <summary>
    /// Поля ломания блока: <c>hardness</c> (секунды рукой, по умолчанию 1), <c>tool</c> (вид инструмента) и <c>toolTier</c>
    /// (нужный уровень, по умолчанию 0). Уровень без вида инструмента не имеет смысла — ошибка.
    /// </summary>
    private static BlockMining BlockMiningOf(JsonElement root, ContentJsonFile file)
    {
        var hardness = OptionalNumber(root, "hardness", BlockMining.Default.Hardness, file);
        if (hardness < 0)
            throw Error(file, $"hardness не может быть отрицательной, получено {hardness}.");
        var tool = OptionalString(root, "tool", file);
        if (tool is not null && !ToolTypePattern().IsMatch(tool))
            throw Error(file, $"tool '{tool}' должен быть видом инструмента: a–z и _, например \"pickaxe\".");
        var tier = root.TryGetProperty("toolTier", out _) ? RequiredInt32(root, "toolTier", file) : 0;
        if (tier < 0)
            throw Error(file, $"toolTier не может быть отрицательным, получено {tier}.");
        if (tier > 0 && tool is null)
            throw Error(file, "toolTier указан без tool: неизвестно, инструмент какого вида нужен.");
        return new BlockMining(hardness, tool, tier);
    }

    /// <summary>Поле <c>tool</c> предмета: объект <c>{"type": "pickaxe", "tier": 1, "speed": 2}</c>.</summary>
    private static ToolInfo? ToolOf(JsonElement root, ContentJsonFile file)
    {
        if (!root.TryGetProperty("tool", out var tool))
            return null;
        if (tool.ValueKind != JsonValueKind.Object)
            throw Error(file, "поле 'tool' должно быть объектом {\"type\", \"tier\", \"speed\"}.");
        CheckNoDuplicates(tool, file);
        foreach (var property in tool.EnumerateObject())
        {
            if (property.Name is not ("type" or "tier" or "speed"))
                throw Error(file, $"неизвестное поле 'tool.{property.Name}'; допустимы type, tier и speed.");
        }

        var type = RequiredString(tool, "type", file);
        if (!ToolTypePattern().IsMatch(type))
            throw Error(file, $"tool.type '{type}' должен быть видом инструмента: a–z и _, например \"pickaxe\".");
        var tier = tool.TryGetProperty("tier", out _) ? RequiredInt32(tool, "tier", file) : 0;
        var speed = (float)OptionalNumber(tool, "speed", double.NaN, file);
        if (tier < 0 || !(speed > 0))
            throw Error(file, $"у инструмента tier ≥ 0 и обязательный speed > 0, получено tier {tier}, speed {speed}.");
        return new ToolInfo(type, tier, speed);
    }

    private static float OptionalNumber(JsonElement root, string property, double defaultValue, ContentJsonFile file)
    {
        if (!root.TryGetProperty(property, out var value))
            return (float)defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Error(file, $"поле '{property}' должно быть числом.");
        return (float)number;
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

    private sealed record BlockJson(string Path, string Pack, string Name, bool IsOpaque, bool IsSolid, BlockTextures? Textures,
        BlockMining Mining, int Light, BlockShape Shape, bool NeedsSupport, bool BlocksLight);
    private sealed record ItemJson(string Path, string Pack, string Name, int MaxStackSize, string? PlacesBlock, string? Behavior,
        string? DisplayName, string? Icon, ToolInfo? Tool, string? SpawnsMob);
    private sealed record MobJson(string Path, string Pack, string Name, string? DisplayName, int Health, float Speed, double Width,
        double Height, string Behavior, ImmutableArray<(string Item, int Min, int Max)> Drops, MobLook Look);
    private sealed record RecipeJson(string Path, string Pack, string Name, StackJson Result, ImmutableArray<StackJson> Ingredients,
        string? Station);
    private sealed record StackJson(string Item, int Count);
    private sealed record QuestJson(string Path, string Pack, string Name, string Title, string Description,
        (QuestGoalKind Kind, string Target, int Count) Goal, ImmutableArray<string> Requires);
}
