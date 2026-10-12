using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Shared.Networking;

/// <summary>
/// Канал в памяти для одиночной игры: две очереди, по одной в каждую сторону.
/// </summary>
/// <remarks>
/// Очереди потокобезопасные, поэтому сервер можно будет перенести в отдельный поток без изменений здесь.
/// Сообщения передаются по ссылке, без копирования, поэтому они обязаны быть неизменяемыми (record с init-свойствами).
/// Иначе одна сторона могла бы поменять объект, который уже читает другая.
/// </remarks>
public static class LocalConnection
{
    public static (IConnection<TA, TB> A, IConnection<TB, TA> B) CreatePair<TA, TB>()
    {
        var aToB = new ConcurrentQueue<TA>();
        var bToA = new ConcurrentQueue<TB>();
        return (new Endpoint<TA, TB>(aToB, bToA), new Endpoint<TB, TA>(bToA, aToB));
    }

    private sealed class Endpoint<TSend, TReceive>(ConcurrentQueue<TSend> outgoing, ConcurrentQueue<TReceive> incoming)
        : IConnection<TSend, TReceive>
    {
        public void Send(TSend message) => outgoing.Enqueue(message);

        public bool TryReceive([MaybeNullWhen(false)] out TReceive message) => incoming.TryDequeue(out message);
    }
}
