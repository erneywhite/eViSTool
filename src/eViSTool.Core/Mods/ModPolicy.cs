using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

/// <summary>
/// Пожелания пользователя к версиям модов.
/// Закреплённый мод (pin) не обновляется. Пропущенные версии (block) не предлагаются, следующие — да.
/// Ключи — modid в нижнем регистре.
/// </summary>
public sealed record ModPolicy(
    IReadOnlyDictionary<string, string> Pinned,
    IReadOnlyDictionary<string, List<string>> Blocked)
{
    public static ModPolicy Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, List<string>>());

    public bool IsPinned(string modId) => Pinned.ContainsKey(modId);

    public bool IsBlocked(string modId, ModVersion version) =>
        Blocked.TryGetValue(modId, out var list)
        && list.Any(b => ModVersion.ParseOrNull(b) is { } v && v.CompareTo(version) == 0);
}
