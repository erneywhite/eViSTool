using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Пункт списка миров: существующий мир, ещё не созданный (путь в конфиге, файла нет) или «Создать новый мир…».</summary>
public sealed record WorldChoice(string Label, string? Path, bool IsCreate = false);

/// <summary>
/// Выбор мира на вкладке «Мир»: миры из Saves сервера и «Создать новый мир…». Выбор прописывает путь в
/// WorldConfig.SaveFileLocation (как поле «Мир» ниже); новый мир — путь к файлу, которого ещё нет, со своим именем и
/// случайным сидом, а прежние миры остаются в Saves. Сид и стиль у каждого мира свои (записаны в его файле), поэтому
/// переключение назад ничего не ломает.
/// </summary>
public sealed partial class ServerConfigViewModel
{
    private const string WorldNamePath = "WorldConfig.WorldName";
    private const string SeedPath = "WorldConfig.Seed";

    private ServerWorldList? _worlds;
    private bool _choosing; // список перестраивается — его выбор не правка пользователя

    public ObservableCollection<WorldChoice> WorldChoices { get; } = [];

    /// <summary>Список миров прочитан: у старого агента его нет — тогда выбор скрыт, остаётся поле «Мир».</summary>
    [ObservableProperty] private bool _worldsAvailable;

    [ObservableProperty] private WorldChoice? _selectedWorld;

    /// <summary>Выбрали «Создать новый мир…» — под списком поле для имени.</summary>
    [ObservableProperty] private bool _isCreatingWorld;

    [ObservableProperty] private string _newWorldName = "";
    [ObservableProperty] private string _worldError = "";

    /// <summary>Миры сервера заново — после загрузки конфига (у удалённого — через агента).</summary>
    private async Task LoadWorldsAsync()
    {
        try
        {
            if (IsRemote)
            {
                using var cts = new CancellationTokenSource(RemoteTimeout);
                _worlds = await RemoteClient.WorldsAsync(cts.Token);
            }
            else if (Path.GetDirectoryName(_path) is { } dataDir)
            {
                _worlds = await Task.Run(() => ServerWorlds.List(dataDir));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            _worlds = null; // старый агент (404), нет связи — без выбора, поле «Мир» ниже работает как раньше
        }
        WorldsAvailable = _worlds is not null && _doc is not null;
        RefreshWorldChoices();
        UpdateWorldExists();
    }

    private string CurrentSaveFile => _doc?.Get(SaveFilePath) is Newtonsoft.Json.Linq.JValue { Value: string text } ? text.Trim() : "";

    /// <summary>Пункты списка по мирам и пути из конфига; выбранный — тот, что сейчас в конфиге.</summary>
    private void RefreshWorldChoices()
    {
        _choosing = true;
        try
        {
            WorldChoices.Clear();
            if (_worlds is null || _doc is null) return;
            var current = CurrentSaveFile;
            var found = current.Length > 0 ? ServerWorlds.Find(_worlds, current) : null;
            WorldChoice? selected = null;
            foreach (var world in _worlds.Worlds)
            {
                var label = Loc.T("srvcfg.worldItem", world.Name, Dates.Stamp(world.ModifiedUtc.ToLocalTime()),
                    WorldCopiesViewModel.SizeText(world.Size));
                var choice = new WorldChoice(label, world.Path);
                WorldChoices.Add(choice);
                if (world == found) selected = choice;
            }
            // путь из конфига без файла — мир, который сервер создаст при запуске
            if (found is null && current.Length > 0)
            {
                selected = new WorldChoice(Loc.T("srvcfg.worldPending", Path.GetFileNameWithoutExtension(current.Replace('\\', '/').Split('/')[^1])), current);
                WorldChoices.Insert(0, selected);
            }
            WorldChoices.Add(new WorldChoice(Loc.T("srvcfg.worldCreate"), null, IsCreate: true));
            SelectedWorld = selected;
        }
        finally
        {
            _choosing = false;
        }
    }

    partial void OnSelectedWorldChanged(WorldChoice? value)
    {
        if (_choosing || value is null) return;
        WorldError = "";
        if (value.IsCreate)
        {
            IsCreatingWorld = true;
            NewWorldName = "";
            // в списке остаётся мир из конфига, пока новый не создан
            Ui.Post(RefreshWorldChoices);
            return;
        }
        IsCreatingWorld = false;
        if (value.Path is not { } path || ServerWorlds.SamePath(path, CurrentSaveFile)) return;
        // список перестраиваем после выбора, а не посреди него: иначе выпадающий список теряет выделенный пункт
        _choosing = true;
        try
        {
            SetText(SaveFilePath, path);
            // у существующего мира параметры создания из конфига не читаются — вернуть их как были загружены, чтобы
            // имя и сид, заданные для нового мира, не остались в конфиге лишней правкой
            if (_worlds is { } worlds && ServerWorlds.Find(worlds, path) is not null)
                foreach (var field in _fields.Where(f => f.IsWorldCreationOnly)) field.ResetToOriginal();
        }
        finally
        {
            _choosing = false;
        }
        Ui.Post(RefreshWorldChoices);
    }

    [RelayCommand]
    private void CreateWorld()
    {
        if (_worlds is null) return;
        var (path, error) = ServerWorlds.NewWorldPath(_worlds, NewWorldName);
        if (path is null)
        {
            WorldError = error ?? "";
            return;
        }
        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        SetText(SaveFilePath, path);
        SetText(WorldNamePath, name);
        SetText(SeedPath, ""); // сид прежнего мира дал бы ту же карту — у нового случайный, если не задать свой
        IsCreatingWorld = false;
        WorldError = "";
        RefreshWorldChoices();
    }

    [RelayCommand]
    private void CancelCreateWorld()
    {
        IsCreatingWorld = false;
        WorldError = "";
    }

    /// <summary>Записать значение в поле редактора, как если бы его ввёл пользователь (пометка «изменено», сохранение).</summary>
    private void SetText(string fieldPath, string text)
    {
        if (_fields.FirstOrDefault(f => f.Path == fieldPath) is ConfigTextFieldViewModel field) field.Text = text;
    }

    private void ClearWorlds()
    {
        _worlds = null;
        WorldsAvailable = false;
        IsCreatingWorld = false;
        WorldError = "";
        _choosing = true;
        try { WorldChoices.Clear(); SelectedWorld = null; }
        finally { _choosing = false; }
    }
}
