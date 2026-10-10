using System.Buffers.Binary;
using System.IO.Compression;
using SatisfactoryMapRenderer.Save.Properties;

namespace SatisfactoryMapRenderer.Save;

/// <summary>
/// Reads a Satisfactory .sav file. Supports the compressed formats from Update 5 (save version 21+)
/// up to 1.2 (save version 58): the legacy single-level body, the leveled body of U6/U7,
/// the 64-bit blobs of U8 and the per-level/per-object versioning of 1.x.
/// </summary>
public static class SaveGameReader
{
    private const uint PackageFileTag = 0x9E2A83C1;
    private const uint ArchiveHeaderV2 = 0x22222222;

    public static SaveGame Load(string path, Action<string>? log = null)
    {
        log ??= _ => { };
        byte[] file = File.ReadAllBytes(path);
        var headerReader = new BinaryDataReader(file);
        var header = SaveHeader.Read(headerReader);
        log($"Header: v{header.HeaderVersion}, save version {header.SaveVersion} ({header.GameVersionName}), build {header.BuildVersion}");

        if (header.SaveVersion < (int)SaveCustomVersion.SaveFileIsCompressed)
            throw new SaveFormatException($"Save version {header.SaveVersion} is too old (uncompressed early access format).");

        var (body, chunkCount) = Decompress(file, headerReader.Position);
        log($"Decompressed {chunkCount} chunks: {file.Length - headerReader.Position:N0} → {body.Length:N0} bytes");

        var save = new SaveGame { FilePath = path, Header = header, ChunkCount = chunkCount, UncompressedBodySize = body.Length };
        new BodyParser(save, body, log).Parse();
        return save;
    }

    /// <summary>Decompresses all zlib chunks in parallel into one contiguous buffer.</summary>
    private static (byte[] Body, int ChunkCount) Decompress(byte[] file, int offset)
    {
        var chunks = new List<(int Source, int CompressedSize, long Target, int UncompressedSize)>();
        long total = 0;
        int pos = offset;
        while (pos < file.Length)
        {
            var span = file.AsSpan(pos);
            if (span.Length < 49 || BinaryPrimitives.ReadUInt32LittleEndian(span) != PackageFileTag)
                throw new SaveFormatException($"Missing or truncated chunk header at offset {pos}.");
            bool v2 = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) == ArchiveHeaderV2;
            int headerSize = v2 ? 49 : 48;
            int summaryOffset = v2 ? 17 : 16;
            long compressed = BinaryPrimitives.ReadInt64LittleEndian(span[summaryOffset..]);
            long uncompressed = BinaryPrimitives.ReadInt64LittleEndian(span[(summaryOffset + 8)..]);
            if (v2 && span[16] != 3)
                throw new SaveFormatException($"Unsupported compression algorithm {span[16]} (only zlib is supported).");
            if (compressed <= 0 || uncompressed < 0 || uncompressed > int.MaxValue || compressed > span.Length - headerSize)
                throw new SaveFormatException($"Chunk at offset {pos} is truncated or corrupt (compressed size {compressed:N0}).");

            chunks.Add((pos + headerSize, (int)compressed, total, (int)uncompressed));
            total += uncompressed;
            pos += headerSize + (int)compressed;
        }

        if (total > Array.MaxLength)
            throw new SaveFormatException($"Save body of {total:N0} bytes is too large.");

