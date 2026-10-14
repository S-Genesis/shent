using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed partial class DashboardViewModel(MainViewModel shell) : PageViewModel(shell)
{
    private long _lastHeight = -1;
    private Shent.Core.Node.LogEntry? _lastLog;

    public override string Title => Loc.T("Inicio");
    public override string Subtitle => Loc.T("Resumen de tu nodo y tu billetera");
    public override Geometry Icon => Icons.Home;

    [ObservableProperty] private bool _hasWallet;
    [ObservableProperty] private string _balance = "0";
    [ObservableProperty] private string _pending = "";
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private string _pseudonym = "";
    [ObservableProperty] private string _shortAddress = "";

    [ObservableProperty] private string _height = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DifficultyText))] private string _difficulty = "";
    [ObservableProperty] private string _networkHashrate = "";
    [ObservableProperty] private string _peers = "0";
    [ObservableProperty] private string _peersDetail = "";
    [ObservableProperty] private string _issued = "";
    [ObservableProperty] private string _issuedPercent = "";
    [ObservableProperty] private double _issuedRatio;
    [ObservableProperty] private string _subsidy = "";
    [ObservableProperty] private string _nextHalving = "";
    [ObservableProperty] private string _mempool = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LastBlockAgoText))] private string _lastBlockAgo = "";

    [ObservableProperty] private bool _isMining;
    [ObservableProperty] private string _miningHashrate = "";
    [ObservableProperty] private IReadOnlyList<double>? _hashrateHistory;

    public ObservableCollection<BlockItem> RecentBlocks { get; } = [];
    public ObservableCollection<LogItem> Activity { get; } = [];

    public string MaxSupply => Format.Shent(Money.MaxSupply);
    public string MaxSupplyText => Loc.T("de {0} SHENT máximos", MaxSupply);
    public string DifficultyText => Loc.T("Dificultad: {0}", Difficulty);
    public string LastBlockAgoText => Loc.T("Último bloque: {0}", LastBlockAgo);

    public override void Refresh()
    {
        var host = Shell.Host;
        var node = host.Node;
        var status = node.GetStatus();

        var wallet = host.Wallet;
        HasWallet = wallet is not null;
        if (wallet is not null && host.WalletTotals() is { } totals)
        {
            Balance = Format.Shent(totals.Balance);
            HasPending = totals.PendingOut > 0 || totals.PendingIn > 0;
            Pending = HasPending ? Loc.T("Pendiente: +{0} / −{1}", Format.Shent(totals.PendingIn), Format.Shent(totals.PendingOut)) : "";
            Pseudonym = wallet.Pseudonym;
            ShortAddress = Format.ShortAddress(wallet.EncodedAddress);
        }

        Height = Format.Integer(status.Height);
        Difficulty = Format.Number(status.Difficulty);
        NetworkHashrate = Format.Hashrate(status.NetworkHashrate);
        Peers = status.PeersConnected.ToString();
        PeersDetail = Loc.T("{0} salientes · {1} entrantes", Format.Integer(status.Outbound), Format.Integer(status.Inbound));
        Issued = Format.Shent(status.IssuedSupply);
        IssuedRatio = status.MaxSupply == 0 ? 0 : (double)status.IssuedSupply / status.MaxSupply;
        IssuedPercent = Format.Percent(IssuedRatio, 4);
        Subsidy = $"{Format.Shent(status.CurrentSubsidy)} SHENT";
        NextHalving = Loc.T("Próximo halving en el bloque {0}", Format.Integer(status.NextHalvingHeight));
        Mempool = status.MempoolCount == 1 ? Loc.T("1 transacción pendiente") : Loc.T("{0} transacciones pendientes", Format.Integer(status.MempoolCount));
        LastBlockAgo = status.Height == 0 ? Loc.T("Génesis") : Format.TimeAgo(status.TipTimestamp);

        IsMining = node.Miner.IsRunning;
        MiningHashrate = Format.Hashrate(node.Miner.Hashrate);
        HashrateHistory = node.Miner.HashrateHistory;

        if (status.Height != _lastHeight || RecentBlocks.Count > 0 && RecentBlocks[0].TimeAgo != Format.TimeAgo(RecentBlocks[0].Timestamp))
        {
            _lastHeight = status.Height;
            RecentBlocks.Clear();
            foreach (var b in node.Chain.GetRecentBlocks(6))
            {
                RecentBlocks.Add(BlockItem.From(b, node.Network, wallet?.Address));
            }
        }

        var log = host.Log.Snapshot();
        var newest = log.Count > 0 ? log[^1] : null;
        if (!ReferenceEquals(newest, _lastLog))
        {
            _lastLog = newest;
            Activity.Clear();
            foreach (var entry in log.TakeLast(8).Reverse()) Activity.Add(LogItem.From(entry));
        }
    }

    [RelayCommand]
    private void ToggleMining() => Shell.Mining.ToggleCommand.Execute(null);

    [RelayCommand]
    private void OpenMining() => Shell.Navigate(Shell.Mining);

    [RelayCommand]
    private void OpenExplorer() => Shell.Navigate(Shell.Explorer);

    [RelayCommand]
    private void Send()
    {
        if (Shell.Host.Wallet is null) { Shell.Navigate(Shell.Wallet); return; }
        Shell.ShowDialog(new SendDialogViewModel(Shell));
    }

    [RelayCommand]
    private void Receive()
    {
        if (Shell.Host.Wallet is null) { Shell.Navigate(Shell.Wallet); return; }
        Shell.ShowDialog(new ReceiveDialogViewModel(Shell));
    }

    [RelayCommand]
    private void CreateWallet() => Shell.ShowDialog(new CreateWalletDialogViewModel(Shell));

    [RelayCommand]
    private void ImportWallet() => Shell.ShowDialog(new ImportWalletDialogViewModel(Shell));
}
