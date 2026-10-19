using Avalonia.Controls.Shapes;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Кольцо «дыхания» вокруг точки состояния (docs/design.md, раздел 3): круг под точкой, который стиль
/// <c>BreathingRing.breathing</c> из Forest.axaml раз в 2,6 с раздувает и гасит. Свой тип нужен только этому стилю:
/// анимация масштаба (ScaleTransform.ScaleX) ещё при подключении стиля ставит RenderTransform локальным значением
/// каждому элементу, к которому стиль может подойти (класс ведь может появиться позже). Со стилем на любой Ellipse
/// вставала ручка переключателя — её сдвигает стиль темы, а локальное значение сильнее стиля.
/// </summary>
public sealed class BreathingRing : Ellipse;
