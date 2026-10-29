using System.Numerics;
using Shared;
using Shared.Blocks;
using Shared.Content;
using Shared.Entities;
using Shared.Mobs;
using Shared.Physics;
using Shared.Voxels;

namespace Server.Entities;

/// <summary>Чем занят моб.</summary>
internal enum BrainState : byte
{
    /// <summary>Стоит и ждёт (<see cref="Brain.Timer"/>), потом пойдёт бродить.</summary>
    Idle,

    /// <summary>Бредёт к случайной точке рядом.</summary>
    Wander,

    /// <summary>Идёт к игроку (<see cref="Brain.Target"/>).</summary>
    Chase,

    /// <summary>Убегает от того, кто ударил (<see cref="Brain.FleeFrom"/>), пока не выйдет <see cref="Brain.Timer"/>.</summary>
    Flee,
}

/// <summary>Состояние поведения моба (шаг 9.3): что делает, к кому идёт, каким путём.</summary>
internal struct Brain
{
    public BrainState State;

    /// <summary>Тиков до следующего решения: сколько ещё стоять (<see cref="BrainState.Idle"/>) или бежать (<see cref="BrainState.Flee"/>).</summary>
    public int Timer;

    /// <summary>Сущность игрока, к которому идёт (<see cref="BrainState.Chase"/>).</summary>
    public EntityId Target;

    /// <summary>Откуда бежать (<see cref="BrainState.Flee"/>).</summary>
    public Vector3d FleeFrom;

    /// <summary>Куда нужен путь: поведение ставит точку и <see cref="WantsPath"/>, путь ищет <see cref="MobAi"/> в пределах бюджета тика.</summary>
    public Vector3d Destination;
    public bool WantsPath;

    /// <summary>Тиков до нового поиска пути к той же цели (преследование).</summary>
    public int RepathIn;

    public MobPath? Path;

    /// <summary>Какая точка пути следующая.</summary>
    public int Waypoint;

    /// <summary>Где моб был при прошлой проверке застревания и сколько тиков до следующей.</summary>
    public Vector3d LastCheck;
    public int StuckCheckIn;
}

/// <summary>
/// Поведение моба по имени (<c>behavior</c> в JSON, как у предметов): решает, куда идти и на что смотреть. Сам путь
/// ищет и ведёт по нему <see cref="MobAi"/>.
/// </summary>
internal abstract class MobBehavior
{
    public abstract void Think(MobAi ai, EntityId mob, ref Brain brain);
}

/// <summary>
/// Мозги мобов — система сервера (шаг 9.3), в тике перед ходьбой (<see cref="MobEntities.Move"/>): поведение решает,
/// поиск пути — в общем бюджете тика, ведение по пути задаёт мобу направление ходьбы.
/// </summary>
internal sealed class MobAi
{
    public const string PassiveName = "base:passive";
    public const string HostileMeleeName = "base:hostile_melee";

    /// <summary>
    /// Сколько клеток могут раскрыть все поиски пути за тик. Поиск начинается, только если в бюджете есть место на целый
    /// (<see cref="PathFinder.MaxNodes"/>); кто не успел — ищет в следующем тике, а пока идёт старым путём. Полный поиск
    /// (цель недостижима) — 2,1 мс в Debug и 0,25 мс в Release (замер шага 9.3), поэтому два: до ~4 мс тика в худшем
    /// случае; обычный поиск по открытому месту раскрывает десятки клеток.
    /// </summary>
    public const int NodesPerTick = 2 * PathFinder.MaxNodes;

    /// <summary>Как далеко уходит бродящий моб от места, где стоял, блоков.</summary>
    public const int WanderRadius = 8;

    /// <summary>Сколько стоит между прогулками, тиков: от 3 до 8 секунд.</summary>
    public const int MinIdleTicks = 3 * GameTime.TicksPerSecond, MaxIdleTicks = 8 * GameTime.TicksPerSecond;

    /// <summary>Точка пути пройдена, когда до её центра по горизонтали меньше этого, блоков.</summary>
    public const double WaypointReach = 0.35;

    /// <summary>Раз в сколько тиков проверяется, сдвинулся ли идущий моб; не сдвинулся на 0,3 блока — путь брошен.</summary>
    public const int StuckCheckTicks = GameTime.TicksPerSecond;

