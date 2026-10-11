using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Одна пропущенная версия в окне «Вышла новая версия»: номер, дата, итог и разделы списка изменений.</summary>
public sealed record UpdateNotesItem(string Version, string Date, string? Summary, IReadOnlyList<ReleaseNotesSection> Sections, string PageUrl,
    bool IsFirst)
{
    public bool IsEmpty => Summary is null && Sections.Count == 0;

    public static UpdateNotesItem From(AppRelease release, bool russian, bool first)
    {
        var notes = ReleaseNotes.Parse(release.Notes, russian);
        var culture = CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US");
        var date = release.Published is { } at ? "  " + at.ToLocalTime().ToString(russian ? "d MMMM" : "MMMM d", culture) : "";
        return new UpdateNotesItem(release.Version.ToString(), date, notes.Summary, notes.Sections, release.PageUrl, first);
    }
}

/// <summary>
/// «Вышла новая версия eViSTool» при запуске: слева версии и кнопки, справа что изменилось за все пропущенные версии
/// на языке интерфейса. «Обновить сейчас» ставит и сразу перезапускает, «Позже» — до следующего запуска,
/// «Пропустить эту версию» — пока не выйдет следующая.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly AboutViewModel _about;
    private readonly Action _skip;

    /// <param name="skip">Запомнить, что эту версию предлагать не надо.</param>
    public UpdateWindow(AboutViewModel about, Action skip)
    {
        InitializeComponent();
        (_about, _skip) = (about, skip);
        Title = Loc.T("updwin.title");
        OldVersion.Text = about.AppVersion;
        NewVersion.Text = about.AvailableUpdate?.Version.ToString() ?? "";
        var russian = Loc.Instance.Language == "ru";
        Versions.ItemsSource = about.MissedReleases.Select((r, i) => UpdateNotesItem.From(r, russian, i == 0)).ToList();
        _about.PropertyChanged += OnAboutChanged;
        Closed += (_, _) => _about.PropertyChanged -= OnAboutChanged;
    }

    private void OnAboutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AboutViewModel.UpdateStatus)) return;
        Status.Text = _about.UpdateStatus;
        Status.Visibility = string.IsNullOrEmpty(Status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = false;
        if (await _about.InstallAsync() is null)
        {
            // не вышло (нет сети, файл не сошёлся) — причина уже в строке состояния; можно попробовать ещё раз
            InstallButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = true;
            return;
        }
        Close();
        App.RestartAfterClose(Path.Combine(AppContext.BaseDirectory, AppUpdater.MainExe));
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _skip();
        Close();
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: > 0 } url }) Shell.OpenUrl(url);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _about.IsUpdating; // посреди установки не закрываем: файлы программы как раз подменяются
        base.OnClosing(e);
    }
}
