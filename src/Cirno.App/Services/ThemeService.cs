using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Cirno.App.Services;

/// <summary>主题选择器里的一项。默认主题由应用内置，其余来自构建期注册表。</summary>
public sealed record ThemeEntry(string Id, string DisplayName, string? Description)
{
    public override string ToString() => DisplayName;
}

/// <summary>插画主题的艺术层参数（来自 ThemeInfo.json）。</summary>
public sealed class ThemeArt
{
    public BitmapImage? BackgroundDay { get; init; }
    public BitmapImage? BackgroundNight { get; init; }
    public BitmapImage? CharacterLeft { get; init; }
    public BitmapImage? CharacterRight { get; init; }

    public double OverlayOpacity { get; init; } = 0.24;
    public double CharacterOpacity { get; init; } = 0.92;
    public double LeftHeightRatio { get; init; } = 0.86;
    public double RightHeightRatio { get; init; } = 0.78;

    public BitmapImage? BackgroundFor(bool isDark) => isDark ? BackgroundNight : BackgroundDay;
}

/// <summary>
/// 主题装配：把构建期产出的 Theme.xaml 合并进 Application.Resources。
/// 默认主题不加载任何字典，直接使用 Fluent 内置资源。
/// </summary>
public sealed class ThemeService
{
    private const string DefaultThemeId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<ThemeEntry> _themes = [];
    private ResourceDictionary? _applied;

    public ThemeService()
    {
        _themes.Add(new ThemeEntry(DefaultThemeId, "默认（Fluent）", "纯 Fluent 2 + Mica，不加载任何图片"));
        LoadRegistry();
        Active = _themes[0];
    }

    public IReadOnlyList<ThemeEntry> Themes => _themes;

    public ThemeEntry Active { get; private set; }

    /// <summary>当前主题的艺术层；默认主题为 null。</summary>
    public ThemeArt? Art { get; private set; }

    public event EventHandler? Changed;

    private static string ThemesRoot => Path.Combine(AppContext.BaseDirectory, "Themes");

    /// <summary>应用主题。失败时退回默认主题，不抛出——主题问题不应拖垮启动。</summary>
    public void Apply(string? id)
    {
        var entry = _themes.FirstOrDefault(t => t.Id == id) ?? _themes[0];

        if (_applied is not null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(_applied);
            _applied = null;
        }

        Art = null;

        if (!string.Equals(entry.Id, DefaultThemeId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri($"ms-appx:///Themes/{entry.Id}/Theme.xaml"),
                };
                // 后合并的字典优先级更高，覆盖 Fluent 默认值
                Application.Current.Resources.MergedDictionaries.Add(dictionary);
                _applied = dictionary;
                Art = LoadArt(entry.Id);
            }
            catch
            {
                if (_applied is not null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(_applied);
                    _applied = null;
                }

                entry = _themes[0];
            }
        }

        Active = entry;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LoadRegistry()
    {
        var path = Path.Combine(ThemesRoot, "themes.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var registry = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(path), JsonOptions);
            foreach (var theme in registry?.Themes ?? [])
            {
                if (string.IsNullOrWhiteSpace(theme.Id) ||
                    _themes.Any(t => string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _themes.Add(new ThemeEntry(theme.Id, theme.DisplayName ?? theme.Id, theme.Description));
            }
        }
        catch
        {
            // 注册表损坏时只保留默认主题
        }
    }

    private static ThemeArt? LoadArt(string id)
    {
        var path = Path.Combine(ThemesRoot, id, "ThemeInfo.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        BitmapImage? Image(string role)
        {
            if (!root.TryGetProperty("assets", out var assets) ||
                !assets.TryGetProperty(role, out var uri) ||
                uri.GetString() is not { Length: > 0 } value)
            {
                return null;
            }

            return new BitmapImage(new Uri(value));
        }

        var overlay = 0.24;
        var character = 0.92;
        if (root.TryGetProperty("art", out var art))
        {
            if (art.TryGetProperty("overlayOpacity", out var o) && o.TryGetDouble(out var ov)) overlay = ov;
            if (art.TryGetProperty("characterOpacity", out var c) && c.TryGetDouble(out var cv)) character = cv;
        }

        var left = 0.86;
        var right = 0.78;
        if (root.TryGetProperty("charConfig", out var chars))
        {
            left = ParseRatio(chars, "character-left", left);
            right = ParseRatio(chars, "character-right", right);
        }

        return new ThemeArt
        {
            BackgroundDay = Image("background-day"),
            BackgroundNight = Image("background-night"),
            CharacterLeft = Image("character-left"),
            CharacterRight = Image("character-right"),
            OverlayOpacity = overlay,
            CharacterOpacity = character,
            LeftHeightRatio = left,
            RightHeightRatio = right,
        };
    }

    /// <summary>把 manifest 里的 "86%" 解析成 0.86。</summary>
    private static double ParseRatio(JsonElement chars, string role, double fallback)
    {
        if (!chars.TryGetProperty(role, out var config) ||
            !config.TryGetProperty("height", out var height) ||
            height.GetString() is not { Length: > 1 } text)
        {
            return fallback;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith('%') && double.TryParse(trimmed[..^1], out var percent))
        {
            return Math.Clamp(percent / 100.0, 0.1, 1.5);
        }

        return fallback;
    }

    private sealed class RegistryFile
    {
        public string? Builtin { get; set; }
        public List<RegistryTheme>? Themes { get; set; }
    }

    private sealed class RegistryTheme
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Xaml { get; set; }
        public string? Info { get; set; }
    }
}
