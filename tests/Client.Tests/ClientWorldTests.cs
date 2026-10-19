using Client.Rendering;
using Shared.Blocks;
using Shared.Lighting;
using Shared.Protocol;
using Shared.Voxels;

namespace Client.Tests;

public class ClientWorldTests
{
    private static List<ChunkPos> TakeDirty(ClientWorld world)
    {
        var dirty = new List<ChunkPos>();
        world.TakeDirty(dirty);
        return dirty;
    }

    [Fact]
    public void AddChunk_MarksItDirty()
    {
        var world = new ClientWorld();

        world.AddChunk(new Chunk(new ChunkPos(0, 0, 0)));

        Assert.Equal([new ChunkPos(0, 0, 0)], TakeDirty(world));
        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void AddChunk_MarksLoadedNeighborsDirty_ButNotOthers()
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(new ChunkPos(1, 0, 0)));   // сосед по +X
        world.AddChunk(new Chunk(new ChunkPos(1, 1, 0)));   // по ребру: его рамка тоже содержит край нового чанка
        world.AddChunk(new Chunk(new ChunkPos(-1, 1, -1))); // по углу
        world.AddChunk(new Chunk(new ChunkPos(2, 0, 0)));   // через один — не сосед
        world.AddChunk(new Chunk(new ChunkPos(5, 0, 0)));   // далеко
        TakeDirty(world);

        world.AddChunk(new Chunk(new ChunkPos(0, 0, 0)));

