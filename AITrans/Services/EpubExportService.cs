using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Markdig;

namespace AITrans.Services;

public sealed record EpubExportResult(
    string OutputPath,
    string Title,
    int ImageCount,
    int SkippedImages,
    IReadOnlyList<string> Warnings);

public sealed class EpubExportService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Dictionary<string, string> ExtensionToMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

    private static readonly Dictionary<string, string> MediaTypeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/bmp"] = ".bmp",
        ["image/tiff"] = ".tif",
    };

    private sealed record ImageResource(
        string Id,
        string Href,
        string EpubPath,
        string MediaType,
        string SourcePath);

    private sealed record ImageHarvestResult(
        List<ImageResource> Images,
        int SkippedImages,
        string? TempRoot);

    public async Task<EpubExportResult> ExportAsync(
        string markdown,
        string? sourcePath,
        string outputPath,
        string? languageName,
        CancellationToken ct)
    {
        var title = ExtractTitle(markdown, sourcePath);
        var lang = NormalizeLanguage(languageName);
        var baseDir = ResolveBaseDirectory(sourcePath);

        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var bodyHtml = Markdig.Markdown.ToHtml(markdown ?? string.Empty, pipeline);

        var doc = new HtmlDocument
        {
            OptionOutputAsXml = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(BuildHtmlSkeleton(title, lang, bodyHtml));

        var warnings = new List<string>();
        var harvest = await HarvestImagesAsync(doc, baseDir, warnings, ct);

        try
        {
            var bodyNode = doc.DocumentNode.SelectSingleNode("//body");
            var bodyContent = bodyNode?.InnerHtml ?? string.Empty;
            var contentXhtml = BuildContentXhtml(title, lang, bodyContent);
            var navXhtml = BuildNavXhtml(title, lang);
            var opf = BuildContentOpf(title, lang, harvest.Images);

            CreateEpubArchive(outputPath, contentXhtml, navXhtml, opf, harvest.Images);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(harvest.TempRoot))
            {
                try { Directory.Delete(harvest.TempRoot, true); } catch { }
            }
        }

        return new EpubExportResult(
            outputPath,
            title,
            harvest.Images.Count,
            harvest.SkippedImages,
            warnings);
    }

    private static string BuildHtmlSkeleton(string title, string lang, string bodyHtml)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        return $"""
<!DOCTYPE html>
<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"{lang}\" xml:lang=\"{lang}\">
<head>
  <meta charset=\"utf-8\" />
  <title>{safeTitle}</title>
  <link rel=\"stylesheet\" type=\"text/css\" href=\"styles.css\" />
</head>
<body>
{bodyHtml}
</body>
</html>
""";
    }

    private static string BuildContentXhtml(string title, string lang, string bodyHtml)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        return $"""
<?xml version=\"1.0\" encoding=\"utf-8\"?>
<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"{lang}\" xml:lang=\"{lang}\">
<head>
  <meta charset=\"utf-8\" />
  <title>{safeTitle}</title>
  <link rel=\"stylesheet\" type=\"text/css\" href=\"styles.css\" />
</head>
<body>
{bodyHtml}
</body>
</html>
""";
    }

    private static string BuildNavXhtml(string title, string lang)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        return $"""
<?xml version=\"1.0\" encoding=\"utf-8\"?>
<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{lang}\" xml:lang=\"{lang}\">
<head>
  <meta charset=\"utf-8\" />
  <title>Table of Contents</title>
</head>
<body>
  <nav epub:type=\"toc\" id=\"toc\">
    <h1>Table of Contents</h1>
    <ol>
      <li><a href=\"content.xhtml\">{safeTitle}</a></li>
    </ol>
  </nav>
</body>
</html>
""";
    }

    private static string BuildContentOpf(string title, string lang, List<ImageResource> images)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var bookId = "urn:uuid:" + Guid.NewGuid().ToString();
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">");
        sb.AppendLine("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        sb.AppendLine($"    <dc:identifier id=\"bookid\">{bookId}</dc:identifier>");
        sb.AppendLine($"    <dc:title>{safeTitle}</dc:title>");
        sb.AppendLine($"    <dc:language>{lang}</dc:language>");
        sb.AppendLine($"    <meta property=\"dcterms:modified\">{modified}</meta>");
        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <manifest>");
        sb.AppendLine("    <item id=\"content\" href=\"content.xhtml\" media-type=\"application/xhtml+xml\" />");
        sb.AppendLine("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\" />");
        sb.AppendLine("    <item id=\"css\" href=\"styles.css\" media-type=\"text/css\" />");

        foreach (var img in images)
            sb.AppendLine($"    <item id=\"{img.Id}\" href=\"{img.Href}\" media-type=\"{img.MediaType}\" />");

        sb.AppendLine("  </manifest>");
        sb.AppendLine("  <spine>");
        sb.AppendLine("    <itemref idref=\"content\" />");
        sb.AppendLine("  </spine>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    private static string BuildContainerXml()
    {
        return """
<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
  </rootfiles>
</container>
""";
    }

    private static string BuildDefaultCss()
    {
        return """
body { font-family: serif; line-height: 1.5; margin: 0; padding: 0; }
img { max-width: 100%; height: auto; }
pre, code { font-family: monospace; white-space: pre-wrap; }
h1, h2, h3, h4, h5, h6 { font-weight: 600; }
blockquote { margin-left: 1em; padding-left: 1em; border-left: 3px solid #ccc; }
""";
    }

    private static void CreateEpubArchive(
        string outputPath,
        string contentXhtml,
        string navXhtml,
        string contentOpf,
        List<ImageResource> images)
    {
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var stream = mimetype.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write("application/epub+zip");

        AddTextEntry(zip, "META-INF/container.xml", BuildContainerXml());
        AddTextEntry(zip, "OEBPS/content.opf", contentOpf);
        AddTextEntry(zip, "OEBPS/content.xhtml", contentXhtml);
        AddTextEntry(zip, "OEBPS/nav.xhtml", navXhtml);
        AddTextEntry(zip, "OEBPS/styles.css", BuildDefaultCss());

        foreach (var img in images)
        {
            if (!File.Exists(img.SourcePath))
                continue;

            var entry = zip.CreateEntry(img.EpubPath, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(img.SourcePath);
            fileStream.CopyTo(entryStream);
        }
    }

    private static void AddTextEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<ImageHarvestResult> HarvestImagesAsync(
        HtmlDocument doc,
        string? baseDir,
        List<string> warnings,
        CancellationToken ct)
    {
        var nodes = doc.DocumentNode.SelectNodes("//img[@src]");
        if (nodes == null || nodes.Count == 0)
            return new ImageHarvestResult([], 0, null);

        var images = new List<ImageResource>();
        string? tempRoot = null;
        int index = 1;
        int skipped = 0;

        foreach (var node in nodes.ToList())
        {
            var src = node.GetAttributeValue("src", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                node.Remove();
                continue;
            }

            try
            {
                node.Attributes.Remove("srcset");

                byte[]? data = null;
                string? sourcePath = null;
                string? mediaType = null;

                if (TryParseDataUri(src, out var dataMediaType, out var dataBytes))
                {
                    data = dataBytes;
                    mediaType = dataMediaType;
                }
                else if (TryGetHttpUri(src, out var httpUri))
                {
                    using var response = await HttpClient.GetAsync(httpUri, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        warnings.Add($"Image download failed ({(int)response.StatusCode}): {src}");
                        node.Remove();
                        skipped++;
                        continue;
                    }

                    data = await response.Content.ReadAsByteArrayAsync(ct);
                    mediaType = NormalizeMediaType(response.Content.Headers.ContentType?.MediaType);
                }
                else
                {
                    var resolvedPath = ResolveLocalImagePath(src, baseDir);
                    if (resolvedPath == null || !File.Exists(resolvedPath))
                    {
                        warnings.Add($"Image not found: {src}");
                        node.Remove();
                        skipped++;
                        continue;
                    }

                    sourcePath = resolvedPath;
                    mediaType = GuessMediaTypeFromPath(resolvedPath);
                }

                var ext = GetExtension(mediaType, sourcePath, src);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".img";

                mediaType = NormalizeMediaType(mediaType) ?? GuessMediaTypeFromExtension(ext) ?? "application/octet-stream";

                var fileName = $"image-{index:0000}{ext}";
                var href = $"images/{fileName}";
                var epubPath = $"OEBPS/{href}";
                var id = $"img-{index:0000}";

                if (data != null)
                {
                    tempRoot ??= Directory.CreateTempSubdirectory("aitrans-epub-").FullName;
                    sourcePath = Path.Combine(tempRoot, fileName);
                    await File.WriteAllBytesAsync(sourcePath, data, ct);
                }

                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    warnings.Add($"Image skipped (no source): {src}");
                    node.Remove();
                    skipped++;
                    continue;
                }

                node.SetAttributeValue("src", href);
                images.Add(new ImageResource(id, href, epubPath, mediaType, sourcePath));
                index++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Image skipped: {src} ({ex.Message})");
                node.Remove();
                skipped++;
            }
        }

        return new ImageHarvestResult(images, skipped, tempRoot);
    }

    private static bool TryParseDataUri(string src, out string mediaType, out byte[] data)
    {
        mediaType = string.Empty;
        data = Array.Empty<byte>();

        if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIndex = src.IndexOf(',');
        if (commaIndex <= 5)
            return false;

        var meta = src[5..commaIndex];
        var payload = src[(commaIndex + 1)..];
        if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = meta.Split(';', StringSplitOptions.RemoveEmptyEntries);
        mediaType = parts.Length > 0 ? parts[0] : "application/octet-stream";

        var clean = payload.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
        try
        {
            data = Convert.FromBase64String(clean);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetHttpUri(string src, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(src, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }

    private static string? ResolveLocalImagePath(string src, string? baseDir)
    {
        var cleaned = StripQueryAndFragment(Uri.UnescapeDataString(src));

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFile)
            return uri.LocalPath;

        if (Path.IsPathRooted(cleaned))
            return cleaned;

        if (string.IsNullOrWhiteSpace(baseDir))
            return null;

        return Path.GetFullPath(Path.Combine(baseDir, cleaned));
    }

    private static string StripQueryAndFragment(string value)
    {
        var noFragment = value.Split('#', 2)[0];
        return noFragment.Split('?', 2)[0];
    }

    private static string? GuessMediaTypeFromPath(string path)
    {
        var ext = Path.GetExtension(path);
        return GuessMediaTypeFromExtension(ext);
    }

    private static string? GuessMediaTypeFromExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return null;

        return ExtensionToMediaType.TryGetValue(ext, out var media) ? media : null;
    }

    private static string? NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        return mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : mediaType;
    }

    private static string? GetExtension(string? mediaType, string? sourcePath, string originalSrc)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var ext = Path.GetExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(ext))
                return ext.ToLowerInvariant();
        }

        mediaType = NormalizeMediaType(mediaType);
        if (!string.IsNullOrWhiteSpace(mediaType) && MediaTypeToExtension.TryGetValue(mediaType, out var mapped))
            return mapped;

        var fromSrc = ExtractExtensionFromPathOrUrl(originalSrc);
        if (!string.IsNullOrWhiteSpace(fromSrc))
            return fromSrc;

        return null;
    }

    private static string? ExtractExtensionFromPathOrUrl(string value)
    {
        var cleaned = StripQueryAndFragment(value);
        var ext = Path.GetExtension(cleaned);
        return string.IsNullOrWhiteSpace(ext) ? null : ext.ToLowerInvariant();
    }

    private static string ResolveBaseDirectory(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return string.Empty;

        var dir = Path.GetDirectoryName(sourcePath);
        return dir ?? string.Empty;
    }

    private static string NormalizeLanguage(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return "en";

        return languageName.Trim().ToLowerInvariant() switch
        {
            "bulgarian" => "bg",
            "russian" => "ru",
            "english" => "en",
            "german" => "de",
            "french" => "fr",
            "spanish" => "es",
            _ => "en"
        };
    }

    private static string ExtractTitle(string markdown, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# "))
                {
                    var title = trimmed[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                        return title;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.GetFileNameWithoutExtension(sourcePath) ?? "Document";

        return "Document";
    }
}
