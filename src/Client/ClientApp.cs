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
    /// <summary>Камера в глазах игрока. Направление взгляда при входе задаёт сервер (<see cref="JoinAccepted.StartLook"/>).</summary>
    private readonly Camera _camera = new();
    private readonly ClientWorld _world = new();
    private PlayerController _controller = null!;

    private BlockRegistry _registry = null!;
    private ItemRegistry _items = null!;

    /// <summary>
    /// Копия инвентаря для отображения. Авторитетная живёт на сервере: клиент меняет только выбор слота
    /// и сообщает его, а содержимое слотов присылает сервер. Что делает предмет при нажатии, тоже решает сервер.
    /// </summary>
    private Inventory _inventory = null!;

    /// <summary>
    /// Сервер прислал инвентарь (<see cref="InventoryContents"/>). До этого слот не выбирается: выбор ушёл бы серверу
    /// после входа, а пришедший следом инвентарь вернул бы клиенту прежний слот — в руке на сервере было бы другое.
    /// </summary>
    private bool _inventoryReceived;

    /// <summary>Накопленная прокрутка колеса: тачпад шлёт доли деления, слот меняется на каждое целое.</summary>
    private float _scroll;

    private ShaderProgram _chunkShader = null!;
    private ShaderProgram _lineShader = null!;
    private ColorMesh _selectionBox = null!;
    private ColorMesh _crosshair = null!;
    private TextureArray _blockTextures = null!;
    private ChunkRenderer _chunkRenderer = null!;

    private Vector3d _lastSentPosition = new(double.NaN, double.NaN, double.NaN);
    private LookAngles _lastSentLook;

    /// <summary>Режим игрока. Его меняет только сервер (<see cref="GameModeChanged"/>); клиент по G лишь просит.</summary>
    private GameMode _mode;

    /// <summary>Номер последнего телепорта от сервера: идёт в каждом PlayerMove (см. <see cref="PlayerTeleport"/>).</summary>
    private int _teleportId;
    private long _tick;
    private bool _joined;

    /// <summary>Сколько чанков нарисовано в последнем кадре (после отсечения невидимых).</summary>
    private int _drawnChunks;

    /// <summary>Построение мешей за последние 10 секунд: пишется в лог вместе с числом чанков.</summary>
    private MeshStatsWindow _meshStats;

    /// <summary>Самый долгий кадр и число кадров дольше 20 мс за последние 10 секунд. Цель этапа 2 — ни одного такого.</summary>
    private (double MaxMs, int Slow) _frameTimes;
    private long _framesRendered;
    private Vector2D<int> _framebufferSize;

    /// <summary>Блок под прицелом в текущем кадре.</summary>
    private BlockHit? _target;

    protected override void OnLoad()
    {
        Gl.ClearColor(0.53f, 0.81f, 0.92f, 1f);

        // Клиент собирает свои реестры и сверяет их с таблицами сервера при подключении (см. JoinAccepted).
        _registry = new BlockRegistry();
        BaseBlocks.Register(_registry);
        _registry.Freeze();
        _items = new ItemRegistry();
        _items.RegisterBlockItems(_registry);
        _items.Freeze();
        _inventory = new Inventory(_items);
        UpdateStatusText();

        var visuals = new BlockVisuals(_registry, BlockVisuals.BaseAppearances);
        _blockTextures = LoadBlockTextures(visuals.TextureNames);
        _chunkRenderer = new ChunkRenderer(Gl, new ChunkMesher(visuals), MeshBuildQueue.DefaultWorkerCount);

        var shaders = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
        _chunkShader = ShaderProgram.FromFiles(Gl, Path.Combine(shaders, "chunk"));
        _chunkShader.SetUniform("u_Textures", 0); // сэмплер читает текстурный блок 0
        _lineShader = ShaderProgram.FromFiles(Gl, Path.Combine(shaders, "basic"));
        _selectionBox = LineGeometry.CreateBoxOutline(Gl, new Vector3(0.05f));
        _crosshair = LineGeometry.CreateCrosshair(Gl);

        // Позицию задаст сервер в JoinAccepted. До неё чанков нет, и тело стоит на месте.
        _controller = new PlayerController(_camera, _world.Chunks, _registry);
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

        _controller.Tick(Input, controlsEnabled: Input.IsCursorCaptured);

        if (++_tick % GameTime.TicksPerSecond == 0)
            UpdateStatusText();
        if (_tick % (GameTime.TicksPerSecond * 10) == 0)
        {
            // Память — чтобы при долгом полёте было видно, что она не растёт без предела.
            Console.WriteLine($"[Client] Чанков {_world.Chunks.Count}, мешей {_chunkRenderer.MeshCount} (в очереди {_chunkRenderer.Pending}), " +
                              $"память {GC.GetTotalMemory(false) / 1024 / 1024} МБ, позиция {_controller.Position:F0}; {_meshStats}; " +
                              $"самый долгий кадр {_frameTimes.MaxMs:F1} мс, кадров дольше 20 мс: {_frameTimes.Slow}");
            _meshStats = default;
            _frameTimes = default;
        }

        // Шлём позицию и взгляд, только если они изменились. NaN в начальной позиции гарантирует первую отправку.
        // До JoinAccepted позиция ещё не настоящая (начало координат): сервер начал бы грузить чанки вокруг неё.
        var look = _controller.LookAngles;
        if (_joined && (_controller.Position != _lastSentPosition || look != _lastSentLook))
        {
            connection.Send(new PlayerMove(_controller.Position, look, _teleportId));
            _lastSentPosition = _controller.Position;
            _lastSentLook = look;
        }
    }

    private void ProcessServerMessages()
    {
        while (connection.TryReceive(out var message))
        {
            switch (message)
            {
                case JoinAccepted accepted:
                    VerifyNames("блоков", accepted.BlockNames, _registry.All.Select(b => b.Name));
                    VerifyNames("предметов", accepted.ItemNames, _items.All.Select(i => i.Name));
                    _controller.Teleport(accepted.StartPosition);
                    _controller.LookAngles = accepted.StartLook;
                    _mode = accepted.Mode;
                    _joined = true;
                    UpdateStatusText();
                    Console.WriteLine($"[Client] Подключён к серверу, id игрока {accepted.PlayerId}, режим {_mode.ToName()}");
                    break;

                case GameModeChanged changedMode:
                    _mode = changedMode.Mode;
                    // Летать можно только в творческом. Скорость полёта погасит телепорт, который сервер шлёт следом.
                    if (_mode != GameMode.Creative)
                        _controller.IsFlying = false;
                    UpdateStatusText();
                    Console.WriteLine($"[Client] Режим {_mode.ToName()}");
                    break;

                case PlayerTeleport teleport:
                    // Сервер отклонил движение (или перенёс игрока): встаём, где он сказал, и считаем дальше от этой точки.
                    _controller.Teleport(teleport.Position);
                    _teleportId = teleport.TeleportId;
                    Console.WriteLine($"[Client] Сервер перенёс игрока в {teleport.Position}");
                    break;

                case ChunkData data:
                    _world.AddChunk(data.ToChunk());
                    break;

                case ChunkUnload unload:
                    _world.RemoveChunk(unload.Position);
                    break;

                case BlockChanged changed:
                    _world.SetBlock(changed.Position, changed.Block);
                    break;

                case InventoryContents contents:
                    if (contents.Slots.Length != Inventory.SlotCount)
                        throw new InvalidDataException($"Инвентарь от сервера: {contents.Slots.Length} слотов вместо {Inventory.SlotCount}");
                    for (var slot = 0; slot < Inventory.SlotCount; slot++)
                        _inventory.Set(slot, contents.Slots[slot]);
                    _inventory.TrySelect(contents.SelectedSlot);
                    _inventoryReceived = true;
                    UpdateStatusText();
                    break;

                case InventoryChanged changed:
                    foreach (var (slot, stack) in changed.Slots)
                        _inventory.Set(slot, stack);
                    UpdateStatusText();
                    break;

                default:
                    Console.WriteLine($"[Client] Неизвестное сообщение {message.GetType().Name}");
                    break;
            }
        }
    }

    /// <summary>
    /// Числовые id блоков и предметов у клиента и сервера должны означать одно и то же. Если таблицы разошлись
    /// (разные версии или моды), продолжать нельзя: мир отрисовался бы не теми блоками, а в слотах были бы не те предметы.
    /// </summary>
    private static void VerifyNames(string what, IReadOnlyList<string> serverNames, IEnumerable<string> clientNames)
    {
        var client = clientNames.ToArray();
        if (!client.SequenceEqual(serverNames))
        {
            throw new InvalidOperationException(
                $"Таблица {what} сервера не совпадает с клиентской.\nСервер: {string.Join(", ", serverNames)}\nКлиент: {string.Join(", ", client)}");
        }
    }

    protected override void OnRender(double deltaTime, float alpha)
    {
        // deltaTime — промежуток от прошлого кадра: долгий кадр виден по нему в следующем.
        // Первый промежуток не считаем: в него входит запуск (окно, текстуры, шейдеры), а не игра.
        var frameMs = deltaTime * 1000;
        if (_framesRendered++ > 0)
            _frameTimes = (Math.Max(_frameTimes.MaxMs, frameMs), _frameTimes.Slow + (frameMs > 20 ? 1 : 0));

        var actionsAllowed = HandleFrameInput();
        _controller.UpdateEye(alpha);
        var eye = _controller.EyePosition;
        _target = VoxelRaycast.Cast(_world.Chunks, eye, _camera.Forward, GameRules.BlockReach);
        if (actionsAllowed)
            HandleItemActions();

        var stats = _chunkRenderer.Update(_world, eye);
        _meshStats.Add(stats);

        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _chunkShader.Use();
        // Камера в начале координат: матрица содержит только поворот и проекцию, а смещения от камеры
        // считаются в double для каждого объекта. Так мир не дрожит и в миллионе блоков от центра.
        var viewProjection = _camera.ViewProjection;
        _chunkShader.SetUniform("u_ViewProjection", viewProjection);
        _blockTextures.Bind(0);
        _drawnChunks = _chunkRenderer.Draw(_chunkShader, eye, Frustum.FromMatrix(viewProjection));

        DrawSelection(eye);
        DrawCrosshair();
    }

    private void DrawSelection(Vector3d eye)
    {
        if (_target is not { } target)
            return;

        // Рамка чуть больше блока, иначе её линии совпадут с рёбрами граней и будут мерцать (z-fighting).
        const float Grow = 0.004f;
        var block = target.Block;
        var offset = (new Vector3d(block.X, block.Y, block.Z) - eye).ToVector3();
        var model = Matrix4x4.CreateScale(1f + Grow)
                    * Matrix4x4.CreateTranslation(offset - new Vector3(Grow / 2));

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

    private static readonly Key[] HotbarKeys =
        [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6, Key.Number7, Key.Number8, Key.Number9];

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

        // Полёт — только в творческом режиме.
        if (Input.WasKeyPressed(Key.F) && _mode == GameMode.Creative)
        {
            _controller.IsFlying = !_controller.IsFlying;
            UpdateStatusText();
        }

        // Смену режима решает сервер: клиент переключится, когда придёт GameModeChanged.
        // Временно, до команд и прав доступа (сейчас сервер разрешает её всем).
        if (Input.WasKeyPressed(Key.G) && _joined)
            connection.Send(new SetGameMode(_mode == GameMode.Creative ? GameMode.Survival : GameMode.Creative));

        if (_inventoryReceived)
            HandleSlotSelection();

        return true;
    }

    /// <summary>Клавиши 1–9 и колесо мыши. Слот выбирает сам клиент и сообщает серверу.</summary>
    private void HandleSlotSelection()
    {
        for (var slot = 0; slot < HotbarKeys.Length; slot++)
        {
            if (Input.WasKeyPressed(HotbarKeys[slot]))
                SelectSlot(slot);
        }

        // Колесо от себя — слот левее, на себя — правее; за краем хотбара выбор переходит на другой край.
        _scroll += Input.ScrollDelta;
        var steps = (int)_scroll;
        _scroll -= steps;
        if (steps != 0)
            SelectSlot(((_inventory.SelectedSlot - steps) % Inventory.HotbarSize + Inventory.HotbarSize) % Inventory.HotbarSize);
    }

    private void SelectSlot(int slot)
    {
        if (slot == _inventory.SelectedSlot || !_inventory.TrySelect(slot))
            return;
        connection.Send(new SelectHotbarSlot(slot));
        UpdateStatusText();
    }

    /// <summary>
    /// Кнопки мыши означают действия предмета, а не «сломать/поставить»: смысл задаёт предмет в руке на сервере.
    /// Вызывается после обновления камеры и луча выделения, поэтому сервер повторит луч из того же положения,
    /// по которому нарисована рамка, и изменится именно выделенный блок.
    /// </summary>
    private void HandleItemActions()
    {
        if (Input.WasMousePressed(MouseButton.Left))
            connection.Send(new UseItem(ItemAction.Primary, _controller.EyePosition, _camera.Forward));
        if (Input.WasMousePressed(MouseButton.Right))
            connection.Send(new UseItem(ItemAction.Secondary, _controller.EyePosition, _camera.Forward));
    }

    private void UpdateStatusText()
    {
        // Предмет в руке — до хотбара на экране (шаг 3.6) он виден только здесь.
        var held = _inventory.Selected;
        var item = held.IsEmpty ? "пусто" : $"{_items.Get(held.Item).Name} ×{held.Count}";
        // Позиция ног, полёт и режим — для проверок скриптом: по заголовку видно, стоит игрок, падает или упёрся.
        var player = _controller is null ? "" : $"{_controller.Position}{(_controller.IsFlying ? " полёт" : "")} | ";
        var mode = _mode == GameMode.Creative ? "творческий" : "выживание";
        StatusText = $"{player}{mode} | слот {_inventory.SelectedSlot + 1}: {item} | " +
                     $"чанков {_world.Chunks.Count}, мешей {_chunkRenderer?.MeshCount ?? 0}, нарисовано {_drawnChunks}";
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
