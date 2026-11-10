**eViSTool 0.9.0** — server statistics, game chat in Discord, any backup folder.

- **Statistics:** a new server tab, off by default. Turn it on and the agent records once a minute who is online and how much memory and CPU the server uses, plus joins, starts and crashes. You get totals for a day, a week or 30 days (time played by everyone, peak online, how long the server was running, crashes), charts with exact values on hover, and a players table: time played, sessions, last seen. Kept for 30 days on the server's computer, about 1 MB a month.
- **Game chat in Discord:** players' messages from the general chat go to a Discord channel, each under the player's name; several messages in a row arrive as one. Joins and leaves can go there too. One way only, from the game to Discord, and `@everyone` typed in the game pings nobody.
- **New notifications:** mod updates are out (checked once a day, with the list; the same list is not sent twice), low disk space (less than 5 GB on the server data disk), and server can't keep up (many "Server overloaded" warnings, with a hint whether the computer is short of memory).
- **Backup folder:** world backups can go to any folder, a network share like `\\nas\share` included, picked with Browse or typed in. The server still makes the copy in its own Backups and the agent moves it over. If the folder is unreachable, the copy stays on the server, you get a notification, and it moves next time. The list, rotation and restore see both folders.
- **Update there** (from 0.8.2): when the server's computer runs an older eViSTool, a button on the Server tab updates it to your version over the remote connection; a running server stops for about a minute and starts again.

Update from **About**, a running server keeps running.

---

Статистика сервера, чат игры в Discord, любая папка для копий.

- **Статистика:** новая вкладка сервера, по умолчанию выключена. Если включить, агент раз в минуту записывает, кто в игре и сколько памяти и процессора занимает сервер, а ещё входы, запуски и вылеты. Вкладка показывает итоги за сутки, неделю или 30 дней (сколько наиграли все вместе, пик онлайна, сколько работал сервер, вылеты), графики с точными значениями под мышью и таблицу игроков: сколько наиграл, сколько раз заходил, когда был последний раз. Хранится 30 дней на компьютере с сервером, около 1 МБ в месяц.
- **Чат игры в Discord:** сообщения игроков из общего чата уходят в канал Discord, каждое под ником игрока, а несколько подряд от одного приходят одним. По желанию туда же пишется, кто зашёл и вышел. Направление одно, из игры в Discord, и `@everyone` из игры никого не позовёт.
- **Новые оповещения:** вышли обновления модов (проверка раз в сутки, со списком, один и тот же список повторно не приходит), мало места на диске (меньше 5 ГБ на диске с данными сервера) и сервер не успевает (много предупреждений «Server overloaded», с подсказкой, хватает ли компьютеру памяти).
- **Папка для копий:** копии мира можно складывать в любую папку, в том числе сетевую вроде `\\nas\share`, через «Обзор» или вписав путь. Сервер по-прежнему делает копию у себя в Backups, а агент переносит её. Если папка недоступна, копия остаётся на сервере, приходит оповещение, и в следующий раз она переедет. Список, удаление старых копий и восстановление видят обе папки.
- **«Обновить там»** (с 0.8.2): если на компьютере с сервером стоит eViSTool старее твоего, кнопка на вкладке «Сервер» обновит его до твоей версии по удалённому подключению. Работающий сервер остановится примерно на минуту и запустится снова.

Обнови из **«О программе»**, запущенный сервер не остановится.
