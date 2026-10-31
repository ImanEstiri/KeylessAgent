using System.Text;
using ImanSoftware.FileStorage;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Shared file-writing rules: UTF-8 without BOM for new files, and BOM preservation
/// when an existing file starts with one.
/// </summary>
internal static class FileWriteHelpers
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Whether the file's first bytes are the UTF-8 byte-order mark.
    /// </summary>
    internal static async Task<bool> HasBomAsync(IFileStorage storage, string path)
    {
        var bytes = await storage.ReadAllBytesAsync(path);
        return bytes.IsSuccess && bytes.Data is [0xEF, 0xBB, 0xBF, ..];
    }

    /// <summary>
    /// Writes new content to an existing file, re-adding the BOM when the file had one.
    /// </summary>
    internal static async Task<Outcome> WritePreservingBomAsync(IFileStorage storage, string path, string content)
    {
        if (!await HasBomAsync(storage, path))
            return await storage.WriteAllTextAsync(path, content);

        var contentBytes = Encoding.UTF8.GetBytes(content);
        var withBom = new byte[Bom.Length + contentBytes.Length];
        Bom.CopyTo(withBom, 0);
        contentBytes.CopyTo(withBom, Bom.Length);

        return await storage.WriteAllBytesAsync(path, withBom);
    }
}
