using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shent.Core.Wallet;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;

namespace Shent.Desktop.ViewModels;

public sealed partial class CreateWalletDialogViewModel(MainViewModel shell) : DialogViewModel(shell)
{
    public override string Title => Loc.T("Crear billetera");

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _label = Loc.T("Mi billetera");
    [ObservableProperty] private bool _useGenerated = true;
    [ObservableProperty] private string _generatedPassword = PasswordPolicy.GenerateMasterPassword();
    [ObservableProperty] private string _customPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private bool _savedConfirmed;
    [ObservableProperty] private string _createdAddress = "";
    [ObservableProperty] private string _createdPseudonym = "";

    public bool IsFormStep => Step == 0;
    public bool IsDoneStep => Step == 1;
    public bool UseCustom
    {
        get => !UseGenerated;
        set => UseGenerated = !value;
    }
    public int Strength => PasswordPolicy.Strength(UseGenerated ? GeneratedPassword : CustomPassword);
    public string StrengthText => Strength switch
    {
        0 => Loc.T("Muy débil (mínimo {0} caracteres)", PasswordPolicy.MinLength),
        1 => Loc.T("Débil"),
        2 => Loc.T("Aceptable"),
        3 => Loc.T("Fuerte"),
        _ => Loc.T("Muy fuerte"),
    };
    public bool CanCreate => !IsBusy && SavedConfirmed;

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsFormStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    partial void OnUseGeneratedChanged(bool value)
    {
        OnPropertyChanged(nameof(UseCustom));
        OnPropertyChanged(nameof(Strength));
        OnPropertyChanged(nameof(StrengthText));
        Error = null;
    }

