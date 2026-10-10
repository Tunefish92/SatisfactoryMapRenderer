using System.Buffers.Binary;

namespace SatisfactoryMapRenderer.Game;

/// <summary>
/// Satisfactory ships CommunityResources/FactoryGame.usmap (type mappings for its cooked assets).
/// Its writer omits the inner type of OptionalProperty entries, which CUE4Parse expects; the rest of
/// the file is then misread. This rewrites the file with a ByteProperty inner type for every optional.
/// None of the assets the renderer reads use optional properties. The fixed copy is only used when
/// CUE4Parse cannot read the original file.
/// </summary>
public static class UsmapFixer
{
    private const byte OptionalProperty = 28;

    public static byte[] Fix(byte[] usmap, out int fixedCount)
    {
        int p = 3; // uint16 magic + uint8 version
        byte version = usmap[2];
        if (BinaryPrimitives.ReadUInt16LittleEndian(usmap) != 0x30C4)
            throw new InvalidDataException("Not a usmap file.");
        if (version >= 1)
        {
            bool hasVersioning = BinaryPrimitives.ReadInt32LittleEndian(usmap.AsSpan(p)) != 0;
            p += 4;
            if (hasVersioning)
            {
                p += 8; // UE4 + UE5 package versions
                int customVersions = BinaryPrimitives.ReadInt32LittleEndian(usmap.AsSpan(p));
                p += 4 + customVersions * 20 + 4; // (guid + version) × n, net CL
            }
        }
        int headerEnd = p;
        if (usmap[p] != 0)
            throw new NotSupportedException("Compressed usmap files are not supported.");
        int size = BinaryPrimitives.ReadInt32LittleEndian(usmap.AsSpan(p + 1));
        byte[] payload = usmap.AsSpan(p + 9, size).ToArray();

        var output = new MemoryStream(size + 256);
        int q = 0;
        int count = 0;

        void Copy(int n)
        {
            output.Write(payload, q, n);
            q += n;
        }
        uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(q));
        ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(q));

        void Type()
        {
            byte type = payload[q];
            Copy(1);
            switch (type)
            {
                case 26: Type(); Copy(4); break;        // EnumProperty: underlying type + enum name
                case 9: Copy(4); break;                 // StructProperty: struct name
                case 8: case 25: Type(); break;        // Array, Set
                case 24: Type(); Type(); break;        // Map
                case OptionalProperty:
                    output.WriteByte(0);                // insert ByteProperty as inner type
                    count++;
                    break;
            }
        }

        int names = (int)U32();
        Copy(4);
        for (int i = 0; i < names; i++)
        {
            int length = version >= 2 ? U16() : payload[q];
            Copy((version >= 2 ? 2 : 1) + length);
        }

        int enums = (int)U32();
        Copy(4);
        for (int i = 0; i < enums; i++)
        {
            Copy(4);
            int entries = version >= 3 ? U16() : payload[q];
            Copy(version >= 3 ? 2 : 1);
            Copy(entries * (version >= 4 ? 12 : 4));
        }

        int structs = (int)U32();
        Copy(4);
        for (int i = 0; i < structs; i++)
        {
            Copy(4 + 4 + 2);            // name, super, property count
            int serializable = U16();
            Copy(2);
            for (int j = 0; j < serializable; j++)
            {
                Copy(2 + 1 + 4);        // schema index, array size, name
                Type();
            }
        }
        Copy(size - q);                 // extensions

        var result = new MemoryStream(headerEnd + 9 + (int)output.Length);
        result.Write(usmap, 0, headerEnd);
        result.WriteByte(0);
        Span<byte> sizes = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(sizes, (int)output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(sizes[4..], (int)output.Length);
        result.Write(sizes);
        output.Position = 0;
        output.CopyTo(result);
        fixedCount = count;
        return result.ToArray();
    }
}
