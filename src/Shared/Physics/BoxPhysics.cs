using System.Collections.Immutable;
using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Physics;

/// <summary>
/// Форма и поведение падающего тела (предмет на земле, позже — мобы без своего ввода).
/// </summary>
/// <param name="Width">Ширина короба по X и Z, в блоках.</param>
/// <param name="Height">Высота короба; точка тела — центр нижней грани, как у игрока.</param>
/// <param name="Gravity">Ускорение вниз, блоков в секунду за секунду.</param>
/// <param name="TerminalVelocity">Предел скорости падения, блоков в секунду.</param>
/// <param name="AirDrag">Какую долю скорости тело теряет за тик в воздухе.</param>
/// <param name="GroundDrag">Какую долю горизонтальной скорости тело теряет за тик на земле (трение).</param>
public readonly record struct BodyShape(double Width, double Height, float Gravity, float TerminalVelocity, float AirDrag, float GroundDrag);

/// <summary>Состояние падающего тела между тиками.</summary>
/// <param name="Position">Точка тела: центр нижней грани короба.</param>
/// <param name="Velocity">Скорость, блоков в секунду.</param>
/// <param name="OnGround">Лежит на опоре в конце тика.</param>
public readonly record struct BodyState(Vector3d Position, Vector3 Velocity = default, bool OnGround = false);

/// <summary>
/// Движение коробов среди блоков — общее для всех тел: игрока (<see cref="PlayerPhysics"/>) и сущностей.
/// Детерминированно, шаг всегда равен тику: сервер и клиент получают одно и то же.
/// </summary>
/// <remarks>
/// <para>
/// Столкновения — короб тела против коробов формы твёрдых блоков (<see cref="BlockRegistry.IsSolid"/>,
/// <see cref="BlockShape"/>): у куба один короб на клетку, у полублока — нижняя половина.
/// Перемещение за тик раскладывается по осям: сначала Y, затем X и Z, и по каждой оси обрезается о ближайший
/// блок на пути. Раздельные оси дают скольжение вдоль стен. Блоки берутся из короба «тело + весь путь за тик»,
/// поэтому на любой скорости тело не проскакивает сквозь тонкую стену.
/// </para>
/// <para>
/// Порядок осей (Y первым, как в Minecraft) влияет только на угловые случаи: тело одновременно падает
/// и упирается в край блока. Клиент и сервер считают одним кодом, поэтому расходиться им не в чем.
/// </para>
/// </remarks>
public static class BoxPhysics
{
    private const double Dt = GameTime.TickDuration;

    /// <summary>Скорость меньше этой гасится до нуля: иначе трение вечно уменьшало бы её, и тело «ползло» бы.</summary>
    private const float RestSpeed = 0.01f;

    /// <summary>Короб тела шириной <paramref name="width"/> и высотой <paramref name="height"/> с точкой в <paramref name="feet"/>.</summary>
    public static Aabb BodyAt(Vector3d feet, double width, double height) => new(
        new Vector3d(feet.X - width / 2, feet.Y, feet.Z - width / 2),
        new Vector3d(feet.X + width / 2, feet.Y + height, feet.Z + width / 2));

    /// <summary>
    /// Один тик падающего тела: гравитация, столкновения, сопротивление воздуха и трение о землю. Если путь задевает
    /// незагруженный чанк, тело стоит на месте без скорости — как игрок: не проваливается сквозь ещё не пришедший мир.
    /// </summary>
    public static BodyState Fall(BodyState state, in BodyShape shape, ChunkMap chunks, BlockRegistry blocks)
    {
        // Лежит и опора на месте: шаг ничего бы не изменил (гравитацию гасит та же опора), а стоит он десятков чтений
        // блоков. Тысяча лежащих предметов — основная работа системы движения (замер шага 7.6).
        if (state.OnGround && state.Velocity == Vector3.Zero && HasSupport(state.Position, shape, chunks, blocks))
            return state;

        var velocity = state.Velocity;
        velocity.Y = MathF.Max(velocity.Y - shape.Gravity * (float)Dt, -shape.TerminalVelocity);

        var wanted = new Vector3d(velocity.X * Dt, velocity.Y * Dt, velocity.Z * Dt);
        if (Move(BodyAt(state.Position, shape.Width, shape.Height), wanted, chunks, blocks) is not { } moved)
            return state with { Velocity = Vector3.Zero };

        // Обрезанная ось — удар: скорость по ней гасится. Удар вниз — опора.
        var hitY = moved.Y != wanted.Y;
        if (moved.X != wanted.X) velocity.X = 0;
        if (hitY) velocity.Y = 0;
        if (moved.Z != wanted.Z) velocity.Z = 0;
        var onGround = hitY && wanted.Y < 0;

        var keep = 1 - (onGround ? shape.GroundDrag : shape.AirDrag);
        velocity.X = Settle(velocity.X * keep);
        velocity.Z = Settle(velocity.Z * keep);
        if (!onGround)
            velocity.Y *= 1 - shape.AirDrag;

        return new BodyState(state.Position + moved, velocity, onGround);
    }

