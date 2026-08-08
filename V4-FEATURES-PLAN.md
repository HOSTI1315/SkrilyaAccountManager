# План: перенос фич из RAM v4 в SkrilyaAccountManager

Документ-передача для отдельной сессии. Задача — портировать функциональность из
**Roblox Account Manager v4** (Rust + Tauri + React) в наш C#/WinForms проект.
Код оттуда не переносится напрямую: там другой стек. Переносятся **механизмы** —
какие Win32 API дёргать, какие эндпоинты звать, какая логика.

Разделы A–G взяты из v4. **Раздел H — единственный не из v4**: он пришёл из разбора
коммерческого RIFT Manager, готового кода для сверки под него нет, поэтому там есть
отдельная нулевая фаза с проверкой API вживую.

---

## 1. Контекст: где мы сейчас

Проект уже мигрирован и отрефакторен, это **не** ванильный RAM.

| | |
|---|---|
| Рантайм | .NET 10 (`net10.0-windows`), C# 14, x64 |
| Проект | SDK-style csproj, PackageReference |
| Имя | SkrilyaAccountManager (SAM), exe `SkrilyaAccountManager.exe` |
| Браузер | только PuppeteerSharp — **CefSharp удалён целиком** |
| Хранилище | `%LOCALAPPDATA%\SkrilyaAccountManager\AccountData.json`, DPAPI CurrentUser + per-install энтропия |
| Настройки | `RAMSettings.ini` в рабочей папке, секции `[General]`, `[AccountControl]`, `[Developer]`, `[WebServer]`, `[Watcher]` |
| Тема | `RAMTheme.ini`, секция берётся из имени сборки (`[SkrilyaAccountManager]`) |
| Mutex | `{5A4D1C7E-2B94-4F30-9E61-7C8D0A5B3F12}` — свой, SAM запускается рядом со стоковым RAM |

Уже сделано (не переделывать): фиксы безопасности веб-API, DPAPI, токен Nexus,
кэш `GetCommandLine()`, `HarvestRotatedCookie`, `LaunchLock`, гонки в Batch/RobloxProcess/Nexus,
`Classes/ResourceManager.cs` (приоритет / affinity / EmptyWorkingSet / статистика CPU-RAM),
`PerfLowGraphics` в `Classes/ClientSettingsPatcher.cs`.

### Ключевые файлы нашего проекта

```
RBX Alt Manager/
  AccountManager.cs              главная форма, веб-API (SendResponse), таймеры, настройки-дефолты
  Classes/Account.cs             JoinServer, авторизация, тикеты, окна клиента
  Classes/ResourceManager.cs     per-instance управление ресурсами  <- расширять здесь
  Classes/ClientSettingsPatcher.cs  ClientAppSettings.json / FastFlags
  Classes/RobloxProcess.cs       парсинг лога клиента, детект дисконнекта
  Classes/RobloxWatcher.cs       обнаружение процессов
  Classes/IniFile.cs             настройки
  Nexus/AccountControl.cs        websocket-сервер, AutoRelaunch  <- пересекается с botting
  Forms/SettingsForm.cs          UI настроек
```

---

## 2. Референс v4

Архив: `%USERPROFILE%\Downloads\Roblox-Account-Manager-4.zip`
(если его нет — репозиторий `niccsprojects/Roblox-Account-Manager`, ветка v4-beta).

```powershell
Expand-Archive "$env:USERPROFILE\Downloads\Roblox-Account-Manager-4.zip" -DestinationPath "$env:TEMP\ram4"
```

Нужные файлы внутри `Roblox-Account-Manager-4\src-tauri\src\`:

| Фича | Референс |
|---|---|
| Job Objects / приоритеты | `platform/windows/optimization.rs` (14 КБ) |
| Изоляция | `platform/windows/isolation.rs` (41 КБ) |
| Менеджер версий | `platform/windows/versions.rs` (26 КБ) + `data/versions.rs` |
| AFK | `commands/afk.rs` (11 КБ) |
| Генератор | `commands/generators.rs` (23 КБ) |
| Botting | `commands/botting.rs` (39 КБ) |
| i18n | `src/i18n/`, `src/locales/en/common.json` (51 КБ) |

---

## 3. Общие правила

- **Не тащить новые тяжёлые зависимости.** Win32 через `DllImport`, HTTP через уже
  подключённый RestSharp/HttpClient. Никакого WebView2 без отдельного согласования.
- Настройки — в `RAMSettings.ini` через `AccountManager.General.Set(...)`, дефолты задавать
  там же, где `Perf*` (см. `AccountManager.cs`, блок `if (!General.Exists(...))`).
- UI — WinForms, в стиле существующих форм, с темизацией через `ThemeEditor`.
- Всё новое — **opt-in, по умолчанию выключено**.
- Комментировать только неочевидное: почему так, а не что делает строка.

### Сборка и проверка

```powershell
cd "c:\Users\User\Desktop\Roblox-Account-Manager-master"
dotnet build "RBX Alt Manager\RBX Alt Manager.csproj" -c Debug
```

**Важно при smoke-тестах:** у пользователя постоянно запущен стоковый RAM
(`C:\Users\User\Desktop\Полезно\Roblox Account Manager\`) и несколько `RobloxPlayerBeta` —
идёт фарм. Нельзя:
- запускать тест из папки внутри `%TEMP%` — сработает защита «must be extracted», выход с кодом 1337;
- оставлять `EnableMultiRbx=true` в тестовом `RAMSettings.ini` — тест захватит `ROBLOX_singletonMutex`
  и сломает мультиклиент пользователю;
- забывать убивать тестовый процесс.

Рабочий рецепт: копия `build\Release` в папку на рабочем столе (не в Temp), рядом положить
`RAMSettings.ini` с `EnableMultiRbx=false`, `StartOnLaunch=false`, `EnableWebServer=false`,
`CheckForUpdates=false`, `WindowScale=1.0`, и заглушку `.local-chromium\win64-1108766\`,
чтобы не качать Chromium на 150 МБ.

Проверять, что поднялась именно форма, а не диалог: перечислить окна процесса через
`EnumWindows` и посмотреть класс. `WindowsForms10.Window.*` — форма, `#32770` — MessageBox.
У главной формы рядом появляются два окна `GlassPanelForm` — это оверлеи ObjectListView,
хороший признак что сетка аккаунтов инициализировалась.

---

## 4. Фичи по порядку

Порядок = убывание отношения польза/трудозатраты. Каждая независима, можно брать любую.

---

### A. Job Objects — CPU hard cap + управление resident memory — 🧪 КОД НАПИСАН, НУЖЕН WINDOWS/LIVE SMOKE

**Решение для проверки:** отдельный Job Object на каждый клиент, CPU в режиме `HARD_CAP`, память —
`JOB_OBJECT_LIMIT_WORKINGSET`. Важно: working-set limit **не является потолком выделяемой/commit-памяти**.
Он ограничивает резидентный набор и заставляет Windows активнее вытеснять страницы; это управление давлением
на RAM, а не гарантия «процесс никогда не использует больше N MB». Настоящий commit ceiling потребовал бы
`JOB_OBJECT_LIMIT_PROCESS_MEMORY` и может ломать/ронять клиент при отказе allocation, поэтому его здесь нет.
`KILL_ON_JOB_CLOSE` намеренно не выставляется: закрытие SAM не должно убивать Roblox.
Job не доверяет PID, возвращённому ShellExecute/лаунчером: `ResourceManager.OnLaunched()` до 40 секунд заново
перечисляет процессы именно с именем `RobloxPlayerBeta`, читает их command line и сопоставляет
`browsertrackerid` с аккаунтом. Только найденный таким способом реальный player PID получает Job Object.
Это пока проверено по коду, не live-процессом на Windows.
Хэндлы освобождаются после выхода клиента и при отключении resource manager. Memory priority и Windows power
throttling тоже доступны как opt-in настройки. Реализация — `Classes/ResourceManager.cs`.

