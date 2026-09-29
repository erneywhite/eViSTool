using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using Microsoft.Win32;

namespace eViSTool.App.ViewModels;

public sealed partial class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ModUpdateService _service;

    // последний ответ модбазы: после локальных операций статусы не теряются
    private Dictionary<string, ModDbResult> _remote = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LocalMod> _locals = [];

    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private string _search = "";

    public ModsViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _service = new ModUpdateService(db);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ModRowViewModel r
            && (!OnlyIssues || r.NeedsAttention)
            && (Search.Length == 0
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.ModId.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnOnlyIssuesChanged(bool value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    private ResolvedProfile? Profile => _main.ActiveProfile?.Resolved;

    /// <summary>Строка под кнопками: какие папки и какая игра.</summary>
    public string ProfileInfo => Profile is { } r
        ? $"Игра: {r.GameVersion?.ToString() ?? "не найдена"}   ·   Папки модов: {string.Join("; ", r.ModDirs)}"
        : "Профиль не выбран";

    /// <summary>Сменился профиль или его настройки — перечитываем моды с диска, ответ модбазы про другой набор сбрасываем.</summary>
    public void OnProfileSwitched()
    {
        _remote = new(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ProfileInfo));
        ReloadLocal();
        StatusText = "Моды прочитаны с диска. «Проверить обновления» — сверить с модбазой.";
    }

    /// <summary>Перечитать папки модов (без сети) и перестроить таблицу с уже известными статусами.</summary>
    public void ReloadLocal()
    {
        if (_main.ActiveProfile is not { } profile) return;
        profile.Refresh(notify: false); // игра могла поменять свои настройки
        try
        {
            _locals = ModUpdateService.ScanLocal(profile.Resolved);
        }
        catch (IOException ex)
        {
            _locals = [];
            StatusText = $"Не удалось прочитать папку модов: {ex.Message}";
        }
        Rebuild();
    }

    private void Rebuild()
    {
        // без версии игры с модбазой не сравнить — покажем только локальное
        var game = Profile?.GameVersion ?? ModVersion.ParseOrNull("0.0")!;
        var remote = Profile?.GameVersion is null ? new Dictionary<string, ModDbResult>() : _remote;
        var results = UpdateChecker.Evaluate(_locals, remote, game, _main.AllowUnstable);

        Rows.Clear();
        foreach (var r in results) Rows.Add(new ModRowViewModel(r));

        var disabled = results.Count(r => r.Local.IsDisabled);
        var parts = new List<string> { $"Модов: {results.Count}", $"выключено: {disabled}" };
        if (_remote.Count > 0)
        {
            parts.Add($"обновлений: {results.Count(r => r.Status == ModStatus.UpdateAvailable)}");
            parts.Add($"требуют внимания: {Rows.Count(r => r.NeedsAttention)}");
        }
        Summary = string.Join(" · ", parts);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (Profile is null) return;

        try
        {
            ReloadLocal();
            if (Profile?.GameVersion is not { } game)
            {
                StatusText = "Не определена версия игры — укажи папку игры в настройках профиля.";
                return;
            }
            var progress = new Progress<string>(s => StatusText = s);
            _remote = await _service.FetchRemoteAsync(_locals, progress, ct);
            Rebuild();
            StatusText = $"Проверено {DateTime.Now:HH:mm:ss} для игры {game}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Проверка отменена";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
    }

    // ---------- включение / выключение ----------

    [RelayCommand]
    private void ToggleEnabled(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || Profile is not { } profile) return;

        if (GameProcess.IsRunning(profile.Profile))
        {
            // клиент при выходе перезаписывает свои настройки, сервер применит только после перезапуска
            var server = profile.Profile.Kind == ProfileKind.Server;
            var what = server
                ? "Сервер запущен. Изменение вступит в силу после перезапуска, а при остановке сервер может перезаписать свой конфиг."
                : "Игра запущена. При выходе она перезапишет свои настройки, и изменение потеряется — лучше сначала закрыть игру.";
            if (!Confirm($"{what}\n\nВсё равно {(row.IsEnabled ? "выключить" : "включить")} «{row.Name}»?"))
            {
                ReloadLocal(); // вернуть галочку как было
                return;
            }
        }

        try
        {
            ModConfigEditor.SetEnabled(profile, info, enabled: !row.IsEnabled);
            StatusText = $"«{row.Name}» {(row.IsEnabled ? "выключен" : "включён")}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error($"Не удалось изменить настройки игры:\n{ex.Message}");
        }
        ReloadLocal();
    }

    // ---------- удаление ----------

    [RelayCommand]
    private void Delete(ModRowViewModel? row)
    {
        if (row is null || Profile is not { } profile) return;

        var running = GameProcess.IsRunning(profile.Profile)
            ? "\n\nИгра/сервер сейчас запущены — файл может быть занят." : "";
        if (!Confirm($"Удалить «{row.Name}» {row.Installed}?\n\nФайл уйдёт в Корзину, его можно будет восстановить.{running}"))
            return;

        try
        {
            Shell.MoveToRecycleBin(row.Local.Path);
            // если других копий мода не осталось — чистим его и из списка выключенных
            if (row.Local.Info is { } info && _locals.Count(l => l.Info?.ModId == info.ModId) == 1)
                ModConfigEditor.Forget(profile, info);
            StatusText = $"«{row.Name}» удалён в Корзину";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Error($"Не удалось удалить «{row.Name}»:\n{ex.Message}");
        }
        ReloadLocal();
    }

    // ---------- добавление из архива ----------

    [RelayCommand]
    private void AddFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выбери мод (zip)",
            Filter = "Моды Vintage Story (*.zip)|*.zip",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    /// <summary>Установка набора zip (кнопка или перетаскивание).</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        if (Profile is not { } profile) return;

        var zips = files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0)
        {
            Error("Моды Vintage Story — это zip-архивы, а среди выбранных файлов их нет.");
            return;
        }

        if (GameProcess.IsRunning(profile.Profile)
            && !Confirm("Игра/сервер сейчас запущены: новые моды подхватятся только после перезапуска, а заменяемые файлы могут быть заняты.\n\nПродолжить?"))
            return;

        var backups = ModBackupStore.ForProfile(profile.Profile);
        var installed = new List<string>();
        var problems = new List<string>();

        foreach (var zip in zips)
        {
            try
            {
                var plan = ModInstaller.Plan(zip, profile, ModUpdateService.ScanLocal(profile));
                var info = plan.Incoming.Info!;
                var old = plan.Replaces.FirstOrDefault()?.Info?.Version;

                if (plan.IsSameVersion && !Confirm($"«{info.Name}» {info.Version} уже установлен.\n\nПереустановить?"))
                    continue;
                if (plan.IsDowngrade && !Confirm($"Установлен «{info.Name}» {old}, а ставится более старая {info.Version}.\n\nОткатиться на {info.Version}?"))
                    continue;

                var disabledSet = new HashSet<string>(profile.DisabledMods);
                var wasDisabled = plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabledSet));
                ModInstaller.Apply(plan, backups);
                // выключенный мод остаётся выключенным и в новой версии
                if (wasDisabled) ModConfigEditor.SetEnabled(profile, info, enabled: false);

                installed.Add(plan.IsReplace ? $"{info.Name}: {old} → {info.Version}" : $"{info.Name} {info.Version}");
                if (plan.MissingDependencies.Count > 0)
                    problems.Add($"{info.Name}: не хватает зависимостей — {string.Join(", ", plan.MissingDependencies)}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(zip)}: {ex.Message}");
            }
        }

        ReloadLocal();
        if (installed.Count > 0) StatusText = "Установлено: " + string.Join("; ", installed);

        if (problems.Count > 0)
        {
            var text = (installed.Count > 0 ? "Установлено:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                       + "Внимание:\n• " + string.Join("\n• ", problems);
            MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- прочее ----------

    [RelayCommand]
    private static void OpenPage(ModRowViewModel? row)
    {
        if (row?.PageUrl is { } url) Shell.OpenUrl(url);
    }

    [RelayCommand]
    private static void ShowFile(ModRowViewModel? row)
    {
        if (row is not null && Path.Exists(row.FilePath)) Shell.ShowInFolder(row.FilePath);
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Profile?.InstallDir is { } dir && Directory.Exists(dir)) Shell.OpenFolder(dir);
    }

    private static bool Confirm(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void Error(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}
