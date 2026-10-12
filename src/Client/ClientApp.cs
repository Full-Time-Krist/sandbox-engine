using Engine;
using Engine.Windowing;
using Silk.NET.OpenGL;

namespace Client;

internal sealed class ClientApp(WindowSettings settings) : Application(settings)
{
    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);
    }

    protected override void OnRender(double deltaTime)
    {
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }
}
