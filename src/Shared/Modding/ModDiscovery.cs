using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shared.Modding;

/// <summary>
/// Находит моды в папке модов, читает их <c>mod.json</c> и выстраивает порядок загрузки по зависимостям.
/// Встроенный мод <see cref="BaseId"/> в папке не лежит, но от него можно зависеть.
/// </summary>
/// <remarks>
/// <c>mod.json</c>: <c>id</c> (a–z, 0–9, _; совпадает с именем папки), <c>name</c>, <c>version</c> (например 1.2.0),
/// необязательные <c>dependencies</c> (объект «id → наименьшая версия») и <c>assembly</c> (имя DLL в папке мода).
/// </remarks>
public static partial class ModDiscovery
{
    public const string ManifestFile = "mod.json";

    /// <summary>Встроенный ванильный мод: его контент — <c>Content/base</c>, код — в игре.</summary>
    public const string BaseId = "base";

    /// <summary>Версия встроенного мода <c>base</c> — для зависимостей вида <c>"base": "1.0"</c>.</summary>
    public static readonly Version BaseVersion = new(1, 0, 0);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();

    /// <summary>Моды из папки в порядке загрузки. Нет папки — модов нет.</summary>
    /// <exception cref="ModLoadException">Ошибка в манифесте или зависимостях.</exception>
    public static IReadOnlyList<ModInfo> Discover(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
            return [];

        var mods = Directory.GetDirectories(modsDirectory)
            .Order(StringComparer.Ordinal)
            .Select(ReadManifest);
        return Order(mods);
    }

    public static ModInfo ReadManifest(string modDirectory)
    {
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(modDirectory));
        var path = Path.Combine(modDirectory, ManifestFile);
        if (!File.Exists(path))
            throw new ModLoadException($"Мод в папке '{folder}': нет файла {ManifestFile}.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new ModLoadException($"Мод в папке '{folder}': некорректный {ManifestFile}: {exception.Message}", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Error(folder, "корень должен быть объектом.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Error(folder, $"поле '{property.Name}' указано несколько раз.");
                if (property.Name is not ("id" or "name" or "version" or "dependencies" or "assembly"))
                    throw Error(folder, $"неизвестное поле '{property.Name}'.");
            }

            var id = RequiredString(root, "id", folder);
            if (!IdPattern().IsMatch(id))
                throw Error(folder, $"id '{id}' должен состоять из a–z, 0–9 и _: это пространство имён мода.");
            if (id == BaseId)
                throw Error(folder, $"id '{BaseId}' занят встроенным модом игры.");
            if (id != folder)
                throw Error(folder, $"id '{id}' должен совпадать с именем папки мода.");

            var name = RequiredString(root, "name", folder);
            var version = ParseVersion(RequiredString(root, "version", folder), "version", folder);

            var dependencies = new Dictionary<string, Version>(StringComparer.Ordinal);
            if (root.TryGetProperty("dependencies", out var dependencyList))
            {
                if (dependencyList.ValueKind != JsonValueKind.Object)
                    throw Error(folder, "поле 'dependencies' должно быть объектом «id мода → наименьшая версия».");
                foreach (var dependency in dependencyList.EnumerateObject())
                {
                    if (dependency.Value.ValueKind != JsonValueKind.String)
                        throw Error(folder, $"версия зависимости '{dependency.Name}' должна быть строкой.");
                    if (dependency.Name == id)
                        throw Error(folder, "мод не может зависеть от самого себя.");
                    if (!dependencies.TryAdd(dependency.Name, ParseVersion(dependency.Value.GetString()!, $"dependencies.{dependency.Name}", folder)))
                        throw Error(folder, $"зависимость '{dependency.Name}' указана несколько раз.");
                }
            }

            string? assembly = null;
            if (root.TryGetProperty("assembly", out var assemblyValue))
            {
                assembly = assemblyValue.ValueKind == JsonValueKind.String ? assemblyValue.GetString() : null;
                // Только имя файла в папке мода: путь наружу из JSON указать нельзя.
                if (string.IsNullOrWhiteSpace(assembly) || assembly != Path.GetFileName(assembly)
                    || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw Error(folder, "поле 'assembly' должно быть именем DLL в папке мода, например \"MyMod.dll\".");
                if (!File.Exists(Path.Combine(modDirectory, assembly)))
                    throw Error(folder, $"сборка '{assembly}' не найдена в папке мода.");
            }

            return new ModInfo(id, name, version, dependencies, Path.GetFullPath(modDirectory), assembly);
        }
    }

    /// <summary>
    /// Порядок загрузки: каждый мод после своих зависимостей, при прочих равных — по id (ordinal).
    /// Проверяет, что зависимости есть, их версии подходят и циклов нет.
    /// </summary>
    public static IReadOnlyList<ModInfo> Order(IEnumerable<ModInfo> mods)
    {
        var byId = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            if (!byId.TryAdd(mod.Id, mod))
                throw new ModLoadException($"Мод '{mod.Id}' найден дважды.");
        }

        foreach (var mod in byId.Values)
        {
            foreach (var (dependency, minimum) in mod.Dependencies)
            {
                var installed = dependency == BaseId ? BaseVersion
                    : byId.TryGetValue(dependency, out var found) ? found.Version
                    : throw new ModLoadException($"Мод '{mod.Id}' требует мод '{dependency}', которого нет в папке модов.");
                if (installed < minimum)
                    throw new ModLoadException(
                        $"Мод '{mod.Id}' требует мод '{dependency}' версии не ниже {minimum}, установлена {installed}.");
            }
        }

        // Алгоритм Кана: берём готовый мод (все зависимости уже в списке) с наименьшим id.
        var ordered = new List<ModInfo>(byId.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal) { BaseId };
        var waiting = byId.Values.OrderBy(mod => mod.Id, StringComparer.Ordinal).ToList();
        while (waiting.Count > 0)
        {
            var ready = waiting.FirstOrDefault(mod => mod.Dependencies.Keys.All(placed.Contains))
                ?? throw new ModLoadException(
                    $"Циклическая зависимость между модами: {string.Join(", ", waiting.Select(mod => mod.Id))}.");
            ordered.Add(ready);
            placed.Add(ready.Id);
            waiting.Remove(ready);
        }
        return ordered;
    }

    private static string RequiredString(JsonElement root, string property, string folder)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw Error(folder, $"обязательное поле '{property}' должно быть непустой строкой.");
        return value.GetString()!;
    }

    private static Version ParseVersion(string text, string property, string folder) =>
        Version.TryParse(text, out var version) && version.Major >= 0
            ? version
            : throw Error(folder, $"поле '{property}': '{text}' — не версия (ожидается вида 1.0 или 1.2.3).");

    private static ModLoadException Error(string folder, string message) => new($"Мод '{folder}', {ManifestFile}: {message}");
}
