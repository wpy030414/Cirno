using System.Collections.Specialized;
using Cirno.App.Services;
using Cirno.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Cirno.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _themes = new();
    private readonly SessionViewModel _vm;
    private bool _isDark;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Cirno";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.Resize(new SizeInt32(1280, 860));

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

        var workingDirectory = ReadArgument("--cwd") ?? Environment.CurrentDirectory;
        _vm = new SessionViewModel(
            DispatcherQueue,
            workingDirectory,
            autoApprove: HasFlag("--auto-approve"),
            tracePath: ReadArgument("--trace"));
        _vm.ScrollRequested += (_, _) => ScrollToEnd();
        _vm.Items.CollectionChanged += OnItemsChanged;

        RootGrid.DataContext = _vm;
        Timeline.ItemsSource = _vm.Items;

        var requestedId = ReadArgument("--theme");
        var initial = _themes.Themes.FirstOrDefault(
                          t => string.Equals(t.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                      ?? _themes.Themes[0];

        ThemePicker.SelectedItem = initial;
        _ready = true;
        ApplyTheme(initial);
        UpdateEmptyState();

        RootGrid.Loaded += OnRootLoaded;
        Closed += OnWindowClosed;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;
        Composer.Focus(FocusState.Programmatic);
        await _vm.StartAsync();

        // 开发期自检：--prompt 可在无人值守时直接跑一轮真实会话
        var prompt = ReadArgument("--prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            Composer.Text = prompt;
            await SendAsync();

            if (int.TryParse(ReadArgument("--stop-after"), out var seconds) && seconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await _vm.InterruptAsync();
            }
        }
    }

    private static bool HasFlag(string name)
        => Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        await _vm.DisposeAsync();
    }

    // ---------- 发送与中断 ----------

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shift = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (shift)
        {
            return;
        }

        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        if (_vm.IsBusy)
        {
            return;
        }

        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Composer.Text = string.Empty;
        await _vm.SendAsync(text);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e) => await _vm.InterruptAsync();

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimelineItem item })
        {
            await _vm.ApproveAsync(item, allow: true);
        }
    }

    private async void OnDenyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimelineItem item })
        {
            await _vm.ApproveAsync(item, allow: false);
        }
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await _vm.ChangeDirectoryAsync(folder.Path);
    }

    // ---------- 时间线滚动 ----------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
        => EmptyState.Visibility = _vm.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>只在新内容到达时跟随滚动；用户回看历史时不抢滚动位置。</summary>
    private void ScrollToEnd()
    {
        if (Timeline.Items.Count == 0)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(Timeline);
        if (scrollViewer is not null &&
            scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > 240)
        {
            return;
        }

        Timeline.ScrollIntoView(Timeline.Items[^1]);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------- 主题 ----------

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
        ThemeStatusText.Text = $"主题：{_themes.Active.DisplayName}（立绘 {art.LeftHeightRatio:P0}/{art.RightHeightRatio:P0}）";

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
