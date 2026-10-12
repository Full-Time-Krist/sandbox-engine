using Shared.Voxels;

namespace Server.Tests;

public class PlayerChunkViewTests
{
    private static readonly ChunkPos Origin = new(0, 0, 0);

    private static PlayerChunkView View(ChunkPos center = default) => new(viewDistance: 6, verticalDistance: 4, center);

    private static void SendAll(PlayerChunkView view)
    {
        foreach (var position in view.Missing().ToList())
            view.MarkSent(position);
    }

    [Fact]
    public void LoadArea_IsOneRingWiderThanViewDistance_AndKeepAreaWiderStill()
    {
        var view = View();

        Assert.Equal(new ChunkArea(7, 5), view.LoadArea);
        Assert.Equal(new ChunkArea(7 + PlayerChunkView.UnloadMargin, 5 + PlayerChunkView.UnloadMargin), view.KeepArea);
    }

    [Fact]
    public void Missing_CoversWholeArea_NearestFirst()
    {
        var view = View(new ChunkPos(10, -3, 7));
        var missing = view.Missing().ToList();

        // Перебором: все чанки цилиндра, каждый ровно один раз.
        var expected = 0;
        for (var x = -7; x <= 7; x++)
        for (var y = -5; y <= 5; y++)
        for (var z = -7; z <= 7; z++)
        {
            if (x * x + z * z <= 49)
                expected++;
        }
        Assert.Equal(expected, missing.Count);
        Assert.Equal(expected, missing.Distinct().Count());
        Assert.Equal(view.AreaSize, missing.Count);

        // Первым идёт чанк игрока, дальше расстояние не убывает.
        Assert.Equal(view.Center, missing[0]);
        var distances = missing.Select(p => DistanceSquared(p, view.Center)).ToList();
        Assert.Equal(distances.Order(), distances);
    }

    private static int DistanceSquared(ChunkPos a, ChunkPos b)
    {
        int dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    [Fact]
    public void AfterEverythingSent_IsComplete_AndNothingMissing()
    {
        var view = View();

        SendAll(view);

        Assert.True(view.IsComplete);
        Assert.Empty(view.Missing());
        Assert.Equal(view.AreaSize, view.SentCount);
    }

    [Fact]
    public void MovingOneChunk_UnloadsNothing_ButNeedsNewEdge()
    {
        var view = View();
        SendAll(view);

        var unload = view.MoveTo(new ChunkPos(1, 0, 0));

        Assert.Empty(unload);
        Assert.NotEmpty(view.Missing());
        Assert.All(view.Missing(), p => Assert.True(p.X > 0, $"Недостающий чанк {p} не на новом краю"));
    }

    [Fact]
    public void MovingBackAndForth_NeverUnloads()
    {
        var view = View();
        SendAll(view);

        for (var i = 0; i < 10; i++)
        {
            Assert.Empty(view.MoveTo(new ChunkPos(i % 2, 0, 0)));
            SendAll(view);
        }
    }

    [Fact]
    public void MovingFar_UnloadsEverythingOutsideKeepArea()
    {
        var view = View();
        SendAll(view);
        var sentBefore = view.SentCount;

        var center = new ChunkPos(12, 0, 0);
        var unload = view.MoveTo(center);

        Assert.NotEmpty(unload);
        Assert.All(unload, p => Assert.False(view.KeepArea.Contains(center, p), $"{p} выгружен, хотя ещё нужен"));
        Assert.All(unload, p => Assert.False(view.WasSent(p)));
        Assert.Equal(sentBefore - unload.Count, view.SentCount);

        // Всё, что осталось отправленным из старой области, — внутри новой области сохранения.
        var oldArea = View(Origin);
        var stillSent = oldArea.Missing().Where(view.WasSent).ToList();
        Assert.NotEmpty(stillSent);
        Assert.All(stillSent, p => Assert.True(view.Keeps(p), $"{p} должен был выгрузиться"));
    }

    [Fact]
    public void Keeps_MatchesKeepArea()
    {
        var view = View(Origin);

        Assert.True(view.Keeps(new ChunkPos(9, 0, 0)));
        Assert.False(view.Keeps(new ChunkPos(10, 0, 0)));
        Assert.True(view.Keeps(new ChunkPos(0, 7, 0)));
        Assert.False(view.Keeps(new ChunkPos(0, -8, 0)));
        Assert.False(view.Keeps(new ChunkPos(7, 0, 7)));   // угол квадрата — вне круга
    }
}
