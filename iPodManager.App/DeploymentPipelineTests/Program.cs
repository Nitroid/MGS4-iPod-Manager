using System.Security.Cryptography;
using iPodManager;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

static DeploymentManifestRecord Record(Guid id, uint control, string runtime, string source, ulong duration)
{
    byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source));
    byte[] dbmHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source + "dbm"));
    return new(
        source.Contains("podcast", StringComparison.OrdinalIgnoreCase) ? TrackClassification.Podcast : TrackClassification.Music,
        duration, id, hash, hash, 1444, DeploymentContract.DirectStreamProfile, 1, runtime, source,
        DeploymentContract.ProgrammerBankName, runtime, "Artist", "Album", control, 256,
        DeploymentContract.ProgrammerEventGuid, dbmHash, ArtifactNaming.BuilderVersion,
        ArtifactNaming.DbmRequest(runtime), ArtifactNaming.DbmRequest(runtime), ArtifactNaming.EventPath(runtime),
        DeploymentContract.ProgrammerInstrumentName);
}

var a = Record(Guid.Parse("10000000-0000-0000-0000-000000000001"), 9000,
    ArtifactNaming.RuntimeId(Guid.Parse("10000000-0000-0000-0000-000000000001")), "custom/a.flac", 278);
var b = Record(Guid.Parse("20000000-0000-0000-0000-000000000002"), 9001,
    ArtifactNaming.RuntimeId(Guid.Parse("20000000-0000-0000-0000-000000000002")), "podcasts/long.flac", 6561);
var manifest = new DeploymentManifest(1, Enumerable.Repeat((byte)1, 73).ToArray(), [a, b]);
byte[] bytes = DeploymentManifestSerializer.Write(manifest);
DeploymentManifest roundTrip = DeploymentManifestSerializer.Read(bytes);
Check(roundTrip.Records.Count == 2, "direct-stream manifest round trip");
Check(roundTrip.Records.All(x => x.BankName == DeploymentContract.ProgrammerBankName), "records share one programmer BANK");
Check(roundTrip.Records.All(x => x.EventGuid == DeploymentContract.ProgrammerEventGuid), "records share programmer event GUID");
Check(roundTrip.Records[0].SourcePath == "custom/a.flac" && roundTrip.Records[1].SourcePath == "podcasts/long.flac",
    "records retain their own source paths");
Check(roundTrip.Records[0].DurationSeconds == 278 && roundTrip.Records[1].DurationSeconds == 6561,
    "records retain their own durations");
Check(roundTrip.Records[1].Classification == TrackClassification.Podcast, "podcast classification retained");
Check(DeploymentContract.MaxNonDefaultTracks == 951, "production non-default track limit is 951");
Check(DeploymentContract.StockCount + DeploymentContract.MaxNonDefaultTracks == DeploymentContract.NativeCatalogCapacity,
    "73 stock and 951 non-default tracks fit the proven 1024-record runtime catalog");
Check(DeploymentContract.SupportsNonDefaultTrackCount(951), "exactly 951 non-default tracks are accepted");
Check(!DeploymentContract.SupportsNonDefaultTrackCount(952), "952 non-default tracks are rejected");
var capacityCategories = Enumerable.Repeat(LibraryCategory.Default, 73)
    .Concat(Enumerable.Repeat(LibraryCategory.Custom, 476))
    .Concat(Enumerable.Repeat(LibraryCategory.Podcast, 475))
    .ToArray();
int nonDefaultCount = capacityCategories.Count(category =>
    category is LibraryCategory.Custom or LibraryCategory.Podcast);
Check(nonDefaultCount == 951 && DeploymentContract.SupportsNonDefaultTrackCount(nonDefaultCount),
    "Custom and Podcast tracks count together while 73 Default tracks do not consume capacity");
var allocated = ControlIdAllocator.Allocate(Enumerable.Range(0, 951)
    .Select(index => (new Guid(index + 1, 0, 0, new byte[8]), (uint?)null)));
Check(allocated.Count == 951 && allocated.Values.Distinct().Count() == 951,
    "control-ID allocation supports all 951 non-default tracks");

SourceHashCacheTests.Run();
TrackDisplayOrderTests.Run();
var vorbisAnnotation = new TagLib.Ogg.XiphComment();
Check(SourceAnnotation.Resolve("Comment", vorbisAnnotation) == "Comment",
    "source annotation uses COMMENT without DESCRIPTION");
vorbisAnnotation.SetField("DESCRIPTION", ["Description\nUnicode 注釈"]);
Check(SourceAnnotation.Resolve(null, vorbisAnnotation) == "Description\nUnicode 注釈",
    "source annotation reads the raw Vorbis DESCRIPTION field");
