using System.IO.Compression;
using System.Security.Cryptography;

namespace DiscoAutosave;

internal static class SavePayloadComparison
{
    internal sealed record Entry(string Name, bool Equal, int? OptimizedLength, int? NativeLength,
        string? OptimizedSha256, string? NativeSha256, int? FirstDifference);

    internal static Entry[] Compare(byte[] optimized, byte[] native)
    {
        var left = Read(optimized);
        var right = Read(native);
        return left.Keys.Union(right.Keys).OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => CompareEntry(name, left.GetValueOrDefault(name), right.GetValueOrDefault(name))).ToArray();
    }

    private static Entry CompareEntry(string name, byte[]? a, byte[]? b)
    {
        int? difference = null;
        if (a != null && b != null)
        {
            int i = 0;
            while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
            if (i < Math.Max(a.Length, b.Length)) difference = i;
        }
        return new Entry(name, a != null && b != null && difference == null, a?.Length, b?.Length,
            Hash(a), Hash(b), difference);
    }

    private static string? Hash(byte[]? bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    private static Dictionary<string, byte[]> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            entries.Add(entry.FullName, output.ToArray());
        }
        return entries;
    }
}
