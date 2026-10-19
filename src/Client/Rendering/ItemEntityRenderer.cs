using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Graphics;
using Shared;
using Shared.Items;
using Shared.Voxels;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Вершина меша предмета на земле, 32 байта.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ItemVertex(Vector3 position, Vector2 uv, float face, float shade, float sprite)
{
    public readonly Vector3 Position = position;
    public readonly Vector2 Uv = uv;
    /// <summary>Какой слой экземпляра брать: 0 — верх, 1 — бок, 2 — низ (<see cref="ItemInstance.Layers"/>).</summary>
    public readonly float Face = face;
    public readonly float Shade = shade;
    /// <summary>1 — вершина плоской картинки, 0 — кубика.</summary>
    public readonly float Sprite = sprite;
}

/// <summary>Данные одного предмета для отрисовки — по одному на экземпляр (instancing), 40 байт.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ItemInstance(Vector3 offset, float yaw, Vector3 layers, float sprite, Vector2 light)
{
    /// <summary>Точка предмета относительно глаза (рендер относительно камеры).</summary>
    public readonly Vector3 Offset = offset;
    /// <summary>Поворот кубика вокруг вертикали, радианы. Картинка всегда повёрнута к камере.</summary>
    public readonly float Yaw = yaw;
    /// <summary>Слои массива текстур: верх, бок, низ кубика; у картинки — три раза её слой.</summary>
    public readonly Vector3 Layers = layers;
    public readonly float Sprite = sprite;
    /// <summary>Свет блока, где лежит предмет: небесный и блочный, 0–15.</summary>
    public readonly Vector2 Light = light;
}

/// <summary>Как выглядит предмет на земле: кубик со слоями граней блока или плоская картинка.</summary>
internal readonly record struct ItemLook(bool Sprite, Vector3 Layers)
{
    // Номера граней в BlockFace.All.
    private const int Side = 0;
    private const int Top = 2;
    private const int Bottom = 3;

    /// <summary>
    /// Как у иконок интерфейса (<c>ItemIcons</c>): своя картинка — картинка; предмет-блок без неё — кубик его текстур;
    /// остальное — картинка «нет текстуры» (слой 0).
    /// </summary>
    public static ItemLook For(ItemDefinition item, ItemVisuals visuals)
    {
        var icon = visuals.IconLayer(item.Id);
        if (icon != ItemVisuals.NoIcon || item.Block is not { } block)
        {
            float layer = Math.Max(icon, 0);
            return new ItemLook(true, new Vector3(layer));
        }
        return new ItemLook(false, new Vector3(
            visuals.Blocks.GetLayer(block, Top), visuals.Blocks.GetLayer(block, Side), visuals.Blocks.GetLayer(block, Bottom)));
    }
}

/// <summary>
/// Предметы на земле — все одним вызовом отрисовки. Меш один: кубик и квадрат картинки вместе; каждый экземпляр
/// выбирает в шейдере свою часть, а вершины другой части уводит за экран (нулевые треугольники отбрасываются до
/// растеризации). Тысяча предметов — один вызов, а не тысяча (урок <c>glDrawElements</c> этапа 1).
/// </summary>
internal sealed class ItemEntityRenderer : IDisposable
{
    /// <summary>Сторона кубика и картинки, в блоках. Картинка крупнее: она плоская, и издалека её хуже видно.</summary>
    public const float CubeSize = 0.25f;
    public const float SpriteSize = 0.4f;

    // Кубик медленно вращается и покачивается над землёй, как в Minecraft.
    private const float SpinPerSecond = 1.2f;
    private const float BobHeight = 0.06f;
    private const float BobPerSecond = 2.5f;
    private const float Hover = 0.08f;

    private readonly ShaderProgram _shader;
    private readonly ItemVisuals _visuals;
    private readonly ItemRegistry _items;
    private readonly GpuBuffer<ItemVertex> _vertices;
    private readonly GpuBuffer<ItemInstance> _instances;
    private readonly VertexArrayObject _vao;
    private readonly int _vertexCount;
    private readonly List<ItemInstance> _frame = [];

    public ItemEntityRenderer(GL gl, ShaderProgram shader, ItemVisuals visuals, ItemRegistry items)
    {
        _shader = shader;
        _visuals = visuals;
        _items = items;

        var vertices = BuildMesh();
        _vertexCount = vertices.Count;
        _vertices = new GpuBuffer<ItemVertex>(gl, CollectionsMarshal.AsSpan(vertices));
        _instances = new GpuBuffer<ItemInstance>(gl, ReadOnlySpan<ItemInstance>.Empty, BufferUsageARB.StreamDraw);

        _vao = new VertexArrayObject(gl);
        _vao.SetVertexBuffer(_vertices, binding: 0);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemVertex>(nameof(ItemVertex.Position)));
        _vao.SetAttribute(1, 2, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemVertex>(nameof(ItemVertex.Uv)));
        _vao.SetAttribute(2, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemVertex>(nameof(ItemVertex.Face)));
        _vao.SetAttribute(3, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemVertex>(nameof(ItemVertex.Shade)));
        _vao.SetAttribute(4, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemVertex>(nameof(ItemVertex.Sprite)));

