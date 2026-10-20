using Shared.Blocks;
using Shared.Content;

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

    /// <summary>Внешний вид блоков по текстурам из контента (поле <c>textures</c> в JSON блока).</summary>
    public static IReadOnlyDictionary<string, BlockAppearance> AppearancesFrom(IReadOnlyDictionary<string, BlockTextures> textures) =>
        textures.ToDictionary(pair => pair.Key, pair => new BlockAppearance(pair.Value.Top, pair.Value.Side, pair.Value.Bottom),
            StringComparer.Ordinal);

    private readonly bool[] _isOpaque;
    private readonly BlockModel[] _models;
    private readonly int[] _faceLayers;
    private readonly List<string> _textureNames = [MissingTexture];
    private readonly Dictionary<string, int> _layerByName = new() { [MissingTexture] = 0 };

    /// <param name="models">Формы блоков не-кубов (<see cref="ContentRegistries.BlockModels"/>); null — все кубы.</param>
    public BlockVisuals(BlockRegistry registry, IReadOnlyDictionary<string, BlockAppearance> appearances,
        IReadOnlyDictionary<string, BlockModel>? models = null)
    {
        // Таблицы имеют фиксированный размер: блок, зарегистрированный позже, вышел бы за их пределы.
        if (!registry.IsFrozen)
            throw new InvalidOperationException("Реестр блоков нужно заморозить до построения таблиц внешнего вида.");

        _isOpaque = new bool[registry.Count];
        _models = new BlockModel[registry.Count];
        _faceLayers = new int[registry.Count * BlockFace.All.Length];

        foreach (var definition in registry.All)
        {
            _isOpaque[definition.Id.Value] = definition.IsOpaque;
            _models[definition.Id.Value] = models?.GetValueOrDefault(definition.Name) ?? BlockModel.Cube;
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

    public BlockModel Model(BlockId id) => _models[id.Value];

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
