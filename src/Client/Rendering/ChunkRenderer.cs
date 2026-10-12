using System.Diagnostics;
using System.Numerics;
using Engine.Graphics;
using Shared.Voxels;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Сводка одного обновления мешей, для замера скорости.</summary>
internal readonly record struct MeshUpdateStats(int Chunks, int Quads, double TotalMs, double MaxMs);

/// <summary>Сводка построения мешей за несколько кадров: сколько, сколько времени и самый тяжёлый кадр.</summary>
internal record struct MeshStatsWindow(int Chunks, double TotalMs, double MaxFrameMs)
{
    public void Add(MeshUpdateStats frame)
    {
        Chunks += frame.Chunks;
        TotalMs += frame.TotalMs;
        MaxFrameMs = Math.Max(MaxFrameMs, frame.TotalMs);
    }

    public override readonly string ToString() =>
        $"меши: {Chunks} за {TotalMs:F0} мс, самый долгий кадр мешей {MaxFrameMs:F1} мс";
}

/// <summary>Держит меши чанков в видеопамяти: перестраивает «грязные» и рисует все.</summary>
internal sealed class ChunkRenderer(GL gl, ChunkMesher mesher) : IDisposable
{
    private readonly Dictionary<ChunkPos, ChunkRenderMesh> _meshes = new();
    private readonly ChunkMeshData _data = new();
    private readonly List<ChunkPos> _dirty = [];

    public int MeshCount => _meshes.Count;

    /// <summary>
    /// Перестраивает меши всех «грязных» чанков. Пока синхронно, в основном потоке;
    /// на этапе 2 построение уйдёт в фоновые потоки, а здесь останется только загрузка в видеопамять.
    /// </summary>
    public MeshUpdateStats Update(ClientWorld world)
    {
        world.TakeDirty(_dirty);
        if (_dirty.Count == 0)
            return default;

        var built = 0;
        var quads = 0;
        var maxMs = 0.0;
        var total = Stopwatch.StartNew();

        foreach (var position in _dirty)
        {
            var started = total.Elapsed;

            var chunk = world.Chunks.Get(position);
            if (chunk is null)
            {
                // Чанк выгружен — удаляем и меш.
                if (_meshes.Remove(position, out var removed))
                    removed.Dispose();
                continue;
            }

            // Пока не пришли все соседи, оставляем меш как есть (обычно его ещё нет): чанк снова станет «грязным»,
            // когда придёт последний сосед (ClientWorld.AddChunk помечает соседей).
            if (!world.CanMesh(position))
                continue;

            if (_meshes.Remove(position, out var old))
                old.Dispose();

            mesher.Build(chunk, world.GetNeighbors(position), _data);
            // Пустой меш (воздух или блоки, со всех сторон закрытые соседями) не рисуем и не храним.
            if (!_data.IsEmpty)
                _meshes.Add(position, new ChunkRenderMesh(gl, _data));

            built++;
            quads += _data.QuadCount;
            maxMs = Math.Max(maxMs, (total.Elapsed - started).TotalMilliseconds);
        }

        var stats = new MeshUpdateStats(built, quads, total.Elapsed.TotalMilliseconds, maxMs);
        _dirty.Clear();
        return stats;
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
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();
    }
}
