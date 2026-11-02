using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace RobloxMultiLauncher.Services;

/// <summary>檢查 GitHub Releases 有沒有新版(只通知,不自動下載)。</summary>
public static class UpdateService
{
    // 改成你自己的 GitHub 帳號 / 倉庫(要跟 README 的 Releases 連結一致)
    private const string Repo = "zahisgood-spec/ZAH-RBLX-Launcher";
    private static readonly string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly string ReleasesPage = $"https://github.com/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static bool _prompting;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxMultiLauncher-UpdateCheck");   // GitHub API 一定要有 User-Agent
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public enum Status { UpToDate, NewVersion, NoRelease, Failed }

    public sealed record Result(Status Status, Version? Latest, string Url);

    /// <summary>查詢最新版本。不會丟例外,失敗時回傳 Status.Failed。</summary>
    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(ApiUrl);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(Status.NoRelease, null, ReleasesPage);   // 還沒發佈過任何 Release
            if (!resp.IsSuccessStatusCode)
                return new Result(Status.Failed, null, ReleasesPage);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return new Result(Status.Failed, null, url);
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));

            return new Result(latest > AppConfig.Current ? Status.NewVersion : Status.UpToDate, latest, url);
        }
        catch
        {
            return new Result(Status.Failed, null, ReleasesPage);
        }
    }

    /// <summary>跳出「有新版本」提示;使用者按「前往下載」就用瀏覽器開啟 Release 頁面。</summary>
    public static void PromptNewVersion(Result r)
    {
        if (_prompting || r.Latest is null) return;
        _prompting = true;
        try
        {
            var ans = RobloxMultiLauncher.MessageBox.Show(
                $"發現新版本 v{r.Latest.ToString(3)}(你目前是 v{AppConfig.VersionText})。\n要現在前往下載頁面嗎?",
                "有新版本",
                MessageBoxButton.YesNo, MessageBoxImage.Information,
                "前往下載", "稍後再說");
            if (ans == MessageBoxResult.Yes) OpenPage(r.Url);
        }
        finally { _prompting = false; }
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>啟動時背景檢查(靜默):只有發現新版才打擾使用者,沒網路或出錯都不顯示任何東西。</summary>
    public static async Task CheckOnStartupAsync()
    {
        if (!UiSettings.Current.AutoCheckUpdate) return;
        var r = await CheckAsync();
        if (r.Status == Status.NewVersion) PromptNewVersion(r);
    }
}
