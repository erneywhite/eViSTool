using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Оповещения» раздела «Сервер» с настоящей моделью: поле «Канал Discord» держит выбор после пересборки таблицы
/// (смена языка, возврат на вкладку), тумблеры пишут в модель, а подсказка «каналов ещё нет» ведёт в «Настройки».
/// Каналы и настройки сервера лежат в папке data рядом с тестами — настоящие данные пользователя тест не трогает.
/// </summary>
public sealed class ServerNotifyTabTests : IDisposable
{
    private readonly string _profileId = "notify-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<ResourceInclude> _theme = [];
    private StyleInclude? _themeStyles;

    public void Dispose()
    {
        if (Application.Current is { } app)
        {
            foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
            if (_themeStyles is not null) app.Styles.Remove(_themeStyles);
        }
        TryDelete(ChannelsFile);
        TryDelete(ServerNotifySettings.FileFor(_profileId));
    }

    private static string ChannelsFile => NotifyChannels.FileIn(AppPaths.Root);

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }

    private static readonly NotifyChannel Telegram = new() { Id = "tg1", Name = "Telegram: админы", Kind = NotifyKind.Telegram };
    private static readonly NotifyChannel Server = new() { Id = "dc1", Name = "Discord: сервер", Kind = NotifyKind.Discord };
    private static readonly NotifyChannel Announces = new() { Id = "dc2", Name = "Discord: анонсы", Kind = NotifyKind.Discord };

    /// <summary>Лесная тема окна: вкладка берёт из неё переключатель и кисти (в тестовом приложении только Fluent).</summary>
    private void AddTheme()
    {
        var app = Application.Current!;
        foreach (var name in new[] { "ForestResources", "ForestControls" })
        {
            var include = new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri($"avares://eViSTool/Themes/{name}.axaml") };
            _theme.Add(include);
            app.Resources.MergedDictionaries.Add(include);
        }
        _themeStyles = new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
        app.Styles.Add(_themeStyles);
    }

    /// <summary>Вкладка на экране: каналы и настройки сервера записаны на диск, модель прочитала их при открытии вкладки.</summary>
    private async Task<(Window Window, ServerNotifyViewModel Model)> Open(IReadOnlyList<NotifyChannel> channels, ServerNotifySettings? settings = null)
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        AddTheme();
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
        NotifyChannels.Save(AppPaths.Root, channels);
        (settings ?? new ServerNotifySettings()).Save(_profileId);

        var model = new ServerNotifyViewModel(new ServerViewModel(null!));
        model.OnProfileSwitched(new GameProfile { Id = _profileId, Name = "Стенд", Kind = ProfileKind.Server });
        model.SetActive(true);
        Assert.True(model.Loaded);

        var window = new Window { Width = 1100, Height = 900, Content = new ServerNotifyTab { DataContext = model } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Wait.Idle(); // открытие вкладки заодно обновляет копии каналов у сервера — дать записи закончиться до проверок
        return (window, model);
    }

    private static Control Named(Window window, string automationId) =>
        window.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public async Task Chat_channel_is_picked_from_settings_and_survives_rebuild()
    {
        var (window, model) = await Open([Telegram, Server, Announces],
            new ServerNotifySettings { ChatChannelId = "dc2", Routes = new() { [NotifyEvent.ServerCrashed] = ["tg1"] } });
        var combo = (ComboBox)Named(window, "ChatChannel");

        Assert.Equal("dc2", combo.SelectedValue);
        Assert.Equal("dc2", model.ChatChannelId);
        Assert.Equal(["", "dc1", "dc2"], model.ChatChannels.Select(c => c.Id));

        // пересборка таблицы (язык сменили): список опций очищается и наполняется заново, выбор не должен сброситься
        model.OnLanguageChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("dc2", model.ChatChannelId);
        Assert.Equal("dc2", combo.SelectedValue);
        Assert.True(Named(window, "ChatJoins").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Picking_chat_channel_in_the_list_reaches_the_model()
    {
        var (window, model) = await Open([Telegram, Server, Announces]);
        var combo = (ComboBox)Named(window, "ChatChannel");
        Assert.Equal("", model.ChatChannelId);
        Assert.False(Named(window, "ChatJoins").IsEffectivelyVisible); // чат выключен — «кто зашёл и вышел» прячется

        combo.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("dc1", model.ChatChannelId);
        Assert.True(Named(window, "ChatJoins").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switch_in_the_table_writes_the_route()
    {
        var (window, model) = await Open([Telegram, Server]);
        var switches = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => AutomationProperties.GetName(c) is "Telegram: админы" or "Discord: сервер").ToList();
        Assert.Equal(model.Rows.Count * 2, switches.Count); // событие × канал

        switches[0].IsChecked = true; // первая строка — «Сервер упал», первый столбец — Telegram
        Assert.True(model.Rows[0].Cells[0].IsOn);

        await Wait.Until(() => ServerNotifySettings.Load(_profileId).IsOn(NotifyEvent.ServerCrashed, "tg1"));
        Assert.False(ServerNotifySettings.Load(_profileId).IsOn(NotifyEvent.ServerCrashed, "dc1"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_channels_the_hint_leads_to_settings_and_the_table_is_hidden()
    {
        var (window, model) = await Open([]);
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.OpenSettingsCommand));
        var table = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, model.Rows));

        Assert.True(open.IsEffectivelyVisible);
        Assert.False(table.IsEffectivelyVisible);
        Assert.False(Named(window, "ChatChannel").IsEffectivelyVisible); // Discord-каналов нет — выбирать нечего
        window.Close();
    }

    [AvaloniaFact]
    public async Task Status_and_error_lines_show_only_when_they_have_text()
    {
        var (window, model) = await Open([Telegram]);
        var status = Named(window, "SrvNotifyStatus");
        var error = Named(window, "SrvNotifyError");
        Assert.False(status.IsVisible);
        Assert.False(error.IsVisible);

        model.StatusText = "Отправлено";
        model.ErrorText = "Не вышло";
        Dispatcher.UIThread.RunJobs();

        Assert.True(status.IsVisible);
        Assert.True(error.IsVisible);
        window.Close();
    }
}
