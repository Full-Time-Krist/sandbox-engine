using Shared.Blocks;

namespace Shared.Items;

/// <summary>
/// Сколько ломается блок и даёт ли он добычу — в зависимости от предмета в руке. Одна формула для сервера (он решает)
/// и клиента (он рисует трещины), поэтому обе стороны видят одинаковое время.
/// </summary>
/// <remarks>
/// Подходящий инструмент (вид совпадает с <see cref="BlockMining.Tool"/>) делит время на свою скорость. Если блоку нужен
/// инструмент уровня не ниже <see cref="BlockMining.ToolTier"/>, а его нет, блок ломается в <see cref="NoHarvestSlowdown"/>
/// раз медленнее и ничего не даёт — как в Minecraft (камень рукой — 7,5 с и пусто, деревянной киркой — около секунды).
/// </remarks>
public static class Mining
{
    public const float NoHarvestSlowdown = 10f / 3f;

    public static bool CanHarvest(BlockMining block, ToolInfo? tool) =>
        block.ToolTier == 0 || (tool is { } t && t.Type == block.Tool && t.Tier >= block.ToolTier);

    public static float BreakSeconds(BlockMining block, ToolInfo? tool)
    {
        var speed = tool is { } t && t.Type == block.Tool ? t.Speed : 1f;
        var seconds = block.Hardness / speed;
        return CanHarvest(block, tool) ? seconds : seconds * NoHarvestSlowdown;
    }

    /// <summary>Время ломания в тиках сервера (не меньше одного у блока с ненулевой твёрдостью); 0 — ломается сразу.</summary>
    public static int BreakTicks(BlockMining block, ToolInfo? tool) =>
        (int)Math.Ceiling(BreakSeconds(block, tool) * GameTime.TicksPerSecond - 1e-4);
}
