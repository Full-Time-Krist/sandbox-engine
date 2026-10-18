using Shared.Modding;

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

    /// <summary>Контент игры и модов: пакеты из <c>Content/</c> и пакет каждого мода (его id, папка <c>content/</c>).</summary>
    /// <param name="mods">Моды в порядке загрузки; null — только контент игры.</param>
    /// <exception cref="ContentLoadException">Описание контента с ошибкой: сообщение называет файл.</exception>
    public static ContentRegistries Load(IReadOnlyList<ModInfo>? mods = null) =>
        ContentLoader.LoadPacks(ContentLoader.PacksIn(DefaultDirectory)
            .Concat((mods ?? []).Select(mod => new ContentPack(mod.Id, mod.ContentDirectory))));
}
