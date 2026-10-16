namespace eViSTool.Core.Server;

/// <summary>
/// Агент ответил ошибкой. Текст — для человека (detail из problem+json или «код причина»), код — для проверок:
/// не сравнивать текст сообщения, он зависит от языка и от того, прислал ли агент пояснение.
/// </summary>
public sealed class AgentHttpException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>
    /// Такого адреса у агента нет — он из версии без этой функции (сам агент 404 не отдаёт, это ответ маршрутизации).
    /// </summary>
    public static bool IsOldAgent(Exception ex) => ex is AgentHttpException { StatusCode: 404 };
}
