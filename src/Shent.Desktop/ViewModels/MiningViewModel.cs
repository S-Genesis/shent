using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Core.Mining;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed partial class MiningViewModel : PageViewModel, IDisposable
{
    private bool _suppressRestart;

    public override string Title => Loc.T("Minería");
    public override string Subtitle => Loc.T("Prueba de trabajo con la CPU de esta PC");
    public override Geometry Icon => Icons.Flash;

    public int MaxThreads { get; } = Environment.ProcessorCount;
    public string CpuText { get; } = Loc.T("{0} hilos lógicos disponibles", Environment.ProcessorCount);

    [ObservableProperty] private bool _isMining;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _intensity;
    [ObservableProperty] private string _payoutAddress = "";
    [ObservableProperty] private string? _payoutError;
    [ObservableProperty] private bool _autoStart;

    [ObservableProperty] private string _hashrate = "0 H/s";
    [ObservableProperty] private string _hashratePerThread = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalHashesText))]
    private string _totalHashes = "0";
    [ObservableProperty] private string _blocksFound = "0";
    [ObservableProperty] private string _uptime = "—";
    [ObservableProperty] private string _workingOn = "—";
    [ObservableProperty] private string _difficulty = "";
    [ObservableProperty] private string _networkHashrate = "";
    [ObservableProperty] private string _networkShare = "—";
    [ObservableProperty] private double _networkShareRatio;
    [ObservableProperty] private string _expectedTime = "—";
    [ObservableProperty] private string _dailyEstimate = "—";
    [ObservableProperty] private string _reward = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string? _offlineWarning;
    [ObservableProperty] private IReadOnlyList<double>? _hashrateHistory;

    public ObservableCollection<FoundBlockItem> FoundBlocks { get; } = [];

    public string TotalHashesText => Loc.T("{0} hashes totales", TotalHashes);
    public string ThreadsText => Threads == 1 ? Loc.T("1 hilo") : Loc.T("{0} hilos", Threads);
    public string IntensityText => $"{Intensity} %";
    public string IntensityHint => Intensity switch
    {
        <= 30 => Loc.T("Suave: casi no se nota mientras usás la PC."),
        <= 70 => Loc.T("Equilibrado: buen rendimiento sin trabar el equipo."),
        _ => Loc.T("Máximo: la CPU trabaja a pleno (más calor y consumo)."),
    };
    public string ToggleText => IsMining ? Loc.T("Detener minado") : Loc.T("Iniciar minado");
    public string ReactorText => IsMining ? Loc.T("MINANDO") : Loc.T("EN PAUSA");
    public bool HasFoundBlocks => FoundBlocks.Count > 0;

    public MiningViewModel(MainViewModel shell) : base(shell)
    {
        var config = shell.Host.Config;
        _suppressRestart = true;
        Threads = Math.Clamp(config.MiningThreads, 1, MaxThreads);
        Intensity = Math.Clamp(config.MiningIntensity, 10, 100);
        AutoStart = config.Mine;
        PayoutAddress = config.MiningAddress ?? shell.Host.Wallet?.EncodedAddress ?? "";
        _suppressRestart = false;
        FoundBlocks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFoundBlocks));
    }

    partial void OnIsMiningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ReactorText));
    }

    partial void OnThreadsChanged(int value)
    {
        OnPropertyChanged(nameof(ThreadsText));
        if (_suppressRestart) return;
        SaveSettings();
        var miner = Shell.Host.Node.Miner;
        if (miner.IsRunning && miner.Payout is { } payout && miner.ThreadCount != value)
        {
            miner.Start(payout, value, Intensity);
        }
    }

    partial void OnIntensityChanged(int value)
    {
        OnPropertyChanged(nameof(IntensityText));
        OnPropertyChanged(nameof(IntensityHint));
        if (_suppressRestart) return;
        Shell.Host.Node.Miner.Intensity = value;
        SaveSettings();
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_suppressRestart) return;
        SaveSettings();
    }

    partial void OnPayoutAddressChanged(string value) => PayoutError = null;

    private void SaveSettings()
    {
        var config = Shell.Host.Config;
        config.MiningThreads = Threads;
        config.MiningIntensity = Intensity;
        config.Mine = AutoStart;
        if (Address.TryParse(PayoutAddress.Trim(), Shell.Host.Node.Network.AddressHrp, out _, out _))
        {
            config.MiningAddress = PayoutAddress.Trim();
        }
        try { Shell.Host.SaveConfig(); } catch (IOException) { }
    }

    [RelayCommand]
    private void Toggle()
    {
        var miner = Shell.Host.Node.Miner;
        if (miner.IsRunning)
        {
            miner.Stop();
            Refresh();
            return;
        }

        var text = PayoutAddress.Trim();
        if (text.Length == 0 && Shell.Host.Wallet is { } wallet)
        {
            text = PayoutAddress = wallet.EncodedAddress;
        }
        if (text.Length == 0)
        {
            PayoutError = Loc.T("Creá o desbloqueá una billetera, o pegá la dirección que recibirá las recompensas.");
            return;
        }
        if (!Address.TryParse(text, Shell.Host.Node.Network.AddressHrp, out var payout, out var error))
        {
            PayoutError = Loc.Message(error);
            return;
        }

        SaveSettings();
        miner.Start(payout!, Threads, Intensity);
        Shell.ShowToast(Loc.T("Minado iniciado. Las recompensas llegan a la dirección indicada."), ToastKind.Success);
        Refresh();
    }

    [RelayCommand]
    private void UseWalletAddress()
    {
        if (Shell.Host.Wallet is { } wallet) PayoutAddress = wallet.EncodedAddress;
        else Shell.Navigate(Shell.Wallet);
    }

    [RelayCommand]
    private void SetIntensity(string value)
    {
        if (int.TryParse(value, out var n)) Intensity = n;
    }

    public void OnBlockFound(MinedBlockInfo info)
    {
        FoundBlocks.Insert(0, new FoundBlockItem(info.Height, info.Hash.ToShortString(), Format.Shent(info.Reward), info.FoundAt));
        while (FoundBlocks.Count > 50) FoundBlocks.RemoveAt(FoundBlocks.Count - 1);
    }

    public void OnWalletChanged()
    {
        if (Shell.Host.Wallet is { } wallet && string.IsNullOrWhiteSpace(PayoutAddress))
        {
            PayoutAddress = wallet.EncodedAddress;
        }
    }

    public void OnNodeRestarted()
    {
        if (!Address.TryParse(PayoutAddress.Trim(), Shell.Host.Node.Network.AddressHrp, out _, out _))
        {
            PayoutAddress = Shell.Host.Wallet?.EncodedAddress ?? "";
        }
        Refresh();
    }

    public override void Refresh()
    {
        var node = Shell.Host.Node;
        var miner = node.Miner;
        IsMining = miner.IsRunning;

        var rate = miner.Hashrate;
        Hashrate = Format.Hashrate(rate);
        HashratePerThread = IsMining && miner.ThreadCount > 0 ? Loc.T("{0} por hilo", Format.Hashrate(rate / miner.ThreadCount)) : Loc.T("Reactor apagado");
        TotalHashes = Format.Number(miner.TotalHashes);
        BlocksFound = Format.Integer(miner.BlocksFound);
        Uptime = miner.StartedAt is { } started ? Format.Duration((DateTimeOffset.UtcNow - started).TotalSeconds) : "—";
        WorkingOn = miner.CurrentHeight is { } h && IsMining ? $"#{Format.Integer(h)}" : "—";
        HashrateHistory = miner.HashrateHistory;

        var hasPeers = node.P2P.GetPeers().Any(p => p.HandshakeComplete);
        OfflineWarning = hasPeers
            ? null
            : IsMining
                ? Loc.T("Estás minando sin conexión a otros nodos. Estos bloques quedan solo en tu cadena: si al conectarte la red tiene una cadena con más trabajo, se descartan y sus recompensas desaparecen. Si sos el primero de la red, podés seguir.")
                : Loc.T("No hay otros nodos conectados. Si minás ahora, los bloques quedan solo en tu cadena y pueden descartarse cuando te conectes a la red. Esperá a ver al menos un par conectado, salvo que seas el primero de la red.");

        var context = node.Chain.GetTemplateContext();
        var expectedHashes = Target.Difficulty(context.Bits);
        var networkRate = node.Chain.EstimateNetworkHashrate();
        Difficulty = Format.Number(expectedHashes);
        NetworkHashrate = Format.Hashrate(networkRate);
        var nextHeight = context.Parent.Height + 1;
        var subsidy = Emission.SubsidyAt(node.Network, nextHeight);
        Reward = Loc.T("{0} SHENT + comisiones", Format.Shent(subsidy));

        if (IsMining && rate > 0)
        {
            var share = networkRate > 0 ? Math.Min(1, rate / Math.Max(networkRate, rate)) : 1;
            NetworkShareRatio = share;
            NetworkShare = Format.Percent(share);
            ExpectedTime = Format.Duration(expectedHashes / rate);
            var blocksPerDay = 86400.0 / node.Network.TargetSpacingSeconds;
            var dailyCoins = share * blocksPerDay * subsidy / Money.Coin;
            DailyEstimate = Loc.T("≈ {0} SHENT/día", Format.Decimal(dailyCoins, 2));
            StatusLine = node.P2P.IsSyncing
                ? Loc.T("Sincronizando la cadena: el minado sigue sobre la punta local.")
                : Loc.T("Buscando un nonce válido. Cada hash es un intento independiente.");
        }
        else
        {
            NetworkShareRatio = 0;
            NetworkShare = "—";
            ExpectedTime = "—";
            DailyEstimate = "—";
            StatusLine = Loc.T("Elegí hilos e intensidad y encendé el reactor.");
        }
    }

    public void Dispose()
    {
    }
}
