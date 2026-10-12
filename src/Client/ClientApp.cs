using System.Numerics;
using Client.Rendering;
using Engine;
using Engine.Graphics;
using Engine.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client;

internal sealed class ClientApp(WindowSettings settings) : Application(settings)
{
    private readonly Camera _camera = new() { Pitch = -0.3f };
    private FlyCameraController _controller = null!;

    private ShaderProgram _shader = null!;
    private ColorMesh _cube = null!;
    private ColorMesh _grid = null!;

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        _shader = ShaderProgram.FromFiles(Gl, Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "basic"));
        _cube = DebugGeometry.CreateCube(Gl);
        _grid = DebugGeometry.CreateGrid(Gl, halfSize: 16);

        _controller = new FlyCameraController(_camera, startPosition: new Vector3(0.5f, 2f, 4f));
        UpdateAspectRatio(Window.FramebufferSize);
    }

    protected override void OnTick()
    {
        _controller.Tick(Input, (float)TickDuration);
    }

    protected override void OnRender(double deltaTime, float alpha)
    {
        HandleFrameInput();
        _controller.UpdateCamera(alpha);

        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _shader.Use();
        _shader.SetUniform("u_ViewProjection", _camera.ViewProjection);

        _shader.SetUniform("u_Model", Matrix4x4.Identity);
        _grid.Draw();
        _cube.Draw();
    }

    protected override void OnResize(Vector2D<int> framebufferSize) => UpdateAspectRatio(framebufferSize);

    private void UpdateAspectRatio(Vector2D<int> size)
    {
        // При сворачивании окна размер становится 0×0, деление дало бы NaN в матрице проекции.
        if (size.X > 0 && size.Y > 0)
            _camera.AspectRatio = (float)size.X / size.Y;
    }

    private void HandleFrameInput()
    {
        if (!Input.IsCursorCaptured && Input.WasMousePressed(MouseButton.Left))
            Input.CaptureCursor();
        else if (Input.IsCursorCaptured && Input.WasKeyPressed(Key.Escape))
            Input.ReleaseCursor();

        if (Input.IsCursorCaptured)
            _controller.Look(Input.MouseDelta);
    }

    protected override void OnUnload()
    {
        _grid.Dispose();
        _cube.Dispose();
        _shader.Dispose();
    }
}
