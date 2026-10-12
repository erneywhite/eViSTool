using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Код набора»: готовый код блоком (щелчок — скопировать ещё раз, если в буфер не попало), сколько в нём модов
/// и какие моды в код не вошли.
/// </summary>
public sealed partial class ModCodeShare(string code, int modCount, IReadOnlyList<string> missing) : ObservableObject
{
    public string Code { get; } = code;

    /// <summary>Код для показа: дефис неразрывный, иначе строка ломается сразу после «EV1-» (разбор понимает оба).</summary>
    public string ShownCode => Code.Replace('-', '‑');

    public string Summary { get; } = Loc.T("modcode.shareSummary", Loc.Plural("modcode.mods", modCount), code.Length);
    public string MissingText { get; } = missing.Count == 0 ? "" : Loc.T("modcode.missing", string.Join("\n", missing.Select(m => "• " + m)));
    public bool HasMissing => MissingText.Length > 0;

    /// <summary>«Скопировано» под блоком после щелчка или кнопки.</summary>
    [ObservableProperty] private string _copiedText = "";

    public void MarkCopied() => CopiedText = Loc.T("modcode.copiedAgain");
}
