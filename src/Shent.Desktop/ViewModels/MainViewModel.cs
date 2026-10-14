using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Core.Node;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;

namespace Shent.Desktop.ViewModels;

public enum ToastKind { Info, Success, Error }

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _toastCts;
    private long _walletScanHeight = -1;

    public AppHost Host { get; }
    public IPlatformServices Platform { get; }

    public DashboardViewModel Dashboard { get; }
    public MiningViewModel Mining { get; }
    public WalletViewModel Wallet { get; }
    public NetworkViewModel Network { get; }
    public ExplorerViewModel Explorer { get; }
    public StatsViewModel Stats { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private ToastKind _toastKind;
    [ObservableProperty] private bool _isToastVisible;

    [ObservableProperty] private string _networkName = "";
    [ObservableProperty] private bool _isMainnet;
    [ObservableProperty] private string _heightText = "";
    [ObservableProperty] private string _peersText = "";
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private string _syncState = "warn";
    [ObservableProperty] private bool _isMining;
    [ObservableProperty] private string _miningText = "";
    [ObservableProperty] private bool _hasWallet;
    [ObservableProperty] private string _walletPseudonym = "";
    [ObservableProperty] private string _walletShortAddress = "";
    [ObservableProperty] private string _walletBalance = "";
    [ObservableProperty] private string _walletInitials = "";

    public bool HasDialog => Dialog is not null;
    public string VersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    public MainViewModel(AppHost host, IPlatformServices platform)
    {
        Host = host;
        Platform = platform;

        Dashboard = new DashboardViewModel(this);
        Mining = new MiningViewModel(this);
        Wallet = new WalletViewModel(this);
        Network = new NetworkViewModel(this);
        Explorer = new ExplorerViewModel(this);
        Stats = new StatsViewModel(this);
        Settings = new SettingsViewModel(this);
        Pages = [Dashboard, Mining, Wallet, Network, Explorer, Stats, Settings];
        _currentPage = Dashboard;

        _selectedLanguage = Loc.Current;
        host.WalletChanged += PostWalletChanged;
        host.NodeRestarted += PostNodeRestarted;
        AttachNodeEvents();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
        CurrentPage.OnActivated();
    }

    partial void OnCurrentPageChanged(PageViewModel value) => value.OnActivated();

    partial void OnDialogChanged(DialogViewModel? value) => OnPropertyChanged(nameof(HasDialog));

    public void Navigate(PageViewModel page) => CurrentPage = page;

    [RelayCommand]
    private void GoToWallet() => CurrentPage = Wallet;

    public void ShowDialog(DialogViewModel dialog) => Dialog = dialog;

    public void CloseDialog(DialogViewModel dialog)
    {
        if (!ReferenceEquals(Dialog, dialog)) return;
        Dialog = null;
        dialog.OnClosed();
    }

    [RelayCommand]
    private void DismissDialog()
    {
        if (Dialog is { IsBusy: false } dialog) dialog.Close();
    }

    public async void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastMessage = message;
        ToastKind = kind;
        IsToastVisible = true;
        try
        {
            await Task.Delay(kind == ToastKind.Error ? 6000 : 3500, cts.Token);
            IsToastVisible = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    [RelayCommand]
    private void HideToast() => IsToastVisible = false;

    [RelayCommand]
    private async Task CopyAddress()
    {
        if (Host.Wallet is null) return;
        await Platform.CopyToClipboardAsync(Host.Wallet.EncodedAddress);
        ShowToast(Loc.T("Dirección copiada al portapapeles."), ToastKind.Success);
    }

    public IReadOnlyList<LanguageInfo> Languages => Loc.Languages;

    [ObservableProperty] private LanguageInfo _selectedLanguage;

    partial void OnSelectedLanguageChanged(LanguageInfo value)
    {
        if (value.Code == Loc.Current.Code) return;
        if (Dialog is not null)
        {
            ShowToast(Loc.T("Cerrá la ventana abierta antes de cambiar el idioma."), ToastKind.Info);
            Dispatcher.UIThread.Post(() => SelectedLanguage = Loc.Current);
            return;
        }
        Dispatcher.UIThread.Post(() => (Avalonia.Application.Current as App)?.ChangeLanguage(value.Code));
    }

    private void PostWalletChanged() => Dispatcher.UIThread.Post(OnWalletChanged);

    private void PostNodeRestarted() => Dispatcher.UIThread.Post(OnNodeRestarted);

    private Shent.Core.Mining.Miner? _attachedMiner;

    private void AttachNodeEvents()
    {
        if (_attachedMiner is not null) _attachedMiner.BlockFound -= OnBlockFound;
        _attachedMiner = Host.Node.Miner;
        _attachedMiner.BlockFound += OnBlockFound;
    }

    private void OnBlockFound(Shent.Core.Mining.MinedBlockInfo info) => Dispatcher.UIThread.Post(() =>
    {
        Mining.OnBlockFound(info);
        ShowToast(Loc.T("¡Bloque {0} minado! +{1} SHENT", info.Height, Format.Shent(info.Reward)), ToastKind.Success);
    });

    private void OnNodeRestarted()
    {
        AttachNodeEvents();
        Mining.OnNodeRestarted();
        Network.OnNodeRestarted();
        Tick();
        CurrentPage.OnActivated();
    }

    private void OnWalletChanged()
    {
        Wallet.OnWalletChanged();
        Mining.OnWalletChanged();
        Dashboard.Refresh();
        Tick();
    }

    private void Tick()
    {
        var node = Host.Node;
        var status = node.GetStatus();
        NetworkName = status.Network switch
        {
            "mainnet" => Loc.T("Red principal"),
            "testnet" => Loc.T("Red de pruebas"),
            "regtest" => Loc.T("Regtest local"),
            _ => status.Network,
        };
        IsMainnet = status.Network == "mainnet";
        HeightText = Loc.T("Bloque {0}", Format.Integer(status.Height));
        PeersText = status.PeersConnected == 1 ? Loc.T("1 par") : Loc.T("{0} pares", status.PeersConnected);

        if (status.Syncing && status.BestKnownHeight > status.Height)
        {
            SyncText = Loc.T("Sincronizando {0} / {1}", Format.Integer(status.Height), Format.Integer(status.BestKnownHeight));
            SyncState = "warn";
        }
        else if (status.PeersConnected == 0)
        {
            SyncText = Loc.T("Sin pares: buscando nodos");
            SyncState = "bad";
        }
        else
        {
            SyncText = Loc.T("Sincronizado");
            SyncState = "ok";
        }

        IsMining = node.Miner.IsRunning;
        MiningText = IsMining ? Format.Hashrate(node.Miner.Hashrate) : Loc.T("Minado en pausa");

        var wallet = Host.Wallet;
        HasWallet = wallet is not null;
        if (wallet is not null)
        {
            // Al llegar bloques nuevos (p. ej. sincronizando un respaldo restaurado) pueden aparecer direcciones de cobro usadas.
            if (status.Height != _walletScanHeight)
            {
                _walletScanHeight = status.Height;
                if (Host.DiscoverWalletAddresses()) Wallet.OnWalletChanged();
            }
            WalletPseudonym = wallet.Pseudonym;
            WalletShortAddress = Format.ShortAddress(wallet.EncodedAddress);
            WalletInitials = wallet.Pseudonym.Length >= 2 ? wallet.Pseudonym[..2].ToUpperInvariant() : "SH";
            WalletBalance = $"{Format.Shent(Host.WalletTotals()?.Balance ?? 0)} SHENT";
        }

        CurrentPage.Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _toastCts?.Cancel();
        Host.WalletChanged -= PostWalletChanged;
        Host.NodeRestarted -= PostNodeRestarted;
        if (_attachedMiner is not null) _attachedMiner.BlockFound -= OnBlockFound;
        Mining.Dispose();
    }
}
