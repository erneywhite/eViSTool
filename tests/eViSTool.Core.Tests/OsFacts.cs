namespace eViSTool.Core.Tests;

// Тесты только для одной системы: на другой они отмечаются пропущенными, а не «прошли» после раннего return
// (аудит 6.12). В xunit 2 нет Assert.Skip — пропуск задаётся атрибутом.

/// <summary>Тест только для Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "только Windows";
    }
}

/// <summary>Тест только для Linux.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "только Linux";
    }
}

/// <summary>Тест для всего, кроме Windows (права файлов Unix и т. п.).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "не для Windows";
    }
}
