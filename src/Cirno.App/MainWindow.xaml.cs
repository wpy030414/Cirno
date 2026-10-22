using Cirno.App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Cirno.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _themes = new();
    private bool _isDark;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Cirno";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.Resize(new SizeInt32(1280, 820));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        _isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        ModeToggle.IsChecked = _isDark;
        ModeToggle.Content = _isDark ? "深色" : "浅色";
        ApplyElementTheme();

        ThemePicker.ItemsSource = _themes.Themes;

        // 开发便利：--theme <id> 可直接以指定主题启动，便于无人值守验证主题包
        var requestedId = ReadArgument("--theme");
        var initial = _themes.Themes.FirstOrDefault(
                          t => string.Equals(t.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                      ?? _themes.Themes[0];

        ThemePicker.SelectedItem = initial;
        _ready = true;
        ApplyTheme(initial);
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ThemePicker.SelectedItem is ThemeEntry entry)
        {
            ApplyTheme(entry);
        }
    }

    private void ApplyTheme(ThemeEntry entry)
    {
        _themes.Apply(entry.Id);
        UpdateArt();
    }

    private static string? ReadArgument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private void OnModeToggleClick(object sender, RoutedEventArgs e)
    {
        _isDark = ModeToggle.IsChecked == true;
        ModeToggle.Content = _isDark ? "深色" : "浅色";
        ApplyElementTheme();
        UpdateArt();
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => ResizeCharacters();

    private void ApplyElementTheme()
    {
        RootGrid.RequestedTheme = _isDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    /// <summary>按当前主题装配艺术层；默认主题时不加载任何图片。</summary>
    private void UpdateArt()
    {
        if (_themes.Art is not { } art)
        {
            ArtLayer.Visibility = Visibility.Collapsed;
            BackdropImage.Source = null;
            CharacterLeftImage.Source = null;
            CharacterRightImage.Source = null;
            ThemeStatusText.Text = "主题：默认（Fluent，不加载图片）";
            return;
        }

        ArtLayer.Visibility = Visibility.Visible;
        BackdropImage.Source = art.BackgroundFor(_isDark);
        BackdropImage.Opacity = art.OverlayOpacity;
        CharacterLeftImage.Source = art.CharacterLeft;
        CharacterRightImage.Source = art.CharacterRight;
        CharacterLeftImage.Opacity = art.CharacterOpacity;
        CharacterRightImage.Opacity = art.CharacterOpacity;
        ThemeStatusText.Text =
            $"主题：{_themes.Active.DisplayName} · 立绘 {art.LeftHeightRatio:P0} / {art.RightHeightRatio:P0} · 氛围层 {art.OverlayOpacity:P0}";

        ResizeCharacters();
    }

    private void ResizeCharacters()
    {
        if (_themes.Art is not { } art || ContentHost.ActualHeight <= 0)
        {
            return;
        }

        CharacterLeftImage.Height = ContentHost.ActualHeight * art.LeftHeightRatio;
        CharacterRightImage.Height = ContentHost.ActualHeight * art.RightHeightRatio;
        CharacterLeftImage.MaxWidth = ContentHost.ActualWidth * 0.45;
        CharacterRightImage.MaxWidth = ContentHost.ActualWidth * 0.45;
    }
}
