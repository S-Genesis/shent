using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TextEncoding = System.Text.Encoding;

namespace Shent.Core.Tests;

/// <summary>
/// Revisa los archivos de traducción de la app de escritorio: JSON válido, mismos marcadores {n} que el
/// español y una traducción para cada texto que la interfaz pasa por Loc.T o {l:T}.
/// </summary>
public sealed class LocalizationFilesTests
{
    private static readonly string[] Languages = ["en", "pt", "fr", "de", "it", "ru", "zh", "ja", "tr"];
    private static readonly Regex Placeholder = new(@"\{\d+(?:[,:][^}]*)?\}");

    private static string DesktopDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Shent.Desktop");
    }

    private static Dictionary<string, string> LoadLanguage(string code)
    {
        var path = Path.Combine(DesktopDirectory(), "Localization", $"{code}.json");
        Assert.True(File.Exists(path), $"Falta {path}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, TextEncoding.UTF8))!;
    }

    private static string Placeholders(string text) =>
        string.Join("|", Placeholder.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal));

    private static string UnescapeCSharp(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    internal static SortedSet<string> InterfaceTexts()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var root = DesktopDirectory();
        bool Source(string path) => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

        var regular = new Regex(@"Loc\.T\(\s*""((?:[^""\\]|\\.)*)""");
        var verbatim = new Regex(@"Loc\.T\(\s*@""((?:[^""]|"""")*)""");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(Source))
        {
            var text = File.ReadAllText(file, TextEncoding.UTF8);
            foreach (Match m in regular.Matches(text)) keys.Add(UnescapeCSharp(m.Groups[1].Value));
            foreach (Match m in verbatim.Matches(text)) keys.Add(m.Groups[1].Value.Replace("\"\"", "\""));
        }

        var markup = new Regex(@"\{l:T '((?:[^']|\\')*)'\}");
        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories).Where(Source))
        {
            foreach (Match m in markup.Matches(File.ReadAllText(file, TextEncoding.UTF8)))
                keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value).Replace("\\'", "'"));
        }

        foreach (var line in File.ReadAllLines(Path.Combine(root, "Localization", "core-messages.txt"), TextEncoding.UTF8))
        {
            if (line.Trim().Length > 0) keys.Add(line);
        }
        keys.RemoveWhere(k => k.Trim().Length == 0);
        return keys;
    }

    [Fact]
    public void TranslationsKeepPlaceholdersAndSpacing()
    {
        foreach (var code in Languages)
        {
            foreach (var (source, translated) in LoadLanguage(code))
            {
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{code}: traducción vacía para «{source}»");
                Assert.True(Placeholders(source) == Placeholders(translated), $"{code}: marcadores distintos en «{source}» → «{translated}»");
                Assert.True(source.StartsWith(' ') == translated.StartsWith(' ') && source.EndsWith(' ') == translated.EndsWith(' '),
                    $"{code}: espacios de borde distintos en «{source}» → «{translated}»");
            }
        }
    }

    [Fact]
    public void EveryInterfaceTextHasATranslation()
    {
        var texts = InterfaceTexts();
        Assert.NotEmpty(texts);
        foreach (var code in Languages)
        {
            var map = LoadLanguage(code);
            var missing = texts.Where(t => !map.ContainsKey(t)).Take(10).ToList();
            Assert.True(missing.Count == 0, $"{code}: faltan traducciones para: {string.Join(" | ", missing)}");
        }
    }

    [Fact]
    public void AmountWatermarkUsesEachLanguageDecimalSeparator()
    {
        foreach (var code in Languages)
        {
            var expected = code is "en" or "zh" or "ja" ? "0.00" : "0,00";
            Assert.Equal(expected, LoadLanguage(code)["0,00"]);
        }
    }
}
