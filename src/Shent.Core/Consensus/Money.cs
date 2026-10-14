using System.Globalization;

namespace Shent.Core.Consensus;

/// <summary>
/// Importes en unidades mínimas enteras: 1 SHENT = 100.000.000 unidades (8 decimales).
/// Nunca se usan números de punto flotante para saldos.
/// </summary>
public static class Money
{
    public const int Decimals = 8;
    public const long Coin = 100_000_000L;
    public const long MaxSupplyCoins = 100_000_000L;
    public const long MaxSupply = MaxSupplyCoins * Coin;

    public static bool IsValidAmount(long units) => units >= 0 && units <= MaxSupply;

    public static string Format(long units, bool trimZeros = true)
    {
        var negative = units < 0;
        var abs = negative ? -(decimal)units : units;
        var whole = decimal.Truncate(abs / Coin);
        var frac = (long)(abs - whole * Coin);
        var fracText = frac.ToString("D8", CultureInfo.InvariantCulture);
        if (trimZeros)
        {
            fracText = fracText.TrimEnd('0');
        }
        var wholeText = whole.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');
        var text = fracText.Length > 0 ? $"{wholeText},{fracText}" : wholeText;
        return negative ? "-" + text : text;
    }

    /// <summary>Acepta "12", "12.5", "12,5" o "0,00000001". Rechaza más de 8 decimales y signos.</summary>
    public static bool TryParse(string? text, out long units)
    {
        units = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace(" ", string.Empty);

        var sepIndex = text.IndexOfAny(['.', ',']);
        var wholePart = sepIndex >= 0 ? text[..sepIndex] : text;
        var fracPart = sepIndex >= 0 ? text[(sepIndex + 1)..] : string.Empty;

        if (wholePart.Length == 0 && fracPart.Length == 0) return false;
        if (fracPart.Length > Decimals) return false;
        if (fracPart.IndexOfAny(['.', ',']) >= 0) return false;
        if (!wholePart.All(char.IsAsciiDigit) || !fracPart.All(char.IsAsciiDigit)) return false;
        if (wholePart.Length > 12) return false;

        try
        {
            var whole = wholePart.Length == 0 ? 0 : long.Parse(wholePart, CultureInfo.InvariantCulture);
            var frac = fracPart.Length == 0 ? 0 : long.Parse(fracPart.PadRight(Decimals, '0'), CultureInfo.InvariantCulture);
            units = checked(whole * Coin + frac);
        }
        catch (OverflowException)
        {
            return false;
        }
        return units <= MaxSupply;
    }
}
