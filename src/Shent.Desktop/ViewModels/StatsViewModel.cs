using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Wallet;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed record MinerRow(string ShortAddress, string Pseudonym, string BlocksText, string ShareText, double Share, bool IsMine);

/// <summary>Estadísticas de la red calculadas con los bloques que validó este nodo. No hay precio: SHENT no cotiza.</summary>
public sealed partial class StatsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    private bool _busy;
    private long _lastHeight = -1;
    private long _lastUpdate;

    public override string Title => Loc.T("Estadísticas");
    public override string Subtitle => Loc.T("Monedas emitidas, mineros y actividad de la red");
    public override Geometry Icon => Icons.Chart;

    [ObservableProperty] private string _issued = "—";
    [ObservableProperty] private string _issuedCaption = "";
    [ObservableProperty] private double _issuedRatio;
    [ObservableProperty] private string _miners = "—";
    [ObservableProperty] private string _minersCaption = "";
    [ObservableProperty] private string _hashrate = "—";
    [ObservableProperty] private string _blocks24h = "—";
    [ObservableProperty] private string _blocksCaption = "";
    [ObservableProperty] private string _transactions24h = "—";
    [ObservableProperty] private string _subsidy = "—";
    [ObservableProperty] private string _halvingText = "";
    [ObservableProperty] private string _maxSupply = "";
    [ObservableProperty] private string? _concentrationWarning;
    [ObservableProperty] private bool _hasMiners;

    [ObservableProperty] private IReadOnlyList<double>? _supplySeries;
    [ObservableProperty] private IReadOnlyList<double>? _hashrateSeries;
    [ObservableProperty] private IReadOnlyList<double>? _blocksSeries;
    [ObservableProperty] private IReadOnlyList<double>? _minersSeries;
    [ObservableProperty] private string _seriesRange = "";
    [ObservableProperty] private string _supplyToday = "";
    [ObservableProperty] private string _hashrateToday = "";
    [ObservableProperty] private string _blocksToday = "";
    [ObservableProperty] private string _minersToday = "";

    public ObservableCollection<MinerRow> TopMiners { get; } = [];

    public override void Refresh()
    {
        var node = Shell.Host.Node;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_busy || (node.Chain.Height == _lastHeight && now - _lastUpdate < 30)) return;
        _busy = true;
        _lastHeight = node.Chain.Height;
        _lastUpdate = now;
        var stats = Shell.Host.Stats;
        Task.Run(() => stats.Snapshot(now)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            if (t.IsCompletedSuccessfully) Apply(t.Result);
        }));
    }

    private void Apply(ChainStats s)
    {
        var network = Shell.Host.Node.Network;
        IssuedRatio = s.MaxSupply > 0 ? (double)s.Issued / s.MaxSupply : 0;
        Issued = Format.Shent(s.Issued);
        IssuedCaption = Loc.T("{0} del máximo de {1} SHENT", Format.Percent(IssuedRatio, 4), Format.Shent(s.MaxSupply));
        MaxSupply = Loc.T("{0} SHENT como máximo, nunca más", Format.Shent(s.MaxSupply));

        Miners = Format.Integer(s.Miners24h);
        MinersCaption = Loc.T("{0} en 7 días · {1} desde el inicio", Format.Integer(s.Miners7d), Format.Integer(s.MinersTotal));
        Hashrate = Format.Hashrate(s.Hashrate24h);
        Blocks24h = Format.Integer(s.Blocks24h);
        BlocksCaption = s.Blocks24h >= 2
            ? Loc.T("Uno cada {0} en promedio (objetivo: {1})", Format.Duration(s.AverageBlockSeconds24h), Format.Duration(network.TargetSpacingSeconds))
            : Loc.T("Objetivo: un bloque cada {0}", Format.Duration(network.TargetSpacingSeconds));
        Transactions24h = Format.Integer(s.Transactions24h);

        Subsidy = Loc.T("{0} SHENT por bloque", Format.Shent(s.Subsidy));
        var remaining = s.NextHalvingHeight - (s.Height + 1);
        HalvingText = Loc.T("Se reduce a la mitad en el bloque {0}: faltan {1} bloques (≈ {2}).",
            Format.Integer(s.NextHalvingHeight), Format.Integer(remaining), Format.Duration(remaining * (double)network.TargetSpacingSeconds));

        var days = s.Days;
        SupplySeries = days.Select(d => (double)d.IssuedAtEnd / Money.Coin).ToList();
        HashrateSeries = days.Select(d => d.Hashrate).ToList();
        BlocksSeries = days.Select(d => (double)d.Blocks).ToList();
        MinersSeries = days.Select(d => (double)d.Miners).ToList();
        SeriesRange = days.Count > 0
            ? Loc.T("{0} – {1} · por día (UTC)", Format.LocalDate(days[0].Day), Format.LocalDate(days[^1].Day))
            : "";
        if (days.Count > 0)
        {
            var today = days[^1];
            SupplyToday = Loc.T("{0} SHENT", Format.Shent(today.IssuedAtEnd));
            HashrateToday = Format.Hashrate(today.Hashrate);
            BlocksToday = Loc.T("Hoy: {0}", Format.Integer(today.Blocks));
            MinersToday = Loc.T("Hoy: {0}", Format.Integer(today.Miners));
        }

        var mine = Shell.Host.Wallet;
        TopMiners.Clear();
        foreach (var m in s.TopMiners7d)
        {
            var encoded = m.Miner.Encode(network);
            TopMiners.Add(new MinerRow(
                Address.Shorten(encoded),
                Pseudonym.FromAddress(m.Miner),
                m.Blocks == 1 ? Loc.T("1 bloque") : Loc.T("{0} bloques", Format.Integer(m.Blocks)),
                Format.Percent(m.Share, 1),
                m.Share,
                mine?.IsMine(m.Miner) == true));
        }
        HasMiners = TopMiners.Count > 0;
        ConcentrationWarning = s.TopMiners7d.FirstOrDefault() is { Share: > 0.5 } top && s.Miners7d > 0
            ? Loc.T("Una sola dirección minó el {0} de los bloques de la semana. Con más de la mitad de la potencia se pueden reorganizar bloques recientes (no robar monedas). Cuantos más mineros haya, más segura es la red.", Format.Percent(top.Share, 0))
            : null;
    }
}
