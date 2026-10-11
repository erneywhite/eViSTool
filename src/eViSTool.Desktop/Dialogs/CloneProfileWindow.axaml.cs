using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Клон серверного профиля и импорт существующих данных сервера (DataContext — <see cref="CloneProfileViewModel"/>):
/// ответ — готовый <see cref="GameProfile"/>, закрыли без копирования — null. Здесь только вид: закрыть окно с
/// профилем, «Отмена» и закрытие во время копирования прерывают копирование (окно остаётся), Enter на переключателях.
/// </summary>
public partial class CloneProfileWindow : DialogWindow
{
    private CloneProfileViewModel? _model;

    public CloneProfileWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as CloneProfileViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    private void OnFinished(GameProfile profile) => Close(profile);

    // «Отмена» во время копирования — прервать его (окно остаётся), иначе — закрыть
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_model?.CancelCopy() != true) Close();
    }

    // крестик и Escape во время копирования — тоже только прервать: дождаться, пока копирование остановится и уберёт за собой
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_model?.CancelCopy() == true) e.Cancel = true;
    }

    // Enter на переключателях и галочке Avalonia оставляет им самим, а закрытый список по Enter раскрывается — до кнопки
    // по умолчанию он не доходит. В окне на WPF он шёл на «Клонировать» — здесь так же. В полях ввода и на кнопках Enter
    // работает сам: поле его не забирает, кнопка нажимается.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source) return;
        if (source is not (ToggleButton or ComboBox { IsDropDownOpen: false })) return;
        e.Handled = true;
        if (_model?.GoCommand is { } go && go.CanExecute(null)) go.Execute(null);
    }
}
