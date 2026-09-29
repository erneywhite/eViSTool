# eViSTool

Инструмент для Vintage Story: менеджер модов (клиент и сервер), каталог модов из модбазы
и управление выделенным сервером (запуск, консоль, игроки, бэкапы, рестарты, удалённое управление).

Идейные предшественники: [ViSST Server Tool](https://mods.vintagestory.at/show/mod/17652)
и [Rustique](https://github.com/Tekunogosu/Rustique).

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
