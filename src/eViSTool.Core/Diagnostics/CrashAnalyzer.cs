using System.Text.RegularExpressions;

namespace eViSTool.Core.Diagnostics;

public enum GameExitKind
{
    /// <summary>Игра закрылась с ошибкой (Exit reason: Game crashed, client-crash.log).</summary>
    Crashed,
    /// <summary>Игра работает, но мир закрылся из-за ошибки (экран отключения: встроенный сервер упал).</summary>
    WorldClosed,
    /// <summary>Выделенный сервер остановился сам: упал или выключился от числа ошибок.</summary>
    ServerDown,
}

/// <summary>Как найден виновник: сама игра назвала мод, его код в стеке, его метка в сообщении, его строка перевода.</summary>
public enum CulpritSource { GameReport, Stack, Message, Translation }

public sealed record ModCulprit(ModPrint? Mod, string ModId, string? Version, CulpritSource Source);

/// <summary>Что случилось: вылет или закрытие мира, причина от игры, ошибка, стек, виновник (если удалось найти).</summary>
public sealed record CrashFinding(GameExitKind Kind, string Reason, string? Error, IReadOnlyList<string> Stack, ModCulprit? Culprit,
    int ErrorCount);

/// <summary>
/// Разбор вылета по логам сессии игры. Игра пишет: при вылете — «Exit reason: Game crashed» и client-crash.log, где сама
/// называет мод («Critical error occurred in the following mod: id@версия»); при падении встроенного сервера —
/// «Exiting current game to disconnected screen, reason: …», а ошибки со стеком — в server-main.log. Виновника ищем:
/// по слову игры, по стеку (пространства имён сборок модов), по строке перевода, на которой упало форматирование.
/// </summary>
public static partial class CrashAnalyzer
{
    private const string DisconnectedPrefix = "Exiting current game to disconnected screen, reason: ";
    private const string MainMenuPrefix = "Exiting current game to main menu, reason: ";
    private const string NormalLeave = "leave world button pressed";
    private const string TranslationPrefix = "Translation string format exception thrown for: ";

    /// <summary>
    /// Сервер выгнал или забанил игрока: это не вылет, даже если в логе есть ошибки модов (они могли случиться раньше,
    /// например при входе). Причину игра пишет на языке игрока — тексты из её lang/en.json и ru.json:
    /// «You've been kicked by {0}[, reason: {1}]», «You've been banned by {0}{1}» и «Вас выгнал {0}[, причина: {1}]»,
    /// «Вы были забанены {0}{1}».
    /// </summary>
    [GeneratedRegex(@"^(?:You've been kicked by |You've been banned by |Вас выгнал |Вы были забанены )")]
    private static partial Regex KickedOrBanned();

    [GeneratedRegex(@"Critical error occurred in the following mod: (?<id>[^@\s]+)(?:@(?<ver>\S+))?")]
    private static partial Regex CriticalMod();

    [GeneratedRegex(@"Crash written to file at ""(?<path>[^""]+)""")]
    private static partial Regex CrashFileLine();

    private const string TooManyErrors = "errors detected. Shutting down now";

    /// <summary>Путь к отчёту о вылете, если игра или сервер его записали («Crash written to file at "…"»).</summary>
    public static string? CrashFilePath(IEnumerable<string> lines) =>
        lines.Select(l => CrashFileLine().Match(l)).FirstOrDefault(m => m.Success)?.Groups["path"].Value;

    /// <summary>
    /// Выделенный сервер остановился не по нашей команде: разбор его вывода за этот запуск. Отчёт о вылете (сервер сам
    /// назвал мод), «too many errors» или просто падение процесса (<paramref name="crashedExit"/>) — находка; тихая
    /// остановка без ошибок (админ набрал /stop в игре) — null.
    /// </summary>
    public static CrashFinding? AnalyzeServer(IReadOnlyList<LogEntry> entries, string? crashReport, bool crashedExit, ModFingerprints mods)
    {
        var text = string.Join("\n", entries.SelectMany(e => e.Details.Prepend(e.Message)));
        if (crashReport is not null || CriticalMod().IsMatch(text))
            return FromCrash(entries, crashReport ?? text, null, mods) with { Kind = GameExitKind.ServerDown };

        var tooMany = entries.Any(e => e.Message.Contains(TooManyErrors, StringComparison.Ordinal));
        var errors = Errors(entries).Where(e => !e.Entry.Message.Contains(TooManyErrors, StringComparison.Ordinal)).ToList();
        if (!tooMany && !crashedExit) return null;
        var reason = tooMany ? "Too many errors" : "Server process crashed";
        if (errors.Count == 0) return new CrashFinding(GameExitKind.ServerDown, reason, null, [], null, 0);
        var (culprit, last, count) = Blame(errors, mods);
        return new CrashFinding(GameExitKind.ServerDown, reason, last.Entry.Message, [.. last.Entry.StackFrames], culprit, count);
    }

    // метки, которыми моды подписывают свои сообщения: «[carryon] …», «[PlayerModelLib] [CustomModelsSystem] …», «… for mod X»
    [GeneratedRegex(@"^\s*(?:\[(?<tag>[^\]\[]{2,60})\]\s*)+")]
    private static partial Regex LeadingTags();

    [GeneratedRegex(@"\b(?:for|in|from) mod '?(?<tag>[\w.\-]{2,60})'?")]
    private static partial Regex ForMod();

