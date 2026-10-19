using eViSTool.Core.Localization;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Примерные имена в окнах для снимков — на языке окна: снимки идут в README на обоих языках.</summary>
internal static class Sample
{
    public static string Text(string ru, string en) => Loc.Instance.Language == "ru" ? ru : en;
}
