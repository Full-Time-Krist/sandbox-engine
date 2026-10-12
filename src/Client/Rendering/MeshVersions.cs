using Shared.Voxels;

namespace Client.Rendering;

/// <summary>
/// Версии мешей, которые строятся в фоне. Пока чанк строится, он может снова измениться (его отправят ещё раз)
/// или выгрузиться. Готовый меш принимается, только если он построен по последней отправке этого чанка.
/// </summary>
/// <remarks>Отдельно от <see cref="ChunkRenderer"/>, чтобы проверять эту логику тестами без OpenGL.</remarks>
internal sealed class MeshVersions
{
    private readonly Dictionary<ChunkPos, int> _latest = new();
    private int _next;

    /// <summary>Сколько чанков строится сейчас.</summary>
    public int Count => _latest.Count;

    /// <summary>Новая версия для отправки чанка в построение. Все прежние версии этого чанка становятся устаревшими.</summary>
    public int Next(ChunkPos position)
    {
        var version = ++_next;
        _latest[position] = version;
        return version;
    }

    /// <summary>Принять ли готовый меш. Принятый меш завершает построение чанка.</summary>
    public bool TryAccept(ChunkPos position, int version)
    {
        if (!_latest.TryGetValue(position, out var latest) || latest != version)
            return false;

        _latest.Remove(position);
        return true;
    }

    /// <summary>Чанк выгружен: любой его строящийся меш будет отброшен.</summary>
    public void Forget(ChunkPos position) => _latest.Remove(position);
}
