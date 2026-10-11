using System.ComponentModel;
using eViSTool.Core.Game;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Найти сервер»: dialog.findserver (общий список — с настоящего мастер-сервера игры, нужна сеть, -Wait 8),
/// dialog.findserver.expanded (то же, первая строка раскрыта) и dialog.findserver.favorites (избранное, сеть не нужна).
/// Окну нужен клиентский профиль, а у стенда — серверный: клиент собирается здесь же, на папках стенда (в них нет
/// clientsettings.json — значит, своё избранное пусто, а мастер-сервер — официальный). Ничего не записывается: записи
/// избранного кладутся прямо в список окна.
/// </summary>
internal static class FindServer
{
    /// <summary>Вкладка «Все серверы» после загрузки.</summary>
    [SnapshotDialog("findserver")]
    private static void Open(MainViewModel main) => AppWindows.Current.ShowFindServer(Model(main));

    /// <summary>Вкладка «Все серверы», первая строка раскрыта: описание сервера и его моды.</summary>
    [SnapshotDialog("findserver.expanded")]
    private static void OpenExpanded(MainViewModel main)
    {
        var model = Model(main);
        // список придёт из сети позже — раскрыть первую строку, когда загрузка закончится
        model.PropertyChanged += Expand;
        AppWindows.Current.ShowFindServer(model);

        void Expand(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FindServerViewModel.IsLoading) || model.IsLoading) return;
            model.PropertyChanged -= Expand;
            // первый сервер с модами: у раскрытой строки видны и описание, и сводка модов
            var row = model.View.Items.FirstOrDefault(r => r.Server is { Mods.Count: > 0 }) ?? model.View.Items.FirstOrDefault();
            if (row is not null) row.IsExpanded = true;
        }
    }

    /// <summary>Вкладка «Избранное»: три сервера, один с паролем; у одного строка раскрыта (описания у избранного нет).</summary>
    [SnapshotDialog("findserver.favorites")]
    private static void OpenFavorites(MainViewModel main)
    {
        var model = Model(main);
        // адреса — из TEST-NET и example.org: снимок никуда не подключается
        model.Favorites.Add(Row(model, "Дом", "192.168.1.20", null));
        model.Favorites.Add(Row(model, "Сервер друзей", "play.example.org:42420", "секрет"));
        model.Favorites.Add(Row(model, "Тестовый мир", "203.0.113.7:42421", null));
        model.Favorites[1].IsExpanded = true;
        model.IsFavoritesTab = true;
        AppWindows.Current.ShowFindServer(model);
    }

    private static ServerRowViewModel Row(FindServerViewModel model, string name, string address, string? password) =>
        new(model, null, new PlayTarget($"fav:{name}|{address}", PlayTargetKind.Favorite, name, address, password));

    /// <summary>Клиентский профиль на папках стенда (игра стенда пустая — версия игры неизвестна, «?»).</summary>
    private static FindServerViewModel Model(MainViewModel main)
    {
        var stand = main.ActiveProfile?.Model ?? throw new InvalidOperationException("Нет активного профиля стенда");
        var client = new GameProfile
        {
            Id = "snapshot-client",
            Name = "Игра",
            Kind = ProfileKind.Client,
            GameDir = stand.GameDir,
            DataDir = stand.DataDir,
        };
        return new FindServerViewModel(main, new ProfileViewModel(client, _ => { }));
    }
}
