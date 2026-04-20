using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using ReverseMarkdown;

namespace AITrans.Services;

public sealed record EbookImportResult(
    string Markdown,
    string MarkdownPath,
    string AssetsDirectory,
    int ImageCount,
    int SkippedImages,
    IReadOnlyList<string> Warnings);

public sealed class EbookImportService
{
    private static readonly Converter MdConverter = new(new Config
    {
        UnknownTags = Config.UnknownTagsOption.Bypass,
        GithubFlavored = true,
        SmartHrefHandling = true,
        RemoveComments = true
    });

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

    public async Task<EbookImportResult> ImportAsync(string sourcePath, string outputRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("Output folder is required.", nameof(outputRoot));

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        return ext switch
        {
            ".epub" => await ImportEpubAsync(sourcePath, outputRoot, ct),
            ".fb2" => await ImportFb2Async(sourcePath, outputRoot, ct),
            _ => throw new InvalidOperationException("Unsupported format. Use .epub or .fb2")
        };
    }

    private async Task<EbookImportResult> ImportEpubAsync(string sourcePath, string outputRoot, CancellationToken ct)
    {
        var warnings = new List<string>();
        var outputDir = EnsureDirectory(outputRoot);
        var tempRoot = Path.Combine(Path.GetTempPath(), "aitrans_epub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            ZipFile.ExtractToDirectory(sourcePath, tempRoot, true);

            var containerPath = Path.Combine(tempRoot, "META-INF", "container.xml");
            if (!File.Exists(containerPath))
                throw new InvalidOperationException("EPUB container.xml not found.");

            var rootFileRel = GetEpubRootFilePath(containerPath) ?? "";
            if (string.IsNullOrWhiteSpace(rootFileRel))
                throw new InvalidOperationException("EPUB rootfile not found in container.xml.");

            var opfPath = Path.GetFullPath(Path.Combine(tempRoot, rootFileRel.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(opfPath))
                throw new InvalidOperationException("EPUB content.opf not found.");

            var opfDir = Path.GetDirectoryName(opfPath) ?? tempRoot;
            var opfDoc = XDocument.Load(opfPath);
            var title = GetEpubTitle(opfDoc) ?? Path.GetFileNameWithoutExtension(sourcePath);

            var manifest = BuildEpubManifest(opfDoc, opfDir);
            var spinePaths = BuildEpubSpine(opfDoc, manifest);

            if (spinePaths.Count == 0)
            {
                warnings.Add("EPUB spine is empty. Falling back to all XHTML files.");
                spinePaths = Directory.GetFiles(opfDir, "*.xhtml", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(opfDir, "*.html", SearchOption.AllDirectories))
                    .OrderBy(p => p)
                    .ToList();
            }

            var combinedHtml = new StringBuilder();
            foreach (var file in spinePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(file))
                {
                    warnings.Add($"Missing spine item: {Path.GetFileName(file)}");
                    continue;
                }

                var html = await File.ReadAllTextAsync(file, ct);
                var doc = new HtmlDocument
                {
                    OptionFixNestedTags = true,
                    OptionAutoCloseOnEnd = true
                };
                doc.LoadHtml(html);
                var body = doc.DocumentNode.SelectSingleNode("//body");
                combinedHtml.AppendLine(body?.InnerHtml ?? doc.DocumentNode.InnerHtml);
                combinedHtml.AppendLine("<hr />");
            }

            var merged = new HtmlDocument
            {
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true
            };
            merged.LoadHtml($"<html><body>{combinedHtml}</body></html>");

            RemoveNodesByTag(merged, "script", "style");

            var assetsInfo = CreateOutputPaths(outputDir, title);
            var (imageCount, skippedImages) = ProcessHtmlImages(
                merged,
                opfDir,
                assetsInfo.AssetsDirectory,
                assetsInfo.AssetsFolderName,
                manifest.MediaTypes,
                warnings);

            var markdown = ConvertHtmlToMarkdown(merged, title);
            await File.WriteAllTextAsync(assetsInfo.MarkdownPath, markdown, new UTF8Encoding(false), ct);

            return new EbookImportResult(
                markdown,
                assetsInfo.MarkdownPath,
                assetsInfo.AssetsDirectory,
                imageCount,
                skippedImages,
                warnings);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private async Task<EbookImportResult> ImportFb2Async(string sourcePath, string outputRoot, CancellationToken ct)
    {
        var warnings = new List<string>();
        var outputDir = EnsureDirectory(outputRoot);
        var xml = await File.ReadAllTextAsync(sourcePath, ct);
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var title = doc.Descendants(ns + "book-title").FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(sourcePath);

        var assetsInfo = CreateOutputPaths(outputDir, title);
        var imageMap = ExtractFb2Images(doc, ns, assetsInfo.AssetsDirectory, assetsInfo.AssetsFolderName, warnings);

        var html = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            html.AppendLine($"<h1>{WebUtility.HtmlEncode(title)}</h1>");

        var bodies = doc.Root?.Elements(ns + "body").ToList() ?? new List<XElement>();
        var mainBodies = bodies.Where(b => string.IsNullOrWhiteSpace((string?)b.Attribute("name")) ||
                                            !string.Equals((string?)b.Attribute("name"), "notes", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var notesBodies = bodies.Where(b => string.Equals((string?)b.Attribute("name"), "notes", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var body in mainBodies)
        {
            ct.ThrowIfCancellationRequested();
            RenderBody(body, html, ns, 1, imageMap);
        }

        if (notesBodies.Count > 0)
        {
            html.AppendLine("<h1>Notes</h1>");
            foreach (var body in notesBodies)
            {
                ct.ThrowIfCancellationRequested();
                RenderBody(body, html, ns, 1, imageMap);
            }
        }

        var markdown = ConvertHtmlToMarkdown(html.ToString(), title);
        await File.WriteAllTextAsync(assetsInfo.MarkdownPath, markdown, new UTF8Encoding(false), ct);

        return new EbookImportResult(
            markdown,
            assetsInfo.MarkdownPath,
            assetsInfo.AssetsDirectory,
            imageMap.Count,
            0,
            warnings);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static (string MarkdownPath, string AssetsDirectory, string AssetsFolderName) CreateOutputPaths(string outputRoot, string title)
    {
        var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "ebook" : title);
        var mdPath = BuildUniquePath(outputRoot, baseName, ".md");
        var assetsFolderName = BuildUniqueFolderName(outputRoot, baseName + "-assets");
        var assetsDir = Path.Combine(outputRoot, assetsFolderName);
        Directory.CreateDirectory(assetsDir);

        return (mdPath, assetsDir, assetsFolderName);
    }

    private static string BuildUniquePath(string dir, string baseName, string extension)
    {
        var safe = string.IsNullOrWhiteSpace(baseName) ? "ebook" : baseName;
        var candidate = Path.Combine(dir, safe + extension);
        var i = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{safe}_{i}{extension}");
            i++;
        }
        return candidate;
    }

    private static string BuildUniqueFolderName(string dir, string baseName)
    {
        var safe = string.IsNullOrWhiteSpace(baseName) ? "ebook-assets" : baseName;
        var candidate = safe;
        var i = 1;
        while (Directory.Exists(Path.Combine(dir, candidate)))
        {
            candidate = $"{safe}_{i}";
            i++;
        }
        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    private static string? GetEpubRootFilePath(string containerPath)
    {
        var doc = XDocument.Load(containerPath);
        var rootFile = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
        return rootFile?.Attribute("full-path")?.Value;
    }

    private static string? GetEpubTitle(XDocument opfDoc)
    {
        return opfDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value?.Trim();
    }

    private sealed record ManifestData(Dictionary<string, string> MediaTypes, Dictionary<string, string> SpineItems);

    private static ManifestData BuildEpubManifest(XDocument opfDoc, string opfDir)
    {
        var mediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spineItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in opfDoc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var id = item.Attribute("id")?.Value ?? "";
            var href = item.Attribute("href")?.Value ?? "";
            var media = item.Attribute("media-type")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
                continue;

            var fullPath = Path.GetFullPath(Path.Combine(opfDir, href.Replace('/', Path.DirectorySeparatorChar)));
            mediaTypes[NormalizePath(fullPath)] = media;
            spineItems[id] = fullPath;
        }

        return new ManifestData(mediaTypes, spineItems);
    }

    private static List<string> BuildEpubSpine(XDocument opfDoc, ManifestData manifest)
    {
        var items = new List<string>();
        foreach (var itemref in opfDoc.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            var idref = itemref.Attribute("idref")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(idref))
                continue;
            if (manifest.SpineItems.TryGetValue(idref, out var path))
                items.Add(path);
        }
        return items;
    }

    private static void RemoveNodesByTag(HtmlDocument doc, params string[] tags)
    {
        foreach (var tag in tags)
        {
            var nodes = doc.DocumentNode.SelectNodes("//" + tag);
            if (nodes == null) continue;
            foreach (var n in nodes)
                n.Remove();
        }
    }

    private static (int ImageCount, int SkippedImages) ProcessHtmlImages(
        HtmlDocument doc,
        string opfDir,
        string assetsDir,
        string assetsFolderName,
        Dictionary<string, string> mediaTypes,
        List<string> warnings)
    {
        var images = 0;
        var skipped = 0;
        var hashToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var imgNodes = doc.DocumentNode.SelectNodes("//img")?.ToList() ?? new List<HtmlNode>();
        foreach (var img in imgNodes)
        {
            var src = img.GetAttributeValue("src", "").Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                skipped++;
                continue;
            }

            if (TryParseDataUri(src, out var mime, out var bytes) && bytes != null)
            {
                var ext = GetExtensionFromMimeType(mime) ?? ".png";
                var fileName = BuildImageFile(bytes, ext, assetsDir, hashToFile);
                img.SetAttributeValue("src", $"{assetsFolderName}/{fileName}");
                images++;
                continue;
            }

            var resolvedPath = ResolveRelativePath(opfDir, src);
            if (resolvedPath == null || !File.Exists(resolvedPath))
            {
                warnings.Add($"Missing image: {src}");
                skipped++;
                continue;
            }

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(resolvedPath);
            }
            catch
            {
                warnings.Add($"Failed to read image: {src}");
                skipped++;
                continue;
            }

            var extFromFile = Path.GetExtension(resolvedPath);
            var mediaType = mediaTypes.TryGetValue(NormalizePath(resolvedPath), out var media) ? media : "";
            var fileExt = !string.IsNullOrWhiteSpace(extFromFile) ? extFromFile : GetExtensionFromMimeType(mediaType) ?? ".png";
            var imageFileName = BuildImageFile(fileBytes, fileExt, assetsDir, hashToFile);
            img.SetAttributeValue("src", $"{assetsFolderName}/{imageFileName}");
            images++;
        }

        var svgImages = doc.DocumentNode.SelectNodes("//image")?.ToList() ?? new List<HtmlNode>();
        foreach (var svgImage in svgImages)
        {
            var href = svgImage.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href))
                href = svgImage.GetAttributeValue("xlink:href", "");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var resolvedPath = ResolveRelativePath(opfDir, href);
            if (resolvedPath == null || !File.Exists(resolvedPath))
            {
                warnings.Add($"Missing image: {href}");
                skipped++;
                continue;
            }

            var fileBytes = File.ReadAllBytes(resolvedPath);
            var extFromFile = Path.GetExtension(resolvedPath);
            var mediaType = mediaTypes.TryGetValue(NormalizePath(resolvedPath), out var media) ? media : "";
            var fileExt = !string.IsNullOrWhiteSpace(extFromFile) ? extFromFile : GetExtensionFromMimeType(mediaType) ?? ".png";
            var imageFileName = BuildImageFile(fileBytes, fileExt, assetsDir, hashToFile);
            var img = HtmlNode.CreateNode("<img />");
            img.SetAttributeValue("src", $"{assetsFolderName}/{imageFileName}");
            svgImage.ParentNode?.InsertBefore(img, svgImage);
            svgImage.Remove();
            images++;
        }