Check(SourceAnnotation.Resolve("Comment", vorbisAnnotation) == "Comment",
    "source annotation prefers COMMENT when both fields exist");
Check(SourceAnnotation.Resolve("", vorbisAnnotation) == "Description\nUnicode 注釈" &&
      SourceAnnotation.Resolve(" \t\n", vorbisAnnotation) == "Description\nUnicode 注釈",
    "blank COMMENT falls back to DESCRIPTION");
Check(SourceAnnotation.Resolve(null, new TagLib.Ogg.XiphComment()) == string.Empty &&
      SourceAnnotation.Resolve(null, null) == string.Empty,
    "missing annotations preserve the empty value used for UI N/A");
DeploymentArtifactHashCacheTests.Run();
StartupProgressTests.Run();
ApplicationPreferencesTests.Run();
GamePathsTests.Run();
SingleInstanceGuardTests.Run();

Check(!LibraryFileFilter.IsAppleDouble("song.flac"), "ordinary audio filename is retained");
Check(LibraryFileFilter.IsAppleDouble("._song.flac") &&
    LibraryFileFilter.IsAppleDouble(Path.Combine("folder", "._song.mp3")),
    "AppleDouble audio sidecars are ignored before parsing");
Check(!LibraryFileFilter.IsAppleDouble("song._alternate.flac"),
    "AppleDouble rule does not reject names containing ._ after the beginning");
string[] supportedAudioExtensions = [".aac", ".flac", ".m4a", ".mp3", ".ogg", ".wav", ".wma"];
Check(supportedAudioExtensions.All(extension =>
        LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "custom", "track" + extension)) &&
        LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "podcasts", "track" + extension.ToUpperInvariant()))),
    "supported source extensions are discovered consistently for Custom and Podcast tracks");
Check(!LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "custom", "track.at3")) &&
      !LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "podcasts", "track.AT3")),
    "ATRAC3 source files are excluded from Custom and Podcast discovery");
Check(!LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "custom", "track.oma")) &&
      !LibraryFileFilter.IsSupportedAudio(Path.Combine("content", "podcasts", "track.OMA")),
    "OpenMG ATRAC source files are excluded from Custom and Podcast discovery");

static void CheckPrepared(string field, string original, string prepared, int limit, bool truncated)
{
    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(prepared);
    Check(bytes.Length <= limit && new System.Text.UTF8Encoding(false, true).GetString(bytes) == prepared,
        $"{field} remains valid UTF-8 within its independent DBM limit");
    Check(truncated == prepared.EndsWith("...", StringComparison.Ordinal),
        $"{field} uses three ASCII periods only when truncation is required");
}

var exact = ArtifactPackager.PrepareDbmMetadata(
    new string('T', ArtifactPackager.TitlePayloadByteLimit),
    new string('A', ArtifactPackager.ArtistPayloadByteLimit),
    new string('L', ArtifactPackager.AlbumPayloadByteLimit));
Check(exact.Title.Length == ArtifactPackager.TitlePayloadByteLimit &&
    exact.Artist.Length == ArtifactPackager.ArtistPayloadByteLimit &&
    exact.Album.Length == ArtifactPackager.AlbumPayloadByteLimit,
    "metadata exactly at each DBM payload limit remains unchanged");

string sourceTitle = new string('T', ArtifactPackager.TitlePayloadByteLimit + 1);
string sourceArtist = new string('é', 40);
string sourceAlbum = new string('L', 59) + "     suffix";
var prepared = ArtifactPackager.PrepareDbmMetadata(sourceTitle, sourceArtist, sourceAlbum);
CheckPrepared("title", sourceTitle, prepared.Title, ArtifactPackager.TitlePayloadByteLimit, true);
CheckPrepared("artist", sourceArtist, prepared.Artist, ArtifactPackager.ArtistPayloadByteLimit, true);
CheckPrepared("album", sourceAlbum, prepared.Album, ArtifactPackager.AlbumPayloadByteLimit, true);
Check(!prepared.Album.EndsWith(" ...", StringComparison.Ordinal) &&
    !prepared.Album.EndsWith("  ...", StringComparison.Ordinal),
    "truncation removes trailing whitespace before the ellipsis");
Check(sourceTitle.Length == ArtifactPackager.TitlePayloadByteLimit + 1 &&
    sourceArtist == new string('é', 40) && sourceAlbum.EndsWith("suffix", StringComparison.Ordinal),
    "preparing DBM metadata does not mutate original values");

string donorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "tools", "ipod-dbm-template.dbm"));
byte[] generatedDbm = ArtifactPackager.BuildDbm(
    donorPath, sourceTitle, sourceArtist, sourceAlbum, 60, 9000);
