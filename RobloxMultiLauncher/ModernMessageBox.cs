using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RobloxMultiLauncher;

/// <summary>統一深色電競風格的提示視窗，取代外觀不一致的 Windows 系統訊息框。</summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string message) => Show(message, "提示");

    public static MessageBoxResult Show(string message, string caption) =>
        Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image,
        string? yesText = null, string? noText = null)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var dialog = new Window
        {
            Title = caption,
            Width = 440,
            MinHeight = 190,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Microsoft JhengHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(231, 239, 255))
        };

        // 錯誤 / 警告提示用專屬音效(此時略過一般的「視窗開啟」音)
        if (image == MessageBoxImage.Error) { dialog.Tag = "snd"; Services.AudioService.Play("error"); }
        else if (image == MessageBoxImage.Warning) { dialog.Tag = "snd"; Services.AudioService.Play("warn"); }

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 83, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0, 0, 0), BlurRadius = 24, ShadowDepth = 5, Opacity = .42 }
        };
        var layout = new StackPanel();
        var accent = image == MessageBoxImage.Error ? Color.FromRgb(248, 113, 113) :
                     image == MessageBoxImage.Warning ? Color.FromRgb(251, 191, 36) : Color.FromRgb(56, 189, 248);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new Border { Width = 4, Height = 24, Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) });
        header.Children.Add(new TextBlock { Text = caption, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        layout.Children.Add(header);
        layout.Children.Add(new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 0, 22) });

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        MessageBoxResult result = MessageBoxResult.None;
        void AddButton(string text, MessageBoxResult value, bool primary)
        {
            var b = new Button { Content = text, MinWidth = 94, Height = 36, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1), FontSize = 13, FontWeight = FontWeights.SemiBold };
            b.Background = primary ? new SolidColorBrush(Color.FromRgb(14, 165, 233)) : new SolidColorBrush(Color.FromRgb(30, 41, 59));
            b.Foreground = Brushes.White;
            b.BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(71, 85, 105));
            b.Click += (_, _) => { result = value; dialog.DialogResult = value != MessageBoxResult.None; dialog.Close(); };
            buttonsPanel.Children.Add(b);
        }
        if (buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel)
        {
            if (buttons == MessageBoxButton.YesNoCancel) AddButton("取消", MessageBoxResult.Cancel, false);
            AddButton(noText ?? "否", MessageBoxResult.No, false);
            AddButton(yesText ?? "是，繼續", MessageBoxResult.Yes, true);
        }
        else if (buttons == MessageBoxButton.OKCancel)
        {
            AddButton("取消", MessageBoxResult.Cancel, false);
            AddButton("確定", MessageBoxResult.OK, true);
        }
        else AddButton("知道了", MessageBoxResult.OK, true);
        layout.Children.Add(buttonsPanel);
        panel.Child = layout;
        dialog.Content = panel;
        dialog.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel; dialog.Close(); } };
        dialog.Closed += (_, _) => { if (result == MessageBoxResult.None) result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel; };
        dialog.ShowDialog();
        return result;
    }
}
