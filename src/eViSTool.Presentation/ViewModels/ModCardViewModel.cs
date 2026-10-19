using eViSTool.Presentation;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels;

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

    /// <summary>Последние изменения этого мода из истории — над списком версий.</summary>
    [ObservableProperty] private IReadOnlyList<string> _historyLines = [];
    public bool HasHistory => HistoryLines.Count > 0;
    partial void OnHistoryLinesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasHistory));
    private bool _versionsLoaded;

    /// <summary>Страница мода, которую карточка загрузила с модбазы сама (<see cref="LoadAsync"/>): мод ещё не сверяли.</summary>
    private ModDbMod? _page;

    /// <param name="previous">
    /// Прежняя карточка: таблицу построили заново после операции (мод включили, закрепили, обновили), и выбран тот же мод.
    /// То, что она уже узнала, остаётся на месте: страница с модбазы не запрашивается снова, иконка шапки и списки не
    /// пересоздаются. Иначе шапка, описание и скриншоты пропадали до ответа модбазы — карточка мигала целиком.
    /// </param>
    public ModCardViewModel(ModRowViewModel row, ModsViewModel owner, ModVersion? game, string profileName, ModCardViewModel? previous = null)
    {
        Row = row;
        Owner = owner;
        ProfileName = profileName;
        var info = row.Local.Info;
        _summary = info?.Description?.Trim() ?? "";
        _author = info?.Authors is { Count: > 0 } a ? string.Join(", ", a) : "";
        // мод хостера без modinfo (исходник .cs) работает только на сервере хостера
        _side = SideText(row.IsHosterMod && info is null ? "server" : info?.Side);
        _compatibility = CompatibilityText(row, game);
        var same = previous is not null && row.ModId.Length > 0
                   && string.Equals(previous.Row.ModId, row.ModId, StringComparison.OrdinalIgnoreCase) ? previous : null;
        if (row.Result.Remote is { } remote) Apply(remote);
        else if (same?._page is { } page)
        {
            _page = page;
            Apply(page);
        }
        // шапка того же мода решает про иконку из файла так же, как раньше: иначе логотип, узнанный проверкой, спрятал бы
        // уже показанную иконку до своей загрузки — мигнули бы буквы
        IconWaitsForLogo = same?.IconWaitsForLogo ?? Logo is not null;
        if (same is null) return;
        // те же скриншоты — тот же список: ItemsControl не пересоздаёт картинки. Вкладка «Версии» до перечитывания
        // показывает прежний список, а не «Загрузка…»
        if (Screenshots.SequenceEqual(same.Screenshots)) _screenshots = same.Screenshots;
        _versions = same.Versions;
        _historyLines = same.HistoryLines;
        _versionsStatus = same.VersionsStatus;
    }

    public string Name => Row.Name;

    /// <summary>
    /// Иконка из файла мода для шапки — без запасного логотипа: логотип у шапки свой (<see cref="Logo"/>) и важнее, а
    /// иконка встаёт на его место, когда его нет или он не загрузился.
    /// </summary>
    public ModIconSource FileIcon => new(Row.Icon.File, null);

    /// <summary>
    /// Логотип известен уже при открытии карточки (ответ проверки обновлений): иконку из файла шапка не просит, пока он не
    /// подведёт, — спрятанная под логотипом, она читала бы архив зря. Логотип, пришедший позже (<see cref="LoadAsync"/>),
    /// иконку не отменяет: она уже прочитана и видна, а убрать её до появления логотипа значило бы мигнуть буквами.
    /// Не загрузился ли логотип, знает только окно — решает оно (Desktop: CardHeaderIcon).
    /// </summary>
    public bool IconWaitsForLogo { get; }

    /// <summary>«Настройки мода (2)» — у мода есть файлы в ModConfig; ссылка ведёт на вкладку «Настройки модов».</summary>
    public int ConfigCount => Owner.ConfigCount(Row.ModId);
    public bool HasConfigs => ConfigCount > 0;
    public string ConfigText => Loc.T("mcard.configs", ConfigCount);

    /// <summary>Число конфигов стало известно позже (удалённый сервер) — обновить ссылку.</summary>
    public void NotifyConfigs()
    {
        OnPropertyChanged(nameof(ConfigCount));
        OnPropertyChanged(nameof(HasConfigs));
        OnPropertyChanged(nameof(ConfigText));
    }
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
        Logo = ModDbLogo.Usable(mod.LogoFile); // стандартная картинка модбазы — не логотип: шапка покажет иконку из файла
        if (!string.IsNullOrWhiteSpace(mod.Author)) Author = mod.Author;
        Tag = mod.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        // сторона — как её видит игра (modinfo.json установленного файла); модбаза — только справка, если пишет другое
        if (mod.Side is { Length: > 0 } side)
        {
            if (Row.Local.Info is null) Side = SideText(side);
            else if (ModSides.Parse(side) != ModSides.Parse(Row.Local.Info.Side))
                Side = SideText(Row.Local.Info.Side) + " " + Loc.T("card.sideOnModDb", SideText(side));
        }
        Screenshots = mod.Screenshots.Select(s => s.MainFile).Where(u => !string.IsNullOrWhiteSpace(u)).ToList()!;
    }

    /// <summary>Мод ещё не сверяли с модбазой — подгрузить страницу мода только для карточки.</summary>
    public async Task LoadAsync(ModDbClient db, CancellationToken ct)
    {
        if (Row.Result.Remote is not null || _page is not null || Row.ModId.Length == 0) return;
        try
        {
            if (await db.GetModAsync(Row.ModId, ct) is { } mod && !ct.IsCancellationRequested)
            {
                _page = mod;
                Apply(mod);
                // строка таблицы до проверки знает только иконку из файла — логотип шапки ей тоже
                if (Logo is { } logo) Owner.LogoLearned(Row.ModId, logo);
            }
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
        if (row.IsHosterMod) return Loc.T("mcard.compatHoster");
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
        // список, оставшийся от прежней карточки того же мода, стоит со своей подписью до нового
        if (VersionsStatus.Length == 0) VersionsStatus = Loc.T("mcard.versionsLoading");
        HistoryLines = await Owner.ModHistoryLinesAsync(Row.ModId);
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
        Ui.Confirm(text);

    [RelayCommand]
    private void OpenScreenshot(string? url)
    {
        if (string.IsNullOrEmpty(url) || Screenshots.Count == 0) return;
        var index = Math.Max(0, Screenshots.ToList().IndexOf(url));
        AppWindows.Current.ShowScreenshots(Screenshots, index, Loc.T("shots.title", Name));
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
