using System.Globalization;
using Shent.Core.Consensus;
using Shent.Desktop.Localization;

namespace Shent.Desktop.Services;

/// <summary>Formato de cifras, montos y fechas según el idioma elegido en la interfaz.</summary>
public static class Format
{
    private static CultureInfo C => Loc.Culture;

    public static string Hashrate(double hps) => hps switch
    {
        >= 1e9 => $"{(hps / 1e9).ToString("N2", C)} GH/s",
        >= 1e6 => $"{(hps / 1e6).ToString("N2", C)} MH/s",
        >= 1e3 => $"{(hps / 1e3).ToString("N2", C)} kH/s",
        _ => $"{hps.ToString("N0", C)} H/s",
    };

    public static string Number(double value) => value switch
    {
        >= 1e12 => $"{(value / 1e12).ToString("N2", C)} T",
        >= 1e9 => $"{(value / 1e9).ToString("N2", C)} G",
        >= 1e6 => $"{(value / 1e6).ToString("N2", C)} M",
        >= 1e3 => $"{(value / 1e3).ToString("N1", C)} k",
        _ => value.ToString("N0", C),
    };

    public static string Integer(long value) => value.ToString("N0", C);

    public static string Decimal(double value, int decimals) => value.ToString("N" + decimals, C);

    public static string Percent(double ratio, int decimals = 2) => $"{(ratio * 100).ToString("N" + decimals, C)} %";

    public static string Shent(long units) => MoneyText.Format(units, C.NumberFormat);

    /// <summary>Lee un monto escrito con el formato del idioma activo; el error ya viene traducido.</summary>
    public static bool TryParseAmount(string? text, out long units, out string? error)
    {
        var ok = MoneyText.TryParse(text, C.NumberFormat, out units, out var raw);
        error = Loc.Message(raw);
        return ok;
    }

    public static string TimeAgo(long unixSeconds)
    {
        var delta = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds;
        if (delta < 5) return Loc.T("ahora");
        return delta switch
        {
            < 60 => Loc.T("hace {0} s", delta),
            < 3600 => Loc.T("hace {0} min", delta / 60),
            < 86400 => Loc.T("hace {0} h", delta / 3600),
            _ => Loc.T("hace {0} d", delta / 86400),
        };
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "—";
        return seconds switch
        {
            < 60 => Loc.T("{0} s", Decimal(seconds, 0)),
            < 3600 => Loc.T("{0} min", Decimal(seconds / 60, 0)),
            < 86400 => Loc.T("{0} h", Decimal(seconds / 3600, 1)),
            < 86400 * 365 => Loc.T("{0} días", Decimal(seconds / 86400, 1)),
            _ => Loc.T("más de un año"),
        };
    }

    public static string LocalTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString(C.DateTimeFormat.ShortDatePattern + " HH:mm:ss", C);

    public static string LocalDate(DateOnly day) => day.ToString(C.DateTimeFormat.ShortDatePattern, C);

    public static string ShortAddress(string encoded) => Address.Shorten(encoded);
}
