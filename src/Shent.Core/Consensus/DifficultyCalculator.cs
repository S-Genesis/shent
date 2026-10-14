using System.Numerics;

namespace Shent.Core.Consensus;

/// <summary>
/// Reajuste de dificultad LWMA (promedio móvil ponderado linealmente, de zawy12) bloque a bloque.
/// Responde rápido a cambios de potencia de minado, algo importante en redes pequeñas donde
/// un solo minero puede entrar o salir y multiplicar o dividir el hashrate total.
/// </summary>
public static class DifficultyCalculator
{
    public readonly record struct BlockTiming(long Timestamp, uint Bits);

    /// <summary>Bloques mínimos antes de empezar a reajustar (los primeros usan la dificultad inicial).</summary>
    public const int MinimumWindow = 3;

    /// <param name="window">Los últimos N+1 bloques, del más antiguo al más reciente (el último es el padre).</param>
    public static uint NextBits(NetworkParams network, long parentHeight, IReadOnlyList<BlockTiming> window)
    {
        if (network.NoRetargeting) return network.InitialBits;

        // Mientras la cadena es más corta que la ventana se promedian los bloques disponibles,
        // excluyendo el intervalo génesis→bloque 1 (el génesis tiene una fecha fija de lanzamiento).
        var n = (int)Math.Min(network.DifficultyWindow, parentHeight - 1);
        if (n < MinimumWindow || window.Count < n + 1)
        {
            return network.InitialBits;
        }

        long t = network.TargetSpacingSeconds;
        BigInteger sumTarget = BigInteger.Zero;
        long weightedSolveTime = 0;
        var start = window.Count - (n + 1);
        for (var i = 1; i <= n; i++)
        {
            var current = window[start + i];
            var previous = window[start + i - 1];
            var solveTime = Math.Clamp(current.Timestamp - previous.Timestamp, 1, 6 * t);
            weightedSolveTime += solveTime * i;
            sumTarget += Target.FromCompact(current.Bits);
        }

        long k = (long)n * (n + 1) * t / 2;
        weightedSolveTime = Math.Max(weightedSolveTime, k / 10);

        var next = sumTarget * weightedSolveTime / ((long)n * k);
        if (next > network.PowLimit) next = network.PowLimit;
        if (next.Sign <= 0) next = BigInteger.One;
        return Target.ToCompact(next);
    }
}
