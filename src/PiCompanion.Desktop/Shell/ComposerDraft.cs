namespace PiCompanion.Desktop.Shell;

public sealed record ComposerDraft(
    string WorkingDirectory,
    string Prompt,
    string Model,
    string ThinkingLevel,
    IReadOnlyList<ComposerAttachment> Attachments,
    string PermissionMode = "standard");

public sealed record ComposerAttachment(
    string Path,
    string DisplayName,
    string Kind,
    bool IsAvailable,
    string? PreviewDataUrl = null)
{
    private const int PreviewPixelSize = 160;

    public static ComposerAttachment FromPath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var isDirectory = System.IO.Directory.Exists(fullPath);
        var isFile = System.IO.File.Exists(fullPath);
        var trimmedPath = System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        var displayName = System.IO.Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = fullPath;
        }

        return new ComposerAttachment(
            fullPath,
            displayName,
            isDirectory ? "文件夹" : isFile ? "文件" : "缺失",
            isDirectory || isFile,
            isFile ? TryCreateImagePreviewDataUrl(fullPath) : null);
    }

    private static string? TryCreateImagePreviewDataUrl(string path)
    {
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is not
            (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"))
        {
            return null;
        }

        try
        {
            var uri = new Uri(path, UriKind.Absolute);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                uri,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var preview = new System.Windows.Media.Imaging.BitmapImage();
            preview.BeginInit();
            preview.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            preview.UriSource = uri;
            if (frame.PixelWidth >= frame.PixelHeight)
            {
                preview.DecodePixelWidth = PreviewPixelSize;
            }
            else
            {
                preview.DecodePixelHeight = PreviewPixelSize;
            }
            preview.EndInit();
            preview.Freeze();

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
        }
        catch
        {
            return null;
        }
    }
}
