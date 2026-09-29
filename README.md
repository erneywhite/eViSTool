# eViSTool

Инструмент для Vintage Story: менеджер модов (клиент и сервер), каталог модов из модбазы
и управление выделенным сервером (запуск, консоль, игроки, бэкапы, рестарты, удалённое управление).

## Благодарности

- [Rustique](https://github.com/Tekunogosu/Rustique) (Tekunogosu, MIT) — логика работы с API модбазы,
  выбор версий и разбор капризных modinfo.json перенесены отсюда.
- [ViSST Server Tool](https://mods.vintagestory.at/show/mod/17652) (THumbert) — идеи управления сервером:
  автобэкапы через `/genbackup`, рестарты с объявлениями в чате.

## Структура

- `src/eViSTool.Core` — общая логика: модбаза, моды, конфиги, бэкапы.
- `src/eViSTool.Agent` — фоновый агент на сервере: владеет процессом VS-сервера.
- `src/eViSTool.App` — GUI (WPF, .NET 10).
- `tests/eViSTool.Core.Tests` — тесты.

## Сборка

```
dotnet build
```

© 2026 Erney White