        _vao.SetVertexBuffer(_instances, binding: 1);
        _vao.SetBindingDivisor(1, 1);
        _vao.SetAttribute(5, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemInstance>(nameof(ItemInstance.Offset)), binding: 1);
        _vao.SetAttribute(6, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemInstance>(nameof(ItemInstance.Yaw)), binding: 1);
        _vao.SetAttribute(7, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemInstance>(nameof(ItemInstance.Layers)), binding: 1);
        _vao.SetAttribute(8, 1, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemInstance>(nameof(ItemInstance.Sprite)), binding: 1);
        _vao.SetAttribute(9, 2, VertexAttribType.Float, (uint)Marshal.OffsetOf<ItemInstance>(nameof(ItemInstance.Light)), binding: 1);
    }

    /// <summary>Сколько предметов нарисовано в последнем кадре.</summary>
    public int Drawn { get; private set; }

    /// <summary>
    /// Кубик (6 граней по 2 треугольника) с центром основания в начале координат, затем квадрат картинки в плоскости
    /// «вправо — вверх» (её поворачивает к камере шейдер). Треугольники — против часовой стрелки снаружи, как у блоков.
    /// </summary>
    internal static List<ItemVertex> BuildMesh()
    {
        var vertices = new List<ItemVertex>(42);
        var center = new Vector3(0.5f, 0, 0.5f);
        foreach (var face in BlockFace.All)
        {
            var kind = face.Kind switch { FaceKind.Top => 0f, FaceKind.Side => 1f, _ => 2f };
            ItemVertex V(Vector3 corner, Vector2 uv) => new((corner - center) * CubeSize, uv, kind, face.Brightness, 0);
            vertices.AddRange([V(face.A, face.UvA), V(face.B, face.UvB), V(face.C, face.UvC),
                               V(face.A, face.UvA), V(face.C, face.UvC), V(face.D, face.UvD)]);
        }

        const float h = SpriteSize / 2;
        ItemVertex S(float right, float up, Vector2 uv) => new(new Vector3(right, up, 0), uv, 0, 1, 1);
        vertices.AddRange([S(-h, 0, new(0, 1)), S(h, 0, new(1, 1)), S(h, SpriteSize, new(1, 0)),
                           S(-h, 0, new(0, 1)), S(h, SpriteSize, new(1, 0)), S(-h, SpriteSize, new(0, 0))]);
        return vertices;
    }

    /// <param name="eye">Глаз камеры: смещения предметов считаются от него в double.</param>
    /// <param name="alpha">Доля пройденного тика — для интерполяции положений.</param>
    /// <param name="seconds">Время с запуска — для вращения и покачивания.</param>
    /// <param name="world">Откуда брать свет: предмет освещён светом блока, в котором лежит.</param>
    /// <param name="skyBrightness">Яркость неба, как у чанков (<c>u_SkyBrightness</c>).</param>
    public void Draw(Matrix4x4 viewProjection, Vector3d eye, ClientEntities entities, float alpha, double seconds,
        ClientWorld world, float skyBrightness)
    {
        _frame.Clear();
        foreach (var (id, entity) in entities.All)
        {
            if (entity.Item.IsEmpty || !_items.Contains(entity.Item.Item))
                continue;
            var look = ItemLook.For(_items.Get(entity.Item.Item), _visuals);
            // Фаза от номера: лежащие рядом предметы качаются вразнобой.
            var phase = id.Index * 1.7f;
            var bob = Hover + BobHeight * MathF.Sin((float)(seconds * BobPerSecond) + phase);
            var position = entity.At(alpha);
            var offset = (position - eye).ToVector3() + new Vector3(0, bob, 0);
            var yaw = (float)(seconds * SpinPerSecond) + phase;
            _frame.Add(new ItemInstance(offset, yaw, look.Layers, look.Sprite ? 1 : 0, LightOf(world, position)));
        }

        Drawn = _frame.Count;
        if (_frame.Count == 0)
            return;

        _instances.SetData(CollectionsMarshal.AsSpan(_frame), BufferUsageARB.StreamDraw);
        _shader.Use();
        _shader.SetUniform("u_ViewProjection", viewProjection);
        _shader.SetUniform("u_SkyBrightness", skyBrightness);
        _vao.DrawArraysInstanced(PrimitiveType.Triangles, _vertexCount, _frame.Count);
    }

    /// <summary>
    /// Свет блока, где лежит предмет. Точка тела — низ короба, на опоре она ровно на верхней грани блока под ним;
    /// погрешность могла бы опустить её в непрозрачный блок (там темно), поэтому берём чуть выше.
    /// </summary>
    internal static Vector2 LightOf(ClientWorld world, Vector3d position)
    {
        var packed = world.LightAt(BlockPos.Containing(position + new Vector3d(0, 0.1, 0)));
        return new Vector2(packed >> 4, packed & 0x0F);
    }

    public void Dispose()
    {
        _vao.Dispose();
        _instances.Dispose();
        _vertices.Dispose();
    }
}
