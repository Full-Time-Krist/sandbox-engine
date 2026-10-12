using System.Numerics;
using Client.Rendering;
using Engine;
using Engine.Graphics;
using Engine.Windowing;
using Server;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client;

/// <param name="connection">Канал к серверу: локальный в одиночной игре, сетевой в мультиплеере.</param>
/// <param name="integratedServer">
/// Встроенный сервер одиночной игры, работающий в своём потоке; в мультиплеере null.
/// Клиент его не тикает, а только следит, не упал ли он.
/// </param>
internal sealed class ClientApp(
    WindowSettings settings,
    IConnection<ClientMessage, ServerMessage> connection,
    ServerHost? integratedServer)
    : Application(settings, GameTime.TicksPerSecond)
{
    private readonly Camera _camera = new() { Pitch = -0.4f };
    private readonly ClientWorld _world = new();
    private FlyCameraController _controller = null!;

    private BlockRegistry _registry = null!;

    /// <summary>
    /// Копия хотбара для отображения. Авторитетная живёт на сервере: клиент только сообщает выбор слота,
    /// а что делает предмет при нажатии, решает сервер.
    /// </summary>
    private Hotbar _hotbar = null!;
    private ShaderProgram _chunkShader = null!;
    private ShaderProgram _lineShader = null!;
    private ColorMesh _selectionBox = null!;
    private ColorMesh _crosshair = null!;
    private TextureArray _blockTextures = null!;
    private ChunkRenderer _chunkRenderer = null!;

    private Vector3 _lastSentPosition = new(float.NaN);
    private Vector2D<int> _framebufferSize;

    /// <summary>Блок под прицелом в текущем кадре.</summary>
    private BlockHit? _target;

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        // Клиент собирает свой реестр и сверяет его с таблицей сервера при подключении (см. JoinAccepted).
        _registry = new BlockRegistry();
        _hotbar = Hotbar.CreateDefault(BaseBlocks.Register(_registry));
        _registry.Freeze();
        UpdateStatusText();

        var visuals = new BlockVisuals(_registry, BlockVisuals.BaseAppearances);
        _blockTextures = LoadBlockTextures(visuals.TextureNames);
        _chunkRenderer = new ChunkRenderer(Gl, new ChunkMesher(visuals));

        var shaders = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
        _chunkShader = ShaderProgram.FromFiles(Gl, Path.Combine(shaders, "chunk"));
        _chunkShader.SetUniform("u_Textures", 0); // сэмплер читает текстурный блок 0
        _lineShader = ShaderProgram.FromFiles(Gl, Path.Combine(shaders, "basic"));
        _selectionBox = LineGeometry.CreateBoxOutline(Gl, new Vector3(0.05f));
        _crosshair = LineGeometry.CreateCrosshair(Gl);

        // Позицию задаст сервер в JoinAccepted.
        _controller = new FlyCameraController(_camera, startPosition: Vector3.Zero);
        UpdateAspectRatio(Window.FramebufferSize);

        connection.Send(new JoinRequest("Player"));
    }

    private TextureArray LoadBlockTextures(IReadOnlyList<string> names)
    {
        const int MissingSize = 16;
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Blocks");
        var images = new List<Image>(names.Count);

        foreach (var name in names)
        {
            var path = Path.Combine(directory, name + ".png");
            if (name == BlockVisuals.MissingTexture)
            {
                images.Add(Image.CreateMissing(MissingSize, MissingSize));
            }
            else if (File.Exists(path))
            {
                images.Add(Image.Load(path));
            }
            else
            {
                // Нет файла — не повод падать: блок будет в шахматку, а в логе видно, чего не хватает.
                Console.WriteLine($"[Client] Нет текстуры {path}");
                images.Add(Image.CreateMissing(MissingSize, MissingSize));
            }
        }

        Console.WriteLine($"[Client] Текстуры блоков: {images.Count} слоёв");
        return new TextureArray(Gl, images);
    }

    protected override void OnTick()
    {
        // Сервер тикает в своём потоке; ответы на действия клиента приходят через очередь, обычно к следующему тику.
        if (integratedServer?.Fault is { } fault)
            throw new InvalidOperationException("Встроенный сервер упал", fault);

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
                    VerifyBlockTable(accepted.BlockNames);
                    _controller.Teleport(accepted.SpawnPosition);
                    Console.WriteLine($"[Client] Подключён к серверу, id игрока {accepted.PlayerId}");
                    break;

                case ChunkData data:
                    _world.AddChunk(data.ToChunk());
                    break;

                case BlockChanged changed:
                    _world.SetBlock(changed.Position, changed.Block);
                    break;

                default:
                    Console.WriteLine($"[Client] Неизвестное сообщение {message.GetType().Name}");
                    break;
            }
        }
    }

    /// <summary>
    /// Числовые id блоков у клиента и сервера должны означать одно и то же. Если таблицы разошлись
    /// (разные версии или моды), продолжать нельзя: мир молча отрисовался бы не теми блоками.
    /// </summary>
    private void VerifyBlockTable(IReadOnlyList<string> serverNames)
    {
        var clientNames = _registry.All.Select(b => b.Name).ToArray();
        if (!clientNames.SequenceEqual(serverNames))
        {
            throw new InvalidOperationException(
                $"Таблица блоков сервера не совпадает с клиентской.\nСервер: {string.Join(", ", serverNames)}\nКлиент: {string.Join(", ", clientNames)}");
        }
    }

    protected override void OnRender(double deltaTime, float alpha)
    {
        var actionsAllowed = HandleFrameInput();
        _controller.UpdateCamera(alpha);
        _target = VoxelRaycast.Cast(_world.Chunks, _camera.Position, _camera.Forward, GameRules.BlockReach);
        if (actionsAllowed)
            HandleItemActions();

        var stats = _chunkRenderer.Update(_world);
        if (stats.Chunks > 0)
        {
            Console.WriteLine($"[Client] Меши: {stats.Chunks} чанков, {stats.Quads} граней за {stats.TotalMs:F1} мс " +
                              $"(в среднем {stats.TotalMs / stats.Chunks:F2} мс, максимум {stats.MaxMs:F2} мс), в видеопамяти {_chunkRenderer.MeshCount}");
        }

        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _chunkShader.Use();
        _chunkShader.SetUniform("u_ViewProjection", _camera.ViewProjection);
        _blockTextures.Bind(0);
        _chunkRenderer.Draw(_chunkShader);

        DrawSelection();
        DrawCrosshair();
    }

    private void DrawSelection()
    {
        if (_target is not { } target)
            return;

        // Рамка чуть больше блока, иначе её линии совпадут с рёбрами граней и будут мерцать (z-fighting).
        const float Grow = 0.004f;
        var block = target.Block;
        var model = Matrix4x4.CreateScale(1f + Grow)
                    * Matrix4x4.CreateTranslation(block.X - Grow / 2, block.Y - Grow / 2, block.Z - Grow / 2);

        _lineShader.Use();
        _lineShader.SetUniform("u_ViewProjection", _camera.ViewProjection);
        _lineShader.SetUniform("u_Model", model);
        _selectionBox.Draw();
    }

    private void DrawCrosshair()
    {
        if (_framebufferSize.X <= 0 || _framebufferSize.Y <= 0)
            return;

        // Перекрестие рисуется прямо в координатах экрана (NDC от −1 до 1), поэтому камера не нужна.
        // Меш задан в пикселях; масштаб переводит их в NDC отдельно по X и Y, чтобы крест не растягивался с окном.
        _lineShader.Use();
        _lineShader.SetUniform("u_ViewProjection", Matrix4x4.Identity);
        _lineShader.SetUniform("u_Model", Matrix4x4.CreateScale(2f / _framebufferSize.X, 2f / _framebufferSize.Y, 1f));

        // Белый цвет со смешиванием «1 − цвет под крестом» инвертирует фон: крест виден и на небе, и на траве.
        Gl.Disable(EnableCap.DepthTest);
        Gl.Enable(EnableCap.Blend);
        Gl.BlendFunc(BlendingFactor.OneMinusDstColor, BlendingFactor.Zero);
        _crosshair.Draw();
        Gl.Disable(EnableCap.Blend);
        Gl.Enable(EnableCap.DepthTest);
    }

    protected override void OnResize(Vector2D<int> framebufferSize) => UpdateAspectRatio(framebufferSize);

    private void UpdateAspectRatio(Vector2D<int> size)
    {
        // При сворачивании окна размер становится 0×0, деление дало бы NaN в матрице проекции.
        _framebufferSize = size;
        if (size.X > 0 && size.Y > 0)
            _camera.AspectRatio = (float)size.X / size.Y;
    }

    private static readonly Key[] HotbarKeys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6];

    /// <summary>Захват курсора, взгляд мышью и выбор слота. Возвращает, можно ли в этом кадре применять предмет.</summary>
    private bool HandleFrameInput()
    {
        if (!Input.IsCursorCaptured)
        {
            // Клик, захвативший курсор, не должен заодно сломать блок: действия — только со следующего кадра.
            if (Input.WasMousePressed(MouseButton.Left))
                Input.CaptureCursor();
            return false;
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            Input.ReleaseCursor();
            return false;
        }

        _controller.Look(Input.MouseDelta);

        for (var slot = 0; slot < HotbarKeys.Length; slot++)
        {
            if (Input.WasKeyPressed(HotbarKeys[slot]) && _hotbar.TrySelect(slot))
            {
                connection.Send(new SelectHotbarSlot(slot));
                UpdateStatusText();
            }
        }

        return true;
    }

    /// <summary>
    /// Кнопки мыши означают действия предмета, а не «сломать/поставить»: смысл задаёт предмет в руке на сервере.
    /// Вызывается после обновления камеры и луча выделения, поэтому сервер повторит луч из того же положения,
    /// по которому нарисована рамка, и изменится именно выделенный блок.
    /// </summary>
    private void HandleItemActions()
    {
        if (Input.WasMousePressed(MouseButton.Left))
            connection.Send(new UseItem(ItemAction.Primary, _camera.Position, _camera.Forward));
        if (Input.WasMousePressed(MouseButton.Right))
            connection.Send(new UseItem(ItemAction.Secondary, _camera.Position, _camera.Forward));
    }

    private void UpdateStatusText()
    {
        var name = _registry.Get(_hotbar.SelectedItem).Name;
        StatusText = $"слот {_hotbar.SelectedSlot + 1}: {name}";
    }

    protected override void OnUnload()
    {
        _chunkRenderer.Dispose();
        _blockTextures.Dispose();
        _crosshair.Dispose();
        _selectionBox.Dispose();
        _lineShader.Dispose();
        _chunkShader.Dispose();
    }
}