    private static float Settle(float speed) => MathF.Abs(speed) < RestSpeed ? 0 : speed;

    /// <summary>
    /// Стоит ли тело на твёрдом блоке: низ короба ровно на верхней грани хотя бы одного короба формы под ним (тело может
    /// стоять сразу на нескольких; верх полублока — на половине клетки). Незагруженный мир опорой не считается — тогда
    /// решает полный шаг.
    /// </summary>
    public static bool HasSupport(Vector3d feet, in BodyShape shape, ChunkMap chunks, BlockRegistry blocks)
    {
        var body = BodyAt(feet, shape.Width, shape.Height);
        // Верх опоры — в клетке чуть ниже ног: у куба это клетка под ногами, у полублока — та, где стоят ноги.
        var y = (int)Math.Floor(feet.Y - Aabb.Epsilon);
        // Касание гранью соседнего столбца — не опора: края сужаются на допуск, как в Aabb.
        var minX = (int)Math.Floor(body.Min.X + Aabb.Epsilon);
        var maxX = (int)Math.Floor(body.Max.X - Aabb.Epsilon);
        var minZ = (int)Math.Floor(body.Min.Z + Aabb.Epsilon);
        var maxZ = (int)Math.Floor(body.Max.Z - Aabb.Epsilon);
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var pos = new BlockPos(x, y, z);
            if (!chunks.Contains(pos.Chunk))
                continue;
            var block = chunks.GetBlock(pos);
            if (!blocks.IsSolid(block))
                continue;
            foreach (var box in blocks.ShapeOf(block).Boxes)
            {
                var obstacle = Aabb.Of(pos, box);
                if (Math.Abs(obstacle.Max.Y - feet.Y) <= Aabb.Epsilon
                    && body.Max.X > obstacle.Min.X + Aabb.Epsilon && body.Min.X < obstacle.Max.X - Aabb.Epsilon
                    && body.Max.Z > obstacle.Min.Z + Aabb.Epsilon && body.Min.Z < obstacle.Max.Z - Aabb.Epsilon)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Сдвигает короб на <paramref name="delta"/>, обрезая движение о твёрдые блоки: сначала по Y, затем X и Z.
    /// Возвращает фактическое перемещение или null, если по пути есть незагруженный чанк.
    /// </summary>
    public static Vector3d? Move(Aabb box, Vector3d delta, ChunkMap chunks, BlockRegistry blocks)
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
    internal static (BlockPos Min, BlockPos Max) BlockRange(Aabb box) =>
        (BlockPos.Containing(box.Min), BlockPos.Containing(box.Max));

    internal static SolidBlockEnumerable SolidBlocks(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks) =>
        new(min, max, chunks, blocks);

    /// <summary>
    /// Перебор коробов твёрдых блоков в диапазоне — всех коробов формы каждого. Структура, а не yield: без выделения
    /// памяти каждый тик.
    /// </summary>
    internal readonly struct SolidBlockEnumerable(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
    {
        public Enumerator GetEnumerator() => new(min, max, chunks, blocks);

        public struct Enumerator(BlockPos min, BlockPos max, ChunkMap chunks, BlockRegistry blocks)
        {
            // Начинаем «перед» первым блоком: MoveNext сначала сдвигает.
            private int _x = min.X - 1, _y = min.Y, _z = min.Z;
            private BlockPos _block;
            private ImmutableArray<ShapeBox> _boxes;
            private int _box;

            public Aabb Current { get; private set; }

            public bool MoveNext()
            {
                while (true)
                {
                    // Сначала — оставшиеся короба текущего блока.
                    if (!_boxes.IsDefault && ++_box < _boxes.Length)
                    {
                        Current = Aabb.Of(_block, _boxes[_box]);
                        return true;
                    }

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

                    _block = new BlockPos(_x, _y, _z);
                    _boxes = default;
                    var id = chunks.GetBlock(_block);
                    if (blocks.IsSolid(id))
                    {
                        _boxes = blocks.ShapeOf(id).Boxes;
                        _box = 0;
                        Current = Aabb.Of(_block, _boxes[0]);
                        return true;
                    }
                }
            }
        }
    }
}
