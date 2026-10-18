using Shared.Modding;

namespace Client.Rendering;

/// <summary>Чья текстура: грани блока или картинка предмета. От этого зависит папка.</summary>
internal enum TextureKind
{
    Block,
    Item,
}

/// <summary>
/// Где лежит файл текстуры по её полному имени <c>пакет:имя</c>: у мода — <c>textures/blocks/имя.png</c> или
/// <c>textures/items/имя.png</c> в его папке, у пакетов игры — в ассетах клиента (<c>Assets/Textures/Blocks</c>, <c>…/Items</c>).
/// </summary>
internal sealed class TextureFiles(IReadOnlyList<ModInfo> mods)
{
    public static string GameDirectory(TextureKind kind) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", kind == TextureKind.Block ? "Blocks" : "Items");

    private readonly Dictionary<string, ModInfo> _mods = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);

    public string PathFor(string texture, TextureKind kind)
    {
        var separator = texture.IndexOf(':');
        var (pack, name) = separator < 0 ? ("", texture) : (texture[..separator], texture[(separator + 1)..]);
        var directory = _mods.TryGetValue(pack, out var mod)
            ? kind == TextureKind.Block ? mod.BlockTextureDirectory : mod.ItemTextureDirectory
            : GameDirectory(kind);
        return Path.Combine(directory, name + ".png");
    }
}
