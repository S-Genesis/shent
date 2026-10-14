using System.Numerics;

namespace Shent.Core.Consensus;

/// <summary>Conversión entre el objetivo de dificultad (entero de 256 bits) y su forma compacta de 32 bits.</summary>
public static class Target
{
    public static readonly BigInteger TwoPow256 = BigInteger.One << 256;

    public static BigInteger FromCompact(uint compact)
    {
        var size = (int)(compact >> 24);
        var word = compact & 0x007fffffu;
        if ((compact & 0x00800000u) != 0)
        {
            throw new FormatException("Objetivo compacto negativo.");
        }
        if (size > 34)
        {
            throw new FormatException("Objetivo compacto desbordado.");
        }
        return size <= 3
            ? new BigInteger(word >> (8 * (3 - size)))
            : new BigInteger(word) << (8 * (size - 3));
    }

    public static uint ToCompact(BigInteger target)
    {
        if (target.Sign <= 0) return 0;
        var size = target.GetByteCount(isUnsigned: true);
        uint compact = size <= 3
            ? (uint)(target << (8 * (3 - size)))
            : (uint)(target >> (8 * (size - 3)));
        if ((compact & 0x00800000u) != 0)
        {
            compact >>= 8;
            size++;
        }
        return compact | ((uint)size << 24);
    }

    /// <summary>Trabajo esperado para encontrar un hash ≤ objetivo: 2^256 / (objetivo + 1).</summary>
    public static BigInteger WorkFor(BigInteger target) => TwoPow256 / (target + 1);

    public static BigInteger WorkForCompact(uint bits) => WorkFor(FromCompact(bits));

    /// <summary>Dificultad legible: trabajo esperado en hashes por bloque.</summary>
    public static double Difficulty(uint bits) => (double)WorkForCompact(bits);
}
