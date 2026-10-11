using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using eViSTool.Core.Localization;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Окно вопроса или сообщения в лесной теме — замена MessageBox из WPF (в Avalonia своего нет). Значок по смыслу,
/// кнопки по набору, кнопка по умолчанию — на Enter, Escape — «Отмена» (или «Нет», если отмены нет).
/// </summary>
internal sealed class MessageDialog : Window
{
    private Answer _answer;

    private MessageDialog(string text, IReadOnlyList<(Answer Answer, string Label)> buttons, Answer byDefault, Answer onEscape, Glyph glyph)
    {
        _answer = onEscape;
        Title = "eViSTool";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (answer, label) in buttons)
        {
            var button = new Button { Content = label, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = answer == byDefault };
            if (answer == byDefault) button.Classes.Add("primary");
            button.Click += (_, _) => { _answer = answer; Close(); };
            row.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        new Border
                        {
                            [DockPanel.DockProperty] = Dock.Left, Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                            Background = new SolidColorBrush(GlyphColor(glyph)), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                            Child = new TextBlock { Text = GlyphText(glyph), FontWeight = FontWeight.Bold, FontSize = 16, Foreground = Brush.Parse("#2C2B1C"),
                                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        },
                        new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                    },
                },
                row,
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _answer = onEscape;
            Close();
        };
    }

    private enum Glyph { Question, Info, Warning, Error }

    private static Color GlyphColor(Glyph g) => g switch
    {
        Glyph.Warning => Color.Parse("#EDBF82"),
        Glyph.Error => Color.Parse("#E89B96"),
        Glyph.Info => Color.Parse("#B0C898"),
        _ => Color.Parse("#D4BD83"),
    };

    private static string GlyphText(Glyph g) => g switch { Glyph.Question => "?", Glyph.Info => "i", _ => "!" };

    /// <summary>Вопрос: ответ — по нажатой кнопке, закрыли окно — как Escape.</summary>
    public static Task<Answer> AskAsync(Window? owner, string text, AskButtons buttons, Answer? byDefault, bool warning)
    {
        (Answer, string)[] set = buttons switch
        {
            AskButtons.YesNoCancel => [(Answer.Yes, Loc.T("common.yes")), (Answer.No, Loc.T("common.no")), (Answer.Cancel, Loc.T("common.cancel"))],
            AskButtons.OkCancel => [(Answer.Ok, "OK"), (Answer.Cancel, Loc.T("common.cancel"))],
            _ => [(Answer.Yes, Loc.T("common.yes")), (Answer.No, Loc.T("common.no"))],
        };
        var escape = buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel;
        var dialog = new MessageDialog(text, set, byDefault ?? set[0].Item1, escape, warning ? Glyph.Warning : Glyph.Question);
        return dialog.ShowAsync(owner);
    }

    /// <summary>Сообщение с одной кнопкой OK.</summary>
    public static Task<Answer> NotifyAsync(Window? owner, string text, NoticeKind kind)
    {
        var glyph = kind switch { NoticeKind.Error => Glyph.Error, NoticeKind.Warning => Glyph.Warning, _ => Glyph.Info };
        return new MessageDialog(text, [(Answer.Ok, "OK")], Answer.Ok, Answer.Ok, glyph).ShowAsync(owner);
    }

    private async Task<Answer> ShowAsync(Window? owner)
    {
        if (owner is not null) await ShowDialog(owner);
        else
        {
            // без главного окна (запуск, закрытие) — обычное окно по центру экрана
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }
        return _answer;
    }
}
