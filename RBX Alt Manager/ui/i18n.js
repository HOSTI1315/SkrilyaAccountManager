// Interface language.
//
// The dictionary is keyed by the ENGLISH source text rather than by invented ids. That is deliberate: the page
// is assembled from a designer's export that is re-imported regularly, and any id we sprinkled into that markup
// would be lost on the next import. English text is what the export actually contains, so a translation keyed by
// it survives re-imports untouched — and anything not translated yet simply stays in English instead of showing
// a raw key.
//
// Two ways in:
//   t('Refresh')                       for text app.js produces
//   I18N.apply(root)                   walks the DOM and translates the design's own static text
//
// apply() only ever replaces exact dictionary matches, so account names, game names and other user data can
// never be hit by it. Running it twice is harmless: Russian text matches no English key.

const RU = {
  // ---------------------------------------------------------------- rail and chrome
  'Dashboard': 'Сводка',
  'Accounts': 'Аккаунты',
  'Launcher': 'Запуск',
  'Relauncher': 'Перезапуск',
  'Watcher': 'Наблюдение',
  'Free items': 'Бесплатные предметы',
  'Macros': 'Макросы',
  'Server list': 'Список серверов',
  'Proxies': 'Прокси',
  'Resources': 'Ресурсы',
  'Settings': 'Настройки',
  'New group': 'Новая группа',
  'Interface language': 'Язык интерфейса',
  'ACCOUNT MANAGER': 'МЕНЕДЖЕР АККАУНТОВ',
  'Search anything — accounts, actions, settings…': 'Поиск: аккаунты, действия, настройки…',
  'Drag to reorder · right-click for options': 'Перетащите, чтобы изменить порядок · правый клик — меню',

  // ---------------------------------------------------------------- accounts screen
  'Manage, filter and launch your accounts': 'Управление, фильтры и запуск аккаунтов',
  'Refresh': 'Обновить',
  'Add account': 'Добавить аккаунт',
  'All': 'Все',
  'Valid': 'Рабочие',
  'Invalid': 'Нерабочие',
  'Banned': 'Забаненные',
  'Clear': 'Снять',
  'Group': 'Группа',
  'manual order': 'ручной порядок',
  'Default': 'По умолчанию',
  'Search accounts...': 'Поиск по аккаунтам...',
  'Search accounts…': 'Поиск по аккаунтам…',
  'Select an account to launch': 'Выберите аккаунт для запуска',
  '{valid} / {total} valid': '{valid} / {total} рабочих',
  'Launches {n} selected': 'Запустит выбранных: {n}',
  '{n} shown': 'показано: {n}',
  'Launching {name}…': 'Запускаю {name}…',
  '{name}: client started': '{name}: клиент запущен',
  'Launching {n} accounts, {delay}s apart on each exit': 'Запускаю {n} аккаунтов, по {delay} с на каждой точке выхода',
  'Launching {n} accounts': 'Запускаю {n} аккаунтов',
  '{done} of {total} launched': 'запущено {done} из {total}',
  'Stopping after the current launch…': 'Останавливаю после текущего запуска…',
  'launch failed': 'запуск не удался',

  // groups and bulk actions
  '{n} accounts selected': 'выбрано аккаунтов: {n}',
  'Show only this group': 'Показать только эту группу',
  'New accounts go here': 'Новые аккаунты — сюда',
  'Rename…': 'Переименовать…',
  'Delete…': 'Удалить…',
  'Rename {name} to': 'Переименовать {name} в',
  'New accounts will go to {name}': 'Новые аккаунты будут попадать в {name}',
  '{n} accounts moved to {name}': 'Перенесено аккаунтов в {name}: {n}',
  '{n} accounts moved to Default': 'Перенесено аккаунтов в Default: {n}',
  'Delete {name}?\n\nIts {n} accounts are moved to Default. Nothing is deleted.':
    'Удалить {name}?\n\nЕё аккаунты ({n}) перейдут в Default. Ничего не удаляется.',
  'Name for the new group': 'Название новой группы',
  'Tick the accounts for the new group first — a group with nobody in it is not stored':
    'Сначала отметьте аккаунты для новой группы — пустая группа не сохраняется',
  'Copied the {what}': 'Скопировано: {what}',
  'Copied the {what} of {n} accounts': 'Скопировано ({what}) у аккаунтов: {n}',
  '{name} has no {what} stored': 'у {name} не сохранено: {what}',
  'Checking {name}…': 'Проверяю {name}…',
  'Checking {n} accounts…': 'Проверяю аккаунтов: {n}…',
  '{good} of {n} still work': 'рабочих: {good} из {n}',
  'Remove {n} accounts from the manager?': 'Убрать из менеджера аккаунтов: {n}?',
  'Remove {name} from the manager?': 'Убрать {name} из менеджера?',
  '{n} accounts removed': 'Убрано аккаунтов: {n}',
  '{name} removed': '{name} убран',
  'Set {what} for {n} accounts': 'Задать {what} для аккаунтов: {n}',
  'Set {what} for {name}': 'Задать {what} для {name}',
  '{name}: group set': '{name}: группа задана',
  '{name}: {what} set': '{name}: {what} задан',
  '{n} accounts given a proxy': 'Прокси назначен аккаунтам: {n}',
  'Proxy removed from {n} accounts': 'Прокси снят с аккаунтов: {n}',
  'Stopped: {started} of {total} launched': 'Остановлено: запущено {started} из {total}',
  '{started} launched, {failed} failed': 'Запущено: {started}, с ошибкой: {failed}',
  '{n} selected': 'выбрано: {n}',
  'Expired': 'Истёк',
  'In game': 'В игре',
  'Online': 'В сети',
  'Offline': 'Не в сети',
  'Signed out': 'Вышел',
  'Unknown': 'Неизвестно',

  // ---------------------------------------------------------------- launcher panel
  'Place ID': 'ID места',
  'Place ID or Roblox link': 'ID места или ссылка Roblox',
  'Job ID (optional)': 'Job ID (необязательно)',
  'Job ID': 'Job ID',
  'VIP link / code (optional)': 'VIP-ссылка / код (необязательно)',
  'VIP link / code': 'VIP-ссылка / код',
  'Join server': 'Зайти на сервер',
  'Favorites': 'В избранном',
  'Likes': 'Лайки',

  // ---------------------------------------------------------------- server list
  'Pick a server, or drop in a private link — the launch uses the accounts you ticked':
    'Выберите сервер или вставьте приватную ссылку — запустятся отмеченные аккаунты',
  'Private link': 'Приватная ссылка',
  'Favourites': 'Избранное',
  'Recent': 'Недавние',
  'Servers': 'Серверы',
  'Has room': 'Есть места',
  'Low ping': 'Низкий пинг',
  'Search games or place ID...': 'Поиск игр или ID места...',
  'Search games or place ID…': 'Поиск игр или ID места…',
  'Join': 'Зайти',
  'no game': 'игра не выбрана',
  'loading…': 'загрузка…',
  'Nothing picked yet — type a place id or a Roblox link in the box on the left, or choose a game under it.':
    'Пока ничего не выбрано — введите ID места или ссылку Roblox слева, либо выберите игру ниже.',
  'Loading the servers of this place…': 'Загружаю серверы этого места…',

  // ---------------------------------------------------------------- proxies
  'Assign proxies to accounts and keep an eye on which ones still answer':
    'Назначайте прокси аккаунтам и следите, какие ещё отвечают',
  'Check all': 'Проверить все',
  'Add proxies': 'Добавить прокси',
  'Proxy list': 'Список прокси',
  'Proxy list — one per line': 'Список прокси — по одному в строке',
  'Bulk assign': 'Массовое назначение',
  'Assign': 'Назначить',
  'Handed out round-robin.': 'Раздаются по кругу.',
  'Proxy': 'Прокси',
  'Type': 'Тип',
  'Used': 'Исп.',
  'Status': 'Статус',
  'Checked': 'Проверен',
  'Alive': 'Живой',
  'Dead': 'Мёртвый',
  'Untested': 'Не проверен',
  'Checking': 'Проверяю',
  'just now': 'только что',
  'exit address unknown': 'адрес выхода неизвестен',

  // ---------------------------------------------------------------- add account
  'Cookies are stored encrypted with your Windows account.':
    'Куки хранятся зашифрованными вашей учётной записью Windows.',
  'Single cookie': 'Одна кука',
  'Cookie list': 'Список кук',
  'Sign in': 'Вход',
  'Cancel': 'Отмена',
  'Add to group': 'В группу',
  'Open the sign-in browser': 'Открыть браузер для входа',

  // ---------------------------------------------------------------- row menu
  'Launch': 'Запустить',
  'Launch here': 'Запустить здесь',
  'Open profile': 'Открыть профиль',
  'Refresh this one': 'Обновить этот',
  'Revalidate': 'Перепроверить',
  'Remove': 'Удалить',
  'Copy': 'Копировать',
  'Set': 'Изменить',
  'Tools': 'Инструменты',
  'Move to group': 'Переместить в группу',
  'Username': 'Логин',
  'User ID': 'ID пользователя',
  'Alias': 'Псевдоним',
  'Description': 'Описание',

  // ---------------------------------------------------------------- settings screen
  'Sections': 'Разделы',
  'Launch, client windows, proxies, watching, web server and appearance':
    'Запуск, окна клиентов, прокси, наблюдение, веб-сервер и оформление',
  'Client launch': 'Запуск клиента',
  'Client windows': 'Окна клиентов',
  'Relaunching': 'Перезапуск',
  'Dead clients': 'Мёртвые клиенты',
  'Web server': 'Веб-сервер',
  'Interface': 'Интерфейс',
  'Watching': 'Наблюдение',
  'Appearance': 'Оформление',
  '{key} saved': '{key} сохранено',

  // setting labels (app.js SETTINGS table)
  'Multi Roblox': 'Мульти-Roblox',
  'Allow more than one Roblox client at a time. Roblox itself refuses a second client, so the manager holds the lock it checks. Turn it on before Roblox is open, not after.':
    'Разрешить больше одного клиента Roblox одновременно. Сам Roblox второй клиент не пускает, поэтому менеджер держит замок, который тот проверяет. Включать до запуска Roblox, а не после.',
  'Saved, but Roblox is already running: close every client and restart the manager for multi Roblox to take effect':
    'Сохранено, но Roblox уже запущен: закройте все клиенты и перезапустите менеджер, чтобы мульти-Roblox заработал',
  'Direct launch': 'Прямой запуск',
  'Delay between launches': 'Пауза между запусками',
  'Launch all at once': 'Запускать без пауз',
  'Seconds between launches when several accounts start at once. Counted per exit address, so accounts on different proxies do not wait for each other.':
    'Секунд между запусками, когда стартует сразу несколько аккаунтов. Считается на каждую точку выхода: аккаунты на разных прокси друг друга не ждут.',
  'Start the next client as soon as the previous one\'s process is up, instead of waiting the delay. Faster, and far more likely to be rate limited.':
    'Запускать следующий клиент сразу, как поднялся процесс предыдущего, не выжидая паузу. Быстрее и заметно вероятнее упереться в лимит Roblox.',
  'Close a running client first': 'Сначала закрыть запущенный клиент',
  'Watch how each launch ends': 'Следить, чем закончился запуск',
  'Name the windows': 'Именовать окна',
  'Title template': 'Шаблон заголовка',
  'Launch off screen': 'Запускать вне экрана',
  'Desktop name': 'Имя рабочего стола',
  'Close error dialogs': 'Закрывать окна ошибок',
  'Use the proxy on each account': 'Использовать прокси аккаунта',
  'Split tunnelling': 'Раздельный туннель',
  'Check the exit address first': 'Сначала проверять адрес выхода',
  'Hosts that must go through it': 'Хосты, которые идут через прокси',
  'Put accounts back': 'Возвращать аккаунты в игру',
  'After a crash': 'После краха',
  'After a stuck client': 'После зависшего клиента',
  'Cooldown per account': 'Пауза на аккаунт',
  'Ceiling per hour': 'Потолок в час',
  'Give up after': 'Сдаться после',
  'Close crashed clients': 'Закрывать упавшие клиенты',
  'Report only': 'Только отчёт',
  'Grace period': 'Льготный период',
  'Sweep every': 'Проверять каждые',
  'Clean up crash handlers': 'Убирать crash handler’ы',
  'Never touch these process ids': 'Никогда не трогать эти PID',
  'Read the clients’ own logs': 'Читать логи самих клиентов',
  'Close refused clients': 'Закрывать отклонённые клиенты',
  'Close clients that never joined': 'Закрывать так и не зашедшие клиенты',
  'Still starting until': 'Считать запускающимся до',
  'Counts as stuck after': 'Считать зависшим после',
  'Classify every': 'Классифицировать каждые',
  'Port': 'Порт',
  'Password': 'Пароль',
  'Every request needs it': 'Требовать для каждого запроса',
  'Accept other machines': 'Принимать другие машины',
  'May list accounts': 'Может получать список аккаунтов',
  'May read cookies': 'Может читать куки',
  'May launch accounts': 'Может запускать аккаунты',
  'May edit accounts': 'Может изменять аккаунты',
  'Show what accounts are doing': 'Показывать, чем заняты аккаунты',
  'Check presence every': 'Проверять присутствие каждые',
  'Minutes between checks of what each account is doing. Every account is asked about, in batches, not just the rows on screen.':
    'Минут между проверками того, чем занят каждый аккаунт. Спрашиваются все аккаунты пачками, а не только строки на экране.',
  'Keep cookies fresh': 'Обновлять куки',
  'Store passwords': 'Хранить пароли',
  'New accounts go to': 'Новые аккаунты — в группу',
  'Recent games kept': 'Хранить недавних игр',
  'Hide the ageing warning': 'Скрыть предупреждение о возрасте',

  // setting descriptions (the comments beside each key in RAMSettings.ini)
  'Start RobloxPlayerBeta.exe directly instead of through the roblox-player: protocol. Required for per-account proxies, window titles and launch tracking.':
    'Запускать RobloxPlayerBeta.exe напрямую, а не через протокол roblox-player:. Нужно для прокси на аккаунт, заголовков окон и отслеживания запуска.',
  'Read each launched client\'s log to record whether it actually joined, disconnected, or died with a rejected ticket.':
    'Читать лог каждого запущенного клиента, чтобы знать: зашёл, отключился или умер с отклонённым тикетом.',
  'Rename each client\'s window after the account running in it.':
    'Переименовывать окно каждого клиента по имени аккаунта в нём.',
  'Window title template. Placeholders: {name} {username} {alias} {userid} {place} {pid}':
    'Шаблон заголовка окна. Подстановки: {name} {username} {alias} {userid} {place} {pid}',
  'Launch clients on a separate named desktop: their windows are not on screen at all. Window positioning and window titles cannot be seen there.':
    'Запускать клиенты на отдельном рабочем столе: их окон не видно совсем. Расстановку окон и заголовки там не увидеть.',
  'Name of the desktop used by UseSeparateDesktop.': 'Имя рабочего стола для UseSeparateDesktop.',
  'Automatically close Roblox\'s Win32 error message boxes (the modals left behind by a failed launch).':
    'Автоматически закрывать окна ошибок Roblox (те, что остаются после неудачного запуска).',
  'Route an account\'s login AND its client through the proxy in that account\'s Proxy field. Accepts host:port, host:port:user:pass, user:pass@host:port or scheme://... (http, socks4, socks5).':
    'Вести и вход, и сам клиент через прокси из поля Proxy аккаунта. Форматы: host:port, host:port:user:pass, user:pass@host:port или scheme://... (http, socks4, socks5).',
  'Send only identity traffic (login/join) through the proxy and fetch game assets directly. Much faster and far less proxy traffic; the exit IP that matters is still the proxy\'s.':
    'Через прокси идёт только трафик входа и захода в игру, ресурсы игры качаются напрямую. Заметно быстрее и сильно меньше трафика; важный адрес выхода всё равно прокси.',
  'Check the proxy\'s exit IP before spending an authentication ticket on it. Catches dead proxies early and warns when a proxy rotates (a rotating IP makes Roblox reject the ticket with 403).':
    'Проверять адрес выхода прокси до того, как на него потратят тикет. Ловит мёртвые прокси заранее и предупреждает о ротации (меняющийся IP — и Roblox отклоняет тикет с 403).',
  'Hosts that must go through the proxy when split tunnelling. Subdomains included automatically.':
    'Хосты, которые при раздельном туннеле обязаны идти через прокси. Поддомены учитываются автоматически.',
  'Put an account back where it was when its client dies. Uses the launch watcher and the stuck detector, so it does not need Nexus.':
    'Возвращать аккаунт туда, где он был, если его клиент умер. Работает на наблюдателе запуска и детекторе зависаний, Nexus не нужен.',
  'Relaunch when a client died before joining or dropped straight out.':
    'Перезапускать, если клиент умер до захода или сразу вылетел.',
  'Also relaunch clients the detector calls stuck or refused. Off by default: those causes often repeat.':
    'Перезапускать и те клиенты, которые детектор счёл зависшими или отклонёнными. По умолчанию выключено: такие причины обычно повторяются.',
  'The same account is never relaunched more often than this.':
    'Один и тот же аккаунт не перезапускается чаще, чем раз в этот срок.',
  'Ceiling across all accounts. Stops a Roblox outage from turning into hundreds of login attempts from one address.':
    'Потолок на все аккаунты. Не даёт сбою Roblox превратиться в сотни попыток входа с одного адреса.',
  'Consecutive failures after which an account is left alone until you look at it.':
    'Сколько неудач подряд, после которых аккаунт оставляют в покое до вашего вмешательства.',
  'Kill Roblox clients that crashed but whose process is still running (no window, hundreds of MB held). Only touches real game clients that have been window-less past the grace period.':
    'Убивать клиенты, которые упали, но чей процесс ещё жив (окна нет, сотни МБ заняты). Трогает только настоящие игровые клиенты, пробывшие без окна дольше льготного периода.',
  'Report what the reaper would kill without killing anything. Use this for a day before trusting it.':
    'Показывать, что было бы убито, ничего не убивая. Подержите день, прежде чем доверять.',
  'How long a client may run without a window before it counts as dead. Lower it only if your machine boots clients fast.':
    'Сколько клиент может жить без окна, прежде чем счесть его мёртвым. Снижать только если клиенты стартуют быстро.',
  'Seconds between sweeps.': 'Секунд между проходами.',
  'Close RobloxCrashHandler processes left behind once no client is running.':
    'Закрывать оставшиеся процессы RobloxCrashHandler, когда не запущен ни один клиент.',
  'Process ids neither sweeper may ever touch, comma separated. 0 is a placeholder and matches nothing.':
    'PID, которые нельзя трогать ни одному чистильщику, через запятую. 0 — заглушка, ни с чем не совпадает.',
  'Read each running client\'s own log and classify it: joining, in game, refused by Roblox, or stuck without ever joining. Diagnostic unless StuckKill is on.':
    'Читать лог каждого живого клиента и определять: заходит, в игре, отклонён Roblox или завис так и не зайдя. Только диагностика, пока не включён StuckKill.',
  'Close clients Roblox refused a ticket to. They keep a window and a few hundred MB and will never join.':
    'Закрывать клиенты, которым Roblox отказал в тикете. Они держат окно и сотни МБ и никогда не зайдут.',
  'Close clients that never joined anything. Less certain than a refused ticket - a slow machine or a long queue looks the same.':
    'Закрывать клиенты, которые никуда не зашли. Менее надёжно, чем отказ в тикете: медленная машина или длинная очередь выглядят так же.',
  'Below this age a client that has not joined is simply still starting.':
    'Моложе этого возраста незашедший клиент считается просто запускающимся.',
  'Above this age, a client that never joined counts as stuck.':
    'Старше этого возраста так и не зашедший клиент считается зависшим.',
  'Seconds between classification passes.': 'Секунд между проходами классификации.',
  'The group new accounts are put in when none is picked while adding them.':
    'Группа, в которую попадают новые аккаунты, если при добавлении не выбрана другая.',

  // ---------------------------------------------------------------- free items
  'Buys every zero-price catalog item on the accounts you ticked. No Robux is ever spent.':
    'Скупает все бесплатные предметы каталога на отмеченных аккаунтах. Robux не тратится никогда.',
  'Collect': 'Собрать',
  'Stop': 'Стоп',
  'Progress': 'Прогресс',

  // ---------------------------------------------------------------- security
  'Protect your accounts': 'Защита аккаунтов',
  'Choose how the account file on this machine is encrypted.':
    'Выберите, как шифруется файл аккаунтов на этой машине.',
  'This Windows account only': 'Только эта учётная запись Windows',
  'No password. Readable only by you, on this PC.': 'Без пароля. Читается только вами и только на этом ПК.',
  'Asked for on every start. Recommended.': 'Спрашивается при каждом запуске. Рекомендуется.',
  'Unlock your accounts': 'Разблокируйте аккаунты',
  'This account file is password-locked.': 'Этот файл аккаунтов защищён паролем.',
  'Set a password': 'Задайте пароль',
  'It will be asked for every time the manager starts. There is no way to recover it.':
    'Он будет спрашиваться при каждом запуске. Восстановить его нельзя.',
  'Repeat the password': 'Повторите пароль',
  'Unlock': 'Разблокировать',
  'Save': 'Сохранить',
  'Those passwords do not match.': 'Пароли не совпадают.',

  // ---------------------------------------------------------------- language picker
  'Language': 'Язык',
  'English': 'English',
  'Русский': 'Русский',
  'Applies immediately — no restart needed.': 'Применяется сразу — перезапуск не нужен.'
};

