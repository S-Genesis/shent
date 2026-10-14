namespace Shent.Core.Consensus;

/// <summary>
/// Calendario de emisión. Recompensa inicial de 50 SHENT por bloque, que se reduce a la mitad cada
/// <see cref="NetworkParams.HalvingInterval"/> bloques. Con 1.000.000 de bloques por período la suma
/// converge a menos de 100.000.000 SHENT (el truncamiento entero garantiza que nunca se alcance el máximo).
/// </summary>
public static class Emission
{
    public static long SubsidyAt(NetworkParams network, long height)
    {
        if (height <= 0) return 0;
        var halvings = (height - 1) / network.HalvingInterval;
        if (halvings >= 63) return 0;
        return network.InitialSubsidy >> (int)halvings;
    }

    /// <summary>Suministro total emitido tras minar hasta la altura indicada (inclusive), calculado por períodos.</summary>
    public static long CumulativeSupply(NetworkParams network, long height)
    {
        if (height <= 0) return 0;
        long total = 0;
        long remaining = height;
        for (var halving = 0; halving < 63 && remaining > 0; halving++)
        {
            var blocks = Math.Min(remaining, network.HalvingInterval);
            total = checked(total + blocks * (network.InitialSubsidy >> halving));
            remaining -= blocks;
        }
        return total;
    }

    public static long NextHalvingHeight(NetworkParams network, long height)
    {
        var period = Math.Max(0, height - 1) / network.HalvingInterval;
        return (period + 1) * network.HalvingInterval + 1;
    }
}