    /// <summary>Насколько голова поворачивается относительно тела, радианы (75°).</summary>
    public const float MaxHeadTurn = 75 * MathF.PI / 180;

    private readonly Dictionary<string, MobBehavior> _behaviors = new(StringComparer.Ordinal)
    {
        [PassiveName] = new PassiveBehavior(),
        [HostileMeleeName] = new HostileMeleeBehavior(),
    };

    private readonly MobBehavior[] _byMob;
    private int _budget;

    /// <summary>Поиск пути с запасёнными словарями — один на сервер (мир один, поток один).</summary>
    private PathFinder? _finder;

    /// <exception cref="ContentLoadException">Моб ссылается на поведение, которого нет.</exception>
    public MobAi(MobRegistry mobs, Random random)
    {
        Mobs = mobs;
        Random = random;
        _byMob = new MobBehavior[mobs.Count + 1];
        foreach (var mob in mobs.All)
        {
            _byMob[mob.Id.Value] = _behaviors.TryGetValue(mob.Behavior, out var behavior)
                ? behavior
                : throw new ContentLoadException($"Моб '{mob.Name}' ссылается на неизвестное поведение '{mob.Behavior}'; " +
                                                 $"есть {string.Join(", ", _behaviors.Keys.Order(StringComparer.Ordinal))}.");
        }
    }

    public MobRegistry Mobs { get; }
    public Random Random { get; }

    // Мир этого тика — для поведений (заполняется в Run).
    public EntityWorld Entities { get; private set; } = null!;
    public ChunkMap Chunks { get; private set; } = null!;
    public BlockRegistry Blocks { get; private set; } = null!;
    public IReadOnlyList<ClientSession> Sessions { get; private set; } = [];

    /// <summary>Сколько поисков пути прошло в последнем тике (для тестов бюджета и замеров).</summary>
    public int SearchesLastTick { get; private set; }

    /// <summary>Номер тика мозгов: по нему мобы смотрят вокруг вразнобой, а не все в один тик.</summary>
    public long Tick { get; private set; }

    public void Run(EntityWorld entities, ChunkMap chunks, BlockRegistry blocks, IReadOnlyList<ClientSession> sessions)
    {
        (Entities, Chunks, Blocks, Sessions) = (entities, chunks, blocks, sessions);
        _budget = NodesPerTick;
        SearchesLastTick = 0;
        Tick++;
        foreach (var entity in entities.Query<Mob, Brain>())
        {
            ref var brain = ref entities.Get<Brain>(entity);
            _byMob[entities.Get<Mob>(entity).Kind.Value].Think(this, entity, ref brain);
            if (brain.WantsPath && _budget >= PathFinder.MaxNodes)
                Search(entity, ref brain);
            Follow(entity, ref brain);
        }
    }

    /// <summary>Моба ударили (шаг 9.4): мирный убегает от точки <paramref name="from"/> несколько секунд.</summary>
    public static void Panic(EntityWorld entities, EntityId mob, Vector3d from, Random random)
    {
        ref var brain = ref entities.Get<Brain>(mob);
        brain.State = BrainState.Flee;
        brain.FleeFrom = from;
        brain.Timer = random.Next(3 * GameTime.TicksPerSecond, 5 * GameTime.TicksPerSecond);
        brain.Path = null;
        brain.WantsPath = false;
    }

    public Vector3d PositionOf(EntityId entity) => Entities.Get<Position>(entity).Value;

    /// <summary>Попросить путь к точке: найдётся в этом тике, если хватит бюджета, иначе в следующих.</summary>
    public static void RequestPath(ref Brain brain, Vector3d destination)
    {
        brain.Destination = destination;
        brain.WantsPath = true;
    }

    /// <summary>Стоять и ждать от <see cref="MinIdleTicks"/> до <see cref="MaxIdleTicks"/>.</summary>
    public void Rest(ref Brain brain)
    {
        brain.State = BrainState.Idle;
        brain.Timer = Random.Next(MinIdleTicks, MaxIdleTicks);
        brain.Path = null;
        brain.WantsPath = false;
    }

