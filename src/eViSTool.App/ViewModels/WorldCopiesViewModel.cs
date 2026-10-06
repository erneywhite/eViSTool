using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка копий: мир, когда снята, размер; отложенный перед возвратом мир помечен.</summary>
public sealed class WorldCopyRow(WorldCopy copy)
{
    public WorldCopy Copy { get; } = copy;
    public string Name => Copy.WorldName;
    public string Details => string.Join(" · ", new[]
    {
        Copy.IsBeforeRestore ? Loc.T("wcopy.asideLabel") : null,
        Copy.CopiedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
        WorldCopiesViewModel.SizeText(Copy.Size),
    }.Where(s => s is not null));
}

/// <summary>
/// Копии одиночных миров игрового профиля в настройках: список, «Вернуть», папка. Копии снимаются перед
/// изменением модов (<see cref="ModsViewModel"/>); здесь — только просмотр и возврат.
/// </summary>
public sealed partial class WorldCopiesViewModel : ObservableObject
{
    private readonly GameProfile _profile;
    private readonly string _dataDir;

    public WorldCopiesViewModel(GameProfile profile)
    {
        _profile = profile;
        _dataDir = string.IsNullOrWhiteSpace(profile.DataDir) ? GameInstall.DefaultDataDir : profile.DataDir;
        Refresh();
    }

    public ObservableCollection<WorldCopyRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>«2 копии · 1,4 ГБ» — сколько места занимают.</summary>
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _status = "";

    public void Refresh()
    {
        Rows.Clear();
        try
        {
            foreach (var c in WorldCopies.List(_dataDir)) Rows.Add(new WorldCopyRow(c));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        Summary = Rows.Count == 0 ? "" : Loc.T("wcopy.summary", Rows.Count, SizeText(Rows.Sum(r => r.Copy.Size)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public static string SizeText(long bytes) =>
        bytes >= 1L << 30 ? Loc.T("wcopy.sizeGb", (bytes / (double)(1L << 30)).ToString("0.0"))
        : Loc.T("wcopy.sizeMb", Math.Max(1, bytes >> 20));

    [RelayCommand]
    private async Task RestoreAsync(WorldCopyRow? row)
    {
        if (row is null) return;
        // игра держит мир открытым и при выходе записала бы его поверх возвращённого
        if (GameProcess.IsRunning(_profile))
        {
            Dialogs.Warn(Loc.T("wcopy.closeGame"));
            return;
        }
        var question = row.Copy.IsBeforeRestore
            ? Loc.T("wcopy.restoreAsideConfirm", row.Name)
            : Loc.T("wcopy.restoreConfirm", row.Name, row.Copy.CopiedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
        if (Dialogs.Ask(question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        Status = Loc.T("wcopy.restoring", row.Name);
        try
        {
            await Task.Run(() => WorldCopies.Restore(_dataDir, row.Copy));
            Status = Loc.T("wcopy.restored", row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "";
            Dialogs.Warn(Loc.T("wcopy.restoreFailed", row.Name, ex.Message));
        }
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = WorldCopies.DirFor(_dataDir);
        if (Directory.Exists(dir)) Shell.OpenFolder(dir);
    }
}
