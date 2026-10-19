using System.Numerics;
using Server.Entities;
using Shared;
using Shared.Content;
using Shared.Entities;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;
using static Server.Tests.TestServers;

namespace Server.Tests.Entities;

/// <summary>Предметы на земле: добыча падает, притягивается и подбирается; слияние, исчезновение, курсор, творческий.</summary>
public sealed class ItemPickupTests : IDisposable
{
    private static readonly ContentRegistries Content = GameContent.Load();
    private static readonly ItemId Dirt = Content.Items.Get("base:dirt").Id;
    private static readonly ItemId Stone = Content.Items.Get("base:stone").Id;
    private static readonly ItemId Planks = Content.Items.Get("base:planks").Id;
    private static readonly Vector3 Down = new(0, -1, 0);

    private readonly GameServer _server = Create();
    private readonly TestClient _client;
    private readonly Inventory _inventory;
    private readonly Vector3d _feet;

    public ItemPickupTests()
    {
        _client = JoinAndLoad(_server);
        _inventory = _server.PlayerInventory(_client.PlayerId);
        // Игрок стоит на верхнем блоке столбца (0, 0).
        var top = TopBlock(_server.World, 0, 0);
        _feet = new Vector3d(0.5, top.Y + 1, 0.5);
        Teleport(_server, _client, _feet);
    }

    public void Dispose() => _server.Dispose();

    private int InInventory(ItemId item) => _inventory.Count(item);

    private int Total(ItemId item) => InInventory(item) + OnGround(_server, item);

    private List<EntityId> Dropped()
    {
        var result = new List<EntityId>();
        foreach (var entity in _server.Entities.Query<DroppedItem>())
            result.Add(entity);
        return result;
    }

    private void FillInventory()
    {
        for (var slot = 0; slot < Inventory.Size; slot++)
            _inventory.Set(slot, new ItemStack(Planks, 64));
    }

    /// <summary>Добыча — на земле в центре блока, полсекунды её не подобрать, потом она в инвентаре стоящего рядом.</summary>
    [Fact]
    public void BreakDrop_FallsAsItem_PickedUpAfterHalfSecond()
    {
        var below = BlockPos.Containing(_feet) - new BlockPos(0, 1, 0);
        var drop = Content.Items.ForBlock(_server.World.Chunks.GetBlock(below));
        _client.Send(new UseItem(ItemAction.Primary, _feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), Down));
        TickUntil(_server, () => Dropped().Count == 1, "блок сломается и уронит добычу");

        var entity = Assert.Single(Dropped());
        var item = _server.Entities.Get<DroppedItem>(entity);
        Assert.Equal(new ItemStack(drop, 1), item.Stack);
        // Добыча появляется при ломании, до старения в том же тике: один тик задержки уже прошёл.
        Assert.Equal(ItemEntities.DropPickupDelay - 1, item.PickupDelay);
        Assert.Equal(0, InInventory(drop));