        var body = new byte[total];
        try
        {
            Parallel.ForEach(chunks, chunk =>
            {
                using var input = new MemoryStream(file, chunk.Source, chunk.CompressedSize, writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                zlib.ReadExactly(body, (int)chunk.Target, chunk.UncompressedSize);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is InvalidDataException or EndOfStreamException))
        {
            throw new SaveFormatException($"A compressed chunk is corrupt: {ex.InnerExceptions[0].Message}", ex);
        }
        return (body, chunks.Count);
    }

    private sealed class BodyParser(SaveGame save, byte[] body, Action<string> log)
    {
        private readonly BinaryDataReader _r = new(body);
        private readonly int _saveVersion = save.Header.SaveVersion;
        private int _ue5Version = 1000;

        public void Parse()
        {
            long declared = _saveVersion >= (int)SaveCustomVersion.UnrealEngine5 ? _r.ReadInt64() : _r.ReadInt32();
            if (declared + _r.Position != body.Length)
                save.Warnings.Add($"Body size field says {declared:N0}, actual {body.Length - _r.Position:N0}.");

            if (_saveVersion >= (int)SaveCustomVersion.SerializeDataPackageVersionAndCustomVersions)
                _ue5Version = ReadObjectVersionData();

            if (_saveVersion >= (int)SaveCustomVersion.IntroducedWorldPartition)
                ReadGrids();

            if (_saveVersion < (int)SaveCustomVersion.AddedSublevelStreaming)
                ParseLegacyBody();
            else
                ParseLevels();

            log($"Parsed {save.Levels.Count} level(s), {save.AllObjects.Count():N0} objects, " +
                $"{save.PropertiesDecoded:N0} properties ({save.PropertiesFailed:N0} undecodable)");
        }

        /// <summary>FSaveObjectVersionData; returns the UE5 package file version.</summary>
        private int ReadObjectVersionData()
        {
            _r.ReadUInt32();              // version of this struct
            _r.ReadInt32();               // UE4 package version
            int ue5 = _r.ReadInt32();     // UE5 package version
            _r.ReadInt32();               // licensee version
            _r.ReadUInt16(); _r.ReadUInt16(); _r.ReadUInt16(); // engine major.minor.patch
            _r.ReadUInt32();              // changelist
            _r.ReadString();              // branch
            int customVersions = _r.ReadInt32();
            _r.Skip(customVersions * 20); // guid + int32 each
            return ue5;
        }

        private void ReadGrids()
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = _r.ReadString();
                int cellSize = _r.ReadInt32();
                uint hash = _r.ReadUInt32();
                int cellCount = _r.ReadInt32();
                var cells = new Dictionary<string, uint>(cellCount);
                for (int c = 0; c < cellCount; c++)
                    cells[_r.ReadString()] = _r.ReadUInt32();
                save.Grids.Add(new SaveGrid(name, cellSize, hash, cells));
            }
        }

        /// <summary>Update 5 and older: a single object list without levels.</summary>
        private void ParseLegacyBody()
        {
            var level = new SaveLevel { Name = save.Header.MapName, IsPersistent = true };
            ReadObjectHeaders(level, _saveVersion);

            int objectCount = _r.ReadInt32();
            if (objectCount != level.Objects.Count)
                throw new SaveFormatException($"Object count {objectCount} does not match header count {level.Objects.Count}.");
            var ctx = new ReadContext { SaveVersion = _saveVersion, UE5PackageVersion = _ue5Version };
            foreach (var obj in level.Objects)
            {
                int size = _r.ReadInt32();
                ReadObjectData(obj, size, ctx);
            }
            Accumulate(ctx);

            ReadReferenceList(level.Collectables);
            save.Levels.Add(level);
        }

        private void ParseLevels()
        {
            int sublevelCount = _r.ReadInt32();
            for (int i = 0; i <= sublevelCount; i++)
            {
                bool persistent = i == sublevelCount;
                string name = persistent ? save.Header.MapName : _r.ReadString();
                save.Levels.Add(ReadLevel(name, persistent));
            }

            if (_r.Remaining >= 4)
                ReadReferenceList(save.UnresolvedWorldSaveData);
            if (_r.Remaining > 0)
                save.Warnings.Add($"{_r.Remaining:N0} unread bytes at the end of the body.");
        }

        private int ReadBlobSize()
        {
            int size = _r.ReadInt32();
            if (_saveVersion >= (int)SaveCustomVersion.UnrealEngine5 && _r.ReadInt32() != 0)
                throw new SaveFormatException("Blob larger than 2 GB is not supported.");
            return size;
        }

