using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка каналов: имя, тип и куда уходит.</summary>
public sealed class NotifyChannelRowViewModel(NotifyChannel channel)
{
    public NotifyChannel Channel { get; } = channel;
    public string Name => Channel.Name;

    /// <summary>«Telegram · @erney», «ntfy · evistool-…», «Вебхук · hooks.example.org» — без секретов.</summary>
    public string Details => (Channel.Kind switch
    {
        NotifyKind.Telegram => "Telegram",
        NotifyKind.Discord => "Discord",
        NotifyKind.Ntfy => "ntfy",
        _ => Loc.T("notify.whKind"),
    }) + (Channel.ChatTitle is { Length: > 0 } t ? " · " + t : "");
}

/// <summary>
/// Карточка «Оповещения» в «Настройках»: каналы этого компьютера (как в Uptime Kuma — настраиваются здесь один раз,
/// у серверов потом только включаются). Канал правится в отдельном окне; «Проверить» шлёт проверочное сообщение.
/// </summary>
public sealed partial class NotifyChannelsViewModel : ObservableObject
{
    /// <summary>Общий для оповещений: короткий таймаут — проверка не должна висеть.</summary>
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ObservableCollection<NotifyChannelRowViewModel> Channels { get; } = [];
    public bool HasChannels => Channels.Count > 0;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsError;

    public NotifyChannelsViewModel() => Reload();

    private void Reload()
    {
        Channels.Clear();
        foreach (var c in NotifyChannels.Load(AppPaths.Root)) Channels.Add(new NotifyChannelRowViewModel(c));
        OnPropertyChanged(nameof(HasChannels));
    }

    private void Save() => NotifyChannels.Save(AppPaths.Root, Channels.Select(r => r.Channel));

    /// <summary>Новый канал: тип выбирается в окне.</summary>
    [RelayCommand]
    private void AddChannel() => Edit(null, NotifyKind.Telegram);

    [RelayCommand]
    private void EditChannel(NotifyChannelRowViewModel? row)
    {
        if (row is not null) Edit(row, row.Channel.Kind);
    }

    private void Edit(NotifyChannelRowViewModel? row, NotifyKind kind)
    {
        var dlg = new NotifyChannelWindow(row?.Channel ?? new NotifyChannel { Kind = kind, Name = kind.ToString() }, isNew: row is null)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true || dlg.Result is not { } result) return;
        try
        {
            var i = row is null ? -1 : Channels.IndexOf(row);
            if (i >= 0) Channels[i] = new NotifyChannelRowViewModel(result);
            else Channels.Add(new NotifyChannelRowViewModel(result));
            OnPropertyChanged(nameof(HasChannels));
            Save();
            Show(Loc.T("notify.saved", result.Name), error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void DeleteChannel(NotifyChannelRowViewModel? row)
    {
        if (row is null) return;
        if (MessageBox.Show(Application.Current.MainWindow, Loc.T("notify.deleteConfirm", row.Name), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            Channels.Remove(row);
            OnPropertyChanged(nameof(HasChannels));
            Save();
            StatusText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task TestChannel(NotifyChannelRowViewModel? row)
    {
        if (row is null) return;
        Show(Loc.T("notify.testing", row.Name), error: false);
        try
        {
            await new Notifier(Http).SendAsync(row.Channel, TestMessage());
            Show(Loc.T("notify.testSent", row.Name), error: false);
        }
        catch (NotifyException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    internal static NotifyMessage TestMessage() =>
        new(NotifySeverity.Info, null, Loc.T("notify.testTitle"),
            [new NotifyLine("◇", Loc.T("notify.lbl.from"), Loc.T("notify.testText", Environment.MachineName))]);

    private void Show(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }
}
