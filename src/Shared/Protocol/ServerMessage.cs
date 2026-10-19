using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Shared.Blocks;
using Shared.Entities;
using Shared.Items;
using Shared.Voxels;

namespace Shared.Protocol;

/// <summary>Сообщение от сервера клиенту.</summary>
public abstract record ServerMessage;

/// <param name="StartPosition">Где игрок начинает: там, где он был при прошлом выходе, или в точке появления мира.</param>
/// <param name="StartLook">Куда игрок смотрел при прошлом выходе; новый игрок смотрит вдаль, вдоль −Z.</param>
/// <param name="Mode">Режим игрока: сохранённый или, у нового игрока, режим мира по умолчанию.</param>
/// <param name="BlockNames">
/// Имена блоков по порядку числовых id. Клиент сверяет их со своим реестром: при расхождении
/// одинаковые числа означали бы разные блоки. В мультиплеере клиент будет строить таблицу по этому списку.
/// </param>
/// <param name="ItemNames">Имена предметов по порядку числовых id: сверяются так же, как блоки.</param>
public sealed record JoinAccepted(int PlayerId, Vector3d StartPosition, LookAngles StartLook, GameMode Mode,
    ImmutableArray<string> BlockNames, ImmutableArray<string> ItemNames) : ServerMessage;

/// <summary>
/// Режим игрока сменился. При переходе в выживание следом приходит <see cref="PlayerTeleport"/> на текущее место:
/// перемещения, посчитанные клиентом ещё по-творчески (в полёте), сервер по новым пределам отклонил бы.
/// </summary>
public sealed record GameModeChanged(GameMode Mode) : ServerMessage;

/// <summary>Весь инвентарь игрока: приходит при входе сразу за <see cref="JoinAccepted"/>, дальше — только изменения.</summary>
/// <param name="Slots">Все слоты по номерам <see cref="Inventory"/>, последний — предмет на курсоре (<see cref="Inventory.SlotCount"/> штук).</param>
public sealed record InventoryContents(ImmutableArray<ItemStack> Slots, int SelectedSlot) : ServerMessage;

/// <summary>
/// Изменённые слоты инвентаря (добыча, установка блока, клики). Сервер собирает их за тик и шлёт одним сообщением в конце тика.
/// Выбор слота сюда не входит: его меняет сам клиент и сообщает серверу.
/// </summary>
public sealed record InventoryChanged(ImmutableArray<InventorySlot> Slots) : ServerMessage;

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

/// <summary>
/// Задания игрока целиком: приходит при входе сразу за <see cref="InventoryContents"/>, дальше — только <see cref="QuestChanged"/>.
/// Задания — по именам (<see cref="Quests.Quest.Name"/>): клиент грузит тот же контент и знает их определения.
/// </summary>
/// <param name="Completed">Выполненные задания, в том числе из модов, которых сейчас нет.</param>
/// <param name="Progress">Набранное у невыполненных считаемых заданий.</param>
public sealed record QuestBookContents(ImmutableArray<string> Completed, ImmutableArray<QuestCount> Progress) : ServerMessage;

/// <summary>Задание продвинулось или выполнено. Выполненное клиент показывает всплывающим сообщением.</summary>
/// <param name="Progress">Набранное у считаемого задания; у выполненного не важно.</param>
public sealed record QuestChanged(string Quest, int Progress, bool Completed) : ServerMessage;

/// <summary>Набранное у задания: для <see cref="QuestBookContents"/>.</summary>
public readonly record struct QuestCount(string Quest, int Count);

/// <summary>Вид сущности: что клиент рисует. Пока только предметы на земле.</summary>
public enum EntityKind : byte
{
    Item = 1,
}

/// <summary>
/// Сущность появилась у клиента: её создали в отправленном ему чанке, или клиент получил чанк, где она уже была,
/// или она пришла в такой чанк. Номер — тот же, что на сервере: по нему придут перемещения и удаление.
/// </summary>
/// <param name="Position">Точка тела: центр нижней грани короба.</param>
/// <param name="Item">Стопка у предмета на земле (<see cref="EntityKind.Item"/>).</param>
public sealed record EntitySpawned(EntityId Entity, EntityKind Kind, Vector3d Position, ItemStack Item) : ServerMessage;

/// <summary>Сущности, сдвинувшиеся за тик, — одним сообщением в конце тика; неподвижные не шлются.</summary>
public sealed record EntitiesMoved(ImmutableArray<EntityPosition> Entities) : ServerMessage;

/// <summary>Новое положение сущности для <see cref="EntitiesMoved"/>.</summary>
public readonly record struct EntityPosition(EntityId Entity, Vector3d Position);

/// <summary>Сущность больше не видна клиенту: удалена на сервере или ушла (вместе с чанком) за пределы отправленного.</summary>
public sealed record EntityRemoved(EntityId Entity) : ServerMessage;
