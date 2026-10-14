using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Consensus;
using Shent.Core.Model;
using Shent.Core.Wallet;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;

namespace Shent.Desktop.ViewModels;

public sealed partial class SendDialogViewModel : DialogViewModel
{
    private const int ExpiryBlocks = 72;
    private List<Transaction>? _prepared;

    public override string Title => Loc.T("Enviar SHENT");

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _recipient = "";
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _fee;
    [ObservableProperty] private string _memo = "";

    [ObservableProperty] private string _available = "";
    [ObservableProperty] private string _confirmRecipient = "";
    [ObservableProperty] private string _confirmRecipientPseudonym = "";
    [ObservableProperty] private string _confirmAmount = "";
    [ObservableProperty] private string _confirmFee = "";
    [ObservableProperty] private string _confirmTotal = "";
    [ObservableProperty] private string _confirmNonce = "";
    [ObservableProperty] private string _confirmExpiry = "";
    [ObservableProperty] private string? _confirmLinkWarning;
    [ObservableProperty] private string _txId = "";

    public bool IsFormStep => Step == 0;
    public bool IsConfirmStep => Step == 1;
    public bool IsDoneStep => Step == 2;
    public string MinFeeText { get; }
    public string Hrp { get; }

    public SendDialogViewModel(MainViewModel shell) : base(shell)
    {
        var network = shell.Host.Node.Network;
        _fee = Format.Shent(network.MinRelayFee);
        MinFeeText = Loc.T("Mínimo {0} SHENT. La comisión la cobra el minero que incluya la transacción.", Format.Shent(network.MinRelayFee));
        Hrp = network.AddressHrp;
        UpdateAvailable();
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsFormStep));
        OnPropertyChanged(nameof(IsConfirmStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    partial void OnRecipientChanged(string value) => Error = null;
    partial void OnAmountChanged(string value) => Error = null;

    /// <summary>Saldos por dirección propia, sin la del destinatario (no se puede enviar de una dirección a sí misma).</summary>
    private List<AddressBalance> Balances(Address? exclude = null) =>
        Shell.Host.WalletTotals()?.PerAddress.Where(b => exclude is null || !b.Address.Address.Equals(exclude)).ToList() ?? [];

    private void UpdateAvailable()
    {
        if (Shell.Host.WalletTotals() is not { } totals) return;
        Available = Format.Shent(totals.Available);
    }

    [RelayCommand]
    private void Max()
    {
        if (!Format.TryParseAmount(Fee, out var fee, out _)) fee = Shell.Host.Node.Network.MinRelayFee;
        Address.TryParse(Recipient.Trim(), Hrp, out var recipient, out _);
        var max = WalletBalances.MaxSendable(Balances(recipient), fee);
        Amount = max > 0 ? Format.Shent(max) : "0";
    }

    [RelayCommand]
    private void Review()
    {
        Error = null;
        var wallet = Shell.Host.Wallet;
        if (wallet is null) { Error = Loc.T("Desbloqueá una billetera primero."); return; }
        var node = Shell.Host.Node;
        var network = node.Network;

        if (!Address.TryParse(Recipient.Trim(), network.AddressHrp, out var recipient, out var addressError))
        {
            Error = Loc.Message(addressError);
            return;
        }
        if (recipient!.Equals(NetworkParams.BurnAddress))
        {
            Error = Loc.T("Esa es la dirección de quemado: las monedas enviadas ahí no podrían usarse nunca.");
            return;
        }
        if (!Format.TryParseAmount(Amount, out var amount, out var amountError) || amount <= 0)
        {
            Error = amountError ?? Loc.T("Importe inválido. Usá hasta 8 decimales (ej. 12,5).");
            return;
        }
        if (!Format.TryParseAmount(Fee, out var fee, out var feeError))
        {
            Error = feeError ?? Loc.T("La comisión mínima es {0} SHENT.", Format.Shent(network.MinRelayFee));
            return;
        }
        if (fee < network.MinRelayFee)
        {
            Error = Loc.T("La comisión mínima es {0} SHENT.", Format.Shent(network.MinRelayFee));
            return;
        }
        var memoBytes = System.Text.Encoding.UTF8.GetByteCount(Memo ?? "");
        if (memoBytes > Transaction.MaxMemoBytes)
        {
            Error = Loc.T("El memo admite hasta {0} bytes.", Format.Integer(Transaction.MaxMemoBytes));
            return;
        }
        var balances = Balances(recipient);
        var plan = WalletBalances.Plan(balances, amount, fee);
        if (plan is null)
        {
            var max = WalletBalances.MaxSendable(balances, fee);
            Error = Loc.T("Saldo insuficiente. Podés enviar hasta {0} SHENT (cada dirección tuya que aporta fondos paga su comisión).", Format.Shent(max));
            return;
        }

        var expires = node.Chain.Height + ExpiryBlocks;
        _prepared = plan
            .Select(part => wallet.CreateTransfer(network, part.From.Address, recipient, part.Amount, fee,
                node.Mempool.NextNonce(part.From.Address), expires, Memo ?? ""))
            .ToList();

        var totalFee = fee * plan.Count;
        var encoded = recipient.Encode(network);
        ConfirmRecipient = encoded;
        ConfirmRecipientPseudonym = $"{Shent.Core.Wallet.Pseudonym.FromAddress(recipient)} · {Address.Shorten(encoded)}";
        ConfirmAmount = $"{Format.Shent(amount)} SHENT";
        ConfirmFee = $"{Format.Shent(totalFee)} SHENT";
        ConfirmTotal = $"{Format.Shent(amount + totalFee)} SHENT";
        ConfirmNonce = plan.Count == 1 ? "1" : Loc.T("{0} (desde {1} direcciones tuyas)", plan.Count, plan.Count);
        ConfirmLinkWarning = plan.Count == 1
            ? null
            : Loc.T("Ninguna de tus direcciones tiene saldo suficiente sola, así que el pago sale de varias. Quien mire la cadena puede sospechar que esas direcciones son de la misma persona porque pagan juntas al mismo destinatario.");
        ConfirmExpiry = Loc.T("Si no se confirma antes del bloque {0} (~12 h), queda anulada.", Format.Integer(expires));
        Step = 1;
    }

    [RelayCommand]
    private void Back()
    {
        _prepared = null;
        Step = 0;
    }

    [RelayCommand]
    private void Submit()
    {
        if (_prepared is null) return;
        var sent = new List<Transaction>();
        foreach (var tx in _prepared)
        {
            var result = Shell.Host.Node.SubmitTransaction(tx);
            if (!result.Accepted)
            {
                var reason = Loc.Message(result.Error);
                Error = sent.Count == 0
                    ? reason ?? Loc.T("La transacción fue rechazada.")
                    : Loc.T("Se enviaron {0} de {1} partes; la siguiente fue rechazada: {2}", sent.Count, _prepared.Count, reason);
                _prepared = null;
                Step = 0;
                return;
            }
            sent.Add(tx);
        }
        TxId = string.Join(Environment.NewLine, sent.Select(t => t.Id.ToString()));
        _prepared = null;
        Step = 2;
        Shell.ShowToast(sent.Count == 1 ? Loc.T("Transacción enviada a la red.") : Loc.T("{0} transacciones enviadas a la red.", sent.Count), ToastKind.Success);
    }

    [RelayCommand]
    private async Task CopyTxId()
    {
        await Shell.Platform.CopyToClipboardAsync(TxId);
        Shell.ShowToast(Loc.T("ID copiado."), ToastKind.Success);
    }

    [RelayCommand]
    private async Task Paste()
    {
        var text = await Shell.Platform.GetClipboardTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) Recipient = text.Trim();
    }

    [RelayCommand]
    private void Finish() => Close();
}

