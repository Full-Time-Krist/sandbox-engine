using System.Diagnostics;
using System.Numerics;
using Engine.Graphics;
using Shared.Voxels;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Сводка одного кадра: сколько мешей загружено, время основного потока и самое долгое построение в фоне.</summary>
internal readonly record struct MeshUpdateStats(int Chunks, int Quads, double MainThreadMs, double MaxBuildMs);

/// <summary>Сводка построения мешей за несколько кадров.</summary>
internal record struct MeshStatsWindow(int Chunks, double MainThreadMs, double MaxFrameMs, double MaxBuildMs)
{
    public void Add(MeshUpdateStats frame)
    {
        Chunks += frame.Chunks;
        MainThreadMs += frame.MainThreadMs;
        MaxFrameMs = Math.Max(MaxFrameMs, frame.MainThreadMs);
        MaxBuildMs = Math.Max(MaxBuildMs, frame.MaxBuildMs);
    }

    public override readonly string ToString() =>
        $"меши: {Chunks}, основной поток {MainThreadMs:F0} мс (за кадр до {MaxFrameMs:F1} мс), построение в фоне до {MaxBuildMs:F1} мс";
}

/// <summary>
/// Держит меши чанков в видеопамяти. Меши строятся в фоновых потоках (<see cref="MeshBuildQueue"/>),
/// а здесь, в основном потоке, остаётся снять снимки «грязных» чанков и загрузить готовые меши.
/// </summary>
/// <remarks>
/// <para>
/// Обе части ограничены временем на кадр: при входе в мир «грязных» чанков полторы тысячи,
/// и даже снимки (десятки микросекунд на чанк) заняли бы один длинный кадр. Что не уложилось,
/// ждёт следующего кадра; ближние к камере чанки идут первыми.
/// </para>
/// <para>
/// Пока чанк строится, он может снова измениться или выгрузиться. Каждой отправке присваивается номер версии;
/// готовый меш принимается, только если его версия — последняя отправленная для этого чанка.
/// Старый меш рисуется, пока не готов новый, поэтому сломанный блок не мигает дырой.
/// </para>
/// </remarks>
internal sealed class ChunkRenderer : IDisposable
{
    /// <summary>Сколько миллисекунд кадра можно тратить на снимки и, отдельно, на загрузку мешей.</summary>
    public const double SubmitBudgetMs = 2;
    public const double UploadBudgetMs = 2;

    private readonly GL _gl;
    private readonly MeshBuildQueue _queue;
    private readonly Dictionary<ChunkPos, ChunkRenderMesh> _meshes = new();

    private readonly MeshVersions _versions = new();

    /// <summary>Чанки, которые нужно отправить в построение, но не хватило времени кадра.</summary>
    private readonly HashSet<ChunkPos> _waiting = [];
    private readonly List<ChunkPos> _dirty = [];
    private readonly List<ChunkPos> _ordered = [];

    public ChunkRenderer(GL gl, ChunkMesher mesher, int workerCount)
    {
        _gl = gl;
        _queue = new MeshBuildQueue(mesher, workerCount);
    }

    public int MeshCount => _meshes.Count;

    /// <summary>Сколько чанков ждёт отправки или строится.</summary>
    public int Pending => _waiting.Count + _versions.Count;

    public MeshUpdateStats Update(ClientWorld world, Vector3 cameraPosition)
    {
        var clock = Stopwatch.StartNew();
        SubmitDirty(world, BlockPos.Containing(cameraPosition).Chunk, clock);

        var uploadStart = clock.Elapsed.TotalMilliseconds;
        var uploaded = 0;
        var quads = 0;
        var maxBuild = 0.0;
        while (clock.Elapsed.TotalMilliseconds - uploadStart < UploadBudgetMs && _queue.TryTakeCompleted(out var result))
        {
            if (Accept(result))
            {
                uploaded++;
                quads += result.Data.QuadCount;
                maxBuild = Math.Max(maxBuild, result.BuildMs);
            }
            _queue.Return(result.Data);
        }

        return new MeshUpdateStats(uploaded, quads, clock.Elapsed.TotalMilliseconds, maxBuild);
    }

    private void SubmitDirty(ClientWorld world, ChunkPos cameraChunk, Stopwatch clock)
    {
        world.TakeDirty(_dirty);
        foreach (var position in _dirty)
            _waiting.Add(position);
        _dirty.Clear();
        if (_waiting.Count == 0)
            return;

        _ordered.Clear();
        _ordered.AddRange(_waiting);
        _ordered.Sort((a, b) => DistanceSquared(a, cameraChunk).CompareTo(DistanceSquared(b, cameraChunk)));

        foreach (var position in _ordered)
        {
            if (clock.Elapsed.TotalMilliseconds >= SubmitBudgetMs)
                break;
            _waiting.Remove(position);

            var chunk = world.Chunks.Get(position);
            if (chunk is null)
            {
                // Чанк выгружен: удаляем меш и забываем версию, чтобы строящийся меш не вернул его обратно.
                if (_meshes.Remove(position, out var removed))
                    removed.Dispose();
                _versions.Forget(position);
                continue;
            }

            // Пока не пришли все соседи, меш не строим: чанк снова станет «грязным», когда придёт последний сосед.
            if (!world.CanMesh(position))
                continue;

            var version = _versions.Next(position);
            var snapshot = _queue.RentSnapshot();
            snapshot.Capture(chunk, world.GetNeighbors(position));
            _queue.Submit(snapshot, version);
        }
    }

    /// <summary>Загружает готовый меш в видеопамять, если он не устарел. Возвращает, принят ли он.</summary>
    private bool Accept(MeshBuildResult result)
    {
        if (!_versions.TryAccept(result.Position, result.Version))
            return false;

        if (_meshes.Remove(result.Position, out var old))
            old.Dispose();
        // Пустой меш (воздух или блоки, со всех сторон закрытые соседями) не рисуем и не храним.
        if (!result.Data.IsEmpty)
            _meshes.Add(result.Position, new ChunkRenderMesh(_gl, result.Data));
        return true;
    }

    private static int DistanceSquared(ChunkPos a, ChunkPos b)
    {
        int dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Рисует все меши. Шейдер чанков должен быть активен, ViewProjection и текстуры выставлены.</summary>
    public void Draw(ShaderProgram shader)
    {
        foreach (var (position, mesh) in _meshes)
        {
            var origin = position.Origin;
            shader.SetUniform("u_Model", Matrix4x4.CreateTranslation(origin.X, origin.Y, origin.Z));
            mesh.Draw();
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();
    }
}