        private SaveLevel ReadLevel(string name, bool persistent)
        {
            var level = new SaveLevel { Name = name, IsPersistent = persistent };

            int tocSize = ReadBlobSize();
            int tocStart = _r.Position;
            _r.Skip(tocSize);
            int dataSize = ReadBlobSize();
            int dataStart = _r.Position;
            _r.Skip(dataSize);

            int levelUE5 = _ue5Version;
            if (!persistent && _saveVersion >= (int)SaveCustomVersion.SerializePerStreamableLevelTOCVersion)
                level.LevelSaveVersion = _r.ReadInt32();

            if (persistent)
            {
                ReadPersistentLevelTrailer(level);
            }
            else
            {
                ReadReferenceList(level.Collectables);
                if (_saveVersion >= (int)SaveCustomVersion.SerializeDataPackageVersionAndCustomVersions && _r.ReadInt32() >= 1)
                    levelUE5 = ReadObjectVersionData();
            }
            int levelEnd = _r.Position;
            int levelVersion = level.LevelSaveVersion ?? _saveVersion;

            _r.Position = tocStart;
            ReadObjectHeaders(level, levelVersion);
            if (_r.Position < tocStart + tocSize)
            {
                // Newer saves duplicate the collectables / destroyed actors inside the TOC blob.
                if (persistent)
                    ReadDestroyedActors(new Dictionary<string, List<ObjectReference>>());
                else
                    ReadReferenceList([]);
            }
            if (_r.Position != tocStart + tocSize)
                save.Warnings.Add($"Level {name}: TOC blob size mismatch ({_r.Position - tocStart} vs {tocSize}).");

            _r.Position = dataStart;
            ReadLevelObjects(level, levelVersion, levelUE5);
            if (_r.Position != dataStart + dataSize)
                save.Warnings.Add($"Level {name}: data blob size mismatch ({_r.Position - dataStart} vs {dataSize}).");

            _r.Position = levelEnd;
            return level;
        }

