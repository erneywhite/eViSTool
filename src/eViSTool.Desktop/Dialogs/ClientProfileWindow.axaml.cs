using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Новый клиентский профиль на основе существующего или его клон (DataContext — <see cref="ClientProfileViewModel"/>):
/// ответ — готовый <see cref="GameProfile"/> (папка уже создана), закрыли без ответа — null. Логика в модели, здесь —
/// вид: открылось окно — посчитать миры; «Отмена», Escape и крестик во время копирования его прерывают, а окно остаётся.
/// </summary>
public partial class ClientProfileWindow : DialogWindow
{
    private ClientProfileViewModel? _model;

    public ClientProfileWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as ClientProfileViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    // миры бывают большими — их размер модель считает в фоне, когда окно уже на экране
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_model is { } model) await model.LoadAsync();
    }

    // во время копирования окно не закрывается: копирование прерывается, окно ждёт, пока оно остановится и уберёт за собой
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_model?.CancelCopy() == true) e.Cancel = true;
    }

    // Enter на переключателях и галочке Avalonia оставляет им самим, и до кнопки по умолчанию он не доходит. Поэтому
    // он идёт на «Создать» / «Клонировать». На обычной кнопке Enter нажимает её саму, тут ничего не меняем.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is not ToggleButton) return;
        e.Handled = true;
        if (_model?.GoCommand is { } go && go.CanExecute(null)) go.Execute(null);
    }

    private void OnFinished(GameProfile profile) => Close(profile);

    // «Отмена» во время копирования — прервать его (окно остаётся, см. OnClosing), иначе — закрыть
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
