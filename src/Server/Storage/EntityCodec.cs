using System.Numerics;
using Shared;
using Shared.Items;

namespace Server.Storage;

/// <summary>Предмет на земле в сохранении: всё, что нужно, чтобы он продолжил лежать (и исчезнуть) как прежде.</summary>
/// <param name="Stack">Стопка в id реестра этого запуска.</param>
internal sealed record SavedItem(Vector3d Position, Vector3 Velocity, bool OnGround, ItemStack Stack, int PickupDelay, int Age);

/// <summary>
/// Сущности одного чанка в сохранении — двоичный блок: байт версии формата, число сущностей и для каждой — имя вида,
/// длина её данных в байтах и сами данные. Длина позволяет пропустить вид, которого игра не знает (сущность мода,
/// которого больше нет), не испортив остальные. Вид <c>item</c>: позиция (3 × double), скорость (3 × float), опора,
/// номер предмета в сохранении (<see cref="NameIdMap"/>), число штук, задержка подбора и возраст в тиках.
/// </summary>
/// <remarks>Данные с диска не считаются надёжными: <see cref="Decode"/> проверяет всё и бросает <see cref="InvalidDataException"/>.</remarks>
internal static class EntityCodec
{
    public const byte Version = 1;
    public const string ItemKind = "item";

    public static byte[] Encode(IReadOnlyCollection<SavedItem> items, NameIdMap itemIds)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Version);
            writer.Write7BitEncodedInt(items.Count);
            using var entity = new MemoryStream();
            foreach (var item in items)
            {
                entity.SetLength(0);
                using (var data = new BinaryWriter(entity, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    data.Write(item.Position.X);
                    data.Write(item.Position.Y);
                    data.Write(item.Position.Z);
                    data.Write(item.Velocity.X);
                    data.Write(item.Velocity.Y);
                    data.Write(item.Velocity.Z);
                    data.Write(item.OnGround);
                    data.Write7BitEncodedInt(itemIds.ToSave(item.Stack.Item.Value));
                    data.Write7BitEncodedInt(item.Stack.Count);
                    data.Write7BitEncodedInt(item.PickupDelay);
                    data.Write7BitEncodedInt(item.Age);
                }
                writer.Write(ItemKind);
                writer.Write7BitEncodedInt((int)entity.Length);
                writer.Write(entity.GetBuffer(), 0, (int)entity.Length);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Читает сущности чанка. Неизвестный вид пропускается (<paramref name="skippedKinds"/>), предмет, которого больше нет
    /// в реестре, пропадает; стопка больше предела урезается до него.
    /// </summary>
    /// <exception cref="InvalidDataException">Данные испорчены или от неизвестной версии формата.</exception>
    public static List<SavedItem> Decode(byte[] data, NameIdMap itemIds, ItemRegistry registry, List<string>? skippedKinds = null)
    {
        if (data.Length == 0 || data[0] != Version)
            throw new InvalidDataException($"Неизвестная версия формата сущностей: {(data.Length == 0 ? "нет данных" : data[0])}.");

        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1));
            var count = reader.Read7BitEncodedInt();
            if (count < 0 || count > data.Length)
                throw new InvalidDataException($"Неверное число сущностей: {count}.");

            var items = new List<SavedItem>(count);
            for (var i = 0; i < count; i++)
            {
                var kind = reader.ReadString();
                var length = reader.Read7BitEncodedInt();
                if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException($"Сущность «{kind}»: неверная длина данных {length}.");
                var body = reader.ReadBytes(length);
                if (kind != ItemKind)
                {
                    skippedKinds?.Add(kind);
                    continue;
                }
                if (ReadItem(body, itemIds, registry) is { } item)
                    items.Add(item);
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Лишние данные после сущностей.");
            return items;
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Данные сущностей обрываются.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Неверное число в данных сущностей.", e);
        }
    }

    private static SavedItem? ReadItem(byte[] body, NameIdMap itemIds, ItemRegistry registry)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        var position = new Vector3d(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        var velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var onGround = reader.ReadBoolean();
        var saveId = reader.Read7BitEncodedInt();
        var count = reader.Read7BitEncodedInt();
        var pickupDelay = reader.Read7BitEncodedInt();
        var age = reader.Read7BitEncodedInt();
        if (reader.BaseStream.Position != body.Length)
            throw new InvalidDataException("Лишние данные в предмете на земле.");

        if (!position.IsFinite || !float.IsFinite(velocity.LengthSquared()))
            throw new InvalidDataException($"Неверное положение или скорость предмета на земле: {position}.");
        if (count <= 0 || pickupDelay < 0 || age < 0)
            throw new InvalidDataException($"Неверный предмет на земле: {count} шт., задержка {pickupDelay}, возраст {age}.");

        var item = new ItemId(itemIds.ToRuntime(saveId));
        if (item.IsAir)
            return null;
        var max = registry.Get(item).MaxStackSize;
        if (count > max)
        {
            Console.WriteLine($"[Server] На земле {count} × {registry.Get(item).Name}, больше предела {max}: лишнее пропадёт");
            count = max;
        }
        return new SavedItem(position, velocity, onGround, new ItemStack(item, count), pickupDelay, age);
    }
}
