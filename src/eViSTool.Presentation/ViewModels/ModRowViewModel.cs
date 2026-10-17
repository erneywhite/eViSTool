using eViSTool.Core.Mods;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Цвет состояния в таблице и карточке.</summary>
public enum RowTone { Good, Update, Warn, Danger, Muted }

/// <summary>Строка таблицы модов.</summary>
public sealed class ModRowViewModel
{
    public ModRowViewModel(ModCheckResult r, IReadOnlyList<DependencyIssue> issues, bool pinned, ProfileKind profileKind,
        ModVersion? game = null)
    {
        Result = r;
        Local = r.Local;
        Kind = r.Status;
        IsDuplicate = r.IsDuplicate;
        IsEnabled = !r.Local.IsDisabled;
        IsHosterMod = r.Local.IsHosterMod;
        IsPinned = pinned;
        Name = r.Local.Info?.Name is { Length: > 0 } n ? n : r.Local.FileName;
        ModId = r.Local.Info?.ModId ?? "";
        FileName = r.Local.FileName;
        FilePath = r.Local.Path;
        Installed = r.Local.Info?.Version ?? "";
        Latest = r.LatestCompatible?.ModVersion ?? "";
        PageUrl = r.Remote?.PageUrl;
        Initials = MakeInitials(Name);
        CanUpdate = r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null;
        LatestArrow = r.Status == ModStatus.UpdateAvailable && Latest.Length > 0 ? "→ " + Latest : "";

        // этот мод требует чего-то, чего нет, — или его самого ждут другие моды в другой версии/включённым
        DependencyIssues = issues.Where(i => i.RequiredBy.Contains(Name)
                                             || (ModId.Length > 0 && string.Equals(i.ModId, ModId, StringComparison.OrdinalIgnoreCase)))
                                 .ToList();

        // мод требует игру новее, чем у профиля, — игра его не загрузит (выключенному — не важно)
        NeedsGame = IsEnabled ? r.Local.Info?.NeedsNewerGame(game) : null;

        IsProblem = !IsHosterMod && (IsDuplicate || DependencyIssues.Count > 0 || NeedsGame is not null
                    || r.Status is ModStatus.NoCompatibleRelease or ModStatus.Unreadable or ModStatus.CheckFailed);

        (StatusText, Tone) = IsHosterMod ? (Loc.T("status.hosterMod"), RowTone.Muted)
            : NeedsGame is { } need ? (Loc.T("state.needsGame", need), RowTone.Danger)
            : DependencyIssues.Count > 0 && IsEnabled ? (Loc.T("state.dependency"), RowTone.Danger)
            : IsDuplicate ? (Loc.T("state.duplicate"), RowTone.Danger)
            : r.Status switch
            {
                ModStatus.UpToDate => (Loc.T("status.upToDate"), RowTone.Good),
                ModStatus.UpdateAvailable => (Loc.T("status.updateAvailable"), RowTone.Update),
                ModStatus.NoCompatibleRelease => (Loc.T("status.noCompatible"), RowTone.Warn),
                ModStatus.NotInModDb => (Loc.T("status.notInModDb"), RowTone.Muted),
                ModStatus.Unreadable => (Loc.T("status.unreadable"), RowTone.Danger),
                ModStatus.CheckFailed => (Loc.T("status.checkFailed"), RowTone.Danger),
                ModStatus.NotChecked => (Loc.T("status.notChecked"), RowTone.Muted),
                ModStatus.Pinned => (Loc.T("status.pinned"), RowTone.Muted),
                _ => (r.Status.ToString(), RowTone.Muted),
            };

        SubState = !IsEnabled ? Loc.T("state.disabledInProfile") : "";

        Note = string.Join(" · ", new[]
        {
            IsHosterMod ? Loc.T("hosted.hosterMod") : null,
            NeedsGame is { } needed ? Loc.T("note.needsGame", needed, game) : null,
            r.Message,
            r.LatestAny is { } any ? Loc.T("note.newerElsewhere", any.ModVersion, string.Join(", ", any.GameVersions.TakeLast(1))) : null,
        }.Concat(DependencyIssues.Select(i => i.Describe())).Where(s => !string.IsNullOrEmpty(s)));

        Changelog = r.Status == ModStatus.UpdateAvailable ? Html.ToPlainText(r.LatestCompatible?.Changelog, 1500) : null;

        // сторона — из modinfo.json, как её видит игра; у «обеих сторон» (таких большинство) пометки нет
        Side = r.Local.Info is { } info ? ModSides.Parse(info.Side) : ModSide.Both;
        IsUnneeded = ModSides.IsUnneeded(Side, profileKind);
        SideText = Loc.T(Side switch { ModSide.Client => "mods.sideColClient", ModSide.Server => "mods.sideColServer", _ => "mods.sideColBoth" });
        SideNote = Side switch
        {
            ModSide.Client when IsUnneeded => " · " + Loc.T("mods.unneededOnServer"),
            // у игрока серверный мод работает в одиночной игре (её мир крутит встроенный сервер)
            ModSide.Server when profileKind == ProfileKind.Client => " · " + Loc.T("mods.forSingleplayer"),
            _ => "",
        };
    }

