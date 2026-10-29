using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Graphics;
using Shared;
using Shared.Mobs;
using Shared.Protocol;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Одна часть одного моба для отрисовки — экземпляр (instancing), 80 байт.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct MobPartInstance(Matrix4x4 model, Vector2 light, float flash, float layer)
{
    /// <summary>Поза части со смещением от глаза (рендер относительно камеры): из точки поворота части — в мир.</summary>
    public readonly Matrix4x4 Model = model;
    /// <summary>Свет блока, где стоит моб: небесный и блочный, 0–15.</summary>
    public readonly Vector2 Light = light;
    /// <summary>Сила красной вспышки после удара, 0–1.</summary>
    public readonly float Flash = flash;
    /// <summary>Слой текстуры в массиве текстур мобов.</summary>
    public readonly float Layer = layer;
}

/// <summary>
/// Мобы — модели из коробов (<see cref="MobModel"/>). Меши всех частей всех видов лежат подряд в одном буфере вершин;
/// каждая часть каждого моба — экземпляр со своей матрицей позы. За кадр — по вызову на вид части (у зомби их 6),
/// сколько бы мобов ни было: сотня зомби — те же 6 вызовов, а не 600.
/// </summary>
internal sealed class MobRenderer : IDisposable
{
    private readonly ShaderProgram _shader;
    private readonly MobRegistry _mobs;
    private readonly MobVisuals _visuals;
    private readonly GpuBuffer<MobVertex> _vertices;
    private readonly GpuBuffer<MobPartInstance> _instances;
    private readonly VertexArrayObject _vao;

    /// <summary>Первая часть вида моба в <see cref="_ranges"/> — по <c>MobId.Value</c>.</summary>
    private readonly int[] _firstPart;

    /// <summary>Вершины каждой части каждого вида в общем буфере.</summary>
    private readonly (int First, int Count)[] _ranges;

    /// <summary>Экземпляры за кадр — по частям (индекс как у <see cref="_ranges"/>), потом подряд в <see cref="_frame"/>.</summary>
    private readonly List<MobPartInstance>[] _byPart;
    private readonly List<MobPartInstance> _frame = [];
    private readonly Matrix4x4[] _pose = new Matrix4x4[MobModel.MaxParts];

    public MobRenderer(GL gl, ShaderProgram shader, MobRegistry mobs, MobVisuals visuals)
    {
        _shader = shader;
        _mobs = mobs;
        _visuals = visuals;

        var vertices = new List<MobVertex>();
        var ranges = new List<(int, int)>();
        _firstPart = new int[mobs.Count + 1];
        foreach (var mob in mobs.All)
        {
            _firstPart[mob.Id.Value] = ranges.Count;
            foreach (var part in visuals.Model(mob.Id).Parts)
            {
                var mesh = MobPose.BuildMesh(part);
                ranges.Add((vertices.Count, mesh.Length));
                vertices.AddRange(mesh);
            }
        }
        _ranges = [.. ranges];
        _byPart = [.. _ranges.Select(_ => new List<MobPartInstance>())];

        // Буфер вершин не бывает пустым (мобов в игре может и не быть): GL не любит буферы нулевого размера.
        if (vertices.Count == 0)
            vertices.Add(default);
        _vertices = new GpuBuffer<MobVertex>(gl, CollectionsMarshal.AsSpan(vertices));
        _instances = new GpuBuffer<MobPartInstance>(gl, ReadOnlySpan<MobPartInstance>.Empty, BufferUsageARB.StreamDraw);

        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_vertices, binding: 0);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobVertex>(nameof(MobVertex.Position)));
        _vao.SetAttribute(1, 2, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobVertex>(nameof(MobVertex.Uv)));
        _vao.SetAttribute(2, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobVertex>(nameof(MobVertex.Normal)));

        _vao.SetVertexBuffer(_instances, binding: 1);
        _vao.SetBindingDivisor(1, 1);
        // mat4 в шейдере — четыре атрибута подряд (locations 3–6), по vec4 на строку матрицы System.Numerics: в GLSL
        // это столбцы, поэтому матрица доходит без транспонирования, как в uniform'ах.
        var model = (uint)Marshal.OffsetOf<MobPartInstance>(nameof(MobPartInstance.Model));
        for (uint row = 0; row < 4; row++)
            _vao.SetAttribute(3 + row, 4, VertexAttribType.Float, model + row * 16, binding: 1);
        _vao.SetAttribute(7, 2, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobPartInstance>(nameof(MobPartInstance.Light)), binding: 1);
        _vao.SetAttribute(8, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobPartInstance>(nameof(MobPartInstance.Flash)), binding: 1);
        _vao.SetAttribute(9, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<MobPartInstance>(nameof(MobPartInstance.Layer)), binding: 1);
    }

    /// <summary>Сколько мобов нарисовано в последнем кадре.</summary>
    public int Drawn { get; private set; }

    /// <param name="eye">Глаз камеры: смещения мобов считаются от него в double.</param>
    /// <param name="alpha">Доля пройденного тика — для интерполяции положения, поворотов и шага.</param>
    /// <param name="seconds">Часы кадра — для вспышки после удара (<see cref="ClientEntity.HurtAt"/>).</param>
    /// <param name="world">Откуда брать свет: моб освещён светом блока, где стоит (как предмет на земле).</param>
    public void Draw(Matrix4x4 viewProjection, Vector3d eye, ClientEntities entities, float alpha, double seconds,
        ClientWorld world, in Atmosphere atmosphere, TextureArray textures)
    {
        foreach (var list in _byPart)
            list.Clear();

        var drawn = 0;
        foreach (var (_, entity) in entities.All)
        {
            if (entity.Kind != EntityKind.Mob || !_mobs.Contains(entity.Mob))
                continue;
            var model = _visuals.Model(entity.Mob);
            var position = entity.At(alpha);
            var offset = Matrix4x4.CreateTranslation((position - eye).ToVector3());
            MobPose.Compute(model, new MobPoseState(entity.YawAt(alpha), entity.HeadTurnAt(alpha), entity.WalkAt(alpha), entity.SwingAt(alpha)),
                _pose);
            var light = ItemEntityRenderer.LightOf(world, position);
            var flash = MobPose.Flash(seconds - entity.HurtAt);
            var layer = _visuals.Layer(entity.Mob);
            var first = _firstPart[entity.Mob.Value];
            for (var i = 0; i < model.Parts.Length; i++)
                _byPart[first + i].Add(new MobPartInstance(_pose[i] * offset, light, flash, layer));
            drawn++;
        }

        Drawn = drawn;
        if (drawn == 0)
            return;

        _frame.Clear();
        foreach (var list in _byPart)
            _frame.AddRange(list);
        _instances.SetData(CollectionsMarshal.AsSpan(_frame), BufferUsageARB.StreamDraw);

        _shader.Use();
        _shader.SetUniform("u_ViewProjection", viewProjection);
        atmosphere.Apply(_shader);
        textures.Bind(1);
        var baseInstance = 0;
        for (var part = 0; part < _ranges.Length; part++)
        {
            var count = _byPart[part].Count;
            if (count == 0)
                continue;
            _vao.DrawArraysInstanced(PrimitiveType.Triangles, _ranges[part].First, _ranges[part].Count, count, baseInstance);
            baseInstance += count;
        }
    }

    public void Dispose()
    {
        _vao.Dispose();
        _instances.Dispose();
        _vertices.Dispose();
    }
}
