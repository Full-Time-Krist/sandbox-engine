namespace Shared.Modding;

/// <summary>
/// Мод из папки модов: прочитанный и проверенный <c>mod.json</c>. Id мода — это и имя его папки, и его пакет контента,
/// и пространство имён его блоков, предметов и поведений.
/// </summary>
/// <param name="Dependencies">Моды, без которых этот не работает: id → наименьшая подходящая версия.</param>
/// <param name="Directory">Папка мода (полный путь).</param>
/// <param name="AssemblyFile">Имя DLL с кодом мода в его папке, или null у мода только из данных.</param>
public sealed record ModInfo(string Id, string Name, Version Version, IReadOnlyDictionary<string, Version> Dependencies,
    string Directory, string? AssemblyFile)
{
    /// <summary>JSON-описания блоков и предметов мода: его пакет контента (папки может не быть).</summary>
    public string ContentDirectory => Path.Combine(Directory, "content");

    /// <summary>Текстуры блоков мода: <c>textures/blocks/&lt;имя&gt;.png</c>.</summary>
    public string BlockTextureDirectory => Path.Combine(Directory, "textures", "blocks");

    /// <summary>Картинки предметов мода: <c>textures/items/&lt;имя&gt;.png</c>.</summary>
    public string ItemTextureDirectory => Path.Combine(Directory, "textures", "items");

    /// <summary>Текстуры мобов мода: <c>textures/mobs/&lt;имя&gt;.png</c>, 64×64 (с шага 9.2).</summary>
    public string MobTextureDirectory => Path.Combine(Directory, "textures", "mobs");

    /// <summary>Модели мобов мода: <c>models/&lt;имя&gt;.json</c> (с шага 9.2).</summary>
    public string ModelDirectory => Path.Combine(Directory, "models");

    public string? AssemblyPath => AssemblyFile is null ? null : Path.Combine(Directory, AssemblyFile);

    public override string ToString() => $"{Id} {Version}";
}

/// <summary>Ошибка мода: манифест, зависимости, сборка, загрузка. Сообщение называет мод.</summary>
public sealed class ModLoadException : Exception
{
    public ModLoadException(string message) : base(message) { }
    public ModLoadException(string message, Exception innerException) : base(message, innerException) { }
}