    /// <summary>Общая часть поведений: постоять, потом пойти к случайной точке рядом, дойти (или застрять) — снова постоять.</summary>
    public void Wander(EntityId mob, ref Brain brain)
    {
        if (brain.State is not (BrainState.Idle or BrainState.Wander))
            Rest(ref brain);
        if (brain.State == BrainState.Wander)
        {
            if (brain.Path == null && !brain.WantsPath)
                Rest(ref brain); // дошёл, застрял или пути нет
            return;
        }
        if (--brain.Timer > 0)
            return;
        var here = PositionOf(mob);
        brain.State = BrainState.Wander;
        RequestPath(ref brain, here + new Vector3d(Random.Next(-WanderRadius, WanderRadius + 1), 0, Random.Next(-WanderRadius, WanderRadius + 1)));
        Entities.Get<Mob>(mob).LookAt = null;
    }

    private void Search(EntityId mob, ref Brain brain)
    {
        var definition = Mobs.Get(Entities.Get<Mob>(mob).Kind);
        var from = PositionOf(mob);
        _finder ??= new PathFinder(Chunks, Blocks);
        var path = _finder.Search(MobEntities.WalkerOf(definition), from, brain.Destination);
        _budget -= path?.Expanded ?? PathFinder.MaxNodes;
        SearchesLastTick++;
        brain.WantsPath = false;
        brain.Path = path;
        brain.Waypoint = 0;
        brain.LastCheck = from;
        brain.StuckCheckIn = StuckCheckTicks;
    }

    /// <summary>
    /// Ведёт по пути: направление ходьбы — к центру следующей точки по горизонтали; дошёл — следующая; последняя — путь
    /// пройден. За <see cref="StuckCheckTicks"/> не сдвинулся на 0,3 блока — путь бросается (поведение решит, что дальше).
    /// </summary>
    private void Follow(EntityId entity, ref Brain brain)
    {
        ref var mob = ref Entities.Get<Mob>(entity);
        mob.Move = Vector2.Zero;
        if (brain.Path is not { } path)
            return;
        var here = PositionOf(entity);
        while (brain.Waypoint < path.Points.Length && Horizontal(path.Points[brain.Waypoint] - here).Length() < WaypointReach)
            brain.Waypoint++;
        if (brain.Waypoint >= path.Points.Length)
        {
            brain.Path = null;
            return;
        }
        if (--brain.StuckCheckIn <= 0)
        {
            if (Horizontal(here - brain.LastCheck).Length() < 0.3 && Math.Abs(here.Y - brain.LastCheck.Y) < 0.3)
            {
                brain.Path = null;
                return;
            }
            brain.LastCheck = here;
            brain.StuckCheckIn = StuckCheckTicks;
        }
        var direction = Horizontal(path.Points[brain.Waypoint] - here);
        mob.Move = Vector2.Normalize(direction);
    }

    private static Vector2 Horizontal(Vector3d delta) => new((float)delta.X, (float)delta.Z);
}

/// <summary>Мирный моб (<c>base:passive</c>, овца): бродит; ударили — убегает от удара бегом.</summary>
internal sealed class PassiveBehavior : MobBehavior
{
    /// <summary>Как далеко бежит за один рывок, блоков.</summary>
    public const int FleeDistance = 8;

    public override void Think(MobAi ai, EntityId mob, ref Brain brain)
    {
        ref var state = ref ai.Entities.Get<Mob>(mob);
        if (brain.State != BrainState.Flee)
        {
            state.Sprint = false;
            ai.Wander(mob, ref brain);
            return;
        }
        state.Sprint = true;
        state.LookAt = null;
        if (--brain.Timer <= 0)
        {
            state.Sprint = false;
            ai.Rest(ref brain);
            return;
        }
        if (brain.Path == null && !brain.WantsPath)
        {
            // Прочь от удара, чуть вбок наугад: иначе у стены моб упирался бы в неё раз за разом.
            var here = ai.PositionOf(mob);
            var away = here - brain.FleeFrom;
            var direction = new Vector2((float)away.X, (float)away.Z);
            direction = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
            var angle = (ai.Random.NextSingle() - 0.5f) * MathF.PI / 2;
            var (sin, cos) = MathF.SinCos(angle);
            direction = new Vector2(direction.X * cos - direction.Y * sin, direction.X * sin + direction.Y * cos);
            MobAi.RequestPath(ref brain, here + new Vector3d(direction.X * FleeDistance, 0, direction.Y * FleeDistance));
        }
    }
}

