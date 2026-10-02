using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteBoard.Models;

namespace WhiteBoard.Services;

/// <summary>
/// 白板文件的读写与图片导出。文件后缀 .wbd（WhiteBoard Document），内容为 JSON。
/// </summary>
public static class WhiteboardFileService
{
    public const string FileExtension = "wbd";

    public const string FileFilterName = "互动白板文档";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync(WhiteboardDocument document, string path)
    {
        document.ModifiedAt = DateTimeOffset.Now;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // ConfigureAwait(false)：库代码不应该依赖调用方的同步上下文，
        // 否则在 UI 线程上同步等待（.Result / GetResult()）会死锁。
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options).ConfigureAwait(false);

        await stream.FlushAsync().ConfigureAwait(false);
    }

    public static async Task<WhiteboardDocument?> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var doc = await JsonSerializer.DeserializeAsync<WhiteboardDocument>(stream, Options).ConfigureAwait(false);

        if (doc is not null)
        {
            if (doc.Pages.Count == 0)
                doc.Pages.Add(new WhiteboardPage { Title = "第 1 页" });

            doc.ActivePage = doc.Pages[0];
        }

        return doc;
    }

    public static string Serialize(WhiteboardDocument document)
        => JsonSerializer.Serialize(document, Options);

    public static WhiteboardDocument? Deserialize(string json)
        => JsonSerializer.Deserialize<WhiteboardDocument>(json, Options);

    /// <summary>导出为 PNG（按页面顺序命名）。</summary>
    public static void ExportPng(WhiteboardPage page, string path, int pixelWidth, int pixelHeight)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var bmp = RenderPageBitmap(page, pixelWidth, pixelHeight);
            bmp.Save(fullPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ExplainWriteFailure(fullPath, directory, ex), ex);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new IOException(
                $"文件被占用，无法写入：\n{fullPath}\n\n" +
                "如果该图片正在被其它程序打开（看图工具、图片编辑器、Office 等），请先关闭后再试。", ex);
        }
    }

    /// <summary>
    /// 把「拒绝访问」翻译成用户能看懂、能处理的中文提示。
    ///
    /// Windows 上有几种常见原因会导致写 Desktop 之类的目录被拒：
    ///   • 受控文件夹访问（Windows 安全中心的「勒索软件防护」）拦住了本程序；
    ///   • Desktop / Documents 被重定向到 OneDrive 且当前未同步 / 无权限；
    ///   • 目标目录是只读的，或当前用户没有写权限。
    /// 这几种情况下程序无法自行提权，只能给出明确指引。
    /// </summary>
    private static string ExplainWriteFailure(string fullPath, string? directory, Exception ex)
    {
        var dir = string.IsNullOrEmpty(directory) ? "(未知目录)" : directory;

        return $"""
               没有权限写入这个位置：
               {fullPath}

               目标目录：{dir}

               常见原因与解决办法：
               1) Windows 安全中心的「受控文件夹访问」（勒索软件防护）会拦截程序写入
                  桌面 / 文档 / 图片等目录。可在「Windows 安全中心 → 病毒和威胁防护 →
                  勒索软件防护」中把本程序加入允许列表，或临时关闭该功能。
               2) 如果桌面 / 文档被重定向到 OneDrive 且当前未登录或未同步完成，
                  也会出现拒绝访问。可先保存到本地其它目录。
               3) 目标目录只读，或当前账户没有写权限。

               临时的替代方案：保存到「下载」或其它自建目录，也可以直接用
               「导出全部页面」选择别的文件夹。

               系统原始信息：{ex.Message}
               """;
    }

    private static bool IsSharingViolation(IOException ex)
        => ex.HResult == unchecked((int)0x80070020)   // ERROR_SHARING_VIOLATION
           || ex.HResult == unchecked((int)0x80070021); // ERROR_LOCK_VIOLATION

    /// <summary>
    /// 检查目录是否可写。用于在弹保存对话框前给出提示，避免用户挑完路径才失败。
    /// </summary>
    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            var probe = Path.Combine(directory, $".wb-write-test-{Guid.NewGuid():N}.tmp");
            using (var fs = File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在多个候选目录里挑一个可写的，作为保存对话框的默认位置。
    /// 顺序：用户上次用过的目录 → 图片 → 下载 → 文档 → 临时目录。
    /// </summary>
    public static string PickWritableDirectory(string? preferred = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferred))
            candidates.Add(preferred);

        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        candidates.Add(Path.GetTempPath());

        foreach (var dir in candidates)
        {
            if (!string.IsNullOrWhiteSpace(dir) && IsDirectoryWritable(dir))
                return dir;
        }

        return Path.GetTempPath();
    }

    /// <summary>
    /// 渲染整页。逻辑坐标取「1920×1080」与「背景截图实际尺寸」的较大者，
    /// 再按比例映射到目标像素尺寸。
    /// </summary>
    public static Avalonia.Media.Imaging.RenderTargetBitmap RenderPageBitmap(
        WhiteboardPage page, int pixelWidth, int pixelHeight)
    {
        var logicalW = Math.Max(1920, page.BackgroundImageRect.Right);
        var logicalH = Math.Max(1080, page.BackgroundImageRect.Bottom);

        Avalonia.Media.Imaging.RenderTargetBitmap? bitmap = null;
        Avalonia.Media.IImage? bg = null;

        try
        {
            bg = BackgroundImageCache.Get(page.BackgroundImageBase64);

            // RenderTargetBitmap 不带缩放，直接按目标像素尺寸建立画布，
            // 用 PushTransform 做逻辑坐标 -> 像素坐标的映射。
            bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize(pixelWidth, pixelHeight),
                new Avalonia.Vector(96, 96));

            using (var ctx = bitmap.CreateDrawingContext())
            {
                var scaleX = pixelWidth / logicalW;
                var scaleY = pixelHeight / logicalH;

                using (ctx.PushTransform(Avalonia.Matrix.CreateScale(scaleX, scaleY)))
                {
                    var area = new Avalonia.Rect(0, 0, logicalW, logicalH);
                    WhiteboardRenderer.DrawPageBackground(ctx, page, area, bg);
                    WhiteboardRenderer.DrawItems(ctx, page.Items);
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
        finally
        {
            (bg as IDisposable)?.Dispose();
        }
    }

    /// <summary>估算文件大小（用于界面提示）。</summary>
    public static long EstimateSize(WhiteboardDocument document)
        => System.Text.Encoding.UTF8.GetByteCount(Serialize(document));

    public static string ToCompactJson(WhiteboardDocument document)
        => JsonSerializer.Serialize(document, CompactOptions);
}
