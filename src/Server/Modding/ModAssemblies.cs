using System.Reflection;
using System.Runtime.Loader;
using Api;
using Shared.Blocks;
using Shared.Modding;

namespace Server.Modding;

/// <summary>
/// Загружает код модов. Каждая сборка мода — в своём <see cref="AssemblyLoadContext"/>: библиотеки разных модов
/// (даже разных версий) не мешают друг другу. Сборки API и Shared всегда берутся из игры — иначе тип <see cref="IMod"/>
/// из мода и из игры оказались бы разными типами, и мод нельзя было бы вызвать.
/// </summary>
/// <remarks>Код модов исполняется с полным доверием, как в tModLoader и Vintage Story. Выгрузки нет.</remarks>
internal static class ModAssemblies
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IMod).Assembly.GetName().Name!,
        typeof(BlockId).Assembly.GetName().Name!,
    };

    /// <summary>Экземпляр точки входа мода или null у мода без сборки (только данные).</summary>
    /// <exception cref="ModLoadException">Сборка не загрузилась, или в ней не ровно один класс <see cref="IMod"/>.</exception>
    public static IMod? CreateMod(ModInfo mod)
    {
        if (mod.AssemblyPath is not { } path)
            return null;

        Assembly assembly;
        try
        {
            assembly = new ModLoadContext(mod.Id, path).LoadFromAssemblyPath(path);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            throw new ModLoadException($"Мод '{mod.Id}': сборка '{mod.AssemblyFile}' не загрузилась: {exception.Message}", exception);
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var reason = exception.LoaderExceptions.FirstOrDefault(e => e != null)?.Message ?? exception.Message;
            throw new ModLoadException($"Мод '{mod.Id}': типы сборки не загрузились (собран для другой версии API?): {reason}", exception);
        }

        var entries = types.Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IMod).IsAssignableFrom(type)).ToArray();
        if (entries.Length != 1)
        {
            throw new ModLoadException(entries.Length == 0
                ? $"Мод '{mod.Id}': в сборке '{mod.AssemblyFile}' нет класса, реализующего IMod."
                : $"Мод '{mod.Id}': в сборке несколько классов IMod ({string.Join(", ", entries.Select(t => t.FullName))}), нужен один.");
        }

        try
        {
            return (IMod)Activator.CreateInstance(entries[0])!;
        }
        catch (Exception exception) when (exception is MissingMethodException or TargetInvocationException)
        {
            var reason = exception is TargetInvocationException { InnerException: { } inner } ? inner.Message : exception.Message;
            throw new ModLoadException($"Мод '{mod.Id}': не удалось создать {entries[0].FullName} (нужен открытый конструктор без параметров): {reason}", exception);
        }
    }

    private sealed class ModLoadContext(string modId, string assemblyPath) : AssemblyLoadContext($"mod:{modId}")
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // null — взять сборку из контекста игры (API, Shared, сама платформа .NET).
            if (SharedAssemblies.Contains(name.Name!))
                return null;
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
