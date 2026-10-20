using System.Collections.Immutable;
using System.Numerics;
using Shared.Blocks;
using Shared.Physics;
using Shared.Voxels;

namespace Shared.Tests.Physics;

/// <summary>
/// Столкновения с неполными блоками (шаг 8.6): тела ударяются о короба формы, а не о клетку. Пол — верх камня на высоте 0;
/// полублок — нижняя половина клетки; «ступенька» — два короба: нижняя половина и верхняя половина западной части.
/// </summary>
public class ShapeCollisionTests
{
    private const double Tolerance = 1e-9;

    private static readonly BodyShape Item = new(Width: 0.25, Height: 0.25, Gravity: 16, TerminalVelocity: 40,
        AirDrag: 0.02f, GroundDrag: 0.4f);

    private readonly BlockRegistry _blocks = new();
    private readonly ChunkMap _chunks = new();
    private readonly BlockId _stone;
    private readonly BlockId _slab;
    private readonly BlockId _step;
    private readonly BlockId _torch;

    public ShapeCollisionTests()
    {
        _stone = _blocks.Register("test:stone", isOpaque: true).Id;
        _slab = _blocks.Register("test:slab", isOpaque: false, shape: BlockShape.Slab).Id;
        _step = _blocks.Register("test:step", isOpaque: false, shape: new BlockShape(null,
            [ShapeBox.FromPixels(0, 0, 0, 16, 8, 16), ShapeBox.FromPixels(0, 8, 0, 8, 16, 16)])).Id;
        _torch = _blocks.Register("test:torch", isOpaque: false, isSolid: false, shape: BlockShape.Torch).Id;
        _blocks.Freeze();
        for (var x = -1; x <= 0; x++)
        for (var y = -1; y <= 0; y++)
        for (var z = -1; z <= 0; z++)
            _chunks.Set(new Chunk(new ChunkPos(x, y, z)));
        for (var x = -20; x <= 20; x++)
        for (var z = -20; z <= 20; z++)
            Set(x, -1, z, _stone);
    }

    private void Set(int x, int y, int z, BlockId block) => Assert.True(_chunks.TrySetBlock(new BlockPos(x, y, z), block));

    private BodyState FallUntilRest(Vector3d start)
    {
        var state = new BodyState(start);
        for (var tick = 0; tick < 200 && !(state.OnGround && state.Velocity == Vector3.Zero); tick++)
            state = BoxPhysics.Fall(state, Item, _chunks, _blocks);
        Assert.True(state.OnGround);
        return state;
    }

    [Fact]
    public void Item_FallsOntoSlab_RestsOnItsTop()
    {
        Set(3, 0, 3, _slab);

        var state = FallUntilRest(new Vector3d(3.5, 4, 3.5));

        Assert.Equal(0.5, state.Position.Y, Tolerance);
    }

    /// <summary>У формы из двух коробов работают оба: над нижней половиной тело ложится на 0,5, над верхней — на 1.</summary>
    [Fact]
    public void Item_OnTwoBoxShape_RestsOnTheBoxBelowIt()
    {
        Set(3, 0, 3, _step);

        Assert.Equal(1, FallUntilRest(new Vector3d(3.2, 4, 3.5)).Position.Y, Tolerance);
        Assert.Equal(0.5, FallUntilRest(new Vector3d(3.8, 4, 3.5)).Position.Y, Tolerance);
    }

