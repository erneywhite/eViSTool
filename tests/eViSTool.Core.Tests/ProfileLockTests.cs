using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Один агент на профиль (<see cref="ProfileLock"/>): второй на тот же профиль не запускается, на другой — запускается,
/// копия после обновления дожидается, пока прежняя выйдет. Каждый агент — свой поток: на Windows замок — именованный
/// Mutex, а его владелец — поток (тот же поток вошёл бы в свой Mutex повторно).
/// </summary>
public sealed class ProfileLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-lock-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile = "test-" + Guid.NewGuid().ToString("N")[..8]; // не пересечься с настоящими агентами

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>«Агент» в своём потоке: занимает профиль и держит, пока не скажут выйти.</summary>
    private sealed class Agent : IDisposable
    {
        private readonly ManualResetEventSlim _taken = new();
        private readonly ManualResetEventSlim _exit = new();
        private readonly Thread _thread;
        public bool Got { get; private set; }

        public Agent(string profile, string dir, TimeSpan wait)
        {
            _thread = new Thread(() =>
            {
                using (var held = ProfileLock.Take(profile, dir, wait))
                {
                    Got = held is not null;
                    _taken.Set();
                    _exit.Wait();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        /// <summary>Дождаться, чем кончилась попытка занять профиль.</summary>
        public bool WaitTaken(TimeSpan timeout) => _taken.Wait(timeout) && Got;

        /// <summary>Выйти, как выходит процесс агента: замок снимается вместе с ним.</summary>
        public void Exit()
        {
            _exit.Set();
            _thread.Join();
        }

        public void Dispose()
        {
            if (_thread.IsAlive) Exit();
            _taken.Dispose();
            _exit.Dispose();
        }
    }

    [Fact]
    public void SecondAgent_OnTheSameProfile_IsRefused()
    {
        using var first = new Agent(_profile, _dir, TimeSpan.Zero);
        Assert.True(first.WaitTaken(TimeSpan.FromSeconds(5)));

        using var second = new Agent(_profile, _dir, TimeSpan.Zero);
        Assert.False(second.WaitTaken(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AnotherProfile_IsNotBlocked()
    {
        using var first = new Agent(_profile, _dir, TimeSpan.Zero);
        Assert.True(first.WaitTaken(TimeSpan.FromSeconds(5)));

        using var other = new Agent(_profile + "-other", _dir, TimeSpan.Zero);
        Assert.True(other.WaitTaken(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CopyAfterUpdate_WaitsForThePreviousOne()
    {
        using var previous = new Agent(_profile, _dir, TimeSpan.Zero);
        Assert.True(previous.WaitTaken(TimeSpan.FromSeconds(5)));

        // новая копия ждёт до 20 секунд; прежняя выходит через полсекунды — профиль переходит к новой
        using var next = new Agent(_profile, _dir, TimeSpan.FromSeconds(20));
        Thread.Sleep(500);
        previous.Exit();
        Assert.True(next.WaitTaken(TimeSpan.FromSeconds(20)));
    }
}
