using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher;

public partial class SettingsView : UserControl
{
    /// <summary>設定被改動時通知主視窗。key:"topmost"、"audio"、"arrange"、"language"。</summary>
    public event Action<string>? SettingChanged;

    private bool _loading;

    public SettingsView() => InitializeComponent();

    /// <summary>切到設定頁時:把目前設定讀進控制項,標題與卡片依序浮上來。</summary>
    public void OnShown()
    {
        LoadValues();
        Rise(Hdr, 0);
        for (int i = 0; i < Cards.Children.Count; i++)
            if (Cards.Children[i] is UIElement c) Rise(c, 70 + i * 55);
        Scroll.ScrollToTop();
    }

    /// <summary>外部(例如標題列釘選、音效面板)改了設定後,重新同步畫面。</summary>
    public void LoadValues()
    {
        _loading = true;
        var s = UiSettings.Current;
        LangZh.IsChecked = s.Language != "en";
        LangEn.IsChecked = s.Language == "en";
        TopSw.IsChecked = s.MainTopmost;
        IntroSw.IsChecked = s.IntroEnabled;
        AutoUpdateSw.IsChecked = s.AutoCheckUpdate;
        MasterSl.Value = s.MasterVolume;
        BgmSl.Value = s.BgmVolume;
        SfxSl.Value = s.SfxVolume;
        BgmSw.IsChecked = s.BgmOn;
        SfxSw.IsChecked = s.SfxOn;
        QuietSw.IsChecked = s.QuietWhenInactive;
        CoolSl.Value = s.CooldownSeconds;
        RetrySl.Value = s.MaxAttempts;
        ArrangeSw.IsChecked = s.AutoArrange;
        VerText.Text = $"展-RBLX多開器  v{AppConfig.VersionText}";
        RefreshTexts();
        UpdateEnabled();
        _loading = false;
    }

    private void RefreshTexts()
    {
        MasterTx.Text = $"{(int)Math.Round(MasterSl.Value * 100)}%";
        BgmTx.Text = $"{(int)Math.Round(BgmSl.Value * 100)}%";
        SfxTx.Text = $"{(int)Math.Round(SfxSl.Value * 100)}%";
        CoolTx.Text = $"{(int)CoolSl.Value} 秒";
        RetryTx.Text = $"{(int)RetrySl.Value} 次";
    }

    private void UpdateEnabled()
    {
        BgmSl.IsEnabled = BgmSw.IsChecked == true; BgmSl.Opacity = BgmSl.IsEnabled ? 1 : 0.4;
        SfxSl.IsEnabled = SfxSw.IsChecked == true; SfxSl.Opacity = SfxSl.IsEnabled ? 1 : 0.4;
    }

    private static void Save() => UiSettings.Save();

    // ---------- 語言 ----------
    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string lang }) return;
        if (UiSettings.Current.Language == lang) return;
        Loc.SetLanguage(lang);   // 儲存 + 立刻重新翻譯所有視窗
        SettingChanged?.Invoke("language");
    }

    // ---------- 視窗 ----------
    private void TopSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.MainTopmost = TopSw.IsChecked == true;
        Save();
        SettingChanged?.Invoke("topmost");
    }

    private void IntroSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.IntroEnabled = IntroSw.IsChecked == true;
        Save();
    }

    // ---------- 聲音 ----------
    private void Master_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        UiSettings.Current.MasterVolume = MasterSl.Value; RefreshTexts(); Save(); AudioChanged();
    }

    private void Bgm_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        UiSettings.Current.BgmVolume = BgmSl.Value; RefreshTexts(); Save(); AudioChanged();
    }

    private void Sfx_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        UiSettings.Current.SfxVolume = SfxSl.Value; RefreshTexts(); Save();
    }

    private void BgmSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.BgmOn = BgmSw.IsChecked == true; UpdateEnabled(); Save(); AudioChanged();
    }

    private void SfxSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.SfxOn = SfxSw.IsChecked == true; UpdateEnabled(); Save(); SettingChanged?.Invoke("audio");
    }

    private void QuietSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.QuietWhenInactive = QuietSw.IsChecked == true; Save();
    }

    private void AudioChanged()
    {
        AudioService.ApplySettings();
        SettingChanged?.Invoke("audio");
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in new[] { "select", "toggle_on", "ok", "launch" })
        {
            AudioService.Play(n);
            await Task.Delay(n == "ok" ? 900 : 450);
        }
    }

    private void ResetAudio_Click(object sender, RoutedEventArgs e)
    {
        var d = new UiSettings();
        var s = UiSettings.Current;
        s.BgmOn = d.BgmOn; s.SfxOn = d.SfxOn; s.MasterVolume = d.MasterVolume; s.BgmVolume = d.BgmVolume;
        s.SfxVolume = d.SfxVolume; s.QuietWhenInactive = d.QuietWhenInactive;
        Save(); LoadValues(); AudioChanged();
    }

    // ---------- 啟動行為 ----------
    private void Cool_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        UiSettings.Current.CooldownSeconds = (int)CoolSl.Value; RefreshTexts(); Save();
    }

    private void Retry_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        UiSettings.Current.MaxAttempts = (int)RetrySl.Value; RefreshTexts(); Save();
    }

    private void ArrangeSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.AutoArrange = ArrangeSw.IsChecked == true; Save();
        SettingChanged?.Invoke("arrange");
    }

    // ---------- 更新 ----------
    private void AutoUpdateSw_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UiSettings.Current.AutoCheckUpdate = AutoUpdateSw.IsChecked == true; Save();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "檢查中…";
        try
        {
            var r = await UpdateService.CheckAsync();
            switch (r.Status)
            {
                case UpdateService.Status.NewVersion:
                    UpdateStatusText.Text = $"有新版本 v{r.Latest!.ToString(3)}";
                    UpdateService.PromptNewVersion(r);
                    break;
                case UpdateService.Status.UpToDate:
                    UpdateStatusText.Text = "已是最新版本 ✔";
                    break;
                case UpdateService.Status.NoRelease:
                    UpdateStatusText.Text = "目前還沒有發佈任何版本";
                    break;
                default:
                    UpdateStatusText.Text = "檢查失敗,請確認網路連線後再試";
                    break;
            }
        }
        finally { CheckUpdateBtn.IsEnabled = true; }
    }

    // ---------- 資料 ----------
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogStore.FilePath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("確定要清除所有啟動紀錄嗎?", "清除紀錄", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try { File.Delete(LogStore.FilePath); } catch { }
    }

    private static void Rise(UIElement el, int delayMs)
    {
        var shift = new TranslateTransform(0, 18);
        el.RenderTransform = shift;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = begin });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(460))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}
