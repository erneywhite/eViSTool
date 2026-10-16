using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Профили главного окна: список, активный (переключатель в шапке) и редактируемый в «Настройках», связи и копии
/// миров редактируемого. Окна и файлы при добавлении, клонировании и удалении — в <see cref="ProfileActions"/>.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    /// <summary>Профиль, с которым сейчас работаем (переключатель в шапке).</summary>
    private ProfileViewModel? _activeProfile;

    /// <summary>
    /// Активный профиль. Перед сменой — правки конфига сервера: «Сохранить? Да / Нет / Отмена»; отмена или неудачное
    /// сохранение оставляют прежний профиль (и список выбора возвращается на него).
    /// </summary>
    public ProfileViewModel? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value == _activeProfile) return;
            if ((Server is not null && !Server.Config.ConfirmSwitch()) || (ModConfig is not null && !ModConfig.ConfirmLeave()))
            {
                ProfileSwitchDeclined?.Invoke(); // список уже показывает новый выбор — окно вернёт его на прежний
                return;
            }
            if (SetProperty(ref _activeProfile, value)) OnActiveProfileChanged(value);
        }
    }

    /// <summary>Профиль, открытый в редакторе на вкладке «Настройки».</summary>
    [ObservableProperty] private ProfileViewModel? _editedProfile;

    /// <summary>Связи редактируемого профиля: в какие профили ставить моды заодно.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LinkChoice> LinkChoices { get; } = [];

    public bool HasLinkChoices => LinkChoices.Count > 0;

    /// <summary>Пересобрать список связей (сменили редактируемый профиль, добавили/удалили профиль, запомнили выбор).</summary>
    public void RefreshLinks()
    {
        LinkChoices.Clear();
        if (EditedProfile is { } edited)
            foreach (var other in Profiles.Where(p => p != edited))
                LinkChoices.Add(new LinkChoice(other.Name, other.KindText, edited.Model.IsLinkedTo(other.Model), linked =>
                {
                    edited.Model.SetLinked(other.Model, linked);
                    Save();
                }));
        OnPropertyChanged(nameof(HasLinkChoices));
    }

    /// <summary>Копии одиночных миров редактируемого профиля (только у игрового профиля).</summary>
    [ObservableProperty] private WorldCopiesViewModel? _worldCopies;

    partial void OnEditedProfileChanged(ProfileViewModel? value)
    {
        NotifyEditedDefaultServer();
        RefreshLinks();
        CleanupDiskCommand.NotifyCanExecuteChanged();
        WorldCopies = value?.Model.Kind == ProfileKind.Client ? new WorldCopiesViewModel(value.Model) : null;
    }

    /// <summary>«Уборка диска» профиля (файлы на этой машине; у удалённого сервера — пока нет).</summary>
    [RelayCommand(CanExecute = nameof(CanCleanupDisk))]
    private void CleanupDisk(ProfileViewModel? profile)
    {
        if (profile is null) return;
        AppWindows.Current.ShowCleanup(profile.Model);
        WorldCopies?.Refresh();
    }

    private static bool CanCleanupDisk(ProfileViewModel? profile) => profile is { Model.IsRemote: false };

    /// <summary>Сделаны новые копии миров — обновить список в настройках, если открыт этот профиль.</summary>
    public void RefreshWorldCopies(GameProfile profile)
    {
        if (EditedProfile?.Model == profile) WorldCopies?.Refresh();
    }

    /// <summary>Смену профиля отменили (правки конфига) — выбор в списке надо вернуть на текущий профиль.</summary>
    public event Action? ProfileSwitchDeclined;

    private void OnActiveProfileChanged(ProfileViewModel? value)
    {
        _settings.ActiveProfileId = value?.Model.Id;
        Save();
        NotifyPlay();
        Mods.OnProfileSwitched();
        Server.OnProfileSwitched();
        LoadErrorReport();
        if (SelectedTab == AppTab.ModConfig) _ = ModConfig.LoadAsync();
    }

    private void OnProfileChanged(ProfileViewModel profile)
    {
        Save();
        if (profile == ActiveProfile)
        {
            NotifyPlay(); // имя, тип или папка данных профиля — в кнопке «Играть» и её подсказке
            Mods.OnProfileSwitched();
            Server.OnProfileSwitched();
        }
    }

    [RelayCommand]
    private void AddClientProfile()
    {
        if (_profileActions.NewClient() is { } created) AddProfile(created);
    }

    [RelayCommand]
    private void AddServerProfile()
    {
        if (_profileActions.NewServer() is { } created) AddProfile(created);
    }

    private void AddProfile(GameProfile model)
    {
        _settings.Profiles.Add(model);
        var vm = new ProfileViewModel(model, OnProfileChanged);
        Profiles.Add(vm);
        EditedProfile = vm;
        RefreshLinks();
        Save();
    }

    /// <summary>
    /// Клон профиля со своей папкой данных: серверного — «тот же мир» или «новый мир»,
    /// клиентского — с общими или своими модами, с настройками и мирами по выбору.
    /// </summary>
    [RelayCommand]
    private async Task CloneProfile(ProfileViewModel? profile)
    {
        if (await _profileActions.CloneAsync(profile) is { } clone) AddProfile(clone);
    }

    [RelayCommand]
    private async Task RemoveProfile(ProfileViewModel? profile)
    {
        if (profile is null || Profiles.Count <= 1) return; // последний профиль не удаляем
        var model = profile.Model;
        // удаляем активный — сначала правки его конфига (отмена — профиль не удаляем)
        if (profile == ActiveProfile && !Server.Config.ConfirmSwitch()) return;

        // работающий сервер, вопрос о папке данных, Корзина и файлы агента — там; отменили или не вышло — профиль остаётся
        if (!await _profileActions.PrepareRemoveAsync(profile)) return;

        _settings.Profiles.Remove(profile.Model);
        Profiles.Remove(profile);
        if (ActiveProfile == profile) ActiveProfile = Profiles[0];
        if (EditedProfile == profile) EditedProfile = ActiveProfile;
        foreach (var p in _settings.Profiles) p.LinkedProfiles.RemoveAll(id => id == model.Id); // связи с удалённым — не нужны
        RefreshLinks();
        Save();
    }
}
