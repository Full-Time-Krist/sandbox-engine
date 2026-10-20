using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Lighting;

/// <summary>
/// Считает свет загруженных чанков (<see cref="Chunk.Light"/>): небесный — от солнца сверху, блочный — от светящихся блоков.
/// Вызывающий сообщает о каждом изменении мира (<see cref="ChunkAdded"/>, <see cref="ChunkRemoved"/>, <see cref="BlockChanged"/>),
/// и свет пересчитывается только там, где мог измениться. Работает в одном потоке — в том, что меняет мир.
/// </summary>
/// <remarks>
/// <para>
/// Правило: непрозрачный блок света не пропускает — его небесный свет 0, блочный равен его собственному свечению.
/// В прозрачном блоке свет — наибольшее из собственного свечения и света соседей минус один. Небесный свет 15 идёт
/// вниз без потерь (столб солнца), в остальные стороны — тоже с минус одним. Прозрачный блок, не пропускающий свет
/// (<see cref="BlockDefinition.BlocksLight"/>, плита), — поглотитель: свет в него входит по тому же правилу, а соседям
/// он отдаёт только собственное свечение (небесного — ничего), столб солнца на нём обрывается. Сверху верхний слой чанка получает
/// солнце от чанка выше, а если тот не загружен — по <see cref="ISkyAbove"/>. Незагруженные соседи света не дают.
/// Результат — наименьший свет, удовлетворяющий правилу, и он не зависит от порядка изменений (это проверяют тесты
/// сверкой с расчётом «с нуля»).
/// </para>
/// <para>
/// Алгоритм — заливка двумя очередями (как в Minecraft). Снятие: у изменившегося блока свет обнуляется, и обнуление
/// идёт к соседям, чей свет мог прийти отсюда (он меньше снятого, или это столб солнца под снятым солнцем); соседи
/// со своим, не меньшим светом попадают в очередь добавления. Добавление: от каждого блока очереди свет растекается
/// к соседям, где он сейчас меньше. Так после правки пересчитывается только окрестность, а не весь мир.
/// </para>
/// </remarks>
public sealed class LightEngine
{
    private const int Down = 3;
    private const int TopLayer = ChunkMath.Size - 1;
    private const int Layer = ChunkMath.Size * ChunkMath.Size;

    /// <summary>Смещения к соседнему чанку по направлениям: +X, −X, +Y, −Y, +Z, −Z.</summary>
    private static readonly (int X, int Y, int Z)[] Directions = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private readonly ChunkMap _chunks;
    private readonly ISkyAbove _sky;
    private readonly bool[] _opaque;
    private readonly bool[] _sink;
    private readonly byte[] _emission;
    private readonly bool _hasEmitters;

    private readonly Queue<Node> _remove = new();
    private readonly Queue<Node> _add = new();
    private readonly HashSet<ChunkPos> _changed = [];
    private Chunk? _lastChanged;

    /// <summary>Блок в очереди. <see cref="Level"/> — снятый свет (для очереди снятия).</summary>
    private readonly record struct Node(Chunk Chunk, int Index, int Level);

    public LightEngine(BlockRegistry blocks, ChunkMap chunks, ISkyAbove sky)
    {
        _chunks = chunks;
        _sky = sky;
        _opaque = new bool[blocks.Count];
        _sink = new bool[blocks.Count];
        _emission = new byte[blocks.Count];
        foreach (var block in blocks.All)
        {
            _opaque[block.Id.Value] = block.IsOpaque;
            _sink[block.Id.Value] = !block.IsOpaque && block.BlocksLight;
            _emission[block.Id.Value] = block.Light;
            _hasEmitters |= block.Light > 0;
        }
    }

    /// <summary>Небесный свет блока; в незагруженном чанке — 0.</summary>
    public int GetSky(BlockPos pos) => _chunks.Get(pos.Chunk)?.Light.GetSky(pos.Local.Index) ?? 0;

    /// <summary>Блочный свет блока; в незагруженном чанке — 0.</summary>
    public int GetBlock(BlockPos pos) => _chunks.Get(pos.Chunk)?.Light.GetBlock(pos.Local.Index) ?? 0;

