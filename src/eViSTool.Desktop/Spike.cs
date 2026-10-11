// ВРЕМЕННО, не коммитить: проверка синхронных окон (вложенный цикл диспетчера) на Windows и Linux.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Presentation;

namespace eViSTool.Desktop;

internal static class Spike
{
    private static readonly string LogPath = Environment.GetEnvironmentVariable("EVISTOOL_SPIKE_LOG")
                                             ?? Path.Combine(AppContext.BaseDirectory, "spike.log");
    private static TextBlock? _view;
    private static bool _autoClose;

    private static void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
        File.AppendAllText(LogPath, line + Environment.NewLine);
        if (_view is not null) _view.Text = line + "\n" + _view.Text;
    }

    public static Window Create(IClassicDesktopStyleApplicationLifetime desktop, bool auto)
    {
        File.WriteAllText(LogPath, "");
        _view = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new Avalonia.Media.FontFamily(Program.MonoFont) };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(string label, Action action)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { Log("ОШИБКА: " + ex); }
            };
            buttons.Children.Add(b);
        }
        Add("Вопрос", () => Log("Ответ: " + Ui.Ask("Тестовый вопрос. Нажми любую кнопку.", AskButtons.YesNoCancel)));
        Add("Вопрос в вопросе", () =>
        {
            DispatcherTimer.RunOnce(() => Log("Внутренний: " + Ui.Ask("Внутренний вопрос (поверх первого)", AskButtons.YesNo)), TimeSpan.FromSeconds(1.5));
            Log("Внешний: " + Ui.Ask("Внешний вопрос. Через полторы секунды поверх появится второй — ответь сначала на него.", AskButtons.YesNo));
        });
        Add("Выбрать папку", () => Log("Папка: " + (Ui.Platform.PickFolder("Выбери любую папку", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "<отмена>")));
        Add("Открыть файл", () => Log("Файл: " + string.Join(", ", Ui.Platform.PickFiles("Выбери файл", [new FileFilter("Все файлы", "*.*")], multiple: false) ?? ["<отмена>"])));
        Add("Сохранить файл", () => Log("Сохранить в: " + (Ui.Platform.PickSaveFile("Куда сохранить", [new FileFilter("Модпак", "*.evpack")], "проба", null) ?? "<отмена>")));
        Add("В буфер", () => { Ui.Platform.SetClipboard("eViSTool: проверка буфера " + DateTime.Now.ToString("HH:mm:ss")); Log("Скопировано в буфер (вставь куда-нибудь)"); });
        Add("Сообщение", () => { Ui.Info("Просто сообщение с кнопкой OK."); Log("Сообщение закрыто"); });

        var window = new Window
        {
            Title = "eViSTool — проверка окон",
            Width = 760,
            Height = 480,
            Content = new DockPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { [DockPanel.DockProperty] = Dock.Top, Margin = new Thickness(0, 0, 0, 12), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = "Нажми каждую кнопку по очереди, потом закрой окно крестиком: сначала откажись («Нет»), потом закрой ещё раз и согласись." },
                    new Border { [DockPanel.DockProperty] = Dock.Top, Child = buttons },
                    new ScrollViewer { Content = _view },
                },
            },
        };
        window.Closing += (_, e) =>
        {
            var answer = Ui.Confirm("Точно закрыть окно проверки?");
            Log($"Закрытие ({e.CloseReason}): {(answer ? "да" : "нет")}");
            e.Cancel = !answer;
        };
        window.Closed += (_, _) => Log("Окно закрыто");
        if (auto) window.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = AutoAsync(desktop, window));
        Log($"Старт: {Environment.OSVersion}, {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}, {Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}, DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}");
        return window;
    }

    // ---- без человека: кнопки в окнах вопроса нажимает таймер

    private static void ClickLater(IClassicDesktopStyleApplicationLifetime desktop, double seconds, int buttonIndex, string? expectTextPart = null) =>
        DispatcherTimer.RunOnce(() =>
        {
            var dialog = desktop.Windows.LastOrDefault(w => w is Platform.MessageDialog);
            if (dialog is null) { Log($"АВТО: нет окна вопроса для нажатия #{buttonIndex}"); return; }
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
            Log($"АВТО: жму «{buttons[buttonIndex].Content}» в окне ({buttons.Count} кнопок)");
            buttons[buttonIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, TimeSpan.FromSeconds(seconds));

    private static async Task AutoAsync(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        await Task.Delay(800);
        ClickLater(desktop, 0.7, 0);
        Log("1. Простой вопрос → ждём «Да»: " + Ui.Ask("Авто 1", AskButtons.YesNo));

        ClickLater(desktop, 1.4, 1); // внутренний: «Нет»
        ClickLater(desktop, 2.4, 0); // внешний: «Да»
        DispatcherTimer.RunOnce(() => Log("2а. Внутренний → ждём «No»: " + Ui.Ask("Авто 2 внутренний", AskButtons.YesNo)), TimeSpan.FromSeconds(0.8));
        Log("2б. Внешний после внутреннего → ждём «Yes»: " + Ui.Ask("Авто 2 внешний", AskButtons.YesNo));

        ClickLater(desktop, 0.7, 0);
        Ui.Info("Авто 3: сообщение");
        Log("3. Сообщение закрыто");

        ClickLater(desktop, 0.7, 1);
        window.Close();
        Log("4. После отказа окно открыто: " + window.IsVisible);

        _autoClose = true;
        ClickLater(desktop, 0.7, 0);
        window.Close();
        Log("5. После согласия окно открыто: " + window.IsVisible + " " + _autoClose);
    }
}
