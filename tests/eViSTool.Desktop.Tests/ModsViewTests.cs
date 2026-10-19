using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// «Мои моды» с настоящей главной моделью (docs/design.md, разделы 3 и 7): справа, пока мод не выбран, — сводка профиля;
/// у выбранной строки песочная метка слева; значки модов — иконка из файла мода или буквы цветом мода; пока идёт проверка
/// обновлений, в колонке статуса — заглушки с бегущим бликом, и только тогда. Модбаза поддельная: ответ ждёт, пока тест
/// не отменит проверку. Профиль — свой клиентский во временной папке (игра — поддельный сервер 1.22.7, моды alpha с
/// иконкой и выключенный beta без неё);
/// настройки программы — в папке data рядом с тестами, после теста прежний файл возвращается. Данные пользователя
/// тест не трогает.
/// </summary>
public sealed class ModsViewTests : IDisposable
{
    private const string ReleasesUrl = "EVISTOOL_RELEASES_URL";
    private static readonly Uri Base = new("avares://eViSTool.Desktop.Tests");
    private static readonly string FakeBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "FakeVsServer", "bin", "Debug", "net10.0"));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "mods-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.Instance.Language;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly ResourceInclude[] _theme =
    [
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") },
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") },
    ];
    private readonly StyleInclude _themeStyles = new(Base) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
    private CleanupWindowTests.TimerSpy? _timers;
    private (bool Saved, string? Text) _settings;
    private Window? _window;
    private string? _imageCache;

    /// <summary>Лесная тема окна — до разметки страницы: та берёт из неё переключатель, кисти и иконки.</summary>
    public ModsViewTests()
    {
        var app = Application.Current!;
        foreach (var include in _theme) app.Resources.MergedDictionaries.Add(include);
        app.Styles.Add(_themeStyles);
    }

    public void Dispose()
    {
        _window?.Close();
        if (_imageCache is not null)
        {
            RemoteImage.HandlerForTests = null;
            RemoteImage.CacheDir = _imageCache;
            RemoteImage.ClearMemory();
            ModIconImage.ReaderForTests = null;
            ModIconImage.ClearMemory();
        }
        // до возврата настроек: опрос, оставшийся с этого теста, мог бы записать их поверх
        foreach (var timer in _timers?.Timers ?? []) timer.Stop();
        if (_timers is not null) Ui.Platform = _oldPlatform ?? new AvaloniaUiPlatform();
        var app = Application.Current!;
        app.Styles.Remove(_themeStyles);
        foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
        Loc.Instance.SetLanguage(_language);
        if (_settings.Saved)
        {
            try
            {
                if (_settings.Text is null) File.Delete(AppPaths.SettingsFile);
                else File.WriteAllText(AppPaths.SettingsFile, _settings.Text);
            }
            catch (IOException) { }
        }
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Модбаза, где модов нет; ответ ждёт, пока запрос не отменят (проверка висит, пока тест её не отменит).</summary>
    private sealed class HangingModDb : HttpMessageHandler
    {
        public int Asked;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Модбаза, которая знает моды из <see cref="Logos"/> (modid → адрес логотипа): страница мода — для проверки обновлений и
    /// для карточки; остальных модов на ней нет.
    /// </summary>
    private sealed class AnsweringModDb : HttpMessageHandler
    {
        public Dictionary<string, string> Logos { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var id = path.StartsWith("/api/mod/", StringComparison.Ordinal) ? Uri.UnescapeDataString(path["/api/mod/".Length..]) : null;
            if (id is null || !Logos.TryGetValue(id, out var logo))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = $$"""{ "statuscode": "200", "mod": { "modid": 1, "assetid": 1, "name": "{{id}}", "logofile": "{{logo}}" } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>
    /// Картинки по адресу (остальные — 404) и свой кэш картинок на тест; иконки из файлов — со счётчиком чтений.
    /// <paramref name="gate"/> — ответ ждёт, пока задача не завершится (логотип «грузится»).
    /// </summary>
    private Dictionary<string, int> UseImages(Dictionary<string, byte[]> images, Task? gate = null)
    {
        _imageCache = RemoteImage.CacheDir;
        RemoteImage.CacheDir = Path.Combine(_dir, "images");
        RemoteImage.HandlerForTests = new ImageServer(images, gate ?? Task.CompletedTask);
        RemoteImage.ClearMemory();
        ModIconImage.ClearMemory();
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ModIconImage.ReaderForTests = path =>
        {
            var name = Path.GetFileName(path);
            lock (reads) reads[name] = reads.GetValueOrDefault(name) + 1;
            return Core.Mods.ModIcon.Read(path);
        };
        return reads;
    }

    private sealed class ImageServer(Dictionary<string, byte[]> images, Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return images.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Архив мода; <paramref name="icon"/> — modicon.png в корне (иконка мода) или null.</summary>
    private static byte[] Zip(string modId, byte[]? icon = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
                w.Write($"{{ \"type\": \"code\", \"modid\": \"{modId}\", \"name\": \"{modId}\", \"version\": \"1.0.0\" }}");
            if (icon is not null)
                using (var s = zip.CreateEntry("modicon.png").Open())
                    s.Write(icon);
        }
        return ms.ToArray();
    }

    private static byte[] Png(int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Главная модель над своими настройками: один клиентский профиль, без автопроверки, модбаза — <paramref name="db"/>.</summary>
    private MainViewModel Main(HttpMessageHandler db)
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        _timers = CleanupWindowTests.TimerSpy.Wrap(new AvaloniaUiPlatform());
        Ui.Platform = (IUiPlatform)(object)_timers;
        _settings = (true, File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null);

        var game = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        File.Copy(Path.Combine(FakeBin, "FakeVsServer.dll"), Path.Combine(game, "VintagestoryServer.dll")); // версия игры — 1.22.7
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, "alpha_1.0.0.zip"), Zip("alpha", Png(128))); // у alpha своя иконка, у beta нет
        File.WriteAllBytes(Path.Combine(mods, "beta_1.0.0.zip"), Zip("beta"));
        File.WriteAllText(Path.Combine(data, "clientsettings.json"), """{ "stringListSettings": { "disabledMods": ["beta"] } }""");
        var profile = new GameProfile { Id = "mods-view-test", Name = "Игрок", Kind = ProfileKind.Client, GameDir = game, DataDir = data };
        new SettingsStore().Save(new AppSettings
        {
            Profiles = [profile], ActiveProfileId = profile.Id, Language = _language, AutoCheckUpdates = false, GameBackground = false,
        });

        // адрес релизов читается ещё в конструкторе, до первого ожидания: потом переменную можно вернуть
        var releases = Environment.GetEnvironmentVariable(ReleasesUrl);
        Environment.SetEnvironmentVariable(ReleasesUrl, "http://127.0.0.1:9/releases"); // отказ — как «нет сети», тихо
        try { return new MainViewModel(new ModDbClient(new HttpClient(db) { BaseAddress = new Uri(ModDbClient.ApiBase) })); }
        finally { Environment.SetEnvironmentVariable(ReleasesUrl, releases); }
    }

    /// <summary>Страница в окне, моды прочитаны, строки таблицы на месте.</summary>
    private async Task<(Window Window, ModsViewModel Mods)> ShowAsync(MainViewModel main)
    {
        var mods = main.Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        await Wait.Until(() => mods.Rows.Count == 2);
        await Wait.Until(() => Rows(_window).Count == 2);
        return (_window, mods);
    }

    private static List<DataGridRow> Rows(Visual root) => [.. root.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible)];

    private static DataGridRow RowOf(Visual root, string modId) => Rows(root).Single(r => (r.DataContext as ModRowViewModel)?.ModId == modId);

    private static T Classed<T>(Visual root, string cls) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(cls));

    /// <summary>Элементы с классом в видимых строках таблицы (строки про запас таблица держит скрытыми).</summary>
    private static List<T> InRows<T>(Visual root, string cls) where T : Control =>
        [.. Rows(root).SelectMany(r => r.GetVisualDescendants().OfType<T>().Where(c => c.Classes.Contains(cls)))];

    private static Control Named(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    /// <summary>Дать часам анимаций пройти <paramref name="ms"/> миллисекунд: окно без экрана двигает их по тику отрисовки.</summary>
    private static async Task Run(int ms)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        } while (watch.ElapsedMilliseconds < ms);
    }

    private static double ShiftOf(Control c) => c.RenderTransform?.Value.M31 ?? 0;

    [AvaloniaFact]
    public async Task Summary_is_shown_until_a_mod_is_selected_and_the_selected_row_gets_a_sand_mark()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        var summary = Named(window, "ModsSummary");

        // мод не выбран — сводка: те же числа, что на вкладках-счётчиках; проверки ещё не было
        Assert.True(summary.IsEffectivelyVisible);
        Assert.Equal(Loc.T("mods.summaryOf", 1, 2), ((TextBlock)Named(window, "ModsSummaryEnabled")).Text);
        Assert.Equal(Loc.T("mods.summaryNone"), ((TextBlock)Named(window, "ModsSummaryProblems")).Text);
        Assert.Equal(Loc.T("mods.summaryNotChecked"), ((TextBlock)Named(window, "ModsSummaryChecked")).Text);
        var check = (Button)Named(window, "ModsSummaryCheck");
        Assert.Same(mods.CheckCommand, check.Command);
        Assert.Contains("primary", check.Classes); // обновлений нет — «Обновить всё» не видно, главная кнопка — эта

        // выбрали мод — вместо сводки его карточка; у строки песочная метка (проявляется переходом), у остальных нет
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        Dispatcher.UIThread.RunJobs();
        Assert.False(summary.IsEffectivelyVisible);
        Assert.NotNull(mods.Card);
        var mark = Classed<Border>(RowOf(window, "alpha"), "rowMark");
        await Wait.Until(() => mark.Opacity == 1);
        Assert.Same(window.FindResource("Forest.Accent"), mark.Background);
        Assert.Equal(2, mark.Bounds.Width);
        Assert.Equal(0, mark.TranslatePoint(default, RowOf(window, "alpha"))!.Value.X); // у самого левого края строки
        Assert.Equal(0, Classed<Border>(RowOf(window, "beta"), "rowMark").Opacity);
    }

    [AvaloniaFact]
    public async Task Mod_initials_are_tinted_by_modid()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));

        foreach (var row in mods.Rows)
        {
            var bg = ModTint.Background(row.Hue);
            var fg = ModTint.Foreground(row.Hue);
            var tile = RowOf(window, row.ModId).GetVisualDescendants().OfType<Border>()
                .Single(b => b.Background is ISolidColorBrush s && s.Color == Color.FromRgb(bg.R, bg.G, bg.B));
            var letters = Assert.IsType<TextBlock>(tile.Child);
            Assert.Equal(row.Initials, letters.Text);
            Assert.Equal(Color.FromRgb(fg.R, fg.G, fg.B), Assert.IsAssignableFrom<ISolidColorBrush>(letters.Foreground).Color);
        }
        Assert.NotEqual(mods.Rows[0].Hue, mods.Rows[1].Hue);
    }

    private static Image IconOf(Visual root) => root.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "Icon");

    private static TextBlock LettersOf(Visual root, ModRowViewModel row) =>
        root.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == row.Initials && t.FontSize == 10);

    [AvaloniaFact]
    public async Task Mod_icon_from_its_file_replaces_the_letters_in_the_row_and_the_card_header()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        var alpha = mods.Rows.Single(r => r.ModId == "alpha");
        var beta = mods.Rows.Single(r => r.ModId == "beta");

        // строка: у alpha — иконка из архива, сразу маленькая; буквы под ней спрятаны. У beta иконки нет, логотипа
        // модбаза не дала — буквы цветом мода
        var icon = IconOf(RowOf(window, "alpha"));
        await Wait.Until(() => icon.Source is not null);
        Assert.True(ModIconImage.GetFromFile(icon));
        Assert.Equal(ModIconImage.DefaultSize, ((Bitmap)icon.Source!).PixelSize.Width);
        Assert.Equal(Stretch.UniformToFill, icon.Stretch);
        var tile = icon.FindAncestorOfType<Border>()!; // плитка 30 × 30 со скруглением
        Assert.Equal(new Size(30, 30), tile.Bounds.Size);
        Assert.True(tile.ClipToBounds);
        Assert.Equal(new CornerRadius(6), tile.CornerRadius);
        Dispatcher.UIThread.RunJobs();
        Assert.False(LettersOf(RowOf(window, "alpha"), alpha).IsEffectivelyVisible);
        Assert.Null(IconOf(RowOf(window, "beta")).Source);
        Assert.True(LettersOf(RowOf(window, "beta"), beta).IsEffectivelyVisible);

        // шапка карточки: логотипа нет (модбаза молчит) — на его месте та же иконка, буквы спрятаны
        mods.Selected = alpha;
        Dispatcher.UIThread.RunJobs();
        Image Header() => window.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "FileIcon1");
        TextBlock Big() => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.FontSize == 40);
        await Wait.Until(() => Header().Source is not null && Header().IsEffectivelyVisible);
        Assert.True(ModIconImage.GetFromFile(Header()));
        Assert.False(Big().IsEffectivelyVisible);

        // у beta — буквы
        mods.Selected = beta;
        await Wait.Until(() => Header().Source is null && Big().IsEffectivelyVisible && Big().Text == beta.Initials);
    }

    // шапка карточки появляется не сразу после выбора мода, а с разметкой: до тех пор null
    private static Image? HeaderIcon(Visual root) => root.GetVisualDescendants().OfType<Image>().SingleOrDefault(i => i.Name == "FileIcon1");
    private static Image? HeaderLogo(Visual root) => root.GetVisualDescendants().OfType<Image>().SingleOrDefault(i => i.Name == "Logo1");

    [AvaloniaFact]
    public async Task Logo_the_card_found_shows_in_the_row_too()
    {
        const string logo = "https://img.test/beta.png";
        var db = new AnsweringModDb { Logos = { ["beta"] = logo } };
        UseImages(new() { [logo] = Png(200) });
        var (window, mods) = await ShowAsync(Main(db));
        var beta = mods.Rows.Single(r => r.ModId == "beta");
        Assert.Null(beta.Icon.Logo); // обновления не проверяли — логотипа строка не знает, у beta буквы

        // карточка сама сходила на модбазу — логотип в шапке и тот же в строке, вместо букв
        mods.Selected = beta;
        var icon = IconOf(RowOf(window, "beta"));
        await Wait.Until(() => icon.Source is not null && HeaderLogo(window)?.Source is not null);
        Assert.Equal(logo, RemoteImage.GetSource(icon));
        Assert.False(ModIconImage.GetFromFile(icon));
        Dispatcher.UIThread.RunJobs();
        Assert.False(LettersOf(RowOf(window, "beta"), beta).IsEffectivelyVisible);

        // таблицу построили заново (моды перечитаны с диска) — у новой строки логотип сразу
        await mods.ReloadAsync();
        var rebuilt = mods.Rows.Single(r => r.ModId == "beta");
        Assert.NotSame(beta, rebuilt);
        Assert.Equal(logo, rebuilt.Icon.Logo);
    }

    [AvaloniaFact]
    public async Task Card_header_does_not_read_the_file_icon_while_a_logo_is_known()
    {
        const string logo = "https://img.test/alpha.png";
        var db = new AnsweringModDb { Logos = { ["alpha"] = logo, ["beta"] = "https://img.test/beta.png" } };
        var reads = UseImages(new() { [logo] = Png(200) });
        var (window, mods) = await ShowAsync(Main(db));

        // проверка обновлений: модбаза ответила, логотип alpha известен ещё до карточки
        mods.CheckCommand.Execute(null);
        await Wait.Until(() => mods.CheckedAt is not null);
        var alpha = mods.Rows.Single(r => r.ModId == "alpha");
        Assert.Equal(logo, alpha.Icon.Logo);
        var icon = IconOf(RowOf(window, "alpha"));
        await Wait.Until(() => icon.Source is not null);
        Assert.True(ModIconImage.GetFromFile(icon)); // в строке иконка из файла важнее логотипа

        // в шапке — логотип; иконку из файла шапка не просит: архив прочитан один раз, для строки, 256 px не декодировались
        mods.Selected = alpha;
        await Wait.Until(() => HeaderLogo(window)?.Source is not null);
        await Wait.Idle(100);
        Assert.Null(ModIconImage.GetSource(HeaderIcon(window)!));
        Assert.Null(HeaderIcon(window)!.Source);
        lock (reads) Assert.Equal(1, reads["alpha_1.0.0.zip"]);
    }

    [AvaloniaFact]
    public async Task Card_header_shows_the_file_icon_when_the_logo_does_not_load()
    {
        var db = new AnsweringModDb { Logos = { ["alpha"] = "https://img.test/gone.png" } }; // картинки по этому адресу нет
        UseImages(new());
        var (window, mods) = await ShowAsync(Main(db));

        // логотип известен с открытия карточки (проверка ответила), но не загрузился — на его месте иконка из файла
        mods.CheckCommand.Execute(null);
        await Wait.Until(() => mods.CheckedAt is not null);
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        Assert.True(mods.Card!.IconWaitsForLogo);
        await Wait.Until(() => HeaderLogo(window) is { } logo && RemoteImage.GetFailed(logo));
        var header = HeaderIcon(window)!;
        await Wait.Until(() => header.Source is not null && header.IsEffectivelyVisible);
        Assert.True(ModIconImage.GetFromFile(header));
        Assert.Equal(128, ((Bitmap)header.Source!).PixelSize.Width); // иконка 128 px — как есть: до 256 не растянута
        Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.FontSize == 40).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Logo_found_by_the_card_does_not_blink_the_letters_while_it_loads()
    {
        const string logo = "https://img.test/alpha.png";
        var db = new AnsweringModDb { Logos = { ["alpha"] = logo } };
        var loaded = new TaskCompletionSource();
        UseImages(new() { [logo] = Png(200) }, loaded.Task);
        var (window, mods) = await ShowAsync(Main(db));

        // проверки не было: в шапке сначала иконка из файла, потом карточка узнаёт логотип, и он качается
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        await Wait.Until(() => mods.Card?.Logo is not null && HeaderLogo(window) is { } l && RemoteImage.GetIsLoading(l));
        Assert.False(mods.Card!.IconWaitsForLogo);
        var header = HeaderIcon(window)!;
        TextBlock Big() => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.FontSize == 40);

        // пока логотип не пришёл, иконка остаётся на месте — буквами шапка не мигает
        await Wait.Until(() => header.Source is not null);
        await Wait.Idle(100);
        Assert.True(header.IsEffectivelyVisible);
        Assert.False(Big().IsEffectivelyVisible);

        // логотип пришёл — он поверх, иконка спрятана
        loaded.SetResult();
        await Wait.Until(() => HeaderLogo(window)!.Source is not null && !header.IsEffectivelyVisible);
        Assert.False(Big().IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Card_tabs_reveal_like_the_catalog()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        Dispatcher.UIThread.RunJobs();

        // проявляются только сами вкладки «Обзор» и «Версии»: шапка, название и полоса вкладок — снаружи
        var tabs = window.GetVisualDescendants().OfType<Panel>().Single(Motion.GetRevealChildren);
        Assert.Equal(2, tabs.Children.Count);
        Assert.Equal([true, false], tabs.Children.Select(c => c.IsVisible));
        Assert.Empty(tabs.GetVisualDescendants().OfType<RadioButton>());
    }

    [AvaloniaFact]
    public async Task Skeletons_replace_the_status_only_while_updates_are_checked()
    {
        var db = new HangingModDb();
        var (window, mods) = await ShowAsync(Main(db));
        var grid = (DataGrid)Named(window, "ModsGrid");

        // проверки нет — статусы на месте, заглушек нет, и блик не бежит
        Assert.All(InRows<Border>(window, "skeleton"), s => Assert.False(s.IsEffectivelyVisible));
        Assert.Equal(2, InRows<StackPanel>(window, "status").Count(s => s.IsEffectivelyVisible));
        var idle = InRows<Border>(window, "glint");
        Assert.Equal(2, idle.Count);
        var still = ShiftOf(idle[0]);
        await Run(150);
        Assert.Equal(still, ShiftOf(idle[0]));

        // проверка висит на модбазе (таблицу перед этим перечитали с диска) — у каждой строки заглушка вместо статуса
        mods.CheckCommand.Execute(null);
        await Wait.Until(() => db.Asked > 0 && mods.IsChecking);
        await Wait.Until(() => Rows(window).Count == 2 && InRows<Border>(window, "skeleton").Count(s => s.IsEffectivelyVisible) == 2);
        Assert.Contains("checking", grid.Classes);
        Assert.Equal(0, InRows<StackPanel>(window, "status").Count(s => s.IsEffectivelyVisible));
        var skeleton = InRows<Border>(window, "skeleton")[0];
        Assert.Equal(new Size(96, 10), skeleton.Bounds.Size);
        Assert.Same(window.FindResource("Forest.Raised"), skeleton.Background);

        // блик бежит по кругу: за полсекунды положение меняется и всегда в пределах «весь левее» … «весь правее» полоски
        var glint = InRows<Border>(window, "glint")[0];
        var seen = new HashSet<double>();
        for (var i = 0; i < 10; i++)
        {
            await Run(50);
            var x = ShiftOf(glint);
            Assert.InRange(x, -96.5, 96.5);
            seen.Add(Math.Round(x));
        }
        Assert.True(seen.Count > 3, $"положения блика: {string.Join(", ", seen)}");

        // проверку отменили — снова статусы
        mods.CheckCancelCommand.Execute(null);
        await Wait.Until(() => !mods.IsChecking);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("checking", grid.Classes);
        Assert.All(InRows<Border>(window, "skeleton"), s => Assert.False(s.IsEffectivelyVisible));
        Assert.Equal(2, InRows<StackPanel>(window, "status").Count(s => s.IsEffectivelyVisible));
    }
}
