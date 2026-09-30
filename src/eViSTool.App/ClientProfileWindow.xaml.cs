using System.IO;
using System.Windows;
using System.Windows.Controls;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using Microsoft.Win32;

namespace eViSTool.App;

/// <summary>
/// Новый клиентский профиль на основе существующего — или его клон: своя папка данных, моды общие / копия / с нуля,
/// настройки игры — копия или чистые, миры — по желанию. Пока окно открыто, план пересчитывается в фоне.
/// </summary>
public partial class ClientProfileWindow : Window
{
    private readonly GameProfile _source;
    private bool _targetEdited;   // пользователь сам поправил папку — больше не подставляем её по названию
    private bool _settingTarget;
    private int _generation;
    private string? _notice;      // итог прерванного копирования — держится до следующей правки
    private readonly System.Windows.Threading.DispatcherTimer _delay;
    private ClientClonePlan? _plan;
    private CancellationTokenSource? _copy;

    /// <summary>Готовый профиль (папка уже создана).</summary>
    public GameProfile? Result { get; private set; }

    /// <param name="source">Профиль-основа.</param>
    /// <param name="clone">true — «Клонировать…» (по умолчанию с мирами), false — «+ Клиент».</param>
    /// <param name="suggestedName">Название нового профиля по умолчанию.</param>
    public ClientProfileWindow(GameProfile source, bool clone, string suggestedName)
    {
        // план — это обход папки данных: считаем не на каждую букву, а после короткой паузы
        _delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _delay.Tick += (_, _) => { _delay.Stop(); RecalculateNow(); };
        InitializeComponent();
        _source = source;

        Title = Loc.T(clone ? "clone.title" : "newprofile.titleClient");
        Heading.Text = clone ? Loc.T("clone.heading", source.Name) : Loc.T("newprofile.titleClient");
        Intro.Text = clone ? Loc.T("cprofile.introClone") : Loc.T("cprofile.introNew", source.Name);
        GoButton.Content = Loc.T(clone ? "clone.go" : "newprofile.create");

        var modDirs = ProfileResolver.Resolve(source).ModDirs;
        SharedModsHint.Text = Loc.T("cprofile.modsSharedHint", source.Name, modDirs.Count == 0 ? "—" : string.Join("; ", modDirs));
        if (modDirs.Count == 0)
        {
            // у основы модов нет вовсе (игра ещё не запускалась) — делить и копировать нечего
            SharedMods.IsEnabled = CopyMods.IsEnabled = false;
            EmptyMods.IsChecked = true;
        }

        NameBox.Text = suggestedName;
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            // миры бывают большими — размер считаем в фоне
            var bytes = await Task.Run(() => WorldBytes(source.DataDir));
            if (bytes <= 0) return;
            WorldsBox.Content = Loc.T("cprofile.worlds", Sizes.Format(bytes));
            WorldsBox.Visibility = Visibility.Visible;
            WorldsBox.IsChecked = clone;
        };
        RecalculateNow();
    }

    private static long WorldBytes(string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return 0;
        long sum = 0;
        foreach (var dir in ClientProfileCloner.WorldFolders(dataDir))
        {
            try
            {
                sum += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sum;
    }

    private ClientCloneOptions Options => new()
    {
        Name = NameBox.Text.Trim(),
        TargetDir = TargetBox.Text.Trim(),
        Mods = CopyMods.IsChecked == true ? ClientModsMode.Copy : EmptyMods.IsChecked == true ? ClientModsMode.Empty : ClientModsMode.Shared,
        CopySettings = CopySettings.IsChecked == true,
        CopyWorlds = WorldsBox.IsChecked == true,
    };

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (!_targetEdited)
        {
            _settingTarget = true;
            var home = string.IsNullOrWhiteSpace(_source.DataDir) ? GameInstall.DefaultDataDir : _source.DataDir;
            TargetBox.Text = NameBox.Text.Trim().Length == 0 ? "" : ClientProfileLayout.SuggestDir(home, NameBox.Text);
            _settingTarget = false;
        }
        Recalculate();
    }

    private void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (!_settingTarget) _targetEdited = true;
        Recalculate();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Recalculate();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var start = TargetBox.Text.Trim();
        while (start.Length > 0 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        var dlg = new OpenFolderDialog { Title = Loc.T("clone.pickTarget"), InitialDirectory = start };
        if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FolderName;
    }

    /// <summary>Что-то поменяли: прежний план не годится, новый посчитаем после паузы.</summary>
    private void Recalculate()
    {
        _notice = null;
        _plan = null;
        _generation++;
        GoButton.IsEnabled = false;
        _delay.Stop();
        _delay.Start();
    }

    /// <summary>Пересчитать план в фоне; устаревший ответ (условия успели поменяться) отбрасывается.</summary>
    private async void RecalculateNow()
    {
        if (_copy is not null) return;
        var generation = ++_generation;
        var options = Options;
        _plan = null;
        GoButton.IsEnabled = false;

        ClientClonePlan? plan = null;
        string? error = null;
        var running = false;
        try
        {
            (plan, running) = await Task.Run(() => (ClientProfileCloner.Plan(_source, options), options.CopyWorlds && GameProcess.IsRunning(_source)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Newtonsoft.Json.JsonException)
        {
            error = ex.Message;
        }
        if (generation != _generation) return;

        // открытый в игре мир копировать нельзя; без миров запущенная игра не мешает
        RunningBox.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (plan is null)
        {
            Summary.Text = Loc.T("cprofile.summarySkipped");
            ShowError(_notice ?? error);
            return;
        }

        var free = FreeSpace(options.TargetDir);
        Summary.Text = Loc.T("clone.summary", plan.Files.Count, Sizes.Format(plan.TotalBytes), free is { } f ? Sizes.Format(f) : "—")
                       + " " + Loc.T("cprofile.summarySkipped");
        var noSpace = free is { } available && available < plan.TotalBytes;
        ShowError(noSpace ? Loc.T("clone.noSpace") : _notice);
        _plan = plan;
        GoButton.IsEnabled = !noSpace && !running;
    }

    private void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static long? FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // сетевой путь и т.п. — не знаем, не мешаем
        }
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || _copy is not null) return;

        _copy = new CancellationTokenSource();
        Form.IsEnabled = false;
        GoButton.IsEnabled = false;
        ShowError(null);
        ProgressPanel.Visibility = Visibility.Visible;
        var total = Math.Max(1, plan.TotalBytes);
        var progress = new Progress<CloneProgress>(p =>
        {
            Progress.Value = (double)p.DoneBytes / total;
            ProgressText.Text = Loc.T("clone.copying", p.File, Sizes.Format(p.DoneBytes), Sizes.Format(p.TotalBytes));
        });

        try
        {
            Result = await Task.Run(() => ClientProfileCloner.ApplyAsync(plan, progress, _copy.Token));
            _copy = null;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Finish(Loc.T("clone.cancelled"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            Finish(Loc.T("clone.failed", ex.Message));
        }
    }

    /// <summary>Копирование прервано: вернуть форму, показать причину.</summary>
    private void Finish(string message)
    {
        _copy?.Dispose();
        _copy = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        Form.IsEnabled = true;
        _notice = message;
        RecalculateNow();
    }

    // «Отмена» во время копирования — прервать его (окно остаётся), иначе — закрыть
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_copy is not null) _copy.Cancel();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_copy is null) return;
        _copy.Cancel();
        e.Cancel = true; // дождаться, пока копирование остановится и уберёт за собой
    }
}
