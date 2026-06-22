param(
  [Parameter(Mandatory = $true)]
  [string]$ProjectDir
)

$programPath = Join-Path $ProjectDir "Program.cs"
if (-not (Test-Path $programPath)) {
  throw "Program.cs not found: $programPath"
}

$content = Get-Content -Raw -Encoding UTF8 $programPath
if ($content.Contains("private static string CleanAsrEnvelope")) {
  Write-Host "ASR output cleanup patch already applied."
  exit 0
}

$needle = @'
    public static string Process(string text, bool enabled)
    {
        text = text.Trim();
        if (!enabled) return text;
'@

$replacement = @'
    private static string CleanAsrEnvelope(string text)
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
            const string languagePrefix = "language Chinese";
            if (text.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[languagePrefix.Length..].TrimStart();
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

    public static string Process(string text, bool enabled)
    {
        text = CleanAsrEnvelope(text);
        if (!enabled) return text;
'@

if (-not $content.Contains($needle)) {
  throw "Expected TextPostProcessor.Process block not found in Program.cs."
}

$content = $content.Replace($needle, $replacement)

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($programPath, $content, $utf8NoBom)
Write-Host "ASR output cleanup patch applied."
