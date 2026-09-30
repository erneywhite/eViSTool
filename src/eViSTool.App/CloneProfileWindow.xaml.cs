using System.IO;
using System.Windows;
using System.Windows.Controls;
using eViSTool.App.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using Microsoft.Win32;

namespace eViSTool.App;

/// <summary>
/// Клонирование серверного профиля: имя, новая папка данных, «тот же мир» или «новый мир».
/// Пока окно открыто, план пересчитывается в фоне — сколько файлов и сколько места нужно.
/// </summary>
public partial class CloneProfileWindow : Window
{
    private readonly GameProfile _source;
    private readonly IReadOnlyList<ServerConfigInfo> _configs;
    private readonly bool _serverRunning;
    private bool _targetEdited;   // пользователь сам поправил папку — больше не подставляем её по названию
    private bool _settingTarget;
    private int _generation;
    private string? _notice;      // итог прерванного копирования — держится до следующей правки
    private readonly System.Windows.Threading.DispatcherTimer _delay;
    private ClonePlan? _plan;
    private CancellationTokenSource? _copy;

    /// <summary>Готовый профиль (папка уже скопирована).</summary>
    public GameProfile? Result { get; private set; }

    public CloneProfileWindow(GameProfile source, bool serverRunning)
    {
        // план — это обход всей папки данных: считаем не на каждую букву, а после короткой паузы
        _delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _delay.Tick += (_, _) => { _delay.Stop(); RecalculateNow(); };
        InitializeComponent();
        _source = source;
        _serverRunning = serverRunning;
        _configs = ProfileCloner.FindConfigs(source.DataDir ?? "");

        Heading.Text = Loc.T("clone.heading", source.Name);
        RunningBox.Visibility = serverRunning ? Visibility.Visible : Visibility.Collapsed;
        SharedModsHint.Text = Loc.T("clone.modsSharedHint", ProfileResolver.Resolve(source).InstallDir ?? "—");

        if (_configs.Count > 1)
        {
            ConfigPanel.Visibility = Visibility.Visible;
            ConfigBox.ItemsSource = _configs.Select(c => new Choice<ServerConfigInfo>(
                Loc.T("clone.configItem", c.FileName, c.WorldName ?? "?", c.SaveExists ? Sizes.Format(c.SaveBytes) : Loc.T("clone.configNoSave")), c)).ToList();
            ConfigBox.SelectedIndex = 0;
        }

        NameBox.Text = Loc.T("clone.copySuffix", source.Name);
        WorldNameBox.Text = NameBox.Text;
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            // резервные копии бывают большими — размер считаем в фоне
            var bytes = await Task.Run(() => BackupBytes(source.DataDir));
            if (bytes > 0)
            {
                BackupsBox.Content = Loc.T("clone.backups", Sizes.Format(bytes));
                BackupsBox.Visibility = SameWorld.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                BackupsBox.Tag = true;
            }
        };
        RecalculateNow();
    }

    private static long BackupBytes(string? dataDir)
    {
        long sum = 0;
        foreach (var name in new[] { "Backups", "BackupSaves" })
        {
            try
            {
                var dir = Path.Combine(dataDir ?? "", name);
                if (Directory.Exists(dir))
                    sum += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sum;
    }

    private CloneOptions Options => new()
    {
        Name = NameBox.Text.Trim(),
        TargetDir = TargetBox.Text.Trim(),
        NewWorld = NewWorld.IsChecked == true,
        WorldName = WorldNameBox.Text.Trim(),
        IncludeBackups = BackupsBox.IsChecked == true,
        ShareMods = SharedMods.IsChecked == true,
        ConfigFile = (ConfigBox.SelectedItem as Choice<ServerConfigInfo>)?.Value.FileName ?? ProfileCloner.MainConfig,
    };

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (!_targetEdited && !string.IsNullOrWhiteSpace(_source.DataDir))
        {
            _settingTarget = true;
            TargetBox.Text = ProfileCloner.SuggestTargetDir(_source.DataDir, NameBox.Text);
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
        var fresh = NewWorld.IsChecked == true;
        WorldNamePanel.Visibility = fresh ? Visibility.Visible : Visibility.Collapsed;
        BackupsBox.Visibility = !fresh && BackupsBox.Tag is true ? Visibility.Visible : Visibility.Collapsed;
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

        ClonePlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => ProfileCloner.Plan(_source, options));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
        }
        if (generation != _generation) return;

        if (plan is null)
        {
            Summary.Text = Loc.T("clone.summarySkipped");
            ShowError(_notice ?? error);
            return;
        }

        var free = FreeSpace(options.TargetDir);
        Summary.Text = Loc.T("clone.summary", plan.Files.Count, Sizes.Format(plan.TotalBytes), free is { } f ? Sizes.Format(f) : "—")
                       + " " + Loc.T("clone.summarySkipped");
        var noSpace = free is { } available && available < plan.TotalBytes;
        ShowError(noSpace ? Loc.T("clone.noSpace") : _notice);
        _plan = plan;
        GoButton.IsEnabled = !noSpace && !_serverRunning;
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
        if (_plan is not { } ready || _copy is not null) return;
        // название мира на состав файлов не влияет — берём то, что в поле сейчас
        var plan = ready with { Options = ready.Options with { WorldName = WorldNameBox.Text.Trim() } };

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
            Result = await Task.Run(() => ProfileCloner.ApplyAsync(plan, progress, _copy.Token));
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
