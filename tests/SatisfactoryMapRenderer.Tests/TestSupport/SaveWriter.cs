using System.Text;

namespace SatisfactoryMapRenderer.Tests.TestSupport;

/// <summary>Little-endian writer producing the primitive encodings used in .sav files.</summary>
public sealed class SaveWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _w;

    public SaveWriter() => _w = new BinaryWriter(_stream);

    public int Position => (int)_stream.Position;

    public SaveWriter Byte(byte v) { _w.Write(v); return this; }
    public SaveWriter SByte(sbyte v) { _w.Write(v); return this; }
    public SaveWriter I32(int v) { _w.Write(v); return this; }
    public SaveWriter U32(uint v) { _w.Write(v); return this; }
    public SaveWriter I64(long v) { _w.Write(v); return this; }
    public SaveWriter U16(ushort v) { _w.Write(v); return this; }
    public SaveWriter F32(float v) { _w.Write(v); return this; }
    public SaveWriter F64(double v) { _w.Write(v); return this; }
    public SaveWriter Bytes(ReadOnlySpan<byte> v) { _w.Write(v); return this; }
    public SaveWriter Zeros(int count) { _w.Write(new byte[count]); return this; }

    /// <summary>FString: ASCII when possible, otherwise UTF-16 with a negative length.</summary>
    public SaveWriter Str(string s)
    {
        if (s.Length == 0)
            return I32(0);
        if (s.All(c => c < 128))
        {
            I32(s.Length + 1);
            _w.Write(Encoding.ASCII.GetBytes(s));
            return Byte(0);
        }
        I32(-(s.Length + 1));
        _w.Write(Encoding.Unicode.GetBytes(s));
        return Bytes([0, 0]);
    }

    public SaveWriter Ref(string path, string level = "Persistent_Level") => Str(path.Length == 0 ? "" : level).Str(path);

    public SaveWriter Vector(double x, double y, double z, bool doubles)
    {
        if (doubles)
            return F64(x).F64(y).F64(z);
        return F32((float)x).F32((float)y).F32((float)z);
    }

    public void PatchI32(int position, int value)
    {
        long current = _stream.Position;
        _stream.Position = position;
        _w.Write(value);
        _stream.Position = current;
    }

    public void PatchI64(int position, long value)
    {
        long current = _stream.Position;
        _stream.Position = position;
        _w.Write(value);
        _stream.Position = current;
    }

    public byte[] ToArray()
    {
        _w.Flush();
        return _stream.ToArray();
    }

    public static byte[] Build(Action<SaveWriter> write)
    {
        var w = new SaveWriter();
        write(w);
        return w.ToArray();
    }
}
