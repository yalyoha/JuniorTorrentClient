# Junior Torrent Client (JTC)

Небольшой торрент-клиент для Windows 10/11 на WinUI 3. Минимум настроек, тихая работа
в трее, честная скорость загрузки и человеческие цвета. Русский интерфейс.

**Текущая версия:** [v0.7.10](https://github.com/yalyoha/JuniorTorrentClient/releases/latest)
— после завершения торрента лишние «пограничные» файлы (соседи выбранных файлов,
куда попали общие piece-boundary байты) автоматически удаляются с диска — в папке
остаются только реально выбранные файлы.

## Возможности

- Загрузка `.torrent`-файлов и `magnet:`-ссылок (движок [MonoTorrent](https://github.com/alanmcgovern/monotorrent) 3.0.2).
- Ассоциация с `.torrent` и обработчик протокола `magnet:` — двойной клик или клик по
  magnet-ссылке в браузере добавляют раздачу в уже запущенный экземпляр.
- Один экземпляр приложения. Повторный запуск с файлом/magnet передаёт его в
  работающий процесс через inbox-файл и не открывает второе окно.
- Свёртывание в трей (`H.NotifyIcon.WinUI`) — движок продолжает раздавать/качать
  в фоне; выход только через контекстное меню трея.
- Персистентное состояние: список раздач и настройки лежат в
  `%LocalAppData%\JTC\` (`torrents.json`, `settings.json`), лог — `debug.log`.
- Автообновление через GitHub Releases: тихая установка нового билда с
  автозапуском клиента после копирования файлов (можно отключить в настройках).
- Темы: «Цветной» (градиент фона + белая или тёмная плашка строк), «Тёмная»,
  «Светлая». 10 встроенных пресетов + пользовательские, полная настройка цветов
  строк и статусов раздачи.
- Статус раздачи (Idle / Downloading / Seeding / Hashing / Error) — цветным
  индикатором в строке и глифом в колонке «Состояние».
- Отдельные счётчики сидов и пиров, скорости в реальном времени, без
  искусственного тротлинга UI.

## Требования

- Windows 10 версии 20H1 (10.0.19041) или новее / Windows 11.
- Архитектура x64.
- Права администратора не нужны — установщик кладёт приложение в
  `%LocalAppData%\Programs\JuniorTorrentClient`.

## Установка

Скачайте `JTC-vX.Y.Z-setup.exe` со страницы
[Releases](https://github.com/yalyoha/JuniorTorrentClient/releases/latest) и
запустите. Установщик:

- корректно завершает уже запущенный JTC (сначала через маркер `@shutdown`,
  затем `taskkill /F` — файлы не остаются заблокированными);
- сносит устаревшую установку `TClient` (старое имя приложения) и её ярлыки;
- перезапускает JTC после установки.

## Сборка из исходников

Требуется .NET SDK 10 и Inno Setup 6.

```powershell
# publish self-contained x64 — the -p:Platform=x64 is mandatory because the
# csproj declares <Platforms>x64;ARM64</Platforms>; without it dotnet writes
# to bin\Release\... while the .iss reads from bin\x64\Release\..., silently
# packaging stale binaries.
dotnet publish src\JTC\JTC.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true

# installer → dist\JTC-vX.Y.Z-setup.exe
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer\JTC.iss
```

## Структура репозитория

- `src/JTC/` — приложение WinUI 3 (WindowsAppSDK 2.2, CommunityToolkit.Mvvm).
- `src/JTC/Services/` — движок торрентов, автообновление, single-instance,
  ассоциация с `.torrent`, персистентность, настройки.
- `installer/JTC.iss` — скрипт Inno Setup для сборки установщика.
- `icon/` — исходники иконки.

## Лицензия

Проект персональный, лицензия не задана.
