using System.Collections.Immutable;
using System.Numerics;
using Shared.Items;

namespace Shared.Protocol;

/// <summary>Сообщение от клиента серверу.</summary>
public abstract record ClientMessage;

/// <summary>Первое сообщение после подключения.</summary>
public sealed record JoinRequest(string PlayerName) : ClientMessage;

/// <summary>
/// Позиция игрока (точка ног) и направление взгляда после тика клиента. Движение считает клиент, а сервер
/// проверяет его тем же кодом физики; неправдоподобное отклоняет и возвращает игрока назад (<see cref="PlayerTeleport"/>).
/// Взгляд сервер не проверяет: он нужен только для сохранения.
/// </summary>
/// <param name="TeleportId">
/// Номер последнего телепорта, который клиент применил. Сообщения со старым номером отправлены до телепорта
/// и посчитаны от старой позиции: сервер их игнорирует.
/// </param>
public sealed record PlayerMove(Vector3d Position, LookAngles Look, int TeleportId) : ClientMessage;

/// <summary>
/// Игрок просит сменить режим (клавиша G). Ответ — <see cref="GameModeChanged"/>. Пока команд и прав доступа нет,
/// сервер разрешает смену всем: игра только одиночная.
/// </summary>
public sealed record SetGameMode(GameMode Mode) : ClientMessage;

/// <summary>Игрок выбрал слот хотбара (клавиши 1–9 или колесо мыши).</summary>
public sealed record SelectHotbarSlot(int Slot) : ClientMessage;

/// <summary>
/// Клик по слоту на экране инвентаря. Клиент шлёт только намерение: сервер применяет его к своей копии тем же кодом
/// (<see cref="Inventory.Click"/>) и присылает изменённые слоты (<see cref="InventoryChanged"/>).
/// </summary>
public sealed record InventoryClick(int Slot, InventoryClickKind Kind) : ClientMessage;

/// <summary>
/// Результат перетаскивания стопки по нескольким слотам. Сервер проверяет все номера и сам применяет
/// распределение к авторитетному инвентарю.
/// </summary>
public sealed record InventoryDragDistribute(ImmutableArray<int> TargetSlots, bool OnePerSlot) : ClientMessage;

/// <summary>Закрытие экрана: сервер возвращает предмет с курсора в инвентарь.</summary>
public sealed record ReturnInventoryCursor : ClientMessage;

/// <summary>Запрос предмета из палитры творческого режима. Сервер проверяет режим игрока.</summary>
public sealed record CreativeGiveItem(ItemId Item) : ClientMessage;

/// <summary>
/// Игрок применил предмет в руке. Клиент не сообщает, какой блок менять: сервер сам повторяет луч
/// от <paramref name="EyePosition"/> по <paramref name="LookDirection"/> и решает, что делает предмет.
/// Так новые предметы (инструменты, еда, оружие) не потребуют новых сообщений.
/// </summary>
public sealed record UseItem(ItemAction Action, Vector3d EyePosition, Vector3 LookDirection) : ClientMessage;

/// <summary>
/// Игрок отпустил ЛКМ или отвёл взгляд от блока: незаконченное ломание отменяется. Начинает ломание сам <see cref="UseItem"/>
/// с основным действием (предмет в руке на сервере решает, ломать ли).
/// </summary>
public sealed record StopBreaking : ClientMessage;