/// <summary>
/// Muestra una dirección de cobro que nadie usó todavía: la última entregada si sigue sin movimientos,
/// o una nueva. Así cada pagador ve una dirección distinta y en la cadena no se puede juntar lo que cobrás.
/// </summary>
public sealed partial class ReceiveDialogViewModel : DialogViewModel
{
    public override string Title => Loc.T("Recibir SHENT");

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _pseudonym = "";
    [ObservableProperty] private string _shortAddress = "";
    [ObservableProperty] private string _indexText = "";

    public ReceiveDialogViewModel(MainViewModel shell) : base(shell)
    {
        if (shell.Host.Wallet is not { } wallet) return;
        var node = shell.Host.Node;
        var current = wallet.ReceiveIndex > 0 ? wallet.Addresses.FirstOrDefault(a => a.Index == wallet.ReceiveIndex) : null;
        if (current is not null)
        {
            var account = node.Chain.GetAccount(current.Address);
            var (_, incoming) = node.Mempool.PendingTotals(current.Address);
            if (account.Balance > 0 || account.Nonce > 0 || incoming > 0) current = null;
        }
        Show(current ?? NewAddress(wallet));
    }

    private WalletAddress NewAddress(UnlockedWallet wallet)
    {
        try
        {
            return Shell.Host.Wallets.NewReceiveAddress(wallet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.T("No se pudo guardar la nueva dirección ({0}). Se muestra la principal.", Loc.Message(ex.Message));
            return wallet.Addresses[0];
        }
    }

    private void Show(WalletAddress address)
    {
        Address = address.Encoded;
        Pseudonym = Shent.Core.Wallet.Pseudonym.FromAddress(address.Address);
        ShortAddress = Shent.Core.Consensus.Address.Shorten(address.Encoded);
        IndexText = address.IsPrimary ? Loc.T("Dirección principal") : Loc.T("Dirección de cobro n.º {0}", address.Index);
    }

    [RelayCommand]
    private void Another()
    {
        if (Shell.Host.Wallet is not { } wallet) return;
        Error = null;
        Show(NewAddress(wallet));
        Shell.Wallet.OnWalletChanged();
    }

    [RelayCommand]
    private async Task Copy()
    {
        await Shell.Platform.CopyToClipboardAsync(Address);
        Shell.ShowToast(Loc.T("Dirección copiada."), ToastKind.Success);
    }
}
