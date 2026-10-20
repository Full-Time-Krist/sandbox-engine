using Shared.Blocks;

namespace Shared.Items;

/// <summary>Описание типа предмета: только то, что нужно логике. Внешний вид (иконки) описывает клиент.</summary>
/// <param name="Name">Постоянное имя вида <c>namespace:name</c>, например <c>base:stone</c>.</param>
/// <param name="MaxStackSize">Сколько штук помещается в один слот (64 для блоков).</param>
/// <param name="Block">
/// Какой блок предмет ставит в мир, или null, если предмет не блок (инструмент, еда). Предмет и блок —
/// разные сущности: у блока есть одноимённый предмет, но не каждый предмет ставится в мир.
/// </param>
/// <param name="Behavior">
/// Имя поведения (<c>namespace:name</c>) — что предмет делает при ЛКМ/ПКМ. Само поведение — код мода на сервере,
/// оно ищется по этому имени после загрузки модов.
/// </param>
/// <param name="Tool">Свойства инструмента или null, если предмет не инструмент.</param>
public sealed record ItemDefinition(ItemId Id, string Name, int MaxStackSize, BlockId? Block, string Behavior, ToolInfo? Tool = null)
{
    /// <summary>Какого моба ставит в мир (яйцо призыва, поле <c>spawnsMob</c>); <see cref="Mobs.MobId.None"/> — никакого.</summary>
    public Mobs.MobId SpawnsMob { get; init; }
}

/// <summary>Инструмент: какие блоки ломает быстрее (<paramref name="Type"/> совпадает с <see cref="BlockMining.Tool"/>), уровень и скорость.</summary>
/// <param name="Tier">Уровень: деревянный 1, каменный 2, медный 3. Блок с <see cref="BlockMining.ToolTier"/> выше уровня добычи не даёт.</param>
/// <param name="Speed">Во сколько раз быстрее руки ломает блоки своего вида.</param>
public sealed record ToolInfo(string Type, int Tier, float Speed);
