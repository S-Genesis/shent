using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed record DetailRow(string Label, string Value, bool Mono = false, bool Copyable = false);

public sealed record TxRow(string Id, string ShortId, string Kind, string From, string To, string Amount, string Fee)
{
    public string FeeText => Loc.T("comisión {0}", Fee);
}

public abstract record ExplorerDetail(string Kind, string Heading, IReadOnlyList<DetailRow> Rows);

public sealed record BlockDetail(string Heading, IReadOnlyList<DetailRow> Rows, IReadOnlyList<TxRow> Transactions)
    : ExplorerDetail(Loc.T("Bloque"), Heading, Rows);

public sealed record TxDetail(string Heading, IReadOnlyList<DetailRow> Rows)
    : ExplorerDetail(Loc.T("Transacción"), Heading, Rows);

public sealed record AddressDetail(string Heading, IReadOnlyList<DetailRow> Rows, IReadOnlyList<TxRow> Transactions)
    : ExplorerDetail(Loc.T("Dirección"), Heading, Rows);

public sealed partial class ExplorerViewModel(MainViewModel shell) : PageViewModel(shell)
{
    private const int PageSize = 25;
    private long _lastHeight = -1;

    public override string Title => Loc.T("Explorador");
    public override string Subtitle => Loc.T("Bloques, transacciones y direcciones de la cadena local");
    public override Geometry Icon => Icons.Cube;

