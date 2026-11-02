using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using RobloxMultiLauncher.Models;
using RobloxMultiLauncher.Services;

namespace RobloxMultiLauncher;

public partial class MainWindow : Window
{
    private static readonly string[] MutexNames = { "ROBLOX_singletonMutex" };

    private static readonly (string Label, string Hint, bool NeedsValue, string Placeholder)[] JoinUi =
    {
        ("自動配對", "進入系統分配的伺服器。Place ID 留空 = 只開啟 Roblox,不進遊戲。", false, ""),
        ("指定伺服器", "加入某個公開伺服器:需要上方的 Place ID,並在右側貼上該伺服器的 JobId(GUID 格式)。", true, "JobId"),
        ("私人伺服器", "貼上私人伺服器連結(privateServerLinkCode 或 roblox.com/share 分享連結皆可)。", true, "私人伺服器連結"),
        ("跟隨好友", "輸入好友的使用者名稱或數字 ID,所有帳號會加入他目前所在的遊戲(需符合 Roblox 的好友/隱私設定)。", true, "好友的使用者名稱或 ID"),
    };

    private static readonly Regex SafeCode = new("^[A-Za-z0-9_\\-]{1,64}$");
    private static readonly SemaphoreSlim LaunchLock = new(1, 1);
    private static int MaxAttempts => Math.Clamp(UiSettings.Current.MaxAttempts, 1, 8);

    private readonly MutexHolder _mutex = new();
    private readonly ObservableCollection<AccountVm> _accounts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _failurePromptShown;
    private List<GameEntry> _games = GameStore.Load();
    private int _joinMode;
    private bool _autoArrange { get => UiSettings.Current.AutoArrange; set { UiSettings.Current.AutoArrange = value; UiSettings.Save(); } }

