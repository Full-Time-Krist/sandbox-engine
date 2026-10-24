using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Graphics;
using Shared;
using Shared.Items;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>Один вызов для всех предметов: неизменный куб и буфер экземпляров.</summary>
internal sealed class ItemEntityRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly VertexArrayObject _vao;
    private readonly GpuBuffer<ChunkVertex> _vertices;
    private readonly GpuBuffer<ItemInstance> _instances;
    private readonly List<ItemInstance> _data = [];
    private readonly ItemRegistry _items;
    private readonly ItemVisuals _visuals;

    public ItemEntityRenderer(GL gl, ItemRegistry items, ItemVisuals visuals, string shaderPath)
    {
        _gl = gl;
        _items = items;
        _visuals = visuals;
        _shader = ShaderProgram.FromFiles(gl, shaderPath);
        _shader.SetUniform("u_Textures", 0);
        var vertices = new List<ChunkVertex>();
        for (var i = 0; i < BlockFace.All.Length; i++)
        {
            var face = BlockFace.All[i];
            vertices.AddRange([
                new(face.A, face.UvA, i, face.Brightness), new(face.B, face.UvB, i, face.Brightness),
                new(face.C, face.UvC, i, face.Brightness), new(face.A, face.UvA, i, face.Brightness),
                new(face.C, face.UvC, i, face.Brightness), new(face.D, face.UvD, i, face.Brightness)]);
        }
        _vertices = new(gl, CollectionsMarshal.AsSpan(vertices));
        _instances = new(gl, ReadOnlySpan<ItemInstance>.Empty, BufferUsageARB.StreamDraw);
        _vao = new(gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, 0);
        _vao.SetAttribute(1, 2, VertexAttribType.Float, 12);
        _vao.SetAttribute(2, 1, VertexAttribType.Float, 20);
        _vao.SetAttribute(3, 1, VertexAttribType.Float, 24);
        _vao.SetVertexBuffer(_instances, 1);
        _vao.SetAttribute(4, 4, VertexAttribType.Float, 0, 1);
        _vao.SetAttribute(5, 3, VertexAttribType.Float, 16, 1);
        _vao.SetAttribute(6, 3, VertexAttribType.Float, 28, 1);
        _vao.SetAttribute(7, 1, VertexAttribType.Float, 40, 1);
        gl.VertexArrayBindingDivisor(_vao.Handle, 1, 1);
    }

    public int Draw(ClientEntities entities, Vector3d eye, Camera camera, float alpha, double seconds)
    {
        _data.Clear();
        var frustum = Frustum.FromMatrix(camera.ViewProjection);
        foreach (var entity in entities.All)
        {
            if (entity.Stack.IsEmpty || !_items.Contains(entity.Stack.Item)) continue;
            var offset = (entity.PositionAt(alpha) - eye).ToVector3();
            var definition = _items.Get(entity.Stack.Item);
            var icon = _visuals.IconLayer(entity.Stack.Item);
            var billboard = definition.Block == null || icon >= 0;
            offset.Y += FloatingHeight(billboard, seconds, entity.Id.Index);
            if (!frustum.Intersects(offset - new Vector3(0.3f), offset + new Vector3(0.3f))) continue;
            var angle = (float)(seconds % (Math.PI * 2)) + entity.Id.Index;
            var first = Vector3.Zero;
            var second = Vector3.Zero;
            if (definition.Block is { } block && icon < 0)
            {
                first = new(_visuals.Blocks.GetLayer(block, 0), _visuals.Blocks.GetLayer(block, 1), _visuals.Blocks.GetLayer(block, 2));
                second = new(_visuals.Blocks.GetLayer(block, 3), _visuals.Blocks.GetLayer(block, 4), _visuals.Blocks.GetLayer(block, 5));
            }
            else icon = Math.Max(0, icon);
            _data.Add(new(offset, angle, first, second, icon));
        }
        if (_data.Count == 0) return 0;
        _instances.SetData(CollectionsMarshal.AsSpan(_data), BufferUsageARB.StreamDraw);
        _shader.Use();
        _shader.SetUniform("u_ViewProjection", camera.ViewProjection);
        _shader.SetUniform("u_Right", camera.Right);
        _gl.BindVertexArray(_vao.Handle);
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 36, (uint)_data.Count);
        return _data.Count;
    }

    /// <summary>Центр изображения над нижней гранью тела; даже нижняя точка покачивания выше опоры.</summary>
    internal static float FloatingHeight(bool billboard, double seconds, int phase) =>
        (billboard ? 0.25f : 0.17f) + MathF.Sin((float)seconds * 2 + phase) * 0.04f;

    public void Dispose()
    {
        _vao.Dispose();
        _instances.Dispose();
        _vertices.Dispose();
        _shader.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ItemInstance(Vector3 position, float angle, Vector3 layersA, Vector3 layersB, float icon)
{
    public readonly Vector3 Position = position;
    public readonly float Angle = angle;
    public readonly Vector3 LayersA = layersA;
    public readonly Vector3 LayersB = layersB;
    public readonly float Icon = icon;
}
