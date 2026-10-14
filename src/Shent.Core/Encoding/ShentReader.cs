using System.Buffers.Binary;
using Shent.Core.Crypto;

namespace Shent.Core.Encoding;

/// <summary>Lector binario con verificación estricta de límites: cualquier dato malformado lanza FormatException.</summary>
public sealed class ShentReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ShentReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public int Position => _position;
    public int Remaining => _data.Length - _position;
    public bool IsAtEnd => _position == _data.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new FormatException("Datos truncados.");
        }
        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }

    public byte ReadU8() => Take(1)[0];
    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long ReadI64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadVarInt()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = ReadU8();
            if (shift == 63 && b > 1)
            {
                throw new FormatException("VarInt fuera de rango.");
            }
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                if (b == 0 && shift > 0)
                {
                    throw new FormatException("VarInt no canónico.");
                }
                return result;
            }
        }
        throw new FormatException("VarInt demasiado largo.");
    }

    public long ReadVarLong(long max = long.MaxValue)
    {
        var value = ReadVarInt();
        if (value > (ulong)max)
        {
            throw new FormatException("Valor fuera de rango.");
        }
        return (long)value;
    }

    public int ReadCount(int max)
    {
        return (int)ReadVarLong(max);
    }

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public byte[] ReadVarBytes(int maxLength)
    {
        var len = ReadCount(maxLength);
        return Take(len).ToArray();
    }

    public string ReadString(int maxBytes)
    {
        return System.Text.Encoding.UTF8.GetString(ReadVarBytes(maxBytes));
    }

    public Hash256 ReadHash() => new(Take(Hash256.Size));

    public void EnsureEnd()
    {
        if (!IsAtEnd)
        {
            throw new FormatException("Datos sobrantes al final del mensaje.");
        }
    }
}
