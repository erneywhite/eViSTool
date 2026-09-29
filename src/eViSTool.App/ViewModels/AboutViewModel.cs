using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;

namespace eViSTool.App.ViewModels;

/// <summary>Вкладка «О программе»: версия, ссылки, поддержка автора, проверка обновлений программы.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public const string KofiUrl = "https://ko-fi.com/erneywhite";

    public string AppVersion { get; } =
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    [ObservableProperty] private string _updateStatus = "";

    [RelayCommand]
    private static void OpenKofi() => Shell.OpenUrl(KofiUrl);

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Shell.OpenUrl(url);
    }

    /// <summary>
    /// Автообновление появится вместе с публичными релизами на GitHub (этап 6):
    /// проверка releases/latest → скачать exe → сверить хэш → переименовать себя в .old → положить новый → перезапуск.
    /// </summary>
    [RelayCommand]
    private void CheckAppUpdate() => UpdateStatus = Loc.T("about.updatesSoon");
}
