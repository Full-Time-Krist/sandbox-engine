using System.Numerics;
using Shared.Blocks;

namespace Client.Rendering;

/// <summary>
/// Внешний вид блоков на клиенте: таблицы с доступом по <see cref="BlockId"/>, чтобы построение меша
/// не искало ничего по строкам. Строится один раз из замороженного реестра.
/// </summary>
internal sealed class BlockVisuals
{
    // Ярко-розовый — общепринятый цвет «нет данных»: сразу видно блок, для которого забыли описать вид.
    private static readonly Vector3 MissingColor = new(1f, 0f, 1f);

    public static readonly IReadOnlyDictionary<string, Vector3> BaseColors = new Dictionary<string, Vector3>
    {
        ["base:stone"] = new(0.50f, 0.50f, 0.52f),
        ["base:dirt"] = new(0.45f, 0.30f, 0.18f),
        ["base:grass"] = new(0.35f, 0.65f, 0.25f),
        ["base:sand"] = new(0.86f, 0.80f, 0.55f),
        ["base:log"] = new(0.40f, 0.28f, 0.15f),
        ["base:planks"] = new(0.72f, 0.56f, 0.32f),
    };

    private readonly bool[] _isOpaque;
    private readonly Vector3[] _colors;

    public BlockVisuals(BlockRegistry registry, IReadOnlyDictionary<string, Vector3> colors)
    {
        // Таблицы имеют фиксированный размер: блок, зарегистрированный позже, вышел бы за их пределы.
        if (!registry.IsFrozen)
            throw new InvalidOperationException("Реестр блоков нужно заморозить до построения таблиц внешнего вида.");

        _isOpaque = new bool[registry.Count];
        _colors = new Vector3[registry.Count];

        foreach (var definition in registry.All)
        {
            _isOpaque[definition.Id.Value] = definition.IsOpaque;
            _colors[definition.Id.Value] = colors.GetValueOrDefault(definition.Name, MissingColor);
        }
    }

    public bool IsOpaque(BlockId id) => _isOpaque[id.Value];
    public Vector3 GetColor(BlockId id) => _colors[id.Value];
}
