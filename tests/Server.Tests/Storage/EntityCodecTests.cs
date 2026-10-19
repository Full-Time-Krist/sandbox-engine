using System.Numerics;
using Server.Storage;
using Shared;
using Shared.Content;
using Shared.Items;

namespace Server.Tests.Storage;

public class EntityCodecTests
{
    private static readonly ItemRegistry Items = GameContent.Load().Items;
    private static readonly NameIdMap Ids = new("предметов", [.. Items.All.Select(i => i.Name)], [.. Items.All.Select(i => i.Name)]);
    private static readonly ItemId Dirt = Items.Get("base:dirt").Id;
    private static readonly ItemId Pickaxe = Items.Get("base:wooden_pickaxe").Id;

    private static readonly SavedItem Lying = new(new Vector3d(-1000000.25, 64, 3.875), Vector3.Zero, true, new ItemStack(Dirt, 37), 0, 5999);
    private static readonly SavedItem Flying = new(new Vector3d(10.5, 70.125, -2.5), new Vector3(1.5f, -3, 0.25f), false, new ItemStack(Pickaxe, 1), 17, 3);

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var data = EntityCodec.Encode([Lying, Flying], Ids);

        Assert.Equal([Lying, Flying], EntityCodec.Decode(data, Ids, Items));
    }

    [Fact]
    public void Empty_RoundTrips()
    {
        Assert.Empty(EntityCodec.Decode(EntityCodec.Encode([], Ids), Ids, Items));
    }

    /// <summary>Вид, которого игра не знает (мод удалён), пропускается целиком, соседние сущности читаются.</summary>
    [Fact]
    public void UnknownKind_IsSkipped_OthersKept()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(EntityCodec.Version);
            writer.Write7BitEncodedInt(3);
            WriteEntity(writer, Lying);
            writer.Write("my_mod:zombie");
            writer.Write7BitEncodedInt(5);
            writer.Write(new byte[] { 1, 2, 3, 4, 5 });
            WriteEntity(writer, Flying);
        }
        var skipped = new List<string>();

        var items = EntityCodec.Decode(stream.ToArray(), Ids, Items, skipped);

        Assert.Equal([Lying, Flying], items);
        Assert.Equal(["my_mod:zombie"], skipped);
    }

    /// <summary>Предмета больше нет (мод удалён) — пропадает; стопка больше предела — урезается.</summary>
    [Fact]
    public void MissingItem_Dropped_OversizedStack_Clamped()
    {
        var oldNames = Items.All.Select(i => i.Name).Append("gone_mod:gem").ToList();
        var saveIds = new NameIdMap("предметов", oldNames, [.. Items.All.Select(i => i.Name)]);
        var gem = new SavedItem(Vector3d.Zero, Vector3.Zero, true, new ItemStack(new ItemId((ushort)(oldNames.Count - 1)), 3), 0, 0);
        // Кодируем «старым» реестром, где есть самоцвет: номера сохранения совпадают с номерами в списке.
        var identity = new NameIdMap("предметов", oldNames, oldNames);
        var data = EntityCodec.Encode([gem, Lying with { Stack = new ItemStack(Pickaxe, 5) }], identity);

        var items = EntityCodec.Decode(data, saveIds, Items);

        Assert.Equal([Lying with { Stack = new ItemStack(Pickaxe, 1) }], items);
    }

    public static TheoryData<string, byte[]> Corrupt
    {
        get
        {
            var good = EntityCodec.Encode([Lying, Flying], Ids);
            var nan = EntityCodec.Encode([Lying with { Position = new Vector3d(double.NaN, 0, 0) }], Ids);
            var zero = EntityCodec.Encode([Lying with { Stack = new ItemStack(Dirt, 1), Age = 0 }], Ids);
            zero[^3] = 0; // число штук (каждое поле после номера предмета — один байт)
            return new()
            {
                { "нет данных", [] },
                { "другая версия", [(byte)(EntityCodec.Version + 1), 0] },
                { "обрыв", good[..^4] },
                { "лишнее в конце", [.. good, 0] },
                { "NaN в позиции", nan },
                { "ноль штук", zero },
                { "огромное число сущностей", [EntityCodec.Version, 0xFF, 0xFF, 0xFF, 0xFF, 0x07] },
            };
        }
    }

    [Theory]
    [MemberData(nameof(Corrupt))]
    public void CorruptData_Throws(string what, byte[] data)
    {
        Assert.False(string.IsNullOrEmpty(what));
        Assert.Throws<InvalidDataException>(() => EntityCodec.Decode(data, Ids, Items));
    }

    private static void WriteEntity(BinaryWriter writer, SavedItem item)
    {
        // Одна сущность в формате кодека — берём её из закодированного списка из одного элемента (после версии и числа).
        var single = EntityCodec.Encode([item], Ids);
        writer.Write(single, 2, single.Length - 2);
    }
}
