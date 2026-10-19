namespace Client.Ui;

/// <summary>Причина смерти для игрока: сервер присылает имя причины (<c>base:fall</c>), текст подбирает клиент.</summary>
internal static class DeathCauses
{
    public static string Text(string cause) => cause switch
    {
        "base:fall" => "Разбился, упав с высоты",
        _ => "Погиб",
    };
}