    partial void OnCustomPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(Strength));
        OnPropertyChanged(nameof(StrengthText));
        Error = null;
    }

    partial void OnSavedConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    private void Regenerate() => GeneratedPassword = PasswordPolicy.GenerateMasterPassword();

    [RelayCommand]
    private async Task CopyPassword()
    {
        await Shell.Platform.CopySecretAsync(GeneratedPassword);
        Shell.ShowToast(Loc.T("Contraseña copiada. El portapapeles se limpia en 60 segundos."));
    }

    [RelayCommand]
    private async Task Create()
    {
        Error = null;
        var password = UseGenerated ? GeneratedPassword : CustomPassword;
        try
        {
            PasswordPolicy.EnsureAcceptable(password);
        }
        catch (ArgumentException)
        {
            Error = Loc.T("La contraseña maestra debe tener al menos {0} caracteres.", PasswordPolicy.MinLength);
            return;
        }
        if (!UseGenerated && CustomPassword != ConfirmPassword)
        {
            Error = Loc.T("Las contraseñas no coinciden.");
            return;
        }

        IsBusy = true;
        try
        {
            var label = string.IsNullOrWhiteSpace(Label) ? Loc.T("Mi billetera") : Label.Trim();
            var wallets = Shell.Host.Wallets;
            var wallet = await Task.Run(() => wallets.Create(password, label));
            CreatedAddress = wallet.EncodedAddress;
            CreatedPseudonym = wallet.Pseudonym;
            Shell.Host.SetWallet(wallet);
            ClearSecrets();
            Step = 1;
        }
        catch (IOException ex)
        {
            Error = Loc.T("No se pudo guardar la billetera: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportBackup()
    {
        if (Shell.Host.Wallet is not { } wallet) return;
        var name = Path.GetFileName(wallet.FilePath);
        if (await Shell.Platform.SaveWalletFileAsync(name, wallet.File.ToJson()))
        {
            Shell.ShowToast(Loc.T("Respaldo guardado. Guardalo junto con tu contraseña en un lugar seguro."), ToastKind.Success);
        }
    }

    [RelayCommand]
    private void Finish() => Close();

    private void ClearSecrets()
    {
        GeneratedPassword = "";
        CustomPassword = "";
        ConfirmPassword = "";
    }

    public override void OnClosed() => ClearSecrets();
}

public sealed partial class ImportWalletDialogViewModel(MainViewModel shell) : DialogViewModel(shell)
{
    private string? _json;

    public override string Title => Loc.T("Importar billetera");

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _filePreview = "";
    [ObservableProperty] private string _password = "";

    public bool HasFile => _json is not null;

    partial void OnPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private async Task PickFile()
    {
        Error = null;
        var result = await Shell.Platform.OpenWalletFileAsync();
        if (result is not { } file) return;
        try
        {
            var parsed = WalletFile.FromJson(file.Content);
            if (parsed.Network != Shell.Host.Node.Network.Name)
            {
                Error = Loc.T("Este respaldo pertenece a la red «{0}» y el nodo está en «{1}».", parsed.Network, Shell.Host.Node.Network.Name);
                return;
            }
            _json = file.Content;
            FileName = file.Name;
            FilePreview = $"{parsed.Pseudonym} · {Shent.Core.Consensus.Address.Shorten(parsed.Address)}";
            OnPropertyChanged(nameof(HasFile));
        }
        catch (WalletFormatException ex)
        {
            Error = Loc.Message(ex.Message);
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (_json is null) { Error = Loc.T("Elegí primero el archivo .shent."); return; }
        if (string.IsNullOrEmpty(Password)) { Error = Loc.T("Ingresá la contraseña maestra del respaldo."); return; }
        IsBusy = true;
        Error = null;
        var json = _json;
        var password = Password;
        try
        {
            var wallets = Shell.Host.Wallets;
            var wallet = await Task.Run(() => wallets.Import(json, password));
            Password = "";
            Shell.Host.SetWallet(wallet);
            Shell.ShowToast(Loc.T("Billetera {0} importada y desbloqueada.", wallet.Pseudonym), ToastKind.Success);
            Close();
        }
        catch (WalletLockedOutException ex)
        {
            Error = Loc.T("Demasiados intentos fallidos. Esperá {0} s antes de volver a intentar.", Format.Integer((long)Math.Ceiling(ex.Wait.TotalSeconds)));
        }
        catch (Exception ex) when (ex is WalletPasswordException or WalletFormatException)
        {
            Error = Loc.Message(ex.Message);
        }
        catch (IOException ex)
        {
            Error = Loc.T("No se pudo guardar la copia local: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void OnClosed()
    {
        Password = "";
        _json = null;
    }
}

public sealed partial class ExportDialogViewModel(MainViewModel shell) : DialogViewModel(shell)
{
    public override string Title => Loc.T("Respaldo y seguridad");

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string? _success;

    public string FilePath => Shell.Host.Wallet?.FilePath ?? "";
    public string KdfText
    {
        get
        {
            var kdf = Shell.Host.Wallet?.File.Kdf;
            return kdf is null ? "" : Loc.T("Argon2id · {0} MiB · {1} pasadas · ChaCha20-Poly1305", Format.Integer(kdf.MemoryKiB / 1024), Format.Integer(kdf.Iterations));
        }
    }

    [RelayCommand]
    private async Task SaveCopy()
    {
        if (Shell.Host.Wallet is not { } wallet) return;
        if (await Shell.Platform.SaveWalletFileAsync(Path.GetFileName(wallet.FilePath), wallet.File.ToJson()))
        {
            Shell.ShowToast(Loc.T("Copia del respaldo guardada."), ToastKind.Success);
        }
    }

    [RelayCommand]
    private void OpenFolder() => Shell.Platform.OpenFolder(Shell.Host.Wallets.WalletsDirectory);

    [RelayCommand]
    private void GenerateNew()
    {
        NewPassword = ConfirmPassword = PasswordPolicy.GenerateMasterPassword();
    }

    [RelayCommand]
    private async Task CopyNew()
    {
        if (NewPassword.Length == 0) return;
        await Shell.Platform.CopySecretAsync(NewPassword);
        Shell.ShowToast(Loc.T("Contraseña copiada. El portapapeles se limpia en 60 segundos."));
    }

    [RelayCommand]
    private async Task ChangePassword()
    {
        Error = null;
        Success = null;
        if (Shell.Host.Wallet is not { } wallet) return;
        try
        {
            PasswordPolicy.EnsureAcceptable(NewPassword);
        }
        catch (ArgumentException)
        {
            Error = Loc.T("La contraseña maestra debe tener al menos {0} caracteres.", PasswordPolicy.MinLength);
            return;
        }
        if (NewPassword != ConfirmPassword) { Error = Loc.T("Las contraseñas nuevas no coinciden."); return; }

        IsBusy = true;
        var current = CurrentPassword;
        var next = NewPassword;
        var network = Shell.Host.Node.Network;
        var wallets = Shell.Host.Wallets;
        try
        {
            await Task.Run(() =>
            {
                var seed = wallet.File.Decrypt(current, network);
                Array.Clear(seed);
                wallets.ChangePassword(wallet, next);
            });
            CurrentPassword = NewPassword = ConfirmPassword = "";
            Success = Loc.T("Contraseña cambiada. Los respaldos anteriores siguen abriéndose con la contraseña vieja: reemplazalos por uno nuevo.");
        }
        catch (WalletPasswordException)
        {
            Error = Loc.T("La contraseña actual no es correcta.");
        }
        catch (IOException ex)
        {
            Error = Loc.T("No se pudo guardar: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void OnClosed()
    {
        CurrentPassword = NewPassword = ConfirmPassword = "";
    }
}
