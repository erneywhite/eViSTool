using eViSTool.App.ViewModels;

namespace eViSTool.App.Tests;

public class ScheduleTimeTests
{
    // в поле расписания — часы окна, агенту — часы сервера
    [Theory]
    [InlineData("18:00", 180, "21:00")]   // сервер UTC, окно UTC+3
    [InlineData("22:30", 180, "01:30")]   // через полночь
    [InlineData("01:30", -180, "22:30")]  // обратно
    [InlineData("05:00", 330, "10:30")]   // пояс с получасом
    [InlineData("05:00", 0, "05:00")]
    [InlineData("не время", 180, "не время")]
    public void ShiftTime_MovesAroundTheClock(string time, int minutes, string expected) =>
        Assert.Equal(expected, ServerScheduleViewModel.ShiftTime(time, minutes));
}
