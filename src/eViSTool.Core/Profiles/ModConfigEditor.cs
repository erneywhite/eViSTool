using System.Text;
using eViSTool.Core.Mods;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Profiles;

/// <summary>
/// Включает/выключает моды так же, как встроенный менеджер модов игры:
/// клиент — stringListSettings.disabledMods в clientsettings.json,
/// сервер — WorldConfig.DisabledMods в serverconfig.json (то есть для текущего мира).
/// </summary>
public static class ModConfigEditor
{
    /// <summary>Выключает (enabled=false) или включает мод. Возвращает false, если менять нечего.</summary>
    public static bool SetEnabled(ResolvedProfile profile, ModInfo mod, bool enabled)
    {
        var path = profile.ConfigPath ?? throw new InvalidOperationException(
            profile.Profile.Kind == ProfileKind.Server
                ? Loc.T("cfg.noServerConfigRun")
                : Loc.T("cfg.noClientConfigRun"));

        var root = Load(path);
        var list = GetOrCreateList(root, profile.Profile.Kind);

        // игра принимает и "modid", и "modid@версия" — при включении убираем обе формы
        var existing = list
            .Where(t => t.Type == JTokenType.String && Matches(t.ToString(), mod.OriginalModId))
            .ToList();

        bool changed;
        if (enabled)
        {
            existing.ForEach(t => t.Remove());
            changed = existing.Count > 0;
        }
        else
        {
            // выключаем по голому modid: так мод останется выключенным и после обновления
            var plain = existing.Any(t => t.ToString() == mod.OriginalModId);
            existing.Where(t => t.ToString() != mod.OriginalModId).ToList().ForEach(t => t.Remove());
            if (!plain) list.Add(mod.OriginalModId);
            changed = !plain || existing.Count > 1;
        }

        if (changed) Save(path, root);
        return changed;
    }

    /// <summary>Убирает мод из списка выключенных совсем (например, после удаления мода).</summary>
    public static void Forget(ResolvedProfile profile, ModInfo mod)
    {
        if (profile.ConfigPath is null) return;
        SetEnabled(profile, mod, enabled: true);
    }

    private static bool Matches(string entry, string modId)
    {
        var at = entry.IndexOf('@');
        var id = at >= 0 ? entry[..at] : entry;
        return string.Equals(id, modId, StringComparison.OrdinalIgnoreCase);
    }

    private static JArray GetOrCreateList(JObject root, ProfileKind kind)
    {
        if (kind == ProfileKind.Server)
        {
            if (root["WorldConfig"] is not JObject world)
                root["WorldConfig"] = world = new JObject();
            if (world["DisabledMods"] is not JArray arr)
                world["DisabledMods"] = arr = new JArray();
            return arr;
        }

        if (root["stringListSettings"] is not JObject lists)
            root["stringListSettings"] = lists = new JObject();
        if (lists["disabledMods"] is not JArray list)
            lists["disabledMods"] = list = new JArray();
        return list;
    }

    /// <summary>Читает как есть: строки не превращаются в даты, дробные числа не теряют точность.</summary>
    internal static JObject Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Текст конфига → дерево, как при чтении файла (даты остаются строками, дроби — точными).</summary>
    internal static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
        };
        return JObject.Load(reader);
    }

    /// <summary>Резервная копия предыдущего состояния рядом (*.evistool.bak) и атомарная запись.</summary>
    internal static void Save(string path, JObject root)
    {
        if (File.Exists(path)) File.Copy(path, path + ".evistool.bak", overwrite: true);
        var tmp = path + ".evistool.tmp";
        File.WriteAllText(tmp, root.ToString(Formatting.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
