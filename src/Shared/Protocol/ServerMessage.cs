using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Protocol;

/// <summary>Сообщение от сервера клиенту.</summary>
public abstract record ServerMessage;

/// <param name="StartPosition">Где игрок начинает: там, где он был при прошлом выходе, или в точке появления мира.</param>
/// <param name="BlockNames">
/// Имена блоков по порядку числовых id. Клиент сверяет их со своим реестром: при расхождении
/// одинаковые числа означали бы разные блоки. В мультиплеере клиент будет строить таблицу по этому списку.
/// </param>
public sealed record JoinAccepted(int PlayerId, Vector3d StartPosition, ImmutableArray<string> BlockNames) : ServerMessage;

/// <summary>Содержимое чанка, сжатое <see cref="ChunkCodec"/>.</summary>
/// <param name="Data">
/// Блоки чанка в формате <see cref="ChunkCodec"/>: обычно 1–3 КБ вместо 64 КБ, пустой чанк — 8 байт.
/// ImmutableArray, а не массив: сообщения передаются по ссылке и должны быть неизменяемыми.
/// </param>
public sealed record ChunkData(ChunkPos Position, ImmutableArray<byte> Data) : ServerMessage
{
    public static ChunkData From(Chunk chunk) =>
        new(chunk.Position, ImmutableCollectionsMarshal.AsImmutableArray(ChunkCodec.Encode(chunk)));

    /// <exception cref="InvalidDataException">Данные повреждены.</exception>
    public Chunk ToChunk() => ChunkCodec.Decode(Position, Data.AsMemory());
}

/// <summary>
/// Чанк ушёл далеко от игрока: клиент удаляет его и его меш. Сервер шлёт это с запасом
/// (дальше радиуса загрузки), чтобы чанки на границе не загружались и не выгружались туда-сюда.
/// </summary>
public sealed record ChunkUnload(ChunkPos Position) : ServerMessage;

/// <summary>
/// Сервер переносит игрока: возвращает назад после отклонённого движения или телепортирует (команды, тесты).
/// Клиент ставит игрока в эту точку со сбросом скорости и дальше шлёт <see cref="PlayerMove"/> с этим номером.
/// </summary>
/// <param name="Position">Точка ног.</param>
/// <param name="TeleportId">Номер телепорта; растёт с каждым телепортом этого игрока.</param>
public sealed record PlayerTeleport(Vector3d Position, int TeleportId) : ServerMessage;

/// <summary>Блок в мире изменился. Клиент меняет свою копию и перестраивает меши.</summary>
public sealed record BlockChanged(BlockPos Position, BlockId Block) : ServerMessage;
