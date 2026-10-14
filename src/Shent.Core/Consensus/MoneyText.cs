using System.Globalization;

namespace Shent.Core.Consensus;

/// <summary>
/// Montos para mostrar y escribir según el idioma de la interfaz ("1,000.5" en inglés, "1.000,5" en español).
/// Al leer se rechaza lo ambiguo en lugar de adivinar: nunca se envía 1 cuando la persona quiso escribir 1000.
/// </summary>
public static class MoneyText
{
    public static string Format(long units, NumberFormatInfo format)
    {
        var value = (decimal)units / Money.Coin;
        return value.ToString("#,0.########", format);
    }

    /// <summary>
    /// Acepta el separador decimal del idioma y separadores de miles bien agrupados (de a 3 cifras).
    /// Con un único separador que no es el decimal del idioma y exactamente 3 cifras detrás: son miles si el
    /// idioma usa ese signo para los miles y, si no, se rechaza por ambiguo. Con otra cantidad de cifras solo
    /// puede ser decimal.
    /// </summary>
    public static bool TryParse(string? text, NumberFormatInfo format, out long units, out string? error)
    {
        units = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Escribí un monto.";
            return false;
        }

        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '\u00A0' && c != '\u202F' && c != '\'').ToArray());
        if (clean.Length == 0 || clean.Any(c => !char.IsAsciiDigit(c) && c != '.' && c != ','))
        {
            error = "El monto solo puede tener números y un separador decimal.";
            return false;
        }

        var decimalSep = format.NumberDecimalSeparator is "," ? ',' : '.';
        var groupSep = format.NumberGroupSeparator switch
        {
            "," => ',',
            "." => '.',
            _ => (char?)null,
        };

        string whole;
        string frac;
        var lastDot = clean.LastIndexOf('.');
        var lastComma = clean.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            var sep = lastDot > lastComma ? '.' : ',';
            var other = sep == '.' ? ',' : '.';
            var at = clean.LastIndexOf(sep);
            whole = clean[..at];
            frac = clean[(at + 1)..];
            if (whole.Contains(sep) || !ValidGroups(whole, other))
            {
                error = "El monto tiene los separadores de miles mal ubicados.";
                return false;
            }
            whole = whole.Replace(other.ToString(), "");
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var count = clean.Count(c => c == sep);
            var at = clean.IndexOf(sep);
            if (count > 1)
            {
                if (!ValidGroups(clean, sep))
                {
                    error = "El monto tiene los separadores de miles mal ubicados.";
                    return false;
                }
                whole = clean.Replace(sep.ToString(), "");
                frac = "";
            }
            else if (sep != decimalSep && clean.Length - at - 1 == 3 && at > 0 && clean[..at].TrimStart('0').Length > 0)
            {
                if (sep != groupSep)
                {
                    error = "El monto es ambiguo: escribilo sin separador de miles o usá el separador decimal de tu idioma.";
                    return false;
                }
                whole = clean.Remove(at, 1);
                frac = "";
            }
            else
            {
                whole = clean[..at];
                frac = clean[(at + 1)..];
            }
        }
        else
        {
            whole = clean;
            frac = "";
        }

        if (frac.Length > Money.Decimals)
        {
            error = "SHENT admite como máximo 8 decimales.";
            return false;
        }
        if (whole.Length == 0 && frac.Length == 0)
        {
            error = "Escribí un monto.";
            return false;
        }
        if (!Money.TryParse(frac.Length > 0 ? $"{(whole.Length == 0 ? "0" : whole)}.{frac}" : whole, out units))
        {
            error = "El monto supera el máximo permitido.";
            return false;
        }
        return true;
    }

    private static bool ValidGroups(string whole, char groupSep)
    {
        var groups = whole.Split(groupSep);
        if (groups.Length == 1) return true;
        if (groups[0].Length is < 1 or > 3) return false;
        return groups.Skip(1).All(g => g.Length == 3);
    }
}
