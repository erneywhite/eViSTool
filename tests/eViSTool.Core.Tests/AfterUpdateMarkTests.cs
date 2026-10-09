using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Отметка для новой версии агента после самообновления под systemd: одноразовая, «работал ли сервер».</summary>
public sealed class AfterUpdateMarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-au-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mark_IsReadOnce(bool running)
    {
        Assert.Null(AgentProtocol.TakeAfterUpdate("server", _dir)); // обычный запуск — отметки нет
        AgentProtocol.MarkAfterUpdate("server", _dir, running);
        Assert.Equal(running, AgentProtocol.TakeAfterUpdate("server", _dir));
        Assert.Null(AgentProtocol.TakeAfterUpdate("server", _dir)); // забрали — следующий запуск обычный
        Assert.Null(AgentProtocol.TakeAfterUpdate("other", _dir));
    }
}
