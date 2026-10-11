using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Окна программы (<see cref="AppWindows"/>) для Avalonia. Каждый метод интерфейса лежит в своём файле
/// <c>Platform/AppWindows/AvaloniaAppWindows.&lt;Метод&gt;.cs</c>: окна переносят параллельно, и файлы не пересекаются.
/// Пока окно не перенесено, метод — заглушка: сообщает об этом и возвращает «ничего не выбрано». Здесь — только общие помощники.
/// </summary>
internal sealed partial class AvaloniaAppWindows : IAppWindows
{
    /// <summary>Временная заглушка не перенесённого окна: сообщение вместо него. Пропадает вместе с заглушками.</summary>
    private static void Stub(string window) =>
        Ui.Info($"Это окно ещё не перенесено на новое окно программы: {window}.");
}
