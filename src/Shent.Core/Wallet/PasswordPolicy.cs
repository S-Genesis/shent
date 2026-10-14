using System.Security.Cryptography;

namespace Shent.Core.Wallet;

public static class PasswordPolicy
{
    public const int MinLength = 12;
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>Genera una clave maestra aleatoria de ~124 bits: cinco grupos de cinco caracteres sin ambigüedades.</summary>
    public static string GenerateMasterPassword()
    {
        var groups = new string[5];
        Span<char> chars = stackalloc char[5];
        for (var g = 0; g < groups.Length; g++)
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
            groups[g] = new string(chars);
        }
        return string.Join('-', groups);
    }

    public static void EnsureAcceptable(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            throw new ArgumentException($"La contraseña maestra debe tener al menos {MinLength} caracteres.");
        }
    }

    /// <summary>Estimación orientativa (0–4) para la interfaz; no reemplaza una contraseña aleatoria.</summary>
    public static int Strength(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
        var score = password.Length switch
        {
            < MinLength => 0,
            < 16 => 1,
            < 20 => 2,
            _ => 3,
        };
        if (classes >= 3 && score > 0) score++;
        return Math.Min(score, 4);
    }
}
