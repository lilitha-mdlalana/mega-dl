using MegaDl.Core.Links;

namespace MegaDl.Core.Tests;

public class MegaLinkTests
{
    [Theory]
    [InlineData("https://mega.nz/file/AbC-12_x#KeY_-abc", MegaLinkKind.File, "AbC-12_x", "KeY_-abc")]
    [InlineData("https://mega.nz/folder/F1d#fk", MegaLinkKind.Folder, "F1d", "fk")]
    [InlineData("https://mega.co.nz/#!old1!oldkey", MegaLinkKind.File, "old1", "oldkey")]
    [InlineData("https://mega.nz/#F!fold!fkey", MegaLinkKind.Folder, "fold", "fkey")]
    [InlineData("  https://www.mega.nz/file/x#y  ", MegaLinkKind.File, "x", "y")]
    public void Parses_supported_formats(string text, MegaLinkKind kind, string id, string key)
    {
        var link = MegaLink.Parse(text);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(id, link.Id);
        Assert.Equal(key, link.Key);
    }

    [Fact]
    public void Parses_file_inside_folder()
    {
        var link = MegaLink.Parse("https://mega.nz/folder/F1d#fk/file/Ch1");
        Assert.Equal(MegaLinkKind.FileInFolder, link.Kind);
        Assert.Equal("Ch1", link.ChildId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("https://example.com/file/a#b")]
    [InlineData("https://mega.nz/file/missingkey")]
    public void Rejects_non_links(string text) => Assert.False(MegaLink.TryParse(text, out _));

    [Fact]
    public void Finds_all_links_in_pasted_text()
    {
        const string text = "grab these: https://mega.nz/file/a#b, and\nhttps://mega.nz/folder/c#d (thanks)";
        Assert.Equal(2, MegaLink.FindAll(text).Count);
    }
}
