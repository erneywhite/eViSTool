using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// «Назад» на странице: Esc и кнопка «Назад» мыши (XButton1, а также клавиша BrowserBack) выполняют команду —
/// <c>&lt;UserControl ctl:BackGesture.Command="{Binding ClearSelectionCommand}"&gt;</c>. На «Моих модах» и в «Каталоге»
/// это снять выбор мода: правая панель возвращается к своему пустому состоянию.
/// <list type="bullet">
/// <item>Слушает всё окно (фокус бывает и вне страницы, а кнопку мыши жмут над любым местом), но действует, только пока
/// страница видна: страницы окна прячутся через IsVisible.</item>
/// <item>Команда недоступна (выбора нет) — событие идёт дальше нетронутым.</item>
/// <item>Esc, который уже забрал кто-то другой (выпадающий список или меню закрылись), сюда не доходит: слушаем всплытие
/// и только необработанные. Пока в окне открыто меню или выпадающий список, страница Esc и «Назад» не трогает вовсе —
/// даже если само меню на Esc не закрывается («Играть ▾»). Диалоги — свои окна, их Esc сюда не попадает.</item>
/// <item>Фокус в поле поиска страницы (TextBox с классом search), и в нём есть текст, — Esc сначала очищает поле, как
/// принято у полей поиска (Ctrl+Z его возвращает), выбор остаётся; поле пустое — Esc снимает выбор. Фокус в любом
/// другом поле — Esc его, страница его не трогает.</item>
/// <item>Кнопку мыши слушаем и уже обработанную: строки таблицы и плитки забирают нажатие себе, а другого смысла у неё в
/// окне нет.</item>
/// </list>
/// </summary>
public static class BackGesture
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(BackGesture));

    // подписки на окно: снимаются, когда страницу убирают из окна или команду сбрасывают
    private static readonly ConditionalWeakTable<Control, Hook> Hooks = new();

    // открытые всплывающие окна (выпадающие списки, меню, «Играть ▾»): слабые ссылки — закрытое окно их не держит
    private static readonly ConditionalWeakTable<Popup, object> OpenPopups = new();

    static BackGesture()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.GetNewValue<bool>()) OpenPopups.AddOrUpdate(popup, popup);
            else OpenPopups.Remove(popup);
        });
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.AttachedToVisualTree -= OnAttached;
            control.DetachedFromVisualTree -= OnDetached;
            Unhook(control);
            if (e.NewValue is null) return;
            control.AttachedToVisualTree += OnAttached;
            control.DetachedFromVisualTree += OnDetached;
            if (TopLevel.GetTopLevel(control) is { } top) Hooks.AddOrUpdate(control, new Hook(control, top));
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);
    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;
        Unhook(control);
        if (TopLevel.GetTopLevel(control) is { } top) Hooks.AddOrUpdate(control, new Hook(control, top));
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Unhook(control);
    }

    private static void Unhook(Control control)
    {
        if (!Hooks.TryGetValue(control, out var hook)) return;
        Hooks.Remove(control);
        hook.Dispose();
    }

    /// <summary>
    /// Открыто ли в окне меню или выпадающий список — всплывающее окно, которое закрывается щелчком мимо (подсказки не в
    /// счёт). Пока открыто, Esc и «Назад» — его: даже то, что само на Esc не закрывается («Играть ▾»), не должно снимать
    /// выбор у страницы под собой.
    /// </summary>
    private static bool HasOpenMenu(TopLevel top)
    {
        foreach (var (popup, _) in OpenPopups)
        {
            if (!popup.IsOpen || !popup.IsLightDismissEnabled) continue;
            // меню строки (ContextMenu) не лежит в дереве окна — его окно узнаём по тому, к чему оно открыто
            var owner = TopLevel.GetTopLevel(popup) ?? (popup.PlacementTarget is { } target ? TopLevel.GetTopLevel(target) : null);
            if (owner == top) return true;
        }
        return false;
    }

    private sealed class Hook : IDisposable
    {
        private readonly Control _page;
        private readonly TopLevel _top;

        public Hook(Control page, TopLevel top)
        {
            _page = page;
            _top = top;
            top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
            top.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        public void Dispose()
        {
            _top.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _top.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Escape or Key.BrowserBack) || !_page.IsEffectivelyVisible
                || HasOpenMenu(_top)) return;
            // клавиша «Назад» (на мультимедийных клавиатурах; её же шлют мыши, у которых кнопку переназначили) — как
            // кнопка мыши: у полей ввода своего смысла у неё нет
            if (e.Key == Key.BrowserBack)
            {
                if (Run()) e.Handled = true;
                return;
            }
            if (_top.FocusManager?.GetFocusedElement() is TextBox box)
            {
                if (!box.Classes.Contains("search") || !_page.IsVisualAncestorOf(box)) return;
                if (!string.IsNullOrEmpty(box.Text) && !box.IsReadOnly)
                {
                    box.Clear(); // правка поля, как любая другая: Ctrl+Z возвращает текст
                    e.Handled = true;
                    return;
                }
            }
            if (Run()) e.Handled = true;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_page.IsEffectivelyVisible || e.GetCurrentPoint(_page).Properties.PointerUpdateKind != PointerUpdateKind.XButton1Pressed
                || HasOpenMenu(_top)) return;
            if (Run()) e.Handled = true;
        }

        private bool Run()
        {
            if (GetCommand(_page) is not { } command || !command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
        }
    }
}
