using System.Buffers.Binary;
using System.Text;
using System.IO;

namespace iPodManager;

internal sealed record DeploymentManifestRecord(TrackClassification Classification, ulong DurationSeconds, Guid SourceId,
    byte[] SourceSha256, byte[] BankSha256, ulong BankSize, uint ConversionProfile, uint TemplateProfile, string RuntimeId,
    string SourcePath, string BankName, string Title, string Artist, string Album, uint ControlId, ulong DbmSize, Guid EventGuid,
    byte[] DbmSha256, uint BuilderVersion, string DbmPath, string DbmRequestPath, string DescriptorEventPath, string BuilderIdentity);
internal sealed record DeploymentManifest(ulong Generation, byte[] StockEnabled, IReadOnlyList<DeploymentManifestRecord> Records);

internal static class DeploymentManifestSerializer
{
    // Shared with the ASI parser; field sizes and order require a coordinated ASI update.
    public const ushort Version = 1;
    public const int HeaderSize = 40, FixedSize = 196, NumericCapacity = 951;
    public const int MaxSourcePathBytes = 1024, MaxManifestBytes = 1024 * 1024;
    private static readonly byte[] Magic = "MGS4IPD\0"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Write(DeploymentManifest manifest)
    {
        Validate(manifest);
        var encoded = manifest.Records.Select(Encode).ToArray();
        int total = GetSerializedSize(encoded);
        int entries = total - HeaderSize - DeploymentContract.StockCount;
        using var stream = new MemoryStream(total);
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((ushort)HeaderSize);
        writer.Write((uint)total);
        writer.Write(manifest.Generation);
        writer.Write((uint)1);
        writer.Write((ushort)73);
        writer.Write((ushort)manifest.Records.Count);
        writer.Write((ushort)73);
        writer.Write((ushort)0);
        writer.Write((uint)entries);
        writer.Write(manifest.StockEnabled);
        for (int i = 0; i < manifest.Records.Count; i++)
            WriteRecord(writer, manifest.Records[i], encoded[i]);
        return stream.ToArray();
    }