Check(generatedDbm.AsSpan(0x40, System.Text.Encoding.UTF8.GetByteCount(prepared.Title))
        .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(prepared.Title)) &&
    generatedDbm.AsSpan(0xc0, System.Text.Encoding.UTF8.GetByteCount(prepared.Artist))
        .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(prepared.Artist)) &&
    generatedDbm.AsSpan(0x100, System.Text.Encoding.UTF8.GetByteCount(prepared.Album))
        .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(prepared.Album)),
    "generated DBM contains the prepared title, artist, and album values");

try
{
    _ = ArtifactPackager.PrepareDbmMetadata("valid", "bad\0artist", "valid");
    throw new Exception("FAIL embedded NUL rejection");
}
catch (DbmMetadataException ex)
{
    Check(ex.Field == "artist" && ex.ContainsNul, "unrepresentable embedded NUL remains rejected");
}

static void WritePcmWave(string path)
{
    const int sampleRate = 8000;
    const int dataLength = sampleRate * 2;
    byte[] wave = new byte[44 + dataLength];
    "RIFF"u8.CopyTo(wave);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
    "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), sampleRate);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), sampleRate * 2);
    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
    "data"u8.CopyTo(wave.AsSpan(36));
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), dataLength);
    File.WriteAllBytes(path, wave);
}

string integrationRoot = Path.Combine(Path.GetTempPath(), "mgs4-ipod-metadata-" + Guid.NewGuid().ToString("N"));
try
{
    string gameRoot = Path.Combine(integrationRoot, "MGS4");
    string ipodRoot = Path.Combine(gameRoot, "iPod");
    Directory.CreateDirectory(Path.Combine(gameRoot, "common", "bank", "default"));
    Directory.CreateDirectory(Path.Combine(gameRoot, "common", "dbm"));
    Directory.CreateDirectory(Path.Combine(gameRoot, "scripts"));
    File.WriteAllBytes(Path.Combine(gameRoot, "mgs4.exe"), []);
    Directory.CreateDirectory(Path.Combine(ipodRoot, "content", "custom"));
    Directory.CreateDirectory(Path.Combine(ipodRoot, "tools"));
    File.Copy(Path.Combine(AppContext.BaseDirectory, "tools", "ipod-dbm-template.dbm"),
        Path.Combine(ipodRoot, "tools", "ipod-dbm-template.dbm"));

    string source = Path.Combine(ipodRoot, "content", "custom", "long-metadata.wav");
    WritePcmWave(source);
    byte[] sourceBefore = File.ReadAllBytes(source);
    byte[] hashBefore = SHA256.HashData(sourceBefore);
    DateTime writeTimeBefore = File.GetLastWriteTimeUtc(source);
    var integrationIntent = new DeploymentTrackIntent(Guid.NewGuid(), LibraryCategory.Custom,
        source, "custom/long-metadata.wav", sourceTitle, sourceArtist, sourceAlbum);
    GamePaths integrationPaths = GamePaths.Resolve(ipodRoot);
    await ManifestLimitTests.Run(integrationPaths, a);
    DeploymentResult integrationResult = await new DeploymentService(integrationPaths)
        .BuildAndDeployAsync([integrationIntent], Enumerable.Repeat((byte)1, 73).ToArray(), 75, dryRun: true);
    DeploymentManifestRecord integrationRecord = DeploymentManifestSerializer
        .Read(File.ReadAllBytes(integrationResult.ManifestPath)).Records.Single();
    Check(integrationRecord.Title == prepared.Title && integrationRecord.Artist == prepared.Artist &&
        integrationRecord.Album == prepared.Album,
        "controlled deployment stores identical DBM-safe metadata in the manifest");
    Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hashBefore) &&
        File.GetLastWriteTimeUtc(source) == writeTimeBefore &&
        File.ReadAllBytes(source).SequenceEqual(sourceBefore),
        "controlled deployment leaves source bytes, hash, and modification time unchanged");
    Directory.Delete(integrationResult.Plan.StagingDirectory, recursive: true);
    LoggingTests.Run(integrationRoot);
    string secondSource = Path.Combine(ipodRoot, "content", "custom", "second.wav");
    WritePcmWave(secondSource);
    var secondIntent = new DeploymentTrackIntent(Guid.NewGuid(), LibraryCategory.Custom,
        secondSource, "custom/second.wav", "Second", "Artist", "Album");
    await DeploymentRecoveryTests.Run(integrationPaths, integrationIntent, secondIntent,
        Enumerable.Repeat((byte)1, 73).ToArray());
    await DeploymentOwnershipTests.Run(integrationIntent, secondIntent, Enumerable.Repeat((byte)1, 73).ToArray());
}
finally
{
    if (Directory.Exists(integrationRoot)) Directory.Delete(integrationRoot, recursive: true);
}

Console.WriteLine("All tests passed.");





