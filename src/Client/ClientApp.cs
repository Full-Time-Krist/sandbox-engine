using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Client.Rendering;
using Engine;
using Engine.Graphics;
using Engine.Windowing;
using Server;
using Shared;
using Shared.Blocks;
using Shared.Networking;
using Shared.Protocol;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client;

/// <param name="connection">Канал к серверу: локальный в одиночной игре, сетевой в мультиплеере.</param>
/// <param name="integratedServer">Встроенный сервер одиночной игры. Клиент тикает его перед своим тиком; в мультиплеере null.</param>
internal sealed class ClientApp(
    WindowSettings settings,
    IConnection<ClientMessage, ServerMessage> connection,
    GameServer? integratedServer)
    : Application(settings, GameTime.TicksPerSecond)
{
    // Камера над углом тестового чанка, взгляд по диагонали на его центр.
    private readonly Camera _camera = new() { Yaw = 0.75f * MathF.PI, Pitch = -0.45f };
    private FlyCameraController _controller = null!;

    private ShaderProgram _shader = null!;
    private ColorMesh _cube = null!;
    private ColorMesh _grid = null!;
    private ColorMesh _testChunkMesh = null!;

    // Куб, которым управляет сервер: две последние полученные позиции для интерполяции.
    private Vector3 _serverCubePrevious;
    private Vector3 _serverCubeCurrent;
    private bool _hasServerCube;

    private Vector3 _lastSentPosition = new(float.NaN);

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        _shader = ShaderProgram.FromFiles(Gl, Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "basic"));
        _cube = DebugGeometry.CreateCube(Gl);
        _grid = DebugGeometry.CreateGrid(Gl, halfSize: 16);
        _testChunkMesh = BuildTestChunkMesh();

        _controller = new FlyCameraController(_camera, startPosition: new Vector3(-10f, 22f, -10f));
        UpdateAspectRatio(Window.FramebufferSize);

        connection.Send(new JoinRequest("Player"));
    }

    private ColorMesh BuildTestChunkMesh()
    {
        // Клиент собирает свой реестр. В мультиплеере таблицу id будет присылать сервер.
        var registry = new BlockRegistry();
        var blocks = BaseBlocks.Register(registry);
        registry.Freeze();

        var mesher = new ChunkMesher(new BlockVisuals(registry, BlockVisuals.BaseColors));
        var chunk = DebugTerrain.CreateTestChunk(blocks);
        var data = new ChunkMeshData();

        var stopwatch = Stopwatch.StartNew();
        mesher.Build(chunk, default, data);
        stopwatch.Stop();
        // Первый вызов включает JIT-компиляцию, поэтому время завышено. Честный замер — на шаге 1.5, по многим чанкам.
        Console.WriteLine($"[Client] Меш чанка: {data.QuadCount} граней, {chunk.NonAirCount} блоков, {stopwatch.Elapsed.TotalMilliseconds:F2} мс");

        return new ColorMesh(Gl, CollectionsMarshal.AsSpan(data.Vertices), CollectionsMarshal.AsSpan(data.Indices), PrimitiveType.Triangles);
    }

    protected override void OnTick()
    {
        // Порядок: сервер обрабатывает прошлые действия клиента и рассылает состояние,
        // затем клиент получает его в том же тике. Так интерполяции хватает ровно двух последних позиций.
        integratedServer?.Tick();
        ProcessServerMessages();

        _controller.Tick(Input, (float)TickDuration);

        // Шлём позицию, только если она изменилась. NaN в начальном значении гарантирует первую отправку.
        if (_controller.Position != _lastSentPosition)
        {
            connection.Send(new PlayerMove(_controller.Position));
            _lastSentPosition = _controller.Position;
        }
    }

    private void ProcessServerMessages()
    {
        while (connection.TryReceive(out var message))
        {
            switch (message)
            {
                case JoinAccepted accepted:
                    Console.WriteLine($"[Client] Подключён к серверу, id игрока {accepted.PlayerId}");
                    break;

                case EntityMoved moved:
                    _serverCubePrevious = _hasServerCube ? _serverCubeCurrent : moved.Position;
                    _serverCubeCurrent = moved.Position;
                    _hasServerCube = true;
                    break;

                default:
                    Console.WriteLine($"[Client] Неизвестное сообщение {message.GetType().Name}");
                    break;
            }
        }
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
        // Тестовый чанк стоит в (0, 0, 0), поэтому матрица модели единичная.
        _testChunkMesh.Draw();

        if (_hasServerCube)
        {
            // Позиция сущности — центр куба, а меш куба построен от угла (0, 0, 0).
            var center = Vector3.Lerp(_serverCubePrevious, _serverCubeCurrent, alpha);
            _shader.SetUniform("u_Model", Matrix4x4.CreateTranslation(center - new Vector3(0.5f)));
            _cube.Draw();
        }
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
        _testChunkMesh.Dispose();
        _grid.Dispose();
        _cube.Dispose();
        _shader.Dispose();
    }
}
