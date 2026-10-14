using Shent.Core.Consensus;
using Shent.Core.Crypto;

namespace Shent.Core.Wallet;

public sealed record WalletInfo(string FilePath, string Address, string Pseudonym, string Label, DateTimeOffset CreatedAt);

public sealed class WalletLockedOutException(TimeSpan wait)
    : Exception($"Demasiados intentos fallidos. Esperá {Math.Ceiling(wait.TotalSeconds)} s antes de volver a intentar.")
{
    public TimeSpan Wait { get; } = wait;
}

/// <summary>Gestión de respaldos cifrados en la carpeta local de billeteras.</summary>
public sealed class WalletManager
{
    private readonly NetworkParams _network;
    private readonly string _directory;
    private readonly UnlockThrottle _throttle;
    private readonly KdfParameters? _kdf;

    public WalletManager(NetworkParams network, string networkDataDirectory, KdfParameters? kdf = null)
    {
        _network = network;
        _directory = Path.Combine(networkDataDirectory, "wallets");
        Directory.CreateDirectory(_directory);
        _throttle = new UnlockThrottle(Path.Combine(_directory, ".unlock-throttle.json"));
        _kdf = kdf;
    }

    public string WalletsDirectory => _directory;

    public List<WalletInfo> List()
    {
        var result = new List<WalletInfo>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + WalletFile.Extension))
        {
            try
            {
                var file = WalletFile.FromJson(File.ReadAllText(path));
                if (file.Network != _network.Name) continue;
                result.Add(new WalletInfo(path, file.Address, file.Pseudonym, file.Label, file.CreatedAt));
            }
            catch (Exception ex) when (ex is WalletFormatException or IOException)
            {
                // Archivos ajenos o dañados se ignoran en el listado.
            }
        }
        return result.OrderBy(w => w.CreatedAt).ToList();
    }

    /// <summary>Crea una billetera nueva: semilla aleatoria del sistema operativo, cifrada con la contraseña maestra.</summary>
    public UnlockedWallet Create(string password, string label)
    {
        var seed = Ed25519Keys.GenerateSeed();
        try
        {
            var file = WalletFile.Encrypt(seed, password, _network, label, _kdf);
            var path = PathFor(file);
            WriteAtomic(path, file.ToJson());
            return new UnlockedWallet(file, path, (byte[])seed.Clone(), _network);
        }
        finally
        {
            Array.Clear(seed);
        }
    }

    public UnlockedWallet Unlock(string filePath, string password)
    {
        var file = WalletFile.FromJson(File.ReadAllText(filePath));
        return UnlockFile(file, filePath, password);
    }

    /// <summary>Importa un respaldo desde cualquier ubicación (otra PC, pendrive, nube) y lo copia a la carpeta local.</summary>
    public UnlockedWallet Import(string json, string password)
    {
        var file = WalletFile.FromJson(json);
        var path = PathFor(file);
        if (File.Exists(path))
        {
            // Si la copia local ya entregó más direcciones de cobro que el respaldo, se conserva ese avance.
            try
            {
                var local = WalletFile.FromJson(File.ReadAllText(path));
                if (local.Address == file.Address && local.ReceiveIndex > file.ReceiveIndex) file = file with { ReceiveIndex = local.ReceiveIndex };
            }
            catch (Exception ex) when (ex is WalletFormatException or IOException)
            {
            }
        }
        var wallet = UnlockFile(file, path, password);
        if (!File.Exists(path)) WriteAtomic(path, file.ToJson());
        return wallet;
    }

    private UnlockedWallet UnlockFile(WalletFile file, string path, string password)
    {
        var key = file.Address;
        var wait = _throttle.RemainingWait(key);
        if (wait > TimeSpan.Zero) throw new WalletLockedOutException(wait);
        try
        {
            var seed = file.Decrypt(password, _network);
            _throttle.RecordSuccess(key);
            return new UnlockedWallet(file, path, seed, _network);
        }
        catch (WalletPasswordException)
        {
            _throttle.RecordFailure(key);
            throw;
        }
    }

    public TimeSpan RemainingWait(string address) => _throttle.RemainingWait(address);

    /// <summary>Entrega una dirección de cobro nueva y guarda el avance, para no repetirla nunca.</summary>
    public WalletAddress NewReceiveAddress(UnlockedWallet wallet)
    {
        var address = wallet.NextReceiveAddress();
        PersistReceiveIndex(wallet);
        return address;
    }

    /// <summary>Recupera direcciones de cobro con movimientos en la cadena (respaldo restaurado en otra PC).</summary>
    public bool DiscoverAddresses(UnlockedWallet wallet, Func<Address, bool> isUsed)
    {
        if (!wallet.DiscoverUsed(isUsed)) return false;
        PersistReceiveIndex(wallet);
        return true;
    }

    private void PersistReceiveIndex(UnlockedWallet wallet)
    {
        var file = wallet.File with { ReceiveIndex = wallet.ReceiveIndex };
        if (File.Exists(wallet.FilePath)) WriteAtomic(wallet.FilePath, file.ToJson());
        wallet.ReplaceFile(file);
    }

    /// <summary>Cambia la contraseña maestra y reemplaza el respaldo local.</summary>
    public void ChangePassword(UnlockedWallet wallet, string newPassword)
    {
        var file = wallet.ReEncrypt(newPassword, _network);
        WriteAtomic(wallet.FilePath, file.ToJson());
        wallet.ReplaceFile(file);
    }

    private string PathFor(WalletFile file)
    {
        var safe = new string(file.Pseudonym.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return Path.Combine(_directory, $"{safe}-{file.Address[^8..]}{WalletFile.Extension}");
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
