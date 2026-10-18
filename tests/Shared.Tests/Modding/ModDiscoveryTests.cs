using Shared.Modding;

namespace Shared.Tests.Modding;

public sealed class ModDiscoveryTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("mods-test-").FullName;

    public void Dispose() => Directory.Delete(_mods, recursive: true);

    private string Mod(string folder, string manifest)
    {
        var directory = Path.Combine(_mods, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ModDiscovery.ManifestFile), manifest);
        return directory;
    }

    private static string Manifest(string id, string version = "1.0.0", string dependencies = "{}") =>
        $$"""{"id":"{{id}}","name":"Мод {{id}}","version":"{{version}}","dependencies":{{dependencies}}}""";

    [Fact]
    public void ReadManifest_ParsesAllFields()
    {
        var directory = Mod("example_mod",
            """{"id":"example_mod","name":"Пример","version":"1.2.3","dependencies":{"base":"1.0"},"assembly":"Example.dll"}""");
        File.WriteAllBytes(Path.Combine(directory, "Example.dll"), []);

        var mod = ModDiscovery.ReadManifest(directory);

        Assert.Equal("example_mod", mod.Id);
        Assert.Equal("Пример", mod.Name);
        Assert.Equal(new Version(1, 2, 3), mod.Version);
        Assert.Equal(new Version(1, 0), mod.Dependencies["base"]);
        Assert.Equal(Path.Combine(directory, "Example.dll"), mod.AssemblyPath);
        Assert.Equal(Path.Combine(directory, "content"), mod.ContentDirectory);
    }

    [Theory]
    [InlineData("other", """{"id":"other_name","name":"x","version":"1.0"}""", "совпадать с именем папки")]
    [InlineData("base", """{"id":"base","name":"x","version":"1.0"}""", "занят встроенным")]
    [InlineData("Bad", """{"id":"Bad","name":"x","version":"1.0"}""", "a–z")]
    [InlineData("m", """{"id":"m","name":"x","version":"one"}""", "не версия")]
    [InlineData("m", """{"id":"m","name":"x","version":"1.0","author":"me"}""", "неизвестное поле 'author'")]
    [InlineData("m", """{"id":"m","version":"1.0"}""", "'name'")]
    [InlineData("m", """{"id":"m","name":"x","version":"1.0","assembly":"../evil.dll"}""", "именем DLL")]
    [InlineData("m", """{"id":"m","name":"x","version":"1.0","assembly":"Missing.dll"}""", "не найдена")]
    [InlineData("m", """{"id":"m","name":"x","version":"1.0","dependencies":{"m":"1.0"}}""", "самого себя")]
    [InlineData("m", """not json""", "некорректный")]
    public void ReadManifest_Invalid_NamesFolderAndProblem(string folder, string manifest, string expected)
    {
        var exception = Assert.Throws<ModLoadException>(() => ModDiscovery.ReadManifest(Mod(folder, manifest)));

        Assert.Contains($"'{folder}'", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Discover_NoFolder_NoMods_FolderWithoutManifest_IsError()
    {
        Assert.Empty(ModDiscovery.Discover(Path.Combine(_mods, "missing")));

        Directory.CreateDirectory(Path.Combine(_mods, "stray"));
        var exception = Assert.Throws<ModLoadException>(() => ModDiscovery.Discover(_mods));
        Assert.Contains("'stray'", exception.Message);
    }

    [Fact]
    public void Discover_OrdersByDependencies_ThenById()
    {
        // a зависит от z: несмотря на алфавит, z раньше a; b ни от кого не зависит и идёт по алфавиту.
        Mod("a", Manifest("a", dependencies: """{"z":"1.0","base":"1.0"}"""));
        Mod("b", Manifest("b"));
        Mod("z", Manifest("z"));

        var order = ModDiscovery.Discover(_mods).Select(mod => mod.Id);

        Assert.Equal(["b", "z", "a"], order);
    }

    [Theory]
    [InlineData("""{"missing_mod":"1.0"}""", "'missing_mod', которого нет")]
    [InlineData("""{"z":"2.0"}""", "не ниже 2.0, установлена 1.0.0")]
    [InlineData("""{"base":"9.0"}""", "'base' версии не ниже 9.0")]
    public void Discover_BadDependency_Throws(string dependencies, string expected)
    {
        Mod("a", Manifest("a", dependencies: dependencies));
        Mod("z", Manifest("z"));

        var exception = Assert.Throws<ModLoadException>(() => ModDiscovery.Discover(_mods));

        Assert.Contains("'a'", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Discover_Cycle_Throws_NamingMods()
    {
        Mod("a", Manifest("a", dependencies: """{"b":"1.0"}"""));
        Mod("b", Manifest("b", dependencies: """{"a":"1.0"}"""));
        Mod("c", Manifest("c"));

        var exception = Assert.Throws<ModLoadException>(() => ModDiscovery.Discover(_mods));

        Assert.Contains("Циклическая", exception.Message);
        Assert.Contains("a, b", exception.Message);
        Assert.DoesNotContain("c", exception.Message.Replace("Циклическая", ""));
    }
}
