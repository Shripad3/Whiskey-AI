using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Alfred.Windows.UI;

/// <summary>The pill's look: near-black glass, white text, one accent. Built in code so every property is type-checked.</summary>
internal static class Theme
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x12, 0x16)));
    public static readonly Brush Surface = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x2A)));
    public static readonly Brush SurfaceHover = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x3B)));
    public static readonly Brush Foreground = Brushes.White;
    public static readonly Brush Secondary = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0xFA)));
    public static readonly Brush Success = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)));
    public static readonly Brush Danger = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57)));
    public static readonly Brush Warning = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)));

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static Effect Shadow => new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };

    /// <summary>Segoe Fluent / MDL2 glyphs.</summary>
    public static class Glyph
    {
        public const string Microphone = "", Check = "", Error = "", Warning = "", Clock = "", Ringer = "";
        public const string Folder = "", Document = "", Photo = "", Video = "", Music = "", Globe = "";
        public const string Search = "", Calendar = "", Volume = "", Brightness = "", Lock = "", Apps = "";
        public const string Weather = "", Chat = "", Stopwatch = "", Note = "", Clipboard = "", Window = "";
        public const string Undo = "", Play = "", Timer = "", Reminder = "", Settings = "", Table = "";
    }

    public static string ToolGlyph(string tool) => tool switch
    {
        "openApp" => Glyph.Apps,
        "openURL" => Glyph.Globe,
        "webSearch" => Glyph.Search,
        "playYouTube" => Glyph.Video,
        "controlSpotify" => Glyph.Music,
        "systemControl" => Glyph.Volume,
        "getWeather" => Glyph.Weather,
        "timer" or "stopwatch" => Glyph.Stopwatch,
        "alarm" => Glyph.Ringer,
        "reminder" => Glyph.Reminder,
        "currentTime" => Glyph.Clock,
        "calendar" => Glyph.Calendar,
        "arrangeWindow" => Glyph.Window,
        "clipboard" => Glyph.Clipboard,
        "takeNote" => Glyph.Note,
        "undoFileChange" => Glyph.Undo,
        "openFile" or "findFiles" => Glyph.Document,
        "organiseFiles" => Glyph.Folder,
        _ => Glyph.Check,
    };

    public static string ItemGlyph(string symbol) => symbol switch
    {
        "photo" => Glyph.Photo,
        "video" => Glyph.Video,
        "audio" => Glyph.Music,
        "folder" => Glyph.Folder,
        "table" => Glyph.Table,
        _ => Glyph.Document,
    };

    public static TextBlock Icon(string glyph, Brush? brush = null, double size = 16) => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, Foreground = brush ?? Foreground,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
    };

    public static TextBlock Label(string text, double size = 14, Brush? brush = null, bool wrap = true, FontWeight? weight = null) => new()
    {
        Text = text, FontFamily = Text, FontSize = size, Foreground = brush ?? Foreground, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, MaxHeight = wrap ? 64 : double.PositiveInfinity,
    };

    /// <summary>A flat pill-style button that works without taking focus.</summary>
    public static Button Button(string text, Action click, bool primary = false)
    {
        var label = Label(text, 13, primary ? Brushes.Black : Foreground, wrap: false, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var border = new Border
        {
            Background = primary ? Accent : Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 6, 14, 6), Child = label,
        };
        var button = new Button { Content = border, Template = BareTemplate(), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        button.MouseEnter += (_, _) => border.Opacity = 0.85;
        button.MouseLeave += (_, _) => border.Opacity = 1;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A result row: icon, title, detail. Clicking goes back through the coordinator.</summary>
    public static Button Row(string glyph, string title, string detail, Action? click)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icon(glyph, Secondary, 14);
        var name = Label(title, 13, wrap: false);
        var info = Label(detail, 12, Secondary, wrap: false);
        info.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(info, 2);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(info);
        var border = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Child = grid };
        var button = new Button { Content = border, Template = BareTemplate(), Focusable = false, IsHitTestVisible = click is not null, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (click is not null)
        {
            button.Cursor = System.Windows.Input.Cursors.Hand;
            button.MouseEnter += (_, _) => border.Background = SurfaceHover;
            button.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            button.Click += (_, _) => click();
        }
        return button;
    }

    private static ControlTemplate BareTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        template.VisualTree = presenter;
        return template;
    }
}
