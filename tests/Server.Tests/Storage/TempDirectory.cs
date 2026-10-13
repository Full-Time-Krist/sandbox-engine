namespace Server.Tests.Storage;

/// <summary>Временная папка для файлов сохранения: удаляется вместе со всем содержимым.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sandbox-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    /// <summary>Путь к файлу базы мира внутри папки.</summary>
    public string SaveFile => System.IO.Path.Combine(Path, "world.db");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
