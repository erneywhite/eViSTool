using System.Runtime.InteropServices;

// Поддельный сервер Vintage Story для тестов ServerHost: пишет строки в формате логов VS
// и реагирует на команды так же, как настоящий.
//   /stop            — корректная остановка (строки завершения, код 0)
//   /crash           — «падение» (код 1)
//   /hang            — дальше игнорирует /stop (для проверки Ctrl+C и kill)
//   /spam N          — N строк в stderr (проверка, что stderr читается и сервер не виснет)
//   /genbackup       — копия мира в Backups, как у настоящего: «default-ГГГГ-ММ-ДД_ЧЧ-ММ-СС.vcdbs»; конец — по-русски
//   /fakejoin N имя, /fakeleave N — строки входа и выхода игрока
//   прочее           — «Handling Console Command …»
// Аргументы: --dataPath <путь> (обязателен, как у нас), --slowstart <мс>

var slowStart = 200;
var dataPath = ".";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--slowstart") slowStart = int.Parse(args[i + 1]);
    if (args[i] == "--dataPath") dataPath = args[i + 1];
}

var hang = false;
var stopping = new ManualResetEventSlim();

void Log(string level, string text) => Console.WriteLine($"{DateTime.Now:d.M.yyyy HH:mm:ss} [{level}] {text}");

// Ctrl+C / SIGTERM — мягкая остановка, как у настоящего сервера
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    ctx.Cancel = true;
    Log("Notification", $"Server termination event {ctx.Signal} received. Shutting down server");
    stopping.Set();
});

Log("Notification", "Server logger started.");
Log("Notification", "Game Version: v1.22.7 (Stable)");
Log("Event", "Launching server...");
Log("Event", "Неповрежденный мир...");   // кириллица, как у настоящего сервера — проверка кодировки
Thread.Sleep(slowStart);
Log("Event", "Dedicated server now running on Port 42420 and ip 127.0.0.1!");

var reader = new Thread(() =>
{
    while (Console.ReadLine() is { } line)
    {
        line = line.Trim();
        if (line == "/stop")
        {
            Log("Notification", "Handling Console Command /stop");
            if (!hang) { stopping.Set(); return; }
            Log("Warning", "(hang) ignoring /stop");
        }
        else if (line == "/crash") Environment.Exit(1);
        else if (line == "/hang") { hang = true; Log("Notification", "Now ignoring /stop"); }
        else if (line.StartsWith("/spam ")) { for (var n = 0; n < int.Parse(line[6..]); n++) Console.Error.WriteLine($"stderr line {n} " + new string('x', 200)); }
        else if (line == "/genbackup" || line.StartsWith("/genbackup "))
        {
            // имя задано — файл называется ровно так (настоящий сервер расширение не дописывает)
            var name = line.Length > 11 ? line[11..].Trim() : $"default-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.vcdbs";
            Log("Notification", $"Handling Console Command {line}");
            Log("Notification", "Ok, generating backup, this might take a while");
            var dir = Directory.CreateDirectory(Path.Combine(dataPath, "Backups")).FullName;
            File.WriteAllText(Path.Combine(dir, name), "world");
            // как у сервера с ServerLanguage=ru: строка о конце копии — на языке сервера, агент не должен на неё полагаться
            Log("Notification", "Резервное копирование завершено!");
        }
        // вход и выход игрока — теми же строками, что печатает настоящий сервер
        else if (line.StartsWith("/fakejoin "))
        {
            var parts = line.Split(' ');
            Log("Notification", $"A Client attempts connecting via TCP on 10.0.0.{parts[1]}:5000, assigning client id {parts[1]}");
            Log("Notification", $"Client {parts[1]} uid 00000000-0000-0000-0000-00000000000{parts[1]} attempting identification. Name: {parts[2]}");
            Log("Event", $"{parts[2]} 10.0.0.{parts[1]}:5000 joins.");
        }
        else if (line.StartsWith("/fakeleave "))
        {
            Log("Notification", $"Client {line.Split(' ')[1]} disconnected: ");
            Log("Event", $"Client {line.Split(' ')[1]} disconnected.");
        }
        else Log("Notification", $"Handling Console Command {line}");
    }
}) { IsBackground = true };
reader.Start();

stopping.Wait();
Log("Notification", "Server stop requested, begin shutdown sequence.");
Log("Notification", "Entering runphase Shutdown");
Thread.Sleep(150);
Log("Event", "Stopped the server!");
return 0;