    /// <param name="client">Записи client-main.log за сессию игры.</param>
    /// <param name="crashReport">client-crash.log, если он появился за эту сессию.</param>
    /// <param name="server">Записи server-main.log за сессию (одиночная игра — встроенный сервер).</param>
    /// <returns>null — ничего не случилось (обычный выход).</returns>
    public static CrashFinding? Analyze(IReadOnlyList<LogEntry> client, string? crashReport, IReadOnlyList<LogEntry>? server, ModFingerprints mods)
    {
        var crashed = crashReport is not null || client.Any(e => e.Message.Contains("Exit reason: Game crashed", StringComparison.Ordinal));
        if (crashed) return FromCrash(client, crashReport, server, mods);

        var exit = client.LastOrDefault(e => e.Message.StartsWith(DisconnectedPrefix, StringComparison.Ordinal)
                                             || (e.Message.StartsWith(MainMenuPrefix, StringComparison.Ordinal)
                                                 && !e.Message.EndsWith(NormalLeave, StringComparison.Ordinal)));
        if (exit is null) return null;
        var reason = exit.Message.StartsWith(DisconnectedPrefix, StringComparison.Ordinal)
            ? exit.Message[DisconnectedPrefix.Length..] : exit.Message[MainMenuPrefix.Length..];
        if (KickedOrBanned().IsMatch(reason)) return null; // выгнали или забанили — мир закрылся не из-за ошибки

        // мир закрыт — ошибки ищем в логах обеих сторон; ошибок нет (сервер перезагрузили, кикнули) — это не вылет
        var errors = Errors(server ?? []).Concat(Errors(client)).ToList();
        if (errors.Count == 0) return null;
        var (culprit, last, count) = Blame(errors, mods);
        return new CrashFinding(GameExitKind.WorldClosed, reason, last.Entry.Message, [.. last.Entry.StackFrames], culprit, count);
    }

    private static CrashFinding FromCrash(IReadOnlyList<LogEntry> client, string? crashReport, IReadOnlyList<LogEntry>? server, ModFingerprints mods)
    {
        // текст отчёта: файл, а если его нет — запись [Fatal] в логе (там то же самое)
        var text = crashReport ?? string.Join("\n", client.Where(e => e.Level == "Fatal").SelectMany(e => e.Details.Prepend(e.Message)));
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var error = lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith(' ') && l.Contains("Exception", StringComparison.Ordinal)
                                              && !l.StartsWith("Loaded Mods", StringComparison.Ordinal));
        var stack = GameLog.Frames(lines).ToList();

        ModCulprit? culprit = null;
        if (CriticalMod().Match(text) is { Success: true } m)
        {
            var id = m.Groups["id"].Value;
            var mod = mods.ByModId(id);
            culprit = new ModCulprit(mod, mod?.ModId ?? id, m.Groups["ver"].Success ? m.Groups["ver"].Value : mod?.Version, CulpritSource.GameReport);
        }
        else if (mods.ByStack(stack) is { } byStack)
            culprit = new ModCulprit(byStack, byStack.ModId, byStack.Version, CulpritSource.Stack);

        return new CrashFinding(GameExitKind.Crashed, "Game crashed", error, stack, culprit, 1);
    }

    /// <summary>Ошибка лога и (если за ней идёт) строка перевода, на которой она случилась.</summary>
    public sealed record ErrorEntry(LogEntry Entry, string? Translation);

    /// <summary>Записи [Error]/[Fatal] со стеком; к ошибке форматирования перевода — сама строка перевода из следующей записи.</summary>
    public static IEnumerable<ErrorEntry> Errors(IReadOnlyList<LogEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsError) continue;
            string? translation = null;
            if (i + 1 < entries.Count && entries[i + 1].Level == "Warning"
                && entries[i + 1].Message.StartsWith(TranslationPrefix, StringComparison.Ordinal))
                translation = entries[i + 1].Message[TranslationPrefix.Length..].Trim().Trim('"');
            yield return new ErrorEntry(entries[i], translation);
        }
    }

    /// <summary>Чья это ошибка: по стеку, по метке мода в сообщении, по строке перевода.</summary>
    public static ModCulprit? Attribute(ErrorEntry error, ModFingerprints mods)
    {
        if (mods.ByStack(error.Entry.StackFrames) is { } byStack) return new ModCulprit(byStack, byStack.ModId, byStack.Version, CulpritSource.Stack);
        if (ByMessage(error.Entry.Message, mods) is { } byTag) return new ModCulprit(byTag, byTag.ModId, byTag.Version, CulpritSource.Message);
        if (error.Translation is { } t && mods.ByTranslation(t) is { } byText)
            return new ModCulprit(byText, byText.ModId, byText.Version, CulpritSource.Translation);
        return null;
    }

    private static ModPrint? ByMessage(string message, ModFingerprints mods)
    {
        if (LeadingTags().Match(message) is { Success: true } lead)
            foreach (Capture tag in lead.Groups["tag"].Captures)
                if (mods.ByTag(tag.Value) is { } mod) return mod;
        return ForMod().Match(message) is { Success: true } m ? mods.ByTag(m.Groups["tag"].Value) : null;
    }

    /// <summary>Виновник среди многих ошибок — мод, на которого их больше всего; и последняя его ошибка (для показа).</summary>
    private static (ModCulprit? Culprit, ErrorEntry Last, int Count) Blame(List<ErrorEntry> errors, ModFingerprints mods)
    {
        var attributed = errors.Select(e => (Error: e, Culprit: Attribute(e, mods))).ToList();
        var top = attributed.Where(a => a.Culprit is not null)
            .GroupBy(a => a.Culprit!.ModId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return top is null
            ? (null, errors[^1], errors.Count)
            : (top.Last().Culprit, top.Last().Error, top.Count());
    }
}
