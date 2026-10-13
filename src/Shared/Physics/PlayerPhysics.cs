using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Physics;

/// <summary>Что игрок хочет сделать в этот тик. Собирает клиент из нажатых клавиш.</summary>
/// <param name="Move">
/// Желаемое направление по горизонтали в мировых осях: X — мировой X, Y — мировой Z. Длина до 1
/// (длиннее — нормализуется). Направление из поворота камеры считает клиент: физика о камере не знает.
/// </param>
/// <param name="Jump">Прыжок; в полёте — вверх.</param>
/// <param name="Down">В полёте — вниз. При ходьбе пока ничего не делает (приседания нет).</param>
/// <param name="Sprint">Бег; в полёте — быстрый полёт.</param>
public readonly record struct PlayerInput(Vector2 Move, bool Jump = false, bool Down = false, bool Sprint = false);

/// <summary>Состояние тела игрока между тиками.</summary>
/// <param name="Position">Точка ног: центр нижней грани тела. Абсолютная позиция, поэтому double.</param>
/// <param name="Velocity">Скорость в блоках в секунду. Небольшие числа — хватает float.</param>
/// <param name="OnGround">Стоял на опоре в конце прошлого тика: только тогда можно прыгнуть.</param>
/// <param name="Flying">Полёт: без гравитации, Jump и Down двигают вверх и вниз.</param>
public readonly record struct PlayerState(Vector3d Position, Vector3 Velocity = default, bool OnGround = false, bool Flying = false);

/// <summary>
/// Физика тела игрока: один тик — чистая функция «состояние + ввод + мир → новое состояние».
/// Одним и тем же кодом движение считает клиент и проверяет сервер, поэтому здесь нет ни случайностей,
/// ни зависимости от времени кадра: шаг всегда равен тику.
/// </summary>
/// <remarks>
/// <para>
/// Столкновения — короб тела против полных кубов твёрдых блоков (<see cref="BlockRegistry.IsSolid"/>).
/// Перемещение за тик раскладывается по осям: сначала Y, затем X и Z, и по каждой оси обрезается о ближайший
/// блок на пути. Раздельные оси дают скольжение вдоль стен. Блоки берутся из короба «тело + весь путь за тик»,
/// поэтому на любой скорости тело не проскакивает сквозь тонкую стену.
/// </para>
/// <para>
/// Порядок осей (Y первым, как в Minecraft) влияет только на угловые случаи: тело одновременно падает
/// и упирается в край блока. Клиент и сервер считают одним кодом, поэтому расходиться им не в чем.
/// </para>
/// </remarks>
public static class PlayerPhysics
{
    /// <summary>Ширина тела по X и Z, в блоках: проходит в проём шириной 1.</summary>
    public const double Width = 0.6;

    /// <summary>Высота тела: проходит в проём высотой 2, но не 1.</summary>
    public const double Height = 1.8;

    /// <summary>Высота глаза над ногами.</summary>
    public const double EyeHeight = 1.62;

    // Скорости — в блоках в секунду, ускорения — в блоках в секунду за секунду. Близко к Minecraft.
    public const float WalkSpeed = 4.3f;
    public const float SprintSpeed = 5.6f;
    public const float FlySpeed = 11f;
    public const float FlySprintSpeed = 22f;
    public const float Gravity = 32f;
    public const float TerminalVelocity = 78f;

    /// <summary>Начальная скорость прыжка: при шаге 1/20 с вершина около 1,26 блока — на блок запрыгнуть можно, на два нет.</summary>
    public const float JumpVelocity = 9.8f;

    // Какую долю разницы между текущей и желаемой скоростью тело набирает за тик. На земле разгон и остановка
    // почти мгновенные, в воздухе направление меняется медленно: прыжок сохраняет разбег.
    private const float GroundControl = 0.6f;
    private const float AirControl = 0.15f;
    private const float FlyControl = 0.4f;

    private const double Dt = GameTime.TickDuration;

    /// <summary>Короб тела, стоящего ногами в точке <paramref name="feet"/>.</summary>
    public static Aabb BodyAt(Vector3d feet) => new(
        new Vector3d(feet.X - Width / 2, feet.Y, feet.Z - Width / 2),
        new Vector3d(feet.X + Width / 2, feet.Y + Height, feet.Z + Width / 2));

