using System.IO;
using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Код набора (<see cref="ModCode"/>): короткая строка с модами и их версиями на модбазе. «Скопировать» собирает код из
/// включённых модов профиля, «Вставить» узнаёт моды у модбазы и ставит их обычным импортом модпака.
/// </summary>
internal sealed class ModCodeActions(ModsViewModel owner, ModPackActions packs, ModUpdateService service, ModDbClient db)
{
    /// <summary>
    /// Код набора включённых модов — в буфер обмена. В код попадает мод, у которого на модбазе есть ровно эта версия;
    /// остальные (поставлены из zip, версии там нет) перечисляются в сообщении.
    /// </summary>
    public async Task CopyAsync()
    {
        if (owner.Profile is null || owner.IsBusy) return;
        var mods = owner.Locals.Where(l => l.Info is not null && !l.IsDisabled).ToList();
        if (mods.Count == 0)
        {
            Error(Loc.T("modcode.noMods"));
            return;
        }

        owner.IsBusy = true;
        try
        {
            // модбазу могли спрашивать раньше, до установки части модов: про них дозапрашиваем, иначе они выпали бы
            // из кода как «нет на модбазе»
            var unknown = owner.Locals.Where(l => l.Info is not null && !owner.ModDbAnswers.ContainsKey(l.Info.ModId)).ToList();
            if (unknown.Count > 0)
            {
                owner.StatusText = Loc.T("check.querying", 0, unknown.Count);
                var progress = new StatusProgress(s => owner.StatusText = s);
                try
                {
                    foreach (var (id, r) in await service.FetchRemoteAsync(unknown, progress))
                        owner.ModDbAnswers[id] = r;
                }
                finally { progress.Close(); }
            }
            var entries = new List<ModCodeEntry>();
            var missing = new List<string>();
            foreach (var local in mods)
            {
                var info = local.Info!;
                if (owner.ModDbAnswers.TryGetValue(info.ModId, out var found) && found.Mod is { } mod)
                {
                    var releases = ModCode.OrderedReleases(mod);
                    var index = releases.FindIndex(r => SameVersion(r.ModVersion, info.Version));
                    if (index >= 0)
                    {
                        entries.Add(new ModCodeEntry(mod.ModId, index));
                        continue;
                    }
                }
                missing.Add($"{info.Name} {info.Version}");
            }
            if (entries.Count == 0)
            {
                Error(Loc.T("modcode.noneOnModDb"));
                return;
            }

            var code = ModCode.Encode(entries, owner.Profile?.GameVersion);
            Ui.Platform.SetClipboard(code);
            owner.StatusText = Loc.Plural("modcode.copiedStatus", entries.Count);
            AppWindows.Current.ShowModCode(new ModCodeShare(code, entries.Count, missing));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Error(Loc.T("modcode.failed", ex.Message));
        }
        finally
        {
            owner.IsBusy = false;
        }
    }

    private static bool SameVersion(string? a, string? b) =>
        ModVersion.ParseOrNull(a) is { } x && ModVersion.ParseOrNull(b) is { } y ? x.Equals(y) : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Поставить набор по коду: моды и версии узнаём у модбазы, собираем из них модпак без файлов внутри (всё
    /// скачивается с модбазы ровно этих версий) и ставим обычным импортом — с окном «что поставится», копиями миров
    /// и отчётом. Чего на модбазе уже нет — перечисляем до импорта.
    /// </summary>
    public async Task PasteAsync()
    {
        if (owner.Profile is not { } profile || owner.IsBusy) return;
        if (AppWindows.Current.AskModCode() is not { Entries.Count: > 0 } set) return;
        var entries = set.Entries;

        var manifest = new PackManifest
        {
            Name = Loc.T("modcode.packName"), Kind = profile.Profile.Kind, CreatedWith = $"eViSTool {ModPackActions.AppVersion}",
            GameVersion = set.GameVersion?.ToString(), // другая ветка игры — импорт предупредит, как у модпака
        };
        var missing = new List<string>();
        owner.IsBusy = true;
        try
        {
            owner.StatusText = Loc.T("modcode.resolving", entries.Count);
            var found = await db.GetModsAsync(entries.Select(e => e.ModDbId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var entry in entries)
            {
                var key = entry.ModDbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var mod = found.TryGetValue(key, out var r) ? r.Mod : null;
                var releases = mod is null ? [] : ModCode.OrderedReleases(mod);
                if (mod is null || entry.ReleaseIndex >= releases.Count)
                {
                    missing.Add(mod is null ? Loc.T("modcode.unknownMod", entry.ModDbId) : Loc.T("modcode.unknownRelease", mod.Name));
                    continue;
                }
                var release = releases[entry.ReleaseIndex];
                if (string.IsNullOrWhiteSpace(release.ModIdStr) || string.IsNullOrWhiteSpace(release.ModVersion))
                {
                    missing.Add(Loc.T("modcode.unknownRelease", mod.Name));
                    continue;
                }
                manifest.Mods.Add(new PackMod
                {
                    ModId = release.ModIdStr, Name = mod.Name ?? release.ModIdStr, Version = release.ModVersion,
                    FileName = release.FileName ?? "", Bundled = false,
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Error(Loc.T("modcode.failed", ex.Message));
            return;
        }
        finally
        {
            owner.IsBusy = false;
        }

        if (manifest.Mods.Count == 0)
        {
            Error(Loc.T("modcode.nothingFound") + (missing.Count > 0 ? "\n\n" + string.Join("\n", missing.Select(m => "• " + m)) : ""));
            return;
        }
        if (missing.Count > 0) Ui.Warn(Loc.T("modcode.someMissing", string.Join("\n", missing.Select(m => "• " + m))));

        // модпак без файлов — тем же импортом, что и .evpack
        var temp = Path.Combine(Path.GetTempPath(), $"evistool-code-{Guid.NewGuid():N}{PackManifest.Extension}");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(temp, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open()))
                writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented));
            await packs.ImportFileAsync(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private static void Error(string text) =>
        Ui.Warn(text);
}
