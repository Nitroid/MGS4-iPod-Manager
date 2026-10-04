using iPodManager;

internal static class TrackDisplayOrderTests
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL " + message);
            Console.WriteLine("PASS " + message);
        }
        string[] titles = ["Episode 1", "Episode 10", "Episode 2", "Episode 11", "Episode 3"];
        Check(titles.OrderBy(x => x, TrackDisplayOrder.NaturalTitle).SequenceEqual(
            ["Episode 1", "Episode 2", "Episode 3", "Episode 10", "Episode 11"]), "ALL uses natural episode ordering");
        string prefix = "Guns of the HIDECHAN! Radio. 第";
        string[] japanese = [prefix + "1回", prefix + "10回", prefix + "2回", prefix + "11回"];
        Check(japanese.OrderBy(x => x, TrackDisplayOrder.NaturalTitle).SequenceEqual(
            new[] {1, 2, 10, 11}.Select(n => prefix + n + "回")), "natural digits work within Japanese titles");
        Check(TrackDisplayOrder.NaturalTitle.Compare("Episode " + new string('9', 100), "Episode 10") > 0,
            "natural numeric comparison does not overflow");
        Check(TrackDisplayOrder.NaturalTitle.Compare("episode 02", "Episode 2") == 0,
            "equivalent numeric titles preserve stable ordering and ignore case");
        Check(TrackDisplayOrder.NaturalTitle.Compare(prefix + "１０回", prefix + "2回") > 0 &&
            TrackDisplayOrder.NaturalTitle.Compare(prefix + "１０回", prefix + "11回") < 0,
            "restored podcasts sort full-width and ASCII digit runs together");
        (string Title, uint Number, uint Disc)[] records = [
            ("Z", 1u, 0u), ("A", 10u, 1u), ("B", 2u, 1u),
            ("Episode 10", 0u, 0u), ("Episode 2", 0u, 0u),
            ("Episode 10", 2u, 1u), ("Episode 2", 2u, 1u), ("Disc 2", 1u, 2u)
        ];
        var original = records.ToArray();
        var album = TrackDisplayOrder.Album(records, x => x.Disc, x => x.Number, x => x.Title).ToArray();
        Check(album.Select(x => x.Title).SequenceEqual(
            ["Z", "B", "Episode 2", "Episode 10", "A", "Disc 2", "Episode 2", "Episode 10"]),
            "album uses disc and track numbers, natural duplicate titles, then unnumbered titles");
        Check(records.SequenceEqual(original), "display ordering leaves underlying source order unchanged");
        Check(records.OrderBy(x => x.Title, TrackDisplayOrder.NaturalTitle).First().Title == "A" && album[0].Title == "Z",
            "ALL title order and album metadata order remain distinct");
    }
}