    /// <summary>Переносит в <paramref name="output"/> чанки, чей свет изменился с прошлого вызова.</summary>
    public void TakeChanged(List<ChunkPos> output)
    {
        output.AddRange(_changed);
        _changed.Clear();
        _lastChanged = null;
    }

    /// <summary>
    /// Чанк только что попал в <see cref="ChunkMap"/>: считает его свет и то, что он меняет у соседей. Заменить загруженный
    /// чанк другим — сначала <see cref="ChunkRemoved"/>.
    /// </summary>
    public void ChunkAdded(Chunk chunk)
    {
        chunk.Light.Fill(0);
        MarkChanged(chunk);
        var below = _chunks.Get(chunk.Position with { Y = chunk.Position.Y - 1 });

        // Внутри нового чанка темно, свет только прибавится: снимать нечего, нужны лишь источники —
        // собственные (солнце сверху, светящиеся блоки) и освещённые граничные блоки загруженных соседей.
        FillSkyColumns(chunk);
        EnqueueLitNeighborFaces(chunk, sky: true);
        Propagate(sky: true);

        // Верхний слой чанка снизу брал солнце «по рельефу», а теперь сверху настоящий чанк. Где над ним по-прежнему
        // столб солнца, ничего не изменилось; где столб оборвался — свет снимается и заливается заново. Пересчитывать
        // весь слой нельзя: каждый новый чанк воздуха сверху перезаливал бы все столбы под собой (замер шага 8.1:
        // 7 мс на чанк воздуха).
        if (below is not null)
        {
            foreach (var index in FacingLayer(2))
            {
                var aboveIndex = index - TopLayer * Layer;
                // Столб оборвался: над блоком нет солнца или там поглотитель (он солнце принимает, но вниз не пропускает).
                if (below.Light.GetSky(index) == ChunkLight.Max
                    && (chunk.Light.GetSky(aboveIndex) != ChunkLight.Max || IsSink(chunk, aboveIndex)))
                    Seed(below, index, sky: true);
            }
            Propagate(sky: true);
        }

        if (_hasEmitters)
        {
            for (var index = 0; index < ChunkMath.Volume; index++)
                Reseed(chunk, index, sky: false);
        }
        EnqueueLitNeighborFaces(chunk, sky: false);
        Propagate(sky: false);
    }

    /// <summary>
    /// Солнце в новом чанке по столбцам: от верха вниз до первого непрозрачного — 15, без очереди. В очередь добавления
    /// попадает только край освещённого: блоки, рядом с которыми есть прозрачный не на солнце (туда свет растечётся
    /// с минус одним), и граничные — к загруженным соседям по сторонам и снизу (вверх отдавать нечего: над блоком
    /// столба солнца тоже солнце). Чанк воздуха целиком под солнцем остаётся одним значением, без массива.
    /// </summary>
    private void FillSkyColumns(Chunk chunk)
    {
        var above = Neighbor(chunk.Position, 2);
        Span<bool> open = stackalloc bool[Layer];
        var openCount = 0;
        for (var column = 0; column < Layer; column++)
        {
            var top = TopLayer * Layer + column;
            open[column] = above is not null
                ? above.Light.GetSky(column) == ChunkLight.Max && !IsSink(above, column) && !IsOpaque(chunk, top)
                : Intrinsic(chunk, top, sky: true) == ChunkLight.Max;
            if (open[column])
                openCount++;
        }
        if (openCount == 0)
            return;

        if (chunk.IsEmpty && openCount == Layer)
        {
            chunk.Light.Fill(ChunkLight.Pack(ChunkLight.Max, 0));
            for (var d = 0; d < Directions.Length; d++)
            {
                // Вверх отдавать нечего: столб солнца здесь значит, что и над ним солнце (или чанк сверху не загружен).
                if (d == 2 || Neighbor(chunk.Position, d) is null)
                    continue;
                foreach (var index in FacingLayer(d))
                    _add.Enqueue(new Node(chunk, index, 0));
            }
            return;
        }

        for (var column = 0; column < Layer; column++)
        {
            if (!open[column])
                continue;
            for (var index = TopLayer * Layer + column; index >= 0 && !IsOpaque(chunk, index); index -= Layer)
            {
                chunk.Light.SetSky(index, ChunkLight.Max);
                if (IsSink(chunk, index))
                    break; // поглотитель солнце принимает, но вниз не пропускает
            }
        }

        Span<bool> loaded = stackalloc bool[Directions.Length];
        for (var d = 0; d < Directions.Length; d++)
            loaded[d] = Neighbor(chunk.Position, d) is not null;

        for (var index = 0; index < ChunkMath.Volume; index++)
        {
            if (chunk.Light.GetSky(index) != ChunkLight.Max)
                continue;
            int x = index & ChunkMath.Mask, y = index >> (ChunkMath.Bits * 2), z = (index >> ChunkMath.Bits) & ChunkMath.Mask;
            var frontier = (x == TopLayer && loaded[0]) || (x == 0 && loaded[1]) || (y == 0 && loaded[3]) || (z == TopLayer && loaded[4]) || (z == 0 && loaded[5])
                || (x < TopLayer && LitBelowMax(chunk, index + 1)) || (x > 0 && LitBelowMax(chunk, index - 1))
                || (z < TopLayer && LitBelowMax(chunk, index + ChunkMath.Size)) || (z > 0 && LitBelowMax(chunk, index - ChunkMath.Size));
            if (frontier)
                _add.Enqueue(new Node(chunk, index, 0));
        }
    }

