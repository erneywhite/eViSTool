using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public sealed class PlayerTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 22, 8, 0);

    // строки — как их печатает консоль сервера (в файле лога уровень без «Server »: трекеру всё равно)
    private static void Feed(PlayerTracker tracker, params string[] lines)
    {
        for (var i = 0; i < lines.Length; i++) tracker.Process(lines[i], T0.AddSeconds(i));
    }

    [Fact]
    public void JoinAndLeave_AreTrackedByClientNumber()
    {
        var tracker = new PlayerTracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        Feed(tracker,
            "5.9.2026 22:08:03 [Server Notification] A Client attempts connecting via TCP on 192.168.31.220:63829, assigning client id 1",
            "5.9.2026 22:08:04 [Server Notification] Client 1 uid 6dff07bf-acd0-11ee-8b6b-cef56310268d attempting identification. Name: Erney");
        Assert.Empty(tracker.Players); // ещё не вошёл

        Feed(tracker, "5.9.2026 22:09:05 [Server Event] Erney 192.168.31.220:63829 joins.");
        var player = Assert.Single(tracker.Players);
        Assert.Equal("Erney", player.Name);
        Assert.Equal("192.168.31.220:63829", player.Address);
        Assert.Equal(1, changes);

        Feed(tracker,
            "5.9.2026 22:10:47 [Server Notification] Client 1 disconnected: ",
            "5.9.2026 22:10:48 [Server Event] Игрок Erney вышел.",
            "5.9.2026 22:10:48 [Server Event] Client 1 disconnected.");
        Assert.Empty(tracker.Players);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ClientWhoNeverJoined_DoesNotKickAnyoneOut()
    {
        // как в настоящем логе: Erney на сервере, а у второго игрока вход сорвался
        var tracker = new PlayerTracker();
        Feed(tracker,
            "[Notification] Client 2 uid aaa attempting identification. Name: Erney",
            "[Notification] Client 3 uid bbb attempting identification. Name: toristarm",
            "[Event] Client 3 disconnected.",
            "[Notification] Client 3 disconnected: ",
            "[Event] Erney 192.168.31.220:63989 joins.");

        Assert.Equal(["Erney"], tracker.Players.Select(p => p.Name));
    }

    [Fact]
    public void Reconnect_UnderNewClientNumber_KeepsPlayerOnline()
    {
        var tracker = new PlayerTracker();
        Feed(tracker,
            "[Notification] Client 1 uid aaa attempting identification. Name: Erney",
            "[Event] Erney 10.0.0.2:1000 joins.",
            // связь оборвалась: новое подключение пришло раньше, чем сервер «отпустил» старое
            "[Notification] Client 5 uid aaa attempting identification. Name: Erney",
            "[Notification] Client 1 disconnected: Lost connection",
            "[Event] Erney 10.0.0.2:2000 joins.");

        var player = Assert.Single(tracker.Players);
        Assert.Equal("10.0.0.2:2000", player.Address);
    }

    [Fact]
    public void SeveralPlayers_AreOrderedByJoinTime_AndResetClearsAll()
    {
        var tracker = new PlayerTracker();
        Feed(tracker,
            "[Notification] Client 1 uid a attempting identification. Name: Erney",
            "[Notification] Client 2 uid b attempting identification. Name: toristarm",
            "[Event] toristarm 10.0.0.3:1 joins.",
            "[Event] Erney 10.0.0.2:1 joins.",
            "[Event] Chat: someone joins. the party", // не строка входа
            "[Notification] Client 9 disconnected: "); // незнакомый номер
        Assert.Equal(["toristarm", "Erney"], tracker.Players.Select(p => p.Name));

        Feed(tracker, "[Event] Client 2 disconnected.");
        Assert.Equal(["Erney"], tracker.Players.Select(p => p.Name));

        tracker.Reset();
        Assert.Empty(tracker.Players);
    }
}
