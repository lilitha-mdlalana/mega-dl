using System.Text.RegularExpressions;

namespace MegaDl.Core.Links;

public enum MegaLinkKind { File, Folder, FileInFolder }

/// <summary>
/// A parsed MEGA public link. Handles both URL styles:
///   new:    https://mega.nz/file/ID#KEY
///           https://mega.nz/folder/ID#KEY
///           https://mega.nz/folder/ID#KEY/file/FILEID
///   legacy: https://mega.nz/#!ID!KEY    https://mega.nz/#F!ID!KEY
/// Java port note: MiscTools.extractMegaLinksFromString / newMegaLinks2Legacy and the
/// regexes in MegaAPI.getMegaFileMetadata. MegaBasterd converts everything to the
/// legacy form internally; we parse into a typed object instead.
/// </summary>
public sealed partial record MegaLink(MegaLinkKind Kind, string Id, string Key, string? ChildId = null)
{
    public static bool TryParse(string? text, out MegaLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        Match m;
        if ((m = NewFolderFile().Match(s)).Success)
            link = new(MegaLinkKind.FileInFolder, m.Groups["id"].Value, m.Groups["key"].Value, m.Groups["child"].Value);
        else if ((m = NewStyle().Match(s)).Success)
            link = new(m.Groups["type"].Value == "file" ? MegaLinkKind.File : MegaLinkKind.Folder,
                       m.Groups["id"].Value, m.Groups["key"].Value);
        else if ((m = LegacyStyle().Match(s)).Success)
            link = new(m.Groups["f"].Success && m.Groups["f"].Value == "F" ? MegaLinkKind.Folder : MegaLinkKind.File,
                       m.Groups["id"].Value, m.Groups["key"].Value);

        return link is not null;
    }

    public static MegaLink Parse(string text) =>
        TryParse(text, out var link) ? link! : throw new FormatException($"Not a MEGA link: '{text}'");

    /// <summary>Finds every MEGA link in a blob of text (clipboard, pasted list...).</summary>
    public static IReadOnlyList<MegaLink> FindAll(string text)
    {
        var result = new List<MegaLink>();
        foreach (Match m in AnyLink().Matches(text))
            if (TryParse(m.Value, out var link)) result.Add(link!);
        return result;
    }

    public override string ToString() => Kind switch
    {
        MegaLinkKind.File => $"https://mega.nz/file/{Id}#{Key}",
        MegaLinkKind.Folder => $"https://mega.nz/folder/{Id}#{Key}",
        _ => $"https://mega.nz/folder/{Id}#{Key}/file/{ChildId}",
    };

    private const string Host = @"https?://(?:www\.)?mega(?:\.co)?\.nz";

    [GeneratedRegex(Host + @"/folder/(?<id>[\w-]+)#(?<key>[\w-]+)/file/(?<child>[\w-]+)")]
    private static partial Regex NewFolderFile();

    [GeneratedRegex(Host + @"/(?<type>file|folder)/(?<id>[\w-]+)#(?<key>[\w-]+)")]
    private static partial Regex NewStyle();

    [GeneratedRegex(Host + @"/#(?<f>F)?!(?<id>[\w-]+)!(?<key>[\w-]+)")]
    private static partial Regex LegacyStyle();

    [GeneratedRegex(Host + @"/\S+")]
    private static partial Regex AnyLink();
}