    /// <summary>Прозрачный блок не под солнцем: сюда солнце растечётся сбоку.</summary>
    private bool LitBelowMax(Chunk chunk, int index) =>
        !IsOpaque(chunk, index) && chunk.Light.GetSky(index) != ChunkLight.Max;

    /// <summary>Освещённые граничные блоки загруженных соседей, обращённые к чанку, — в очередь добавления.</summary>
    private void EnqueueLitNeighborFaces(Chunk chunk, bool sky)
    {
        for (var d = 0; d < Directions.Length; d++)
        {
            if (Neighbor(chunk.Position, d) is not { } neighbor)
                continue;
            foreach (var index in FacingLayer(Opposite(d)))
            {
                if (Level(neighbor, index, sky) > 0)
                    _add.Enqueue(new Node(neighbor, index, 0));
            }
        }
    }

    /// <summary>
    /// Чанк <paramref name="removed"/> только что убран из <see cref="ChunkMap"/> (его свет — последний посчитанный).
    /// Уходят сразу несколько — можно сначала убрать из <see cref="ChunkMap"/> все, затем вызвать это для каждого:
    /// стороны между ушедшими пропускаются.
    /// свет, пришедший от него в соседей, пересчитывается, а верхний слой чанка снизу снова берёт солнце по <see cref="ISkyAbove"/>.
    /// </summary>
    /// <remarks>
    /// Снимается только свет, который мог прийти из ушедшего чанка, — по тому же правилу, что при снятии в заливке:
    /// у соседа меньше, чем у граничного блока ушедшего, или это столб солнца прямо под ним. Снимать все граничные блоки
    /// подряд нельзя: столб солнца у соседа сбоку снимался бы до самой земли через несколько чанков и заливался заново —
    /// в полёте, где за секунду уходит сотня чанков, это стоило до 0,9 с на проход выгрузки (замер шага 8.2).
    /// </remarks>
    public void ChunkRemoved(Chunk removed)
    {
        foreach (var sky in (ReadOnlySpan<bool>)[true, false])
        {
            for (var d = 0; d < Directions.Length; d++)
            {
                if (Neighbor(removed.Position, d) is not { } neighbor)
                    continue;
                foreach (var index in FacingLayer(Opposite(d)))
                {
                    var level = Level(neighbor, index, sky);
                    var from = Level(removed, AcrossFrom(index, d), sky);
                    var dependent = level > 0 && !IsOpaque(neighbor, index)
                        && (level < from || (sky && d == Down && from == ChunkLight.Max && level == ChunkLight.Max));
                    // Собственный свет (у верхнего слоя чанка снизу — теперь солнце по ISkyAbove) мог и вырасти.
                    var intrinsic = Intrinsic(neighbor, index, sky);
                    if (dependent && intrinsic < level)
                    {
                        SetLevel(neighbor, index, sky, 0);
                        _remove.Enqueue(new Node(neighbor, index, level));
                        Reseed(neighbor, index, sky);
                    }
                    else if (intrinsic > level)
                    {
                        SetLevel(neighbor, index, sky, intrinsic);
                        _add.Enqueue(new Node(neighbor, index, 0));
                    }
                }
            }
            Propagate(sky);
        }
    }