// One account's state, which needs its own entries: "Valid" is a filter pill reading "все рабочие аккаунты" but
// a row status reading "этот аккаунт рабочий", and Russian will not use the same word for both.
const RU_STATUS = {
  'Valid': 'Рабочий',
  'Expired': 'Истёк',
  'In game': 'В игре',
  'Online': 'В сети'
};

const I18N = {
  lang: 'en',

  dictionaries: { ru: RU },
  statusDictionaries: { ru: RU_STATUS },

  /// The same English word can need different translations in different places; this is the row-status one.
  status(text) {
    const dict = this.statusDictionaries[this.lang];

    return (dict && dict[text]) || this.t(text);
  },

  get dict() { return this.dictionaries[this.lang] || null; },

  /// <summary>Translates one string, or returns it unchanged when there is no translation for it.</summary>
  t(text) {
    if (text == null) return text;

    const dict = this.dict;

    return (dict && dict[text]) || text;
  },

  /// Translates then fills {name} placeholders, so a translation may reorder them.
  fmt(text, values) {
    return String(this.t(text)).replace(/\{(\w+)\}/g, (whole, key) =>
      values && key in values ? values[key] : whole);
  },

  /// Walks the design's own markup and translates it in place. Exact matches only, so user data is never hit.
  apply(root) {
    if (!this.dict) return;

    const scope = root || document.body;
    if (!scope) return;

    const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
    const found = [];

    while (walker.nextNode()) found.push(walker.currentNode);

    found.forEach(node => {
      const raw = node.textContent;
      const trimmed = raw.trim();

      if (!trimmed) return;

      const hit = this.dict[trimmed];

      if (hit) node.textContent = raw.replace(trimmed, hit);
    });

    // The parts of the interface that are attributes rather than text.
    scope.querySelectorAll('[placeholder]').forEach(node => {
      const hit = this.dict[node.getAttribute('placeholder')];

      if (hit) node.setAttribute('placeholder', hit);
    });

    scope.querySelectorAll('[title]').forEach(node => {
      const hit = this.dict[node.getAttribute('title')];

      if (hit) node.setAttribute('title', hit);
    });
  }
};

const t = text => I18N.t(text);