    /// <summary>
    /// Опора — верх короба на любой высоте: на плите (0,5) и на кубе (1) — да; на высоте плиты рядом с ней, но не над ней,
    /// и над половиной куба — нет.
    /// </summary>
    [Fact]
    public void HasSupport_TopOfAnyBox()
    {
        Set(3, 0, 3, _slab);
        Set(6, 0, 6, _stone);

        Assert.True(BoxPhysics.HasSupport(new Vector3d(3.5, 0.5, 3.5), Item, _chunks, _blocks));
        Assert.True(BoxPhysics.HasSupport(new Vector3d(6.5, 1, 6.5), Item, _chunks, _blocks));
        Assert.True(BoxPhysics.HasSupport(new Vector3d(9.5, 0, 9.5), Item, _chunks, _blocks));
        Assert.False(BoxPhysics.HasSupport(new Vector3d(5.5, 0.5, 3.5), Item, _chunks, _blocks));
        Assert.False(BoxPhysics.HasSupport(new Vector3d(6.5, 0.5, 6.5), Item, _chunks, _blocks)); // внутри куба, не на нём
        Assert.False(BoxPhysics.HasSupport(new Vector3d(3.5, 1, 3.5), Item, _chunks, _blocks));   // над плитой на высоте куба
    }

    /// <summary>Игрок идёт по полу на плиту — упирается в её бок; стоя на плитах (ноги на 0,5) — идёт по ним свободно.</summary>
    [Fact]
    public void Player_IsStoppedBySlabSide_AndWalksOnSlabTops()
    {
        for (var x = 2; x <= 8; x++)
            Set(x, 0, 0, _slab);

        var blocked = PlayerPhysics.Sweep(new Vector3d(0.5, 0, 0.5), new Vector3d(3, 0, 0), _chunks, _blocks);
        Assert.Equal(2 - PlayerPhysics.Width / 2 - 0.5, blocked!.Value.X, Tolerance);

        var free = PlayerPhysics.Sweep(new Vector3d(2.5, 0.5, 0.5), new Vector3d(3, 0, 0), _chunks, _blocks);
        Assert.Equal(new Vector3d(3, 0, 0), free!.Value);
    }

    private static void AssertStopped(PlayerState state, double y, double x)
    {
        Assert.Equal(y, state.Position.Y, Tolerance);
        Assert.Equal(x, state.Position.X, Tolerance);
    }

    /// <summary>Идёт <paramref name="ticks"/> тиков; смотрит туда же, куда идёт, если не задано иное.</summary>
    private PlayerState Walk(PlayerState state, Vector2 move, int ticks, bool jump = false, Vector2? facing = null)
    {
        for (var i = 0; i < ticks; i++)
            state = PlayerPhysics.Step(state, new PlayerInput(move, Jump: jump, Facing: facing ?? move), _chunks, _blocks);
        return state;
    }

    /// <summary>
    /// Подъём только перед собой: пятясь на уступ спиной или подходя к нему боком — не поднимается; по диагонали
    /// «вперёд-вбок» (45° от взгляда) — поднимается.
    /// </summary>
    [Fact]
    public void Player_StepsUp_OnlyInFrontOfItself()
    {
        for (var x = 3; x <= 12; x++)
        for (var z = -5; z <= 5; z++)
            Set(x, 0, z, _stone);
        var start = new PlayerState(new Vector3d(0.5, 0, 0.5), OnGround: true);

        AssertStopped(Walk(start, Vector2.UnitX, 30, facing: -Vector2.UnitX), y: 0, x: 3 - PlayerPhysics.Width / 2);
        AssertStopped(Walk(start, Vector2.UnitX, 30, facing: Vector2.UnitY), y: 0, x: 3 - PlayerPhysics.Width / 2);
        var diagonal = Vector2.Normalize(new Vector2(1, 1));
        Assert.Equal(1, Walk(start, Vector2.UnitX, 30, facing: diagonal).Position.Y, Tolerance);
    }

