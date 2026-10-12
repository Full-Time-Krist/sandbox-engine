using Engine.Graphics;
using Engine.Windowing;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Engine;

/// <summary>
/// Базовый класс приложения: создаёт окно с контекстом OpenGL и вызывает методы-хуки.
/// Клиент наследуется от него и переопределяет нужные хуки.
/// </summary>
public abstract class Application : IDisposable
{
    private readonly IWindow _window;

    protected GL Gl { get; private set; } = null!;
    protected IWindow Window => _window;

    protected Application(WindowSettings settings)
    {
        var flags = ContextFlags.ForwardCompatible;
#if DEBUG
        flags |= ContextFlags.Debug;
#endif

        var options = WindowOptions.Default with
        {
            Title = settings.Title,
            Size = new Vector2D<int>(settings.Width, settings.Height),
            VSync = settings.VSync,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, flags, new APIVersion(4, 6)),
        };

        _window = Silk.NET.Windowing.Window.Create(options);
        _window.Load += HandleLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += HandleResize;
        _window.Closing += HandleClosing;
    }

    public void Run() => _window.Run();

    protected virtual void OnLoad() { }
    protected virtual void OnRender(double deltaTime) { }
    protected virtual void OnResize(Vector2D<int> framebufferSize) { }
    protected virtual void OnUnload() { }

    private void HandleLoad()
    {
        Gl = _window.CreateOpenGL();
#if DEBUG
        GlDebug.Enable(Gl);
#endif
        Console.WriteLine($"OpenGL {Gl.GetStringS(StringName.Version)} | {Gl.GetStringS(StringName.Renderer)}");
        OnLoad();
    }

    private void HandleResize(Vector2D<int> size)
    {
        Gl.Viewport(size);
        OnResize(size);
    }

    // Closing вызывается, пока контекст OpenGL ещё жив: здесь освобождаются GPU-ресурсы.
    private void HandleClosing()
    {
        OnUnload();
        Gl.Dispose();
    }

    public void Dispose()
    {
        _window.Dispose();
        GC.SuppressFinalize(this);
    }
}
