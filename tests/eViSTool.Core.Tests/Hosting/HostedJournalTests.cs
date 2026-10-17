using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Доведение прерванной установки по журналу: по тому, что осталось на сервере.</summary>
public sealed class HostedJournalTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public void Dispose() => _host.Dispose();

    private (HostedPending Pending, string Copy) Journal(long size, bool withCopy = true)
    {
        var dir = Directory.CreateDirectory(Path.Combine(HostedJournal.DirFor(_host.Profile.Id), "old", "j1")).FullName;
        var copy = Path.Combine(dir, "alpha_1.0.0.zip");
        if (withCopy) File.WriteAllBytes(copy, HostedFixture.ZipBytes("alpha", "1.0.0"));
        var pending = new HostedPending("/Mods/alpha_1.1.0.zip.evistool.tmp", "/Mods/alpha_1.1.0.zip", size,
            [new HostedPendingOld("/Mods/alpha_1.0.0.zip", copy, 100)], "alpha", "Alpha", "1.0.0", "1.1.0");
        HostedJournal.Write(_host.Profile.Id, pending);
        return (pending, copy);
    }

    private async Task<bool> FinishAsync(HostedPending pending)
    {
        await _host.Server.ConnectAsync(default);
        return await HostedJournal.FinishAsync(_host.Server, _host.Profile.Id, pending, default);
    }

    [Fact]
    public async Task RenameHadHappened_OnlyTheBookkeepingIsLeft()
    {
        _host.PutMod("/Mods/alpha_1.1.0.zip", "alpha", "1.1.0");
        var (pending, _) = Journal(size: 1);

        Assert.True(await FinishAsync(pending));

        Assert.Equal(["alpha_1.1.0.zip"], _host.Server.Names("/Mods"));
        Assert.Empty(_host.Server.Writes);
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
        Assert.Equal(("1.0.0", "1.1.0"), (ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Single().From,
            ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Single().To));
    }

    [Fact]
    public async Task UploadedButOldStillThere_IsFinished_OldRemovedNewInPlace()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var bytes = HostedFixture.ZipBytes("alpha", "1.1.0");
        _host.Server.Put("/Mods/alpha_1.1.0.zip.evistool.tmp", bytes);
        var (pending, _) = Journal(size: bytes.Length);

        Assert.True(await FinishAsync(pending));

        Assert.Equal(["alpha_1.1.0.zip"], _host.Server.Names("/Mods"));
    }

    [Fact]
    public async Task NoLocalCopyOfAnOldFileStillThere_IsRolledBack_NotDeletedBlindly()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var bytes = HostedFixture.ZipBytes("alpha", "1.1.0");
        _host.Server.Put("/Mods/alpha_1.1.0.zip.evistool.tmp", bytes);
        var (pending, _) = Journal(size: bytes.Length, withCopy: false);

        Assert.False(await FinishAsync(pending));

        Assert.Equal(["alpha_1.0.0.zip"], _host.Server.Names("/Mods"));
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        Assert.Empty(ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)));
    }

    [Fact]
    public async Task TargetTakenBySomethingElse_IsRolledBack()
    {
        _host.PutMod("/Mods/alpha_1.1.0.zip", "other", "9.0.0"); // кто-то положил туда свой файл
        var bytes = HostedFixture.ZipBytes("alpha", "1.1.0");
        _host.Server.Put("/Mods/alpha_1.1.0.zip.evistool.tmp", bytes);
        var (pending, _) = Journal(size: bytes.Length);

        Assert.False(await FinishAsync(pending));

        Assert.Equal(["alpha_1.0.0.zip", "alpha_1.1.0.zip"], _host.Server.Names("/Mods"));
        Assert.Equal("9.0.0", ModScanner.ReadZip(Path.Combine(_host.Server.Root, "Mods", "alpha_1.1.0.zip")).Info!.Version);
    }

    [Fact]
    public async Task RollBack_AfterTheRenameWentThrough_DoesNotPutTheOldVersionBack()
    {
        // ответ на переименование потерялся, а сервер его выполнил: временного файла нет, новая версия на месте
        _host.PutMod("/Mods/alpha_1.1.0.zip", "alpha", "1.1.0");
        var (pending, _) = Journal(size: 1);
        await _host.Server.ConnectAsync(default);

        Assert.False(await HostedJournal.RollBackAsync(_host.Server, pending, default));

        Assert.Equal(["alpha_1.1.0.zip"], _host.Server.Names("/Mods"));
        Assert.Empty(_host.Server.Writes);
    }

    [Fact]
    public async Task LocalFailureWhileKeepingCopies_DoesNotBlockTheJournal()
    {
        _host.PutMod("/Mods/alpha_1.1.0.zip", "alpha", "1.1.0");
        var (pending, _) = Journal(size: 1);
        // на месте папки хранилища версий профиля — файл: перенести копию туда не выйдет
        var store = Path.Combine(AppPaths.ModBackups, _host.Profile.Id);
        Directory.CreateDirectory(AppPaths.ModBackups);
        File.WriteAllText(store, "in the way");
        try
        {
            Assert.True(await FinishAsync(pending));
            Assert.Null(HostedJournal.Read(_host.Profile.Id));
        }
        finally
        {
            File.Delete(store);
        }
    }

    [Fact]
    public async Task InstallWaitsWhileAnInterruptedOneCannotBeFinished()
    {
        _host.GPortalLayout();
        // старой версии нет на сервере, а залить её копию сервер не даёт — журнал не довести
        var (pending, _) = Journal(size: 1);
        _host.Server.FailOn = (op, path) => op == "upload" && path == "/Mods/alpha_1.0.0.zip";

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            ModTargets.InstallAsync(_host.Target, _host.Zip("beta_1.0.0.zip", "beta", "1.0.0")));

        Assert.Contains(HostedTestText.Of("hosted.errPendingInstall"), t => ex.Message.StartsWith(t[..t.IndexOf("{0}", StringComparison.Ordinal)], StringComparison.Ordinal));
        Assert.Equal(pending, HostedJournal.Read(_host.Profile.Id), new PendingComparer());
        Assert.False(_host.Server.Has("/Mods/beta_1.0.0.zip"));
    }

    private sealed class PendingComparer : IEqualityComparer<HostedPending?>
    {
        public bool Equals(HostedPending? a, HostedPending? b) => a?.Tmp == b?.Tmp && a?.Target == b?.Target;
        public int GetHashCode(HostedPending? p) => 0;
    }
}
