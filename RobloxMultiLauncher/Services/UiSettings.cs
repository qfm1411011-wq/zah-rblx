using System.Text.Json;

namespace RobloxMultiLauncher.Services;

/// <summary>介面偏好(視窗置頂),存在 %AppData%\RobloxMultiLauncher\ui.json。</summary>
public sealed class UiSettings
{
    public bool MainTopmost { get; set; } = true;
    public bool ToolsTopmost { get; set; } = true;

    // ---- 一般設定 ----
    /// <summary>介面語言:"zh" = 繁體中文,"en" = English。</summary>
    public string Language { get; set; } = "zh";
    /// <summary>開啟軟體時是否播放開場動畫(含開場音)。</summary>
    public bool IntroEnabled { get; set; } = true;
    /// <summary>啟動軟體時是否自動檢查 GitHub 上有沒有新版本(只通知)。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>每啟動成功一個帳號,要等這麼多秒才開下一個。</summary>
    public int CooldownSeconds { get; set; } = 25;
    /// <summary>啟動失敗時,每個帳號最多嘗試幾次。</summary>
    public int MaxAttempts { get; set; } = 4;
    /// <summary>啟動後自動排列 Roblox 視窗。</summary>
    public bool AutoArrange { get; set; } = true;

    // ---- 音效與音樂 ----
    public bool BgmOn { get; set; } = true;
    public bool SfxOn { get; set; } = true;
    public double MasterVolume { get; set; } = 0.85;
    public double BgmVolume { get; set; } = 0.55;
    public double SfxVolume { get; set; } = 0.85;
    /// <summary>離開本軟體(切到遊戲或其他視窗)時,背景音樂自動淡出,回來再淡入。</summary>
    public bool QuietWhenInactive { get; set; } = true;
    /// <summary>是否已顯示過「音效設定在這裡」的首次提示。</summary>
    public bool AudioHintShown { get; set; } = false;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiLauncher", "ui.json");

    public static UiSettings Current { get; } = Load();

    private static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