    /// <summary>Подъём без прыжка (шаг 8.6, выбор пользователя — на блок): идёт по полу на уступ в блок — встаёт на него.</summary>
    [Fact]
    public void Player_StepsUpOneBlock_WithoutJumping()
    {
        // Уступ широкий: на узком игрок сошёл бы с дальнего края обратно.
        for (var x = 3; x <= 12; x++)
        for (var z = -2; z <= 2; z++)
            Set(x, 0, z, _stone);

        var state = Walk(new PlayerState(new Vector3d(0.5, 0, 0.5), OnGround: true), Vector2.UnitX, 30);

        Assert.Equal(1, state.Position.Y, Tolerance);
        Assert.True(state.Position.X > 3.5, $"дошёл до {state.Position}");
        Assert.True(state.OnGround);
        Assert.False(PlayerPhysics.Collides(state.Position, _chunks, _blocks));
    }

    /// <summary>На плиту — тоже, и с неё на блок: 0,5 + 0,5.</summary>
    [Fact]
    public void Player_StepsUpOntoSlab_ThenOntoBlock()
    {
        for (var z = -2; z <= 2; z++)
        {
            Set(3, 0, z, _slab);
            Set(5, 0, z, _stone);
        }

        var onSlab = Walk(new PlayerState(new Vector3d(0.5, 0, 0.5), OnGround: true), Vector2.UnitX, 12);
        Assert.Equal(0.5, onSlab.Position.Y, Tolerance);

        var onBlock = Walk(onSlab, Vector2.UnitX, 20);
        Assert.Equal(1, onBlock.Position.Y, Tolerance);
        Assert.True(onBlock.Position.X > 5.5);
    }

    /// <summary>Стену в два блока не перешагнуть; под низким потолком (голова не пролезет) — тоже.</summary>
    [Fact]
    public void Player_DoesNotStep_OverTwoBlocks_OrUnderLowCeiling()
    {
        for (var z = -2; z <= 2; z++)
        {
            Set(3, 0, z, _stone);
            Set(3, 1, z, _stone);
            Set(-3, 0, z, _stone);
            Set(-4, 2, z, _stone); // над уступом по −X до потолка 1 блок: тело (1,8) не встанет
            Set(-3, 2, z, _stone);
        }

        var wall = Walk(new PlayerState(new Vector3d(0.5, 0, 0.5), OnGround: true), Vector2.UnitX, 30);
        AssertStopped(wall, y: 0, x: 3 - PlayerPhysics.Width / 2);

        var ceiling = Walk(new PlayerState(new Vector3d(-1.5, 0, 0.5), OnGround: true), -Vector2.UnitX, 30);
        AssertStopped(ceiling, y: 0, x: -2 + PlayerPhysics.Width / 2);
    }

    /// <summary>В полёте и в воздухе подъёма нет: только с земли.</summary>
    [Fact]
    public void Player_DoesNotStep_WhenFlyingOrInAir()
    {
        for (var z = -2; z <= 2; z++)
            Set(3, 0, z, _stone);

        // Летит, но стоит на земле (давит вниз в полёте): подъёма всё равно нет.
        var flying = new PlayerState(new Vector3d(0.5, 0, 0.5), OnGround: true, Flying: true);
        for (var i = 0; i < 30; i++)
            flying = PlayerPhysics.Step(flying, new PlayerInput(Vector2.UnitX, Down: true, Facing: Vector2.UnitX), _chunks, _blocks);
        AssertStopped(flying, y: 0, x: 3 - PlayerPhysics.Width / 2);

        // Висит у стены на высоте 0,2, не на опоре: шаг в стену без подъёма.
        var inAir = PlayerPhysics.Step(new PlayerState(new Vector3d(3 - PlayerPhysics.Width / 2 - 0.01, 0.2, 0.5)),
            new PlayerInput(Vector2.UnitX, Facing: Vector2.UnitX), _chunks, _blocks);
        Assert.True(inAir.Position.Y < 0.2);
    }

