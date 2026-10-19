using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace AITrans.Services;

/// <summary>
/// Stream-based read/write helpers for <see cref="IStorageFile"/>. Unlike <c>File.ReadAllText</c>/
/// <c>File.WriteAllText</c> on a raw path, these work on every Avalonia platform: on Browser and
/// Android, a picked file's path is often not a real filesystem path (no local filesystem on
/// Browser; content:// URIs on Android), so <see cref="IStorageItem.TryGetLocalPath"/> returns null
/// and direct <see cref="System.IO.File"/> access fails. Opening a stream through the
/// <see cref="IStorageFile"/> itself works everywhere, including Desktop.
/// </summary>
public static class StorageFileIO
{
    public static async Task<string> ReadAllTextAsync(this IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    public static async Task WriteAllTextAsync(this IStorageFile file, string content)
    {
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content);
    }

    public static async Task<byte[]> ReadAllBytesAsync(this IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    public static async Task WriteAllBytesAsync(this IStorageFile file, byte[] content)
    {
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(content);
    }

    /// <summary>
    /// A path usable for display, cache keys and extension checks: the real local path when one
    /// exists (Desktop, and often Android), falling back to just the file name (Browser, and Android
    /// content:// URIs without a resolvable path) where the file can no longer be reopened by path alone.
    /// </summary>
    public static string GetDisplayPath(this IStorageFile file) => file.TryGetLocalPath() ?? file.Name;
}