    public ObservableCollection<BlockItem> Blocks { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _searchError;
    [ObservableProperty] private ExplorerDetail? _detail;
    [ObservableProperty] private BlockItem? _selectedBlock;

    public bool HasDetail => Detail is not null;

    partial void OnDetailChanged(ExplorerDetail? value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnSelectedBlockChanged(BlockItem? value)
    {
        if (value is null) return;
        var block = Shell.Host.Node.Chain.GetBlockAtHeight(value.Height);
        if (block is not null) Detail = BuildBlockDetail(block, value.Height);
    }

    public override void Refresh()
    {
        var chain = Shell.Host.Node.Chain;
        if (chain.Height == _lastHeight) return;
        _lastHeight = chain.Height;
        var count = Math.Max(PageSize, Blocks.Count);
        Blocks.Clear();
        foreach (var b in chain.GetRecentBlocks(count)) Blocks.Add(BlockItem.From(b, chain.Network, Shell.Host.Wallet?.Address));
    }

    [RelayCommand]
    private void LoadMore()
    {
        var chain = Shell.Host.Node.Chain;
        if (Blocks.Count == 0) return;
        var from = Blocks[^1].Height - 1;
        if (from < 0) return;
        foreach (var b in chain.GetRecentBlocks(PageSize, from)) Blocks.Add(BlockItem.From(b, chain.Network, Shell.Host.Wallet?.Address));
    }

    [RelayCommand]
    private void CloseDetail()
    {
        Detail = null;
        SelectedBlock = null;
    }

    [RelayCommand]
    private async Task Copy(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        await Shell.Platform.CopyToClipboardAsync(value);
        Shell.ShowToast(Loc.T("Copiado al portapapeles."), ToastKind.Success);
    }

    [RelayCommand]
    private void OpenTx(TxRow? row)
    {
        if (row is null) return;
        Query = row.Id;
        Search();
    }

    [RelayCommand]
    private void Search()
    {
        SearchError = null;
        var q = Query.Trim();
        if (q.Length == 0) return;
        var node = Shell.Host.Node;
        var chain = node.Chain;
        var network = node.Network;

        if (long.TryParse(q.TrimStart('#'), out var height))
        {
            var block = chain.GetBlockAtHeight(height);
            if (block is null) { SearchError = Loc.T("No existe el bloque {0} en la cadena principal.", height); return; }
            Detail = BuildBlockDetail(block, height);
            return;
        }

        if (Address.TryParse(q, network.AddressHrp, out var address, out _))
        {
            Detail = BuildAddressDetail(address!, q);
            return;
        }

        if (Hash256.TryParse(q, out var hash))
        {
            var block = chain.GetBlock(hash);
            if (block is not null && chain.IsInMainChain(hash))
            {
                Detail = BuildBlockDetail(block, block.Header.Height);
                return;
            }
            var tx = chain.GetTransaction(hash);
            if (tx is not null)
            {
                Detail = BuildTxDetail(tx.Transaction, tx);
                return;
            }
            var pending = node.Mempool.Get(hash);
            if (pending is not null)
            {
                Detail = BuildTxDetail(pending, null);
                return;
            }
            SearchError = Loc.T("No se encontró ningún bloque ni transacción con ese hash.");
            return;
        }

        SearchError = Loc.T("Buscá por altura (ej. 1200), hash de bloque, ID de transacción o dirección {0}1….", network.AddressHrp);
    }

    private BlockDetail BuildBlockDetail(Block block, long height)
    {
        var network = Shell.Host.Node.Network;
        var chain = Shell.Host.Node.Chain;
        var h = block.Header;
        var fees = block.Transactions.Skip(1).Sum(t => t.Fee);
        var rows = new List<DetailRow>
        {
            new(Loc.T("Altura"), Format.Integer(height)),
            new(Loc.T("Hash del bloque (SHA-256d de la cabecera)"), block.Hash.ToString(), true, true),
            new(Loc.T("Bloque anterior"), h.PrevHash.ToString(), true, true),
            new(Loc.T("Raíz Merkle"), h.MerkleRoot.ToString(), true, true),
            new(Loc.T("Fecha"), Format.LocalTime(h.Timestamp)),
            new(Loc.T("Confirmaciones"), Format.Integer(chain.Height - height + 1)),
            new(Loc.T("Dificultad"), Format.Number(Target.Difficulty(h.Bits))),
            new(Loc.T("Bits / nonce"), $"0x{h.Bits:x8} / {h.Nonce}", true),
            new(Loc.T("Minero"), block.Coinbase.Recipient.Encode(network), true, true),
            new(Loc.T("Recompensa"), Loc.T("{0} SHENT (incluye {1} de comisiones)", Format.Shent(block.Coinbase.Amount), Format.Shent(fees))),
            new(Loc.T("Tamaño"), Loc.T("{0} bytes", Format.Integer(block.Serialize().Length))),
        };
        var txs = block.Transactions.Select(t => ToRow(t, network)).ToList();
        return new BlockDetail(Loc.T("Bloque #{0}", Format.Integer(height)), rows, txs);
    }

    private TxDetail BuildTxDetail(Transaction tx, TxLocation? loc)
    {
        var network = Shell.Host.Node.Network;
        var rows = new List<DetailRow>
        {
            new(Loc.T("ID de transacción (SHA-256d del cuerpo firmado)"), tx.Id.ToString(), true, true),
            new(Loc.T("Estado"), loc is null
                ? Loc.T("Pendiente en mempool")
                : loc.Confirmations == 1
                    ? Loc.T("Confirmada en el bloque {0} (1 confirmación)", Format.Integer(loc.Height))
                    : Loc.T("Confirmada en el bloque {0} ({1} confirmaciones)", Format.Integer(loc.Height), Format.Integer(loc.Confirmations))),
            new(Loc.T("Tipo"), tx.IsCoinbase ? Loc.T("Coinbase (recompensa de minado)") : Loc.T("Transferencia")),
        };
        if (!tx.IsCoinbase) rows.Add(new(Loc.T("Remitente"), tx.Sender.Encode(network), true, true));
        rows.Add(new(Loc.T("Destinatario"), tx.Recipient.Encode(network), true, true));
        rows.Add(new(Loc.T("Importe"), $"{Format.Shent(tx.Amount)} SHENT"));
        if (!tx.IsCoinbase)
        {
            rows.Add(new(Loc.T("Comisión"), $"{Format.Shent(tx.Fee)} SHENT"));
            rows.Add(new(Loc.T("Nonce"), tx.Nonce.ToString()));
            rows.Add(new(Loc.T("Expira"), tx.ExpiresAtHeight == 0 ? Loc.T("Sin expiración") : Loc.T("Bloque {0}", Format.Integer(tx.ExpiresAtHeight))));
            if (tx.Memo.Length > 0) rows.Add(new(Loc.T("Memo"), System.Text.Encoding.UTF8.GetString(tx.Memo)));
        }
        if (loc is not null)
        {
            rows.Add(new(Loc.T("Bloque"), loc.BlockHash.ToString(), true, true));
            rows.Add(new(Loc.T("Fecha"), Format.LocalTime(loc.Timestamp)));
        }
        return new TxDetail(Loc.T("Transacción {0}", tx.Id.ToShortString()), rows);
    }

    private AddressDetail BuildAddressDetail(Address address, string encoded)
    {
        var node = Shell.Host.Node;
        var account = node.Chain.GetAccount(address);
        var (outgoing, incoming) = node.Mempool.PendingTotals(address);
        var rows = new List<DetailRow>
        {
            new(Loc.T("Dirección"), encoded, true, true),
            new(Loc.T("Seudónimo (no único)"), Shent.Core.Wallet.Pseudonym.FromAddress(address)),
            new(Loc.T("Saldo confirmado"), $"{Format.Shent(account.Balance)} SHENT"),
            new(Loc.T("Pendiente"), $"+{Format.Shent(incoming)} / −{Format.Shent(outgoing)}"),
            new(Loc.T("Transacciones enviadas (nonce)"), account.Nonce.ToString()),
        };
        var txs = node.Chain.GetAddressHistory(address, 50).Select(l => ToRow(l.Transaction, node.Network)).ToList();
        return new AddressDetail(Address.Shorten(encoded), rows, txs);
    }

    private static TxRow ToRow(Transaction tx, NetworkParams network) => new(
        tx.Id.ToString(),
        tx.Id.ToShortString(),
        tx.IsCoinbase ? Loc.T("Coinbase") : Loc.T("Transferencia"),
        tx.IsCoinbase ? Loc.T("Nuevas monedas") : Address.Shorten(tx.Sender.Encode(network)),
        Address.Shorten(tx.Recipient.Encode(network)),
        Format.Shent(tx.Amount),
        tx.IsCoinbase ? "—" : Format.Shent(tx.Fee));
}
