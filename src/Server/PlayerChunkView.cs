using Shared.Voxels;

namespace Server;

/// <summary>
/// Какие чанки нужны одному игроку и какие уже отправлены его клиенту.
/// Без сети и потоков: сервер спрашивает, что отправить или выгрузить, и сам шлёт сообщения.
/// </summary>
/// <remarks>
/// <para>
/// Радиусов три. Дальность прорисовки — сколько чанков игрок видит. Загружается на кольцо больше:
/// клиент строит меш чанка, только когда пришли все его соседи, иначе крайнее кольцо осталось бы без мешей.
/// Выгружается ещё на <see cref="UnloadMargin"/> дальше: если игрок топчется у границы чанка,
/// чанки на краю не будут выгружаться и тут же загружаться обратно (гистерезис).
/// </para>
/// </remarks>
internal sealed class PlayerChunkView
{
    /// <summary>На сколько колец дальше загрузки чанк выгружается.</summary>
    public const int UnloadMargin = 2;

    private readonly ChunkPos[] _offsets;
    private readonly HashSet<ChunkPos> _sent = [];

    /// <param name="viewDistance">Дальность прорисовки по горизонтали, в чанках.</param>
    /// <param name="verticalDistance">Дальность прорисовки по высоте, в чанках вверх и вниз.</param>
    public PlayerChunkView(int viewDistance, int verticalDistance, ChunkPos center)
    {
        LoadArea = new ChunkArea(viewDistance + 1, verticalDistance + 1);
        KeepArea = new ChunkArea(LoadArea.Horizontal + UnloadMargin, LoadArea.Vertical + UnloadMargin);
        _offsets = LoadArea.SortedOffsets();
        Center = center;
    }

    /// <summary>Чанки, которые должны быть у клиента.</summary>
    public ChunkArea LoadArea { get; }

    /// <summary>Чанки, которые не выгружаются, даже если уже не входят в <see cref="LoadArea"/>.</summary>
    public ChunkArea KeepArea { get; }

    /// <summary>Чанк, в котором стоит игрок.</summary>
    public ChunkPos Center { get; private set; }

    public int SentCount => _sent.Count;

    /// <summary>Отправлены все чанки области загрузки.</summary>
    public bool IsComplete => !Missing().Any();

    public int AreaSize => _offsets.Length;

    public bool WasSent(ChunkPos position) => _sent.Contains(position);

    /// <summary>Нужен ли чанк игроку: сервер не выгружает такой чанк из памяти.</summary>
    public bool Keeps(ChunkPos position) => KeepArea.Contains(Center, position);

    /// <summary>
    /// Игрок перешёл в другой чанк. Возвращает отправленные чанки, которые теперь за пределами <see cref="KeepArea"/>:
    /// их нужно выгрузить у клиента. Они сразу считаются не отправленными.
    /// </summary>
    public List<ChunkPos> MoveTo(ChunkPos center)
    {
        var unload = new List<ChunkPos>();
        if (center == Center)
            return unload;

        Center = center;
        foreach (var position in _sent)
        {
            if (!KeepArea.Contains(center, position))
                unload.Add(position);
        }

        foreach (var position in unload)
            _sent.Remove(position);
        return unload;
    }

    /// <summary>Чанки области загрузки, которых ещё нет у клиента, от ближних к дальним.</summary>
    public IEnumerable<ChunkPos> Missing()
    {
        foreach (var position in Wanted())
        {
            if (!_sent.Contains(position))
                yield return position;
        }
    }

    public void MarkSent(ChunkPos position) => _sent.Add(position);

    private IEnumerable<ChunkPos> Wanted()
    {
        foreach (var offset in _offsets)
            yield return new ChunkPos(Center.X + offset.X, Center.Y + offset.Y, Center.Z + offset.Z);
    }
}
