using System.Security.Cryptography;
using Shent.Core.Consensus;

namespace Shent.Core.Wallet;

/// <summary>
/// Seudónimo público legible derivado de la dirección (por ejemplo "LoboAzul-7K29").
/// Es un apodo visual, no un identificador único: en una red P2P sin registro central dos direcciones
/// pueden coincidir en seudónimo, por eso la interfaz siempre lo muestra junto a la dirección abreviada.
/// No revela nada de la clave privada.
/// </summary>
public static class Pseudonym
{
    private static readonly string[] Animals =
    [
        "Lobo", "Zorro", "Puma", "Halcon", "Condor", "Yaguar", "Lince", "Oso", "Tigre", "Aguila",
        "Delfin", "Buho", "Cuervo", "Ciervo", "Coyote", "Tucan", "Carpincho", "Huemul", "Nandu", "Pinguino",
        "Orca", "Tiburon", "Pantera", "Leon", "Bisonte", "Gato", "Colibri", "Jaguar", "Fenix", "Dragon",
        "Gacela", "Ocelote",
    ];

    private static readonly string[] Colors =
    [
        "Azul", "Rojo", "Verde", "Dorado", "Plateado", "Negro", "Blanco", "Violeta", "Naranja", "Cobalto",
        "Esmeralda", "Rubi", "Ambar", "Indigo", "Turquesa", "Carmesi", "Celeste", "Grafito", "Coral", "Jade",
        "Bronce", "Lila", "Cian", "Magenta", "Ocre", "Perla", "Zafiro", "Onix", "Escarlata", "Marfil",
        "Neon", "Solar",
    ];

    private const string SuffixAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string FromAddress(Address address)
    {
        var digest = SHA256.HashData([.. "SHENT-PSEUDONYM"u8, .. address.Hash]);
        var animal = Animals[digest[0] % Animals.Length];
        var color = Colors[digest[1] % Colors.Length];
        Span<char> suffix = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            suffix[i] = SuffixAlphabet[digest[2 + i] % SuffixAlphabet.Length];
        }
        return $"{animal}{color}-{suffix}";
    }
}
