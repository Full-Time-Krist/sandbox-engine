using Shared;
using Shared.Items;

namespace Server.Storage;

/// <summary>Снимок предмета для сохранения; номера ECS после загрузки назначаются заново.</summary>
internal sealed record SavedItemEntity(Vector3d Position, Vector3d Velocity, ItemStack Stack, int AgeTicks, int PickupDelayTicks);

/// <summary>Версионированный блок сущностей чанка. Виды и предметы записываются по именам.</summary>
internal static class EntityCodec
{
    public const byte Version = 1;
    private const int MaxEntities = 1_000_000;

    public static byte[] Encode(IReadOnlyList<SavedItemEntity> entities, ItemRegistry items)
    {
        if (entities.Count > MaxEntities)
            throw new InvalidDataException("Слишком много сущностей в чанке.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write(entities.Count);
        foreach (var entity in entities)
        {
            Validate(entity);
            if (!items.Contains(entity.Stack.Item) || entity.Stack.IsEmpty)
                throw new InvalidDataException("Неизвестный или пустой предмет.");
            writer.Write("base:item");
            writer.Write(entity.Position.X); writer.Write(entity.Position.Y); writer.Write(entity.Position.Z);
            writer.Write(entity.Velocity.X); writer.Write(entity.Velocity.Y); writer.Write(entity.Velocity.Z);
            writer.Write(items.Get(entity.Stack.Item).Name);
            writer.Write(entity.Stack.Count);
            writer.Write(entity.AgeTicks);
            writer.Write(entity.PickupDelayTicks);
        }
        return stream.ToArray();
    }

    public static IReadOnlyList<SavedItemEntity> Decode(ReadOnlySpan<byte> data, ItemRegistry items)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Version)
                throw new InvalidDataException("Неизвестная версия формата сущностей.");
            var count = reader.ReadInt32();
            // Даже минимальная запись занимает больше 64 байт; не выделяем память по непроверенному числу.
            if (count < 0 || count > MaxEntities || count > (stream.Length - stream.Position) / 64)
                throw new InvalidDataException("Неверное число сущностей.");
            var result = new List<SavedItemEntity>(count);
            for (var i = 0; i < count; i++)
            {
                var kind = reader.ReadString();
                if (kind != "base:item")
                    throw new InvalidDataException($"Неизвестный вид сущности '{kind}'.");
                var position = new Vector3d(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                var velocity = new Vector3d(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                var name = reader.ReadString();
                var amount = reader.ReadInt32();
                var age = reader.ReadInt32();
                var delay = reader.ReadInt32();
                var entity = new SavedItemEntity(position, velocity, new ItemStack(new ItemId(1), amount), age, delay);
                Validate(entity);
                if (!items.TryGet(name, out var item) || item.Id.IsAir)
                {
                    Console.WriteLine($"[Server] Предмет сущности '{name}' неизвестен, сущность пропущена");
                    continue;
                }
                if (amount > item.MaxStackSize)
                {
                    Console.WriteLine($"[Server] Стопка сущности '{name}' больше предела {item.MaxStackSize}: урезана");
                    amount = item.MaxStackSize;
                }
                result.Add(entity with { Stack = new ItemStack(item.Id, amount) });
            }
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Лишние данные после сущностей.");
            return result;
        }
        catch (Exception error) when (error is EndOfStreamException or FormatException or ArgumentException or IOException && error is not InvalidDataException)
        {
            throw new InvalidDataException("Данные сущностей оборваны или испорчены.", error);
        }
    }

    private static void Validate(SavedItemEntity entity)
    {
        var p = entity.Position;
        if (!p.IsFinite || Math.Abs(p.X) >= 1_000_000_000 || Math.Abs(p.Y) >= 1_000_000_000 || Math.Abs(p.Z) >= 1_000_000_000
            || !entity.Velocity.IsFinite || entity.Velocity.LengthSquared() > 1_000_000
            || entity.Stack.Count <= 0 || entity.AgeTicks < 0 || entity.PickupDelayTicks < 0)
            throw new InvalidDataException("Неверные координаты, скорость, стопка или таймер сущности.");
    }
}
