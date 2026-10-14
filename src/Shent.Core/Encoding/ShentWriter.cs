using System.Buffers.Binary;

namespace Shent.Core.Encoding;

/// <summary>Serializador binario canónico (little-endian, longitudes LEB128).</summary>
public sealed class ShentWriter
{
    private readonly MemoryStream _stream;

    public ShentWriter(int capacity = 256)
    {
        _stream = new MemoryStream(capacity);
    }

    public long Length => _stream.Length;

    public ShentWriter WriteU8(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public ShentWriter WriteU16(ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        _stream.Write(buf);
        return this;
    }

    public ShentWriter WriteU32(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        _stream.Write(buf);
        return this;
    }

    public ShentWriter WriteU64(ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _stream.Write(buf);
        return this;
    }

    public ShentWriter WriteI64(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        _stream.Write(buf);
        return this;
    }

    public ShentWriter WriteVarInt(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
        return this;
    }

    public ShentWriter WriteBytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        return this;
    }

    public ShentWriter WriteVarBytes(ReadOnlySpan<byte> data)
    {
        WriteVarInt((ulong)data.Length);
        _stream.Write(data);
        return this;
    }

    public ShentWriter WriteString(string value)
    {
        return WriteVarBytes(System.Text.Encoding.UTF8.GetBytes(value));
    }

    public byte[] ToArray() => _stream.ToArray();
}
