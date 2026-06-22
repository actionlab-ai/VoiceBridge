using System.Text;
using System.Text.Json;

namespace VoiceBridge.Desktop;

internal static class AsrTextCleaner
{
    public static string CleanEnvelope(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        const string asrTag = "<asr_text>";
        var tagIndex = text.IndexOf(asrTag, StringComparison.OrdinalIgnoreCase);
        if (tagIndex >= 0 && tagIndex <= 64)
        {
            text = text[(tagIndex + asrTag.Length)..].TrimStart();
        }
        else
        {
            const string languageChinesePrefix = "language Chinese";
            if (text.StartsWith(languageChinesePrefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[languageChinesePrefix.Length..].TrimStart();
            }
        }

        const string closeTag = "</asr_text>";
        var closeIndex = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
        if (closeIndex >= 0)
        {
            text = text[..closeIndex].TrimEnd();
        }

        return text.Trim();
    }
}

// Keep ASR envelope cleanup in C# runtime code, not in build-time source patch scripts.
// This wrapper intentionally shadows System.Text.Json.JsonDocument inside the
// VoiceBridge.Desktop namespace so the existing AsrClient.ExtractText path returns
// normalized ASR text without changing build scripts or generated source.
internal sealed class JsonDocument : IDisposable
{
    private readonly System.Text.Json.JsonDocument _inner;

    private JsonDocument(System.Text.Json.JsonDocument inner)
    {
        _inner = inner;
    }

    public AsrJsonElement RootElement => new(_inner.RootElement);

    public static JsonDocument Parse(string json) => new(System.Text.Json.JsonDocument.Parse(json));

    public void Dispose() => _inner.Dispose();
}

internal readonly struct AsrJsonElement
{
    private readonly JsonElement _inner;

    public AsrJsonElement(JsonElement inner)
    {
        _inner = inner;
    }

    public JsonValueKind ValueKind => _inner.ValueKind;

    public AsrJsonElement this[int index] => new(_inner[index]);

    public AsrJsonElement GetProperty(string propertyName) => new(_inner.GetProperty(propertyName));

    public bool TryGetProperty(string propertyName, out AsrJsonElement value)
    {
        if (_inner.TryGetProperty(propertyName, out var innerValue))
        {
            value = new AsrJsonElement(innerValue);
            return true;
        }

        value = default;
        return false;
    }

    public IEnumerable<AsrJsonElement> EnumerateArray()
    {
        foreach (var item in _inner.EnumerateArray())
        {
            yield return new AsrJsonElement(item);
        }
    }

    public string? GetString()
    {
        var value = _inner.GetString();
        return value == null ? null : AsrTextCleaner.CleanEnvelope(value);
    }

    public override string ToString() => AsrTextCleaner.CleanEnvelope(_inner.ToString());
}