        /// <summary>
        /// 1.0 stores a map of destroyed actors per level; older versions a plain collectables list.
        /// Both start with a count, so fall back to the other layout if the first one does not fit.
        /// </summary>
        private void ReadPersistentLevelTrailer(SaveLevel level)
        {
            int start = _r.Position;
            bool preferMap = _saveVersion >= (int)SaveCustomVersion.Version1;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool asMap = preferMap ^ (attempt == 1);
                try
                {
                    _r.Position = start;
                    level.Collectables.Clear();
                    level.DestroyedActors.Clear();
                    if (asMap)
                        ReadDestroyedActors(level.DestroyedActors);
                    else
                        ReadReferenceList(level.Collectables);

                    // The persistent level is last; only the unresolved list may follow.
                    int after = _r.Position;
                    var probe = new List<ObjectReference>();
                    if (_r.Remaining >= 4)
                        ReadReferenceList(probe);
                    bool fits = _r.Remaining == 0;
                    _r.Position = after;
                    if (fits || attempt == 1)
                        return;
                }
                catch (SaveFormatException) when (attempt == 0)
                {
                }
            }
        }

        private void ReadDestroyedActors(Dictionary<string, List<ObjectReference>> map)
        {
            int count = _r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string levelName = _r.ReadString();
                var list = new List<ObjectReference>();
                ReadReferenceList(list);
                map[levelName] = list;
            }
        }

        private void ReadReferenceList(List<ObjectReference> list)
        {
            int count = _r.ReadInt32();
            if (count < 0 || count > _r.Remaining)
                throw new SaveFormatException($"Invalid reference list length {count} at {_r.Position - 4}.");
            for (int i = 0; i < count; i++)
                list.Add(ObjectReference.Read(_r));
        }

        private void ReadObjectHeaders(SaveLevel level, int levelVersion)
        {
            int count = _r.ReadInt32();
            bool hasFlags = levelVersion >= (int)SaveCustomVersion.SerializeObjectFlags;
            level.Objects.Capacity = count;
            for (int i = 0; i < count; i++)
            {
                int type = _r.ReadInt32();
                string className = _r.ReadString();
                string root = _r.ReadString();
                string instance = _r.ReadString();
                uint flags = hasFlags ? _r.ReadUInt32() : 0;
                switch (type)
                {
                    case 1:
                        level.Objects.Add(new SaveEntity
                        {
                            ClassName = className,
                            RootObject = root,
                            InstanceName = instance,
                            ObjectFlags = flags,
                            NeedTransform = _r.ReadInt32() != 0,
                            Transform = Transform.ReadFloat(_r),
                            WasPlacedInLevel = _r.ReadInt32() != 0,
                        });
                        break;
                    case 0:
                        level.Objects.Add(new SaveComponent
                        {
                            ClassName = className,
                            RootObject = root,
                            InstanceName = instance,
                            ObjectFlags = flags,
                            ParentEntityName = _r.ReadString(),
                        });
                        break;
                    default:
                        throw new SaveFormatException($"Unknown object type {type} in level {level.Name} at {_r.Position}.");
                }
            }
        }

        private void ReadLevelObjects(SaveLevel level, int levelVersion, int levelUE5)
        {
            int count = _r.ReadInt32();
            if (count != level.Objects.Count)
                throw new SaveFormatException($"Level {level.Name}: {count} objects but {level.Objects.Count} headers.");

            var ctx = new ReadContext();
            foreach (var obj in level.Objects)
            {
                int objectVersion = levelVersion;
                if (levelVersion >= (int)SaveCustomVersion.IntroducedWorldPartition)
                {
                    objectVersion = _r.ReadInt32();
                    obj.ShouldMigrateObjectRefsToPersistent = _r.ReadInt32() != 0;
                }
                obj.ObjectSaveVersion = objectVersion;

                int size = _r.ReadInt32();
                int start = _r.Position;
                _r.Position = start + size;

                int objectUE5 = levelUE5;
                if (objectVersion >= (int)SaveCustomVersion.SerializeDataPackageVersionAndCustomVersions && _r.ReadInt32() == 1)
                    objectUE5 = ReadObjectVersionData();
                int after = _r.Position;

                ctx.SaveVersion = objectVersion;
                ctx.UE5PackageVersion = objectUE5;
                _r.Position = start;
                ReadObjectData(obj, size, ctx);
                _r.Position = after;
            }
            Accumulate(ctx);
        }

        private void Accumulate(ReadContext ctx)
        {
            save.PropertiesDecoded += ctx.PropertiesDecoded;
            save.PropertiesFailed += ctx.PropertiesFailed;
            foreach (var (key, count) in ctx.Underflows)
                save.PropertyUnderflows[key] = save.PropertyUnderflows.GetValueOrDefault(key) + count;
        }

        private void ReadObjectData(SaveObject obj, int size, ReadContext ctx)
        {
            int start = _r.Position;
            int end = start + size;
            if (size == 0)
                return;
            try
            {
                if (obj is SaveEntity entity)
                {
                    entity.Parent = ObjectReference.Read(_r);
                    int componentCount = _r.ReadInt32();
                    for (int i = 0; i < componentCount; i++)
                        entity.Components.Add(ObjectReference.Read(_r));
                }

                if (ctx.CompleteTypeNames)
                    _r.ReadByte(); // class serialization control

                obj.Properties = new PropertyReader(_r, ctx).ReadList();

                if (end - _r.Position >= 4 && _r.ReadInt32() != 0)
                    obj.ObjectGuid = _r.ReadGuid();

                int specialStart = _r.Position;
                try
                {
                    obj.SpecialData = SpecialDataReader.Read(_r, ctx, obj.ClassName, end - specialStart);
                    if (_r.Position > end)
                        throw new SaveFormatException("special data overran object");
                }
                catch (SaveFormatException ex)
                {
                    obj.SpecialData = null;
                    obj.ParseError = $"special data: {ex.Message}";
                    _r.Position = specialStart;
                }

                if (_r.Position < end)
                    obj.TrailingData = _r.ReadBytes(end - _r.Position);
                else if (_r.Position > end)
                    throw new SaveFormatException($"read {_r.Position - start} bytes of {size}");
            }
            catch (SaveFormatException ex)
            {
                obj.ParseError = ex.Message;
            }
            _r.Position = end;
        }
    }
}
