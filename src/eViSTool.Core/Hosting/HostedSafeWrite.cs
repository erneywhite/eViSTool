namespace eViSTool.Core.Hosting;

/// <summary>
/// Запись файла на сервер у хостера так, чтобы сбой посередине не оставил сервер без файла. Содержимое заливается
/// рядом как «имя.evistool.tmp» (ни сервер, ни моды такой файл не читают) и встаёт на место переименованием. Многие
/// серверы (G-Portal тоже) поверх не переименовывают — тогда прежний файл уходит в «имя.evistool.bak», временный встаёт
/// на место, а при сбое bak возвращается. Сорвался и возврат — на этой машине остаётся отметка вызывающего
/// (<c>mark</c>; снимает её <c>clear</c>), по ней bak вернёт следующая операция.
/// Так пишутся serverconfig.json (<see cref="HostedMods"/>) и настройки модов (<see cref="HostedModConfigSource"/>).
/// </summary>
internal static class HostedSafeWrite
{
    public const string TmpSuffix = ".evistool.tmp";
    public const string BakSuffix = ".evistool.bak";

    /// <param name="root">Корень сервера из профиля: выше него листинги могут быть закрыты.</param>
    /// <param name="mark">Отметка «файл сейчас лежит на сервере как bak» — ставится перед тем, как прежний файл уходит в bak.</param>
    /// <param name="clear">Снять отметку: на месте файла новый или вернулся прежний.</param>
    /// <param name="keepBak">Оставить прежнюю версию на сервере как bak (у serverconfig.json — ещё одна страховка); false —
    /// убрать её после записи: копии и так лежат на этой машине.</param>
    public static async Task WriteAsync(IRemoteFiles files, string root, string path, byte[] content, Action mark, Action clear,
        bool keepBak, CancellationToken ct)
    {
        var tmp = path + TmpSuffix;
        var bak = path + BakSuffix;
        using (var buffer = new MemoryStream(content, writable: false))
            await files.UploadAsync(buffer, tmp, null, ct).ConfigureAwait(false);
        try
        {
            try
            {
                await files.RenameAsync(tmp, path, ct).ConfigureAwait(false);
                return;
            }
            catch (RemoteFilesException ex) when (ex.Kind is not (RemoteFailure.Connect or RemoteFailure.Timeout))
            {
                // поверх не переименовывает. По договору это Exists, но 550 сервера («path already exists») адаптер
                // может перевести и как Denied — тогда смотрим сами: временный файл и прежний на месте — идём через bak
                if (ex.Kind != RemoteFailure.Exists)
                {
                    var ls = new HostedListings(files, root);
                    if (await ls.FileAsync(tmp, ct).ConfigureAwait(false) is null
                        || await ls.FileAsync(path, ct).ConfigureAwait(false) is null)
                        throw;
                }
            }
            if (await new HostedListings(files, root).FileAsync(bak, ct).ConfigureAwait(false) is not null)
                await files.DeleteFileAsync(bak, ct).ConfigureAwait(false);
            mark();
            await files.RenameAsync(path, bak, ct).ConfigureAwait(false);
            try
            {
                await files.RenameAsync(tmp, path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                if (await PutBackAsync(files, bak, path).ConfigureAwait(false)) clear();
                throw;
            }
            clear();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            await QuietlyAsync(() => files.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
        if (!keepBak) await QuietlyAsync(() => files.DeleteFileAsync(bak, ct)).ConfigureAwait(false);
    }

    /// <summary>Вернуть bak на место файла; false — не вышло (отметка остаётся для следующей операции).</summary>
    private static async Task<bool> PutBackAsync(IRemoteFiles files, string bak, string path)
    {
        try
        {
            await files.RenameAsync(bak, path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task QuietlyAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // уборка после сбоя или лишнего bak: основное дело уже решено
        }
    }
}
