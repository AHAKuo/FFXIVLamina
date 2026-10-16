using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Lamina.Decals.Mesh;

/// <summary> Little-endian forward reader over a byte array with bounds checks; shared by the .mdl and .mtrl readers. </summary>
internal sealed class BinaryCursor
{
    private readonly byte[] _data;

    public BinaryCursor(byte[] data)
        => _data = data;

    public int Position { get; set; }

    public int Length
        => _data.Length;

    public byte U8()
    {
        Ensure(1);
        return _data[Position++];
    }

    public ushort U16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public uint U32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public void Skip(int count)
    {
        Ensure(count);
        Position += count;
    }

    /// <summary> A view of <paramref name="count"/> bytes at an absolute offset. </summary>
    public ReadOnlySpan<byte> Slice(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > _data.Length)
            throw new InvalidDataException($"Data range 0x{offset:X}+{count} lies outside of the file ({_data.Length} bytes).");

        return _data.AsSpan((int)offset, count);
    }

    /// <summary> Null-terminated UTF-8 string at an absolute offset; empty when out of range. </summary>
    public string StringAt(long offset)
    {
        if (offset < 0 || offset >= _data.Length)
            return string.Empty;

        var span = _data.AsSpan((int)offset);
        var end  = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? span[..end] : span);
    }

    private void Ensure(int count)
    {
        if (count < 0 || Position + count > _data.Length)
            throw new InvalidDataException($"Unexpected end of data at 0x{Position:X} (needed {count} bytes of {_data.Length}).");
    }
}
