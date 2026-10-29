using Shared.Modding;

namespace Client.Rendering;

/// <summary>Чья текстура: грани блока, картинка предмета или развёртка моба. От этого зависит папка.</summary>
internal enum TextureKind
{
    Block,
    Item,

    /// <summary>Текстура модели моба, 64×64 (с шага 9.2).</summary>
    Mob,
}

/// <summary>
/// Где лежит файл текстуры по её полному имени <c>пакет:имя</c>: у мода — <c>textures/blocks/имя.png</c>,
/// <c>textures/items/имя.png</c> или <c>textures/mobs/имя.png</c> в его папке, у пакетов игры — в ассетах клиента
/// (<c>Assets/Textures/Blocks</c>, <c>…/Items</c>, <c>…/Mobs</c>). Так же — модели мобов (<see cref="ModelPathFor"/>).
/// </summary>
internal sealed class TextureFiles(IReadOnlyList<ModInfo> mods)
{
    public static string GameDirectory(TextureKind kind) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", kind switch
        {
            TextureKind.Block => "Blocks",
            TextureKind.Item => "Items",
            _ => "Mobs",
        });

    /// <summary>Модели мобов игры: <c>Assets/Models</c>.</summary>
    public static string GameModelDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Models");

    private readonly Dictionary<string, ModInfo> _mods = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);

    public string PathFor(string texture, TextureKind kind)
    {
        var (mod, name) = Split(texture);
        var directory = mod is null
            ? GameDirectory(kind)
            : kind switch
            {
                TextureKind.Block => mod.BlockTextureDirectory,
                TextureKind.Item => mod.ItemTextureDirectory,
                _ => mod.MobTextureDirectory,
            };
        return Path.Combine(directory, name + ".png");
    }

    /// <summary>Файл модели моба по её полному имени: <c>models/имя.json</c> у мода, <c>Assets/Models/имя.json</c> у игры.</summary>
    public string ModelPathFor(string model)
    {
        var (mod, name) = Split(model);
        return Path.Combine(mod?.ModelDirectory ?? GameModelDirectory, name + ".json");
    }

    private (ModInfo? Mod, string Name) Split(string fullName)
    {
        var separator = fullName.IndexOf(':');
        var (pack, name) = separator < 0 ? ("", fullName) : (fullName[..separator], fullName[(separator + 1)..]);
        return (_mods.GetValueOrDefault(pack), name);
    }
}
