namespace Shent.Core.Crypto;

/// <summary>
/// Codificación Bech32m según BIP-350. Detecta con garantía cualquier error de hasta 4 caracteres
/// y rechaza mayúsculas/minúsculas mezcladas.
/// </summary>
public static class Bech32m
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32mConst = 0x2bc830a3;
    private const int MaxLength = 90;
    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    private static readonly sbyte[] CharsetRev = BuildReverse();

    private static sbyte[] BuildReverse()
    {
        var rev = new sbyte[128];
        Array.Fill(rev, (sbyte)-1);
        for (var i = 0; i < Charset.Length; i++)
        {
            rev[Charset[i]] = (sbyte)i;
        }
        return rev;
    }

    private static uint Polymod(IEnumerable<byte> values)
    {
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    chk ^= Generator[i];
                }
            }
        }
        return chk;
    }

    private static byte[] ExpandHrp(string hrp)
    {
        var result = new byte[hrp.Length * 2 + 1];
        for (var i = 0; i < hrp.Length; i++)
        {
            result[i] = (byte)(hrp[i] >> 5);
            result[i + hrp.Length + 1] = (byte)(hrp[i] & 31);
        }
        return result;
    }

    public static string Encode(string hrp, ReadOnlySpan<byte> data5)
    {
        hrp = hrp.ToLowerInvariant();
        var values = ExpandHrp(hrp).Concat(data5.ToArray()).Concat(new byte[6]);
        var mod = Polymod(values) ^ Bech32mConst;
        var sb = new System.Text.StringBuilder(hrp.Length + 1 + data5.Length + 6);
        sb.Append(hrp).Append('1');
        foreach (var d in data5)
        {
            sb.Append(Charset[d]);
        }
        for (var i = 0; i < 6; i++)
        {
            sb.Append(Charset[(int)((mod >> (5 * (5 - i))) & 31)]);
        }
        return sb.ToString();
    }

    public static bool TryDecode(string input, out string hrp, out byte[] data5)
    {
        hrp = string.Empty;
        data5 = [];
        if (string.IsNullOrEmpty(input) || input.Length > MaxLength) return false;

        bool hasLower = false, hasUpper = false;
        foreach (var c in input)
        {
            if (c < 33 || c > 126) return false;
            if (c is >= 'a' and <= 'z') hasLower = true;
            if (c is >= 'A' and <= 'Z') hasUpper = true;
        }
        if (hasLower && hasUpper) return false;

        var lower = input.ToLowerInvariant();
        var pos = lower.LastIndexOf('1');
        if (pos < 1 || pos + 7 > lower.Length) return false;

        var values = new byte[lower.Length - pos - 1];
        for (var i = 0; i < values.Length; i++)
        {
            var c = lower[pos + 1 + i];
            if (c >= 128 || CharsetRev[c] < 0) return false;
            values[i] = (byte)CharsetRev[c];
        }

        var parsedHrp = lower[..pos];
        if (Polymod(ExpandHrp(parsedHrp).Concat(values)) != Bech32mConst) return false;

        hrp = parsedHrp;
        data5 = values[..^6];
        return true;
    }

    /// <summary>Reagrupa bits entre bases (por ejemplo, de 8 a 5 bits) como en BIP-173.</summary>
    public static byte[]? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << toBits) - 1;
        var result = new List<byte>(data.Length * fromBits / toBits + 1);
        foreach (var value in data)
        {
            if ((value >> fromBits) != 0) return null;
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad)
        {
            if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            return null;
        }
        return result.ToArray();
    }
}