        return (images, skipped);
    }

    private static string BuildImageFile(byte[] bytes, string extension, string assetsDir, Dictionary<string, string> hashToFile)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hashToFile.TryGetValue(hash, out var existing))
            return existing;

        var ext = extension.StartsWith(".") ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        var fileName = $"img_{hash.Substring(0, 12)}{ext}";
        var target = Path.Combine(assetsDir, fileName);
        if (!File.Exists(target))
            File.WriteAllBytes(target, bytes);
        hashToFile[hash] = fileName;
        return fileName;
    }

    private static string ConvertHtmlToMarkdown(HtmlDocument doc, string? title)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body")?.InnerHtml ?? doc.DocumentNode.InnerHtml;
        return ConvertHtmlToMarkdown(body, title);
    }

    private static string ConvertHtmlToMarkdown(string html, string? title)
    {
        var markdown = MdConverter.Convert($"<div>{html}</div>");
        markdown = CleanMarkdown(markdown);
        if (!string.IsNullOrWhiteSpace(title) && !Regex.IsMatch(markdown, @"^#\s+", RegexOptions.Multiline))
            markdown = "# " + title.Trim() + "\n\n" + markdown;
        return markdown.Trim();
    }

    private static string CleanMarkdown(string markdown)
    {
        markdown = Regex.Replace(markdown, @"\n\n\n+", "\n\n");
        markdown = Regex.Replace(markdown, @"^\s*[-*_]{1,2}\s*$", "", RegexOptions.Multiline);
        markdown = Regex.Replace(markdown, @"\n\n\n+", "\n\n");
        return markdown.Trim();
    }

    private static string? ResolveRelativePath(string baseDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        if (relative.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Uri.TryCreate(relative, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return null;

        var trimmed = relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(baseDir, trimmed));
    }

    private static string? GetExtensionFromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;
        return MediaTypeToExtension.TryGetValue(mimeType.Trim(), out var ext) ? ext : null;
    }

    private static bool TryParseDataUri(string source, out string? mimeType, out byte[]? dataBytes)
    {
        mimeType = null;
        dataBytes = null;

        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIdx = source.IndexOf(',');
        if (commaIdx < 0) return false;

        var header = source.Substring(5, commaIdx - 5);
        var dataPart = source[(commaIdx + 1)..];
        mimeType = header.Split(';').FirstOrDefault();
        var isBase64 = header.Contains(";base64", StringComparison.OrdinalIgnoreCase);

        try
        {
            dataBytes = isBase64
                ? Convert.FromBase64String(dataPart)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(dataPart));
            return true;
        }
        catch
        {
            dataBytes = null;
            return true;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

    private static Dictionary<string, string> ExtractFb2Images(
        XDocument doc,
        XNamespace ns,
        string assetsDir,
        string assetsFolderName,
        List<string> warnings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var binaries = doc.Descendants(ns + "binary").ToList();

        foreach (var bin in binaries)
        {
            var id = bin.Attribute("id")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var contentType = bin.Attribute("content-type")?.Value ?? "";
            var data = (bin.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                warnings.Add($"Empty binary image: {id}");
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch
            {
                warnings.Add($"Invalid base64 image: {id}");
                continue;
            }

            var ext = GetExtensionFromMimeType(contentType) ?? ".bin";
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fileName = $"img_{hash.Substring(0, 12)}{ext}";
            var target = Path.Combine(assetsDir, fileName);
            if (!File.Exists(target))
                File.WriteAllBytes(target, bytes);

            map[id] = $"{assetsFolderName}/{fileName}";
        }

        return map;
    }

    private static void RenderBody(XElement body, StringBuilder sb, XNamespace ns, int depth, Dictionary<string, string> images)
    {
        foreach (var element in body.Elements())
        {
            if (element.Name.LocalName == "section")
                RenderSection(element, sb, ns, depth, images);
        }
    }

    private static void RenderSection(XElement section, StringBuilder sb, XNamespace ns, int depth, Dictionary<string, string> images)
    {
        var title = section.Element(ns + "title");
        if (title != null)
        {
            var titleText = ExtractPlainText(title);
            if (!string.IsNullOrWhiteSpace(titleText))
            {
                var level = Math.Min(6, Math.Max(1, depth + 1));
                sb.AppendLine($"<h{level}>{WebUtility.HtmlEncode(titleText)}</h{level}>");
            }
        }

        foreach (var child in section.Elements())
        {
            if (child.Name.LocalName == "title")
                continue;
            RenderBlockNode(child, sb, ns, depth, images);
        }
    }

    private static void RenderBlockNode(XElement node, StringBuilder sb, XNamespace ns, int depth, Dictionary<string, string> images)
    {
        switch (node.Name.LocalName)
        {
            case "section":
                RenderSection(node, sb, ns, depth + 1, images);
                break;
            case "p":
                sb.Append("<p>");
                RenderInlineNodes(node, sb, ns, images);
                sb.AppendLine("</p>");
                break;
            case "subtitle":
                var subtitle = ExtractPlainText(node);
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    var level = Math.Min(6, Math.Max(2, depth + 2));
                    sb.AppendLine($"<h{level}>{WebUtility.HtmlEncode(subtitle)}</h{level}>");
                }
                break;
            case "cite":
            case "epigraph":
                sb.Append("<blockquote>");
                RenderBlockChildren(node, sb, ns, depth, images);
                sb.AppendLine("</blockquote>");
                break;
            case "poem":
                sb.Append("<pre>");
                RenderPoem(node, sb, ns);
                sb.AppendLine("</pre>");
                break;
            case "image":
                RenderImage(node, sb, images);
                break;
            case "table":
                RenderTable(node, sb, ns, images);
                break;
            case "empty-line":
                sb.AppendLine("<br />");
                break;
            case "code":
                sb.Append("<pre><code>");
                sb.Append(WebUtility.HtmlEncode(node.Value));
                sb.AppendLine("</code></pre>");
                break;
            default:
                RenderBlockChildren(node, sb, ns, depth, images);
                break;
        }
    }

    private static void RenderBlockChildren(XElement node, StringBuilder sb, XNamespace ns, int depth, Dictionary<string, string> images)
    {
        foreach (var child in node.Elements())
            RenderBlockNode(child, sb, ns, depth, images);
    }

    private static void RenderInlineNodes(XElement node, StringBuilder sb, XNamespace ns, Dictionary<string, string> images)
    {
        foreach (var child in node.Nodes())
        {
            switch (child)
            {
                case XText text:
                    sb.Append(WebUtility.HtmlEncode(text.Value));
                    break;
                case XElement el:
                    RenderInlineElement(el, sb, ns, images);
                    break;
            }
        }
    }

    private static void RenderInlineElement(XElement el, StringBuilder sb, XNamespace ns, Dictionary<string, string> images)
    {
        switch (el.Name.LocalName)
        {
            case "strong":
            case "b":
                sb.Append("<strong>");
                RenderInlineNodes(el, sb, ns, images);
                sb.Append("</strong>");
                break;
            case "emphasis":
            case "em":
            case "i":
                sb.Append("<em>");
                RenderInlineNodes(el, sb, ns, images);
                sb.Append("</em>");
                break;
            case "code":
                sb.Append("<code>");
                sb.Append(WebUtility.HtmlEncode(el.Value));
                sb.Append("</code>");
                break;
            case "a":
                var href = el.Attribute("href")?.Value ?? "";
                sb.Append("<a href=\"");
                sb.Append(WebUtility.HtmlEncode(href));
                sb.Append("\">");
                RenderInlineNodes(el, sb, ns, images);
                sb.Append("</a>");
                break;
            case "image":
                RenderImage(el, sb, images);
                break;
            default:
                RenderInlineNodes(el, sb, ns, images);
                break;
        }
    }

    private static void RenderPoem(XElement poem, StringBuilder sb, XNamespace ns)
    {
        var lines = new List<string>();
        foreach (var stanza in poem.Elements(ns + "stanza"))
        {
            foreach (var line in stanza.Elements(ns + "v"))
                lines.Add(WebUtility.HtmlEncode(line.Value.Trim()));
            lines.Add("");
        }
        sb.Append(string.Join("\n", lines).TrimEnd());
    }

    private static void RenderTable(XElement table, StringBuilder sb, XNamespace ns, Dictionary<string, string> images)
    {
        sb.AppendLine("<table>");
        foreach (var row in table.Elements(ns + "tr"))
        {
            sb.AppendLine("<tr>");
            foreach (var cell in row.Elements())
            {
                var cellTag = cell.Name.LocalName == "th" ? "th" : "td";
                sb.Append("<").Append(cellTag).Append(">");
                RenderInlineNodes(cell, sb, ns, images);
                sb.Append("</").Append(cellTag).AppendLine(">");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</table>");
    }

    private static void RenderImage(XElement node, StringBuilder sb, Dictionary<string, string> images)
    {
        var href = node.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(href))
            return;
        href = href.TrimStart('#');
        if (!images.TryGetValue(href, out var path))
            return;
        sb.Append("<img src=\"");
        sb.Append(WebUtility.HtmlEncode(path));
        sb.AppendLine("\" />");
    }

    private static string ExtractPlainText(XElement node)
    {
        var sb = new StringBuilder();
        foreach (var text in node.DescendantNodes().OfType<XText>())
            sb.Append(text.Value);
        return sb.ToString().Trim();
    }
}
