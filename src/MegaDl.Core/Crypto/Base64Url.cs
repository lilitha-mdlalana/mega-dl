namespace MegaDl.Core.Crypto;

/// <summary>
/// MEGA uses URL-safe base64 with the '=' padding stripped (keys in links, handles,
/// encrypted attributes). Java port note: MiscTools.UrlBASE642Bin / Bin2UrlBASE64.
/// </summary>
public static class Base64Url
{
    public static byte[] Decode(string value)
    {
        string s = value.Trim().Replace('-', '+').Replace('_', '/').Replace(",", "");
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException($"Invalid base64url length: '{value}'");
        }
        return Convert.FromBase64String(s);
    }

    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