    /// <summary>
    /// Индекс блока соседнего чанка, от которого шаг в направлении <paramref name="d"/> ведёт в блок <paramref name="index"/>
    /// (граничный слой этого чанка, обращённый против <paramref name="d"/>).
    /// </summary>
    private static int AcrossFrom(int index, int d)
    {
        const int Last = ChunkMath.Size - 1;
        var stride = d switch
        {
            0 or 1 => 1,
            2 or 3 => Layer,
            _ => ChunkMath.Size,
        };
        return (d & 1) == 0 ? index + Last * stride : index - Last * stride;
    }

    /// <summary>Блок в <paramref name="pos"/> только что изменился: пересчитывает свет вокруг. В незагруженном чанке — ничего.</summary>
    public void BlockChanged(BlockPos pos)
    {
        if (_chunks.Get(pos.Chunk) is not { } chunk)
            return;
        var index = pos.Local.Index;
        foreach (var sky in (ReadOnlySpan<bool>)[true, false])
        {
            Seed(chunk, index, sky);
            Propagate(sky);
        }
    }

    /// <summary>
    /// Снимает свет блока и ставит его в очередь снятия; собственный свет блока (уже по новому блоку) сразу возвращается
    /// и растечётся при добавлении.
    /// </summary>
    private void Seed(Chunk chunk, int index, bool sky)
    {
        var old = Level(chunk, index, sky);
        SetLevel(chunk, index, sky, 0);
        // Даже у тёмного блока: его освещённые соседи попадут в очередь добавления и заново зальют его
        // (блок мог стать прозрачным).
        _remove.Enqueue(new Node(chunk, index, old));
        Reseed(chunk, index, sky);
    }

    private void Reseed(Chunk chunk, int index, bool sky)
    {
        var level = Intrinsic(chunk, index, sky);
        if (level == 0)
            return;
        SetLevel(chunk, index, sky, level);
        _add.Enqueue(new Node(chunk, index, 0));
    }

    private void Propagate(bool sky)
    {
        while (_remove.TryDequeue(out var node))
        {
            for (var d = 0; d < Directions.Length; d++)
            {
                if (!Step(node.Chunk, node.Index, d, out var chunk, out var index))
                    continue;
                var level = Level(chunk, index, sky);
                if (level == 0)
                    continue;

                // Свет соседа мог прийти отсюда — снимаем и его. Непрозрачный сосед светится сам, от соседей не зависит.
                var dependent = !IsOpaque(chunk, index)
                    && (level < node.Level || (sky && d == Down && node.Level == ChunkLight.Max && level == ChunkLight.Max));
                if (dependent)
                {
                    SetLevel(chunk, index, sky, 0);
                    _remove.Enqueue(new Node(chunk, index, level));
                    Reseed(chunk, index, sky);
                }
                else
                {
                    // Свой свет, не отсюда: он заново зальёт снятое.
                    _add.Enqueue(new Node(chunk, index, 0));
                }
            }
        }

        while (_add.TryDequeue(out var node))
        {
            var level = Outgoing(node.Chunk, node.Index, sky);
            if (level == 0)
                continue;
            for (var d = 0; d < Directions.Length; d++)
            {
                if (!Step(node.Chunk, node.Index, d, out var chunk, out var index) || IsOpaque(chunk, index))
                    continue;
                var next = sky && d == Down && level == ChunkLight.Max ? ChunkLight.Max : level - 1;
                if (next > Level(chunk, index, sky))
                {
                    SetLevel(chunk, index, sky, next);
                    _add.Enqueue(new Node(chunk, index, 0));
                }
            }
        }
    }

