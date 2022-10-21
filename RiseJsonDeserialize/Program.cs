using System.Text;

if (args.Length != 2 || (args[0] != "-c" && args[0] != "-d"))
{
    Console.WriteLine($"Usage:\n\t{AppDomain.CurrentDomain.FriendlyName} <-c|-d> <file>");
    return;
}

if (args[0] == "-c")
    await CompressFile(args[1], $"compressed_{args[1]}");
else
    await DecompressFile(args[1], $"decompressed_{args[1]}");

async Task DecompressFile(string file, string saveAs)
{
    // (Type)           0:       [1] --------------------> 1. Read decoder type
    // (Meta Def)       1:       [121623, 24, 34] -------> 2. Read meta definition.
    //                                                     3. call _ConvertAndSetBytes(offset, originalSize, compressedSize):
    //                                                        seek to offset, adjust buffer size, create tmp stream with buffer
    //                                                     4. call IDecoder.Decode(fileStream, tmpStream, compressedSize, originalSize)
    // (Entries Data)   25:      []                        6. call Load("file"): get meta entry info
    //                                                        seek to offset, adjust buffer size, create tmp stream with buffer
    //                                                     7. call IDecoder.Decode(fileStream, tmpStream, compressedSize, originalSize)
    //                                                     8. Get decoded file data, LoadData finished.
    // (Meta Data)      121623:  [4, "file", 6666, 300] -> 5. Get decoded entry def, LoadMeta finished.
    //
    // Meta Data:
    // Decompress the Meta Data, 34 bytes to 24 bytes and Deserialize this 24 bytes, offset is 0
    // 4 bytes          (nameLen)
    // offset bytes     (padding)
    // nameLen bytes    (name) --------------------> entryName
    // 8 bytes          (originalSize) ------------> entryOriginalSize
    // offset bytes     (padding)
    // 8 bytes          (compressedSize) ----------> entryCompressedSize
    //                  25 ------------------------> entryOffset
    var fileArchive = new FileArchive(file, false);
    var bytes = fileArchive.Archive.Load("file");
    var json = Encoding.UTF8.GetString(bytes);
    await File.WriteAllTextAsync(saveAs, json);
}

async Task CompressFile(string fileName, string saveAs)
{
    // load file data, Len: file length
    // 0:           [0]
    // 1:           [25 + Len, 24, 24]
    // 25:          [file data]
    // 25 + Len:    [4, "file", Len, Len]
    var bytes = await File.ReadAllBytesAsync(fileName);
    await using var stream = File.OpenWrite(saveAs);
    var len = bytes.Length;
    stream.WriteByte(0);
    var lenBytes = BitConverter.GetBytes((ulong)len);
    await stream.WriteAsync(BitConverter.GetBytes((ulong)len + 25));
    await stream.WriteAsync(BitConverter.GetBytes(24L));
    await stream.WriteAsync(BitConverter.GetBytes(24L));
    await stream.WriteAsync(bytes);
    await stream.WriteAsync(BitConverter.GetBytes(4));
    await stream.WriteAsync(Encoding.UTF8.GetBytes("file"));
    await stream.WriteAsync(BitConverter.GetBytes((ulong)len));
    await stream.WriteAsync(BitConverter.GetBytes((ulong)len));
}

class FileArchive
{
    public readonly StreamArchive Archive;

    public FileArchive(string archivePath, bool isCachingInMemory)
    {
        var fileBytesStream = _OpenArchiveStream(archivePath, isCachingInMemory); // Load file into memory stream
        var streamArchive = new StreamArchive(fileBytesStream); // Initialize loader, load meta
        streamArchive.Loader.IsDisposingStream = true;
        Archive = streamArchive;
    }

    Stream _OpenArchiveStream(string archivePath, bool isCachingInMemory)
    {
        if (!isCachingInMemory)
            return File.OpenRead(archivePath);
        return new MemoryStream(File.ReadAllBytes(archivePath));
    }
}

class StreamArchive
{
    public readonly ArchiveStreamLoader Loader;
    private readonly ArchiveMeta _meta;

    public StreamArchive(Stream archiveStream)
    {
        Loader = new ArchiveStreamLoader(archiveStream);
        _meta = Loader.LoadMeta();
    }

    public byte[] Load(string filePath)
    {
        var meta = this._meta;
        return Loader.LoadBytes(meta._Table_k__BackingField[filePath]); // loader load file
    }
}

class ArchiveMetaEntry
{
    public string FileName;
    public long OriginalSize;
    public long CompressedSize;
    public long Offset;
}

class ArchiveMeta
{
    public readonly Dictionary<string, ArchiveMetaEntry> _Table_k__BackingField;

    public ArchiveMeta()
    {
        _Table_k__BackingField = new Dictionary<string, ArchiveMetaEntry>();
    }

    public static ArchiveMeta DeserializeFromArchive(byte[] data)
    {
        var archiveMeta = new ArchiveMeta();
        if (data.Length >= 1)
        {
            var v6 = 0;
            var v7 = 25;
            while (true)
            {
                var v8 = ArchiveMeta.DeserializeEntry(data, v6, out var bytesRead);
                v8.Offset = v7;
                archiveMeta._Table_k__BackingField.Add(v8.FileName, v8);
                v7 += (int)v8.CompressedSize;
                v6 += bytesRead;
                if (v6 >= data.Length)
                    return archiveMeta;
            }
        }

        return archiveMeta;
    }

