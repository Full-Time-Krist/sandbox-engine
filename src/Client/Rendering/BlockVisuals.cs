using Shared.Blocks;

namespace Client.Rendering;

/// <summary>
/// Внешний вид блоков на клиенте: для каждого блока и каждой грани — номер слоя в массиве текстур.
/// Таблицы с доступом по <see cref="BlockId"/>, чтобы построение меша не искало ничего по строкам.
/// Строится один раз из замороженного реестра.
/// </summary>
internal sealed class BlockVisuals
{
    /// <summary>Слой 0 всегда «нет текстуры»: им отрисуются блоки без описания и текстуры без файла.</summary>
    public const string MissingTexture = "missing";

    public static readonly IReadOnlyDictionary<string, BlockAppearance> BaseAppearances = new Dictionary<string, BlockAppearance>
    {
        ["base:stone"] = BlockAppearance.Uniform("stone"),
        ["base:dirt"] = BlockAppearance.Uniform("dirt"),
        ["base:grass"] = new("grass_top", "grass_side", "dirt"),
        ["base:sand"] = BlockAppearance.Uniform("sand"),
        ["base:log"] = new("log_top", "log_side", "log_top"),
        ["base:planks"] = BlockAppearance.Uniform("planks"),
    };

    private readonly bool[] _isOpaque;
    private readonly int[] _faceLayers;
    private readonly List<string> _textureNames = [MissingTexture];
    private readonly Dictionary<string, int> _layerByName = new() { [MissingTexture] = 0 };

    public BlockVisuals(BlockRegistry registry, IReadOnlyDictionary<string, BlockAppearance> appearances)
    {
        // Таблицы имеют фиксированный размер: блок, зарегистрированный позже, вышел бы за их пределы.
        if (!registry.IsFrozen)
            throw new InvalidOperationException("Реестр блоков нужно заморозить до построения таблиц внешнего вида.");

        _isOpaque = new bool[registry.Count];
        _faceLayers = new int[registry.Count * BlockFace.All.Length];

        foreach (var definition in registry.All)
        {
            _isOpaque[definition.Id.Value] = definition.IsOpaque;
            if (definition.Id.IsAir)
                continue;

            var appearance = appearances.GetValueOrDefault(definition.Name) ?? BlockAppearance.Uniform(MissingTexture);
            for (var face = 0; face < BlockFace.All.Length; face++)
                _faceLayers[definition.Id.Value * BlockFace.All.Length + face] = GetOrAddLayer(appearance.For(BlockFace.All[face].Kind));
        }
    }

    /// <summary>Имена текстур по порядку слоёв. В этом порядке их нужно загрузить в массив текстур.</summary>
    public IReadOnlyList<string> TextureNames => _textureNames;

    public bool IsOpaque(BlockId id) => _isOpaque[id.Value];

    /// <summary>Слой текстуры для грани блока; <paramref name="faceIndex"/> — индекс в <see cref="BlockFace.All"/>.</summary>
    public int GetLayer(BlockId id, int faceIndex) => _faceLayers[id.Value * BlockFace.All.Length + faceIndex];

    private int GetOrAddLayer(string textureName)
    {
        if (_layerByName.TryGetValue(textureName, out var layer))
            return layer;

        layer = _textureNames.Count;
        _textureNames.Add(textureName);
        _layerByName.Add(textureName, layer);
        return layer;
    }
}
