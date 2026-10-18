namespace Shared.Blocks;

/// <summary>
/// Ванильные блоки, на которые опирается код (генератор мира, тесты). Сами блоки описаны в JSON
/// (<c>content/base/blocks</c>); здесь только их id, найденные в реестре по постоянным именам.
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
        Stone = registry.Get("base:stone").Id;
        Dirt = registry.Get("base:dirt").Id;
        Grass = registry.Get("base:grass").Id;
        Sand = registry.Get("base:sand").Id;
        Log = registry.Get("base:log").Id;
        Planks = registry.Get("base:planks").Id;
    }

    /// <exception cref="KeyNotFoundException">В реестре нет одного из ванильных блоков: контент неполный.</exception>
    public static BaseBlocks From(BlockRegistry registry) => new(registry);
}
