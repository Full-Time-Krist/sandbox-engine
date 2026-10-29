using Shared.Content;
using Shared.Mobs;

namespace Client.Rendering;

/// <summary>
/// Как выглядит каждый вид моба: модель (<see cref="MobModel"/>) и слой текстуры в массиве текстур мобов. Слой 0 — «нет
/// текстуры», дальше — текстуры по именам (<see cref="TextureNames"/>), как у блоков. Без OpenGL — проверяется тестами.
/// </summary>
/// <remarks>
/// Модели без файла или с ошибкой игра не падает: в лог — строка с причиной, моб рисуется коробом в размер тела
/// шахматкой «нет текстуры» (<see cref="MobModel.Fallback"/>). Так же поступают текстуры блоков и картинки предметов.
/// </remarks>
internal sealed class MobVisuals
{
    /// <summary>Имя «нет текстуры» в <see cref="TextureNames"/> (слой 0).</summary>
    public const string MissingTexture = "";

    private readonly MobModel[] _models;
    private readonly int[] _layers;

    /// <param name="looks">Модель и текстура по имени моба (<see cref="ContentRegistries.MobLooks"/>).</param>
    /// <param name="log">Куда писать, что с моделью не так.</param>
    public MobVisuals(MobRegistry mobs, IReadOnlyDictionary<string, MobLook> looks, TextureFiles files, Action<string> log)
    {
        // Индекс — MobId.Value: у id 0 («не моб») модели нет, он не рисуется.
        _models = new MobModel[mobs.Count + 1];
        _layers = new int[mobs.Count + 1];
        var textures = new List<string> { MissingTexture };
        var layerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var parsed = new Dictionary<string, MobModel?>(StringComparer.Ordinal);

        foreach (var mob in mobs.All)
        {
            var look = looks.TryGetValue(mob.Name, out var known) ? known : new MobLook(mob.Name, mob.Name);
            if (!parsed.TryGetValue(look.Model, out var model))
                parsed[look.Model] = model = TryLoad(look.Model, files.ModelPathFor(look.Model), log);

            if (model is null)
            {
                _models[mob.Id.Value] = MobModel.Fallback(mob.Width, mob.Height);
                continue;
            }
            _models[mob.Id.Value] = model;
            if (!layerOf.TryGetValue(look.Texture, out var layer))
            {
                layerOf[look.Texture] = layer = textures.Count;
                textures.Add(look.Texture);
            }
            _layers[mob.Id.Value] = layer;
        }
        TextureNames = textures;
    }

    /// <summary>Текстуры мобов по слоям: <c>[0]</c> — <see cref="MissingTexture"/>, дальше — полные имена <c>пакет:имя</c>.</summary>
    public IReadOnlyList<string> TextureNames { get; }

    public MobModel Model(MobId mob) => _models[mob.Value];

    public int Layer(MobId mob) => _layers[mob.Value];

    private static MobModel? TryLoad(string name, string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            log($"[Client] Нет модели моба {name}: {path}");
            return null;
        }
        try
        {
            return MobModel.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            log($"[Client] Модель моба {name} с ошибкой ({path}): {e.Message}");
            return null;
        }
    }
}