    private static int CooldownSeconds => Math.Clamp(UiSettings.Current.CooldownSeconds, 5, 120); // 每啟動成功一個,要等這麼久才能開下一個
    private int _cooldownLeft;
    private bool _cooldownRunning;
    private bool _launchAllRunning;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var d in AccountStore.Load()) _accounts.Add(new AccountVm(d));
        AccountList.ItemsSource = _accounts;
        VersionText.Text = "v" + AppConfig.VersionText;
        Topmost = UiSettings.Current.MainTopmost;
        UpdatePinUi();
        UpdateAudioUi();
        bool introOn = UiSettings.Current.IntroEnabled || !SkipIntro();
        SettingsPage.SettingChanged += OnSettingChanged;

        Loaded += (_, _) =>
        {
            Loc.Sweep(this);
            if (introOn) BuildIntroFx();
            _ = ShowAudioHintAsync(introOn ? 4000 : 900);
            _ = CheckUpdateOnStartupAsync(introOn ? 6000 : 2500);   // 背景檢查新版本(只通知)
            ToolsPage.Attach(this);
            GamesPage.PlaceSelected += id => _ = PlayFromExploreAsync(id);
            TryAcquire();
            ApplyJoinMode();
            UpdateAutoArrangeText();
            UpdateGameName();
            Refresh();
            _timer.Tick += (_, _) => { Refresh(); CheckAutoMutexStatus(); };
            _timer.Start();
            _ = LoadProfilesAsync();
        };
        Closed += (_, _) => { _timer.Stop(); ToolsPage.OnHidden(); LogPage.OnHidden(); _mutex.Dispose(); AudioService.Shutdown(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Chrome.Apply(new WindowInteropHelper(this).Handle);
    }

    // ---------- 自訂標題列 ----------
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // 無邊框視窗最大化時會往螢幕外溢出一圈,內縮補回來
        RootGrid.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxBtn.Content = max ? "\uE923" : "\uE922";
    }

    private void UpdatePinUi()
    {
        PinBtn.Content = Topmost ? "\uE840" : "\uE718";
        PinBtn.Foreground = Res(Topmost ? "Accent" : "TextDim");
        PinBtn.ToolTip = Topmost ? "取消視窗置頂" : "視窗置頂";
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UiSettings.Current.MainTopmost = Topmost;
        UiSettings.Save();
        UpdatePinUi();
        if (_page == "settings") SettingsPage.LoadValues();
        ShowToast(Topmost ? "視窗已置頂,會浮在其他視窗上面" : "已取消視窗置頂", "mute");
    }

    // ---------- 設定頁 ----------
    private void OnSettingChanged(string key)
    {
        switch (key)
        {
            case "topmost": Topmost = UiSettings.Current.MainTopmost; UpdatePinUi(); break;
            case "audio": UpdateAudioUi(); break;
            case "arrange": UpdateAutoArrangeText(); break;
            case "language": UpdatePinUi(); UpdateAudioUi(); Refresh(); break;
        }
    }

    /// <summary>設定裡關閉開場動畫時:拿掉開場時間軸,主介面直接顯示。成功回傳 true。</summary>
    private bool SkipIntro()
    {
        try
        {
            bool removed = false;
            foreach (var tr in RootGrid.Triggers.OfType<EventTrigger>())
            {
                var bs = tr.Actions.OfType<BeginStoryboard>()
                    .FirstOrDefault(b => b.Storyboard.Children.Any(t => Storyboard.GetTargetName(t) == "IntroGlow"));
                if (bs == null) continue;
                tr.Actions.Remove(bs);
                removed = true;
                break;
            }
            if (!removed) return false;
            Intro.Visibility = Visibility.Collapsed;
            Shell.Opacity = 1;
            SideBar.Opacity = 1; SideT.X = 0;
            HeaderPanel.Opacity = 1; HeaderT.Y = 0;
            StatusBarHost.Opacity = 1; StatusBarT.Y = 0;
            NavHost.Opacity = 1; NavT.Y = 0;
            ListHost.Opacity = 1; ListT.Y = 0;
            return true;
        }
        catch { return false; }
    }

    // ---------- 開場動畫的程式碼特效(標題逐字、火花、飄浮光點)----------
    private void BuildIntroFx()
    {
        try
        {
            var rnd = new Random(7);
            static Color Lerp(Color a, Color b, double f) => Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // 標題:逐字落下,之後白光從左掃到右(對應音效的琶音與光波)
            const string title = "展-RBLX多開器";
            var cA = Color.FromRgb(0xC9, 0xB8, 0xFF);
            var cB = Color.FromRgb(0xF3, 0xC9, 0xEA);
            for (int i = 0; i < title.Length; i++)
            {
                var col = Lerp(cA, cB, i / (double)(title.Length - 1));
                var brush = new SolidColorBrush(col);
                var tt = new TranslateTransform(0, -30);
                var tb = new TextBlock
                {
                    Text = title[i].ToString(), FontSize = 28, FontWeight = FontWeights.Bold,
                    Foreground = brush, Opacity = 0, RenderTransform = tt,
                };
                IntroTitle.Children.Add(tb);
                var begin = TimeSpan.FromSeconds(0.95 + i * 0.065);
                tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(560))
                {
                    BeginTime = begin, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 },
                });
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(col, Colors.White, TimeSpan.FromMilliseconds(170))
                {
                    BeginTime = TimeSpan.FromSeconds(2.35 + i * 0.06), AutoReverse = true,
                });
            }

            // 火花:撞擊瞬間從 logo 中心向外迸出
            Color[] sc = { Color.FromRgb(0xB1, 0x97, 0xFF), Color.FromRgb(0xF8, 0xE7, 0xD2), Color.FromRgb(0xFF, 0x7C, 0xC4), Colors.White };
            for (int i = 0; i < 44; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, dist = 70 + rnd.NextDouble() * 210, size = 2 + rnd.NextDouble() * 4;
                double dur = 0.7 + rnd.NextDouble() * 0.8;
                var begin = TimeSpan.FromSeconds(0.85 + rnd.NextDouble() * 0.04);
                var move = new TranslateTransform();
                var shrink = new ScaleTransform(1, 1);
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = size, Height = size, Fill = new SolidColorBrush(sc[rnd.Next(sc.Length)]), Opacity = 0,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new TransformGroup { Children = { shrink, move } },
                };
                System.Windows.Controls.Canvas.SetLeft(dot, 88 - size / 2);
                System.Windows.Controls.Canvas.SetTop(dot, 88 - size / 2);
                IntroSparks.Children.Add(dot);
                var d = TimeSpan.FromSeconds(dur);
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(ang) * dist, d) { BeginTime = begin, EasingFunction = ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(ang) * dist + 36, d) { BeginTime = begin, EasingFunction = ease });
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, d) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.2, d) { BeginTime = begin });
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.2, d) { BeginTime = begin });
            }

            // 進度條下方的狀態文字(用程式碼動畫,才能跟著語言)
            var status = new ObjectAnimationUsingKeyFrames();
            status.KeyFrames.Add(new DiscreteObjectKeyFrame(Loc.T("載入音效引擎…"), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
            status.KeyFrames.Add(new DiscreteObjectKeyFrame(Loc.T("檢查多開狀態…"), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.95))));
            status.KeyFrames.Add(new DiscreteObjectKeyFrame(Loc.T("準備就緒"), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
            IntroStatus.BeginAnimation(TextBlock.TextProperty, status);

            // 背景飄浮光點
            double w = ActualWidth > 100 ? ActualWidth : 1280, h = ActualHeight > 100 ? ActualHeight : 860;
            for (int i = 0; i < 46; i++)
            {
                double size = 1.5 + rnd.NextDouble() * 2.2, x = rnd.NextDouble() * w, y = h * (0.35 + rnd.NextDouble() * 0.65);
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = size, Height = size, Opacity = 0,
                    Fill = new SolidColorBrush(rnd.Next(3) == 0 ? Color.FromRgb(0xF8, 0xE7, 0xD2) : Color.FromRgb(0xB1, 0x97, 0xFF)),
                };
                System.Windows.Controls.Canvas.SetLeft(dot, x);
                System.Windows.Controls.Canvas.SetTop(dot, y);
                IntroDust.Children.Add(dot);
                var begin = TimeSpan.FromSeconds(rnd.NextDouble() * 1.0);
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.25 + rnd.NextDouble() * 0.5, TimeSpan.FromSeconds(0.9 + rnd.NextDouble()))
                    { BeginTime = begin, AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
                dot.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, new DoubleAnimation(y, y - 90 - rnd.NextDouble() * 160, TimeSpan.FromSeconds(3.6))
                    { BeginTime = begin });
            }
        }
        catch { /* 特效失敗不影響主程式 */ }
    }

    // ---------- 音效與音樂 ----------
    private bool _eqOn;

    private void UpdateAudioUi()
    {
        var u = UiSettings.Current;
        bool muted = !u.BgmOn && !u.SfxOn;
        AudioIcon.Text = muted ? "\uE74F" : "\uE767";
        AudioLabel.Text = muted ? "已靜音" : "音效";
        AudioBtn.Tag = muted ? "muted" : null;
        AudioBtn.ToolTip = muted ? "音效與音樂(已全部關閉)- 點一下調整" : "音效與音樂設定";
        AudioEq.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;

        // 背景音樂開著時,三條等化器會跳動;只開音效時靜止
        bool eq = !muted && u.BgmOn;
        if (eq == _eqOn) return;
        _eqOn = eq;
        var bars = new[] { EqS1, EqS2, EqS3 };
        double[] secs = { 0.52, 0.78, 0.63 };
        for (int i = 0; i < bars.Length; i++)
        {
            if (eq)
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(secs[i]))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase(),
                });
            else
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bars[i].ScaleY = 0.4 + 0.15 * i;
            }
        }
    }

    // 第一次使用時,開場動畫結束後彈出提示,指向「音效」按鈕
    private static async Task CheckUpdateOnStartupAsync(int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);   // 等開場動畫與音效提示結束,避免彈窗疊在一起
            await UpdateService.CheckOnStartupAsync();
        }
        catch { }
    }

    private async Task ShowAudioHintAsync(int delayMs = 4000)
    {
        try
        {
            if (UiSettings.Current.AudioHintShown) return;
            UiSettings.Current.AudioHintShown = true;
            UiSettings.Save();
            await Task.Delay(delayMs);

            AudioHint.Visibility = Visibility.Visible;
            AudioHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
            AudioHintT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(500))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } });
            AudioService.Play("info");

            // 按鈕彈跳 3 下吸引注意
            var st = new ScaleTransform(1, 1);
            AudioBtn.RenderTransform = st;
            var bounce = new DoubleAnimation(1, 1.14, TimeSpan.FromMilliseconds(260))
                { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = new SineEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);

            await Task.Delay(8000);
            HideAudioHint();
        }
        catch { }
    }

    private void HideAudioHint()
    {
        if (AudioHint.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => AudioHint.Visibility = Visibility.Collapsed;
        AudioHint.BeginAnimation(OpacityProperty, fade);
    }

    private void AudioBtn_Click(object sender, RoutedEventArgs e)
    {
        HideAudioHint();
        AudioPopup.Show(AudioBtn, () => { UpdateAudioUi(); if (_page == "settings") SettingsPage.LoadValues(); });
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 提示訊息(Toast):不擋住操作、幾秒後自動消失 ----------
    private Brush Res(string key) => (Brush)FindResource(key);

    private async void ShowToast(string message, string kind = "info")
    {
        message = Loc.T(message);
        string snd = kind switch { "ok" => "ok", "warn" => "warn", "err" => "error", "mute" => "", _ => "info" };
        if (snd.Length > 0) AudioService.Play(snd);
        string glyph, color;
        switch (kind)
        {
            case "ok": glyph = "\uE73E"; color = "Good"; break;
            case "warn": glyph = "\uE7BA"; color = "Warn"; break;
            case "err": glyph = "\uE711"; color = "Danger"; break;
            default: glyph = "\uE946"; color = "Accent"; break;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Foreground = Res(color),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = message, Foreground = Res("TextMain"), FontSize = 13, MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        });

        var slide = new TranslateTransform(0, 14);
        var card = new Border
        {
            Background = Res("CardGrad"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 20, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Child = row,
            Opacity = 0,
            RenderTransform = slide,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
        };
        ToastHost.Children.Add(card);

        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        await Task.Delay(3200);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => ToastHost.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    private static void Log(string level, string who, string message) =>
        LogStore.Append(new LogEntry { Level = level, Account = who, Message = message });

    // ---------- 鎖 ----------
    private void TryAcquire()
    {
        _mutex.Dispose();
        bool acquired = _mutex.Acquire(MutexNames);

        if (acquired)
        {
            StatusDot.Fill = (Brush)FindResource("Good");
            StatusHalo.Fill = StatusDot.Fill;
            StatLock.Text = "已準備好";
            StatLock.Foreground = StatusDot.Fill;
            MutexStatus.Text = "多開功能已準備好";
            MutexHint.Text = "狀態會自動每秒檢查，現在可以直接啟動遊戲。";
            CloseRobloxButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("Warn");
            StatusHalo.Fill = StatusDot.Fill;
            StatLock.Text = "需要檢查";
            StatLock.Foreground = StatusDot.Fill;
            MutexStatus.Text = "目前無法準備多開功能";
            MutexHint.Text = "程式正在自動檢查。如果仍無法準備好，請按下方按鈕關閉 Roblox，再重新檢查。";
            CloseRobloxButton.Visibility = Visibility.Visible;
        }
    }

    // 程式啟動後會由計時器每秒自動檢查；不需要使用者按「檢查」按鈕。
    // 只有使用者明確按下「關閉 Roblox」時，才會關閉遊戲程序。
    private void CheckAutoMutexStatus()
    {
        if (_mutex.IsHeld)
        {
            _failurePromptShown = false;
            CloseRobloxButton.Visibility = Visibility.Collapsed;
            return;
        }

        TryAcquire();
    }

    private void CloseRobloxAndRetry_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "要關閉目前所有 Roblox 遊戲視窗並重新檢查多開功能嗎？\n\n請先確認已儲存遊戲進度。",
            "關閉 Roblox 並重新檢查",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        ClientProcessService.KillAll();
        _mutex.Dispose();
        TryAcquire();
        Refresh();
        if (_mutex.IsHeld)
            ShowToast("已準備好，可以啟動遊戲。", "ok");
        else
            ShowToast("仍無法準備好，請關閉其他多開工具後稍候自動重試。", "warn");
    }

    // ---------- 狀態刷新 ----------
    private void Refresh()
    {
        var live = ClientProcessService.List();
        var pids = live.Select(r => r.Pid).ToHashSet();
        foreach (var a in _accounts)
            if (a.Pid != 0 && !pids.Contains(a.Pid)) a.Pid = 0;
        ClientCount.Text = live.Count.ToString();
        AccountCount.Text = _accounts.Count.ToString();
        StatAccounts.Text = AccountCount.Text;
        StatRunning.Text = live.Count.ToString();
        EmptyState.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 帳號資料與頭像 ----------
    private void SaveAccounts() => AccountStore.Save(_accounts.Select(a => a.Data));

    /// <summary>補齊舊帳號缺的 UserId,同步最新的顯示名稱,並載入頭像。</summary>
    private async Task LoadProfilesAsync()
    {
        try
        {
            bool changed = false;
            foreach (var a in _accounts.Where(a => a.Data.UserId == 0).ToList())
            {
                try
                {
                    var u = await RobloxAuth.GetUserAsync(AccountStore.Decrypt(a.Data.Cookie));
                    a.Data.UserId = u.Id; a.Data.Name = u.Name; a.Data.DisplayName = u.DisplayName;
                    a.RefreshProfile();
                    changed = true;
                }
                catch { }
                await Task.Delay(500);
            }

            var ids = _accounts.Select(a => a.Data.UserId).Where(i => i != 0).ToList();
            if (ids.Count == 0) { if (changed) SaveAccounts(); return; }

            var users = await RobloxPublicApi.GetUsersByIdsAsync(ids);
            foreach (var a in _accounts)
            {
                if (!users.TryGetValue(a.Data.UserId, out var u)) continue;
                if (a.Data.Name != u.Name || a.Data.DisplayName != u.Display)
                {
                    a.Data.Name = u.Name; a.Data.DisplayName = u.Display;
                    a.RefreshProfile();
                    changed = true;
                }
            }
            if (changed) SaveAccounts();

            var heads = await RobloxPublicApi.GetHeadshotsAsync(ids);
            foreach (var a in _accounts)
                if (heads.TryGetValue(a.Data.UserId, out var url)) a.SetAvatar(url);
        }
        catch { /* 頭像抓不到不影響使用 */ }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        AccountData? result = null;
        var login = new LoginWindow { Owner = this, Topmost = Topmost };
        if (login.ShowDialog() == true) result = login.Result;
        else if (login.InitFailed)
        {
            MessageBox.Show("無法開啟內建登入視窗(可能沒有安裝 WebView2 Runtime),改用手動貼上 Cookie。", "改用手動新增");
            var manual = new AddAccountWindow { Owner = this, Topmost = Topmost };
            if (manual.ShowDialog() == true) result = manual.Result;
        }
        if (result == null) return;

        var existing = _accounts.FirstOrDefault(a =>
            (result.UserId != 0 && a.Data.UserId == result.UserId) || a.Data.Name == result.Name);
        if (existing != null)
        {
            existing.Data.Cookie = result.Cookie; // 同一個帳號:更新 Cookie
            existing.Data.UserId = result.UserId;
            existing.Data.DisplayName = result.DisplayName;
            existing.RefreshProfile();
        }
        else
        {
            _accounts.Add(new AccountVm(result));
            ApplyCooldown();
            ShowToast($"已新增帳號「{(string.IsNullOrEmpty(result.DisplayName) ? result.Name : result.DisplayName)}」", "ok");
        }
        SaveAccounts();
        Refresh();
        _ = LoadProfilesAsync();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountVm acc }) return;
        if (MessageBox.Show($"要刪除帳號「{acc.Title}」嗎?\n(不會關閉已開啟的客戶端)", "刪除帳號",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _accounts.Remove(acc);
        SaveAccounts();
        Refresh();
    }

    // ---------- 遊戲清單與進入方式 ----------
    /// <summary>成功啟動後記進「繼續遊玩」。只開 Roblox / 跟隨好友沒有固定的遊戲,不記錄。</summary>
    private void RecordRecent(JoinTarget target)
    {
        if (target.Kind is JoinKind.App or JoinKind.Follow || target.PlaceId.Length == 0) return;
        RecentStore.Record(target.PlaceId, KnownGameName(target.PlaceId) ?? "");
        // 成功進入遊戲後,如果還沒收藏,問一次要不要收藏(同一個遊戲每次開程式只問一次)
        string pid = target.PlaceId;
        Dispatcher.BeginInvoke(new Action(() => _ = OfferFavoriteAsync(pid)));
    }

    private readonly HashSet<string> _favAsked = new();
    private readonly HashSet<string> _nameLookupDone = new();

    private bool IsFavorite(string placeId) => _games.Any(g => g.PlaceId == placeId);

    /// <summary>確保知道遊戲名稱(查不到就回傳 null)。</summary>
    private async Task<string?> EnsureGameNameAsync(string placeId)
    {
        var known = KnownGameName(placeId);
        if (known != null) return known;
        try { await GameCatalog.ResolveAsync(new[] { placeId }); } catch { }
        return KnownGameName(placeId);
    }

    private async Task OfferFavoriteAsync(string placeId)
    {
        if (IsFavorite(placeId) || !_favAsked.Add(placeId)) return;
        string name = await EnsureGameNameAsync(placeId) ?? $"遊戲 {placeId}";
        if (IsFavorite(placeId)) return;
        var r = MessageBox.Show($"已進入「{name}」。\n要把它加入收藏嗎?之後可以在探索遊戲的「我的收藏」一鍵進入。",
            "加入收藏", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) await AddFavoriteAsync(placeId);
    }

    private async Task AddFavoriteAsync(string placeId)
    {
        if (IsFavorite(placeId)) return;
        string name = await EnsureGameNameAsync(placeId) ?? $"遊戲 {placeId}";
        _games = GameStore.Load();
        if (!_games.Any(g => g.PlaceId == placeId))
        {
            _games.Add(new GameEntry { Name = name, PlaceId = placeId });
            GameStore.Save(_games);
        }
        UpdateGameName();
        ShowToast($"已收藏「{name}」", "ok");
    }

    private async void FavAdd_Click(object sender, RoutedEventArgs e)
    {
        string id = PlaceIdBox.Text.Trim();
        if (id.Length > 0 && IsDigits(id)) await AddFavoriteAsync(id);
    }

    /// <summary>用收藏、最近遊玩、探索頁查過的資料找遊戲名稱;都沒有就回傳 null。</summary>
    private string? KnownGameName(string placeId)
    {
        var fav = _games.FirstOrDefault(g => g.PlaceId == placeId)?.Name;
        if (!string.IsNullOrWhiteSpace(fav)) return fav;
        var recent = RecentStore.Load().FirstOrDefault(r => r.PlaceId == placeId)?.Name;
        if (!string.IsNullOrWhiteSpace(recent)) return recent;
        var cached = GameCatalog.Cached(placeId)?.Name;
        return string.IsNullOrWhiteSpace(cached) ? null : cached;
    }

    private void UpdateGameName()
    {
        string id = PlaceIdBox.Text.Trim();
        FavAddBtn.Visibility = Visibility.Collapsed;
        if (id.Length == 0) { GameNameText.Text = "← 輸入 Place ID,或按「選擇遊戲」(留空 = 只開啟 Roblox)"; return; }
        if (!IsDigits(id)) { GameNameText.Text = "Place ID 只能是數字"; return; }

        string? name = KnownGameName(id);
        if (name != null)
        {
            bool fav = IsFavorite(id);
            GameNameText.Text = fav ? name : $"{name}(未收藏)";
            FavAddBtn.Visibility = fav ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        if (_nameLookupDone.Contains(id))
        {
            GameNameText.Text = "(查不到遊戲名稱,可能不是公開遊戲)";
            FavAddBtn.Visibility = Visibility.Visible;
            return;
        }

        GameNameText.Text = "正在查詢遊戲名稱…";
        _ = LookupNameAsync(id);
    }

    private async Task LookupNameAsync(string id)
    {
        await EnsureGameNameAsync(id);
        _nameLookupDone.Add(id);
        if (PlaceIdBox.Text.Trim() == id) UpdateGameName(); // 使用者沒有在查詢期間改掉 ID 才更新
    }

    private void PlaceIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (GameNameText != null) UpdateGameName();
    }

    // ---------- 從探索頁直接進入遊戲 ----------
    /// <summary>
    /// 探索頁按下啟動:填入 Place ID,跳出選擇視窗 ——
    /// 「目前帳號」= 用電腦上已登入的 Roblox 直接進入;
    /// 「我軟體上的帳號」= 勾選帳號後依序啟動(每個間隔冷卻秒數)。
    /// </summary>
    private async Task PlayFromExploreAsync(string placeId)
    {
        _joinMode = 0;
        JoinAutoRadio.IsChecked = true;
        PlaceIdBox.Text = placeId;

        if (!_mutex.IsHeld) { ShowPage("accounts"); await BuildTargetAsync(); return; } // 會跳出「多開尚未就緒」提示

        var dlg = new LaunchChoiceWindow(placeId, _accounts.ToList());
        if (dlg.ShowDialog() != true) return;

        if (dlg.UseCurrent)
        {
            QuickLaunch_Click(this, new RoutedEventArgs());
            return;
        }

        ShowPage("accounts");
        if (_launchAllRunning)
        {
            ShowToast("目前正在批次啟動中,請稍後再試", "warn");
            return;
        }
        var target = await BuildTargetAsync();
        if (target == null) return;
        AudioService.Play("launch");
        await LaunchQueueAsync(dlg.SelectedAccounts, target);
    }

    private void PickGame_Click(object sender, RoutedEventArgs e) => ShowPage("games");

    private void ApplyJoinMode()
    {
        var ui = JoinUi[_joinMode];
        JoinHint.Text = ui.Hint;
        JoinValueBox.Visibility = ui.NeedsValue ? Visibility.Visible : Visibility.Collapsed;
        JoinValueBox.ToolTip = ui.Placeholder;
    }

    private void JoinRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // 視窗初始化時 IsChecked 會先觸發一次,略過
        if (sender is RadioButton { Tag: string t } && int.TryParse(t, out int i) && i >= 0 && i < JoinUi.Length)
        {
            _joinMode = i;
            JoinValueBox.Text = "";
            ApplyJoinMode();
        }
    }

    private static bool IsDigits(string s) => s.All(c => c is >= '0' and <= '9');

    /// <summary>依畫面設定組出要加入的目標;格式不對會跳提示並回傳 null。</summary>
    private async Task<JoinTarget?> BuildTargetAsync()
    {
        if (!_mutex.IsHeld)
        {
            MessageBox.Show("請先確認左側「多開狀態」顯示已就緒，再啟動遊戲。", "多開功能尚未就緒");
            return null;
        }
        string placeId = PlaceIdBox.Text.Trim();
        string val = JoinValueBox.Text.Trim();
        if (!IsDigits(placeId))
        {
            MessageBox.Show("Place ID 只能是數字,或留空只開啟 Roblox。", "Place ID");
            return null;
        }

        switch (_joinMode)
        {
            case 0:
                return placeId.Length == 0 ? JoinTarget.App : new JoinTarget(JoinKind.Game, placeId);

            case 1:
                if (placeId.Length == 0 || !Guid.TryParse(val, out var job))
                {
                    MessageBox.Show("指定伺服器需要填 Place ID,以及 GUID 格式的 JobId。", "指定伺服器");
                    return null;
                }
                return new JoinTarget(JoinKind.GameJob, placeId, JobId: job.ToString());

            case 2:
                if (!LinkParser.TryParse(val, out var link) || link == null)
                {
                    MessageBox.Show("看不懂這個連結。請貼含 privateServerLinkCode 的私人伺服器連結,或 roblox.com/share 分享連結。", "私人伺服器");
                    return null;
                }
                if (link.LinkCode.Length > 0)
                    return new JoinTarget(JoinKind.Private, link.PlaceId, LinkCode: link.LinkCode);
                try
                {
                    var first = _accounts.FirstOrDefault();
                    if (first == null) { MessageBox.Show("解析分享連結需要至少一個帳號,請先新增帳號。", "私人伺服器"); return null; }
                    var (pid, code) = await RobloxAuth.ResolveShareLinkAsync(AccountStore.Decrypt(first.Data.Cookie), link.ShareCode!);
                    if (!IsDigits(pid) || pid.Length == 0 || !SafeCode.IsMatch(code)) throw new Exception("回傳資料格式不正確");
                    return new JoinTarget(JoinKind.Private, pid, LinkCode: code);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("解析分享連結失敗:" + ex.Message, "私人伺服器");
                    return null;
                }

            case 3:
                if (val.Length == 0) { MessageBox.Show("請輸入好友的使用者名稱或 ID。", "跟隨好友"); return null; }
                if (long.TryParse(val, out long uid) && uid > 0) return new JoinTarget(JoinKind.Follow, UserId: uid);
                try
                {
                    var map = await RobloxPublicApi.GetIdsByNamesAsync(new[] { val });
                    if (map.TryGetValue(val, out long id)) return new JoinTarget(JoinKind.Follow, UserId: id);
                    MessageBox.Show($"找不到使用者「{val}」。", "跟隨好友");
                }
                catch { MessageBox.Show("查詢使用者失敗,請稍後再試。", "跟隨好友"); }
                return null;
        }
        return null;
    }

    // ---------- 啟動 ----------
    /// <summary>啟動單一帳號(失敗會自動重試),成功回傳 null,失敗回傳錯誤訊息。</summary>
    private async Task<string?> LaunchAccountAsync(AccountVm acc, JoinTarget target)
    {
        acc.Busy = true;
        acc.Progress = "排隊中…";
        await LaunchLock.WaitAsync(); // 一次只啟動一個,才能正確對應 PID
        try
        {
            string cookie;
            try { cookie = AccountStore.Decrypt(acc.Data.Cookie); }
            catch
            {
                Log("err", acc.Title, "無法解密 Cookie(可能換了 Windows 使用者)");
                return $"{acc.Title}:無法解密 Cookie(可能換了 Windows 使用者),請刪除後重新登入此帳號";
            }

            string lastErr = "未知錯誤";
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // 排隊中的帳號也要等冷卻結束,避免連按幾個「啟動」時繞過 25 秒限制
                while (_cooldownLeft > 0)
                {
                    acc.Progress = $"冷卻中,{_cooldownLeft} 秒後啟動…";
                    await Task.Delay(300);
                }
                acc.Progress = attempt == 1 ? "取得登入票證…" : $"第 {attempt}/{MaxAttempts} 次嘗試…";
                Log("info", acc.Title, $"開始啟動:{target.Describe()}(第 {attempt}/{MaxAttempts} 次)");
                int waitSec;
                try
                {
                    string ticket = await RobloxAuth.GetTicketAsync(cookie); // 票證很快過期,每次嘗試都重新取
                    var before = ClientProcessService.List().Select(r => r.Pid).ToHashSet();
                    LaunchService.LaunchWithTicket(ticket, target);

                    acc.Progress = "等待客戶端啟動…";
                    int pid = await ClientProcessService.WaitNewAsync(before, TimeSpan.FromSeconds(25));
                    if (pid != 0)
                    {
                        acc.Progress = "確認客戶端狀態…";
                        if (await ClientProcessService.StaysHealthyAsync(pid, TimeSpan.FromSeconds(10)))
                        {
                            acc.Pid = pid;
                            Log("ok", acc.Title, $"啟動成功(PID {pid})");
                            RecordRecent(target);
                            StartCooldown();
                            return null;
                        }
                        lastErr = "客戶端啟動後立刻關閉,或出現錯誤視窗";
                    }
                    else lastErr = "客戶端沒有出現";
                    waitSec = 5 + attempt * 5;
                }
                catch (RateLimitException rl)
                {
                    lastErr = rl.Message;
                    waitSec = (int)Math.Clamp(rl.RetryAfter.TotalSeconds, 15, 90);
                }
                catch (AuthExpiredException ex)
                {
                    Log("err", acc.Title, ex.Message);
                    return $"{acc.Title}:{ex.Message}";
                }
                catch (InvalidOperationException ex) // 沒裝客戶端、參數不合法:重試沒用
                {
                    Log("err", acc.Title, ex.Message);
                    return $"{acc.Title}:{ex.Message}";
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                    waitSec = 5 + attempt * 5;
                }

                Log("warn", acc.Title, $"失敗:{lastErr}");
                if (attempt == MaxAttempts) break;
                for (int s = waitSec; s > 0; s--)
                {
                    acc.Progress = $"{lastErr},{s} 秒後重試({attempt}/{MaxAttempts})";
                    await Task.Delay(1000);
                }
            }
            Log("err", acc.Title, $"放棄:{lastErr}(已自動重試 {MaxAttempts} 次)");
            return $"{acc.Title}:{lastErr}(已自動重試 {MaxAttempts} 次)";
        }
        finally
        {
            LaunchLock.Release();
            acc.Busy = false;
            acc.Progress = "";
        }
    }

    private async void LaunchRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountVm acc } || _cooldownLeft > 0) return;
        var target = await BuildTargetAsync();
        if (target == null) return;
        AudioService.Play("launch");
        var err = await LaunchAccountAsync(acc, target);
        if (err != null) MessageBox.Show(err, "啟動失敗");
        else if (_autoArrange) await ArrangeAsync(true);
    }

    private async void LaunchAll_Click(object sender, RoutedEventArgs e)
    {
        if (_cooldownLeft > 0 || _launchAllRunning) return;
        var target = await BuildTargetAsync();
        if (target == null) return;
        AudioService.Play("launch");
        await LaunchQueueAsync(_accounts.Where(a => a.CanLaunch).ToList(), target);
    }

    /// <summary>依序啟動指定的帳號(每個之間等冷卻),結束後自動排列視窗並回報結果。</summary>
    private async Task LaunchQueueAsync(List<AccountVm> queue, JoinTarget target)
    {
        _launchAllRunning = true;
        UpdateLaunchAllText();
        var errors = new List<string>();
        int ok = 0;
        try
        {
            foreach (var acc in queue)
            {
                while (_cooldownLeft > 0) await Task.Delay(250); // 等上一個帳號的倒數結束
                if (acc.Busy || acc.IsRunning) continue;
                var err = await LaunchAccountAsync(acc, target);
                if (err != null)
                {
                    errors.Add(err);
                    await Task.Delay(3000);
                }
                else ok++;
            }
        }
        finally
        {
            _launchAllRunning = false;
            UpdateLaunchAllText();
        }

        if (_autoArrange && ok > 0) await ArrangeAsync(true);
        if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors), "部分帳號啟動失敗");
        else if (ok > 0) ShowToast($"已啟動 {ok} 個帳號", "ok");
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        string placeId = PlaceIdBox.Text.Trim();
        if (!_mutex.IsHeld) { MessageBox.Show("請先確認左側「多開狀態」顯示已就緒，再啟動遊戲。", "多開功能尚未就緒"); return; }
        if (!IsDigits(placeId)) { MessageBox.Show("Place ID 只能是數字,或留空只開啟 Roblox。", "Place ID"); return; }
        try
        {
            AudioService.Play("launch");
            LaunchService.Launch(placeId);
            Log("info", "快速開啟", placeId.Length == 0 ? "只開啟 Roblox" : $"遊戲 {placeId}");
            if (placeId.Length > 0) RecordRecent(new JoinTarget(JoinKind.Game, placeId));
            ShowToast("已用目前登入的帳號啟動", "ok");
        }
        catch (Exception ex) { MessageBox.Show("啟動失敗:" + ex.Message); }
    }

    // ---------- 啟動冷卻(倒數) ----------
    /// <summary>啟動成功後開始倒數;倒數期間所有「啟動」按鈕都會鎖住並顯示剩餘秒數。</summary>
    private async void StartCooldown()
    {
        _cooldownLeft = CooldownSeconds;
        ApplyCooldown();
        if (_cooldownRunning) return; // 已經在倒數,只重設秒數
        _cooldownRunning = true;
        try
        {
            while (_cooldownLeft > 0)
            {
                await Task.Delay(1000);
                _cooldownLeft--;
                ApplyCooldown();
            }
            AudioService.Play("ready");
        }
        finally { _cooldownRunning = false; }
    }

    private void ApplyCooldown()
    {
        foreach (var a in _accounts) a.Cooldown = _cooldownLeft;
        CooldownBar.Maximum = CooldownSeconds;
        CooldownBar.Value = _cooldownLeft;
        UpdateLaunchAllText();
    }

    private void UpdateLaunchAllText()
    {
        if (_launchAllRunning)
        {
            LaunchAllBtn.Content = _cooldownLeft > 0 ? $"下一個帳號:{_cooldownLeft} 秒" : "啟動中…";
            LaunchAllBtn.IsEnabled = false;
        }
        else if (_cooldownLeft > 0)
        {
            LaunchAllBtn.Content = $"冷卻中 {_cooldownLeft} 秒";
            LaunchAllBtn.IsEnabled = false;
        }
        else
        {
            LaunchAllBtn.Content = "全部啟動";
            LaunchAllBtn.IsEnabled = true;
        }
    }

    // ---------- 視窗排列 ----------
    private void UpdateAutoArrangeText() => AutoArrangeToggle.IsChecked = _autoArrange;

    private void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        _autoArrange = !_autoArrange;
        UpdateAutoArrangeText();
    }

    private async void Arrange_Click(object sender, RoutedEventArgs e) => await ArrangeAsync(false);

    private async Task ArrangeAsync(bool quiet)
    {
        var pids = _accounts.Where(a => a.Pid != 0).Select(a => a.Pid).ToList();
        if (pids.Count == 0)
        {
            if (!quiet) ShowToast("目前沒有運行中的客戶端", "warn");
            return;
        }
        int n = 0;
        for (int i = 0; i < 8; i++) // 視窗剛開時可能還沒建立好,最多等約 12 秒
        {
            n = WindowService.Arrange(pids);
            if (n >= pids.Count) break;
            await Task.Delay(1500);
        }
        Log("info", "系統", $"已排列 {n}/{pids.Count} 個視窗");
        if (!quiet)
        {
            if (n < pids.Count) ShowToast($"只排列了 {n}/{pids.Count} 個視窗,其餘可能還在載入", "warn");
            else ShowToast($"已排列 {n} 個視窗", "ok");
        }
    }

    // ---------- 頁面切換(全部在同一個視窗內)----------
    private string _page = "accounts";

    private void ShowAccounts_Click(object sender, RoutedEventArgs e) => ShowPage("accounts");
    private void OpenTools_Click(object sender, RoutedEventArgs e) => ShowPage("tools");
    private void OpenLog_Click(object sender, RoutedEventArgs e) => ShowPage("log");
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");

    private FrameworkElement PageOf(string name) => name switch
    {
        "tools" => ToolsPage,
        "log" => LogPage,
        "settings" => SettingsPage,
        "games" => GamesPage,
        _ => AccountsPage,
    };

    private void ShowPage(string name)
    {
        if (name == _page) return;
        string prev = _page;
        _page = name;
        AudioService.Play("whoosh");

        // 離開頁面時的收尾
        if (prev == "tools") ToolsPage.OnHidden();   // 停止連點器/連按並釋放 F6、F7
        else if (prev == "log") LogPage.OnHidden();
        else if (prev == "games") { _games = GameStore.Load(); UpdateGameName(); }

        var target = PageOf(name);
        foreach (var p in new FrameworkElement[] { AccountsPage, GamesPage, ToolsPage, LogPage, SettingsPage })
            p.Visibility = ReferenceEquals(p, target) ? Visibility.Visible : Visibility.Collapsed;

        // 進入頁面時的準備
        if (name == "tools") ToolsPage.OnShown();
        else if (name == "log") LogPage.OnShown();
        else if (name == "games") GamesPage.Reload();
        else if (name == "settings") SettingsPage.OnShown();
        else if (name == "accounts") UpdateAutoArrangeText();

        // 電影感頁面轉場：柔焦解除、淡入、微幅上移與鏡頭式縮放。
        // 動畫只作用於頁面容器，不改動任何按鈕事件或遊戲功能。
        var transition = new TransformGroup();
        var slide = new TranslateTransform(0, 22);
        var scale = new ScaleTransform(0.975, 0.975);
        transition.Children.Add(scale);
        transition.Children.Add(slide);
        target.RenderTransformOrigin = new Point(0.5, 0.04);
        target.RenderTransform = transition;
        target.Opacity = 0;

        var cinematicBlur = new BlurEffect { Radius = 8 };
        target.Effect = cinematicBlur;
        cinematicBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        var clearBlurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        clearBlurTimer.Tick += (_, _) =>
        {
            clearBlurTimer.Stop();
            if (ReferenceEquals(target.Effect, cinematicBlur)) target.Effect = null;
        };
        clearBlurTimer.Start();

        UpdateNavUi();
    }

    private void UpdateNavUi()
    {
        void Set(Button b, bool on)
        {
            b.Background = on ? Res("Card2") : Brushes.Transparent;
            b.Foreground = on ? Res("TextMain") : Res("TextDim");
        }
        Set(NavAccounts, _page == "accounts");
        Set(NavGames, _page == "games");
        Set(NavTools, _page == "tools");
        Set(NavLog, _page == "log");
        Set(NavSettings, _page == "settings");
    }

    // ---------- 關閉 ----------
    private void KillRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AccountVm acc } && acc.Pid != 0)
        {
            ClientProcessService.Kill(acc.Pid);
            AudioService.Play("kill");
            Log("info", acc.Title, "已關閉客戶端");
        }
        Refresh();
    }

    private void KillAll_Click(object sender, RoutedEventArgs e)
    {
        ClientProcessService.KillAll();
        AudioService.Play("kill");
        Log("info", "系統", "已關閉所有客戶端");
        Refresh();
        ShowToast("已關閉所有客戶端", "mute");
    }

    // ---------- 帳號卡片入場動畫 ----------
    /// <summary>帳號卡片出現時依序淡入上滑(依在清單裡的位置錯開)。</summary>
    private void AccRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row || row.RenderTransform is not TranslateTransform shift) return;
        if (shift.IsFrozen) { shift = shift.Clone(); row.RenderTransform = shift; }

        int index = 0;
        if (ItemsControl.ContainerFromElement(AccountList, row) is ListBoxItem item)
            index = Math.Max(0, AccountList.ItemContainerGenerator.IndexFromContainer(item));

        var begin = TimeSpan.FromMilliseconds(Math.Min(index, 8) * 70);
        row.Opacity = 0;
        row.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(480))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
