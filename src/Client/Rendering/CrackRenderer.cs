using System.Numerics;
using Engine.Graphics;
using Shared.Blocks;
using Silk.NET.OpenGL;

namespace Client.Rendering;

/// <summary>
/// Трещины на блоке, который игрок ломает удержанием: куб чуть больше блока (иначе грани совпали бы с гранями блока
/// и мерцали), полупрозрачная текстура стадии из того же массива текстур, что и блоки. Глубину не пишет — рисуется
/// поверх блока, но за всем, что ближе к камере.
/// </summary>
internal sealed class CrackRenderer : IDisposable
{
    /// <summary>Стадий трещин: файлы <c>Assets/Textures/Effects/destroy_0..9.png</c>.</summary>
    public const int Stages = 10;

    private const float Grow = 0.004f;

    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly ChunkRenderMesh _cube;
    private readonly int _firstLayer;

    /// <param name="firstLayer">Слой стадии 0 в массиве текстур; стадии идут подряд.</param>
    public CrackRenderer(GL gl, ShaderProgram shader, int firstLayer)
    {
        _gl = gl;
        _shader = shader;
        _firstLayer = firstLayer;

        // Те же грани и UV, что у блоков в мире: трещина ложится на грань так же, как текстура блока.
        // Свет шейдер трещин не читает: они — тёмные линии поверх уже освещённого блока.
        var data = new ChunkMeshData();
        foreach (var face in BlockFace.All)
        {
            var first = (uint)data.Vertices.Count;
            data.Vertices.Add(new ChunkVertex(face.A, face.UvA, 0, 1, 15, 0));
            data.Vertices.Add(new ChunkVertex(face.B, face.UvB, 0, 1, 15, 0));
            data.Vertices.Add(new ChunkVertex(face.C, face.UvC, 0, 1, 15, 0));
            data.Vertices.Add(new ChunkVertex(face.D, face.UvD, 0, 1, 15, 0));
            data.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        _cube = new ChunkRenderMesh(gl, data);
    }

    /// <summary>Стадия трещины по доле пройденного времени: 0 — только начали, <see cref="Stages"/> − 1 — почти сломан.</summary>
    public static int StageFor(double progress) => Math.Clamp((int)(progress * Stages), 0, Stages - 1);

    /// <param name="offset">Смещение угла блока от глаза (рендер относительно камеры).</param>
    /// <param name="bounds">Общий короб формы блока: трещины ложатся на него (у полублока — на нижнюю половину).</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 offset, ShapeBox bounds, int stage)
    {
        _shader.Use();
        _shader.SetUniform("u_ViewProjection", viewProjection);
        _shader.SetUniform("u_Model", Matrix4x4.CreateScale(bounds.Size + new Vector3(Grow))
                                      * Matrix4x4.CreateTranslation(offset + bounds.Min - new Vector3(Grow / 2)));
        _shader.SetUniform("u_Layer", (float)(_firstLayer + stage));

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _cube.Draw();
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose() => _cube.Dispose();
}
