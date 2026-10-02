using System.Text.Json;
using Avalonia.Media;

namespace WhiteBoard.Services;

/// <summary>用户偏好设置，随程序保存到 %AppData%。</summary>
public sealed class AppSettings
{
    /// <summary>默认笔色：白色（配希沃风格的深绿板，直接就能写）。</summary>
    public const string DefaultPenColor = "#FFFFFFFF";

    /// <summary>默认板面：希沃白板的深绿色。</summary>
    public const string DefaultBoardColor = "#FF0E3A2F";

    /// <summary>旧版本用过的深灰笔色，用于把老配置平滑迁移到白色。</summary>
    public const string LegacyPenColor = "#FF1A1A1A";

    public string LastPenColor { get; set; } = DefaultPenColor;

    public double PenThickness { get; set; } = 3.0;

    public double HighlighterThickness { get; set; } = 8.0;

    /// <summary>激光笔默认关闭轨迹以外的额外行为（保留开关位）。</summary>
    public bool LaserEnabled { get; set; } = true;

    public double EraserRadius { get; set; } = 14.0;

    public string EraserMode { get; set; } = "Pixel";

    public string PageBackgroundStyle { get; set; } = "Blank";

    public string PageBackground { get; set; } = DefaultBoardColor;

    public bool AutoStartAnnotate { get; set; }

    public bool ShowGrid { get; set; } = true;

    /// <summary>启动时是否全屏（一体机上默认开启）。</summary>
    public bool StartFullScreen { get; set; } = true;

    /// <summary>启动时是否隐藏标题栏（全屏白板模式）。</summary>
    public bool HideTitleBar { get; set; } = true;

    /// <summary>页面缩略图面板是否可见（默认隐藏，点页码切换）。</summary>
    public bool ShowPagePanel { get; set; }

    /// <summary>上次导出 / 保存用过的目录。</summary>
    public string? LastExportDirectory { get; set; }

    public string DefaultFontFamily { get; set; } = "Microsoft YaHei UI";

    public double DefaultFontSize { get; set; } = 28;

    public string ShapeFill { get; set; } = "None";

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WhiteBoard");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);

                if (loaded is not null)
                {
                    Migrate(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回落到默认值
        }

        return new AppSettings();
    }

    /// <summary>
    /// 老版本配置的平滑迁移。
    /// 早期默认是「白板 + 黑笔」，现在默认是「希沃深绿板 + 白笔」；
    /// 如果用户从未改过颜色，就把旧默认换成新默认，否则深绿板上写黑字根本看不清。
    /// </summary>
    private static void Migrate(AppSettings settings)
    {
        if (string.Equals(settings.LastPenColor, LegacyPenColor, StringComparison.OrdinalIgnoreCase))
            settings.LastPenColor = DefaultPenColor;

        if (string.Equals(settings.PageBackground, "#FFFFFFFF", StringComparison.OrdinalIgnoreCase))
            settings.PageBackground = DefaultBoardColor;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略保存失败（例如只读环境）
        }
    }

    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        try
        {
            return Color.Parse(value);
        }
        catch
        {
            return fallback;
        }
    }
}
