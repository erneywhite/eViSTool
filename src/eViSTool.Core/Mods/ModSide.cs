using eViSTool.Core.Profiles;

namespace eViSTool.Core.Mods;

/// <summary>Где мод работает: у игрока, на сервере или и там и там.</summary>
public enum ModSide { Both, Client, Server }

public static class ModSides
{
    /// <summary>
    /// modinfo.json пишет «Universal» / «Client» / «Server», модбаза — «both» / «client» / «server».
    /// Не указано — как у игры: мод для обеих сторон.
    /// </summary>
    public static ModSide Parse(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "client" => ModSide.Client,
        "server" => ModSide.Server,
        _ => ModSide.Both,
    };

    /// <summary>
    /// Профилю такой мод ни к чему: выделенный сервер не загружает клиентские моды. Ничего не ломает, просто лежит без дела.
    /// Обратное неверно: серверный мод у игрока нужен одиночной игре — её мир крутит встроенный сервер из той же папки модов.
    /// </summary>
    public static bool IsUnneeded(ModSide side, ProfileKind kind) => side == ModSide.Client && kind == ProfileKind.Server;
}
