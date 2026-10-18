namespace Shared.Content;

/// <summary>
/// Контент игры: все пакеты из папки <see cref="DirectoryName"/> рядом с exe (ванильный — <c>Content/base</c>).
/// Сервер и клиент грузят его одинаково, поэтому их реестры совпадают (клиент сверяет их при подключении).
/// </summary>
/// <remarks>
/// Файлы лежат в репозитории в <c>content/</c> и копируются при сборке проекта Shared, а оттуда — в вывод
/// всех проектов, которые на него ссылаются, включая тесты.
/// </remarks>
public static class GameContent
{
    public const string DirectoryName = "Content";

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    /// <exception cref="ContentLoadException">Описание контента с ошибкой: сообщение называет файл.</exception>
    public static ContentRegistries Load() => ContentLoader.LoadDirectory(DefaultDirectory);
}