    /// <summary>
    /// Один тик. Если тело задело бы незагруженный чанк, игрок стоит на месте и теряет скорость:
    /// иначе при входе в мир или быстром полёте он провалился бы сквозь ещё не пришедшие чанки.
    /// </summary>
    public static PlayerState Step(PlayerState state, PlayerInput input, ChunkMap chunks, BlockRegistry blocks)
    {
        var move = float.IsFinite(input.Move.LengthSquared()) ? input.Move : Vector2.Zero;
        if (move.LengthSquared() > 1)
            move = Vector2.Normalize(move);

        var velocity = state.Velocity;
        if (state.Flying)
        {
            var speed = input.Sprint ? FlySprintSpeed : FlySpeed;
            var vertical = (input.Jump ? 1 : 0) - (input.Down ? 1 : 0);
            velocity = Vector3.Lerp(velocity, new Vector3(move.X * speed, vertical * speed, move.Y * speed), FlyControl);
        }
        else
        {
            var speed = input.Sprint ? SprintSpeed : WalkSpeed;
            var control = state.OnGround ? GroundControl : AirControl;
            velocity.X += (move.X * speed - velocity.X) * control;
            velocity.Z += (move.Y * speed - velocity.Z) * control;
            if (state.OnGround && input.Jump)
                velocity.Y = JumpVelocity;
            velocity.Y = MathF.Max(velocity.Y - Gravity * (float)Dt, -TerminalVelocity);
        }

        var wanted = new Vector3d(velocity.X * Dt, velocity.Y * Dt, velocity.Z * Dt);
        if (Move(BodyAt(state.Position), wanted, chunks, blocks) is not { } moved)
            return state with { Velocity = Vector3.Zero };

        // Обрезанная ось — удар: скорость по ней гасится. Удар вниз — опора под ногами.
        // Сравнение точное: необрезанное перемещение Move возвращает без изменений.
        var hitY = moved.Y != wanted.Y;
        if (moved.X != wanted.X) velocity.X = 0;
        if (hitY) velocity.Y = 0;
        if (moved.Z != wanted.Z) velocity.Z = 0;

        return new PlayerState(state.Position + moved, velocity, OnGround: hitY && wanted.Y < 0, state.Flying);
    }

    /// <summary>Пересекает ли тело в этой точке твёрдый блок. Незагруженный мир считается пустым.</summary>
    public static bool Collides(Vector3d feet, ChunkMap chunks, BlockRegistry blocks)
    {
        var body = BodyAt(feet);
        var (min, max) = BlockRange(body);
        for (var y = min.Y; y <= max.Y; y++)
        for (var x = min.X; x <= max.X; x++)
        for (var z = min.Z; z <= max.Z; z++)
        {
            var block = new BlockPos(x, y, z);
            if (blocks.IsSolid(chunks.GetBlock(block)) && body.Intersects(Aabb.OfBlock(block)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Сдвигает короб на <paramref name="delta"/>, обрезая движение о твёрдые блоки: сначала по Y, затем X и Z.
    /// Возвращает фактическое перемещение или null, если по пути есть незагруженный чанк.
    /// </summary>
    private static Vector3d? Move(Aabb box, Vector3d delta, ChunkMap chunks, BlockRegistry blocks)
    {
        // Все блоки, которые тело может задеть за тик. Скорость ограничена (не больше ~4 блоков за тик),
        // поэтому их десятки; проходим по ним трижды, без выделения памяти.
        var (min, max) = BlockRange(box.Stretch(delta));

        for (var cy = min.Y >> ChunkMath.Bits; cy <= max.Y >> ChunkMath.Bits; cy++)
        for (var cx = min.X >> ChunkMath.Bits; cx <= max.X >> ChunkMath.Bits; cx++)
        for (var cz = min.Z >> ChunkMath.Bits; cz <= max.Z >> ChunkMath.Bits; cz++)
        {
            if (!chunks.Contains(new ChunkPos(cx, cy, cz)))
                return null;
        }

        var dy = delta.Y;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dy = box.ClipY(obstacle, dy);
        box = box.Offset(new Vector3d(0, dy, 0));

        var dx = delta.X;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dx = box.ClipX(obstacle, dx);
        box = box.Offset(new Vector3d(dx, 0, 0));

        var dz = delta.Z;
        foreach (var obstacle in SolidBlocks(min, max, chunks, blocks))
            dz = box.ClipZ(obstacle, dz);

        return new Vector3d(dx, dy, dz);
    }

    /// <summary>Блоки, которые задевает короб (с запасом в касание гранью: лишний блок ничего не обрежет).</summary>
    private static (BlockPos Min, BlockPos Max) BlockRange(Aabb box) =>
        (BlockPos.Containing(box.Min), BlockPos.Containing(box.Max));

    private static SolidBlockEnumerable SolidBlocks(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks) =>
        new(min, max, chunks, blocks);

    /// <summary>Перебор коробов твёрдых блоков в диапазоне. Структура, а не yield: без выделения памяти каждый тик.</summary>
    private readonly struct SolidBlockEnumerable(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
    {
        public Enumerator GetEnumerator() => new(min, max, chunks, blocks);

        public struct Enumerator(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
        {
            // Начинаем «перед» первым блоком: MoveNext сначала сдвигает.
            private int _x = min.X - 1, _y = min.Y, _z = min.Z;

            public Aabb Current { get; private set; }

            public bool MoveNext()
            {
                while (true)
                {
                    if (++_x > max.X)
                    {
                        _x = min.X;
                        if (++_z > max.Z)
                        {
                            _z = min.Z;
                            if (++_y > max.Y)
                                return false;
                        }
                    }

                    var block = new BlockPos(_x, _y, _z);
                    if (blocks.IsSolid(chunks.GetBlock(block)))
                    {
                        Current = Aabb.OfBlock(block);
                        return true;
                    }
                }
            }
        }
    }
}