    /// <summary>
    /// Сервер проверяет движение протягом (<see cref="PlayerPhysics.Sweep"/>): путь подъёма (вверх, вбок, вниз) он
    /// принимает, а то же перемещение сквозь стену в два блока — нет.
    /// </summary>
    [Fact]
    public void Sweep_AcceptsStepUpPath_RejectsTallWall()
    {
        for (var z = -2; z <= 2; z++)
        {
            Set(3, 0, z, _stone);
            Set(3, 1, 2, _stone);
        }
        var start = new Vector3d(3 - PlayerPhysics.Width / 2, 0, 0.5);
        var stepped = PlayerPhysics.Step(new PlayerState(start, Velocity: new Vector3(4, 0, 0), OnGround: true),
            new PlayerInput(Vector2.UnitX, Facing: Vector2.UnitX), _chunks, _blocks);
        var delta = stepped.Position - start;
        Assert.Equal(1, delta.Y, Tolerance);

        Assert.Equal(delta, PlayerPhysics.Sweep(start, delta, _chunks, _blocks));
        // Там, где уступ в два блока (z = 2), тот же путь не проходит.
        var blocked = PlayerPhysics.Sweep(start with { Z = 2.5 }, delta, _chunks, _blocks)!.Value;
        Assert.NotEqual(delta, blocked);
    }

    /// <summary>
    /// Путь подъёма не оправдывает подъём выше ступеньки: под навесом (прямо вверх на 1,5 не пустит потолок) уйти на блок
    /// вбок и подняться на 1,5 нельзя — после подъёма тело только опускается.
    /// </summary>
    [Fact]
    public void Sweep_StepPath_DoesNotClimbHigherThanStep()
    {
        for (var z = -2; z <= 2; z++)
        {
            Set(3, 0, z, _stone);
            Set(4, 0, z, _stone);
            Set(2, 3, z, _stone); // навес над началом: низ на высоте 3
        }
        var start = new Vector3d(2.5, 0, 0.5);
        var delta = new Vector3d(1, 1.5, 0);

        Assert.NotEqual(delta, PlayerPhysics.Sweep(start, delta, _chunks, _blocks));
        // На блок — можно: это обычный подъём.
        Assert.Equal(delta with { Y = 1 }, PlayerPhysics.Sweep(start, delta with { Y = 1 }, _chunks, _blocks));
    }

    /// <summary>
    /// Трудный случай проверки сервера: игрок идёт по диагонали на плиту, а между осями пути (сначала X, потом Z) угол тела
    /// проходит над соседним полным блоком. Прямой протяг на высоте плиты задевает этот блок, а клиент поднимался выше
    /// него — сервер должен узнать путь подъёма и принять шаг. Случайные миры в такой угол не попадают.
    /// </summary>
    [Fact]
    public void Sweep_AcceptsStepOntoSlab_PastBlockCorner()
    {
        Set(1, 0, 0, _stone); // угол, над которым проходит тело между осями
        Set(1, 0, 1, _slab);  // куда встаёт
        var move = Vector2.Normalize(Vector2.One);
        var start = new PlayerState(new Vector3d(0.65, 0, 1.2), new Vector3(3, 0, 3), OnGround: true);

        var next = PlayerPhysics.Step(start, new PlayerInput(move, Facing: move), _chunks, _blocks);
        var delta = next.Position - start.Position;

        Assert.Equal(0.5, delta.Y, Tolerance); // встал на плиту
        Assert.NotEqual(delta, BoxPhysics.Move(PlayerPhysics.BodyAt(start.Position), delta, _chunks, _blocks)); // прямой протяг упирается
        Assert.Equal(delta, PlayerPhysics.Sweep(start.Position, delta, _chunks, _blocks));
    }

