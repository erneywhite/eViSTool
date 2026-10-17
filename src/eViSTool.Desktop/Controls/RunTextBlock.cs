using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// TextBlock для подписи из нескольких Run: <c>&lt;c:RunTextBlock&gt;&lt;Run … /&gt;&lt;Run … /&gt;&lt;/c:RunTextBlock&gt;</c>.
/// Обходит ошибку Avalonia 12: кусок поменялся, пока подпись скрыта (страница окна не выбрана, строка таблицы ждёт
/// повторного использования), — после показа подпись пустая, пока кусок не поменяется ещё раз.
/// </summary>
/// <remarks>
/// Как это получается (проверено на Avalonia 12.1.2, RunInlinesTests): смена Run сбрасывает куски, собранные при
/// измерении (_textRuns), а измерение скрытого элемента откладывается. Кадр же рисует его и без раскладки: TextLayout
/// собирается из пустой строки, и разобранный текст попадает в TextRunCache (новый кэш строк в 12.x). После показа
/// измерение собирает куски заново, но кэш отдаёт ту пустую строку — ширина 0, на экране ничего. Обычный TextBlock
/// с Text этим не болеет: его текст есть и без измерения. Здесь, пока кусков нет, кадр получает пустую раскладку мимо
/// кэша, а ближайшее измерение её выбрасывает. Стили и тема — как у TextBlock.
/// shortcut: обход ошибки Avalonia, убрать, когда RunInlinesTests.PlainTextBlock_… покажет, что её починили.
/// </remarks>
public class RunTextBlock : TextBlock
{
    private bool _laidOutWithoutRuns;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        if (_textRuns is not null || Inlines is not { Count: > 0 }) return base.CreateTextLayout(text);
        // куски ещё не собраны (измерения после смены не было) — рисовать нечего; общий кэш строк не трогаем
        _laidOutWithoutRuns = true;
        return new TextLayout(string.Empty, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_laidOutWithoutRuns)
        {
            _laidOutWithoutRuns = false;
            base.OnMeasureInvalidated(); // пустая раскладка из кадра — в сторону, иначе измерение возьмёт её
        }
        return base.MeasureOverride(availableSize);
    }
}
