using System.Globalization;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Tests;

public sealed class DatesTests
{
    [Theory]
    [InlineData("ru-RU", "dd.MM")]
    [InlineData("lv-LV", "dd.MM")]
    [InlineData("en-US", "M/d")]
    [InlineData("en-GB", "dd/MM")]
    [InlineData("sv-SE", "MM-dd")] // ISO: год впереди
    [InlineData("ja-JP", "MM/dd")]
    public void DayMonth_DropsTheYear_KeepsTheRegionOrder(string culture, string expected) =>
        Assert.Equal(expected, Dates.DayMonthPattern(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Clock_FollowsTheRegion_NotTheInterfaceLanguage()
    {
        var t = new DateTime(2026, 10, 11, 16, 58, 0);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lv-LV");
            Assert.Equal("16:58", Dates.Clock(t));
            Assert.Equal("11.10", Dates.DayMonth(t));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("4:58 PM", Dates.Clock(t));
            Assert.Equal("10/11", Dates.DayMonth(t));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("ru", 2, 3, 5, 7, "2 д 3 ч 05 мин")]
    [InlineData("ru", 0, 3, 5, 7, "3 ч 05 мин")]
    [InlineData("ru", 0, 0, 5, 7, "5 мин 07 с")]
    [InlineData("en", 2, 3, 5, 7, "2d 3h 05m")]
    [InlineData("en", 0, 0, 5, 7, "5m 07s")]
    public void Uptime_IsInTheInterfaceLanguage(string lang, int days, int hours, int minutes, int seconds, string expected)
    {
        var saved = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(lang);
            Assert.Equal(expected, Dates.Uptime(new TimeSpan(days, hours, minutes, seconds)));
        }
        finally
        {
            Loc.Instance.SetLanguage(saved);
        }
    }
}
