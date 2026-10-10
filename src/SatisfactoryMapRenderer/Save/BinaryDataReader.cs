using System.Buffers.Binary;
using System.Text;

namespace SatisfactoryMapRenderer.Save;

/// <summary>
/// Little-endian reader over an in-memory buffer with Unreal Engine FString support.
/// Strings are interned per reader because save files repeat the same paths many thousand times.
/// </summary>
public sealed class BinaryDataReader
{
    private readonly byte[] _buffer;
    private readonly Dictionary<string, string> _stringCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _stringLookup;

    public BinaryDataReader(byte[] buffer, int position = 0)
    {
        _buffer = buffer;
        Position = position;
        _stringLookup = _stringCache.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Position { get; set; }
    public int Length => _buffer.Length;
    public int Remaining => _buffer.Length - Position;
    public bool EndOfData => Position >= _buffer.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > _buffer.Length)
            throw new SaveFormatException($"Attempted to read {count} bytes at offset {Position}, but only {Remaining} remain.");
        var span = new ReadOnlySpan<byte>(_buffer, Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count) => Take(count);

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public Guid ReadGuid() => new(Take(16));

    /// <summary>Reads an FString: int32 length (negative = UTF-16), characters, null terminator.</summary>
    public string ReadString()
    {
        int length = ReadInt32();
        if (length == 0)
            return string.Empty;

        if (length > 0)
        {
            if (length > Remaining)
                throw new SaveFormatException($"Invalid string length {length} at offset {Position - 4}.");
            var bytes = Take(length)[..^1];
            return Intern(bytes, isUtf16: false);
        }

        if (length == int.MinValue || -length * 2L > Remaining)
            throw new SaveFormatException($"Invalid UTF-16 string length {length} at offset {Position - 4}.");
        var utf16 = Take(-length * 2)[..^2];
        return Intern(utf16, isUtf16: true);
    }

    private string Intern(ReadOnlySpan<byte> bytes, bool isUtf16)
    {
        // Long strings (descriptions, JSON blobs) are rarely repeated; do not cache them.
        if (bytes.Length > 256)
            return isUtf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);

        Span<char> chars = stackalloc char[bytes.Length];
        int count = isUtf16 ? Encoding.Unicode.GetChars(bytes, chars) : Encoding.UTF8.GetChars(bytes, chars);
        var span = chars[..count];
        if (_stringLookup.TryGetValue(span, out var cached))
            return cached;
        var str = new string(span);
        _stringCache[str] = str;
        return str;
    }
}

public sealed class SaveFormatException(string message, Exception? inner = null) : Exception(message, inner);
