using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Patchouli.Core.Results;

namespace Patchouli.Host.Plugins;

public static class PluginIndexSignature
{
    public static Result<string> Verify(ReadOnlySpan<byte> content, string signatureText,
        IReadOnlyList<string> publicKeys)
    {
        try
        {
            string[] lines = signatureText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lines.Length is < 4 or > 5 || (lines.Length == 5 && lines[4].Length != 0) ||
                !lines[0].StartsWith("untrusted comment: ", StringComparison.Ordinal) ||
                !lines[2].StartsWith("trusted comment: ", StringComparison.Ordinal))
            {
                return Failure("The index signature has an invalid minisign envelope.");
            }

            byte[] signed = Convert.FromBase64String(lines[1]);
            byte[] commentSignature = Convert.FromBase64String(lines[3]);
            if (signed.Length != 74 || commentSignature.Length != 64 ||
                !(signed.AsSpan(0, 2).SequenceEqual("ED"u8) ||
                  signed.AsSpan(0, 2).SequenceEqual("Ed"u8)))
            {
                return Failure("The index signature uses an unsupported minisign algorithm or length.");
            }

            string trustedComment = lines[2]["trusted comment: ".Length..];
            if (trustedComment.Length == 0 || trustedComment.Length > 8192 ||
                trustedComment.Any(character => char.IsControl(character) && character != '\t'))
            {
                return Failure("The index trusted comment is empty or invalid.");
            }

            ReadOnlySpan<byte> signature = signed.AsSpan(10, 64);
            ReadOnlySpan<byte> keyId = signed.AsSpan(2, 8);
            byte[] hashed = signed.AsSpan(0, 2).SequenceEqual("ED"u8)
                ? NSec.Cryptography.HashAlgorithm.Blake2b_512.Hash(content)
                : content.ToArray();
            byte[] authenticatedComment = new byte[64 + Encoding.UTF8.GetByteCount(trustedComment)];
            signature.CopyTo(authenticatedComment);
            Encoding.UTF8.GetBytes(trustedComment, authenticatedComment.AsSpan(64));

            foreach (string encodedKey in publicKeys)
            {
                byte[] key = Convert.FromBase64String(encodedKey);
                if (key.Length != 42 || !key.AsSpan(0, 2).SequenceEqual("Ed"u8) ||
                    !CryptographicOperations.FixedTimeEquals(key.AsSpan(2, 8), keyId))
                {
                    continue;
                }

                PublicKey publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.AsSpan(10),
                    KeyBlobFormat.RawPublicKey);
                if (SignatureAlgorithm.Ed25519.Verify(publicKey, hashed, signature) &&
                    SignatureAlgorithm.Ed25519.Verify(publicKey, authenticatedComment, commentSignature))
                {
                    return Result<string>.Success(trustedComment);
                }
            }

            return Failure("No trusted Patchouli community key accepted the index signature.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return Failure($"The index signature or public key is malformed: {exception.Message}");
        }
    }

    private static Result<string> Failure(string message)
    {
        return Result<string>.Failure(AppErrorCodes.ValidationFailed, message);
    }
}
