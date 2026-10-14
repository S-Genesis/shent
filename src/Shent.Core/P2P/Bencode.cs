using System.Globalization;
using TextEncoding = System.Text.Encoding;

namespace Shent.Core.P2P;

/// <summary>
/// Codificación bencode (BEP 3) mínima para los mensajes KRPC de la DHT de BitTorrent.
/// Valores: byte[] (cadenas), long (enteros), List&lt;object&gt; (listas) y
/// SortedDictionary&lt;string, object&gt; (diccionarios con claves ordenadas por bytes).
/// </summary>
public static class Bencode
{
    private const int MaxDepth = 16;

    public static byte[] Encode(object value)
    {
        using var ms = new MemoryStream();
        Write(ms, value);
        return ms.ToArray();
    }

    private static void Write(MemoryStream ms, object value)
    {
        switch (value)
        {
            case byte[] bytes:
                WriteAscii(ms, bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
                ms.Write(bytes);
                break;
            case string text:
                Write(ms, TextEncoding.UTF8.GetBytes(text));
                break;
            case int or long:
                WriteAscii(ms, "i" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "e");
                break;
            case IEnumerable<KeyValuePair<string, object>> dict:
                ms.WriteByte((byte)'d');
                foreach (var (key, item) in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    Write(ms, TextEncoding.UTF8.GetBytes(key));
                    Write(ms, item);
                }
                ms.WriteByte((byte)'e');
                break;
            case IEnumerable<object> list:
                ms.WriteByte((byte)'l');
                foreach (var item in list) Write(ms, item);
                ms.WriteByte((byte)'e');
                break;
            default:
                throw new ArgumentException($"Tipo no soportado en bencode: {value.GetType().Name}");
        }
    }

    private static void WriteAscii(MemoryStream ms, string text) => ms.Write(TextEncoding.ASCII.GetBytes(text));

    /// <summary>Decodifica un mensaje completo. Devuelve false si está mal formado o tiene bytes sobrantes.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out object? value)
    {
        value = null;
        var pos = 0;
        try
        {
            value = Read(data, ref pos, 0);
            return pos == data.Length;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    private static object Read(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxDepth || pos >= data.Length) throw new FormatException();
        var c = data[pos];
        switch (c)
        {
            case (byte)'i':
            {
                pos++;
                var end = data[pos..].IndexOf((byte)'e');
                if (end <= 0 || end > 20) throw new FormatException();
                if (!long.TryParse(TextEncoding.ASCII.GetString(data.Slice(pos, end)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                {
                    throw new FormatException();
                }
                pos += end + 1;
                return n;
            }
            case (byte)'l':
            {
                pos++;
                var list = new List<object>();
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    if (list.Count >= 1024) throw new FormatException();
                    list.Add(Read(data, ref pos, depth + 1));
                }
                if (pos >= data.Length) throw new FormatException();
                pos++;
                return list;
            }
            case (byte)'d':
            {
                pos++;
                var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    if (dict.Count >= 256) throw new FormatException();
                    var key = TextEncoding.Latin1.GetString(ReadBytes(data, ref pos));
                    dict[key] = Read(data, ref pos, depth + 1);
                }
                if (pos >= data.Length) throw new FormatException();
                pos++;
                return dict;
            }
            case >= (byte)'0' and <= (byte)'9':
                return ReadBytes(data, ref pos);
            default:
                throw new FormatException();
        }
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int pos)
    {
        var colon = data[pos..].IndexOf((byte)':');
        if (colon <= 0 || colon > 7) throw new FormatException();
        if (!int.TryParse(TextEncoding.ASCII.GetString(data.Slice(pos, colon)), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            throw new FormatException();
        }
        pos += colon + 1;
        if (length > data.Length - pos) throw new FormatException();
        var bytes = data.Slice(pos, length).ToArray();
        pos += length;
        return bytes;
    }
}
