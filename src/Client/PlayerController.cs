using System.Numerics;
using Engine.Graphics;
using Engine.Input;
using Shared;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;
using Silk.NET.Input;

namespace Client;

/// <summary>
/// Игрок на клиенте: тело с физикой (<see cref="PlayerPhysics"/>) и камера в его глазах. Мышь поворачивает взгляд
/// каждый кадр, чтобы отклик был мгновенным. Движение считается в тиках тем же кодом, которым его проверяет
/// сервер, а на экран выводится позиция между двумя последними тиками.
/// </summary>
/// <param name="world">Чанки клиента: о них тело упирается, а в незагруженных стоит на месте.</param>
internal sealed class PlayerController(Camera camera, ChunkMap world, BlockRegistry blocks)
{
    private const float MouseSensitivity = 0.0025f; // радиан на пиксель

    private PlayerState _state;
    private Vector3d _previousPosition;

    /// <summary>Точка ног на последнем тике (не интерполированная). Её клиент шлёт серверу.</summary>
    public Vector3d Position => _state.Position;

    public bool IsFlying => _state.Flying;

    /// <summary>Позиция глаза в текущем кадре, между двумя последними тиками (см. <see cref="UpdateEye"/>).</summary>
    public Vector3d EyePosition { get; private set; }

    /// <summary>Мгновенно переносит игрока (ноги в <paramref name="feet"/>), без интерполяции и со сбросом скорости.</summary>
    public void Teleport(Vector3d feet)
    {
        _state = new PlayerState(feet, Flying: _state.Flying);
        _previousPosition = feet;
        EyePosition = Eye(feet);
    }

    /// <summary>Включает или выключает полёт. Скорость сохраняется: выключив полёт в воздухе, игрок падает.</summary>
    public void ToggleFlying() => _state = _state with { Flying = !_state.Flying };

    /// <summary>Вызывается каждый кадр, пока курсор захвачен.</summary>
    public void Look(Vector2 mouseDelta)
    {
        camera.Yaw += mouseDelta.X * MouseSensitivity;
        // Мышь вниз (экранный Y растёт) опускает взгляд.
        camera.Pitch -= mouseDelta.Y * MouseSensitivity;
    }

    /// <summary>Вызывается каждый тик. Читает только удержание клавиш.</summary>
    /// <param name="controlsEnabled">false — клавиши не действуют (курсор не захвачен), но тело падает и тормозит.</param>
    public void Tick(InputState input, bool controlsEnabled)
    {
        _previousPosition = _state.Position;
        var playerInput = controlsEnabled ? ReadInput(input) : default;
        _state = PlayerPhysics.Step(_state, playerInput, world, blocks);
    }

    private PlayerInput ReadInput(InputState input)
    {
        // Направление ходьбы — в мировых осях: физика о камере не знает. Длину до 1 приводит сама физика.
        var direction = Vector3.Zero;
        if (input.IsKeyDown(Key.W)) direction += camera.FlatForward;
        if (input.IsKeyDown(Key.S)) direction -= camera.FlatForward;
        if (input.IsKeyDown(Key.D)) direction += camera.Right;
        if (input.IsKeyDown(Key.A)) direction -= camera.Right;

        return new PlayerInput(
            new Vector2(direction.X, direction.Z),
            Jump: input.IsKeyDown(Key.Space),
            Down: input.IsKeyDown(Key.ShiftLeft),
            Sprint: input.IsKeyDown(Key.ControlLeft));
    }

    /// <summary>Выставляет глаз в позицию между двумя последними тиками.</summary>
    public void UpdateEye(float alpha) => EyePosition = Eye(Vector3d.Lerp(_previousPosition, _state.Position, alpha));

    private static Vector3d Eye(Vector3d feet) => feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
}