    /// <summary>Сторона из modinfo.json, как её видит игра.</summary>
    public ModSide Side { get; }

    /// <summary>Колонка «Сторона»: клиент / сервер / оба.</summary>
    public string SideText { get; }

    /// <summary>Пояснение после modid: « · серверу не нужен», « · для одиночной игры»; обычно пусто.</summary>
    public string SideNote { get; }

    /// <summary>Профилю мод не нужен (клиентский мод на выделенном сервере) — подсказка, не проблема.</summary>
    public bool IsUnneeded { get; }

    public ModCheckResult Result { get; }
    public LocalMod Local { get; }
    public ModStatus Kind { get; }
    public bool IsDuplicate { get; }
    public bool IsEnabled { get; }
    public bool IsPinned { get; }

    /// <summary>Мод самого хостера: только показываем, переключатель и действия недоступны.</summary>
    public bool IsHosterMod { get; }

    /// <summary>Мод можно выключать, закреплять, откатывать и удалять (не мод хостера).</summary>
    public bool CanChange => !IsHosterMod;

    /// <summary>Мод можно искать в каталоге: есть номер на модбазе, modid или имя из modinfo (у мода хостера ничего).</summary>
    public bool CanFindInCatalog => !IsHosterMod && (Result.Remote?.AssetId is > 0 || ModId.Length > 0 || Local.Info?.Name is { Length: > 0 });

    public string Name { get; }
    public string ModId { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public string Installed { get; }
    public string Latest { get; }
    public string? PageUrl { get; }

    /// <summary>Значок с буквами вместо логотипа: «Carry On» → «CO».</summary>
    public string Initials { get; }

    /// <summary>«→ 1.8.1» под установленной версией, если есть обновление.</summary>
    public string LatestArrow { get; }

    /// <summary>Можно обновить одной кнопкой: есть совместимый релиз с файлом.</summary>
    public bool CanUpdate { get; }

    /// <summary>Предлагается новая версия — её можно пропустить.</summary>
    public bool CanSkip => Kind == ModStatus.UpdateAvailable && !IsHosterMod;

    /// <summary>Требует внимания: дубликат, зависимость, нужна игра новее, нет версии под игру, не читается, ошибка проверки.</summary>
    public bool IsProblem { get; }

    public IReadOnlyList<DependencyIssue> DependencyIssues { get; }

    /// <summary>Какую версию игры мод требует, если она новее игры профиля; иначе null.</summary>
    public ModVersion? NeedsGame { get; }

    public string StatusText { get; }
    public RowTone Tone { get; }

    /// <summary>Вторая строка состояния: «Выключен в профиле».</summary>
    public string SubState { get; }

    public string Note { get; }
    public string? Changelog { get; }

    /// <summary>Порядок «сначала важное»: проблемы, обновления, остальное.</summary>
    public int Importance => IsProblem ? 0 : CanUpdate ? 1 : !IsEnabled ? 3 : 2;

    public static string MakeInitials(string name)
    {
        var words = name.Split([' ', '-', '_', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => char.IsLetterOrDigit(w[0])).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1)
        {
            // «BetterRuins» → «BR», «bushmeatstew» → «B»
            var caps = words[0].Where(char.IsUpper).Take(2).ToArray();
            return caps.Length == 2 ? new string(caps) : char.ToUpperInvariant(words[0][0]).ToString();
        }
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}