    /// <summary>Собственный свет блока, не от соседей: свечение или солнце сверху над верхним загруженным чанком.</summary>
    private int Intrinsic(Chunk chunk, int index, bool sky)
    {
        var block = chunk.Blocks[index].Value;
        if (!sky)
            return block < _emission.Length ? _emission[block] : 0;

        if (index >> (ChunkMath.Bits * 2) != TopLayer || IsOpaque(block))
            return 0;
        var above = chunk.Position with { Y = chunk.Position.Y + 1 };
        if (_chunks.Contains(above))
            return 0; // солнце придёт от чанка сверху заливкой
        var origin = above.Origin;
        return _sky.IsOpen(origin.X + (index & ChunkMath.Mask), origin.Z + ((index >> ChunkMath.Bits) & ChunkMath.Mask), origin.Y)
            ? ChunkLight.Max
            : 0;
    }

    /// <summary>Сосед блока в направлении <paramref name="d"/>; false — он в незагруженном чанке.</summary>
    private bool Step(Chunk chunk, int index, int d, out Chunk neighbor, out int neighborIndex)
    {
        const int Last = ChunkMath.Size - 1;
        var (coordinate, stride) = d switch
        {
            0 or 1 => (index & ChunkMath.Mask, 1),
            2 or 3 => (index >> (ChunkMath.Bits * 2), Layer),
            _ => ((index >> ChunkMath.Bits) & ChunkMath.Mask, ChunkMath.Size),
        };
        var positive = (d & 1) == 0;

        if (positive ? coordinate < Last : coordinate > 0)
        {
            neighbor = chunk;
            neighborIndex = positive ? index + stride : index - stride;
            return true;
        }

        if (Neighbor(chunk.Position, d) is not { } next)
        {
            neighbor = null!;
            neighborIndex = 0;
            return false;
        }
        neighbor = next;
        neighborIndex = positive ? index - Last * stride : index + Last * stride;
        return true;
    }

    private Chunk? Neighbor(ChunkPos position, int d)
    {
        var (x, y, z) = Directions[d];
        return _chunks.Get(new ChunkPos(position.X + x, position.Y + y, position.Z + z));
    }

    private static int Opposite(int d) => d ^ 1;

    /// <summary>Индексы слоя чанка, обращённого в сторону <paramref name="d"/> (например, для +Y — верхний слой).</summary>
    private static IEnumerable<int> FacingLayer(int d)
    {
        var fixedValue = (d & 1) == 0 ? ChunkMath.Size - 1 : 0;
        for (var a = 0; a < ChunkMath.Size; a++)
        for (var b = 0; b < ChunkMath.Size; b++)
        {
            yield return d switch
            {
                0 or 1 => new LocalPos(fixedValue, a, b).Index,
                2 or 3 => new LocalPos(a, fixedValue, b).Index,
                _ => new LocalPos(a, b, fixedValue).Index,
            };
        }
    }

    private bool IsOpaque(Chunk chunk, int index) => IsOpaque(chunk.Blocks[index].Value);

    private bool IsSink(Chunk chunk, int index) => chunk.Blocks[index].Value is var block && block < _sink.Length && _sink[block];

    /// <summary>Сколько света блок отдаёт соседям: свой свет, а поглотитель — только собственное свечение.</summary>
    private int Outgoing(Chunk chunk, int index, bool sky) =>
        !IsSink(chunk, index) ? Level(chunk, index, sky) : sky ? 0 : Math.Min(Level(chunk, index, sky), Intrinsic(chunk, index, sky: false));

    // Id не из реестра (испорченные данные) — непрозрачный, как в физике он твёрдый.
    private bool IsOpaque(ushort block) => block >= _opaque.Length || _opaque[block];

    private static int Level(Chunk chunk, int index, bool sky) =>
        sky ? chunk.Light.GetSky(index) : chunk.Light.GetBlock(index);

    private void SetLevel(Chunk chunk, int index, bool sky, int level)
    {
        if (sky)
            chunk.Light.SetSky(index, level);
        else
            chunk.Light.SetBlock(index, level);
        MarkChanged(chunk);
    }

    private void MarkChanged(Chunk chunk)
    {
        if (ReferenceEquals(chunk, _lastChanged))
            return;
        _changed.Add(chunk.Position);
        _lastChanged = chunk;
    }
}
