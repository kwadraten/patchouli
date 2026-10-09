using System.Security.Cryptography;
using System.Text;

namespace Patchouli.Llm;

/// <summary>Deterministic paired wire IDs; the durable transcript retains the original provider IDs.</summary>
public static class NativeToolIds
{
    public static string Normalize(string id, string vendor)
    {
        bool mistral = vendor.Contains("Mistral", StringComparison.OrdinalIgnoreCase);
        bool valid = id.Length > 0 && id.Length <= 64 &&
                     id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
        if (mistral)
        {
            valid = id.Length == 9 && id.All(char.IsAsciiLetterOrDigit);
        }

        if (valid)
        {
            return id;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return mistral ? hash[..9] : "call_" + hash[..40];
    }
}
