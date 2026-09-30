using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.App.ViewModels;

/// <summary>Карточка выбранного мода во вкладке «Мои моды»: что за мод, что с ним, что можно сделать.</summary>
public sealed partial class ModCardViewModel : ObservableObject
{
    public ModRowViewModel Row { get; }
    public ModsViewModel Owner { get; }

    /// <summary>Коротко о моде (из modinfo.json; нет — начало описания с модбазы).</summary>
    [ObservableProperty] private string _summary;

    /// <summary>Полное описание со страницы модбазы.</summary>
    [ObservableProperty] private string _fullText = "";
    [ObservableProperty] private string? _logo;
    [ObservableProperty] private string _author;
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _side;
    [ObservableProperty] private IReadOnlyList<string> _screenshots = [];
    [ObservableProperty] private string _compatibility;

    /// <summary>Вкладка «Версии» (иначе «Обзор»). Список версий грузится при первом открытии.</summary>
    [ObservableProperty] private bool _isVersionsTab;
    [ObservableProperty] private IReadOnlyList<VersionRowViewModel> _versions = [];
    [ObservableProperty] private string _versionsStatus = "";
    private bool _versionsLoaded;

    public ModCardViewModel(ModRowViewModel row, ModsViewModel owner, ModVersion? game, string profileName)
    {
        Row = row;
        Owner = owner;
        ProfileName = profileName;
        var info = row.Local.Info;
        _summary = info?.Description?.Trim() ?? "";
        _author = info?.Authors is { Count: > 0 } a ? string.Join(", ", a) : "";
        _side = SideText(info?.Side);
        _compatibility = CompatibilityText(row, game);
        if (row.Result.Remote is { } remote) Apply(remote);
    }

    public string Name => Row.Name;
    public string ProfileName { get; }
    public string ByLine => Author.Length > 0 ? Loc.T("mcard.by", Author, Row.ModId) : Row.ModId;
    public string UpdateText => Loc.T("mcard.updateTo", Row.Latest);
    public string PinText => Loc.T(Row.IsPinned ? "mcard.unpin" : "mcard.pin");
    public bool HasChangelog => !string.IsNullOrWhiteSpace(Row.Changelog);
    public string ChangelogTitle => Loc.T("mcard.whatsNew", Row.Latest);

    partial void OnAuthorChanged(string value) => OnPropertyChanged(nameof(ByLine));

    /// <summary>Модбаза уже ответила при проверке — описание, логотип и скриншоты берём оттуда.</summary>
    private void Apply(ModDbMod mod)
    {
        if (Html.ToPlainText(mod.Text) is { Length: > 0 } text)
        {
            FullText = text;
            if (Summary.Length == 0)
            {
                var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                Summary = first.Length > 240 ? first[..240].TrimEnd() + "…" : first;
            }
        }
        Logo = string.IsNullOrWhiteSpace(mod.LogoFile) ? null : mod.LogoFile;
        if (!string.IsNullOrWhiteSpace(mod.Author)) Author = mod.Author;
        Tag = mod.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        if (mod.Side is { Length: > 0 } side) Side = SideText(side);
        Screenshots = mod.Screenshots.Select(s => s.MainFile).Where(u => !string.IsNullOrWhiteSpace(u)).ToList()!;
    }

    /// <summary>Мод ещё не сверяли с модбазой — подгрузить страницу мода только для карточки.</summary>
    public async Task LoadAsync(ModDbClient db, CancellationToken ct)
    {
        if (Row.Result.Remote is not null || Row.ModId.Length == 0) return;
        try
        {
            if (await db.GetModAsync(Row.ModId, ct) is { } mod && !ct.IsCancellationRequested) Apply(mod);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // без сети — останется то, что есть в modinfo.json
        }
    }

    private static string SideText(string? side) => side?.ToLowerInvariant() switch
    {
        "client" => Loc.T("card.sideClient"),
        "server" => Loc.T("card.sideServer"),
        _ => Loc.T("card.sideBoth"),
    };

    private static string CompatibilityText(ModRowViewModel row, ModVersion? game)
    {
        if (game is null) return Loc.T("common.notFound");
        var branch = $"{game.Major}.{game.Minor}.x";
        return row.Kind switch
        {
            ModStatus.NotChecked => Loc.T("mcard.compatUnknown"),
            ModStatus.NotInModDb => Loc.T("mcard.compatUnknown"),
            ModStatus.NoCompatibleRelease => Loc.T("mcard.compatNone", branch),
            _ => Loc.T("mcard.compatYes", branch),
        };
    }

    async partial void OnIsVersionsTabChanged(bool value)
    {
        if (!value || _versionsLoaded) return;
        _versionsLoaded = true;
        VersionsStatus = Loc.T("mcard.versionsLoading");
        var options = await Owner.LoadVersionOptionsAsync(Row);
        Versions = options.Select(o => new VersionRowViewModel(o, Row.Installed)).ToList();
        VersionsStatus = Versions.Count == 0 ? Loc.T("mcard.versionsNone") : Loc.T("mcard.versionsHint");
    }

    [RelayCommand]
    private void InstallVersion(VersionRowViewModel? v)
    {
        if (v is null) return;
        if (!Confirm(Loc.T("mcard.installVersionConfirm", Name, v.Version, Row.Installed))) return;
        Owner.InstallVersion(Row, v.Option);
    }

    private static bool Confirm(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    [RelayCommand]
    private void OpenScreenshot(string? url)
    {
        if (string.IsNullOrEmpty(url) || Screenshots.Count == 0) return;
        var index = Math.Max(0, Screenshots.ToList().IndexOf(url));
        new ScreenshotWindow(Screenshots, index, Loc.T("shots.title", Name)) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
}

/// <summary>Строка вкладки «Версии»: версия, откуда (модбаза или сохранённая копия), дата, под какие версии игры.</summary>
public sealed class VersionRowViewModel(VersionOption option, string installed)
{
    public VersionOption Option { get; } = option;
    public string Version => Option.Version;
    public bool IsInstalled { get; } = option.Version == installed;
    public bool IsPrerelease { get; } = ModVersion.ParseOrNull(option.Version)?.IsPrerelease == true;
    public string PreText => IsPrerelease ? Loc.T("mcard.pre") : "";

    public string Meta { get; } = string.Join(" · ", new[]
    {
        option.Path is not null ? Loc.T("rollback.savedCopy") : Loc.T("mcard.fromModDb"),
        option.Date,
        option.Release is { } r && r.GameVersions.Any() ? "VS " + string.Join(", ", r.GameVersions.TakeLast(3)) : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
}
