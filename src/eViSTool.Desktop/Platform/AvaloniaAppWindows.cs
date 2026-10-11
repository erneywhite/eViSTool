using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Окна программы (<see cref="AppWindows"/>) для Avalonia. Каждый метод интерфейса лежит в своём файле
/// <c>Platform/AppWindows/AvaloniaAppWindows.&lt;Метод&gt;.cs</c>: окна переносят параллельно, и файлы не пересекаются.
/// </summary>
internal sealed partial class AvaloniaAppWindows : IAppWindows;
