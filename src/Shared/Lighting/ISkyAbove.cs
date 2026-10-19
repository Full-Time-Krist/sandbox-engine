namespace Shared.Lighting;

/// <summary>
/// Что над загруженным миром: доходит ли солнце сверху в столбец, если чанк над ним не загружен.
/// Чанки кубические, и над верхним загруженным может быть что угодно — ответ даёт тот, кто знает мир целиком
/// (на сервере — генератор по естественному рельефу).
/// </summary>
public interface ISkyAbove
{
    /// <summary>Нет ли в столбце (<paramref name="x"/>, <paramref name="z"/>) непрозрачных блоков на высоте <paramref name="y"/> и выше.</summary>
    bool IsOpen(int x, int z, int y);
}

/// <summary>Над миром всегда открытое небо: для тестов и миров без рельефа.</summary>
public sealed class OpenSky : ISkyAbove
{
    public static readonly OpenSky Instance = new();

    public bool IsOpen(int x, int z, int y) => true;
}
