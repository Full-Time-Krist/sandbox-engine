using System.Numerics;
using Shared.Items;

namespace Shared.Protocol;

/// <summary>Сообщение от клиента серверу.</summary>
public abstract record ClientMessage;

/// <summary>Первое сообщение после подключения.</summary>
public sealed record JoinRequest(string PlayerName) : ClientMessage;

/// <summary>
/// Позиция игрока после тика клиента. Пока движение считает клиент (как в Minecraft),
/// позже сервер начнёт её проверять.
/// </summary>
public sealed record PlayerMove(Vector3d Position) : ClientMessage;

/// <summary>Игрок выбрал слот хотбара (клавиши 1–6).</summary>
public sealed record SelectHotbarSlot(int Slot) : ClientMessage;

/// <summary>
/// Игрок применил предмет в руке. Клиент не сообщает, какой блок менять: сервер сам повторяет луч
/// от <paramref name="EyePosition"/> по <paramref name="LookDirection"/> и решает, что делает предмет.
/// Так новые предметы (инструменты, еда, оружие) не потребуют новых сообщений.
/// </summary>
public sealed record UseItem(ItemAction Action, Vector3d EyePosition, Vector3 LookDirection) : ClientMessage;
