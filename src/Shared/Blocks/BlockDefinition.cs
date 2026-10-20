namespace Shared.Blocks;

/// <summary>Описание типа блока: только то, что нужно логике мира. Внешний вид описывает клиент.</summary>
/// <param name="Name">Постоянное имя вида <c>namespace:name</c>, например <c>base:stone</c>.</param>
/// <param name="IsOpaque">Полностью закрывает соседние грани. Грань рядом с непрозрачным блоком не рисуется.</param>
/// <param name="IsSolid">
/// Твёрдый: тела в него упираются. Это не то же, что непрозрачность: стекло прозрачное, но твёрдое,
/// а высокая трава прозрачная и проходимая. Пока твёрдый блок — всегда полный куб.
/// </param>
/// <param name="Mining">Как блок ломается: твёрдость, нужный инструмент и его уровень.</param>
/// <param name="Light">Сколько света блок излучает сам, 0–15 (<see cref="Lighting.LightEngine"/>); 0 — не светится.</param>
/// <param name="NeedsSupport">
/// Стоит только на блоке, который может его держать (<see cref="BlockRegistry.CanSupport"/>: твёрдый с полным верхом):
/// игрок ставит его лишь на такой, а когда опора перестаёт держать (её сломали или заменили), блок ломается с добычей. Факел.
/// </param>
public sealed record BlockDefinition(BlockId Id, string Name, bool IsOpaque, bool IsSolid, BlockMining Mining, byte Light = 0,
    bool NeedsSupport = false)
{
    /// <summary>Форма: короба внутри клетки (<see cref="BlockShape"/>). По ним блок рисуется, в него целятся и о него ударяются.</summary>
    public BlockShape Shape { get; init; } = BlockShape.Cube;

    /// <summary>
    /// Прозрачный блок, сквозь который свет не проходит (плита): свет соседей и солнце в него входят — он освещён, как
    /// воздух, — но дальше он отдаёт только собственное свечение, и столб солнца на нём обрывается. Под крышей из плит
    /// темно. У непрозрачного блока ничего не меняет: в него свет и так не входит.
    /// </summary>
    public bool BlocksLight { get; init; }
}

/// <summary>Как ломается блок (<see cref="Items.Mining"/> считает время и добычу).</summary>
/// <param name="Hardness">Секунды рукой, если блок добывается рукой; 0 — ломается сразу.</param>
/// <param name="Tool">Вид инструмента, который ускоряет ломание (<c>pickaxe</c>, <c>axe</c>, <c>shovel</c>…), или null.</param>
/// <param name="ToolTier">
/// Наименьший уровень инструмента вида <paramref name="Tool"/>, при котором блок что-то даёт; 0 — добывается и рукой.
/// Без него блок ломается медленнее и без добычи.
/// </param>
public sealed record BlockMining(float Hardness, string? Tool, int ToolTier)
{
    /// <summary>Блок, зарегистрированный кодом без своих правил: секунда рукой, без инструмента.</summary>
    public static readonly BlockMining Default = new(1, null, 0);
}
