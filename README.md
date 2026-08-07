# Junior Torrent Client (JTC)

> Небольшой торрент-клиент для Windows 10/11 на WinUI 3 —
> витрина возможностей [MonoTorrent](https://github.com/alanmcgovern/monotorrent)
> и одновременно эксперимент: «что получится, если веб-разработчик, впервые
> взявший C#, сядет писать десктопное приложение с [Claude](https://claude.ai)
> в паре».

**Текущая версия:** [v0.8.2](https://github.com/yalyoha/JuniorTorrentClient/releases/latest)

---

## О проекте

JTC — это личный экспериментальный проект. Автор — веб-разработчик, для
которого C#, .NET, WinUI 3 и вся Windows-стековая специфика были в новинку.
Задача была не «выпустить ещё один торрент-клиент» (их и так десятки), а
проверить, насколько далеко можно уйти, если писать код в тесной паре с
Claude — обсуждать архитектуру, читать чужой открытый код, дебажить в
живую, оформлять релизы.

К релизу v0.7.10 в клиенте:

- работает вся базовая функциональность (`.torrent`, magnet, выбор файлов,
  очередь, тихая раздача, автообновление);
- полноценно поддерживаются частичные скачивания (выбор конкретных серий /
  файлов) с автоматической постпроверкой и очисткой piece-boundary остатков;
- живёт диагностика: `debug.log` с ротацией, DIAG-строки по каждой активной
  раздаче каждые 10 секунд, watchdog'и против phantom-Seeding и stuck-Downloading;
- проработана «неудобная» механика: single-instance с передачей открытых
  файлов, безопасный установщик с graceful shutdown, ассоциация с
  `.torrent` и `magnet:`, свёртывание в трей.

## Хвала MonoTorrent

Всё сетевое сердце JTC — это [MonoTorrent](https://github.com/alanmcgovern/monotorrent) от
Alan McGovern: кроссплатформенная open-source библиотека на .NET, реализующая
протокол BitTorrent целиком. Не «упрощённую версию для демо», а полноценно:

- BEP 3 (базовый протокол), BEP 5 (DHT), BEP 6 (fast peers),
  BEP 9 (magnet-метаданные), BEP 10 (extension protocol), BEP 11 (PEX),
  BEP 14 (LSD / LAN peer discovery), BEP 15 (UDP-трекеры), BEP 19 (WebSeed),
  BEP 20 (peer-ID conventions), BEP 27 (private torrents), BEP 29 (µTP),
  BEP 32 (IPv6 DHT), BEP 47 (padding files) и дальше по списку.
- Полноценный piece picker, разные стратегии выбора (rarest-first,
  end-game, приоритеты по файлам).
- Асинхронный disk-IO с настраиваемым кэшем на чтение и запись.
- Шифрование потока (RC4 full/header, plain-text fallback).
- UPnP / NAT-PMP автоматический port-mapping.
- Fast-resume, hash-check с прогрессом, DoNotDownload / приоритеты файлов.

Библиотека компактная, читаемая, современная (nullable enable, async/await,
`ValueTask`, `Memory<byte>`). Именно то, что позволяет JTC быть на 3 тысячах
строк C# и делать почти всё, что делает большой клиент.

Если вам когда-нибудь нужно было встроить BitTorrent в свой .NET-код —
берите MonoTorrent, поставьте им звезду, поддержите деньгами. Это редкий
пример, когда одна маленькая библиотека закрывает целый протокол настолько
качественно, что писать альтернативу — просто перевод сил впустую.

## Возможности

### Скачивание

- `.torrent`-файлы и `magnet:`-ссылки.
- **Выбор файлов при добавлении** — компактный диалог с чекбоксами, сортировкой
  по номеру серии, счётчиком выбранного, поиском по имени. Клик по всей строке
  переключает галку. Не выбранные файлы помечаются `Priority.DoNotDownload`,
  MonoTorrent не запрашивает их куски.
- **Piece-boundary cleanup** — после завершения торрента лишние «фантомные»
  соседи выбранных файлов (в них падают общие пограничные куски) автоматически
  удаляются с диска. `UsePartialFiles=true` включён, так что недокачанные
  файлы легко узнать по суффиксу `.!mt`.
- **Авто-проверка после завершения** — переход Downloading → Seeding запускает
  фоновый HashCheck. Битые/недописанные куски возвращаются в очередь без
  ручного «Обновить». Отключается в настройках.
- **Очередь одновременных загрузок** — по умолчанию до 3 активных, регулируется
  в настройках. Приостановленные и завершённые в очереди не занимают слот.

### Раздача (seeding)

- Автоматический seeding после завершения без переспрашивания.
- Стабильный TCP+UDP порт 51413 — UPnP-маппинг переживает перезапуск,
  peer'ы возвращаются на тот же адрес.
- IPv4 и IPv6 listener одновременно (если у OS есть v6-роут).
- 500 peer-connections на весь движок, до 300 на торрент — на живых swarm'ах
  JTC держит ~93% от throughput qBittorrent на тех же данных.

### Устойчивость

- **Fast-resume каждые 45 секунд** — на жёсткое падение теряется максимум
  минуты прогресса вместо полного rehash.
- **Watchdog phantom-Seeding** — если MonoTorrent решает, что торрент
  «раздаётся», а PartialProgress < 99% три раза подряд, JTC форсирует
  hashcheck: сбрасывает stale bitfield.
- **Watchdog stuck-Downloading** — Downloading state с ~0 B/s и известными
  peer'ами в пуле дольше ~3 минут → tracker + DHT re-announce для обновления
  списка адресов.
- **Retry policy при Error** — до 5 попыток с exponential backoff (200мс →
  30с). На 4-й попытке принудительный HashCheck.
- **Add-retry при race** — MonoTorrent asynchronously освобождает info-hash
  после `RemoveAsync`; add с 8 backoff-точками (200мс → 3.2с) переживает
  быстрые Add-Remove-Add циклы.

### Интерфейс

- **Темы** — «Цветной» (градиент фона + белая или тёмная плашка строк),
  «Тёмная», «Светлая». 10 встроенных пресетов + сохранение своих.
- **Полная настройка цвета** — верх/низ градиента, фон+текст строк, 5
  цветов статусов раздачи (Idle / Downloading / Seeding / Hashing / Error).
- **Настройка формы** — скругление кнопок, скругление плашек, стиль
  индикатора статуса (кружок или полоска слева).
- **Live-preview** — все изменения в настройках применяются мгновенно;
  сохранение / Отмена только фиксируют / откатывают результат.

### Интеграция с Windows

- **Ассоциация с `.torrent`** и обработчик протокола `magnet:` через
  `HKCU\Software\Classes` (без админа).
- **Single-instance** — второй запуск не открывает второе окно, а передаёт
  файл в работающий процесс через `%LocalAppData%\JTC\inbox\` с
  `FileSystemWatcher`.
- **Свёртывание в трей** — `H.NotifyIcon.WinUI`; выход только через
  контекстное меню.
- **Установщик Inno Setup** с graceful shutdown работающего клиента через
  маркер `@shutdown` (и `taskkill /F` как fallback). Не оставляет
  залоченных файлов.
- **Автообновление** — фоновая проверка GitHub Releases, тихая установка
  с автозапуском после копирования. Можно отключить.

### Диагностика

- `%LocalAppData%\JTC\debug.log` с ротацией по ~1 MB.
- DIAG-строки раз в 10 секунд по каждой активной раздаче: состояние,
  скорости, соединения, состав пула peer'ов, статус трекеров, состояние DHT.
- Все переходы состояний торрентов логируются с прогрессом на момент
  перехода — post-mortem по «упавшей» раздаче не требует репродукции.
- Fast-resume вспышки и hashcheck timing тоже пишутся — видно, диск ли
  тормозит или что-то с MonoTorrent'ом.

## Требования

- Windows 10 версии 20H1 (10.0.19041) или новее / Windows 11.
- Архитектура **x64**.
- Права администратора не нужны — установщик кладёт приложение в
  `%LocalAppData%\Programs\JuniorTorrentClient` (per-user install).

## Установка

Скачайте `JTC-vX.Y.Z-setup.exe` со страницы
[Releases](https://github.com/yalyoha/JuniorTorrentClient/releases/latest) и
запустите. Установщик:

- корректно завершает уже запущенный JTC (сначала маркер `@shutdown`, затем
  `taskkill /F` — файлы не остаются заблокированными);
- перезапускает JTC после установки.

При включённом автообновлении (по умолчанию) все последующие релизы
устанавливаются тихо в фоне.

## Сборка из исходников

Требуется .NET SDK 10 и Inno Setup 6.

```powershell
# publish self-contained x64 — -p:Platform=x64 обязателен, потому что csproj
# объявляет <Platforms>x64;ARM64</Platforms>; без него dotnet пишет в
# bin\Release\..., а .iss читает из bin\x64\Release\... и пакует старый билд.
dotnet publish src\JTC\JTC.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true

# installer → dist\JTC-vX.Y.Z-setup.exe
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer\JTC.iss
```

Тесты:

```powershell
dotnet test tests\JTC.Tests\JTC.Tests.csproj -c Debug -p:Platform=x64 -r win-x64
```

## Разработка с Claude

Весь код в этом репозитории написан веб-разработчиком в паре с
Claude ([Claude Code](https://claude.ai/code) CLI, Opus 4.7). Про C#, .NET,
WinUI 3, MSIX, WindowsAppSDK, MonoTorrent, Inno Setup, реестр Windows и всю
остальную нативно-виндовую специфику до начала проекта автор не знал ничего.

Что даёт этот подход на практике:

- **Скорость декомпозиции.** Требование «убери горизонтальную прокрутку в
  диалоге» превращается в диагностику через 3 сообщения: посмотреть
  `FileSelectionDialog.cs`, понять, что `MinWidth=640` не влезает в узкое
  окно, что `ContentDialogMaxWidth` не переопределён, что у внешнего
  `ContentScrollViewer` включён `HorizontalScrollMode.Auto` — и патч готов.
- **Комментарии, объясняющие не «что», а «почему».** В коде много блоков
  вида «раньше делали X → у пользователя ломалось Y → теперь Z». Это
  фактически история изменений в исходниках, не в git blame.
- **Возможность строить сложное из простого.** Piece-boundary cleanup,
  phantom-Seeding watchdog, retry-политика с exponential backoff, single-
  instance через `inbox\` — каждая такая механика рождается из конкретного
  бага и живёт как маленький, изолированный кусочек.
- **Возможность бесконечно рефакторить.** Пример: миграция выбора файлов
  с индексов на пути (v0.7.7). Клиент сам мигрирует записи в `torrents.json`
  на первом чтении. С Claude такое делается за час, без него — day-long
  task с багами на несовместимости форматов.

Не даёт (или даёт с трудом):

- **Визуальный вкус на «глаз».** Скрины UI, «влезает ли», «читается ли» —
  всё это нужно проверять руками. Claude может править XAML, но не увидит
  результат.
- **Гарантий по нативной-Windows специфике.** Иногда WinUI 3 ведёт себя
  сюрпризом (например, `args.OriginalSource` в ContentDialog указывает на
  внутренний темплейт, не на CheckBox — из-за этого случился регресс в
  v0.7.8, чинили в v0.7.9). Такие вещи вылавливаются в проде.

## Структура репозитория

```
src/JTC/
├── App.xaml{,.cs}              — WinUI 3 application root
├── MainWindow.xaml{,.cs}       — главное окно + диалог настроек
├── Assets/                     — иконка + шрифты
├── ViewModels/
│   ├── MainViewModel.cs        — коллекция раздач, timer, тотал-счётчики
│   └── TorrentViewModel.cs     — одна строка раздачи (state, brushes, %)
├── Services/
│   ├── TorrentService.cs       — обёртка над MonoTorrent, очередь,
│   │                             watchdog'и, auto-verify, cleanup
│   ├── UpdateService.cs        — проверка GitHub Releases + тихая установка
│   ├── SingleInstance.cs       — второй запуск → передача через inbox
│   ├── FileAssociation.cs      — регистрация .torrent + magnet: в реестре
│   ├── StateStore.cs           — persist списка раздач в torrents.json
│   ├── SettingsStore.cs        — persist настроек в settings.json
│   ├── AppSettings.cs          — модель настроек + встроенные цветовые пресеты
│   ├── PersistedTorrent.cs     — запись в torrents.json
│   ├── AppPaths.cs             — %LocalAppData%\JTC пути
│   ├── DebugLog.cs             — файловый лог с ротацией
│   ├── CloudLogSink.cs         — опциональная отправка логов вовне
│   └── TorrentRestartPolicy.cs — backoff + fatal-exception classification
├── Helpers/
│   ├── FileSelectionDialog.cs  — компактный диалог выбора файлов
│   ├── ThemeHelper.cs          — темы, гарадиенты, статус-кисти
│   ├── Formatting.cs           — байты / скорости в human-readable
│   └── RowBrushes.cs           — кисти для строк раздач
└── JTC.csproj                  — WindowsAppSDK 2.2, MonoTorrent 3.0.2,
                                  H.NotifyIcon.WinUI 2.3, CommunityToolkit.Mvvm 8.4

tests/JTC.Tests/                — xUnit-тесты (сейчас 39 штук — форматирование
                                  байтов, retry-политика)
installer/JTC.iss               — Inno Setup 6 скрипт: graceful shutdown,
                                  silent-режим для автообновления
icon/                           — исходники иконки (CorelDraw + PNG)
```

## Благодарности

- [**MonoTorrent**](https://github.com/alanmcgovern/monotorrent) — весь
  BitTorrent-стек. Без неё этого проекта бы не было.
- [**WindowsAppSDK / WinUI 3**](https://github.com/microsoft/WindowsAppSDK) —
  современный UI-фреймворк для Windows десктопа.
- [**CommunityToolkit.Mvvm**](https://github.com/CommunityToolkit/dotnet) —
  `[ObservableProperty]` и всё, что делает MVVM в C# читаемым.
- [**H.NotifyIcon.WinUI**](https://github.com/HavenDV/H.NotifyIcon) — трей
  для WinUI 3, которого сам WinUI 3 не даёт.
- [**Inno Setup**](https://jrsoftware.org/isinfo.php) — установщик, который
  просто работает уже двадцать лет.
- [**Claude**](https://claude.ai) — соавтор буквально каждого коммита.

## Лицензия

Проект персональный, лицензия не задана. Если хотите использовать код —
свяжитесь через [issues](https://github.com/yalyoha/JuniorTorrentClient/issues).