Две поправки к исходному разбору, которые сохранены в реализации:

1. **`JOB_OBJECT_LIMIT_PROCESS_MEMORY` в списке ниже — не то, что нужно.** Это лимит на закоммиченную
   виртуальную память: при превышении аллокация **проваливается**, и клиент падает. Он не заставляет Roblox
   экономить. Смыслу «не больше N ГБ» соответствует `JOB_OBJECT_LIMIT_WORKINGSET` (режет резидентную память,
   лишнее уходит в подкачку, клиент жив) плюс `SetProcessInformation(ProcessMemoryPriority, LOW)`.
   Коммит-лимит — только opt-in и с честной подписью «клиент будет убит при превышении».
2. **Формулировка про `KILL_ON_JOB_CLOSE` ниже перевёрнута.** Процессы умирают при закрытии последнего
   хэндла, **только если флаг выставлен**; по умолчанию он снят. Ставить его нельзя — закрытие менеджера
   убило бы все клиенты.

Развилка «per-client или общий бюджет / `HARD_CAP` или `WEIGHT_BASED`» закрыта в пользу per-client +
`HARD_CAP`. Все жёсткие лимиты выключены по умолчанию.

---

#### Исходный разбор раздела A

**Что даёт.** Наш `ResourceManager` умеет только приоритет и affinity — это *подсказки*
планировщику. Job Object даёт **жёсткий** потолок: «этот клиент не получит больше 15% CPU»
и «не больше 2 ГБ RAM». Для 10+ клиентов разница принципиальная.

**Референс:** `platform/windows/optimization.rs`.

Что применяется в v4:
- `CreateJobObjectW` + `AssignProcessToJobObject`
- `JobObjectCpuRateControlInformation` с `JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | HARD_CAP`,
  поле `CpuRate` = проценты × 100
- `JobObjectExtendedLimitInformation` с `JOB_OBJECT_LIMIT_PROCESS_MEMORY` и `ProcessMemoryLimit`
- `SetProcessInformation(ProcessMemoryPriority, MEMORY_PRIORITY_INFORMATION)` —
  значения 1 (VeryLow) / 3 (Low) / 5 (Normal)
- `SetProcessInformation(ProcessPowerThrottling, ...)` с
  `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` и `IGNORE_TIMER_RESOLUTION`

**Куда ложится.** Расширить `Classes/ResourceManager.cs`. Job-хэндл создаётся один раз на
процесс-клиент и хранится в словаре `tracker -> handle`; освобождать при выходе клиента
(в `Poll()` там уже есть чистка `Samples` по мёртвым PID — рядом же чистить хэндлы).
Применять в `OnLaunched()`.

**Настройки** (`[General]`, все opt-in):
```
PerfJobEnabled=false
PerfJobCpuPercent=0        ; 0 = без лимита, иначе 5..100
PerfJobMemoryMB=0          ; 0 = без лимита
PerfMemoryPriority=normal  ; normal | low | verylow
PerfPowerThrottle=false
```

**Подводные камни.**
- Job Object наследуется дочерними процессами. У Roblox есть второй процесс
  (`RobloxCrashHandler`) — проверить, что он не попадает под лимит памяти и не падает.
- Если процесс уже в другом Job (например запущен под Windows Sandbox / некоторыми
  лаунчерами), `AssignProcessToJobObject` вернёт ошибку. Обрабатывать, не падать.
- Хэндл Job нужно держать открытым: закрытие последнего хэндла **убивает все процессы в Job**,
  если не выставлен `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0`. Это самая частая ошибка здесь.
- `CpuRate` = процент × 100 (15% → 1500).

**Оценка:** ~250 строк, полдня. Риск низкий, всё локально в одном классе.

---

### B. Изоляция профиля Roblox — 🧪 КОД НАПИСАН, НУЖЕН WINDOWS/LIVE SMOKE

Реализован только безопасный `light`-режим: `Classes/ProfileCleaner.cs` + форма подтверждения и WebView bridge.
Открытие формы всегда начинает с dry-run, при живом Roblox очистка запрещена. `GlobalBasicSettings_13.xml`
сохраняется и восстанавливается. Каталог `Roblox\Versions` вообще не перечисляется и не меняется; поэтому
ClientAppSettings внутри него не требует бэкапа. MachineGuid, MAC и hardware-spoof режимы отсутствуют.

**Что даёт.** Чистка следов между сессиями: кэш, логи, `AppData\Local\Roblox`,
`HKCU\Software\ROBLOX Corporation`. Полезно при «залипших» настройках клиента и мусоре
на диске (кэш Roblox легко занимает десятки гигабайт).

**Референс:** `platform/windows/isolation.rs` — но брать **только** функции очистки:
`wipe_light`, `wipe_path`, `wipe_glob_in_dir`, `delete_hkcu_roblox`,
`appdata_roaming_roblox`, `localappdata_roblox`, `programdata_roblox`, `windows_prefetch_dir`,
`backup_fast_flags` / `restore_fast_flags`, `backup_basic_settings` / `restore_basic_settings`.

> ### ⛔ Что НЕ портировать
> Из этого файла **не брать**: `new_random_mac`, `new_random_machine_guid`,
> `write_machine_guid_directly`, `build_spoof_script`, `list_network_adapters`,
> и режимы `medium`/`full`, которые их вызывают.
>
> Это подмена аппаратных идентификаторов (MachineGuid в `HKLM\SOFTWARE\Microsoft\Cryptography`
> и MAC сетевого адаптера) ради обхода привязки банов по железу. Это уже не управление
> альтами, а уклонение от блокировок. Оставляем только уровень `light` — очистку профиля.
>
> Плюс практическое: смена MachineGuid ломает лицензии другого софта, DPAPI-привязки и
> активацию Windows. Наш `AccountData.json` шифруется DPAPI — можно потерять все аккаунты.

**Куда ложится.** Новый `Classes/ProfileCleaner.cs` + кнопка в `Forms/SettingsForm.cs`.

**Обязательно.**
- Сначала **dry-run**: показать список того, что будет удалено, и суммарный размер. Только потом удалять.
- Бэкап/восстановление `GlobalBasicSettings_13.xml`; `Versions\**\ClientSettings` исключён целиком и не перечисляется.
- Не запускать при живых `RobloxPlayerBeta` — проверять и предупреждать.
- Никогда не трогать пути внутри нашей собственной папки данных.

**Оценка:** ~350 строк + простая форма, день.

---

### C. Менеджер версий Roblox — 🧪 КОД НАПИСАН, НУЖЕН WINDOWS/LIVE SMOKE

