using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Бан игрока (DataContext — <see cref="BanViewModel"/>): ответ — <see cref="BanChoice"/>, закрыли без «Забанить» — null.
/// Здесь только вид: закрыть окно с ответом модели и Enter на чипе срока.
/// </summary>
public partial class BanWindow : DialogWindow
{
    private BanViewModel? _model;

    public BanWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as BanViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    private void OnFinished(BanChoice choice) => Close(choice);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    // Enter на чипе срока Avalonia оставляет самому чипу (переключатель его забирает), до кнопки по умолчанию он не
    // доходит. Поэтому он идёт на «Забанить». В поле причины и на кнопках Enter работает сам.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is not ToggleButton) return;
        e.Handled = true;
        if (_model?.OkCommand is { } ok && ok.CanExecute(null)) ok.Execute(null);
    }
}
