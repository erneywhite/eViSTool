using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.App.ViewModels;

/// <summary>Пункт уборки с галочкой: что, когда менялось, сколько весит.</summary>
public sealed partial class CleanupItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public CleanupItemViewModel(CleanupItem item, Action changed)
    {
        Item = item;
        _changed = changed;
        _isChecked = item.Default;
    }

    public CleanupItem Item { get; }
    public string Title => Item.Title;
    public string Details => (Item.ChangedUtc > DateTime.MinValue ? Item.ChangedUtc.ToLocalTime().ToString("dd.MM.yyyy") + " · " : "")
                             + WorldCopiesViewModel.SizeText(Item.Size);

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>Вид уборки: заголовок с общей галочкой (все / ни одного / часть), пояснение, пометка «осторожно», пункты.</summary>
public sealed partial class CleanupGroupViewModel : ObservableObject
{
    private bool _bulk;

    public CleanupGroupViewModel(CleanupGroup group, Action changed)
    {
        Group = group;
        Items = [.. group.Items.Select(i => new CleanupItemViewModel(i, () =>
        {
            if (!_bulk) OnPropertyChanged(nameof(IsChecked));
            changed();
        }))];
    }

    public CleanupGroup Group { get; }
    public IReadOnlyList<CleanupItemViewModel> Items { get; }
    // ключи — целиком (не склеивать из частей): по ним тест словаря проверяет, что строки есть
    public string Title => Loc.T(Group.Kind switch
    {
        CleanupKind.UnpackedMods => "clean.kind.UnpackedMods",
        CleanupKind.ServerMods => "clean.kind.ServerMods",
        CleanupKind.OldLogs => "clean.kind.OldLogs",
        CleanupKind.WorldMaps => "clean.kind.WorldMaps",
        CleanupKind.RemovedModData => "clean.kind.RemovedModData",
        CleanupKind.WorldModData => "clean.kind.WorldModData",
        CleanupKind.OrphanConfigs => "clean.kind.OrphanConfigs",
        CleanupKind.RemovedModVersions => "clean.kind.RemovedModVersions",
        CleanupKind.InstalledModVersions => "clean.kind.InstalledModVersions",
        CleanupKind.ConfigVersions => "clean.kind.ConfigVersions",
        _ => "clean.kind.Downloads",
    });

    public string Hint => Loc.T(Group.Kind switch
    {
        CleanupKind.UnpackedMods => "clean.hint.UnpackedMods",
        CleanupKind.ServerMods => "clean.hint.ServerMods",
        CleanupKind.OldLogs => "clean.hint.OldLogs",
        CleanupKind.WorldMaps => "clean.hint.WorldMaps",
        CleanupKind.RemovedModData => "clean.hint.RemovedModData",
        CleanupKind.WorldModData => "clean.hint.WorldModData",
        CleanupKind.OrphanConfigs => "clean.hint.OrphanConfigs",
        CleanupKind.RemovedModVersions => "clean.hint.RemovedModVersions",
        CleanupKind.InstalledModVersions => "clean.hint.InstalledModVersions",
        CleanupKind.ConfigVersions => "clean.hint.ConfigVersions",
        _ => "clean.hint.Downloads",
    });
    public bool IsCareful => Group.Safety == CleanupSafety.Careful;
    public string SafetyText => Loc.T(IsCareful ? "clean.careful" : "clean.safe");
    public string SizeText => WorldCopiesViewModel.SizeText(Group.Size);

    /// <summary>Пункты списком — кроме видов, где пункт всегда один и он и есть сама группа (папка целиком).</summary>
    public bool ShowItems => Group.Kind is not (CleanupKind.UnpackedMods or CleanupKind.OldLogs or CleanupKind.ConfigVersions);

    /// <summary>Все выбраны — true, ни один — false, часть — null (галочка «частично»). Установка — всем пунктам.</summary>
    public bool? IsChecked
    {
        get => Items.All(i => i.IsChecked) ? true : Items.Any(i => i.IsChecked) ? null : false;
        set
        {
            _bulk = true;
            foreach (var i in Items) i.IsChecked = value == true;
            _bulk = false;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// Окно «Уборка диска» профиля: что копят игра и eViSTool, по видам с размерами; выбранное — в корзину Windows.
/// Пока игра (или свой сервер) профиля запущена — убирать нельзя: она держит эти файлы открытыми.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly GameProfile _profile;
    private readonly string _dataDir;

    public CleanupViewModel(GameProfile profile)
    {
        _profile = profile;
        _dataDir = string.IsNullOrWhiteSpace(profile.DataDir) ? GameInstall.DefaultDataDir : profile.DataDir;
    }

    public string Heading => Loc.T("clean.heading", _profile.Name);
    public ObservableCollection<CleanupGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summary = "";

    /// <summary>Что-то уже убрано в корзину — показать «Открыть корзину».</summary>
    [ObservableProperty] private bool _hasRecycled;

    public bool IsRunning => GameProcess.IsRunning(_profile);
    public string RunningText => Loc.T(_profile.Kind == ProfileKind.Server ? "clean.serverRunning" : "clean.gameRunning");
    public bool IsEmpty => !IsBusy && Groups.Count == 0;

    private long Selected => Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Sum(i => i.Item.Size);
    public string RemoveText => Loc.T("clean.remove", WorldCopiesViewModel.SizeText(Selected));

    public async Task ScanAsync()
    {
        IsBusy = true;
        Status = Loc.T("clean.scanning");
        try
        {
            var groups = await Task.Run(() =>
            {
                var mods = ModUpdateService.ScanLocal(ProfileResolver.Resolve(_profile));
                return DiskCleanup.Scan(_profile, _dataDir, mods, AppPaths.Root, DateTime.UtcNow);
            });
            Groups.Clear();
            foreach (var g in groups) Groups.Add(new CleanupGroupViewModel(g, Changed));
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("clean.scanFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Changed();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    private void Changed()
    {
        var all = Groups.Sum(g => g.Group.Size);
        Summary = Loc.T("clean.summary", WorldCopiesViewModel.SizeText(all), WorldCopiesViewModel.SizeText(Selected));
        OnPropertyChanged(nameof(RemoveText));
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemove() => !IsBusy && Selected > 0;
    partial void OnIsBusyChanged(bool value) => RemoveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task Remove()
    {
        OnPropertyChanged(nameof(IsRunning));
        if (IsRunning)
        {
            Dialogs.Warn(RunningText);
            return;
        }
        var chosen = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Item).ToList();
        var careful = Groups.Where(g => g.IsCareful && g.Items.Any(i => i.IsChecked)).Select(g => "• " + g.Title).ToList();
        var question = Loc.T("clean.confirm", chosen.Count, WorldCopiesViewModel.SizeText(chosen.Sum(i => i.Size)))
                       + (careful.Count > 0 ? "\n\n" + Loc.T("clean.confirmCareful") + "\n" + string.Join("\n", careful) : "");
        if (Dialogs.Ask(question, System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Status = Loc.T("clean.removing");
        var (freed, problems) = await Task.Run(() => DiskCleanup.Remove(chosen, eViSTool.Core.Server.RecycleBin.Send));
        IsBusy = false;
        HasRecycled |= freed > 0;
        await ScanAsync();
        Status = Loc.T("clean.done", WorldCopiesViewModel.SizeText(freed))
                 + (problems.Count > 0 ? " " + Loc.T("clean.problems", string.Join("; ", problems)) : "");
    }

    [RelayCommand]
    private static void OpenRecycleBin() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
}
