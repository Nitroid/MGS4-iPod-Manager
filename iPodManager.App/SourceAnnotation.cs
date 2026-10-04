namespace iPodManager;

internal static class SourceAnnotation
{
    internal static string Read(TagLib.File file) =>
        Resolve(file.Tag.Comment, file.GetTag(TagLib.TagTypes.Xiph, false));

    internal static string Resolve(string? comment, TagLib.Tag? vorbis)
    {
        if (!string.IsNullOrWhiteSpace(comment))
            return comment;

        IEnumerable<TagLib.Ogg.XiphComment> comments = vorbis switch
        {
            TagLib.Ogg.XiphComment single => [single],
            TagLib.Ogg.GroupedComment group => group.Comments,
            _ => []
        };
        return comments.SelectMany(value => value.GetField("DESCRIPTION"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
    }
}
