using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Куда можно скопировать схему: другой профиль и действие «скопировать туда».</summary>
public sealed record SchematicTarget(string Name, IRelayCommand Command);

/// <summary>Строка списка построек: имя без «.json», размер и время, куда скопировать.</summary>
public sealed class SchematicRow(SchematicEntry entry, IReadOnlyList<SchematicTarget> targets)
{
    public SchematicEntry Entry { get; } = entry;
    public string Name => Path.GetFileNameWithoutExtension(Entry.Name);
    public string Details => string.Join(" · ", new[]
    {
        Entry.Info is { SizeX: > 0 } i ? Loc.T("schem.blocks", i.SizeX, i.SizeY, i.SizeZ) : null,
        WorldCopiesViewModel.SizeText(Entry.Size),
        Entry.ModifiedUtc > DateTime.MinValue ? Dates.Stamp(Entry.ModifiedUtc.ToLocalTime()) : null,
    }.Where(s => s is not null));

    /// <summary>«Моды: millwright, primitivesurvival» — без них блоки этих модов не встанут; пусто — только блоки игры или не читали.</summary>
    public string ModsText => Entry.Info is { Mods.Count: > 0 } i ? Loc.T("schem.mods", string.Join(", ", i.Mods)) : "";

    public bool HasMods => ModsText.Length > 0;
    public IReadOnlyList<SchematicTarget> Targets { get; } = targets;
    public bool HasTargets => Targets.Count > 0;
}

/// <summary>
/// Карточка «Постройки» в «Профилях»: схемы WorldEdit профиля («/we export» в его мирах) — скопировать в другой
/// профиль (там «/we import»), удалить, открыть папку. Профиль — на этой машине, удалённый сервер или сервер у хостера.
/// </summary>
public sealed partial class SchematicsViewModel : ObservableObject
{
    private readonly GameProfile _profile;
    private readonly Func<IEnumerable<GameProfile>> _profiles;
    private ModTarget? _target;
    private ISchematicSource? _source;

    public SchematicsViewModel(GameProfile profile, Func<IEnumerable<GameProfile>> profiles)
    {
        _profile = profile;
        _profiles = profiles;
    }

    public ObservableCollection<SchematicRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0 && !IsLoading && string.IsNullOrEmpty(Error);

    /// <summary>Папка на этой машине есть — кнопка «Открыть папку» (у удалённого сервера и хостера её нет).</summary>
    public bool CanOpenFolder => _source?.LocalFolder is not null;

