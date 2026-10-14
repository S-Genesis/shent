using System.Globalization;
using Shent.Core.Consensus;

namespace Shent.Core.Tests;

public sealed class MoneyTextTests
{
    // Separadores de cada idioma de la app (definidos a mano: el proyecto usa InvariantGlobalization).
    private static NumberFormatInfo F(string culture)
    {
        var (dec, group) = culture switch
        {
            "en-US" or "zh-CN" or "ja-JP" => (".", ","),
            "fr-FR" => (",", "\u202F"),
            "ru-RU" => (",", "\u00A0"),
            _ => (",", "."),
        };
        return new NumberFormatInfo { NumberDecimalSeparator = dec, NumberGroupSeparator = group, NumberGroupSizes = [3] };
    }

    private static long Parse(string text, string culture)
    {
        Assert.True(MoneyText.TryParse(text, F(culture), out var units, out var error), $"{text} ({culture}): {error}");
        return units;
    }

    private static void Rejected(string text, string culture) =>
        Assert.False(MoneyText.TryParse(text, F(culture), out _, out _), $"{text} ({culture}) debería rechazarse");

    [Theory]
    [InlineData("es-AR")]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("it-IT")]
    [InlineData("ru-RU")]
    [InlineData("zh-CN")]
    [InlineData("ja-JP")]
    [InlineData("tr-TR")]
    public void WhatIsShownCanBeTypedBackExactly(string culture)
    {
        foreach (var units in new[] { 0L, 1L, 12_345_678L, Money.Coin, 1000 * Money.Coin, 1_234_567 * Money.Coin + 89_000_000, Money.MaxSupply })
        {
            var text = MoneyText.Format(units, F(culture));
            Assert.Equal(units, Parse(text, culture));
        }
    }

    [Fact]
    public void ThousandsAreNeverReadAsDecimals()
    {
        Assert.Equal(1000 * Money.Coin, Parse("1.000", "es-AR"));
        Assert.Equal(1000 * Money.Coin, Parse("1.000", "de-DE"));
        Assert.Equal(1000 * Money.Coin, Parse("1,000", "en-US"));
        Assert.Equal(1_000_000 * Money.Coin, Parse("1.000.000", "es-AR"));
        Assert.Equal(1_000_000 * Money.Coin, Parse("1,000,000", "es-AR"));
        Assert.Equal(1000 * Money.Coin + Money.Coin / 2, Parse("1.000,5", "es-AR"));
        Assert.Equal(1000 * Money.Coin + Money.Coin / 2, Parse("1,000.5", "es-AR"));
        Assert.Equal(1000 * Money.Coin, Parse("1 000", "fr-FR"));
        Assert.Equal(1000 * Money.Coin, Parse("1\u202F000", "fr-FR"));

        // En inglés el punto es decimal: 1.000 es uno.
        Assert.Equal(Money.Coin, Parse("1.000", "en-US"));
        // Donde los miles van con espacio, "1.000" puede ser cualquiera de las dos cosas.
        Rejected("1.000", "fr-FR");
        Rejected("1.000", "ru-RU");
        Assert.Equal(Money.Coin, Parse("1,000", "ru-RU"));
    }

    [Fact]
    public void UnambiguousDecimalsAreAcceptedWithEitherSign()
    {
        Assert.Equal(Money.Coin + Money.Coin / 2, Parse("1.5", "es-AR"));
        Assert.Equal(Money.Coin + Money.Coin / 2, Parse("1,5", "en-US"));
        Assert.Equal(12_500_000, Parse("0.125", "es-AR"));
        Assert.Equal(12_500_000, Parse("0,125", "en-US"));
        Assert.Equal(1, Parse("0,00000001", "es-AR"));
        Assert.Equal(Money.Coin / 2, Parse(",5", "es-AR"));
    }

    [Fact]
    public void MalformedAmountsAreRejected()
    {
        foreach (var text in new[] { "", " ", "abc", "1e5", "-1", "+1", "1.2.3,4", "1,00,000", "12.34.567", "1.0000,5", "0,000000001", "1,5.3" })
        {
            Rejected(text, "es-AR");
        }
        Rejected("100000001", "en-US");
    }
}
