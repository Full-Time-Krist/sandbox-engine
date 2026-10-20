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
    private readonly BlockShape[] _shapes;
    private readonly bool[] _hidesFace;
    private readonly int[] _faceLayers;
    private readonly List<string> _textureNames = [MissingTexture];
    private readonly Dictionary<string, int> _layerByName = new() { [MissingTexture] = 0 };

    public BlockVisuals(BlockRegistry registry, IReadOnlyDictionary<string, BlockAppearance> appearances)
    {
        // Таблицы имеют фиксированный размер: блок, зарегистрированный позже, вышел бы за их пределы.
        if (!registry.IsFrozen)
            throw new InvalidOperationException("Реестр блоков нужно заморозить до построения таблиц внешнего вида.");

        _isOpaque = new bool[registry.Count];
        _shapes = new BlockShape[registry.Count];
        _hidesFace = new bool[registry.Count * BlockFace.All.Length];
        _faceLayers = new int[registry.Count * BlockFace.All.Length];

        foreach (var definition in registry.All)
        {
            _isOpaque[definition.Id.Value] = definition.IsOpaque;
            _shapes[definition.Id.Value] = definition.Shape;
            for (var face = 0; face < BlockFace.All.Length; face++)
                _hidesFace[definition.Id.Value * BlockFace.All.Length + face] = definition.IsOpaque
                    || (!definition.Shape.IsFullCube && Covers(definition.Shape, BlockFace.All[face]));
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

    /// <summary>
    /// Закрывает ли блок <paramref name="neighbor"/> грань соседа, обращённую к нему: грань с индексом
    /// <paramref name="faceIndex"/> (в <see cref="BlockFace.All"/>) смотрит на этот блок. Закрывает непрозрачный блок —
    /// и не-куб, чей короб занимает всю прилегающую сторону клетки: низ плиты закрывает верх камня под ней. Прозрачный
    /// куб (воздух, стекло мода) не закрывает ничего: сквозь него видно; не-кубу прозрачность задана формой, а грани у него
    /// сплошные.
    /// </summary>
    public bool HidesFace(BlockId neighbor, int faceIndex) => _hidesFace[neighbor.Value * BlockFace.All.Length + faceIndex];

    /// <summary>Занимает ли форма всю сторону клетки, к которой смотрит грань соседа <paramref name="face"/> (то есть свою противоположную).</summary>
    private static bool Covers(BlockShape shape, BlockFace face) => shape.Boxes.Any(box =>
        (face.Dx == 0 || (face.Dx > 0 ? box.Min.X == 0 : box.Max.X == 1))
        && (face.Dy == 0 || (face.Dy > 0 ? box.Min.Y == 0 : box.Max.Y == 1))
        && (face.Dz == 0 || (face.Dz > 0 ? box.Min.Z == 0 : box.Max.Z == 1))
        && (face.Dx != 0 || (box.Min.X == 0 && box.Max.X == 1))
        && (face.Dy != 0 || (box.Min.Y == 0 && box.Max.Y == 1))
        && (face.Dz != 0 || (box.Min.Z == 0 && box.Max.Z == 1)));

    /// <summary>Форма блока (<see cref="BlockDefinition.Shape"/>): мешер, рамка, трещины и иконка рисуются по ней.</summary>
    public BlockShape Shape(BlockId id) => _shapes[id.Value];

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
