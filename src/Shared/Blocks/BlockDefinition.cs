namespace Shared.Blocks;

/// <summary>Описание типа блока: только то, что нужно логике мира. Внешний вид описывает клиент.</summary>
/// <param name="Name">Постоянное имя вида <c>namespace:name</c>, например <c>base:stone</c>.</param>
/// <param name="IsOpaque">Полностью закрывает соседние грани. Грань рядом с непрозрачным блоком не рисуется.</param>
/// <param name="IsSolid">
/// Твёрдый: тела в него упираются. Это не то же, что непрозрачность: стекло прозрачное, но твёрдое,
/// а высокая трава прозрачная и проходимая. Пока твёрдый блок — всегда полный куб.
/// </param>
public sealed record BlockDefinition(BlockId Id, string Name, bool IsOpaque, bool IsSolid);
