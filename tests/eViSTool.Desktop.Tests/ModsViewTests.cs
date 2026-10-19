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
