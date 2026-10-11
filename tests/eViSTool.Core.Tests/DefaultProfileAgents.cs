namespace eViSTool.Core.Tests;

/// <summary>
/// Тесты, запускающие настоящего агента с профилем по умолчанию («server»). Замок профиля на Windows — именованный
/// Mutex на всю машину (папка агентов его не разводит), поэтому такие тесты идут по одному, а не параллельно.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DefaultProfileAgents
{
    public const string Name = "Агент профиля по умолчанию";
}
