using System.Numerics;
using Api;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Networking;
using Shared.Protocol;
using Shared.Voxels;

namespace Server;

/// <summary>Подключённый клиент с точки зрения сервера.</summary>
internal sealed class ClientSession(IConnection<ServerMessage, ClientMessage> connection) : IPlayer
{
    string IPlayer.Name => PlayerName;
    Vector3d IPlayer.Position => PlayerPosition;

    public IConnection<ServerMessage, ClientMessage> Connection { get; } = connection;

    public bool IsJoined { get; private set; }
    public int PlayerId { get; private set; }
    public string PlayerName { get; private set; } = "";
    public Vector3d PlayerPosition { get; set; }

    /// <summary>Направление взгляда из последнего <see cref="PlayerMove"/>. Нужно только для сохранения.</summary>
    public LookAngles Look { get; set; }

    /// <summary>Режим игрока. Меняется только через <see cref="SetMode"/>: вместе с ним меняются пределы движения.</summary>
    public GameMode Mode { get; private set; }

    /// <summary>Инвентарь игрока (с хотбаром и выбранным слотом). Появляется при подключении (<see cref="Join"/>).</summary>
    public Inventory Inventory { get; private set; } = null!;

    /// <summary>Проверка движения и номер телепорта. Появляется при подключении.</summary>
    public MovementCheck Movement { get; private set; } = null!;

    /// <summary>Какие чанки нужны игроку и какие уже у его клиента. Появляется при подключении.</summary>
    public PlayerChunkView View { get; private set; } = null!;

    public void Join(int playerId, string playerName, GameMode mode, Inventory inventory, PlayerChunkView view)
    {
        View = view;
        Mode = mode;
        Movement = new MovementCheck(mode);
        IsJoined = true;
        PlayerId = playerId;
        PlayerName = playerName;
        Inventory = inventory;
    }

    /// <summary>Блок, который игрок сейчас ломает удержанием ЛКМ, или null (<see cref="GameServer"/> ведёт и завершает).</summary>
    public BlockBreaking? Breaking { get; set; }

    public void SetMode(GameMode mode)
    {
        Mode = mode;
        Movement.SetMode(mode);
    }
}

/// <summary>Незаконченное ломание: что ломается, с какого тика, сколько тиков нужно и с каким предметом в руке.</summary>
/// <param name="Slot">Выбранный слот при начале: сменил слот или предмет в нём — ломание отменяется.</param>
internal readonly record struct BlockBreaking(BlockPos Position, BlockId Block, long StartTick, int Ticks, int Slot, ItemId Held);
