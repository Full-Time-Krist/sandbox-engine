using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace Engine.Input;

/// <summary>
/// Состояние клавиатуры и мыши для текущего кадра.
/// </summary>
/// <remarks>
/// События ОС приходят перед кадром и копятся в «ожидающих» наборах. В начале кадра
/// <see cref="BeginFrame"/> переносит их в «текущие», поэтому весь кадр видит одну и ту же картину.
/// <para>
/// <see cref="WasKeyPressed"/> и <see cref="MouseDelta"/> относятся к кадру. В <c>OnTick</c> их читать нельзя:
/// при FPS выше TPS большинство кадров проходит без тиков, и нажатие потеряется. Логика тика читает только
/// удержание (<see cref="IsKeyDown"/>).
/// </para>
/// </remarks>
public sealed class InputState : IDisposable
{
    private readonly IInputContext _context;
    private readonly IKeyboard _keyboard;
    private readonly IMouse _mouse;

    // HashSet после первого заполнения не аллоцирует: Clear() сохраняет внутренний буфер.
    private HashSet<Key> _pendingKeysPressed = new();
    private HashSet<Key> _keysPressed = new();
    private HashSet<MouseButton> _pendingButtonsPressed = new();
    private HashSet<MouseButton> _buttonsPressed = new();

    private Vector2 _pendingMouseDelta;
    private float _pendingScroll;
    private Vector2 _lastMousePosition;
    private bool _hasLastMousePosition;

    /// <summary>Смещение мыши за кадр в пикселях (Y растёт вниз, как на экране).</summary>
    public Vector2 MouseDelta { get; private set; }

    /// <summary>Прокрутка колеса за кадр (положительная — от себя).</summary>
    public float ScrollDelta { get; private set; }

    /// <summary>Положение курсора в координатах окна, с началом в левом верхнем углу.</summary>
    public Vector2 MousePosition { get; private set; }

    public bool IsCursorCaptured => _mouse.Cursor.CursorMode != CursorMode.Normal;

    internal InputState(IWindow window)
    {
        _context = window.CreateInput();
        _keyboard = _context.Keyboards[0];
        _mouse = _context.Mice[0];

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseMove += OnMouseMove;
        _mouse.Scroll += OnScroll;

        // При переключении на другое окно (Alt+Tab) курсор нужно отпустить, иначе он останется запертым.
        window.FocusChanged += focused =>
        {
            if (!focused)
                ReleaseCursor();
        };
    }

    public bool IsKeyDown(Key key) => _keyboard.IsKeyPressed(key);
    public bool WasKeyPressed(Key key) => _keysPressed.Contains(key);

    public bool IsMouseDown(MouseButton button) => _mouse.IsButtonPressed(button);
    public bool WasMousePressed(MouseButton button) => _buttonsPressed.Contains(button);

    /// <summary>Прячет курсор и переключает мышь в «сырой» режим для управления камерой.</summary>
    public void CaptureCursor()
    {
        // Raw отдаёт движение мыши без ускорения ОС; если драйвер его не поддерживает, подойдёт Disabled.
        _mouse.Cursor.CursorMode = _mouse.Cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled;
        _hasLastMousePosition = false;
    }

    public void ReleaseCursor()
    {
        _mouse.Cursor.CursorMode = CursorMode.Normal;
        _hasLastMousePosition = false;
    }

    /// <summary>Делает накопленные события видимыми для текущего кадра. Вызывается движком в начале кадра.</summary>
    internal void BeginFrame()
    {
        (_keysPressed, _pendingKeysPressed) = (_pendingKeysPressed, _keysPressed);
        _pendingKeysPressed.Clear();

        (_buttonsPressed, _pendingButtonsPressed) = (_pendingButtonsPressed, _buttonsPressed);
        _pendingButtonsPressed.Clear();

        MouseDelta = _pendingMouseDelta;
        _pendingMouseDelta = Vector2.Zero;

        ScrollDelta = _pendingScroll;
        _pendingScroll = 0;
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode) => _pendingKeysPressed.Add(key);

    private void OnMouseDown(IMouse mouse, MouseButton button) => _pendingButtonsPressed.Add(button);

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        // Первое событие после захвата или старта даёт скачок от старой позиции курсора, его пропускаем.
        if (_hasLastMousePosition)
            _pendingMouseDelta += position - _lastMousePosition;

        _lastMousePosition = position;
        MousePosition = position;
        _hasLastMousePosition = true;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel) => _pendingScroll += wheel.Y;

    public void Dispose() => _context.Dispose();
}
