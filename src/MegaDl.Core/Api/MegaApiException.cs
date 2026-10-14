namespace MegaDl.Core.Api;

/// <summary>
/// MEGA reports errors as bare negative integers (e.g. "-9" or "[-9]").
/// Java port note: MegaAPIException + MegaErrorMessages (which also has i18n text).
/// </summary>
public sealed class MegaApiException(int code, string? detail = null)
    : Exception(detail ?? Describe(code))
{
    public int Code { get; } = code;

    /// <summary>Errors worth retrying after a backoff.</summary>
    public bool IsTransient => Code is -3 or -4 or -6 or -18;

    public static string Describe(int code) => code switch
    {
        -1 => "Internal error (EINTERNAL)",
        -2 => "Bad arguments (EARGS)",
        -3 => "Server busy, try again (EAGAIN)",
        -4 => "Rate limited (ERATELIMIT)",
        -6 => "Too many concurrent requests (ETOOMANY)",
        -9 => "File or folder not found (ENOENT)",
        -11 => "Access denied (EACCESS)",
        -14 => "Wrong decryption key (EKEY)",
        -16 => "Blocked / taken down (EBLOCKED)",
        -17 => "Transfer quota exceeded (EOVERQUOTA)",
        -18 => "Temporarily unavailable (ETEMPUNAVAIL)",
        -101 => "No download URL returned",
        _ => $"MEGA error {code}",
    };
}

/// <summary>HTTP 509 on a storage server: the IP's free transfer quota is used up.</summary>
public sealed class TransferQuotaExceededException()
    : Exception("MEGA transfer quota exceeded for this IP (HTTP 509).");
