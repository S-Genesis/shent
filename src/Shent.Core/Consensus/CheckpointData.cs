using System.Text.Json;
using Shent.Core.Crypto;

namespace Shent.Core.Consensus;

/// <summary>
/// Puntos de control incluidos en el programa (checkpoints.json, embebido al compilar).
/// scripts/update-checkpoints.ps1 los actualiza en cada release a partir de un nodo sincronizado.
///
/// Protegen sobre todo a los nodos nuevos: aunque alguien les ofrezca una cadena alternativa con más
/// trabajo que reescriba la historia anterior al último punto de control, la rechazan. No reemplazan
/// la validación: cada bloque se sigue verificando completo. La contrapartida es que confían en que
/// quien armó el release tenía la cadena correcta, como pasaba con los checkpoints de Bitcoin.
/// </summary>
public static class CheckpointData
{
    private const string ResourceName = "Shent.Core.checkpoints.json";
    private static readonly Lazy<Dictionary<string, Dictionary<long, Hash256>>> All = new(Load);

    public static IReadOnlyDictionary<long, Hash256> For(string network) =>
        All.Value.TryGetValue(network, out var map) ? map : new Dictionary<long, Hash256>();

    private static Dictionary<string, Dictionary<long, Hash256>> Load()
    {
        using var stream = typeof(CheckpointData).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("Falta el recurso checkpoints.json.");
        return Parse(JsonDocument.Parse(stream));
    }

    public static Dictionary<string, Dictionary<long, Hash256>> Parse(JsonDocument document)
    {
        var result = new Dictionary<string, Dictionary<long, Hash256>>(StringComparer.Ordinal);
        foreach (var network in document.RootElement.EnumerateObject())
        {
            var map = new Dictionary<long, Hash256>();
            foreach (var entry in network.Value.EnumerateObject())
            {
                if (!long.TryParse(entry.Name, out var height) || height <= 0)
                    throw new FormatException($"Altura de punto de control inválida: {entry.Name}");
                if (!Hash256.TryParse(entry.Value.GetString(), out var hash))
                    throw new FormatException($"Hash de punto de control inválido en la altura {height}.");
                map[height] = hash;
            }
            result[network.Name] = map;
        }
        return result;
    }
}
