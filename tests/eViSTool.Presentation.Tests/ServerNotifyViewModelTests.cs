using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Вкладка «Оповещения» раздела «Сервер»: переход в «Настройки» из подсказки «каналов ещё нет».</summary>
public sealed class ServerNotifyViewModelTests
{
    // «Открыть настройки» — та же команда, что у раздела «Сервер»: окно переходит на вкладку «Настройки».
    // Главная модель настоящая: ей принадлежит выбранная вкладка. Данные она берёт из папки data рядом с тестами.
    [Fact]
    public void OpenSettings_SwitchesMainWindowToSettings() => Sta.Run(() =>
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        try
        {
            var main = new MainViewModel { SelectedTab = AppTab.Server };

            main.Server.Notify.OpenSettingsCommand.Execute(null);

            Assert.Equal(AppTab.Settings, main.SelectedTab);
        }
        finally { Loc.Instance.SetLanguage(language); }
    });

    // команда доступна всегда: без каналов она единственный путь к их добавлению
    [Fact]
    public void OpenSettings_AlwaysCanExecute() => Sta.Run(() =>
    {
        var notify = new ServerNotifyViewModel(new ServerViewModel(null!));

        Assert.True(notify.OpenSettingsCommand.CanExecute(null));
    });
}
