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
}
