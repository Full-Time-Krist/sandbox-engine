using System.Numerics;
using System.Runtime.InteropServices;
using Engine;
using Engine.Graphics;
using Engine.Windowing;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace Client;

internal sealed class ClientApp(WindowSettings settings) : Application(settings)
{
    // Обычные поля, а не record struct: Marshal.OffsetOf ищет поле по имени,
    // а у позиционного record поля скрыты за авто-свойствами.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Vertex(Vector3 position, Vector3 color)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Color = color;
    }

    private ShaderProgram _shader = null!;
    private GpuBuffer<Vertex> _vertices = null!;
    private GpuBuffer<uint> _indices = null!;
    private VertexArrayObject _vao = null!;

    // Состояние логики: угол на предыдущем и текущем тике.
    private float _previousAngle;
    private float _angle;

    private const float RotationSpeed = 2f; // рад/с
    private const float MouseSensitivity = 0.002f;

    private Vector2 _rightOffset = new(0.5f, 0f);

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        _shader = ShaderProgram.FromFiles(Gl, Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "triangle"));

        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0f), new Vector3(1f, 0f, 0f)),
            new(new Vector3( 0.5f, -0.5f, 0f), new Vector3(0f, 1f, 0f)),
            new(new Vector3( 0.0f,  0.5f, 0f), new Vector3(0f, 0f, 1f)),
        ];
        ReadOnlySpan<uint> indices = [0, 1, 2];

        _vertices = new GpuBuffer<Vertex>(Gl, vertices);
        _indices = new GpuBuffer<uint>(Gl, indices);

        _vao = new VertexArrayObject(Gl);
        _vao.SetVertexBuffer(_vertices);
        _vao.SetAttribute(0, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position)));
        _vao.SetAttribute(1, 3, VertexAttribType.Float, (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color)));
        _vao.SetIndexBuffer(_indices);
    }

    protected override void OnTick()
    {
        _previousAngle = _angle;
        // Удержание клавиши в тике читать можно: это состояние, а не событие.
        var speed = Input.IsKeyDown(Key.Space) ? RotationSpeed * 4 : RotationSpeed;
        _angle += speed * (float)TickDuration;
    }

    protected override void OnRender(double deltaTime, float alpha)
    {
        HandleFrameInput();

        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _shader.Use();

        // Слева: состояние последнего тика как есть. Обновляется 20 раз в секунду и заметно дёргается.
        _shader.SetUniform("u_Offset", new Vector2(-0.5f, 0f));
        _shader.SetUniform("u_Angle", _angle);
        _vao.DrawIndexed();

        // Справа: интерполяция между двумя последними тиками. Плавно на любом FPS.
        // Отстаёт от логики на один тик (50 мс); это обычная плата за плавность.
        _shader.SetUniform("u_Offset", _rightOffset);
        _shader.SetUniform("u_Angle", float.Lerp(_previousAngle, _angle, alpha));
        _vao.DrawIndexed();
    }

    private void HandleFrameInput()
    {
        if (!Input.IsCursorCaptured && Input.WasMousePressed(MouseButton.Left))
            Input.CaptureCursor();
        else if (Input.IsCursorCaptured && Input.WasKeyPressed(Key.Escape))
            Input.ReleaseCursor();

        if (Input.IsCursorCaptured)
        {
            // Экранный Y растёт вниз, а в OpenGL вверх, поэтому Y инвертируется.
            var delta = Input.MouseDelta * MouseSensitivity;
            _rightOffset += new Vector2(delta.X, -delta.Y);
        }
    }

    protected override void OnUnload()
    {
        _vao.Dispose();
        _indices.Dispose();
        _vertices.Dispose();
        _shader.Dispose();
    }
}