        Assert.Equal(new HashSet<ChunkPos> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(-1, 1, -1) }, TakeDirty(world).ToHashSet());
    }

    /// <summary>Чанк в начале координат и все 26 его соседей, «грязные» сброшены.</summary>
    private static ClientWorld CreateWorldWithAllNeighbors()
    {
        var world = new ClientWorld();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
            world.AddChunk(new Chunk(new ChunkPos(dx, dy, dz)));
        TakeDirty(world);
        return world;
    }

    [Fact]
    public void SetBlock_OnEdge_MarksFaceAndEdgeNeighbors()
    {
        var world = CreateWorldWithAllNeighbors();

        world.SetBlock(new BlockPos(31, 0, 10), new BlockId(1));

        Assert.Equal(new HashSet<ChunkPos> { new(0, 0, 0), new(1, 0, 0), new(0, -1, 0), new(1, -1, 0) }, TakeDirty(world).ToHashSet());
    }

    [Fact]
    public void SetBlock_InCorner_MarksSevenNeighbors()
    {
        var world = CreateWorldWithAllNeighbors();

        world.SetBlock(new BlockPos(0, 31, 0), new BlockId(1));

        var expected = new HashSet<ChunkPos>();
        foreach (var dx in new[] { 0, -1 })
        foreach (var dy in new[] { 0, 1 })
        foreach (var dz in new[] { 0, -1 })
            expected.Add(new ChunkPos(dx, dy, dz));
        Assert.Equal(expected, TakeDirty(world).ToHashSet());
    }

    /// <summary>Свет чанка (0, 0, 0) с сервера: тот же, что сейчас, но с изменённым светом блока <paramref name="changed"/>.</summary>
    private static ReadOnlyMemory<byte> LightWithChange(ClientWorld world, LocalPos changed)
    {
        var copy = new Chunk(new ChunkPos(0, 0, 0));
        var values = new byte[ChunkMath.Volume];
        world.Chunks.Get(new ChunkPos(0, 0, 0))!.Light.CopyTo(values);
        copy.Light.CopyFrom(values);
        copy.Light.SetSky(changed.Index, copy.Light.GetSky(changed.Index) == 7 ? 8 : 7);
        return ChunkLightChanged.From(copy).Light.AsMemory();
    }

    [Fact]
    public void SetLight_Inside_MarksOnlyItsChunk()
    {
        var world = CreateWorldWithAllNeighbors();

        Assert.True(world.SetLight(new ChunkPos(0, 0, 0), LightWithChange(world, new LocalPos(10, 10, 10))));

        Assert.Equal([new ChunkPos(0, 0, 0)], TakeDirty(world));
        Assert.Equal(7, world.Chunks.Get(new ChunkPos(0, 0, 0))!.Light.GetSky(new LocalPos(10, 10, 10).Index));
    }

    [Fact]
    public void SetLight_AtEdge_MarksNeighborsReadingThatEdge()
    {
        var world = CreateWorldWithAllNeighbors();

        world.SetLight(new ChunkPos(0, 0, 0), LightWithChange(world, new LocalPos(10, 0, 31)));

        Assert.Equal(new HashSet<ChunkPos> { new(0, 0, 0), new(0, -1, 0), new(0, 0, 1), new(0, -1, 1) }, TakeDirty(world).ToHashSet());
    }

    [Fact]
    public void SetLight_Unchanged_MarksNothing()
    {
        var world = CreateWorldWithAllNeighbors();
        var same = ChunkLightChanged.From(world.Chunks.Get(new ChunkPos(0, 0, 0))!).Light.AsMemory();

        Assert.True(world.SetLight(new ChunkPos(0, 0, 0), same));

        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void LightAt_ReadsChunkLight_OrFullSkyWithoutChunk()
    {
        var world = new ClientWorld();
        var chunk = new Chunk(new ChunkPos(0, 0, 0));
        chunk.Light.SetBlock(new LocalPos(1, 2, 3).Index, 9);
        world.AddChunk(chunk);

        Assert.Equal(ChunkLight.Pack(0, 9), world.LightAt(new BlockPos(1, 2, 3)));
        Assert.Equal(ChunkLight.Pack(15, 0), world.LightAt(new BlockPos(100, 2, 3)));
    }

    private static ClientWorld CreateWorldAround(ChunkPos center)
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(center));
        foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) })
            world.AddChunk(new Chunk(new ChunkPos(center.X + dx, center.Y + dy, center.Z + dz)));
        TakeDirty(world);
        return world;
    }

    [Fact]
    public void SetBlock_Inside_MarksOnlyItsChunk()
    {
        var world = CreateWorldAround(new ChunkPos(0, 0, 0));

        Assert.True(world.SetBlock(new BlockPos(10, 10, 10), new BlockId(1)));

        Assert.Equal([new ChunkPos(0, 0, 0)], TakeDirty(world));
    }

    [Theory]
    [InlineData(0, 10, 10, -1, 0, 0)]
    [InlineData(31, 10, 10, 1, 0, 0)]
    [InlineData(10, 0, 10, 0, -1, 0)]
    [InlineData(10, 31, 10, 0, 1, 0)]
    [InlineData(10, 10, 0, 0, 0, -1)]
    [InlineData(10, 10, 31, 0, 0, 1)]
    public void SetBlock_OnBorder_AlsoMarksNeighborBehindIt(int x, int y, int z, int nx, int ny, int nz)
    {
        var world = CreateWorldAround(new ChunkPos(0, 0, 0));

        world.SetBlock(new BlockPos(x, y, z), new BlockId(1));

        var dirty = TakeDirty(world);
        Assert.Equal(2, dirty.Count);
        Assert.Contains(new ChunkPos(0, 0, 0), dirty);
        Assert.Contains(new ChunkPos(nx, ny, nz), dirty);
    }


    [Fact]
    public void SetBlock_InMissingChunk_Fails()
    {
        var world = new ClientWorld();

        Assert.False(world.SetBlock(new BlockPos(0, 0, 0), new BlockId(1)));
        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void GetNeighbors_ReturnsChunksOnEachSide()
    {
        var world = new ClientWorld();
        var center = new ChunkPos(-1, 0, 2);
        world.AddChunk(new Chunk(center));
        world.AddChunk(new Chunk(new ChunkPos(-2, 0, 2)));
        world.AddChunk(new Chunk(new ChunkPos(-1, 0, 3)));

        var neighbors = world.GetNeighbors(center);

        Assert.Equal(new ChunkPos(-2, 0, 2), neighbors.NegX?.Position);
        Assert.Equal(new ChunkPos(-1, 0, 3), neighbors.PosZ?.Position);
        Assert.Null(neighbors.PosX);
        Assert.Null(neighbors.NegY);
        Assert.Null(neighbors.PosY);
        Assert.Null(neighbors.NegZ);
    }

    private static Chunk ChunkWithBlock(ChunkPos position)
    {
        var chunk = new Chunk(position);
        chunk.Set(new LocalPos(1, 1, 1), new BlockId(1));
        return chunk;
    }

    private static readonly ChunkPos[] Sides =
    [
        new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1),
    ];

    [Fact]
    public void RemoveChunk_RemovesIt_AndMarksItDirtyForMeshRemoval()
    {
        var world = new ClientWorld();
        var position = new ChunkPos(2, 0, 0);
        world.AddChunk(ChunkWithBlock(position));
        TakeDirty(world);

        world.RemoveChunk(position);

        Assert.False(world.Chunks.Contains(position));
        Assert.Equal([position], TakeDirty(world));
    }

    [Fact]
    public void RemoveChunk_Missing_DoesNothing()
    {
        var world = new ClientWorld();

        world.RemoveChunk(new ChunkPos(5, 5, 5));

        Assert.Equal(0, world.DirtyCount);
    }

    [Fact]
    public void CanMesh_OnlyWhenAllSixNeighborsLoaded()
    {
        var world = new ClientWorld();
        var center = new ChunkPos(0, 0, 0);
        world.AddChunk(ChunkWithBlock(center));

        foreach (var side in Sides)
        {
            Assert.False(world.CanMesh(center), $"Меш строится без соседа {side}");
            world.AddChunk(new Chunk(side));
        }

        Assert.True(world.CanMesh(center));
    }

    [Fact]
    public void CanMesh_EmptyChunk_NeedsNoNeighbors()
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(new ChunkPos(0, 3, 0)));

        Assert.True(world.CanMesh(new ChunkPos(0, 3, 0)));
        Assert.False(world.CanMesh(new ChunkPos(9, 9, 9)));
    }

    [Fact]
    public void LastNeighborArrival_MarksChunkDirtyAgain()
    {
        // Без этого чанк, пропущенный рендером из-за нехватки соседа, так и остался бы без меша.
        var world = new ClientWorld();
        var center = new ChunkPos(0, 0, 0);
        world.AddChunk(ChunkWithBlock(center));
        foreach (var side in Sides[..^1])
            world.AddChunk(new Chunk(side));
        TakeDirty(world);

        world.AddChunk(new Chunk(Sides[^1]));

        Assert.Contains(center, TakeDirty(world));
        Assert.True(world.CanMesh(center));
    }

    [Fact]
    public void ChunkData_BringsServerLight()
    {
        var server = new Chunk(new ChunkPos(0, 1, 0));
        server.Light.SetSky(5, 15);
        server.Light.SetBlock(6, 9);
        var world = new ClientWorld();

        world.AddChunk(ChunkData.From(server).ToChunk());

        var light = world.Chunks.Get(server.Position)!.Light;
        Assert.Equal(15, light.GetSky(5));
        Assert.Equal(9, light.GetBlock(6));
        Assert.Equal(0, light.Get(7));
    }

    [Fact]
    public void SetLight_ReplacesLightOfChunk()
    {
        var position = new ChunkPos(2, 0, -1);
        var world = new ClientWorld();
        world.AddChunk(new Chunk(position));
        var server = new Chunk(position);
        server.Light.Fill(ChunkLight.Pack(15, 0));
        server.Light.SetSky(100, 4);

        Assert.True(world.SetLight(position, ChunkLightChanged.From(server).Light.AsMemory()));

        var light = world.Chunks.Get(position)!.Light;
        Assert.Equal(4, light.GetSky(100));
        Assert.Equal(15, light.GetSky(101));
    }

    [Fact]
    public void SetLight_ForMissingChunk_IsIgnored()
    {
        var world = new ClientWorld();
        var data = ChunkLightChanged.From(new Chunk(new ChunkPos(0, 0, 0))).Light;

        Assert.False(world.SetLight(new ChunkPos(0, 0, 0), data.AsMemory()));
        Assert.Equal(0, world.Chunks.Count);
    }

    [Fact]
    public void IsSurrounded_OnlyWithAll26Neighbors()
    {
        var world = new ClientWorld();
        world.AddChunk(ChunkWithBlock(default));
        var around = new List<ChunkPos>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx != 0 || dy != 0 || dz != 0)
                around.Add(new ChunkPos(dx, dy, dz));
        }

        // Соседи по граням — первыми: CanMesh уже да, а окружён ли — нет, пока не пришёл последний угол.
        foreach (var position in around.OrderBy(p => Math.Abs(p.X) + Math.Abs(p.Y) + Math.Abs(p.Z)))
        {
            Assert.False(world.IsSurrounded(default), $"Окружён без соседа {position}");
            world.AddChunk(new Chunk(position));
        }

        Assert.True(world.CanMesh(default));
        Assert.True(world.IsSurrounded(default));
        Assert.False(world.IsSurrounded(new ChunkPos(9, 9, 9)));
    }

    [Fact]
    public void IsSurrounded_DoesNotWaitForNeighborsBeyondLoadArea()
    {
        // Область — цилиндр радиуса 2 и ±1 по высоте вокруг (0, 0, 0). Чанк (2, 1, 0) на её краю: его соседи
        // с x = 3 или y = 2 за областью и не придут — ждать их незачем; соседи внутри области — ждём.
        var area = new ChunkArea(2, 1);
        var world = new ClientWorld { LoadArea = area, Center = default };
        var edge = new ChunkPos(2, 1, 0);
        world.AddChunk(ChunkWithBlock(edge));
        var inside = new List<ChunkPos>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var neighbor = new ChunkPos(edge.X + dx, edge.Y + dy, edge.Z + dz);
            if (neighbor != edge && area.Contains(default, neighbor))
                inside.Add(neighbor);
        }
        Assert.True(inside.Count is > 0 and < 26);

        foreach (var neighbor in inside)
        {
            Assert.False(world.IsSurrounded(edge), $"Окружён без соседа {neighbor} внутри области");
            world.AddChunk(new Chunk(neighbor));
        }
        Assert.True(world.IsSurrounded(edge));

        // Игрок ушёл: те же соседи теперь внутри области и снова нужны.
        world.Center = new ChunkPos(2, 1, 0);
        Assert.False(world.IsSurrounded(edge));
    }

    [Fact]
    public void IsSurrounded_EmptyChunk_Always()
    {
        var world = new ClientWorld();
        world.AddChunk(new Chunk(new ChunkPos(0, 3, 0)));

        Assert.True(world.IsSurrounded(new ChunkPos(0, 3, 0)));
    }

    [Fact]
    public void ReadyToMesh_WaitsForNeighbors_OrForQuiet()
    {
        var world = new ClientWorld();
        world.AddChunk(ChunkWithBlock(default));
        foreach (var side in Sides)
            world.AddChunk(new Chunk(side));
        var justChanged = TimeSpan.Zero;

        // Соседи по граням есть, по рёбрам — нет: ждём, пока чанк не успокоится.
        Assert.False(ChunkRenderer.ReadyToMesh(world, default, justChanged));
        Assert.False(ChunkRenderer.ReadyToMesh(world, default, ChunkRenderer.SettleTime - TimeSpan.FromMilliseconds(1)));
        Assert.True(ChunkRenderer.ReadyToMesh(world, default, ChunkRenderer.SettleTime));

        // Окружённый — сразу.
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (!world.Chunks.Contains(new ChunkPos(dx, dy, dz)))
                world.AddChunk(new Chunk(new ChunkPos(dx, dy, dz)));
        }
        Assert.True(ChunkRenderer.ReadyToMesh(world, default, justChanged));
    }
}