    public static DeploymentManifest Read(byte[] data)
    {
        ValidateSize(data.Length);
        if (data.Length < HeaderSize + 73 || !data.AsSpan(0, 8).SequenceEqual(Magic) || U16(data, 8) != Version ||
            U16(data, 10) != 40 || U32(data, 12) != data.Length || U32(data, 24) != 1 ||
            U16(data, 28) != 73 || U16(data, 32) != 73 || U16(data, 34) != 0)
            throw new InvalidDataException("Invalid deployment manifest header.");
        int count = U16(data, 30);
        if (count > NumericCapacity || U32(data, 36) != data.Length - 113)
            throw new InvalidDataException("Invalid deployment manifest count.");
        byte[] stock = data.AsSpan(40, 73).ToArray();
        int position = 113;
        var records = new List<DeploymentManifestRecord>();
        for (int n = 0; n < count; n++)
        {
            int start = position;
            if (data.Length - start < FixedSize)
                throw new InvalidDataException("Truncated deployment manifest record.");
            uint rawLength = U32(data, start);
            if (rawLength < FixedSize || rawLength > data.Length - start)
                throw new InvalidDataException("Invalid deployment manifest record length.");
            int length = (int)rawLength;
            int end = start + length;
            byte classification = data[position + 4];
            ulong duration = U64(data, position + 8);
            Guid source = new(data.AsSpan(position + 16, 16), true);
            byte[] sourceHash = data.AsSpan(position + 32, 32).ToArray();
            byte[] bankHash = data.AsSpan(position + 64, 32).ToArray();
            ulong bankSize = U64(data, position + 96);
            uint conversionProfile = U32(data, position + 104);
            uint templateProfile = U32(data, position + 108);
            uint controlId = U32(data, position + 112);
            ulong dbmSize = U64(data, position + 116);
            Guid eventGuid = new(data.AsSpan(position + 124, 16), true);
            byte[] dbmHash = data.AsSpan(position + 140, 32).ToArray();
            uint builderVersion = U32(data, position + 172);
            ushort[] lengths = Enumerable.Range(0, 10).Select(i => U16(data, position + 176 + i * 2)).ToArray();
            position += FixedSize;
            var strings = new List<string>();
            foreach (ushort textLength in lengths)
            {
                if (textLength > end - position)
                    throw new InvalidDataException("Invalid deployment manifest text lengths.");
                try { strings.Add(Utf8.GetString(data, position, textLength)); }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException("Invalid deployment manifest UTF-8.", ex);
                }
                position += textLength;
            }
            string[] text = strings.ToArray();
            if (position != end)
                throw new InvalidDataException("Invalid deployment manifest text lengths.");
            records.Add(new((TrackClassification)classification, duration, source, sourceHash, bankHash, bankSize,
                conversionProfile, templateProfile, text[0], text[1], text[2], text[3], text[4], text[5],
                controlId, dbmSize, eventGuid, dbmHash, builderVersion, text[6], text[7], text[8], text[9]));
        }
        if (position != data.Length) throw new InvalidDataException("Trailing deployment manifest data.");
        var result = new DeploymentManifest(U64(data, 16), stock, records);
        Validate(result);
        return result;
    }

    public static void Validate(DeploymentManifest manifest)
    {
        if (manifest.Generation == 0 || manifest.StockEnabled.Length != 73 || manifest.StockEnabled.Any(x => x > 1) ||
            manifest.Records.Count > NumericCapacity)
            throw new InvalidDataException("Invalid deployment cardinality.");
        var stableIds = new HashSet<Guid>();
        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
        var controlIds = new HashSet<uint>();
        var requestPaths = new HashSet<string>(StringComparer.Ordinal);
        var legacyEventGuids = new HashSet<Guid>();
        foreach (var record in manifest.Records)
        {
            if (record.RuntimeId != ArtifactNaming.RuntimeId(record.SourceId))
                throw new InvalidDataException($"Invalid persisted runtime identity: {record.RuntimeId}");
            bool direct = record.ConversionProfile == DeploymentContract.DirectStreamProfile &&
                record.BankName == DeploymentContract.ProgrammerBankName &&
                record.BuilderIdentity == DeploymentContract.ProgrammerInstrumentName &&
                record.EventGuid == DeploymentContract.ProgrammerEventGuid;
            bool legacy = record.ConversionProfile == 1 &&
                record.BankName == ArtifactNaming.BankName(record.RuntimeId) &&
                record.BuilderIdentity == record.RuntimeId &&
                record.EventGuid == ArtifactPackager.DeriveEventGuid(record.BuilderIdentity) &&
                legacyEventGuids.Add(record.EventGuid);
            if (record.Classification is not (TrackClassification.Music or TrackClassification.Podcast) ||
                record.SourceId == Guid.Empty || !stableIds.Add(record.SourceId) || !IsValidRuntimeId(record.RuntimeId) ||
                !runtimeIds.Add(record.RuntimeId) ||
                !ControlIdAllocator.IsControl(record.ControlId) ||
                !controlIds.Add(record.ControlId) || record.DurationSeconds == 0 ||
                record.SourceSha256.Length != 32 || record.BankSha256.Length != 32 ||
                record.DbmSha256.Length != 32 || record.BankSize == 0 || record.DbmSize == 0 ||
                record.TemplateProfile != 1 ||
                record.BuilderVersion != ArtifactNaming.BuilderVersion || record.EventGuid == Guid.Empty ||
                !requestPaths.Add(record.DbmRequestPath) ||
                record.DbmPath != ArtifactNaming.DbmRequest(record.RuntimeId) ||
                record.DbmRequestPath != ArtifactNaming.DbmRequest(record.RuntimeId) ||
                record.DescriptorEventPath != ArtifactNaming.EventPath(record.RuntimeId) ||
                (!direct && !legacy))
                throw new InvalidDataException($"Invalid deployment manifest record: {record.RuntimeId}");
            _ = Encode(record);
        }
    }

    private static byte[][] Encode(DeploymentManifestRecord record)
    {
        string[] text = [record.RuntimeId, record.SourcePath, record.BankName, record.Title, record.Artist,
            record.Album, record.DbmPath, record.DbmRequestPath, record.DescriptorEventPath, record.BuilderIdentity];
        return EncodeText(text);
    }

    public static void ValidateSourcePath(string path)
    {
        int length = Utf8.GetByteCount(path);
        if (length > MaxSourcePathBytes)
            throw new InvalidDataException($"Source-relative path '{path}' has {length} UTF-8 bytes; the maximum is {MaxSourcePathBytes} bytes.");
    }

    // Fixed record fields do not change size during preparation; encode the same strings as Write.
    public static int GetDirectStreamSize(IReadOnlyList<DeploymentTrackIntent> intents)
    {
        return GetSerializedSize(intents.Select(intent =>
        {
            string runtime = ArtifactNaming.RuntimeId(intent.StableId);
            string dbm = ArtifactNaming.DbmRequest(runtime);
            return EncodeText([runtime, intent.RelativeSourcePath, DeploymentContract.ProgrammerBankName,
                intent.Title, intent.Artist, intent.Album, dbm, dbm, ArtifactNaming.EventPath(runtime),
                DeploymentContract.ProgrammerInstrumentName]);
        }));
    }

    private static int GetSerializedSize(IEnumerable<byte[][]> encoded)
    {
        int total = checked(HeaderSize + DeploymentContract.StockCount +
            encoded.Sum(x => checked(FixedSize + x.Sum(y => y.Length))));
        ValidateSize(total);
        return total;
    }

    private static void ValidateSize(int size)
    {
        if (size > MaxManifestBytes)
            throw new InvalidDataException($"Serialized deployment manifest has {size} bytes; the maximum is {MaxManifestBytes} bytes.");
    }

    private static byte[][] EncodeText(string[] text)
    {
        ValidateSourcePath(text[1]);
        byte[][] bytes = text.Select(Utf8.GetBytes).ToArray();
        if (bytes.Any(x => x.Length > ushort.MaxValue) ||
            bytes[3].Length is 0 or > ArtifactPackager.TitlePayloadByteLimit ||
            bytes[4].Length > ArtifactPackager.ArtistPayloadByteLimit ||
            bytes[5].Length > ArtifactPackager.AlbumPayloadByteLimit)
            throw new InvalidDataException("Manifest string exceeds bounds.");
        return bytes;
    }

    private static void WriteRecord(BinaryWriter writer, DeploymentManifestRecord record, byte[][] text)
    {
        writer.Write((uint)(FixedSize + text.Sum(x => x.Length)));
        writer.Write((byte)record.Classification);
        writer.Write((byte)(record.Classification == TrackClassification.Podcast ? 5 : 1));
        writer.Write((ushort)0);
        writer.Write(record.DurationSeconds);
        WriteGuid(writer, record.SourceId);
        writer.Write(record.SourceSha256);
        writer.Write(record.BankSha256);
        writer.Write(record.BankSize);
        writer.Write(record.ConversionProfile);
        writer.Write(record.TemplateProfile);
        writer.Write(record.ControlId);
        writer.Write(record.DbmSize);
        WriteGuid(writer, record.EventGuid);
        writer.Write(record.DbmSha256);
        writer.Write(record.BuilderVersion);
        foreach (var bytes in text) writer.Write((ushort)bytes.Length);
        foreach (var bytes in text) writer.Write(bytes);
    }

    private static void WriteGuid(BinaryWriter writer, Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, true, out _);
        writer.Write(bytes);
    }

    private static bool IsValidRuntimeId(string value) =>
        value.Length is > 0 and <= 31 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
}
