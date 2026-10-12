using Engine.Graphics;
using Engine.Input;
using Engine.Timing;
using Engine.Windowing;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Engine;

/// <summary>
/// Базовый класс приложения: создаёт окно с контекстом OpenGL и крутит игровой цикл.
/// Клиент наследуется от него и переопределяет нужные хуки.
/// </summary>
/// <remarks>
/// В каждом кадре сначала выполняется столько тиков <see cref="OnTick"/> (фиксированный шаг),
/// сколько накопилось времени, затем один раз <see cref="OnRender"/> с коэффициентом интерполяции.
/// </remarks>
public abstract class Application : IDisposable
{
    private readonly IWindow _window;
    private readonly string _title;
    private readonly FixedTimestep _timestep;
    private readonly FrameStats _stats = new();

    protected GL Gl { get; private set; } = null!;
    protected InputState Input { get; private set; } = null!;
    protected IWindow Window => _window;
    protected double TickDuration => _timestep.TickDuration;

    /// <summary>Строка состояния в заголовке окна после FPS и TPS (пока в игре нет вывода текста).</summary>
    protected string StatusText { get; set; } = "";

    protected Application(WindowSettings settings, int ticksPerSecond = 20)
    {
        _title = settings.Title;
        _timestep = new FixedTimestep(ticksPerSecond);

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
        _window.Render += HandleFrame;
        _window.FramebufferResize += HandleResize;
        _window.Closing += HandleClosing;
    }

    public void Run() => _window.Run();

    protected virtual void OnLoad() { }

    /// <summary>Шаг игровой логики. Вызывается с частотой ticksPerSecond независимо от FPS.</summary>
    protected virtual void OnTick() { }

    /// <param name="deltaTime">Время с прошлого кадра в секундах.</param>
    /// <param name="alpha">Доля пути от последнего тика к следующему [0; 1) для интерполяции.</param>
    protected virtual void OnRender(double deltaTime, float alpha) { }
    protected virtual void OnResize(Vector2D<int> framebufferSize) { }
    protected virtual void OnUnload() { }

    private void HandleLoad()
    {
        Gl = _window.CreateOpenGL();
        Input = new InputState(_window);
#if DEBUG
        GlDebug.Enable(Gl);
#endif
        Console.WriteLine($"OpenGL {Gl.GetStringS(StringName.Version)} | {Gl.GetStringS(StringName.Renderer)}");

        // Глубина в NDC от 0 до 1, как в DirectX и Vulkan. Тогда матрицы System.Numerics подходят без переделки,
        // точность буфера глубины распределяется лучше, и позже можно включить reversed-Z.
        Gl.ClipControl(ClipControlOrigin.LowerLeft, ClipControlDepth.ZeroToOne);

        Gl.Enable(EnableCap.DepthTest);
        // Задние грани не рисуются. Лицевыми считаются грани с обходом против часовой стрелки, если смотреть снаружи.
        Gl.Enable(EnableCap.CullFace);
        Gl.CullFace(TriangleFace.Back);
        Gl.FrontFace(FrontFaceDirection.Ccw);

        OnLoad();
    }

    private void HandleFrame(double deltaTime)
    {
        Input.BeginFrame();

        // Время сверх MaxFrameTime логика не наверстает, поэтому такой кадр стоит заметить в логе.
        // Например, пока окно тащат мышью, Windows не отдаёт управление циклу, и кадр длится секунды.
        if (deltaTime > FixedTimestep.MaxFrameTime)
            Console.WriteLine($"[Engine] Кадр длился {deltaTime:F2} с");

        var ticks = _timestep.Advance(deltaTime);
        for (var i = 0; i < ticks; i++)
            OnTick();

        OnRender(deltaTime, _timestep.Alpha);

        if (_stats.Record(deltaTime, ticks))
            _window.Title = StatusText.Length == 0
                ? $"{_title} | FPS {_stats.Fps} | TPS {_stats.Tps}"
                : $"{_title} | FPS {_stats.Fps} | TPS {_stats.Tps} | {StatusText}";
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
        Input.Dispose();
        Gl.Dispose();
    }

    public void Dispose()
    {
        _window.Dispose();
        GC.SuppressFinalize(this);
    }
}
