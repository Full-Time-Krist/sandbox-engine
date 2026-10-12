namespace Shared.Blocks;

/// <summary>
/// Базовые блоки игры. Временно регистрируются в коде; на этапе 4 их место займут JSON-файлы встроенного мода.
/// </summary>
public sealed class BaseBlocks
{
    public BlockId Stone { get; }
    public BlockId Dirt { get; }
    public BlockId Grass { get; }
    public BlockId Sand { get; }
    public BlockId Log { get; }
    public BlockId Planks { get; }

    private BaseBlocks(BlockRegistry registry)
    {
        Stone = registry.Register("base:stone", isOpaque: true).Id;
        Dirt = registry.Register("base:dirt", isOpaque: true).Id;
        Grass = registry.Register("base:grass", isOpaque: true).Id;
        Sand = registry.Register("base:sand", isOpaque: true).Id;
        Log = registry.Register("base:log", isOpaque: true).Id;
        Planks = registry.Register("base:planks", isOpaque: true).Id;
    }

    public static BaseBlocks Register(BlockRegistry registry) => new(registry);
}
