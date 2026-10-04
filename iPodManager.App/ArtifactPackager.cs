using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace iPodManager;

internal sealed class DbmMetadataException : Exception
{
    public string Field { get; }
    public bool ContainsNul { get; }

    public DbmMetadataException(string field)
        : base($"DBM {field} contains an embedded NUL.")
    {
        Field = field;
        ContainsNul = true;
    }
}

internal static class ArtifactNaming
{
    public const uint BuilderVersion = 2;
    public static string RuntimeId(Guid stableId) => "M4IPOD_" + stableId.ToString("N")[..20].ToUpperInvariant();
    public static string DbmName(string runtimeId) => runtimeId + ".dbm";
    public static string BankName(string runtimeId) => runtimeId + ".bank";
    public static string DbmRequest(string runtimeId) => "common/dbm/" + DbmName(runtimeId);
    public static string EventPath(string runtimeId) => $"/BGM/{runtimeId}/{runtimeId}";
    public static bool IsGeneratedRuntimeId(string value) =>
        value.Length == 27 && value.StartsWith("M4IPOD_", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789ABCDEF") < 0;
}

internal static class ArtifactPackager
{
    // Each DBM row stores NUL-terminated UTF-8 text in these fixed-size slots.
    internal const int TitlePayloadByteLimit = 0x80 - 1;
    internal const int ArtistPayloadByteLimit = 0x40 - 1;
    internal const int AlbumPayloadByteLimit = 0x40 - 1;

    internal sealed record DbmMetadata(string Title, string Artist, string Album);

    public static byte[] BuildDbm(string templatePath, string title, string artist, string album, ulong duration, uint controlId)
    {
        DbmMetadata metadata = PrepareDbmMetadata(title, artist, album);
        byte[] dbm = File.ReadAllBytes(templatePath);
        if (dbm.Length <= 0x830 || !dbm.AsSpan(0, 4).SequenceEqual("DLBM"u8) || dbm.Length % 0x800 != 0)
            throw new InvalidDataException("PC DBM donor is incompatible.");
        if (duration is 0 or > uint.MaxValue)
            throw new InvalidDataException("DBM duration is out of range.");
        // The donor has two catalog rows; both must describe the generated track.
        SetText(dbm, 0x40, 0x80, metadata.Title);
        SetText(dbm, 0xc0, 0x40, metadata.Artist);
        SetText(dbm, 0x100, 0x40, metadata.Album);
        SetText(dbm, 0x140, 0x80, metadata.Title);
        SetText(dbm, 0x1c0, 0x40, metadata.Artist);
        SetText(dbm, 0x200, 0x40, metadata.Album);
        BinaryPrimitives.WriteUInt32BigEndian(dbm.AsSpan(8, 4), (uint)duration);
        // The PC DBM stores its selectable iPod control ID in the second 0x800-byte block.
        BinaryPrimitives.WriteUInt32LittleEndian(dbm.AsSpan(0x82c, 4), controlId);
        ValidateDbm(dbm, controlId, duration);
        return dbm;
    }

    internal static DbmMetadata PrepareDbmMetadata(string title, string artist, string album)
    {
        // The donor's paired catalog rows use NUL-terminated UTF-8 slots.
        return new(
            PrepareText("title", title, TitlePayloadByteLimit),
            PrepareText("artist", artist, ArtistPayloadByteLimit),
            PrepareText("album", album, AlbumPayloadByteLimit));
    }

    private static string PrepareText(string field, string value, int byteLimit)
    {
        if (value.Contains('\0'))
            throw new DbmMetadataException(field);
        if (Encoding.UTF8.GetByteCount(value) <= byteLimit)
            return value;

        // Reserve three payload bytes for the ASCII ellipsis.
        int length = 0, bytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > byteLimit - 3) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return value[..length].TrimEnd() + "...";
    }

    public static void ValidateDbm(byte[] dbm, uint controlId, ulong duration)
    {
        if (dbm.Length <= 0x830 || !dbm.AsSpan(0, 4).SequenceEqual("DLBM"u8) || dbm.Length % 0x800 != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(dbm.AsSpan(0x82c, 4)) != controlId ||
            BinaryPrimitives.ReadUInt32BigEndian(dbm.AsSpan(8, 4)) != duration)
            throw new InvalidDataException("Generated DBM validation failed.");
    }

    public static Guid DeriveEventGuid(string identity) => new(Derive(identity, "event"));
    private static void SetText(byte[] dbm, int offset, int capacity, string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value);
        Array.Clear(dbm, offset, capacity);
        text.CopyTo(dbm, offset);
    }

    private static byte[] Derive(string identity, string role)
    {
        // This namespace fixes persisted event GUIDs and BANK references across rebuilds.
        ulong state = Fnv("MGS4-iPod-BANK-v2:" + identity + ":" + role);
        byte[] guid = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(guid.AsSpan(0, 8), Mix(ref state));
        BinaryPrimitives.WriteUInt64LittleEndian(guid.AsSpan(8, 8), Mix(ref state));
        guid[7] = (byte)((guid[7] & 15) | 64);
        guid[8] = (byte)((guid[8] & 63) | 128);
        return guid;
    }

    private static ulong Fnv(string value)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }

    private static ulong Mix(ref ulong state)
    {
        ulong value = state += 0x9e3779b97f4a7c15;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9;
        value = (value ^ (value >> 27)) * 0x94d049bb133111eb;
        return value ^ (value >> 31);
    }
}
