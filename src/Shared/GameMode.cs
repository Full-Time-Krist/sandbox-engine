namespace Shared;

/// <summary>Режим игры — свойство игрока, хранится в сохранении.</summary>
public enum GameMode
{
    /// <summary>Выживание: сломанный блок кладётся в инвентарь, поставленный тратится, летать нельзя.</summary>
    Survival,

    /// <summary>Творческий: блоки бесконечные (ломание ничего не даёт, установка не тратит), можно летать.</summary>
    Creative,
}

/// <summary>Имена режимов для командной строки и сохранения.</summary>
public static class GameModes
{
    /// <summary>
    /// Имя режима: <c>survival</c> или <c>creative</c>. В сохранении хранится имя, а не номер: порядок
    /// в enum может поменяться, а записанные миры должны читаться по-прежнему.
    /// </summary>
    public static string ToName(this GameMode mode) => mode switch
    {
        GameMode.Survival => "survival",
        GameMode.Creative => "creative",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Неизвестный режим"),
    };

    /// <summary>Режим по имени (<see cref="ToName"/>). Числа и другие написания не принимаются.</summary>
    public static bool TryParse(string? name, out GameMode mode)
    {
        foreach (var candidate in Enum.GetValues<GameMode>())
        {
            if (candidate.ToName() == name)
            {
                mode = candidate;
                return true;
            }
        }
        mode = default;
        return false;
    }

    /// <summary>Есть ли такой режим. Номер режима может прийти от клиента, и он может быть любым.</summary>
    public static bool IsDefined(this GameMode mode) => Enum.IsDefined(mode);
}