/// <summary>
/// Враг ближнего боя (<c>base:hostile_melee</c>, зомби): бродит, пока не увидит игрока в выживании; увидел — идёт к нему
/// (путь заново раз в секунду или когда игрок ушёл на 2 блока от цели пути), смотрит на него и останавливается рядом
/// (удар — шаг 9.4). Бросает, когда игрок ушёл дальше <see cref="LoseRange"/>, стал творческим, погиб или вышел.
/// </summary>
internal sealed class HostileMeleeBehavior : MobBehavior
{
    /// <summary>На каком расстоянии замечает игрока, блоков (если видит: между глазами нет блоков).</summary>
    public const double DetectRange = 16;

    /// <summary>Дальше скольки блоков бросает погоню.</summary>
    public const double LoseRange = 24;

    /// <summary>Раз в сколько тиков ищет игрока, пока не преследует.</summary>
    public const int ScanTicks = 10;

    /// <summary>Как часто ищет путь к игроку заново, тиков, — и на сколько блоков игрок должен уйти от цели пути, чтобы раньше.</summary>
    public const int RepathTicks = GameTime.TicksPerSecond;
    public const double RepathDistance = 2;

    /// <summary>Ближе этого (по горизонтали, от точки до точки ног) к игроку не подходит — дальше удар (шаг 9.4), блоков.</summary>
    public const double StopDistance = 1.2;

    public override void Think(MobAi ai, EntityId mob, ref Brain brain)
    {
        ref var state = ref ai.Entities.Get<Mob>(mob);
        state.Sprint = false;
        var here = ai.PositionOf(mob);

        if (brain.State == BrainState.Chase && Eligible(ai, brain.Target) is { } target)
        {
            var to = target - here;
            if (to.Length() > LoseRange)
            {
                Lose(ai, mob, ref brain);
                return;
            }
            state.LookAt = target + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
            if (Math.Sqrt(to.X * to.X + to.Z * to.Z) < StopDistance)
            {
                brain.Path = null;
                brain.WantsPath = false;
                return;
            }
            if (--brain.RepathIn <= 0 || (brain.Destination - target).Length() > RepathDistance || brain.Path == null && !brain.WantsPath)
            {
                MobAi.RequestPath(ref brain, target);
                brain.RepathIn = RepathTicks;
            }
            return;
        }
        if (brain.State == BrainState.Chase)
        {
            Lose(ai, mob, ref brain);
            return;
        }

        if ((mob.Index + ai.Tick) % ScanTicks == 0 && Spot(ai, mob, here) is { } player)
        {
            brain.State = BrainState.Chase;
            brain.Target = player;
            brain.Path = null;
            brain.RepathIn = 0;
            return;
        }
        ai.Wander(mob, ref brain);
    }

    private static void Lose(MobAi ai, EntityId mob, ref Brain brain)
    {
        ai.Entities.Get<Mob>(mob).LookAt = null;
        ai.Rest(ref brain);
    }

    /// <summary>Ноги игрока-цели, если за ним ещё стоит идти: подключён, жив, в выживании; иначе null.</summary>
    private static Vector3d? Eligible(MobAi ai, EntityId player)
    {
        foreach (var session in ai.Sessions)
        {
            if (session.IsJoined && session.Entity == player)
                return !session.IsDead && session.Mode == GameMode.Survival ? session.PlayerPosition : null;
        }
        return null;
    }

    /// <summary>Ближайший игрок в выживании ближе <see cref="DetectRange"/>, которого моб видит: от глаз до глаз нет блоков.</summary>
    private static EntityId? Spot(MobAi ai, EntityId mob, Vector3d here)
    {
        var definition = ai.Mobs.Get(ai.Entities.Get<Mob>(mob).Kind);
        var eye = here + new Vector3d(0, definition.Height * 0.85, 0);
        EntityId? best = null;
        var bestDistance = DetectRange;
        foreach (var session in ai.Sessions)
        {
            if (!session.IsJoined || session.IsDead || session.Mode != GameMode.Survival)
                continue;
            var playerEye = session.PlayerPosition + new Vector3d(0, PlayerPhysics.EyeHeight, 0);
            var to = playerEye - eye;
            var distance = to.Length();
            if (distance >= bestDistance)
                continue;
            var hit = VoxelRaycast.Cast(ai.Chunks, ai.Blocks, eye, to.ToVector3(), (float)distance);
            if (hit is null)
                (best, bestDistance) = (session.Entity, distance);
        }
        return best;
    }
}
