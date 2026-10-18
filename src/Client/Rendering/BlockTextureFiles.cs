using Shared.Modding;

namespace Client.Rendering;

/// <summary>
/// Где лежит файл текстуры блока по её полному имени <c>пакет:имя</c>: у мода — <c>textures/blocks/имя.png</c> в его папке,
/// у пакетов игры — в ассетах клиента (<c>Assets/Textures/Blocks/имя.png</c>).
/// </summary>
internal sealed class BlockTextureFiles(IReadOnlyList<ModInfo> mods)
{
    public static string GameDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Blocks");

    private readonly Dictionary<string, string> _modDirectories =
        mods.ToDictionary(mod => mod.Id, mod => mod.BlockTextureDirectory, StringComparer.Ordinal);

    public string PathFor(string texture)
    {
        var separator = texture.IndexOf(':');
        var (pack, name) = separator < 0 ? ("", texture) : (texture[..separator], texture[(separator + 1)..]);
        return Path.Combine(_modDirectories.GetValueOrDefault(pack) ?? GameDirectory, name + ".png");
    }
}