    /// <summary>Список прочитан — можно добавлять файлы (пока читается или нет связи — некуда).</summary>
    public bool CanAdd => _source is not null && !IsLoading && string.IsNullOrEmpty(Error);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CanAdd))] private bool _isLoading;

    /// <summary>Список не прочитать: нет связи, старый агент, код не расшифровать.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CanAdd))] private string _error = "";

    [ObservableProperty] private string _status = "";

    /// <summary>Команда, которой поставить только что скопированную постройку («/we import дом»); клик — в буфер обмена.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportCommand))] private string _importCommand = "";

    public bool HasImportCommand => ImportCommand.Length > 0;

    /// <summary>Команду только что скопировали — подпись «Скопировано» рядом на пару секунд.</summary>
    [ObservableProperty] private bool _commandCopied;

    [RelayCommand]
    private async Task CopyImportCommandAsync()
    {
        if (!HasImportCommand) return;
        Ui.Platform.SetClipboard(ImportCommand);
        CommandCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        CommandCopied = false;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = "";
        Rows.Clear();
        try
        {
            _target = ModTarget.For(_profile);
            if (_target is null)
            {
                Error = ModTarget.UnreadableText(_profile);
                return;
            }
            _source = SchematicSources.For(_target);
            OnPropertyChanged(nameof(CanOpenFolder));
            var targets = Targets();
            foreach (var e in await _source.ListAsync())
                Rows.Add(new SchematicRow(e, [.. targets.Select(p => new SchematicTarget(p.Name, new AsyncRelayCommand(() => CopyAsync(e, p))))]));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Error = Describe(_target, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Другие профили, куда есть смысл копировать: не этот и не профиль с той же папкой данных.</summary>
    private List<GameProfile> Targets()
    {
        var own = _profile.IsLocal ? FullDataDir(_profile) : null;
        return [.. _profiles().Where(p => p.Id != _profile.Id && !(own is not null && p.IsLocal && FullDataDir(p) == own))];
    }

    private static string FullDataDir(GameProfile p)
    {
        var dir = string.IsNullOrWhiteSpace(p.DataDir) ? Core.Game.GameInstall.DefaultDataDir : p.DataDir;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)).ToUpperInvariant();
    }

    private async Task CopyAsync(SchematicEntry entry, GameProfile to)
    {
        if (_source is null) return;
        var name = Path.GetFileNameWithoutExtension(entry.Name);
        if (ModTarget.For(to) is not { } target)
        {
            Ui.Warn(ModTarget.UnreadableText(to));
            return;
        }
        ImportCommand = "";
        Status = Loc.T("schem.copying", name, to.Name);
        try
        {
            var destination = SchematicSources.For(target);
            var there = await destination.ListAsync();
            if (there.Any(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                && !Ui.Confirm(Loc.T("schem.replaceConfirm", name, to.Name)))
            {
                Status = "";
                return;
            }
            var content = await _source.ReadAsync(entry.Name);
            // блоки модов, которых там нет, встанут пустотой — сказать до копирования, каких не хватает
            if (SchematicInfo.Parse(content) is { Mods.Count: > 0 } info)
            {
                Status = Loc.T("schem.checkingMods", to.Name);
                var (_, mods) = await ModTargets.ScanAsync(target);
                if (info.MissingIn(mods) is { Count: > 0 } missing
                    && !Ui.Confirm(Loc.T("schem.missingModsConfirm", name, to.Name, string.Join(", ", missing))))
                {
                    Status = "";
                    return;
                }
            }
            await destination.WriteAsync(entry.Name, content);
            Status = Loc.T("schem.copied", name, to.Name);
            ImportCommand = "/we import " + name;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "";
            Ui.Warn(Loc.T("schem.copyFailed", name, to.Name, Describe(target, ex)));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SchematicRow? row)
    {
        if (row is null || _source is null) return;
        var question = _source.LocalFolder is not null ? Loc.T("schem.deleteConfirmTrash", row.Name) : Loc.T("schem.deleteConfirm", row.Name, _profile.Name);
        if (!Ui.Confirm(question)) return;
        try
        {
            await _source.DeleteAsync(row.Entry.Name);
            ImportCommand = "";
            Status = Loc.T("schem.deleted", row.Name);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Ui.Warn(Loc.T("schem.deleteFailed", row.Name, Describe(_target, ex)));
        }
        await LoadAsync();
    }

    /// <summary>
    /// Схемы с диска (скачанные, присланные, из старой папки) — в этот профиль. Не JSON — не схема, пропускаем с
    /// предупреждением; одноимённая в профиле — с вопросом.
    /// </summary>
    [RelayCommand]
    private async Task AddFromFileAsync()
    {
        if (_source is null) return;
        var files = Ui.Platform.PickFiles(Loc.T("schem.addPick"), [new FileFilter(Loc.T("schem.addFilter"), "*" + Schematics.Extension)], multiple: true);
        if (files is not { Count: > 0 }) return;

        ImportCommand = "";
        var added = 0;
        try
        {
            var there = (await _source.ListAsync()).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var bytes = await Task.Run(() => File.ReadAllBytes(file));
                if (!Schematics.IsValidName(name) || !Schematics.LooksLikeSchematic(bytes))
                {
                    Ui.Warn(Loc.T("schem.notSchematic", name));
                    continue;
                }
                if (there.Contains(name) && !Ui.Confirm(Loc.T("schem.replaceConfirm", Path.GetFileNameWithoutExtension(name), _profile.Name))) continue;
                await _source.WriteAsync(name, bytes);
                added++;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Ui.Warn(Loc.T("schem.addFailed", Describe(_target, ex)));
        }
        if (added > 0) Status = Loc.T("schem.added", added);
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_source?.LocalFolder is not { } dir) return;
        Directory.CreateDirectory(dir); // ещё ничего не экспортировали — пусть откроется пустая, куда класть схемы
        Ui.OpenFolder(dir);
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException
        or InvalidDataException or HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException;

    private static string Describe(ModTarget? target, Exception ex) =>
        AgentHttpException.IsOldAgent(ex) ? Loc.T("schem.agentOld")
        : target is not null ? ModTargets.Describe(target, ex)
        : ex.Message;
}
