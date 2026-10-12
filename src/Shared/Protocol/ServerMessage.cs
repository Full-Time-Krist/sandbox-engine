using System.Collections.Immutable;
using System.Numerics;
using Shared.Blocks;
using Shared.Voxels;

namespace Shared.Protocol;

/// <summary>Сообщение от сервера клиенту.</summary>
public abstract record ServerMessage;

/// <param name="SpawnPosition">Где появляется игрок.</param>
/// <param name="BlockNames">
/// Имена блоков по порядку числовых id. Клиент сверяет их со своим реестром: при расхождении
/// одинаковые числа означали бы разные блоки. В мультиплеере клиент будет строить таблицу по этому списку.
/// </param>
public sealed record JoinAccepted(int PlayerId, Vector3 SpawnPosition, ImmutableArray<string> BlockNames) : ServerMessage;

/// <summary>Содержимое чанка.</summary>
/// <param name="Blocks">
/// Все блоки чанка в порядке <see cref="LocalPos.Index"/>, или пустой массив, если чанк целиком из воздуха.
/// ImmutableArray, а не массив: сообщения передаются по ссылке и должны быть неизменяемыми.
/// </param>
public sealed record ChunkData(ChunkPos Position, ImmutableArray<BlockId> Blocks) : ServerMessage
{
    public bool IsEmpty => Blocks.IsDefaultOrEmpty;

    public static ChunkData From(Chunk chunk) =>
        new(chunk.Position, chunk.IsEmpty ? [] : ImmutableArray.Create(chunk.Blocks));

    public Chunk ToChunk() => IsEmpty ? new Chunk(Position) : Chunk.FromBlocks(Position, Blocks.AsSpan());
}
