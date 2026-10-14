using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Core.Wallet;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed record SavedWalletItem(WalletInfo Info)
{
    public string Pseudonym => Info.Pseudonym;
    public string ShortAddress => Address.Shorten(Info.Address);
    public string Label => string.IsNullOrWhiteSpace(Info.Label) ? Loc.T("Billetera") : Info.Label;
    public string Created => Loc.T("Creada el {0}", Format.LocalDate(DateOnly.FromDateTime(Info.CreatedAt.ToLocalTime().DateTime)));
    public string Initials => Info.Pseudonym.Length >= 2 ? Info.Pseudonym[..2].ToUpperInvariant() : "SH";
}

public sealed partial class WalletViewModel(MainViewModel shell) : PageViewModel(shell)
{
    private long _lastHeight = -1;
    private int _lastMempool = -1;
    private int _lastAddressCount = -1;

    public override string Title => Loc.T("Billetera");
    public override string Subtitle => Loc.T("Tus claves, cifradas en esta PC");
    public override Geometry Icon => Icons.Wallet;

    public ObservableCollection<SavedWalletItem> SavedWallets { get; } = [];
    public ObservableCollection<TxItem> History { get; } = [];

    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private SavedWalletItem? _selectedSaved;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _unlockError;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _pseudonym = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _balance = "0";
    [ObservableProperty] private string _available = "0";
    [ObservableProperty] private string _pendingIn = "0";
    [ObservableProperty] private string _pendingOut = "0";
    [ObservableProperty] private string _nonce = "0";
    [ObservableProperty] private string _receiveAddresses = "0";

    public bool HasSavedWallets => SavedWallets.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool CanUnlock => SelectedSaved is not null && !IsBusy;

    partial void OnSelectedSavedChanged(SavedWalletItem? value)
    {
        UnlockError = null;
        OnPropertyChanged(nameof(CanUnlock));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanUnlock));

    partial void OnPasswordChanged(string value) => UnlockError = null;

    public override void OnActivated()
    {
        LoadSavedWallets();
        _lastHeight = -1;
        base.OnActivated();
    }

    public void OnWalletChanged()
    {
        _lastHeight = -1;
        _lastAddressCount = -1;
        History.Clear();
        LoadSavedWallets();
        Refresh();
    }

    private void LoadSavedWallets()
    {
        var selected = SelectedSaved?.Info.FilePath;
        SavedWallets.Clear();
        foreach (var info in Shell.Host.Wallets.List()) SavedWallets.Add(new SavedWalletItem(info));
        SelectedSaved = SavedWallets.FirstOrDefault(w => w.Info.FilePath == selected) ?? SavedWallets.FirstOrDefault();
        OnPropertyChanged(nameof(HasSavedWallets));
    }

    public override void Refresh()
    {
        var host = Shell.Host;
        var wallet = host.Wallet;
        IsUnlocked = wallet is not null;
        if (wallet is null) return;

        var node = host.Node;
        var network = node.Network;
        if (host.WalletTotals() is not { } totals) return;
        Pseudonym = wallet.Pseudonym;
        Label = string.IsNullOrWhiteSpace(wallet.File.Label) ? Loc.T("Billetera principal") : wallet.File.Label;
        Address = wallet.EncodedAddress;
        Balance = Format.Shent(totals.Balance);
        Available = Format.Shent(totals.Available);
        PendingIn = Format.Shent(totals.PendingIn);
        PendingOut = Format.Shent(totals.PendingOut);
        Nonce = Format.Integer(totals.Sent);
        ReceiveAddresses = Format.Integer(wallet.ReceiveIndex);

        var addressCount = totals.PerAddress.Count;
        if (node.Chain.Height == _lastHeight && node.Mempool.Count == _lastMempool && addressCount == _lastAddressCount) return;
        _lastHeight = node.Chain.Height;
        _lastMempool = node.Mempool.Count;
        _lastAddressCount = addressCount;

        History.Clear();
        var items = new List<TxItem>();
        foreach (var tx in node.Mempool.GetAllIncludingPrivate())
        {
            if (wallet.IsMine(tx.Recipient) || wallet.IsMine(tx.Sender))
            {
                items.Add(TxItem.FromPending(tx, wallet.IsMine, network));
            }
        }
        var seen = new HashSet<string>();
        foreach (var address in wallet.Addresses)
        {
            foreach (var loc in node.Chain.GetAddressHistory(address.Address, 100))
            {
                if (seen.Add(loc.Transaction.Id.ToString())) items.Add(TxItem.FromConfirmed(loc, wallet.IsMine, network));
            }
        }
        foreach (var item in items.OrderByDescending(i => i.Pending).ThenByDescending(i => i.Timestamp).Take(200)) History.Add(item);
        OnPropertyChanged(nameof(HasHistory));
    }

    [RelayCommand]
    private async Task Unlock()
    {
        if (SelectedSaved is not { } item || IsBusy) return;
        if (string.IsNullOrEmpty(Password))
        {
            UnlockError = Loc.T("Ingresá la contraseña maestra.");
            return;
        }
        IsBusy = true;
        UnlockError = null;
        var password = Password;
        try
        {
            var wallets = Shell.Host.Wallets;
            var wallet = await Task.Run(() => wallets.Unlock(item.Info.FilePath, password));
            Password = "";
            Shell.Host.SetWallet(wallet);
            Shell.ShowToast(Loc.T("Billetera {0} desbloqueada.", wallet.Pseudonym), ToastKind.Success);
        }
        catch (WalletLockedOutException ex)
        {
            UnlockError = Loc.T("Demasiados intentos fallidos. Esperá {0} s antes de volver a intentar.", Format.Integer((long)Math.Ceiling(ex.Wait.TotalSeconds)));
        }
        catch (WalletPasswordException ex)
        {
            UnlockError = Loc.Message(ex.Message);
        }
        catch (WalletFormatException ex)
        {
            UnlockError = Loc.Message(ex.Message);
        }
        catch (IOException)
        {
            UnlockError = Loc.T("No se pudo leer el archivo de la billetera.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Lock()
    {
        Shell.Host.SetWallet(null);
        Shell.ShowToast(Loc.T("Billetera bloqueada. La clave privada se borró de la memoria."));
    }

    [RelayCommand]
    private void Create() => Shell.ShowDialog(new CreateWalletDialogViewModel(Shell));

    [RelayCommand]
    private void Import() => Shell.ShowDialog(new ImportWalletDialogViewModel(Shell));

    [RelayCommand]
    private void Send() => Shell.ShowDialog(new SendDialogViewModel(Shell));

    [RelayCommand]
    private void Receive() => Shell.ShowDialog(new ReceiveDialogViewModel(Shell));

    [RelayCommand]
    private void Export() => Shell.ShowDialog(new ExportDialogViewModel(Shell));

    [RelayCommand]
    private async Task CopyAddress()
    {
        if (Shell.Host.Wallet is not { } wallet) return;
        await Shell.Platform.CopyToClipboardAsync(wallet.EncodedAddress);
        Shell.ShowToast(Loc.T("Dirección copiada."), ToastKind.Success);
    }

    [RelayCommand]
    private async Task CopyTxId(TxItem? item)
    {
        if (item is null) return;
        await Shell.Platform.CopyToClipboardAsync(item.Id);
        Shell.ShowToast(Loc.T("ID de transacción copiado."), ToastKind.Success);
    }

    [RelayCommand]
    private void OpenWalletsFolder() => Shell.Platform.OpenFolder(Shell.Host.Wallets.WalletsDirectory);
}
