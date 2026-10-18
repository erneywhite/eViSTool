namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Вышла новая версия eViSTool» — раз за запуск программы. Не поверх игры: пока она запущена или запускается,
/// ждём её выхода. Версию, которую попросили пропустить, не предлагаем (ссылка в боковой панели остаётся) — кроме
/// рекомендуемой.
/// </summary>
/// <param name="about">Вкладка «О программе»: найденное обновление и его установка.</param>
/// <param name="skipped">Версия, которую попросили пропустить (из настроек).</param>
/// <param name="skip">Запомнить версию как пропущенную (кнопка в окне).</param>
/// <param name="gameBusy">Игра запущена, запускается или за ней ещё следим.</param>
public sealed class UpdatePrompt(AboutViewModel about, Func<string?> skipped, Action<string> skip, Func<bool> gameBusy)
{
    private bool _shown;
    private int _waits;

    /// <summary>Показать окно, если пора; иначе — ничего (позовут снова: нашлось обновление, закрыли игру).</summary>
    public void Show()
    {
        if (_shown || about.AvailableUpdate is not { } release || about.IsUpdating
            || (release.Version.ToString() == skipped() && !about.IsRecommended)
            || gameBusy())
            return;
        if (!Ui.Platform.IsMainWindowVisible)
        {
            // проверка ответила раньше, чем показалось главное окно, — повторить, когда программа освободится
            if (++_waits <= 20)
                Ui.PostIdle(Show);
            return;
        }
        _shown = true;
        // не модально: программой можно пользоваться, пока окно открыто
        AppWindows.Current.ShowUpdatePrompt(about, () => skip(release.Version.ToString()));
    }
}