`Classes/VersionManager.cs` получает каталог, скачивает с докачкой и отменой, проверяет хэш каждого zip и
распаковывает immutable-копии в `%LOCALAPPDATA%\SkrilyaAccountManager\Versions`. Закреплённая версия запускается
напрямую через `ClientLauncher`, поэтому стандартный Roblox updater не может заменить её; снятие pin мгновенно
возвращает штатную автообновляемую установку. Неполная установка не считается готовой до записи `.complete`.
`PinnedFallbackToLive=true` по умолчанию: если non-current pin умер/получил disconnect до первого join,
pin атомарно снимается и один повторный запуск идёт через live; причина явно пишется в лог и WebView. Это не
пытается угадать нестабильный текст «outdated client» из лога.

**Что даёт.** Установка и закрепление конкретной версии клиента; откат, когда свежая
версия ломает игру или производительность.

**Референс:** `platform/windows/versions.rs`.

Механика:
- Каталог версий: `https://weao.xyz/api/versions/current` и `.../past`
- CDN: `https://setup-aws.rbxcdn.com` (плюс канальные базовые URL)
- `{version}-rbxPkgManifest.txt` — список пакетов с хэшами
- каждый пакет — zip, распаковывается в свой подкаталог согласно таблице `EXTRACT_ROOTS`
- проверка хэша каждого пакета (`verify_hash`)
- в корень пишется `AppSettings.xml` (константа `APP_SETTINGS_XML`)
- «pristine» копия стандартной установки, чтобы можно было вернуться (`ensure_pristine_roblox`, `swap_in_standard_roblox`)

**Куда ложится.** Новый `Classes/VersionManager.cs` + форма со списком версий и прогрессом.
Хранить версии в `%LOCALAPPDATA%\SkrilyaAccountManager\Versions\<channel>\<hash>\`.
`Classes/ClientSettingsPatcher.cs` резолвит путь установки через `HKCR\roblox\DefaultIcon` —
при закреплённой версии его надо переключать на выбранный каталог.

**Подводные камни.**
- Roblox сам себя обновляет при запуске — закрепление версии придётся защищать
  (либо запускать `RobloxPlayerBeta.exe` напрямую, минуя лаунчер, либо блокировать апдейтер).
  **Это основная сложность фичи**, спланировать до начала кодирования.
- `EXTRACT_ROOTS` — таблица «имя пакета → подкаталог». Перенести один в один, иначе клиент не стартует.
- Скачивание — сотни мегабайт: обязательно прогресс, отмена и докачка.
- Хэши проверять всегда.

**Оценка:** ~700 строк + форма, 2–3 дня. Самая объёмная из списка.

---

### D. AFK-режим — 🧪 КОД НАПИСАН, POSTMESSAGE НА ЖИВОМ ROBLOX НЕ ПРОВЕРЕН

`Classes/AntiAfk.cs` отправляет `WM_KEYDOWN/WM_KEYUP` через `PostMessage` без кражи фокуса; интервал, клавиша и
пауза между окнами настраиваются. По умолчанию выключено. В новом UI есть управление и ручной pulse.

**Что даёт.** Периодическая отправка нажатия клавиши во все окна клиентов, чтобы Roblox
не выкинул за бездействие.

**Референс:** `commands/afk.rs`. Конфиг простой:
`interval_seconds`, `key`, `inter_window_delay_ms`.

**Куда ложится.** Новый `Classes/AntiAfk.cs`. Окна клиентов уже находятся —
`ResourceManager.MapProcesses()` даёт `tracker -> Process`, оттуда `MainWindowHandle`.

Отправлять через `PostMessage(hWnd, WM_KEYDOWN/WM_KEYUP, ...)` — **не** через `SendInput`:
`SendInput` работает только с активным окном и будет воровать фокус у пользователя.
Константы `WM_*` уже есть в `Classes/WM.cs`.

**Настройки:**
```
AfkEnabled=false
AfkIntervalSeconds=300
AfkKey=Space
AfkInterWindowDelayMs=150
```

**Подводные камни.**
- Некоторые игры игнорируют синтетический ввод через `PostMessage`. Это ограничение метода, честно сказать в UI.
- Не слать в свёрнутые окна, если это ничего не даёт — проверить на практике.
- Интервал сильно меньше 60 с смысла не имеет и только грузит систему.

**Оценка:** ~150 строк, полдня. Самая простая фича в списке.

---

### E. Генератор аккаунтов — 🧪 ИЗОЛИРОВАННЫЙ OPT-IN, НУЖЕН BUILD/API SMOKE

**Что даёт.** Массовое создание аккаунтов.

**Референс:** `commands/generators.rs`.

Важное: v4 **не создаёт аккаунты сам**. Это клиент стороннего платного API —
BloxGen (`https://core.bloxgen.net`, документация `https://docs.bloxgen.net`).
Логика: ключ API → запрос генерации → получение куки → `add_generated_account` в хранилище.
Есть проверка баланса ключа (`provider_test_key`).

**Куда ложится.** `Classes/AccountGenerator.cs` + форма. Добавление — через существующий
`AccountManager.AddAccount(cookie)`.

