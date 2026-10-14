using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shent.Desktop.Localization;

/// <summary>
/// Separadores y formato de fecha definidos a mano: la app usa InvariantGlobalization, así que no depende
/// de los datos regionales instalados en cada equipo.
/// </summary>
public sealed record LanguageInfo(string Code, string NativeName, string DecimalSeparator, string GroupSeparator, string DatePattern)
{
    public override string ToString() => NativeName;

    public CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = DecimalSeparator;
        culture.NumberFormat.NumberGroupSeparator = GroupSeparator;
        culture.NumberFormat.PercentDecimalSeparator = DecimalSeparator;
        culture.NumberFormat.PercentGroupSeparator = GroupSeparator;
        culture.DateTimeFormat.ShortDatePattern = DatePattern;
        culture.DateTimeFormat.LongTimePattern = "HH:mm:ss";
        culture.DateTimeFormat.ShortTimePattern = "HH:mm";
        return culture;
    }
}

/// <summary>
/// Traducción de la interfaz. Los textos fuente están en español y son la clave: si falta una traducción,
/// o tiene marcadores {0} distintos de los del original, se muestra el español en lugar de fallar.
/// Solo cambia lo que se lee en pantalla; montos, direcciones y datos de la cadena no se traducen.
/// </summary>
public static partial class Loc
{
    public static readonly IReadOnlyList<LanguageInfo> Languages =
    [
        new("es", "Español", ",", ".", "dd/MM/yyyy"),
        new("en", "English", ".", ",", "MM/dd/yyyy"),
        new("pt", "Português", ",", ".", "dd/MM/yyyy"),
        new("fr", "Français", ",", "\u202F", "dd/MM/yyyy"),
        new("de", "Deutsch", ",", ".", "dd.MM.yyyy"),
        new("it", "Italiano", ",", ".", "dd/MM/yyyy"),
        new("ru", "Русский", ",", "\u00A0", "dd.MM.yyyy"),
        new("zh", "中文（简体）", ".", ",", "yyyy/MM/dd"),
        new("ja", "日本語", ".", ",", "yyyy/MM/dd"),
        new("tr", "Türkçe", ",", ".", "dd.MM.yyyy"),
    ];

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public static LanguageInfo Current { get; private set; } = Languages[0];
    public static CultureInfo Culture { get; private set; } = Languages[0].CreateCulture();

    public static LanguageInfo Find(string? code) =>
        Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

    /// <summary>Idioma del sistema si está disponible; si no, inglés.</summary>
    public static string SystemLanguage()
    {
        var code = OperatingSystem.IsWindows() ? WindowsUiLanguage() : PosixLanguage();
        return Languages.Any(l => l.Code == code) ? code! : "en";
    }

    private static string? WindowsUiLanguage()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) switch
            {
                0x0A => "es",
                0x09 => "en",
                0x16 => "pt",
                0x0C => "fr",
                0x07 => "de",
                0x10 => "it",
                0x19 => "ru",
                0x04 => "zh",
                0x11 => "ja",
                0x1F => "tr",
                _ => null,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? PosixLanguage()
    {
        var lang = Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all : Environment.GetEnvironmentVariable("LANG");
        return lang is { Length: >= 2 } ? lang[..2].ToLowerInvariant() : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    public static void SetLanguage(string? code)
    {
        var language = Find(code);
        _map = language.Code == "es" ? new(StringComparer.Ordinal) : Load(language.Code);
        Current = language;
        Culture = language.CreateCulture();
    }

    public static string T(string text) =>
        _map.TryGetValue(text, out var translated) ? translated : text;

    public static string T(string format, params object?[] args)
    {
        var translated = T(format);
        try
        {
            return string.Format(Culture, translated, args);
        }
        catch (FormatException)
        {
            return string.Format(Culture, format, args);
        }
    }

    /// <summary>
    /// Traduce un mensaje que llega armado desde el núcleo (errores de direcciones, billetera o mempool).
    /// Si no es una clave exacta, lo compara con las plantillas de core-messages.txt y traduce la plantilla
    /// conservando los valores. Si nada coincide, devuelve el mensaje original.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(message))]
    public static string? Message(string? message)
    {
        if (message is null || _map.Count == 0) return message;
        if (_map.ContainsKey(message)) return T(message);
        foreach (var (template, pattern) in CoreTemplates.Value)
        {
            var match = pattern.Match(message);
            if (!match.Success) continue;
            var args = new object?[match.Groups.Count - 1];
            for (var i = 0; i < args.Length; i++) args[i] = match.Groups[$"p{i}"].Value;
            return T(template, args);
        }
        return message;
    }

    private static readonly Lazy<IReadOnlyList<(string Template, Regex Pattern)>> CoreTemplates = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shent.Desktop.Localization.core-messages.txt");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        var templates = new List<(string, Regex)>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains('{')) continue;
            var pattern = TemplatePlaceholderRegex().Replace(Regex.Escape(line), m => $"(?<p{m.Groups[1].Value}>.*?)");
            templates.Add((line, new Regex($"^{pattern}$", RegexOptions.Singleline | RegexOptions.CultureInvariant)));
        }
        return templates;
    });

    [GeneratedRegex(@"\\\{(\d+)\}")]
    private static partial Regex TemplatePlaceholderRegex();

    internal static Dictionary<string, string> Load(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Shent.Desktop.Localization.{code}.json");
        if (stream is null) return map;
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            foreach (var (source, translated) in entries)
            {
                if (IsUsable(source, translated)) map[source] = translated;
            }
        }
        catch (JsonException)
        {
        }
        return map;
    }

    /// <summary>Una traducción solo se usa si conserva exactamente los mismos marcadores {n} que el original.</summary>
    internal static bool IsUsable(string source, string? translated) =>
        !string.IsNullOrWhiteSpace(translated) && Placeholders(source).SequenceEqual(Placeholders(translated));

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