        Tick(_server, ItemEntities.DropPickupDelay - 2);
        Assert.Equal(0, InInventory(drop));
        TickUntilPickedUp(_server);
        Assert.Equal(1, InInventory(drop));
    }

    /// <summary>Полный инвентарь: блок всё равно ломается, добыча лежит; освободилось место — подбирается.</summary>
    [Fact]
    public void FullInventory_DropWaits_ThenPickedUpWhenRoomAppears()
    {
        FillInventory();
        ItemEntities.Spawn(_server.Entities, _feet, Vector3.Zero, new ItemStack(Dirt, 2));

        Tick(_server, 40);
        Assert.Equal(2, OnGround(_server, Dirt));

        _inventory.Set(7, ItemStack.Empty);
        TickUntilPickedUp(_server);
        Assert.Equal(new ItemStack(Dirt, 2), _inventory[7]);
    }

    /// <summary>Влезает не всё: подбирается сколько есть места (доливая стопку), остаток лежит. Ничего не теряется.</summary>
    [Fact]
    public void PartialPickup_RemainderStaysOnGround()
    {
        FillInventory();
        _inventory.Set(3, new ItemStack(Dirt, 61));
        ItemEntities.Spawn(_server.Entities, _feet, Vector3.Zero, new ItemStack(Dirt, 10));

        Tick(_server, 20);

        Assert.Equal(new ItemStack(Dirt, 64), _inventory[3]);
        Assert.Equal(7, OnGround(_server, Dirt));
        Assert.Equal(71, Total(Dirt));
    }

    /// <summary>Выброшенный два секунды не возвращается к бросившему, даже лежа у его ног; потом подбирается.</summary>
    [Fact]
    public void Thrown_NotPickedUpForTwoSeconds()
    {
        _inventory.Set(0, new ItemStack(Stone, 5));
        // Взгляд под ноги: предмет падает прямо к игроку.
        _client.Send(new PlayerMove(_feet, new LookAngles(0, -MathF.PI / 2), TeleportIdOf(_client)));
        _client.Send(new DropItem(WholeStack: false));

        Tick(_server, ItemEntities.ThrowPickupDelay - 1);
        Assert.Equal(4, InInventory(Stone));
        Assert.Equal(1, OnGround(_server, Stone));

        TickUntilPickedUp(_server);
        Assert.Equal(5, InInventory(Stone));
    }

    /// <summary>Предмет в 2,5 блока летит к игроку и подбирается; в 6 блоках — лежит на месте.</summary>
    [Fact]
    public void Attraction_WithinRadiusOnly()
    {
        var near = FreeSpotAt(2.5);
        var far = FreeSpotAt(6);
        var nearItem = ItemEntities.Spawn(_server.Entities, near, Vector3.Zero, new ItemStack(Dirt, 1));
        var farItem = ItemEntities.Spawn(_server.Entities, far, Vector3.Zero, new ItemStack(Stone, 1));

        TickUntil(_server, () => !_server.Entities.IsAlive(nearItem), "ближний предмет подберётся");
        Tick(_server, 40);

        Assert.Equal(1, InInventory(Dirt));
        Assert.True(_server.Entities.IsAlive(farItem));
        Assert.Equal(0, InInventory(Stone));
        var farAt = _server.Entities.Get<Position>(farItem).Value;
        Assert.Equal(far.X, farAt.X, 1e-9);
        Assert.Equal(far.Z, farAt.Z, 1e-9);
    }

    /// <summary>В творческом добыча не падает, и лежащее не подбирается: предметы там бесконечные.</summary>
    [Fact]
    public void Creative_NoDrop_NoPickup()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        ItemEntities.Spawn(_server.Entities, _feet, Vector3.Zero, new ItemStack(Dirt, 1));
        _client.Send(new UseItem(ItemAction.Primary, _feet + new Vector3d(0, PlayerPhysics.EyeHeight, 0), Down));

        Tick(_server, 40);

        Assert.True(_server.World.Chunks.GetBlock(BlockPos.Containing(_feet) - new BlockPos(0, 1, 0)).IsAir);
        Assert.Equal([new ItemStack(Dirt, 1)], ItemsOnGround(_server));
        Assert.Equal(0, InInventory(Dirt));
    }

    /// <summary>
    /// Рядом лежащие одинаковые стопки сливаются до предела стопки, остаток — отдельно; другой предмет и дальний не
    /// сливаются. Число штук не меняется.
    /// </summary>
    [Fact]
    public void Merge_SameItemsNearby_UpToStackLimit()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative); // чтобы не подобрал
        var spot = FreeSpotAt(-8);
        ItemEntities.Spawn(_server.Entities, spot, Vector3.Zero, new ItemStack(Dirt, 30));
        ItemEntities.Spawn(_server.Entities, spot + new Vector3d(0.3, 0, 0), Vector3.Zero, new ItemStack(Dirt, 40));
        ItemEntities.Spawn(_server.Entities, spot + new Vector3d(0, 0, 0.2), Vector3.Zero, new ItemStack(Stone, 5));
        ItemEntities.Spawn(_server.Entities, spot + new Vector3d(3, 0, 0), Vector3.Zero, new ItemStack(Dirt, 1));
        // Тот же X, но в 3 блоках по Z: пары ищутся по полосе X, расстояние должно проверяться целиком.
        ItemEntities.Spawn(_server.Entities, spot + new Vector3d(0.1, 0, 3), Vector3.Zero, new ItemStack(Dirt, 2));

        Tick(_server, 10);

        var stacks = ItemsOnGround(_server);
        Assert.Equal(5, stacks.Count);
        Assert.Contains(new ItemStack(Dirt, 64), stacks);
        Assert.Contains(new ItemStack(Dirt, 6), stacks);
        Assert.Contains(new ItemStack(Stone, 5), stacks);
        Assert.Contains(new ItemStack(Dirt, 1), stacks);
        Assert.Contains(new ItemStack(Dirt, 2), stacks);
        Assert.Equal(73, OnGround(_server, Dirt));
    }

    [Fact]
    public void Merge_ManySmallStacks_IntoOne()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        var spot = FreeSpotAt(-8);
        for (var i = 0; i < 10; i++)
            ItemEntities.Spawn(_server.Entities, spot + new Vector3d(i * 0.04, 0, 0), Vector3.Zero, new ItemStack(Dirt, 3));

        Tick(_server, 5);

        Assert.Equal([new ItemStack(Dirt, 30)], ItemsOnGround(_server));
    }

    /// <summary>Лежащий предмет исчезает ровно через 5 минут.</summary>
    [Fact]
    public void Despawns_AfterLifetime()
    {
        _server.SetGameMode(_client.PlayerId, GameMode.Creative);
        var item = ItemEntities.Spawn(_server.Entities, FreeSpotAt(-8), Vector3.Zero, new ItemStack(Dirt, 1));
        _server.Entities.Get<DroppedItem>(item).Age = ItemEntities.LifetimeTicks - 2;

        _server.Tick();
        Assert.True(_server.Entities.IsAlive(item));
        _server.Tick();
        Assert.False(_server.Entities.IsAlive(item));
    }

    /// <summary>Закрыли экран инвентаря с предметом на курсоре: он возвращается в слоты, а не влезший — выбрасывается.</summary>
    [Fact]
    public void CursorOnClose_ReturnsToSlots_RestIsThrown()
    {
        FillInventory();
        _inventory.Set(5, new ItemStack(Dirt, 60));
        _inventory.Set(Inventory.CursorSlot, new ItemStack(Dirt, 10));

        _client.Send(new ReturnInventoryCursor());
        _server.Tick();

        Assert.True(_inventory.Cursor.IsEmpty);
        Assert.Equal(new ItemStack(Dirt, 64), _inventory[5]);
        var thrown = Assert.Single(Dropped());
        Assert.Equal(new ItemStack(Dirt, 6), _server.Entities.Get<DroppedItem>(thrown).Stack);
        Assert.Equal(ItemEntities.ThrowPickupDelay - 1, _server.Entities.Get<DroppedItem>(thrown).PickupDelay);
    }

    [Fact]
    public void CursorOnClose_AllFits_NothingThrown()
    {
        _inventory.Set(Inventory.CursorSlot, new ItemStack(Dirt, 10));

        _client.Send(new ReturnInventoryCursor());
        _server.Tick();

        Assert.True(_inventory.Cursor.IsEmpty);
        Assert.Equal(10, InInventory(Dirt));
        Assert.Empty(Dropped());
    }

    /// <summary>Добыча, бросок, слияние и подбор вперемешку: штук в мире (инвентарь + земля) столько же, сколько было.</summary>
    [Fact]
    public void Conservation_DropMergePickUp_NothingLostOrDuplicated()
    {
        _inventory.Set(0, new ItemStack(Dirt, 64));
        _inventory.Set(1, new ItemStack(Dirt, 20));
        for (var i = 0; i < 8; i++)
            ItemEntities.Spawn(_server.Entities, _feet + new Vector3d(0.1 * i - 0.4, 1, 0), Vector3.Zero, new ItemStack(Dirt, 7));
        var total = Total(Dirt);
        Assert.Equal(64 + 20 + 56, total);

        for (var i = 0; i < 6; i++)
        {
            _client.Send(new DropItem(WholeStack: i % 2 == 0));
            Tick(_server, 7);
            Assert.Equal(total, Total(Dirt));
        }
        Tick(_server, 100);

        Assert.Equal(total, Total(Dirt));
        Assert.Equal(0, OnGround(_server, Dirt));
    }

    /// <summary>
    /// Точка на высоте поверхности в <paramref name="dx"/> блоках по X от игрока: над твёрдым блоком и в свободном
    /// месте (рельеф неровный, поэтому верх столбца ищется).
    /// </summary>
    private Vector3d FreeSpotAt(double dx)
    {
        var x = (int)Math.Floor(_feet.X + dx);
        var top = TopBlock(_server.World, x, 0);
        return new Vector3d(_feet.X + dx, top.Y + 1, 0.5);
    }

    private static int TeleportIdOf(TestClient client)
    {
        var id = 0;
        while (client.TryReceive(out var message))
        {
            if (message is PlayerTeleport teleport)
                id = teleport.TeleportId;
        }
        return id;
    }
}
