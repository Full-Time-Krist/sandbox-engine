using System.Numerics;
using Engine.Graphics;
using Engine.Input;
using Silk.NET.Input;

namespace Client;

/// <summary>
/// Свободный полёт камеры. Мышь поворачивает взгляд каждый кадр, чтобы отклик был мгновенным.
/// Движение считается в тиках, как будущая физика игрока, а на экран выводится интерполированная позиция.
/// </summary>
internal sealed class FlyCameraController(Camera camera, Vector3 startPosition)
{
    private const float MouseSensitivity = 0.0025f; // радиан на пиксель
    private const float Speed = 10f;                // блоков в секунду
    private const float SprintMultiplier = 3f;

    private Vector3 _position = startPosition;
    private Vector3 _previousPosition = startPosition;

    /// <summary>Позиция на последнем тике (не интерполированная).</summary>
    public Vector3 Position => _position;

    /// <summary>Вызывается каждый кадр, пока курсор захвачен.</summary>
    public void Look(Vector2 mouseDelta)
    {
        camera.Yaw += mouseDelta.X * MouseSensitivity;
        // Мышь вниз (экранный Y растёт) опускает взгляд.
        camera.Pitch -= mouseDelta.Y * MouseSensitivity;
    }

    /// <summary>Вызывается каждый тик. Читает только удержание клавиш.</summary>
    public void Tick(InputState input, float tickDuration)
    {
        _previousPosition = _position;

        var direction = Vector3.Zero;
        if (input.IsKeyDown(Key.W)) direction += camera.FlatForward;
        if (input.IsKeyDown(Key.S)) direction -= camera.FlatForward;
        if (input.IsKeyDown(Key.D)) direction += camera.Right;
        if (input.IsKeyDown(Key.A)) direction -= camera.Right;
        if (input.IsKeyDown(Key.Space)) direction += Vector3.UnitY;
        if (input.IsKeyDown(Key.ShiftLeft)) direction -= Vector3.UnitY;

        if (direction == Vector3.Zero)
            return;

        // Нормализация: по диагонали (W + D) скорость не должна вырастать в √2 раз.
        var speed = input.IsKeyDown(Key.ControlLeft) ? Speed * SprintMultiplier : Speed;
        _position += Vector3.Normalize(direction) * speed * tickDuration;
    }

    /// <summary>Выставляет камеру в позицию между двумя последними тиками.</summary>
    public void UpdateCamera(float alpha) => camera.Position = Vector3.Lerp(_previousPosition, _position, alpha);
}
