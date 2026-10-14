namespace MegaDl.Core.Api;

/// <summary>One entry from a MEGA folder listing, name already decrypted.</summary>
public sealed record FolderNode(
    string Handle,
    string? ParentHandle,
    bool IsFolder,
    string Name,
    long Size,
    byte[] NodeKey);
