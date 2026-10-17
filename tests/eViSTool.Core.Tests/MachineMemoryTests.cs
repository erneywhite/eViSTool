using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

/// <summary>Память машины на Linux: MemTotal, урезанный пределами cgroup (v2 и v1), — на поддельной файловой системе.</summary>
public sealed class MachineMemoryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-mem-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Put(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private const long Mb = 1024 * 1024;

    [Fact]
    public void NoLimit_IsTheWholeMemory()
    {
        // как на ВМ .70: юнит evistool без MemoryMax
        Put("proc/meminfo", "MemTotal:        4009820 kB\nMemFree:          100 kB\n");
        Put("proc/self/cgroup", "0::/system.slice/evistool.service\n");
        Put("sys/fs/cgroup/system.slice/evistool.service/memory.max", "max\n");

        Assert.Equal(4009820L * 1024, MachineMemory.Linux(_root));
    }

    [Fact]
    public void V2_TheSmallestLimitOnThePathToTheRoot()
    {
        // предел юнита 1 ГБ (MemoryMax=1G), у родительского slice — 3 ГБ: берётся 1 ГБ, а не 75 % от него, как у .NET
        Put("proc/meminfo", "MemTotal:        4009820 kB\n");
        Put("proc/self/cgroup", "0::/system.slice/evistool.service\n");
        Put("sys/fs/cgroup/system.slice/evistool.service/memory.max", "1073741824\n");
        Put("sys/fs/cgroup/system.slice/memory.max", (3 * 1024 * Mb).ToString());
        Assert.Equal(1024 * Mb, MachineMemory.Linux(_root));

        // предел только у родителя
        Put("sys/fs/cgroup/system.slice/evistool.service/memory.max", "max");
        Assert.Equal(3 * 1024 * Mb, MachineMemory.Linux(_root));
    }

    [Fact]
    public void V1_DockerLimit_AndTheHugeNumberMeansNoLimit()
    {
        Put("proc/meminfo", "MemTotal:        8000000 kB\n");
        Put("proc/self/cgroup", "12:pids:/docker/abc\n7:memory:/docker/abc\n");
        Put("sys/fs/cgroup/memory/docker/abc/memory.limit_in_bytes", (2048 * Mb).ToString());
        Assert.Equal(2048 * Mb, MachineMemory.Linux(_root));

        Put("sys/fs/cgroup/memory/docker/abc/memory.limit_in_bytes", "9223372036854771712");
        Assert.Equal(8000000L * 1024, MachineMemory.Linux(_root));
    }

    [Fact]
    public void WithoutMeminfo_IsUnknown() => Assert.Null(MachineMemory.Linux(_root));
}
