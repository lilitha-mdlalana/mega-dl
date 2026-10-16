namespace MegaDl.Core.Proxy;

public enum ProxyKind { Http, Socks5 }

/// <summary>One parsed proxy from the list. <see cref="Key"/> identifies it for ban/exclude purposes.</summary>
public sealed record ProxyEntry(string Host, int Port, ProxyKind Kind, string? Username = null, string? Password = null)
{
    public string Key => $"{Kind}:{Host}:{Port}";
}