    /// <summary>
    /// Свойство: сервер принимает каждый шаг честного клиента (<see cref="PlayerPhysics.Sweep"/> возвращает присланное
    /// перемещение целиком) — в случайном мире из блоков и плит, с ходьбой в разные стороны, подъёмами и прыжками.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Sweep_AcceptsEveryHonestStep_InRandomWorld(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 120; i++)
        {
            int x = random.Next(-10, 11), z = random.Next(-10, 11);
            var height = random.Next(3);
            for (var y = 0; y < height; y++)
                Set(x, y, z, _stone);
            if (random.Next(2) == 0)
                Set(x, height, z, _slab);
        }
        var state = new PlayerState(new Vector3d(0.5, 6, 0.5));
        var move = Vector2.UnitX;
        var checkedSteps = 0;
        for (var tick = 0; tick < 1500; tick++)
        {
            if (tick % 25 == 0)
            {
                var angle = random.NextSingle() * MathF.Tau;
                move = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            }
            // Держимся над площадкой: у края разворачиваемся к центру.
            if (Math.Abs(state.Position.X) > 9 || Math.Abs(state.Position.Z) > 9)
                move = Vector2.Normalize(new Vector2((float)-state.Position.X, (float)-state.Position.Z));
            var next = PlayerPhysics.Step(state, new PlayerInput(move, Jump: random.Next(40) == 0, Facing: move), _chunks, _blocks);
            var delta = next.Position - state.Position;
            if (delta != default)
            {
                var swept = PlayerPhysics.Sweep(state.Position, delta, _chunks, _blocks)!.Value;
                Assert.True((swept - delta).Length() < 1e-6, $"тик {tick}: из {state.Position} на {delta}, сервер пустил только на {swept}");
                if (Math.Abs(delta.Y) > 0.4)
                    checkedSteps++;
            }
            state = next;
        }
        Assert.True(checkedSteps > 0, "игрок ни разу не поднялся на уступ");
    }

    [Fact]
    public void Collides_WithBoxes_NotWithCell()
    {
        Set(3, 0, 3, _slab);
        Set(5, 0, 5, _torch);

        Assert.True(PlayerPhysics.Collides(new Vector3d(3.5, 0.4, 3.5), _chunks, _blocks));
        Assert.False(PlayerPhysics.Collides(new Vector3d(3.5, 0.5, 3.5), _chunks, _blocks));
        Assert.False(PlayerPhysics.Collides(new Vector3d(5.5, 0, 5.5), _chunks, _blocks)); // факел не твёрдый
    }

    [Fact]
    public void Shapes_Presets_And_Validation()
    {
        Assert.True(BlockShape.Cube.IsFullCube);
        Assert.False(BlockShape.Slab.IsFullCube);
        Assert.Equal(new ShapeBox(Vector3.Zero, new Vector3(1, 0.5f, 1)), BlockShape.Slab.Bounds);
        Assert.Equal(new ShapeBox(new Vector3(7, 0, 7) / 16, new Vector3(9, 10, 9) / 16), BlockShape.Torch.Bounds);
        var step = _blocks.Get(_step).Shape;
        Assert.Equal(new ShapeBox(Vector3.Zero, Vector3.One), step.Bounds);
        Assert.False(step.IsFullCube);
        // Опору держит только твёрдый блок с полным верхом.
        Assert.Equal((true, false, false, false), (_blocks.CanSupport(_stone), _blocks.CanSupport(_slab), _blocks.CanSupport(_step),
            _blocks.CanSupport(_torch)));
        Assert.True(new BlockShape(null, [ShapeBox.FromPixels(0, 0, 0, 16, 4, 16), ShapeBox.FromPixels(0, 8, 0, 16, 16, 16)]).HasFullTop);

        Assert.Throws<ArgumentException>(() => new BlockShape(null, ImmutableArray<ShapeBox>.Empty));
        Assert.Throws<ArgumentException>(() => new BlockShape(null, [new ShapeBox(Vector3.Zero, new Vector3(1, 1.5f, 1))]));
        Assert.Throws<ArgumentException>(() => new BlockShape(null, [new ShapeBox(new Vector3(0, 0.5f, 0), new Vector3(1, 0.5f, 1))]));
        // Непрозрачный не-куб закрывал бы грани соседей — реестр его не примет.
        var registry = new BlockRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register("test:bad", isOpaque: true, shape: BlockShape.Slab));
    }
}
