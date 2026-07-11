using System.Text;
using Blake3;

namespace Patchouli.Infrastructure.Hashing;

internal static class Blake3Hash
{
    public const int HexLength = 64;
    private const int BufferSize = 1024 * 128;

    public static async Task<string> ComputeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            true);

        return await ComputeStreamAsync(stream, cancellationToken);
    }

    public static async Task<string> ComputeStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using Hasher hasher = Hasher.New();
        byte[] buffer = new byte[BufferSize];

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            hasher.Update(buffer.AsSpan(0, read));
        }

        return hasher.Finalize().ToString();
    }

    public static string ComputeUtf8(string value)
    {
        return Hasher.Hash(Encoding.UTF8.GetBytes(value)).ToString();
    }

    public static string ComputeBytes(ReadOnlySpan<byte> value)
    {
        return Hasher.Hash(value).ToString();
    }
}
