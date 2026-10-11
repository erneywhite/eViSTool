using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Заглушка: окно AlsoInstallWindow ещё не перенесено на Avalonia.</summary>
    public AlsoInstallAnswer? AskAlsoInstall(string hint, string currentName, string currentKind, string currentDetail, IReadOnlyList<AlsoChoice> others)
    {
        Stub("AlsoInstallWindow");
        return null;
    }
}
