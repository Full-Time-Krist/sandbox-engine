namespace Shared.Entities;

/// <summary>
/// Номер сущности: индекс в массивах <see cref="EntityWorld"/> и поколение этого индекса.
/// </summary>
/// <remarks>
/// Индекс удалённой сущности достаётся следующей созданной, а поколение при этом растёт. Поэтому сохранённый
/// где-то старый номер (цель притяжения, владелец) не укажет на чужую новую сущность: <see cref="EntityWorld.IsAlive"/>
/// для него вернёт <c>false</c>. Поколение 0 не выдаётся — <c>default</c> означает «нет сущности».
/// </remarks>
public readonly record struct EntityId(int Index, int Generation)
{
    public static EntityId None => default;

    public bool IsNone => Generation == 0;

    public override string ToString() => IsNone ? "#none" : $"#{Index}.{Generation}";
}
