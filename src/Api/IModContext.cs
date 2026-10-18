using Shared.Blocks;
using Shared.Items;
using Shared.Quests;

namespace Api;

/// <summary>Что игра даёт моду при загрузке (<see cref="IMod.Load"/>).</summary>
public interface IModContext
{
    /// <summary>Id мода — оно же пространство имён его определений и поведений.</summary>
    string ModId { get; }

    /// <summary>Блоки всего контента (всех модов), уже загруженные и замороженные.</summary>
    BlockRegistry Blocks { get; }

    /// <summary>Предметы всего контента, уже загруженные и замороженные.</summary>
    ItemRegistry Items { get; }

    /// <summary>Рецепты всего контента (с заменами модов).</summary>
    RecipeRegistry Recipes { get; }

    /// <summary>Задания всего контента (с заменами модов), в порядке показа в журнале.</summary>
    QuestRegistry Quests { get; }

    /// <summary>
    /// Регистрирует поведение предмета под именем <paramref name="id"/> вида <c>&lt;id мода&gt;:имя</c>.
    /// Предметы ссылаются на него полем <c>behavior</c> в JSON.
    /// </summary>
    /// <exception cref="ArgumentException">Имя не вида <c>namespace:name</c> или не из пространства имён мода.</exception>
    /// <exception cref="InvalidOperationException">Поведение с таким именем уже есть или загрузка модов закончилась.</exception>
    void RegisterItemBehavior(string id, IItemBehavior behavior);

    /// <summary>Подписка на события мира.</summary>
    IModEvents Events { get; }

    /// <summary>Строка в лог игры с пометкой мода: <c>[Mod &lt;id&gt;] …</c>.</summary>
    void Log(string message);
}
