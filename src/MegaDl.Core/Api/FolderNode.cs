namespace MegaDl.Core.Api;

/// <summary>One entry from a MEGA folder listing, name already decrypted.</summary>
public sealed record FolderNode(
    string Handle,
    string? ParentHandle,
    bool IsFolder,
    string Name,
    long Size,
    byte[] NodeKey);

public static class FolderNodeExtensions
{
    /// <summary>
    /// "&lt;folder name&gt;/&lt;subfolder path&gt;/&lt;node name&gt;" — walks the parent
    /// chain a folder listing gives back up to that folder's root. Shared by the console
    /// and the engine so a file downloaded from inside a folder always lands in the same
    /// place either way.
    /// </summary>
    public static string BuildRelativePath(this FolderNode node, IReadOnlyDictionary<string, FolderNode> byHandle)
    {
        var parts = new List<string> { node.Name };
        string? parentHandle = node.ParentHandle;
        while (parentHandle is not null && byHandle.TryGetValue(parentHandle, out FolderNode? parent))
        {
            parts.Insert(0, parent.Name);
            parentHandle = parent.ParentHandle;
        }
        return Path.Combine(parts.ToArray());
    }
}
