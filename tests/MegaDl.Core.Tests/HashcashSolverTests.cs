using System.Security.Cryptography;
using MegaDl.Core.Api;
using MegaDl.Core.Crypto;
using Xunit;

namespace MegaDl.Core.Tests;

public class HashcashSolverTests
{
    [Fact]
    public void Solves_and_returned_nonce_satisfies_the_threshold()
    {
        byte[] token = new byte[48];
        RandomNumberGenerator.Fill(token);
        string tokenB64 = Base64Url.Encode(token);

        // easiness=255 gives the loosest threshold, so this finishes almost immediately.
        string challenge = $"1:255:1700000000:{tokenB64}";

        string solution = HashcashSolver.BuildSolutionHeader(challenge, TimeSpan.FromSeconds(30));
        string[] parts = solution.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.Equal("1", parts[0]);
        Assert.Equal(tokenB64, parts[1]);

        byte[] nonce = Base64Url.Decode(parts[2]);
        Assert.Equal(4, nonce.Length);

        uint threshold = (uint)((((255 & 63) << 1) + 1) << (((255 >> 6) & 3) * 7 + 3));
        byte[] message = new byte[4 + 262_144 * 48];
        nonce.CopyTo(message, 0);
        for (int offset = 4; offset < message.Length; offset += token.Length)
            token.CopyTo(message, offset);

        byte[] hash = SHA256.HashData(message);
        uint firstWord = (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
        Assert.True(firstWord <= threshold);
    }

    [Theory]
    [InlineData("2:255:1:AAAA")]           // wrong version
    [InlineData("1:255:1")]                // too few fields
    [InlineData("1:999:1:AAAA")]           // easiness out of range
    public void Rejects_malformed_challenges(string challenge) =>
        Assert.ThrowsAny<FormatException>(() => HashcashSolver.BuildSolutionHeader(challenge));
}
