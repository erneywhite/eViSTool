using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// обновлений, в колонке статуса — заглушки с бегущим бликом, и только тогда; переключение мода не гасит переключатели, не
/// сбивает таблице прокрутку и порядок и не сбрасывает карточку (раздел 9). Под вкладками-счётчиками одна песочная
/// полоска едет к выбранному фильтру; Esc и кнопка «Назад» мыши снимают выбор мода (и в «Каталоге» тоже), Esc в поле
/// поиска сначала очищает поиск. Модбаза поддельная: ответ ждёт, пока тест
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

        /// <summary>Сколько раз модбазу спросили.</summary>
        public int Asked;

        /// <summary>Модбаза «задумалась»: ответ ждёт, пока запрос не отменят.</summary>
        public bool Silent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            if (Silent) await Task.Delay(Timeout.Infinite, ct);
            var path = request.RequestUri!.AbsolutePath;
            var id = path.StartsWith("/api/mod/", StringComparison.Ordinal) ? Uri.UnescapeDataString(path["/api/mod/".Length..]) : null;
            if (id is null || !Logos.TryGetValue(id, out var logo))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var json = $$"""{ "statuscode": "200", "mod": { "modid": 1, "assetid": 1, "name": "{{id}}", "logofile": "{{logo}}" } }""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

    /// <summary>Сообщения окна — без окон: запоминаются; на вопросы — «Нет».</summary>
    private sealed class QuietUi : AvaloniaUiPlatform
    {
        public List<string> Notices { get; } = [];

        public override void Notify(string text, NoticeKind kind) => Notices.Add(text);

        public override Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) => Answer.No;
    }

    /// <summary>
    /// Главная модель над своими настройками: один клиентский профиль, без автопроверки, модбаза — <paramref name="db"/>;
    /// <paramref name="ui"/> — свои сообщения и вопросы окна вместо настоящих.
    /// </summary>
    private MainViewModel Main(HttpMessageHandler db, int extra = 0, AvaloniaUiPlatform? ui = null)
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        _timers = CleanupWindowTests.TimerSpy.Wrap(ui ?? new AvaloniaUiPlatform());
        Ui.Platform = (IUiPlatform)(object)_timers;
        _settings = (true, File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null);

        var game = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        File.Copy(Path.Combine(FakeBin, "FakeVsServer.dll"), Path.Combine(game, "VintagestoryServer.dll")); // версия игры — 1.22.7
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, "alpha_1.0.0.zip"), Zip("alpha", Png(128))); // у alpha своя иконка, у beta нет
        File.WriteAllBytes(Path.Combine(mods, "beta_1.0.0.zip"), Zip("beta"));
        for (var i = 0; i < extra; i++) File.WriteAllBytes(Path.Combine(mods, $"mod{i:00}_1.0.0.zip"), Zip($"mod{i:00}"));
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

        // моды перечитаны с диска, ничего не поменялось — строка та же, с логотипом
        await mods.ReloadAsync();
        Assert.Same(beta, mods.Rows.Single(r => r.ModId == "beta"));

        // мод включили — строку построили заново: у новой строки логотип сразу
        await mods.ToggleEnabledCommand.ExecuteAsync(beta);
        var rebuilt = mods.Rows.Single(r => r.ModId == "beta");
        Assert.NotSame(beta, rebuilt);
        Assert.True(rebuilt.IsEnabled);
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

    /// <summary>
    /// Что менялось у элементов окна, пока шло действие: перемены свойств, а не кадры. Тихое перечитывание папки модов в
    /// тесте укладывается между двумя кадрами, а у игрока сотня архивов читается секунду-другую — всё, что за это время
    /// переменилось и вернулось, он видит как моргание.
    /// </summary>
    private sealed class Changes : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public List<(Visual Who, AvaloniaProperty What, object? From, object? To)> Seen { get; } = [];

        public Changes(Window window, params AvaloniaProperty[] properties)
        {
            foreach (var property in properties)
                _subscriptions.Add(property.Changed.Subscribe(new Avalonia.Reactive.AnonymousObserver<AvaloniaPropertyChangedEventArgs>(e =>
                {
                    if (e.Sender is Visual who && TopLevel.GetTopLevel(who) == window) Seen.Add((who, property, e.OldValue, e.NewValue));
                })));
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }

    private static CheckBox SwitchOf(Visual row) => row.GetVisualDescendants().OfType<CheckBox>().Single();

    /// <summary>
    /// Переключили мод в строке — «моргала половина окна». Пока идёт запись и перечитывание папки модов, ни один
    /// переключатель не выключается: прежде команда была занята, и все переключатели таблицы и карточки гасли до 40 %. Карточка
    /// того же мода, построенная заново с таблицей, не теряет того, что узнала с модбазы: логотип в шапке не пропадает и за
    /// страницей мода она второй раз не ходит, а картинки окна вообще не перезапрашиваются.
    /// </summary>
    [AvaloniaFact]
    public async Task Toggling_a_mod_does_not_blink_the_switches_or_the_card()
    {
        const string logo = "https://img.test/alpha.png";
        var db = new AnsweringModDb { Logos = { ["alpha"] = logo } };
        UseImages(new() { [logo] = Png(200) });
        var (window, mods) = await ShowAsync(Main(db));
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        await Wait.Until(() => HeaderLogo(window)?.Source is not null);
        await Run(300);
        var asked = db.Asked;
        db.Silent = true; // сходи карточка за страницей снова — до ответа шапка осталась бы без логотипа

        using (var changes = new Changes(window, Avalonia.Input.InputElement.IsEffectivelyEnabledProperty, Image.SourceProperty))
        {
            Mouse.Click(window, SwitchOf(RowOf(window, "alpha")));
            await Wait.Until(() => !mods.ToggleEnabledCommand.IsRunning && mods.Rows.Single(r => r.ModId == "alpha") is { IsEnabled: false });
            await Run(250);

            Assert.Empty(changes.Seen.Where(c => c.What == Avalonia.Input.InputElement.IsEffectivelyEnabledProperty && c.Who is CheckBox)
                .Select(c => $"переключатель {(c.Who as Control)?.DataContext}: {c.From} → {c.To}"));
            Assert.Empty(changes.Seen.Where(c => c.What == Image.SourceProperty).Select(c => $"{(c.Who as Control)?.Name}: {c.From} → {c.To}"));
        }
        Assert.Equal(logo, mods.Card!.Logo);
        Assert.NotNull(HeaderLogo(window)!.Source);
        Assert.Equal(asked, db.Asked);
        Assert.True(SwitchOf(RowOf(window, "beta")).IsEffectivelyEnabled);
        Assert.False(SwitchOf(RowOf(window, "alpha")).IsChecked);
    }

    /// <summary>
    /// Открыта вкладка «Версии», мод переключили в таблице: карточка, построенная заново, сразу на той же вкладке — она не
    /// прячется и не проявляется ещё раз (прежде на миг вставал «Обзор», и «Версии» заново поднимались из прозрачности).
    /// </summary>
    [AvaloniaFact]
    public async Task Rebuilt_card_stays_on_the_versions_tab_without_revealing_it_again()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        mods.Card!.IsVersionsTab = true;
        await Run(400);
        var tabs = window.GetVisualDescendants().OfType<Panel>().Single(Motion.GetRevealChildren);
        var versions = tabs.Children[1];
        Assert.True(versions.IsVisible);

        using (var changes = new Changes(window, Visual.IsVisibleProperty, Visual.OpacityProperty))
        {
            Mouse.Click(window, SwitchOf(RowOf(window, "alpha")));
            await Wait.Until(() => !mods.ToggleEnabledCommand.IsRunning && mods.Rows.Single(r => r.ModId == "alpha") is { IsEnabled: false });
            await Run(300);
            Assert.Empty(changes.Seen.Where(c => c.Who is Control tab && tabs.Children.Contains(tab)).Select(c => $"{c.What.Name}: {c.From} → {c.To}"));
        }
        Assert.True(mods.Card!.IsVersionsTab);
        Assert.Equal(1, versions.Opacity);
    }

    /// <summary>
    /// Щелчки, пока первое переключение ещё идёт: команда не занята (переключатели не гаснут), а сами переключения идут по
    /// одному, и следующее берёт мод из уже перечитанной таблицы. Два щелчка по одному переключателю возвращают мод как
    /// было — прежде оба выключали его по устаревшей строке.
    /// </summary>
    [AvaloniaFact]
    public async Task Toggles_clicked_during_a_toggle_run_after_it_on_the_fresh_row()
    {
        var (_, mods) = await ShowAsync(Main(new HangingModDb()));
        var alpha = mods.Rows.Single(r => r.ModId == "alpha");
        var beta = mods.Rows.Single(r => r.ModId == "beta");

        var first = mods.ToggleEnabledCommand.ExecuteAsync(alpha);
        Assert.True(mods.ToggleEnabledCommand.IsRunning); // папка модов перечитывается в фоне — переключение ещё идёт
        Assert.True(mods.ToggleEnabledCommand.CanExecute(beta));
        var again = mods.ToggleEnabledCommand.ExecuteAsync(alpha);
        var other = mods.ToggleEnabledCommand.ExecuteAsync(beta);
        await Wait.Until(() => first.IsCompleted && again.IsCompleted && other.IsCompleted);
        await first;

        Assert.True(mods.Rows.Single(r => r.ModId == "alpha").IsEnabled);
        Assert.True(mods.Rows.Single(r => r.ModId == "beta").IsEnabled);
    }

    /// <summary>
    /// Длинный список, прокрученный вниз: переключили мод — таблица стоит где стояла, на экране те же строки. Прежде строки
    /// строились заново, вид таблицы на миг пустел, и она прыгала к началу; переключение меняет теперь одну строку.
    /// </summary>
    [AvaloniaFact]
    public async Task Toggling_a_mod_far_down_the_list_keeps_the_table_in_place()
    {
        var mods = Main(new HangingModDb(), extra: 40).Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        var window = _window;
        await Wait.Until(() => mods.Rows.Count == 42);
        mods.SortIndex = 1; // по имени: выключенный мод остаётся на своём месте, а не уходит к выключенным
        await Run(200);
        var grid = (DataGrid)Named(window, "ModsGrid");
        var target = mods.View.Items[30];
        grid.ScrollIntoView(target, null);
        await Run(200);
        mods.Selected = target;
        await Run(300);
        var bar = grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(s => s.Orientation == Avalonia.Layout.Orientation.Vertical);
        List<(string? Id, double Y)> Shown() =>
            [.. Rows(window).Select(r => ((r.DataContext as ModRowViewModel)?.ModId, r.TranslatePoint(default, grid)!.Value.Y)).OrderBy(x => x.Item2)];
        var before = Shown();
        var offset = bar.Value;
        Assert.True(offset > 500, $"таблица прокручена: {offset}");
        var others = mods.Rows.Where(r => r != target).ToList();

        using (var changes = new Changes(window, Avalonia.Controls.Primitives.RangeBase.ValueProperty))
        {
            Mouse.Click(window, SwitchOf(RowOf(window, target.ModId)));
            await Wait.Until(() => !mods.ToggleEnabledCommand.IsRunning && mods.Rows.Single(r => r.ModId == target.ModId) is { IsEnabled: false });
            await Run(300);
            Assert.Empty(changes.Seen.Select(c => $"{c.Who.GetType().Name}: {c.From} → {c.To}"));
        }
        Assert.Equal(offset, bar.Value);
        Assert.True(before.SequenceEqual(Shown()), $"до: {string.Join(", ", before)}\nпосле: {string.Join(", ", Shown())}\nвид: {string.Join(", ", mods.View.Items.Skip(25).Take(10).Select(r => r.ModId))}\nтаблица: {string.Join(", ", grid.CollectionView!.Cast<ModRowViewModel>().Select(r => r.ModId))}");
        Assert.Equal(target.ModId, mods.Selected?.ModId);
        Assert.Equal(others, mods.Rows.Where(r => r.ModId != target.ModId)); // остальные строки — те же объекты
    }
    private static List<string> GridOrderOf(DataGrid grid) => [.. grid.CollectionView!.Cast<ModRowViewModel>().Select(r => r.ModId)];

    /// <summary>
    /// Таблица показывает строки в порядке списка, хотя её вид сдвигов не понимает (GridOrder): смена порядка «Сначала
    /// важное» → «По имени» переставляет строки, выбранный мод остаётся выбранным, а карточка — той же.
    /// </summary>
    [AvaloniaFact]
    public async Task Changing_the_order_reorders_the_table_and_keeps_the_selection()
    {
        var mods = Main(new HangingModDb(), extra: 3).Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        await Wait.Until(() => mods.Rows.Count == 5 && Rows(_window).Count == 5);
        var grid = (DataGrid)Named(_window, "ModsGrid");
        Assert.Equal(["alpha", "mod00", "mod01", "mod02", "beta"], GridOrderOf(grid)); // выключенный beta — в конце
        mods.Selected = mods.Rows.Single(r => r.ModId == "mod01");
        await Run(200);
        var card = mods.Card;

        mods.SortIndex = 1; // по имени
        await Run(200);
        Assert.Equal(["alpha", "beta", "mod00", "mod01", "mod02"], GridOrderOf(grid));
        Assert.Equal(mods.View.Items.Select(r => r.ModId), GridOrderOf(grid));
        Assert.Same(mods.Selected, grid.SelectedItem);
        Assert.Equal("mod01", mods.Selected?.ModId);
        Assert.Same(card, mods.Card);
    }

    private static List<string> GroupHeaders(Visual root) =>
        [.. root.GetVisualDescendants().OfType<DataGridRowGroupHeader>().Where(h => h.IsVisible)
            .Select(h => (h.DataContext as Avalonia.Collections.DataGridCollectionViewGroup)?.Key as string ?? "?")];

    /// <summary>
    /// «По категориям»: таблица — группами по своим категориям (по алфавиту, «Без категории» в конце). Свёрнутую группу
    /// помнит профиль: перестройка таблицы (смена порядка) её не раскрывает. Категория — из меню строки или карточки.
    /// </summary>
    [AvaloniaFact]
    public async Task By_category_groups_the_table_and_remembers_collapsed_groups()
    {
        var main = Main(new HangingModDb(), extra: 3);
        var mods = main.Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        await Wait.Until(() => mods.Rows.Count == 5 && Rows(_window).Count == 5);
        ModRowViewModel Row(string id) => mods.Rows.Single(r => r.ModId == id);
        mods.SetCategory(Row("alpha"), "Карта");
        mods.SetCategory(Row("mod00"), "карта"); // тот же регистр не плодит вторую группу
        mods.SetCategory(Row("mod01"), "Библиотеки");
        Assert.Equal("Карта", Row("mod00").Category);
        Assert.Equal(["Библиотеки", "Карта"], mods.Categories);

        mods.GroupByCategory = true;
        await Run(300);
        Assert.Equal(["Библиотеки", "Карта", ModsViewModel.NoCategoryLabel], GroupHeaders(_window));

        // свернули «Карта» щелчком по заголовку — профиль помнит; смена порядка перестраивает таблицу, а группа свёрнута
        var header = _window.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
            .Single(h => h.IsVisible && (h.DataContext as Avalonia.Collections.DataGridCollectionViewGroup)?.Key as string == "Карта");
        header.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First().IsChecked = false;
        await Run(200);
        Assert.True(mods.IsCategoryCollapsed("Карта"));
        mods.SortIndex = 1;
        await Run(400);
        Assert.DoesNotContain(Rows(_window), r => (r.DataContext as ModRowViewModel)?.Category == "Карта");
        Assert.Contains(Rows(_window), r => (r.DataContext as ModRowViewModel)?.ModId == "mod01");

        if (Environment.GetEnvironmentVariable("EVISTOOL_SHOT") is { } shot)
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(_window)!.Save(shot);

        // куда ляжет перетаскиваемый мод: на заголовок — в его группу, на строку — в группу её мода, «Без категории» — пустая
        var view = (ModsView)_window.Content!;
        DataGridRowGroupHeader HeaderOf(string key) => _window.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
            .Single(h => h.IsVisible && (h.DataContext as Avalonia.Collections.DataGridCollectionViewGroup)?.Key as string == key);
        Assert.Equal("Библиотеки", view.DropTarget(HeaderOf("Библиотеки").GetVisualDescendants().OfType<TextBlock>().First())?.Category);
        Assert.Equal("", view.DropTarget(HeaderOf(ModsViewModel.NoCategoryLabel))?.Category);
        Assert.Equal("Библиотеки", view.DropTarget(RowOf(_window, "mod01"))?.Category);
        mods.GroupByCategory = false;
        await Run(200);
        Assert.Null(view.DropTarget(RowOf(_window, "mod01"))); // без групп перетаскивать некуда
        mods.GroupByCategory = true;
        await Run(200);

        // меню строки: «Без категории» убирает мод из группы
        mods.Selected = Row("mod01");
        mods.CategoryMenu.Single(c => c.Category == "").Command.Execute(null);
        Assert.Equal("", Row("mod01").Category);
        Assert.Equal(["Карта"], mods.Categories);
        Assert.False(main.Profiles[0].Model.ModCategories.ContainsKey("mod01"));
    }

    /// <summary>
    /// «Сначала важное»: выключенный мод уезжает к выключенным — в таблице тоже, без потерянных и лишних строк; включённый
    /// обратно возвращается на место.
    /// </summary>
    [AvaloniaFact]
    public async Task Toggled_mod_moves_to_its_place_in_the_table()
    {
        var mods = Main(new HangingModDb(), extra: 3).Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        await Wait.Until(() => mods.Rows.Count == 5 && Rows(_window).Count == 5);
        var grid = (DataGrid)Named(_window, "ModsGrid");

        Mouse.Click(_window, SwitchOf(RowOf(_window, "mod00")));
        await Wait.Until(() => !mods.ToggleEnabledCommand.IsRunning && mods.Rows.Single(r => r.ModId == "mod00") is { IsEnabled: false });
        await Run(200);
        Assert.Equal(["alpha", "mod01", "mod02", "beta", "mod00"], GridOrderOf(grid));
        Assert.Equal(mods.View.Items.Select(r => r.ModId), GridOrderOf(grid));
        Assert.Equal(5, Rows(_window).Count);

        Mouse.Click(_window, SwitchOf(RowOf(_window, "mod00")));
        await Wait.Until(() => !mods.ToggleEnabledCommand.IsRunning && mods.Rows.Single(r => r.ModId == "mod00") is { IsEnabled: true });
        await Run(200);
        Assert.Equal(["alpha", "mod00", "mod01", "mod02", "beta"], GridOrderOf(grid));
        Assert.Equal(5, Rows(_window).Count);
    }

    /// <summary>Переключатель «Включён в профиле» в карточке мода.</summary>
    private static CheckBox CardSwitch(Visual root, ModsViewModel mods) =>
        root.GetVisualDescendants().OfType<CheckBox>().Single(c => c.DataContext is ModCardViewModel && c.Command == mods.ToggleEnabledCommand);

    /// <summary>
    /// Переключить мод не удалось (настройки игры не записались): переключатели в строке и в карточке встают как было.
    /// Щелчок сам перекидывает переключатель, а привязка к прежней строке (мод не поменялся) его не вернула бы — строку
    /// щёлкнутого мода таблица строит заново, остальные остаются прежними.
    /// </summary>
    [AvaloniaFact]
    public async Task Failed_toggle_puts_the_switches_back()
    {
        var ui = new QuietUi();
        var main = Main(new HangingModDb(), ui: ui);
        // на месте временного файла настроек игры — папка: запись настроек не удастся
        Directory.CreateDirectory(Path.Combine(_dir, "data", "clientsettings.json.evistool.tmp"));
        var (window, mods) = await ShowAsync(main);
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        await Run(200);

        var beta = mods.Rows.Single(r => r.ModId == "beta");

        foreach (var switchOf in new Func<CheckBox>[] { () => SwitchOf(RowOf(window, "alpha")), () => CardSwitch(window, mods) })
        {
            var notices = ui.Notices.Count;
            var before = mods.Rows.Single(r => r.ModId == "alpha");
            Mouse.Click(window, switchOf());
            await Wait.Until(() => ui.Notices.Count > notices && !mods.ToggleEnabledCommand.IsRunning);
            await Run(200);

            var alpha = mods.Rows.Single(r => r.ModId == "alpha");
            Assert.True(alpha.IsEnabled);
            Assert.NotSame(before, alpha); // строку щёлкнутого мода построили заново, хоть он и не поменялся
            Assert.True(SwitchOf(RowOf(window, "alpha")).IsChecked);
            Assert.True(CardSwitch(window, mods).IsChecked);
            Assert.Same(alpha, mods.Card?.Row);
            Assert.Same(beta, mods.Rows.Single(r => r.ModId == "beta")); // остальные строки — прежние
        }
    }

    /// <summary>
    /// Выбранный мод удалили (файла не стало, папку перечитали): выбор снят вместе с карточкой — справа сводка, в таблице
    /// ничего не выделено. Таблица, у которой выбранную строку заменили, сама выбирает соседнюю — та не должна остаться
    /// выбранной без карточки.
    /// </summary>
    [AvaloniaFact]
    public async Task Deleting_the_selected_mod_clears_the_selection()
    {
        var mods = Main(new HangingModDb(), extra: 3).Mods;
        _window = new Window { Width = 1400, Height = 860, Content = new ModsView { DataContext = mods } };
        _window.Show();
        await Wait.Until(() => mods.Rows.Count == 5 && Rows(_window).Count == 5);
        var grid = (DataGrid)Named(_window, "ModsGrid");
        mods.Selected = mods.Rows.Single(r => r.ModId == "mod01");
        await Run(200);
        Assert.Same(mods.Selected, grid.SelectedItem);

        File.Delete(Path.Combine(_dir, "data", "Mods", "mod01_1.0.0.zip"));
        await mods.ReloadAsync();
        await Run(200);

        Assert.Equal(4, mods.Rows.Count);
        Assert.Null(mods.Card);
        Assert.Null(mods.Selected);
        Assert.Null(grid.SelectedItem);
        Assert.True(Named(_window, "ModsSummary").IsEffectivelyVisible);

        // выбрать соседнюю строку после этого — её карточка
        var mod02 = mods.Rows.Single(r => r.ModId == "mod02");
        grid.SelectedItem = mod02;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(mod02, mods.Card?.Row);
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

    // ---------- вкладки-счётчики: одна полоска на ряд

    /// <summary>Вкладка-счётчик, отмеченная сейчас (фильтр таблицы).</summary>
    private static RadioButton CheckedTab(Window root) =>
        root.GetVisualDescendants().OfType<RadioButton>().Single(r => r.IsChecked == true && r.Theme == root.FindResource("Forest.StatTab"));

    private static double BarLeft(TabIndicator bar) => bar.RenderTransform?.Value.M31 ?? 0;

    [AvaloniaFact]
    public async Task Counter_tabs_share_one_sand_bar_that_rides_to_the_chosen_filter()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        var bar = (TabIndicator)Named(window, "ModsTabIndicator");
        var host = (Visual)bar.GetVisualParent()!;
        Assert.Same(window.FindResource("Forest.Accent"), bar.Background);
        Assert.Equal(2, bar.Height);

        // при открытии — сразу под «установлено», во всю ширину вкладки; на линии ряда — нижний край рамки под вкладками
        var all = CheckedTab(window);
        Assert.Same(all, bar.Tab);
        Assert.False(bar.IsMoving);
        Assert.Equal(all.TranslatePoint(default, host)!.Value.X, BarLeft(bar), 3);
        Assert.Equal(all.Bounds.Width, TabIndicator.BaseWidth * bar.RenderTransform!.Value.M11, 3);
        var row = host.FindAncestorOfType<Border>()!;
        var bottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
        Assert.Equal(row.TranslatePoint(new Point(0, row.Bounds.Height), window)!.Value.Y, bottom, 3);

        // фильтр сменился (вкладки привязаны к нему и пересчитываются по одной) — полоска едет, не пропадая по дороге
        mods.Filter = ModFilter.Disabled;
        Dispatcher.UIThread.RunJobs();
        var disabled = CheckedTab(window);
        Assert.NotSame(all, disabled);
        Assert.Same(disabled, bar.Tab);
        Assert.True(bar.IsMoving);
        Assert.True(bar.IsVisible);
        var seen = new List<double>();
        for (var i = 0; i < 40 && bar.IsMoving; i++)
        {
            await Run(10);
            seen.Add(BarLeft(bar));
        }
        var target = disabled.TranslatePoint(default, host)!.Value.X;
        Assert.Contains(seen, x => x > all.TranslatePoint(default, host)!.Value.X + 0.5 && x < target - 0.5); // ехала
        Assert.False(bar.IsMoving);
        Assert.Equal(target, BarLeft(bar), 3);
        Assert.Equal(disabled.Bounds.Width, TabIndicator.BaseWidth * bar.RenderTransform!.Value.M11, 3);

        // щелчок по «Истории» справа — туда же
        var history = (RadioButton)Named(window, "ModsHistoryTab");
        Mouse.Click(window, history);
        await Run(400);
        Assert.Equal(ModFilter.History, mods.Filter);
        Assert.Same(history, bar.Tab);
        Assert.Equal(history.TranslatePoint(default, host)!.Value.X, BarLeft(bar), 3);
    }

    // ---------- Esc и кнопка «Назад» мыши: снять выбор мода

    private static void Esc(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Back(Window window, Visual over)
    {
        var at = Mouse.Center(window, over);
        window.MouseDown(at, MouseButton.XButton1);
        window.MouseUp(at, MouseButton.XButton1);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Esc_and_the_back_button_return_the_right_panel_to_the_summary()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        var summary = Named(window, "ModsSummary");
        var grid = (DataGrid)Named(window, "ModsGrid");

        // щелчок по строке: мод выбран, фокус в таблице, справа карточка
        Mouse.Click(window, RowOf(window, "alpha"), x: 200);
        Assert.Equal("alpha", mods.Selected?.ModId);
        Assert.False(summary.IsEffectivelyVisible);

        Esc(window);
        Assert.Null(mods.Selected);
        Assert.Null(mods.Card);
        Assert.Null(grid.SelectedItem);
        Assert.True(summary.IsEffectivelyVisible);
        // строка не возвращается сама, когда таблицу строят заново (моды перечитаны)
        await mods.ReloadAsync();
        Assert.Null(mods.Selected);

        // кнопка «Назад» мыши — над карточкой справа (где пользователь и смотрит)
        Mouse.Click(window, RowOf(window, "beta"), x: 200);
        Assert.Equal("beta", mods.Selected?.ModId);
        Dispatcher.UIThread.RunJobs();
        Back(window, window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Content is ContentControl { Content: ModCardViewModel }));
        Assert.Null(mods.Selected);
        Assert.True(summary.IsEffectivelyVisible);
    }

    /// <summary>
    /// Меню строки открыто — «Назад» мыши мимо него только закрывает меню, мод остаётся выбранным; следующее нажатие уже
    /// снимает выбор. Меню строки закрывается само, раньше, чем нажатие дойдёт до страницы, и передаёт его тому, что под
    /// ним, — страница это нажатие всё равно не берёт.
    /// </summary>
    [AvaloniaFact]
    public async Task Back_button_that_closes_the_row_menu_keeps_the_mod()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        var grid = (DataGrid)Named(window, "ModsGrid");
        var row = RowOf(window, "alpha");
        Mouse.Prepare(window);
        var at = row.TranslatePoint(new Point(200, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(grid.ContextMenu!.IsOpen);
        Assert.Equal("alpha", mods.Selected?.ModId);

        var card = window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Content is ContentControl { Content: ModCardViewModel });
        Back(window, card);
        Assert.False(grid.ContextMenu.IsOpen);
        Assert.Equal("alpha", mods.Selected?.ModId);

        Back(window, card);
        Assert.Null(mods.Selected);
        Assert.True(Named(window, "ModsSummary").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Esc_in_the_search_box_clears_the_search_before_the_mod()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("search"));
        search.Focus();
        window.KeyTextInput("alp");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("alp", mods.Search);

        Esc(window); // сначала поиск: введённое — не потеря выбора
        Assert.Equal("", mods.Search);
        Assert.Equal("alpha", mods.Selected?.ModId);

        Esc(window); // поле пустое — снимается выбор
        Assert.Null(mods.Selected);
        Assert.True(Named(window, "ModsSummary").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Hidden_mods_page_keeps_the_mod_on_esc_and_back()
    {
        var (window, mods) = await ShowAsync(Main(new HangingModDb()));
        mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
        var page = (Control)window.Content!;
        var other = new Border { Background = Brushes.Gray };
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = new Panel { Children = { page, other } }; // как страницы главного окна: видна одна
        page.IsVisible = false;
        Mouse.Prepare(window);
        window.Focus();
        Esc(window);
        Back(window, other);
        Assert.Equal("alpha", mods.Selected?.ModId);
        Assert.NotNull(mods.Card);

        // страницу снова показали — Esc опять её
        other.IsVisible = false;
        page.IsVisible = true;
        Mouse.Prepare(window);
        Esc(window);
        Assert.Null(mods.Selected);
    }

    /// <summary>
    /// В главном окне: фокус в боковом меню — Esc всё равно снимает выбор на «Моих модах»; на экране другая страница —
    /// выбор в «Моих модах» остаётся. Каталог не открываем: он пошёл бы на модбазу.
    /// </summary>
    [AvaloniaFact]
    public async Task In_the_main_window_esc_belongs_to_the_page_on_screen()
    {
        var main = Main(new HangingModDb());
        var chrome = new ResourceInclude(Base) { Source = new Uri("avares://eViSTool/Themes/ForestWindowChrome.axaml") };
        Application.Current!.Resources.MergedDictionaries.Add(chrome);
        try
        {
            _window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
            _window.Show();
            var mods = main.Mods;
            await Wait.Until(() => mods.Rows.Count == 2);
            Mouse.Prepare(_window);

            mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
            _window.FindControl<ListBox>("Nav")!.Focus();
            Esc(_window);
            Assert.Null(mods.Selected);
            Assert.Null(mods.Card);

            mods.Selected = mods.Rows.Single(r => r.ModId == "alpha");
            main.SelectedTab = AppTab.Settings;
            Dispatcher.UIThread.RunJobs();
            Esc(_window);
            Back(_window, _window.FindControl<ListBox>("Nav")!);
            Assert.Equal("alpha", mods.Selected?.ModId);

            // вернулись на «Мои моды» — снова их Esc
            main.SelectedTab = AppTab.Mods;
            Dispatcher.UIThread.RunJobs();
            Back(_window, _window.FindControl<ListBox>("Nav")!);
            Assert.Null(mods.Selected);
        }
        finally
        {
            _window?.Close();
            _window = null;
            Application.Current!.Resources.MergedDictionaries.Remove(chrome);
        }
    }

    [AvaloniaFact]
    public async Task Catalog_esc_and_the_back_button_close_the_mod_card()
    {
        var main = Main(new HangingModDb());
        var catalog = main.Catalog;
        _window = new Window { Width = 1400, Height = 860, Content = new CatalogView { DataContext = catalog } };
        _window.Show();
        Mouse.Prepare(_window);
        TextBlock Pick() => _window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("catalog.pickMod"));
        Assert.True(Pick().IsEffectivelyVisible);

        var item = new CatalogItemViewModel(new ModDbListItem { ModId = 1, AssetId = 1, Name = "Alpha" }, new Dictionary<string, string>());
        catalog.Selected = item;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(catalog.Details);
        Assert.False(Pick().IsEffectivelyVisible);

        Esc(_window);
        Assert.Null(catalog.Selected);
        Assert.Null(catalog.Details);
        Assert.True(Pick().IsEffectivelyVisible);

        catalog.Selected = item;
        Dispatcher.UIThread.RunJobs();
        Back(_window, Pick().GetVisualParent()!);
        Assert.Null(catalog.Selected);
        Assert.Null(catalog.Details);
    }
}