    // total 24 bytes,
    // 4 bytes: nameLength
    // offset bytes: paddings
    // nameLength bytes: file name
    // offset bytes: paddings
    // 8 bytes: file original size
    // offset bytes: paddings
    // 8bytes: file compressed size
    private static ArchiveMetaEntry DeserializeEntry(byte[] data, int offset, out int bytesRead)
    {
        var v8 = BitConverter.ToInt32(data, offset);
        bytesRead = 4;
        var name = Encoding.UTF8.GetString(data, bytesRead + offset, v8);
        bytesRead += v8;
        var monitor = BitConverter.ToInt64(data, bytesRead + offset);
        bytesRead += 8;
        var klass = BitConverter.ToInt64(data, bytesRead + offset);
        bytesRead += 8;
        return new ArchiveMetaEntry
        {
            FileName = name,
            OriginalSize = monitor,
            CompressedSize = klass
        };
    }
}


class ArchiveStreamLoader : IDisposable
{
    private IDataCoder _coder;
    private Stream _archiveStream;
    private byte[] _buffer;
    public bool IsDisposingStream;
    private bool _disposedValue;

    public ArchiveStreamLoader(Stream archiveStream)
    {
        _archiveStream = archiveStream;
        _buffer = new byte[1024];
        _InitializeCoder();
    }

    private void _InitializeCoder()
    {
        var coderType = this._archiveStream.ReadByte();
        _coder = coderType switch
        {
            2 => new Lz4DataDecoder(4096),
            0 => new MemCopyDataCoder(),
            _ => throw new NotImplementedException()
        };
    }

    public ArchiveMeta LoadMeta()
    {
        var bytes = new byte[24];
        _archiveStream.Read(bytes, 0, 24);
        var offset = BitConverter.ToInt64(bytes, 0);
        var originalSize = BitConverter.ToInt64(bytes, 8);
        var compressedSize = BitConverter.ToInt64(bytes, 16);
        _ConvertAndSetBytes(offset, originalSize, compressedSize);
        return ArchiveMeta.DeserializeFromArchive(_GetFromByteBuffer(originalSize));
    }

    private byte[] _GetFromByteBuffer(long size)
    {
        var bytes = new byte[size];
        Array.Copy(_buffer, bytes, size);
        return bytes;
    }

    private void _ConvertAndSetBytes(long offset, long originalSize, long compressedSize)
    {
        _archiveStream.Seek(offset, SeekOrigin.Begin);
        if (_buffer.Length < originalSize)
        {
            var v11 = (double)originalSize * 1.5;
            var v12 = -v11;
            if (!double.IsInfinity(v11))
                v12 = (double)originalSize * 1.5;
            _buffer = new byte[(int)v12];
        }

        var newStream = new MemoryStream(_buffer, true);
        if (_coder == null)
            throw new NullReferenceException();
        _coder.Code(_archiveStream, newStream, compressedSize, originalSize);
        newStream.Dispose();
    }

    public void Dispose()
    {
        _archiveStream.Dispose();
    }

    public byte[] LoadBytes(ArchiveMetaEntry o)
    {
        _ConvertAndSetBytes(o.Offset, o.OriginalSize, o.CompressedSize);
        return _GetFromByteBuffer(o.OriginalSize);
    }
}

class MemCopyDataCoder : IDataCoder
{
    private byte[] _buffer;

    public MemCopyDataCoder()
    {
        _buffer = new byte[0x1000];
    }

    public void Code(Stream inStream, Stream outStream, long inSize, long outSize)
    {
        var v10 = _buffer;
        if (inSize >= 1)
        {
            var v6 = inSize;
            do
            {
                var v11 = (v6 <= v10.Length ? (v6) : (v10.Length));
                if (v10 == null || v6 <= v11 || inStream == null || outStream == null)
                    throw new Exception();
                inStream.Read(_buffer, 0, (int)v11);
                v6 -= v11;
                outStream.Write(_buffer, 0, (int)v11);
            } while (v6 > 0);
        }
    }
}

class Lz4DataDecoder : IDataCoder
{
    private byte[] _buffer;
    private byte[] _codedBuffer;
    private byte[] _intBuffer;

    public Lz4DataDecoder(int bufferSize)
    {
        _intBuffer = new byte[4];
        _buffer = new byte[bufferSize];
        var len = LZ4.LZ4Codec.MaximumOutputLength(bufferSize);
        _codedBuffer = new byte[len];
    }

    public void Code(Stream inStream, Stream outStream, long inSize, long outSize)
    {
        if (inStream == null || outStream == null || _codedBuffer == null)
            throw new NullReferenceException();
        inStream.Read(_intBuffer, 0, 4);
        var blockSize = BitConverter.ToInt32(_intBuffer, 0);
        if (blockSize > _buffer.Length)
            _buffer = new byte[blockSize];
        var maxLen = LZ4.LZ4Codec.MaximumOutputLength(blockSize);
        if (maxLen > _codedBuffer.Length)
            _codedBuffer = new byte[maxLen];
        if (outSize >= 1)
        {
            while (true)
            {
                inStream.Read(_intBuffer, 0, 4);
                var size = BitConverter.ToInt32(_intBuffer, 0);
                inStream.Read(_codedBuffer, 0, size);
                var v18 = outSize <= blockSize ? (int)outSize : blockSize;
                LZ4.LZ4Codec.Decode(this._codedBuffer, 0, size, _buffer, 0, v18, true);
                outSize -= v18;
                outStream.Write(_buffer, 0, v18);
                if (outSize <= 0)
                    return;
            }
        }
    }
}

public interface IDataCoder
{
    // void Code(Stream inStream, Stream outStream);
    void Code(Stream inStream, Stream outStream, long inSize, long outSize);
}

//
// static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
// {
//     PreserveReferencesHandling = PreserveReferencesHandling.All,
//     TypeNameHandling = TypeNameHandling.All,
//     FloatParseHandling = FloatParseHandling.Decimal
// };