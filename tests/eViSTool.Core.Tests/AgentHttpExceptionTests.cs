using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Tests;

/// <summary>Ошибки агента различаются по коду ответа, а не по тексту сообщения.</summary>
public sealed class AgentHttpExceptionTests
{
    [Fact]
    public void OldAgent_IsOnly404FromAgent()
    {
        Assert.True(AgentHttpException.IsOldAgent(new AgentHttpException(404, "404 Not Found")));
        Assert.False(AgentHttpException.IsOldAgent(new AgentHttpException(409, "сервер запущен")));
        // текст «404» без кода — не признак старого агента (например, пояснение агента или чужая ошибка)
        Assert.False(AgentHttpException.IsOldAgent(new InvalidOperationException("404 Not Found")));
    }

    [Fact]
    public void RemoteErrors_AreDescribedByStatusCode_EvenWithAgentText()
    {
        // агент прислал своё пояснение — код всё равно решает, что сказать человеку
        Assert.Equal(Localization.Loc.T("remote.errKey"), RemoteSecret.Describe(new AgentHttpException(401, "нет доступа")));
        Assert.Equal(Localization.Loc.T("remote.errTooMany"), RemoteSecret.Describe(new AgentHttpException(429, "подожди")));
        Assert.Equal(Localization.Loc.T("remote.errOldAgent"), RemoteSecret.Describe(new AgentHttpException(404, "нет такого")));
        Assert.Equal("сервер запущен", RemoteSecret.Describe(new AgentHttpException(409, "сервер запущен")));
    }
}
