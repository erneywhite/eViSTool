using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;

namespace eViSTool.App.ViewModels;

public sealed partial class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ModUpdateService _service;

    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _statusText = "Нажми «Проверить обновления»";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private string _search = "";

    public ModsViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _service = new ModUpdateService(db);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ModRowViewModel r
            && (!OnlyIssues || r.Kind != ModStatus.UpToDate || r.IsDuplicate)
            && (Search.Length == 0
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.ModId.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnOnlyIssuesChanged(bool value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (_main.GameVersion is null)
        {
            StatusText = "Не определена версия игры — укажи папку игры в настройках.";
            return;
        }
        if (!Directory.Exists(_main.ModsDir))
        {
            StatusText = $"Папка модов не найдена: {_main.ModsDir}";
            return;
        }

        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            var results = await _service.CheckAsync(_main.ModsDir, _main.GameVersion, _main.AllowUnstable, progress, ct);

            Rows.Clear();
            foreach (var r in results) Rows.Add(new ModRowViewModel(r));

            var updates = results.Count(r => r.Status == ModStatus.UpdateAvailable);
            var issues = results.Count(r => r.Status is not (ModStatus.UpToDate or ModStatus.UpdateAvailable) || r.IsDuplicate);
            Summary = $"Модов: {results.Count} · обновлений: {updates} · требуют внимания: {issues}";
            StatusText = $"Проверено {DateTime.Now:HH:mm:ss} для игры {_main.GameVersion}";
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

    [RelayCommand]
    private static void OpenPage(ModRowViewModel? row)
    {
        if (row?.PageUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Directory.Exists(_main.ModsDir)) Process.Start(new ProcessStartInfo(_main.ModsDir) { UseShellExecute = true });
    }
}