**Принятое решение:** оставить BloxGen строго опциональным внешним провайдером с
`BloxGenEnabled=false` по умолчанию. Ключ API не сохраняется;
баланс проверяется отдельно, ответ генерации валидируется через штатный `AddAccount`, кука сразу попадает в
зашифрованное хранилище и не возвращается HTML-интерфейсу. Если сервис недоступен, остальная программа от него
не зависит. Реализация — `Classes/AccountGenerator.cs` + WinForms/WebView UI. Проверенная документация провайдера:
[`Introduction`](https://docs.bloxgen.net/introduction), [`Generate`](https://docs.bloxgen.net/api-reference/generate),
[`Balance`](https://docs.bloxgen.net/api-reference/balance).

**Оценка:** ~300 строк + форма, день. Но сначала решить, нужна ли.

---

### F. Botting-режим — 🧪 КОД НАПИСАН, НУЖЕН WINDOWS/LIVE SMOKE

**Что даёт.** По расписанию поднимает «бот»-аккаунты в игру рядом с «игровыми»
аккаунтами. Интервал в v4 — 10..480 минут, при остановке опционально закрывает бот-клиенты.

**Референс:** `commands/botting.rs` — `run_botting_session`, `launch_account_for_cycle`,
`set_botting_player_accounts`, `botting_account_action`.

**Куда ложится.** Внимание: у нас **уже есть** очень похожее — `AutoRelaunchTimer` в
`Nexus/AccountControl.cs`. Он умеет перезапускать аккаунты по presence или по таймауту пинга.

Архитектура сведена к двум разным обязанностям: `BottingSession` владеет только ролями и расписанием циклов,
а `Relauncher.Request` — единственная точка автоматического recovery. Старый Nexus watchdog тоже переведён на
`Relauncher.Request`, поэтому третьего конкурирующего релаунчера не появилось. Запуски по-прежнему проходят
через штатный `JoinServer`/`LaunchLock` и `AccountJoinDelay`.

**Подводные камни.**
- Одновременный запуск многих клиентов упирается в `LaunchLock` (по аккаунту) и в задержку
  `AccountJoinDelay`. Учитывать, иначе Roblox начнёт отбивать логины.
- Не дублировать логику с AutoRelaunch — иначе два таймера будут драться за один аккаунт.

**Оценка:** ~400 строк, 1–2 дня, если делать надстройкой. Если отдельной подсистемой — вдвое больше и появятся конфликты.

---

### G-новый UI. Русский язык в HTML-интерфейсе — ✅ РЕАЛИЗОВАНО 2026-08-07

Словарь `ui/i18n.js`, переключатель — чип в заголовке (дизайнер его нарисовал, но не подключил), выбор пишется
в `General/UiLanguage`. HTML остаётся на своём JS-словаре; подготовленный раздел G отдельно применяет тот же
выбор языка к caption-контролам старого WinForms через `.resx`.

**Ключ словаря — английский исходный текст, а не выдуманный id.** Причина: страница регулярно пересобирается
из экспорта дизайнера, и любой id, вписанный в его вёрстку, потерялся бы при следующем импорте, а английский
текст — это то, что в экспорте действительно есть. Непереведённое остаётся по-английски, а не показывает
сырой ключ. `I18N.apply()` заменяет только точные совпадения, поэтому имена аккаунтов и игр задеть не может.

**Попутно найдены и починены два бага, из-за которых экран настроек был нерабочим у всех:**
мост camelCase'ил **ключи словарей** (`DirectLaunch` → `directLaunch`), и все значения на экране настроек
выглядели как невыставленные; парсер ini **выбрасывал комментарии** при чтении, а они и есть описания под
переключателями. Плюс `IniSection.Seed` — привязывает описание и к уже существующим ключам (83 строки сидинга
переведены). Подробности — в памяти проекта.

### G. Локализация (i18n) — 🧪 КОД НАПИСАН, НУЖЕН WINDOWS/UI SMOKE

`Classes/LegacyI18n.cs` применяет выбранный `UiLanguage` к caption-контролам старого WinForms UI и меню;
пользовательские TextBox-значения не переводятся. Русские строки лежат в `Localization/LegacyStrings.ru.resx`,
ресурс подключён явно с той же схемой manifest name, что остальные legacy `.resx`.

**Что даёт.** Русский интерфейс (сейчас всё на английском).

**Референс:** `src/locales/en/common.json` (51 КБ) и `de/common.json`. Готовых русских строк
там нет, но структура ключей и полный список фраз — есть, это экономит разбор UI.

**Куда ложится.** У WinForms штатный механизм — `.resx` на культуру
(`SettingsForm.ru.resx` и т.д.) плюс `Thread.CurrentThread.CurrentUICulture`.

**Внимание — грабли, которые уже стреляли в этом проекте.** У нас в csproj включено
`EnableDefaultEmbeddedResourceItems=false`, и все `.resx` перечислены вручную с явным
`LogicalName`, потому что легаси-имена ресурсов не совпадают с путями файлов
(см. комментарий в `RBX Alt Manager.csproj`). Любой новый `.resx` нужно добавлять туда же
и с правильным именем, иначе он молча не подхватится в рантайме.
Также сейчас стоит `SatelliteResourceLanguages=en` — при добавлении локалей это надо снять.

**Оценка:** каркас — день; собственно перевод всех строк — отдельная долгая работа.

---

### H. Сбор бесплатных предметов + база прогрева — ✅ РЕАЛИЗОВАНО 2026-08-04

**Что даёт.** Массовый забор бесплатных предметов каталога на выбранные аккаунты.
Плюс фундамент для «прогрева»: избранное игр и разнесённая по времени активность.
Польза двойная — часть бесплатных UGC со временем дорожает, а одетый аватар сам по себе
снимает признак «свежий нулевой альт».

**Референса в v4 НЕТ.** Единственная фича в документе без готового кода для сверки.
Источник — разведка RIFT (их Account Warmer) плюс официальные эндпоинты Roblox.

#### Что реально сделано

| Файл | Что |
|---|---|
| `Classes/FreeItems.cs` (новый, ~450 строк) | `CatalogItem`, `PurchaseResult`, `FreeItemsSummary`, `DiscoverAsync`, `RunAsync`, CSRF-обвязка, backoff |
| `Classes/Account.cs` | `PurchaseFreeAsync` + `PurchaseCollectible` / `PurchaseClassic` / `Interpret`; `HarvestRotatedCookie` открыт до `internal` |
| `AccountManager.cs` | `CatalogClient` / `ApisClient` / `InventoryClient`; дефолты `FreeItems*`; обработчик `collectFreeItemsToolStripMenuItem_Click` |
| `AccountManager.Designer.cs` | пункт `Collect Free Items` в `AccountsStrip` |

#### Фаза 0 — результаты проверки живого API (проверено, не по памяти)

| Шаг | Запрос | Итог |
|---|---|---|
| Поиск | `GET catalog/v1/search/items?category=All&maxPrice=0&salesTypeFilter=1&limit=30` | 200 без авторизации. **`itemType` — строка** (`"Asset"`/`"Bundle"`), не число. Бандлы приходят вперемешку (~40%), пагинация по `nextPageCursor` |
| Детали | `POST catalog/v1/catalog/items/details` | Нужен **CSRF даже без куки**: первый POST → 403 + токен в заголовке `x-csrf-token`, повтор с ним → 200 |
| `collectibleProductId` | `POST apis/marketplace-items/v1/items/details`, тело `{itemIds:[...]}` | 200. Отвечает **голым массивом**, без обёртки `{data:[]}`. Даёт `collectibleProductId`, `creatorId`, `creatorType` |
| Покупка UGC | `POST apis/marketplace-sales/v1/item/{cid}/purchase-item` | Без куки — 401 `code 9002`, то есть путь и форма тела приняты |
| Владение | `GET inventory/v1/users/{userId}/items/Asset/{assetId}/is-owned` | 403 для чужого приватного инвентаря; со своей кукой — рабочий путь |

**Ключевое открытие, изменившее дизайн:** `collectibleItemId` есть **почти у всего**, включая
классику 2017 года — Roblox мигрировал бесплатные предметы в систему коллекционок.
В прогоне 8 из 8 предметов пошли по collectible-пути, классика — ноль. Поэтому collectible
основной путь, а `economy/v1/purchases/products` — запасной, а не наоборот.

**Второе:** discovery не зависит от аккаунта, поэтому выполняется **один раз** на весь прогон,
а не на каждый аккаунт. Это принципиально для рейт-лимита — каталог лимитирует по IP жёстче,
чем покупка.

**Третье:** 429 прилетает уже со второго-третьего запроса к `items/details` даже с паузой 1.5 с.
Отсюда консервативные дефолты и батчи по 30, а не по 100.

> Заметка: наш `Join Group` (`AccountManager.cs:2202`) поднимает браузер и кликает по
> `#group-join-button`. Здесь так намеренно **не** сделано — только прямые API-запросы.

**Настройки** (`[General]`, все с дефолтами):
```
FreeItemsDelay=1500                 ; мс между покупками внутри одного аккаунта
FreeItemsSearchDelay=1200           ; мс между каталожными запросами при discovery
FreeItemsMaxConcurrentAccounts=3
FreeItemsMaxPerRun=50
FreeItemsSkipOwned=true
```

**Как устроено (для того, кто будет править).**
- `PurchaseResult` — enum, а не `bool`: `Ok / AlreadyOwned / SoldOut / NotPurchasable /
  PriceChanged / RateLimited / Unauthorized / Failed`. Иначе не отличить «уже есть» от 429.
- **CSRF кэшируется** на весь прогон: у аккаунта — из `GetCSRFToken()`, у discovery —
  общий `SharedToken`, полученный с 403-отбоя. Без этого на 50 предметах было бы 50 лишних
  запросов и гарантированный рейт-лимит.
- `Interpret()` в `Account.cs` **логирует тело ответа дословно** на любом отказе. Это механизм
  обнаружения смены API: если Roblox поменяет форму, в логе будет тело, а не молчаливый `Failed`.
- ⚠️ **Порядок полей в `Interpret()` критичен, не переставлять.** Collectible-эндпоинт кладёт
  в `purchaseResult` бесполезную фразу `"Purchase transaction is failed."` на **любой** отказ,
  а машиночитаемую причину — в `errorMessage`. Поэтому `errorMessage` читается **первым**.
  Изначально было наоборот, и все отказы выглядели одинаково. Найдено тестом, не рассуждением.
- `QuantityLimitExceeded` — это и есть «уже владеешь»: у бесплатных предметов лимит на
  пользователя равен единице, поэтому исчерпанный лимит и владение — одно состояние.
- `HarvestRotatedCookie` зовётся на каждом ответе покупки — иначе ротированная кука теряется.
- Стоп по аккаунту после **3 подряд** 429, `SemaphoreSlim` на аккаунты, try/catch на каждый
  предмет. `SoldOut` — не ошибка, идём дальше.
- Последний предохранитель: `PurchaseFreeAsync` сам отказывается от предмета с `Price != 0`,
  независимо от того, что решил вызывающий. Робуксы не тратятся никогда.

#### Тесты

Харнесс лежит в скрэтчпаде сессии (`FreeItemsTest/`) — отдельный net10-проект, дёргает
**настоящий** скомпилированный код через рефлексию (типы `internal`), поднимая статические
`RestClient`ы сам:

```
FreeItemsTest.exe 8                    # только discovery, аккаунтов не касается
FreeItemsTest.exe 8 <файл-с-кукой>     # discovery + ОДНА покупка
```

Кука читается из файла и никогда не печатается. Результаты на 2026-08-04:

| Тест | Итог |
|---|---|
| Discovery предметов на живом API | **PASS** — 6/6 с полными парами collectible, 0 утечек ненулевой цены, ~5.8 с |
| Запуск SAM (smoke) | **PASS** — главная форма поднялась, MessageBox нет, `RobloxPlayerBeta` 2→2 (фарм не тронут) |
| Покупка нового предмета | **PASS** — `PurchaseResult.Ok`, предмет реально получен по collectible-пути |
| Покупка того же предмета повторно | **PASS** — `PurchaseResult.AlreadyOwned` (после фикса порядка полей) |
| `IsOwned()` на живом аккаунте | **PASS** — вернула `True` для принадлежащего предмета |
| Чарты для прогрева | **PASS** — 241 игра одним запросом, без авторизации |
| Добавление в избранное | **PASS** — `WarmResult.Ok`, игра реально добавлена |

> **Ловушка харнесса, стоившая одного ложного вывода.** У тестового проекта `<Reference>` на
> `SkrilyaAccountManager.dll`, поэтому MSBuild копирует её в свой `bin`, и копия попадает в
> TPA-список. `Assembly.LoadFrom(путь)` в .NET Core сначала пробует штатное разрешение и
> отдаёт **эту копию**, а не файл по указанному пути. Пересборка только продукта, без
> пересборки харнесса, молча тестирует предыдущую сборку. В харнесс добавлена проверка:
> он печатает реальный путь загруженной сборки и выходит с кодом 99, если продукт новее.
> **Всегда собирать оба проекта.**
>
> Второй предохранитель там же: харнесс поднимает статические `RestClient`ы сам (в приложении
> это делает инициализация формы) и на старте проверяет, что **все** они не `null`, иначе
> выходит с кодом 98. Забытый клиент иначе роняет прогон NullReference на середине.

Smoke-тест обязательно проверяет `EnableMultiRbx=false` в `build\Debug\RAMSettings.ini`
и отказывается стартовать иначе (см. раздел 3 — у пользователя идёт фарм).

**Побочное наблюдение из тестов:** два прогона discovery подряд ловят рейт-лимит, и он
возвращает пустой список. Это не ошибка и обработано — в обработчике меню стоит проверка
`Items.Count == 0` с внятным сообщением. Учитывать при ручных проверках: между прогонами
нужна пауза порядка минуты.

**Почему `FreeItemsSkipOwned=true` должен остаться дефолтом.** Тест показал, что Roblox не
сообщает причину отказа в покупке внятно: `purchaseResult` всегда generic, а `errorMessage`
хоть и конкретен, но это ответ *после* попытки. Предварительный `IsOwned()` — единственный
достоверный источник факта владения, и он проверен на живом аккаунте (вернул `True` для
принадлежащего предмета). При `SkipOwned=false` фича продолжит работать, но счётчик
пропущенных станет менее осмысленным.

#### Фаза 2 — прогрев (избранное + планировщик), реализована 2026-08-04

`Classes/Warmer.cs` — `WarmGame`, `WarmResult`, `WarmerSummary`, `DiscoverGamesAsync`,
`RunAsync`, `StartScheduler`. Плюс `Account.FavoriteGameAsync` и пункт меню `Warm Accounts`.

**Проверка эндпоинтов (Фаза 0 для этой части):**

| Запрос | Итог |
|---|---|
| `GET games/v1/games/list` | **404, эндпоинт мёртв.** Не использовать |
| `GET apis/explore-api/v1/get-sorts?sessionId=<guid>` | 200 без авторизации. **Игры приходят прямо здесь**, внутри `sorts[].games[]`, сразу с `universeId` и `rootPlaceId`. ~240–390 штук за один запрос |
| `GET apis/explore-api/v1/get-sort-content?sortId=...` | Работает, но **не нужен** — данные уже есть в `get-sorts` |
| `GET apis/universes/v1/places/{placeId}/universe` | 200, `{universeId}`. Нужен, только если игра пришла с `placeId` |
| `POST games/v1/games/{universeId}/favorites` `{isFavorited:true}` | Без куки — 401 `code 9002`, путь и тело приняты |
| `GET games/v1/games/{universeId}/favorites` | Требует авторизации, отдаёт `{isFavorited}` — используется как предпроверка |

**Настройки** (`[General]`, всё выключено по умолчанию):
```
WarmerEnabled=false             ; планировщик; opt-in намеренно
WarmerGroup=                    ; пусто = все валидные аккаунты
WarmerIntervalMinutes=180       ; минимум 15
WarmerFavoritesPerPass=2
WarmerDelay=2500
WarmerMaxConcurrentAccounts=2
```

**Решения, которые стоит сохранить.**
- Чарты кэшируются на час и **общие для всех аккаунтов** — они не зависят от аккаунта,
  а планировщик работает по таймеру.
- `FavoriteGameAsync` сначала читает текущее состояние: повторное добавление это no-op,
  за который рейт-лимит всё равно списывается.
- Спонсорские позиции (`isSponsored`) отбрасываются — это реклама, а не органика чартов.
- `WarmerEnabled=false` по умолчанию потому, что фича действует на аккаунты по таймеру без
  наблюдения пользователя. Включение должно быть решением, а не следствием обновления.
- В комментарии к классу зафиксировано, что это **не** средство «сделать альтов
  необнаружимыми»: ничего не инжектится и не подменяется. Формулировка защищает от того,
  чтобы к этому классу позже пристроили спуф идентификаторов (отвергнут в разделах B и 6).

**Чего здесь НЕ делать.**
- Не смешивать с разделом E: генерация аккаунтов с обходом капчи — другой уровень риска
  (банят IP-пулами), и это отдельное решение.
- Не тащить «уникальный device-id на аккаунт», который обещает RIFT. Мы запускаем
  официальный клиент — он репортит настоящее железо. Не решается без влезания в процесс,
  а спуф идентификаторов мы уже отвергли (раздел B и раздел 6).

**Статус: готово.** Проверено на живом API целиком — discovery, покупка нового предмета,
повторная покупка (`AlreadyOwned`), `IsOwned()`, запуск приложения. Ветка
`PurchaseCollectible` подтверждена по факту приобретения предмета.

**Что осталось непроверенным:** классический путь `economy/v1/purchases/products` — в каталоге
не нашлось ни одного бесплатного предмета без `collectibleItemId`, так что выполнить его было
не на чем. Он остаётся запасной веткой на случай, если Roblox вернёт старую схему.

**Фаза 2 (избранное + планировщик) реализована.** `Warmer.StartScheduler()` вызывается при запуске,
а ручной `Warm Accounts` остаётся доступен из меню.

---

### I. Прокси на аккаунт + управление инстансом клиента — ✅ РЕАЛИЗОВАНО 2026-08-07

**Что даёт.** Каждый аккаунт логинится и играет через свой прокси; клиент запускается напрямую,
а не через протокол, поэтому у нас есть его PID, его окружение и его окно. Побочно чинит
запуск как таковой (см. ниже) и возвращает к жизни четыре подсистемы, которые молча отвалились.

**Референса в v4 НЕТ.** Источник — рабочий Python-стек (мультиинстанс + прокси-на-слот +
кастомное имя окна), портированный на C#.

#### Две находки, ради которых стоило лезть в код

1. **Протокол `roblox-player:` сегодня зарегистрирован прямо на `RobloxPlayerBeta.exe "%1"`**
   (проверено в реестре HKCU/HKLM и по командным строкам живых клиентов). Отсюда два следствия:
   прямой запуск exe даёт **байт-в-байт ту же** командную строку, что и ShellExecute, то есть он
   не «другой способ», а тот же самый плюс контроль env/PID; и командная строка клиента больше
   **не содержит `-b <id>`**, только `browsertrackerid:<id>`.
2. **Старый путь запуска не мог работать на .NET 10.** `new ProcessStartInfo()` оставляет
   `UseShellExecute = false`, а `FileName` там — URI, не файл: `Process.Start` кидает
   Win32Exception и всё уходило в `catch` с «Try re-installing Roblox». Ветка `UseOldJoin`
   при этом вообще никогда не вызывала `Process.Start` — собирала `ProcessStartInfo` и
   выбрасывала его, возвращая «Success».

Из (1) следовало, что `-b (\d+)` не матчился ни у кого: не работали `AutoCloseLastProcess`,
`AdjustWindowPosition`, `ResourceManager` (приоритет/аффинити) и весь `RobloxWatcher`
(он ещё и отсеивал клиентов проверкой `-t `/`-j `).

#### Что реально сделано

| Файл | Что |
|---|---|
| `Classes/ProxyConfig.cs` (новый) | Разбор 5 форматов прокси, `ToWebProxy`/`ToEnvUrl`, кэш exit-IP (`ProxyExitIP`), мост HTTP→SOCKS (`SocksBridge`) |
| `Classes/SlotProxy.cs` (новый) | Локальный CONNECT-прокси на слот: identity-хосты → апстрим, ассеты → напрямую; SOCKS4a/5 и HTTP-апстрим; **без direct-фолбэка для identity** |
| `Classes/ClientLauncher.cs` (новый) | Поиск `RobloxPlayerBeta.exe` (реестр → папки версий), прямой запуск с env-прокси; общий `TrackerRegex` (`-b` **и** `browsertrackerid:`); `HiddenDesktop` (CreateDesktopW + CreateProcessW) |
| `Classes/ClientWindows.cs` (новый) | Имя окна по шаблону + переустановка на таймере (клиент сам перетирает заголовок), закрытие Win32-модалок Roblox |
| `Classes/ClientLogWatcher.cs` (новый) | Результат запуска из лога клиента: `Joined` / `Disconnected` / `DiedEarly` (обычно отвергнутый тикет). Лог ищется по PID через `handle.bin`, иначе по времени + placeId |
| `Classes/AccountProxies.cs` (новый) | Связка «аккаунт → прокси», кэш `RestClient` на прокси, проверка exit-IP, жизненный цикл слота (умирает вместе с клиентом) |
| `Classes/Account.cs` | `GetCSRFToken`/`GetAuthTicket` принимают прокси; ретрай на 403/409 (протухший CSRF), явное сообщение на 429; путь запуска переписан; `Fields` → `ConcurrentDictionary` |
| `Classes/RobloxWatcher.cs`, `ResourceManager.cs` | Общий трекер-regex; правило «убить по чужому заголовку» пропускает окна, переименованные нами |
| `Forms/ProxyAssignForm.cs` (новый) | Раздача прокси по выделенным аккаунтам (по кругу), «Test» проверяет exit-IP; пункт «Assign Proxies» в контекстном меню |
| `AccountManager.cs` | Дефолты `DirectLaunch`, `UseAccountProxies`, `ProxySplitTunnel`, `ProxyIdentityHosts`, `ProxyVerifyExitIP`, `RenameClientWindows`, `ClientWindowTitle`, `UseSeparateDesktop`, `CloseRobloxErrorDialogs`, `WatchLaunchResult` |

**Почему липкий IP обязателен.** Тикет привязан к IP, с которого он выдан. Поэтому прокси
резолвится **до** первого запроса и один и тот же используется для CSRF, тикета и клиента;
при смене exit-IP между запусками в лог идёт предупреждение «прокси ротируется».

**Подводные камни.** Отдельный рабочий стол прячет окна от всего, что работает через
`MainWindowHandle` (позиции окон, минимизация, динамический приоритет по фокусу) — поэтому
выключен по умолчанию. Заголовки окон конфликтовали бы с `CloseIfWindowTitle` — правило
научено пропускать наши. `IniFile.Set` удаляет ключ с пустым значением, поэтому «выключено»
выражено отдельным bool-ключом, а не пустой строкой.

**Что осталось непроверенным:** уважает ли конкретная сборка клиента `http_proxy` (на стенде
проверен сам слот-прокси, 20/20 ассертов, но не клиент). Если игнорирует — через 45 секунд
в лог падает предупреждение «клиент не использовал свой слот»; логин менеджера при этом всё
равно ушёл через прокси.

---

### J. Подготовка к смене UI: развязка логики и оболочки — ✅ СДЕЛАНО 2026-08-07

**Что даёт.** Логика перестала зависеть от WinForms, поэтому новый UI (WPF или WebView2 —
решение отложено) не требует переписывать `Classes/`. Побочно чинит реальный дефект: запуск
из веб-API или с фонового таймера мог открыть модальное окно на машине, где никто не смотрит,
и заблокировать поток.

| Файл | Что |
|---|---|
| `Classes/Shell.cs` (новый) | `IShell` — то, что логика просит у оболочки: `Info/Warn/Error`, `Confirm`, `OnUiThread`, `LaunchFinished`, `CancelLaunching`, `RefreshAccounts`, `ShowMissingAssets`. Статический фасад `Shell` + `HeadlessShell` по умолчанию (всё в лог, на вопросы отвечает «нет») |
| `Classes/WinFormsShell.cs` (новый) | Текущая реализация: единственное место в пути логики, которому позволено знать про `MessageBox`, главную форму и список аккаунтов. Сама маршалит в UI-поток |
| `Classes/Account.cs` | 11 `MessageBox`, 2 `YesNoPrompt`, 6 обращений к главной форме и показ окна `MissingAssets` → через `Shell`. **Файл больше не ссылается на WinForms вообще** (проверено сборкой без `using System.Windows.Forms`) |
| `Classes/Presence.cs` | Обновление строк списка → `Shell.RefreshAccounts` |
| `AccountManager.cs` | Регистрация `Shell.Current = new WinFormsShell()` до `InitializeComponent` |

**Заодно исправлено:** запрос приватности приватных серверов выполнялся внутри `Invoke`, то есть
HTTP-запрос к Roblox шёл на UI-потоке и подвешивал отрисовку; теперь только вопрос пользователю
блокирующий, сам запрос — нет.

**Что НЕ тронуто:** Nexus (`Nexus/ControlledAccount.cs`, 18 обращений к своей форме) — там UI
создаётся динамически из Lua, это отдельный мост и отдельная задача. `Program.cs` — это хост
WinForms по определению.

**Состояние решения по UI:** прототипы одного экрана на обоих стеках лежат в `ui-preview/`
(скриншоты + исходники, WPF-прототип собирается и запускается). Выбран был WPF, но затем
появилось условие «дизайн придёт готовым HTML от дизайнера» — если он будет присылать правки,
WebView2 возвращается в игру, потому что на WPF каждая правка означает перевод вёрстки в XAML
заново. Развязка выше сделана так, что от этого выбора не зависит.

---

### K. Присмотр за клиентами: рипер, детектор, перезапуск — ✅ РЕАЛИЗОВАНО 2026-08-07

**Что даёт.** Мёртвые клиенты убираются, живые классифицируются по их собственному логу, а упавшие
возвращаются в игру — с ограничителем, чтобы это не превратилось в долбёжку.

**Референс** — питоновский `RobloxReaper` пользователя (`SkrilyaReaper.py` и модули рядом). Портированы
идеи, не код; часть решений у него оказалась лучше моих и была перенята (три отдельных выключателя убийства,
белый список PID, уборка осиротевших crash handler'ов, короткий грейс для ошибки авторизации).
**Релаунчер оттуда НЕ портировался** — он там не работает и выключен в конфиге; вместо него написан свой,
поверх сигналов, которых у питоновской версии не было.

| Файл | Что |
|---|---|
| `Classes/ZombieReaper.cs` | Убивает клиенты без видимого окна старше грейса. Не трогает служебный процесс Roblox, клиенты на отдельном рабочем столе и белый список |
| `Classes/StuckDetector.cs` | Читает лог каждого клиента: `Joined` / `AuthError` / `Stuck` / `TrayApp` / `Unknown`. Привязка PID→лог по `BrowserTrackerId`, без Sysinternals |
| `Classes/LaunchBudget.cs` | Кулдаун на аккаунт, потолок в час, отказ после серии неудач. 20 тестов |
| `Classes/Relauncher.cs` | Подписан на результат запуска и на вердикты детектора; не повторяет `CAPTCHA` и `RATE LIMITED`; работает без Nexus |

**Находки, ради которых стоило лезть в реальные логи** (14 файлов на машине):
`waitForNewPlayerProcess` встречается 34 682 раза в логе здорового клиента — как признак «shim» бесполезен.
Первый join лежит на 28-й килобайте 11-мегабайтного лога, поэтому «хвостовое» чтение объявляет зашедшего
клиента застрявшим. И главное: **шесть из четырнадцати логов — это Roblox в режиме трея**, он держит окно,
никогда не заходит и был бы убит как застрявший; опознаётся по `AppState/TrayMode` на 4-й строке.
Голый `429` в маркеры не взят — 1148 штук в логах клиентов, которые прекрасно играли.

**Чего нет:** `kill_captcha` из их конфига не воспроизведён — капча ловится раньше, на этапе тикета
(`Account.LastAuthFailure == "CAPTCHA"`), и клиент в этом случае просто не появляется. Уборка crash
handler'ов сознательно срабатывает только когда не осталось ни одного клиента: иначе нельзя быть уверенным,
что handler осиротел.

---

### L. Экраны настроек, прокси и списка серверов в новом UI — ✅ РЕАЛИЗОВАНО 2026-08-07

**Что даёт.** Три экрана из третьего экспорта дизайнера («Nocturne (2)») перенесены в `ui/index.html` и
подключены к приложению. Рельса слева переключает экраны, полоска вкладок — панели экрана аккаунтов.

| Экран | Откуда данные | Методы моста |
|---|---|---|
| Настройки | реальные ключи `RAMSettings.ini`, 7 разделов, 47 переключателей | `settings.describe`, `settings.set` |
| Прокси | аккаунты, сгруппированные по своему полю `Proxy` | `proxies.list`, `proxies.check`, `proxies.assign` |
| Список серверов | публичный список Roblox + недавние и закреплённые игры | `servers.list`, `games.recent`, `games.favourites`, `games.favourite` |

**Решение, которое стоит помнить:** шесть разделов настроек, придуманных дизайнером (`Appearance`,
`WindowLayout`, `Bloxstrap`…), выброшены — вместо них рендерятся настоящие ключи ini, а подпись под каждым
переключателем берётся из комментария рядом с этим ключом (`settings.describe`). Одна правда, а не две:
поправил комментарий при `General.Set(...)` — изменился текст в интерфейсе.

**Учётные данные прокси через мост не ходят.** Страница видит `scheme://user:***@host:port`, проверку делает
приложение и само записывает адрес выхода обратно на аккаунты. `alive` трёхзначный: «не проверяли» — не то
же самое, что «мёртвый».

**Чего нет:** региона сервера. Публичный список Roblox его не отдаёт, а старое окно получает его join-запросом
на каждый сервер — это дорого и палевно, поэтому в колонке региона стоит job id, под ним — FPS сервера.
Пинг показывается только когда Roblox его прислал; `—` значит «не измерен», а не «быстрый».

**Две ловушки, найденные проверками** (79 автопроверок, все зелёные):
`<template>` лежал внутри контейнера, который перерисовывается через `textContent = ''` — первая же
перерисовка уносила шаблон, и всё дальнейшее молча ничего не делало. Теперь все шаблоны живут в `<body>`.
И вторая: headless-рендер с `--virtual-time-budget` не проигрывает CSS-переходы, а дизайн анимирует
`background-color` у каждого `div` — на таком скриншоте всё, что перекрасил скрипт, снято в старом цвете.
Состояние проверять только в WebView2.

### N. Меню строки, Free items и починка детектора — ✅ РЕАЛИЗОВАНО 2026-08-07

**Меню строки.** Девять команд перестали быть заглушками: шесть `tool-*` через новый `accounts.openBrowser`
(одна ручка, страница задаёт адрес), `follow` через `accounts.follow` (принимает **имя**, резолв на стороне
приложения), `signout` через `accounts.signOutOthers` (`Account.LogOutOfOtherSessions` уже существовал —
аудит ошибочно считал, что его нет), `join-job` — прямым вызовом `accounts.launch`.

**Ловушка `join-job`:** нельзя записать значение в поле Job ID и позвать `launch()` — тот схлопывает
VIP-код и job id в один параметр (`vipValue || jobId`), и `VIP:abc` уехал бы как job id при `joinVip:false`.
Приватный сервер — это отдельный параметр. Аналогично `accounts.follow`: `JoinServer` при `FollowUser`
несёт **user id в параметре placeId**, и эта странность сознательно оставлена внутри моста.

`set-password` / `set-flags` теперь отказывают **до** открытия промпта: раньше пользователь вводил настоящий
пароль от аккаунта, и ввод молча выбрасывался.

**Free items.** `Classes/FreeItemsRunner.cs` владеет одним прогоном, отменой и трансляцией
`IProgress<string>` в события `freeitems.progress` / `freeitems.done`; методы моста `freeitems.start/stop/state`.
Экран дизайнером не рисовался — собран в `app.js` на его токенах. Правило: строить блок в `wire()`, а не в
`open()` — `Screens.show()` расставляет `hidden` до вызова `open()`.

**Баг детектора (был самым опасным).** `Tail` кэшировался по `BrowserTrackerID`, а тот у аккаунта **вечный**:
Roblox пишет новый лог на сессию и не удаляет старый, поэтому кэш никогда не промахивался и все вердикты
после первой сессии были её повтором — «зашёл» навсегда, либо «отказано» навсегда (а с `StuckKillAuthErrors`
это убивало каждый новый клиент через 10 секунд). Починено `StuckDetector.NoteLaunch(tracker)` на обеих
ветках запуска. **Одного сброса мало:** первые секунды новый клиент ещё не создал лог, и пересканирование
подхватило бы старый файл — теперь есть отметка времени запуска, и лог старше неё не принимается
(до появления своего лога клиент читается как «starting», что ничего не убивает).
15 автопроверок гоняют настоящий класс через рефлексию, включая эту гонку.

---

### O. Хвосты нового UI: группы, массовые операции, presence — ✅ РЕАЛИЗОВАНО 2026-08-08

**Группы.** Появились `groups.rename` и `groups.delete`, меню по правому клику на чипе (дизайн его обещает
подсказкой, но разметки не дал — собран в `app.js` на его токенах) и рабочий чип «+».
Ключевое: **группа — не объект**, это строка на каждом аккаунте плюс две настройки. Поэтому переименование
переписывает и все аккаунты, и `GroupOrder`, и `DefaultGroup` (пропустишь любое — группа воскреснет пустой
или новые аккаунты будут уезжать в несуществующую). Удаление не может удалить аккаунты — они переезжают в
Default, и интерфейс говорит об этом до подтверждения. Пустая группа не хранится, поэтому «+» переносит
отмеченные аккаунты в новую группу, а не создаёт пустой чип, который исчез бы при обновлении.

**Массовые операции.** Меню, открытое на строке внутри выделения, действует на всё выделение; открытое вне —
сначала делает эту строку выделением. Заголовок меню при этом пишет «выбрано аккаунтов: N».
`accounts.copy` / `remove` / `setGroup` принимают массив — стор пишется один раз, а не N раз;
`revalidate` сознательно идёт по одному (это запросы к Roblox). **Алиас и описание массово не применяются** —
они именно то, чем аккаунты отличаются друг от друга.

**Что поймало состязательное ревью уже после реализации** (и починено там же): массовая перепроверка была
зарегистрирована **синхронной** перегрузкой моста и потому шла на UI-потоке — 20 аккаунтов морозили окно на
минуты и били по Roblox без пауз; теперь один вызов, работа в `Task.Run`, пауза между аккаунтами, сохранение
и уведомление один раз. Проверки на зарезервированное «All» были односторонние (запрещали переименовать
*из* «All», но не *в* него) — теперь на обоих концах и во всех местах, куда имя приходит от пользователя.
`GroupOrder` мог получить дубль при слиянии групп — добавлен `Distinct()`. И чипы групп рисовались после
кнопки «+», хотя должны перед ней.

**Presence.** Опрашивались только строки, попавшие в пиксельный хит-тест старого окна: всё ниже сгиба (и всё
подряд, когда окно свёрнуто) навсегда оставалось «Offline», а свёрнутое окно раз в две минуты слало пустой
запрос. Теперь спрашиваются все аккаунты пачками по 100 с паузой; `PresenceUpdateRate` наконец читается
(минуты — как подписано в старом окне), а `Presence.UpdatePresence` отбивает пустой список.
Быстрый поллер Nexus не трогали: он решает про перезапуск, и устаревшие данные там стоят дороже.

---

### M. Паритет лаунчера в новом UI — ✅ РЕАЛИЗОВАНО 2026-08-07

Было: запуск пачкой из HTML-окна шёл голым циклом со страницы — без пауз, без отмены и без подстановки
`SavedPlaceId`/`SavedJobId`, хотя экран настроек показывал `AccountJoinDelay` и `AsyncJoin` как рабочие.

Стало: `Classes/BatchLauncher.cs` — пачку ведёт приложение, страница только запускает и слушает события
`launch.progress` / `launch.batch`. Методы моста `accounts.launchBatch` / `cancelLaunch` / `launchState`.
Одиночный запуск как был, прямым вызовом. Кнопка запуска на время пачки превращается в «Стоп»;
`Batch.sync()` спрашивает состояние при старте страницы, потому что пачка переживает перезагрузку страницы
(смена языка её как раз перезагружает).

**Ключевое решение, принятое до реализации (2026-08-07):** пауза между запусками должна считаться **на точку
выхода, а не глобально**. Смысл `AccountJoinDelay` — не злить Roblox запросами тикетов с одного адреса; у
аккаунтов с разными прокси адреса разные, и заставлять их ждать друг друга бессмысленно (10 аккаунтов на 10
прокси должны стартовать практически одновременно). Правило: аккаунты без прокси делят одну очередь
(прямой IP), каждый уникальный прокси — своя очередь со своим таймером. Ключ очереди — `ProxyConfig.Raw`
(он же ключ липкой сессии, см. [[roblox-launch-mechanics]]), пустой — для прямого выхода. Тот же принцип
относится к `RelaunchMaxPerHour` из раздела K: потолок «в час» тоже осмысленно считать на выход, иначе один
прокси-пул упирается в лимит, рассчитанный на один адрес.

---

## 5. Рекомендуемый порядок

На 2026-08-08 H–O и G-новый UI — ранее реализованные разделы. Для оставшихся A–G код подготовлен,
но до зелёной Windows-сборки и указанных live smoke-тестов они **не считаются закрытыми**.

Следующий этап — не ещё один пункт этого списка, а Windows/live smoke: закрепить одну скачанную версию,
проверить Anti-AFK на реальном клиенте и выполнить dry-run очистки. Android-эмуляторы ведутся отдельным планом
и в этот документ/ветку не входят.

## 6. Чего не делать

- Спуф MachineGuid / MAC (см. блок в разделе B).
- ModernUI на WebView2 из форка KingsRAM — в форке недоделан, тянет рантайм-зависимость.
  Если нужен новый UI — это отдельное решение, а не побочный эффект переноса фич.
- Переименовывать файлы данных (`AccountData.json`, `RAMSettings.ini`, `RAMTheme.ini`) —
  совместимость со стоковым RAM держится на них.
- Трогать `Cryptography.RAMHeader` — это байты формата зашифрованного хранилища.
