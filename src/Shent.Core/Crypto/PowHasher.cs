using System.Numerics;
using NSec.Cryptography;

namespace Shent.Core.Crypto;

/// <summary>
/// Función de prueba de trabajo de SHENT: Argon2id(cabecera, sal fija de dominio).
/// Argon2id es "memory-hard": cada intento necesita varios MiB de RAM, lo que favorece a las CPU
/// de computadoras personales frente a ASIC diseñados para SHA-256.
/// </summary>
public sealed class PowHasher
{
    private static readonly byte[] DomainSalt = "SHENT-POW-V1-ARG"u8.ToArray();

    private readonly PasswordBasedKeyDerivationAlgorithm _algorithm;

    public int MemoryKiB { get; }
    public int Passes { get; }

    public PowHasher(int memoryKiB, int passes)
    {
        MemoryKiB = memoryKiB;
        Passes = passes;
        _algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            MemorySize = memoryKiB,
            NumberOfPasses = passes,
        });
    }

    public void Hash(ReadOnlySpan<byte> header, Span<byte> output32)
    {
        _algorithm.DeriveBytes(header, DomainSalt, output32);
    }

    public byte[] Hash(ReadOnlySpan<byte> header)
    {
        var output = new byte[32];
        Hash(header, output);
        return output;
    }

    /// <summary>El hash se interpreta como entero sin signo big-endian y debe ser ≤ objetivo.</summary>
    public static bool MeetsTarget(ReadOnlySpan<byte> powHash, BigInteger target)
    {
        var value = new BigInteger(powHash, isUnsigned: true, isBigEndian: true);
        return value <= target;
    }
}
