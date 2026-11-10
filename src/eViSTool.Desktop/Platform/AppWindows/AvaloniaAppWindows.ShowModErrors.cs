using System.ComponentModel;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    // открытые «Ошибки модов» по профилю: второй щелчок по «!» поднимает уже открытое окно, а не плодит копии
    private readonly Dictionary<string, ModErrorsWindow> _modErrors = [];

    /// <summary>Тестам: открытые «Ошибки модов» по ID профиля.</summary>
    internal IReadOnlyDictionary<string, ModErrorsWindow> OpenModErrors => _modErrors;

    /// <summary>
    /// «Ошибки модов» профиля: не модально и не поверх всех окон, с владельцем — главным окном (из него и открывают, по
    /// «!» у профиля). Одно окно на профиль; пришёл новый отчёт этого профиля (игра ещё идёт или закрылась) — окно
    /// показывает его, а не застывший прежний.
    /// </summary>
    public void ShowModErrors(MainViewModel main, GameProfile profile, ModErrorReport report)
    {
        if (_modErrors.TryGetValue(profile.Id, out var open))
        {
            if ((open.DataContext as ModErrorsViewModel)?.Report != report)
                open.DataContext = ModErrorsViewModel.For(main, profile, report);
            open.Activate();
            return;
        }

        var window = new ModErrorsWindow { DataContext = ModErrorsViewModel.For(main, profile, report) };
        void OnMainChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.ErrorReport) || main.ActiveProfile?.Model.Id != profile.Id) return;
            if (main.ErrorReport is { Total: > 0 } fresh && (window.DataContext as ModErrorsViewModel)?.Report != fresh)
                window.DataContext = ModErrorsViewModel.For(main, profile, fresh);
        }
        main.PropertyChanged += OnMainChanged;
        window.Closed += (_, _) =>
        {
            main.PropertyChanged -= OnMainChanged;
            _modErrors.Remove(profile.Id);
        };
        _modErrors[profile.Id] = window;
        window.ShowModeless();
    }
}
