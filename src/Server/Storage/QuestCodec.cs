using System.Text.Json;
using Shared;
using Shared.Quests;

namespace Server.Storage;

/// <summary>
/// Задания игрока в сохранении — JSON-текст: <c>{"version": 1, "completed": [имена], "progress": {имя: число}}</c>.
/// Заданий немного, а имена в тексте видны прямо в базе. Хранятся имена, а не номера: выполненное задание мода,
/// которого сейчас нет, остаётся в сохранении.
/// </summary>
/// <remarks>
/// Данные с диска не считаются надёжными: <see cref="Decode"/> проверяет версию, имена и числа и при ошибке бросает
/// <see cref="InvalidDataException"/>.
/// </remarks>
internal static class QuestCodec
{
    public const int Version = 1;

    public static string Encode(QuestBook book)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("completed");
            // По порядку имён: одинаковая книга — одинаковый текст.
            foreach (var name in book.Completed.Order(StringComparer.Ordinal))
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteStartObject("progress");
            foreach (var (name, count) in book.Progress.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                writer.WriteNumber(name, count);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <exception cref="InvalidDataException">Текст испорчен: не JSON, другая версия, неверное имя или число.</exception>
    public static QuestBook Decode(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != Version)
                throw new InvalidDataException($"неизвестная версия заданий (поддерживается {Version})");

            var book = new QuestBook();
            if (!root.TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("нет списка выполненных заданий");
            foreach (var element in completed.EnumerateArray())
                book.Complete(Name(element.ValueKind == JsonValueKind.String ? element.GetString() : null));

            if (!root.TryGetProperty("progress", out var progress) || progress.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("нет счётчиков заданий");
            foreach (var entry in progress.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var count) || count < 0)
                    throw new InvalidDataException($"у задания '{entry.Name}' неверный счётчик");
                // Счётчик выполненного не нужен (Complete его убирает): испорченная запись не воскресит задание.
                if (!book.IsCompleted(Name(entry.Name)))
                    book.SetProgress(entry.Name, count);
            }
            return book;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"задания не читаются: {e.Message}", e);
        }
    }

    private static string Name(string? name) =>
        name is not null && ContentNames.IsValid(name) ? name : throw new InvalidDataException($"неверное имя задания '{name}'");
}
