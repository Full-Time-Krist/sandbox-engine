using Shared.Items;

namespace Client.Rendering;

/// <summary>
/// Внешний вид предметов на клиенте: у предмета с картинкой (<c>icon</c> в JSON) — номер её слоя в массиве текстур.
/// Картинки предметов лежат в том же массиве, что и текстуры блоков, сразу после них (тот же размер 16×16):
/// интерфейсу хватает одного массива и для кубиков блоков, и для плоских иконок.
/// </summary>
internal sealed class ItemVisuals
{
    /// <summary>Нет своей картинки: предмет-блок рисуется кубиком, остальные — «нет текстуры».</summary>
    public const int NoIcon = -1;

    private readonly int[] _iconLayers;
    private readonly List<string> _textureNames = [];

    public ItemVisuals(BlockVisuals blocks, ItemRegistry items, IReadOnlyDictionary<string, string> icons)
    {
        if (!items.IsFrozen)
            throw new InvalidOperationException("Реестр предметов нужно заморозить до построения таблицы иконок.");

        Blocks = blocks;
        _iconLayers = new int[items.Count];
        var layerByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.All)
        {
            if (!icons.TryGetValue(item.Name, out var icon))
            {
                _iconLayers[item.Id.Value] = NoIcon;
                continue;
            }
            if (!layerByName.TryGetValue(icon, out var layer))
            {
                layer = blocks.TextureNames.Count + _textureNames.Count;
                layerByName.Add(icon, layer);
                _textureNames.Add(icon);
            }
            _iconLayers[item.Id.Value] = layer;
        }
    }

    public BlockVisuals Blocks { get; }

    /// <summary>Имена картинок предметов по порядку слоёв — их слои идут сразу после текстур блоков.</summary>
    public IReadOnlyList<string> TextureNames => _textureNames;

    /// <summary>Слой картинки предмета или <see cref="NoIcon"/>.</summary>
    public int IconLayer(ItemId item) => _iconLayers[item.Value];
}
