using System.Diagnostics;
using System.Numerics;
using Engine.Graphics;
using Shared;
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

    /// <summary>
    /// Сколько чанк, у которого есть соседи по граням, но не все 26 (<see cref="ClientWorld.IsSurrounded"/>), ждёт
    /// «успокоения» перед построением меша.
    /// Свет и затенение у края читают соседей по рёбрам и углам, и каждый пришедший сосед перестраивает меш. При входе
    /// сервер шлёт чанки от ближних к дальним, и соседи чанка на расстоянии d чанков приходят через ~0,3·d с: с ожиданием
    /// 0,25 с меши поверхности строились в полтора раза чаще, с 1 с — по разу, как до шага 8.3 (замер шага 8.3).
    /// Соседей за областью загрузки <see cref="ClientWorld.IsSurrounded"/> не ждёт, так что это запасной путь: на случай,
    /// если центр области у клиента на тик разошёлся с серверным и сосед, которого ждали, не пришёл.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1000);

    private readonly GL _gl;
    private readonly MeshBuildQueue _queue;
    private readonly Dictionary<ChunkPos, ChunkRenderMesh> _meshes = new();

    private readonly MeshVersions _versions = new();

    /// <summary>
    /// Чанки, которые нужно отправить в построение (не хватило времени кадра или ждут <see cref="SettleTime"/>),
    /// и когда каждый последний раз менялся (<see cref="Stopwatch.GetTimestamp"/>).
    /// </summary>
    private readonly Dictionary<ChunkPos, long> _waiting = [];
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

    public MeshUpdateStats Update(ClientWorld world, Vector3d cameraPosition)
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
        var now = Stopwatch.GetTimestamp();
        world.TakeDirty(_dirty);
        foreach (var position in _dirty)
            _waiting[position] = now;
        _dirty.Clear();
        if (_waiting.Count == 0)
            return;

        _ordered.Clear();
        _ordered.AddRange(_waiting.Keys);
        _ordered.Sort((a, b) => DistanceSquared(a, cameraChunk).CompareTo(DistanceSquared(b, cameraChunk)));

        foreach (var position in _ordered)
        {
            if (clock.Elapsed.TotalMilliseconds >= SubmitBudgetMs)
                break;
            var chunk = world.Chunks.Get(position);
            if (chunk is not null && !ReadyToMesh(world, position, Stopwatch.GetElapsedTime(_waiting[position], now)))
                continue;
            _waiting.Remove(position);

            if (chunk is null || chunk.IsEmpty)
            {
                // Чанк выгружен или пуст: меша у него нет. Удаляем старый и забываем версию, чтобы строящийся меш не
                // вернул его обратно. Пустой в построение не отправляем: соседи помечают его десятки раз за вход.
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

    /// <summary>
    /// Строить ли меш сейчас: чанк не менялся <see cref="SettleTime"/> (запасной путь) или у него есть все 26 соседей,
    /// какие могут прийти (меш больше не изменится от их прихода). Дешёвая проверка — первой.
    /// </summary>
    internal static bool ReadyToMesh(ClientWorld world, ChunkPos position, TimeSpan quiet) =>
        quiet >= SettleTime || world.IsSurrounded(position);

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

    /// <summary>
    /// Рисует меши, попавшие в пирамиду видимости, и возвращает их число. Шейдер чанков должен быть активен,
    /// ViewProjection (без позиции камеры) и текстуры выставлены.
    /// </summary>
    public int Draw(ShaderProgram shader, Vector3d cameraPosition, in Frustum frustum)
    {
        var drawn = 0;
        foreach (var (position, mesh) in _meshes)
        {
            // Смещение чанка от камеры считается в double и только потом становится float: так оно точное
            // (не дальше нескольких сотен блоков), даже если сами координаты — миллионы.
            var origin = position.Origin;
            var offset = (new Vector3d(origin.X, origin.Y, origin.Z) - cameraPosition).ToVector3();
            if (!frustum.Intersects(offset, offset + new Vector3(ChunkMath.Size)))
                continue;

            shader.SetUniform("u_Model", Matrix4x4.CreateTranslation(offset));
            mesh.Draw();
            drawn++;
        }
        return drawn;
    }

    public void Dispose()
    {
        _queue.Dispose();
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();
    }
}
