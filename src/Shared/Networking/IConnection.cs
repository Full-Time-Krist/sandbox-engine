using System.Diagnostics.CodeAnalysis;

namespace Shared.Networking;

/// <summary>
/// Двусторонний канал сообщений. Клиент и сервер общаются только через него,
/// поэтому для мультиплеера достаточно новой реализации поверх сети.
/// </summary>
/// <typeparam name="TSend">Сообщения, которые эта сторона отправляет.</typeparam>
/// <typeparam name="TReceive">Сообщения, которые эта сторона получает.</typeparam>
public interface IConnection<in TSend, TReceive>
{
    void Send(TSend message);

    /// <summary>Достаёт следующее полученное сообщение, если оно есть. Не блокирует.</summary>
    bool TryReceive([MaybeNullWhen(false)] out TReceive message);
}
