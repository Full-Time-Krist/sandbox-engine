using Api;
using Server.Storage;
using Shared;
using Shared.Items;
using Shared.Physics;
using Shared.Protocol;
using Shared.Voxels;

namespace Server;

public sealed partial class GameServer
{
    private void InitializePlayerHealth(ClientSession session, SavedPlayer? saved)
    {
        session.Health = saved?.IsDead == true ? 0 : Math.Clamp(saved?.Health ?? 20, 1, 20);
        session.DamageHandler = (amount, reason) => DamagePlayer(session, amount, reason);
        ResetPlayerFall(session);
        SendHealth(session);
    }

    private static void ResetPlayerFall(ClientSession session) => session.FallPeak = session.PlayerPosition.Y;

    private void ObservePlayerMove(ClientSession session, Vector3d previous)
    {
        if (session.Mode == GameMode.Creative || session.IsDead)
        {
            ResetPlayerFall(session);
            return;
        }
        session.FallPeak = Math.Max(session.FallPeak, Math.Max(previous.Y, session.PlayerPosition.Y));
        // Сервер сам проверяет опору; клиент не присылает доверенный флаг OnGround.
        var probe = new Vector3d(0, -0.01, 0);
        if (session.PlayerPosition.Y <= previous.Y &&
            PlayerPhysics.Sweep(session.PlayerPosition, probe, World.Chunks, _registry) is { } moved &&
            moved.Y > probe.Y + Aabb.Epsilon)
        {
            int damage = (int)Math.Max(0, Math.Floor(session.FallPeak - session.PlayerPosition.Y - 3 + Aabb.Epsilon));
            ResetPlayerFall(session);
            if (damage > 0) DamagePlayer(session, damage, "fall");
        }
    }

    private void DamagePlayer(ClientSession session, int amount, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentNullException.ThrowIfNull(reason);
        if (session.Mode == GameMode.Creative || session.IsDead) return;
        int applied = Math.Min(amount, session.Health);
        session.Health -= applied;
        session.RegenerationTicks = 0;
        session.Breaking = null;
        _events.Raise(new PlayerDamaged(_editor, session, applied, reason));
        if (session.IsDead)
        {
            DropDeathInventory(session);
            _events.Raise(new PlayerDied(_editor, session, reason));
        }
        SendHealth(session, reason);
    }

    private void TickPlayerHealth()
    {
        foreach (var session in _sessions)
        {
            if (!session.IsJoined) continue;
            if (session.IsDead)
            {
                continue;
            }
            if (session.Health >= 20)
            {
                session.RegenerationTicks = 0;
                continue;
            }
            if (++session.RegenerationTicks >= 4 * GameTime.TicksPerSecond)
            {
                session.RegenerationTicks = 0;
                session.Health++;
                SendHealth(session);
            }
        }
    }

    private void DropDeathInventory(ClientSession session)
    {
        var position = session.PlayerPosition;
        // Смерть из события входа возможна раньше фоновой генерации: сначала обеспечиваем чанк,
        // чтобы выпадение и последующий выход не теряли ни одного предмета.
        if (session.Inventory.CopySlots().All(stack => stack.IsEmpty)) return;
        World.EnsureChunk(BlockPos.Containing(position).Chunk);
        for (int slot = 0; slot < Inventory.SlotCount; slot++)
        {
            var stack = session.Inventory[slot];
            if (stack.IsEmpty) continue;
            double angle = slot * 2.399963229728653;
            var velocity = new Vector3d(Math.Cos(angle) * 1.5, 3, Math.Sin(angle) * 1.5);
            SpawnItem(position, stack, velocity, 2 * GameTime.TicksPerSecond);
            session.Inventory.Set(slot, ItemStack.Empty);
        }
    }

    private void RespawnPlayer(ClientSession session)
    {
        if (!session.IsDead) return;
        DropDeathInventory(session);
        session.PlayerPosition = World.SpawnPosition;
        session.Health = 20;
        session.RegenerationTicks = 0;
        ResetPlayerFall(session);
        SendHealth(session);
        SendTeleport(session);
    }

    private static void SendHealth(ClientSession session, string reason = "") =>
        session.Connection.Send(new HealthChanged(session.Health, IsDead: session.IsDead, Reason: reason));
}
