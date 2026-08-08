// Drives the Nocturne interface from the manager's data.
//
// The design is authored elsewhere and re-exported from time to time, so this file never depends on its class
// names or structure — only on the data-ram hooks listed in README.md and on the design's own tokens for the
// two states a row can be in. Re-import the design, re-apply the hooks, and this keeps working.

const Style = {
  // Selected vs idle, taken from the design's own sample rows.
  rowOn:   { borderColor: 'var(--color-accent-700)',  background: 'color-mix(in srgb, var(--color-accent-900) 86%, transparent)' },
  rowOff:  { borderColor: 'var(--color-neutral-800)', background: 'color-mix(in srgb, var(--color-bg) 46%, transparent)' },
  checkOn: { borderColor: 'var(--color-accent-500)',  background: 'var(--color-accent-700)' },
  checkOff:{ borderColor: 'var(--color-neutral-700)', background: 'transparent' },
  chipOn:  { borderColor: 'var(--color-accent-700)',  background: 'var(--color-accent-900)', color: 'var(--color-accent-200)' },
  chipOff: { borderColor: 'transparent',              background: 'transparent',             color: 'var(--color-neutral-500)' },
  dotOk:   'oklch(0.72 0.085 152)',
  dotBad:  'oklch(0.66 0.13 26)',
  dotIdle: 'var(--color-neutral-600)'
};

const State = { accounts: [], groups: [], selected: new Set(), search: '', status: 'all', group: 'All' };

const hook = name => document.querySelector(`[data-ram="${name}"]`);
const hooks = name => Array.from(document.querySelectorAll(`[data-ram="${name}"]`));
const apply = (node, style) => { if (node) Object.assign(node.style, style); };

// ---------------------------------------------------------------- model helpers

function statusOf(account) {
  if (!account.valid) return { label: 'Expired', dot: Style.dotBad };
  if (account.presence === 'InGame') return { label: 'In game', dot: Style.dotOk };
  if (account.presence === 'Online' || account.presence === 'InStudio') return { label: 'Online', dot: Style.dotOk };
  return { label: 'Valid', dot: Style.dotOk };
}

function presenceOf(account) {
  if (!account.valid) return 'Signed out';
  if (account.presence === 'InGame') return account.location || 'In game';
  if (account.presence === 'Online') return 'Online';
  return 'Offline';
}

function visible() {
  const q = State.search.trim().toLowerCase();

  return State.accounts.filter(a => {
    if (State.status === 'valid' && !a.valid) return false;
    if (State.status === 'invalid' && a.valid) return false;
    if (State.status === 'banned') return false;             // the app has no banned flag yet
    if (State.group !== 'All' && a.group !== State.group) return false;
    if (!q) return true;

    return (a.username || '').toLowerCase().includes(q)
        || (a.alias || '').toLowerCase().includes(q)
        || String(a.userId).startsWith(q);
  });
}

// ---------------------------------------------------------------- rendering

function renderRow(account) {
  const template = document.querySelector('[data-ram="account-row"]');
  if (!template) return null;

  const row = template.content.firstElementChild.cloneNode(true);
  const field = name => row.querySelector(`[data-ram-field="${name}"]`);
  const set = (name, value) => { const n = field(name); if (n) n.textContent = value == null ? '' : value; };

  const selected = State.selected.has(account.username);
  const status = statusOf(account);

  set('username', account.alias || account.username);
  set('userId', account.userId);
  set('presence', presenceOf(account));
  set('avatar', (account.alias || account.username || '?').charAt(0).toUpperCase());
  set('robux', account.robux == null ? 'R$ —' : 'R$ ' + account.robux);

  // The label sits next to the dot. Search from the end and ignore whitespace: the markup is pretty-printed,
  // so the first child of that row is an empty text node, and writing into it puts the label before the dot.
  const statusNode = field('status');
  if (statusNode) {
    const label = statusNode.querySelector('.sc-interp')
      || Array.from(statusNode.childNodes).reverse().find(n => n.nodeType === 3 && n.textContent.trim())
      || statusNode.lastElementChild;

    // status(), not t(): a row says "this account is working", a filter pill says "all working accounts", and
    // those are different words in most languages.
    if (label) label.textContent = I18N.status(status.label);
  }

  apply(field('dot'), { background: status.dot });

  const proxy = field('proxy');
  if (proxy) {
    proxy.style.display = account.proxy ? '' : 'none';
    proxy.textContent = account.proxy ? 'proxy' : '';
    if (account.proxy) proxy.title = account.proxy + (account.exitIP ? ' · ' + account.exitIP : '');
  }

  apply(row, selected ? Style.rowOn : Style.rowOff);
  apply(field('check'), selected ? Style.checkOn : Style.checkOff);

  const checkIcon = field('check-icon');
  if (checkIcon) checkIcon.style.visibility = selected ? 'visible' : 'hidden';

  row.dataset.username = account.username;

  row.addEventListener('click', e => {
    if (!(e.ctrlKey || e.metaKey || e.target.closest('[data-ram-field="check"]'))) State.selected.clear();

    State.selected.has(account.username) ? State.selected.delete(account.username) : State.selected.add(account.username);

    renderAccounts();
  });

  row.addEventListener('dblclick', () => launch([account.username]));

  return row;
}

function renderAccounts() {
  const list = hook('accounts');
  if (!list) return;

  list.querySelectorAll('[data-ram-row]').forEach(n => n.remove());

  const rows = visible();

  rows.forEach(account => { const row = renderRow(account); if (row) list.appendChild(row); });

  // These carry numbers, so they go through fmt(): a translation may put the count somewhere else in the phrase.
  const count = hook('account-count');
  if (count) count.textContent = I18N.fmt('{valid} / {total} valid', {
    valid: State.accounts.filter(a => a.valid).length,
    total: State.accounts.length
  });

  const n = State.selected.size;

  // While a batch runs, the hint belongs to it — see Batch.paint.
  const hint = hook('launch-hint');
  if (hint && !Batch.running) hint.textContent = n === 0 ? t('Select an account to launch') : I18N.fmt('Launches {n} selected', { n });

  const selected = hook('selected-count');
  if (selected) selected.textContent = n === 0 ? I18N.fmt('{n} shown', { n: rows.length }) : I18N.fmt('{n} selected', { n });

  I18N.apply(list);
}

/// Roblox-style short numbers: the design's stat row is narrow and 828900 would wrap it.
function compact(value) {
  if (value == null) return '—';
  if (value >= 1e6) return (value / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
  if (value >= 1e3) return (value / 1e3).toFixed(1).replace(/\.0$/, '') + 'K';

  return String(value);
}

// Groups arrive in the order the app decided: the ones dragged into place first, then the rest by name — which
// is where a "001 " prefix still does its work. Each carries the stored name and the name to show, because the
// prefix is hidden but the accounts still hold it.
function renderGroups(groups) {
  const container = hook('group-chips');
  const template = document.querySelector('[data-ram="group-chip"]');

  State.groups = groups || [];

  if (!container || !template) return;

  container.querySelectorAll('[data-ram-group-chip]').forEach(n => n.remove());

  State.groups.forEach(group => {
    const chip = template.content.firstElementChild.cloneNode(true);

    const label = Array.from(chip.childNodes).find(n => n.nodeType === 3 && n.textContent.trim())
      || chip.querySelector('span') || chip;

    label.textContent = group.title;
    chip.dataset.ramGroup = group.name;
    chip.dataset.ramGroupChip = '';
    chip.title = `${group.accounts} account${group.accounts === 1 ? '' : 's'} — drag to reorder`;
    chip.draggable = true;

    apply(chip, State.group === group.name ? Style.chipOn : Style.chipOff);

    chip.addEventListener('click', () => { State.group = group.name; syncChips(); renderAccounts(); });

    // Dragging a chip is the whole ordering feature: the app persists whatever order the chips end up in.
    chip.addEventListener('dragstart', e => {
      e.dataTransfer.setData('text/plain', group.name);
      e.dataTransfer.effectAllowed = 'move';
      chip.style.opacity = '0.4';
    });

    chip.addEventListener('dragend', () => { chip.style.opacity = ''; });

    chip.addEventListener('dragover', e => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; });

    chip.addEventListener('drop', async e => {
      e.preventDefault();

      const moved = e.dataTransfer.getData('text/plain');
      if (!moved || moved === group.name) return;

      const order = State.groups.map(g => g.name).filter(name => name !== moved);
      const at = order.indexOf(group.name);

      order.splice(at < 0 ? order.length : at, 0, moved);

      try {
        await RAM.call('groups.reorder', { order });
        await refresh();
      } catch (err) { toast(err.message, 'error'); }
    });

    // The design's own chip promises this in its tooltip ("right-click for options") but shipped no menu.
    chip.addEventListener('contextmenu', event => {
      event.preventDefault();
      event.stopPropagation();

      GroupMenu.open(group, event.clientX, event.clientY);
    });

    // Before the "+" button and the "manual order" caption, not after them: appending put every real group at
    // the end of the strip, past the controls that belong at its end.
    const tail = hook('group-new');

    if (tail && tail.parentElement === container) container.insertBefore(chip, tail);
    else container.appendChild(chip);
  });

  syncChips();
}

// Renaming and deleting a group. A group is only a label carried by its accounts, so both are really bulk edits
// of those accounts — which is why the counts are worth showing before doing it.
const GroupMenu = {
  close() {
    const open = document.querySelector('[data-ram="group-menu"]');

    if (open) open.remove();
  },

  open(group, x, y) {
    this.close();

    if (group.name === 'All') return;

    const menu = document.createElement('div');

    menu.setAttribute('data-ram', 'group-menu');
    menu.style.cssText = `position:fixed;z-index:80;left:${Math.round(x)}px;top:${Math.round(y)}px;min-width:200px;padding:6px;
      border-radius:var(--radius-lg);background:color-mix(in srgb, var(--color-surface) 94%, transparent);
      backdrop-filter:blur(20px) saturate(112%);border:1px solid var(--color-neutral-700);box-shadow:rgb(0,0,0) 0 24px 54px -24px`;

    const header = document.createElement('div');

    header.style.cssText = 'padding:7px 9px 6px;font-size:9px;font-weight:500;letter-spacing:.16em;text-transform:uppercase;color:var(--color-neutral-600);white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
    header.textContent = group.title;

    menu.appendChild(header);

    const item = (icon, label, danger, run) => {
      const row = document.createElement('div');

      row.style.cssText = `display:flex;align-items:center;gap:10px;padding:6px 9px;border-radius:var(--radius-md);cursor:pointer;
        font-size:12.5px;color:var(${danger ? '--color-danger, oklch(0.66 0.13 26)' : '--color-neutral-200'})`;

      row.innerHTML = `<i class="ph ${icon}" style="flex:0 0 auto;font-size:14px;opacity:.85"></i><span>${label}</span>`;

      row.addEventListener('mouseenter', () => row.style.background = 'color-mix(in srgb, var(--color-accent-900) 60%, transparent)');
      row.addEventListener('mouseleave', () => row.style.background = 'transparent');
      row.addEventListener('click', () => { this.close(); run(); });

      menu.appendChild(row);
    };

    item('ph-funnel', t('Show only this group'), false, () => {
      State.group = group.name;
      syncChips();
      renderAccounts();
    });

    item('ph-star', t('New accounts go here'), false, async () => {
      try {
        await RAM.call('groups.defaultGroup', { name: group.name });
        toast(I18N.fmt('New accounts will go to {name}', { name: group.title }), 'ok');
        await refresh();
      } catch (error) { toast(String(error && error.message || error), 'error'); }
    });

    item('ph-pencil-simple', t('Rename…'), false, async () => {
      const name = await Prompt.ask(I18N.fmt('Rename {name} to', { name: group.title }), group.name);

      if (name === null || !name.trim() || name.trim() === group.name) return;

      try {
        const result = await RAM.call('groups.rename', { from: group.name, to: name.trim() });

        toast(I18N.fmt('{n} accounts moved to {name}', { n: result.renamed, name: result.to }), 'ok');

        if (State.group === group.name) State.group = result.to;

        await refresh();
      } catch (error) { toast(String(error && error.message || error), 'error'); }
    });

    item('ph-trash', t('Delete…'), true, async () => {
      // Deleting cannot delete the accounts, so say where they go before doing it.
      if (!confirm(I18N.fmt('Delete {name}?\n\nIts {n} accounts are moved to Default. Nothing is deleted.',
        { name: group.title, n: group.accounts }))) return;

      try {
        const result = await RAM.call('groups.delete', { name: group.name, into: 'Default' });

        toast(I18N.fmt('{n} accounts moved to Default', { n: result.moved }), 'ok');

        if (State.group === group.name) State.group = 'All';

        await refresh();
      } catch (error) { toast(String(error && error.message || error), 'error'); }
    });

    document.body.appendChild(menu);

    // keep it on screen when the chip is near an edge
    const box = menu.getBoundingClientRect();

    if (box.right > window.innerWidth - 8) menu.style.left = `${Math.round(window.innerWidth - box.width - 8)}px`;
    if (box.bottom > window.innerHeight - 8) menu.style.top = `${Math.round(window.innerHeight - box.height - 8)}px`;
  },

  /// The "+" chip beside the groups. A group with no accounts does not exist in the store, so making one means
  /// putting the picked accounts in it — anything else would vanish on the next refresh.
  wire() {
    document.addEventListener('click', event => {
      if (!event.target.closest('[data-ram="group-menu"]')) this.close();
    });

    const add = document.querySelector('[title="New group"]');

    if (!add) return;

    // Stamped here, while the title is still English: apply() translates titles, so a later lookup by title
    // would miss. renderGroups uses the hook to keep the real chips before this button.
    add.setAttribute('data-ram', 'group-new');
    add.style.cursor = 'pointer';

    add.addEventListener('click', async () => {
      const accounts = Array.from(State.selected);

      if (!accounts.length)
        return toast(t('Tick the accounts for the new group first — a group with nobody in it is not stored'), 'error');

      const name = await Prompt.ask(t('Name for the new group'), '');

      if (name === null || !name.trim()) return;

      try {
        const result = await RAM.call('accounts.setGroup', { accounts, group: name.trim() });

        toast(I18N.fmt('{n} accounts moved to {name}', { n: result.changed, name: result.group }), 'ok');

        await refresh();
      } catch (error) { toast(String(error && error.message || error), 'error'); }
    });
  }
};

function syncChips() {
  document.querySelectorAll('[data-ram-group]').forEach(chip =>
    apply(chip, chip.getAttribute('data-ram-group') === State.group ? Style.chipOn : Style.chipOff));

  document.querySelectorAll('[data-ram-filter]').forEach(chip =>
    apply(chip, chip.getAttribute('data-ram-filter') === State.status ? Style.chipOn : Style.chipOff));
}

let toastTimer = null;

function toast(message, kind = 'info') {
  const box = hook('toast');
  if (!box) return console.log('[toast]', kind, message);

  box.textContent = message;
  box.style.borderColor = kind === 'error' ? 'var(--color-danger, oklch(0.66 0.13 26))'
                        : kind === 'ok' ? 'oklch(0.72 0.085 152)'
                        : 'var(--color-neutral-800)';
  box.style.opacity = '1';
  box.style.transform = 'none';

  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.style.opacity = '0'; box.style.transform = 'translateY(8px)'; }, 6000);
}

// ---------------------------------------------------------------- actions

async function refresh() {
  try {
    State.accounts = (await RAM.call('accounts.list')) || [];

    const groups = (await RAM.call('accounts.groups')) || [];

    renderGroups(groups);
    renderAccounts();
  } catch (e) { toast('Could not load accounts: ' + e.message, 'error'); }
}

async function launch(usernames) {
  const place = hook('place-id');
  const placeId = place ? place.value.trim() : '';

  if (!placeId) return toast('Enter a place id first', 'error');

  const job = hook('job-id');
  const vip = hook('vip-code');
  const vipValue = vip ? vip.value.trim() : '';
  const jobId = vipValue || (job ? job.value.trim() : '');

  const targets = usernames && usernames.length ? usernames : Array.from(State.selected);

  if (!targets.length) return toast('Select an account first', 'error');

  // One account is still a single call: it answers straight away and there is nothing to pace.
  if (targets.length === 1) {
    const username = targets[0];

    try {
      toast(I18N.fmt('Launching {name}…', { name: username }));

      await RAM.call('accounts.launch', { username, placeId, jobId, joinVip: !!vipValue }, 180000);

      toast(I18N.fmt('{name}: client started', { name: username }), 'ok');
    } catch (e) { toast(`${username}: ${e.message}`, 'error'); }

    return;
  }

  // Several is a batch, and the app runs it: looping here meant no delay between launches at all, which is how
  // a dozen accounts ask Roblox for a dozen tickets from one address in a second and get rate limited.
  try {
    const result = await RAM.call('accounts.launchBatch', {
      accounts: targets, placeId, jobId, joinVip: !!vipValue
    });

    Batch.begin(result);
  } catch (e) { toast(e.message, 'error'); }
}

// Shows how a paced batch is going, and offers to stop it. The app owns the pacing; this only reports.
const Batch = {
  running: false,

  begin(result) {
    if (!result) return;

    this.running = true;

    toast(result.delay > 0
      ? I18N.fmt('Launching {n} accounts, {delay}s apart on each exit', { n: result.accounts, delay: result.delay })
      : I18N.fmt('Launching {n} accounts', { n: result.accounts }), 'ok');

    this.paint({ started: 0, failed: 0, total: result.accounts });
  },

  paint(state) {
    const hint = hook('launch-hint');

    if (hint) hint.textContent = this.running
      ? I18N.fmt('{done} of {total} launched', { done: state.started + state.failed, total: state.total })
      : '';

    const join = hook('join');

    if (join) {
      const label = Array.from(join.childNodes).find(n => n.nodeType === 3 && n.textContent.trim())
        || join.querySelector('span');

      if (label) label.textContent = this.running ? t('Stop') : t('Join server');

      // The icon has to follow the word, or the button reads "Stop" behind a play triangle.
      const icon = join.querySelector('i');

      if (icon) {
        if (!icon.dataset.ramIdle) icon.dataset.ramIdle = icon.className;

        icon.className = this.running ? 'ph ph-stop-circle' : icon.dataset.ramIdle;
      }
    }
  },

  async stop() {
    try { await RAM.call('accounts.cancelLaunch'); toast(t('Stopping after the current launch…')); }
    catch (error) { toast(String(error && error.message || error), 'error'); }
  },

  /// A batch outlives the page: switching language reloads it, and the shell can be closed and reopened while
  /// the app keeps launching. Without this the button would sit on "Join server" and start a second batch.
  async sync() {
    try {
      const state = await RAM.call('accounts.launchState');

      if (!state) return;

      this.running = !!state.running;

      this.paint(state);
    } catch { /* not knowing is the same as not running */ }
  },

  wire() {
    RAM.on('launch.progress', state => {
      if (!state) return;

      this.running = true;
      this.paint(state);

      // The app already knows which account failed and why; dropping that would leave the user with a count and
      // no idea which of twenty accounts to look at.
      if (state.state === 'failed')
        toast(`${state.username}: ${state.detail || t('launch failed')}`, 'error');
    });

    RAM.on('launch.batch', state => {
      this.running = false;

      this.paint(state || { started: 0, failed: 0, total: 0 });
      renderAccounts();

      if (!state) return;

      toast(state.cancelled
        ? I18N.fmt('Stopped: {started} of {total} launched', state)
        : I18N.fmt('{started} launched, {failed} failed', state), state.failed ? 'error' : 'ok');
    });
  }
};

// A place id, however it was pasted: bare, or inside a roblox.com/games/<id>/… link.
function placeIdIn(text) {
  const typed = String(text || '').trim();
  const link = typed.match(/(?:games|game)\/(\d+)/i);

  if (link) return link[1];

  return /^\d{5,}$/.test(typed) ? typed : '';
}

// The old window keeps the last place in General/SavedPlaceId; both windows read the same setting, so
// whichever one you used last is what the other opens with.
function rememberPlace() {
  const place = hook('place-id');

  if (place) RAM.call('settings.set', { section: 'General', key: 'SavedPlaceId', value: place.value.trim() }).catch(() => {});
}

async function loadGame() {
  const place = hook('place-id');
  if (!place) return;

  const placeId = place.value.trim();
  const name = hook('game-name');
  const sub = hook('game-place');
  const stats = { 'game-online': null, 'game-favorites': null, 'game-likes': null };

  const setStats = game => Object.entries({
    'game-online': game && game.playing,
    'game-favorites': game && game.favorites,
    'game-likes': game && game.likes
  }).forEach(([key, value]) => { const n = hook(key); if (n) n.textContent = compact(value); });

  if (!placeId) {
    if (name) name.textContent = '—';
    if (sub) sub.textContent = '';
    setStats(null);
    return;
  }

  try {
    const game = await RAM.call('games.info', { placeId });
    if (!game) { setStats(null); return; }

    if (name) name.textContent = game.name;
    if (sub) sub.textContent = game.creator ? `${game.creator} · ${placeId}` : 'Place · ' + placeId;

    setStats(game.known ? game : null);
  } catch {
    if (name) name.textContent = 'Unknown place';
    setStats(null);
  }
}

// ---------------------------------------------------------------- protecting the account store

// The app cannot show any account until the store is open, so this modal is the one thing that blocks the
// interface. The two-step confirmation lives here, not in C#: the app is told the password once, when it is
// already known to be typed twice the same way.
const Security = {
  step: 'Ready',

  async sync() {
    const state = await RAM.call('security.state');
    if (!state) return;

    Security.step = state.step;
    Security.render();

    return state.step;
  },

  render() {
    const modal = hook('security');
    if (!modal) return;

    const choose = hook('security-choose');
    const password = hook('security-password');
    const confirm = hook('security-confirm');
    const title = hook('security-title');
    const text = hook('security-text');
    const submit = hook('security-submit');

    const step = Security.step;

    modal.hidden = step === 'Ready';
    if (step === 'Ready') return;

    const asking = step === 'ChooseProtection';

    if (choose) choose.hidden = !asking;
    if (password) password.hidden = asking;

    if (step === 'Unlock') {
      if (title) title.textContent = 'Unlock your accounts';
      if (text) text.textContent = 'This account file is password-locked.';
      if (confirm) confirm.hidden = true;
      if (submit) submit.textContent = 'Unlock';
    } else if (step === 'SetPassword') {
      if (title) title.textContent = 'Set a password';
      if (text) text.textContent = 'It will be asked for every time the manager starts. There is no way to recover it.';
      if (confirm) confirm.hidden = false;
      if (submit) submit.textContent = 'Save';
    } else {
      if (title) title.textContent = 'Protect your accounts';
      if (text) text.textContent = 'Choose how the account file on this machine is encrypted.';
    }

    const input = hook('security-input');
    if (input && !asking) setTimeout(() => input.focus(), 60);
  },

  fail(message) {
    const box = hook('security-error');
    if (box) box.textContent = message || '';
  },

  async submit() {
    const input = hook('security-input');
    const confirm = hook('security-confirm');
    const value = input ? input.value : '';

    Security.fail('');

    if (Security.step === 'SetPassword' && confirm && value !== confirm.value)
      return Security.fail('Those passwords do not match.');

    try {
      await RAM.call(Security.step === 'Unlock' ? 'security.unlock' : 'security.setPassword', { password: value });

      if (input) input.value = '';
      if (confirm) confirm.value = '';

      await Security.sync();
      await refresh();
    } catch (e) { Security.fail(e.message); }
  },

  wire() {
    const def = hook('security-default');
    if (def) def.addEventListener('click', async () => {
      try { await RAM.call('security.useDefault'); await Security.sync(); await refresh(); }
      catch (e) { Security.fail(e.message); }
    });

    const choose = hook('security-choose-password');
    if (choose) choose.addEventListener('click', async () => {
      try { await RAM.call('security.choosePassword'); await Security.sync(); }
      catch (e) { Security.fail(e.message); }
    });

    const submit = hook('security-submit');
    if (submit) submit.addEventListener('click', () => Security.submit());

    [hook('security-input'), hook('security-confirm')].forEach(input => {
      if (input) input.addEventListener('keydown', e => { if (e.key === 'Enter') Security.submit(); });
    });

    RAM.on('security.changed', () => Security.sync());
  }
};

// ---------------------------------------------------------------- adding accounts

// Three routes into the same place: one cookie, a pasted pile of them, or a browser sign-in. The app validates
// every one of them the same way the old window does, so this only has to collect input and report back.
const AddAccount = {
  tab: 'cookie',
  group: null,

  // Mirrors the app's own cookie matcher: the "_|WARNING:..." banner is an inseparable part of a real cookie,
  // and everything up to the next whitespace belongs to it.
  pattern: /_\|WARNING:\S+/g,

  async open() {
    const modal = hook('add-modal');
    if (!modal) return;

    modal.hidden = false;

    AddAccount.status('');
    AddAccount.select('cookie');

    // Pre-select where accounts go, so the common case is one click fewer.
    try {
      AddAccount.group = await RAM.call('groups.defaultGroup') || null;

      document.querySelectorAll('[data-ram-add-group]').forEach(chip =>
        apply(chip, chip.getAttribute('data-ram-add-group') === AddAccount.group ? Style.chipOn : Style.chipOff));
    } catch { /* the chips simply stay as the design drew them */ }

    const input = hook('add-cookie');
    if (input) setTimeout(() => input.focus(), 60);
  },

  close() {
    const modal = hook('add-modal');
    if (modal) modal.hidden = true;
  },

  select(name) {
    AddAccount.tab = name;

    document.querySelectorAll('[data-ram-add-tab]').forEach(tab => {
      const on = tab.getAttribute('data-ram-add-tab') === name;
      const style = tab.getAttribute(on ? 'data-ram-on-style' : 'data-ram-off-style');

      // The design knows what an active tab looks like; both variants were captured from it on import.
      if (style) tab.style.cssText = style;
    });

    document.querySelectorAll('[data-ram-add-pane]').forEach(pane => {
      pane.hidden = pane.getAttribute('data-ram-add-pane') !== name;
    });

    const submit = hook('add-submit');
    if (submit) submit.hidden = name === 'login';
  },

  count() {
    const box = hook('add-bulk');
    const label = hook('add-bulk-count');
    if (!box || !label) return 0;

    const found = (box.value.match(AddAccount.pattern) || []).length;

    label.textContent = found === 1 ? '1 recognised' : found + ' recognised';

    return found;
  },

  status(text, kind) {
    const strip = hook('add-status');
    const label = hook('add-status-text');

    if (label) label.textContent = text || '';
    if (!strip) return;

    strip.hidden = !text;

    const icon = strip.querySelector('i');
    if (!icon) return;

    icon.className = 'ph ' + (kind === 'checking' ? 'ph-circle-notch'
      : kind === 'error' ? 'ph-warning-circle'
      : kind === 'ok' ? 'ph-check-circle' : 'ph-info');

    icon.style.animation = kind === 'checking' ? 'noct-spin 1s linear infinite' : '';
    strip.style.color = kind === 'error' ? 'oklch(0.66 0.13 26)'
      : kind === 'ok' ? 'oklch(0.72 0.085 152)'
      : 'var(--color-neutral-500)';
  },

  async submit() {
    const submit = hook('add-submit');
    if (submit) submit.disabled = true;

    try {
      if (AddAccount.tab === 'cookie') {
        const input = hook('add-cookie');
        const cookie = input ? input.value.trim() : '';

        AddAccount.status('Checking the cookie with Roblox…', 'checking');

        const account = await RAM.call('accounts.add', { cookie, group: AddAccount.group }, 60000);

        AddAccount.status('Added ' + (account && account.username ? account.username : 'the account'), 'ok');

        if (input) input.value = '';
      } else {
        const box = hook('add-bulk');
        const text = box ? box.value : '';
        const found = AddAccount.count();

        if (!found) { AddAccount.status('No Roblox cookies found in that text', 'error'); return; }

        // Each cookie is validated against Roblox with a delay between them, so a big paste takes a while.
        AddAccount.status(`Checking ${found} cookie${found === 1 ? '' : 's'}… this takes a moment`, 'checking');

        const result = await RAM.call('accounts.addBulk', { text, group: AddAccount.group }, 15 * 60 * 1000);

        if (result) {
          AddAccount.status(`${result.added} added, ${result.failed} rejected out of ${result.found}`,
            result.added > 0 ? 'ok' : 'error');

          if (result.added > 0 && box) box.value = '';
          AddAccount.count();
        }
      }

      await refresh();
    } catch (e) {
      AddAccount.status(e.message, 'error');
    } finally {
      if (submit) submit.disabled = false;
    }
  },

  wire() {
    const open = hook('add-account');
    if (open) open.addEventListener('click', AddAccount.open);

    const close = hook('add-close');
    if (close) close.addEventListener('click', AddAccount.close);

    const modal = hook('add-modal');
    if (modal) modal.addEventListener('click', e => { if (e.target === modal) AddAccount.close(); });

    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && modal && !modal.hidden) AddAccount.close();
    });

    document.querySelectorAll('[data-ram-add-tab]').forEach(tab =>
      tab.addEventListener('click', () => AddAccount.select(tab.getAttribute('data-ram-add-tab'))));

    document.querySelectorAll('[data-ram-add-group]').forEach(chip =>
      chip.addEventListener('click', () => {
        AddAccount.group = chip.getAttribute('data-ram-add-group');

        document.querySelectorAll('[data-ram-add-group]').forEach(other =>
          apply(other, other === chip ? Style.chipOn : Style.chipOff));

        // Choosing here is also a statement about where accounts belong from now on.
        RAM.call('groups.defaultGroup', { name: AddAccount.group }).catch(() => {});
      }));

    const bulk = hook('add-bulk');
    if (bulk) bulk.addEventListener('input', AddAccount.count);

    const login = hook('add-login');
    if (login) login.addEventListener('click', async () => {
      try {
        await RAM.call('accounts.addBrowser');
        AddAccount.status('A browser window is open — sign in there and the account appears here', 'checking');
      } catch (e) { AddAccount.status(e.message, 'error'); }
    });

    const submit = hook('add-submit');
    if (submit) {
      // The design ships this button disabled; nothing ever re-enables it (submit() would, but a disabled button
      // never fires the click that runs submit()). There is no "enable when valid" rule to honour, so enable it.
      submit.disabled = false;
      submit.addEventListener('click', AddAccount.submit);
    }
  }
};

// ---------------------------------------------------------------- the row menu

// Right-click (or the row's ⋮) opens the design's menu. Copying is done by the app, not here: a cookie should
// never pass through the page just to reach the clipboard.
const RowMenu = {
  username: null,

  // Every Tools command is the signed-in browser pointed somewhere. 'ask' means the page prompts for the address,
  // which is what the old window's "URL" command does.
  tools: {
    'tool-cookie-browser': '',
    'tool-settings': 'https://www.roblox.com/my/account',
    'tool-avatar': 'https://www.roblox.com/my/avatar',
    'tool-trade': 'https://www.roblox.com/trades',
    'tool-groups': 'https://www.roblox.com/my/groups',
    'tool-launch-url': 'ask'
  },

  open(username, x, y) {
    const menu = hook('row-menu');
    if (!menu) return;

    RowMenu.username = username;
    RowMenu.closeSubs();

    menu.hidden = false;

    // The design's menu carries the account name as its header; it arrives from the export as a sample value.
    // It has no hook of its own, so take the first labelled node above the commands.
    const header = Array.from(menu.children).find(node => !node.hasAttribute('data-ram-menu') && node.textContent.trim());

    // With a selection the commands act on all of it, so the header has to say so — otherwise "Remove" reads as
    // "remove this one" right up until twenty accounts disappear.
    const targets = RowMenu.targets();

    if (header) header.textContent = targets.length > 1
      ? I18N.fmt('{n} accounts selected', { n: targets.length })
      : username;

    // Clamp to the window: the design positions the menu inline, and a right-click near the edge would
    // otherwise put half of it off screen.
    const box = menu.getBoundingClientRect();
    const left = Math.min(x, window.innerWidth - box.width - 8);
    const top = Math.min(y, window.innerHeight - box.height - 8);

    menu.style.left = Math.max(8, left) + 'px';
    menu.style.top = Math.max(8, top) + 'px';
  },

  close() {
    const menu = hook('row-menu');
    if (menu) menu.hidden = true;

    RowMenu.closeSubs();
  },

  closeSubs() {
    document.querySelectorAll('[data-ram-submenu]').forEach(sub => { sub.hidden = true; });
  },

  showSub(name, anchor) {
    RowMenu.closeSubs();

    const sub = document.querySelector(`[data-ram-submenu="${name}"]`);
    if (!sub || !anchor) return;

    sub.hidden = false;

    const box = anchor.getBoundingClientRect();
    const size = sub.getBoundingClientRect();

    sub.style.left = Math.min(box.right + 4, window.innerWidth - size.width - 8) + 'px';
    sub.style.top = Math.min(box.top, window.innerHeight - size.height - 8) + 'px';
  },

  /// Which accounts a command applies to. Right-clicking inside a selection acts on the whole selection — the
  /// menu opens on a selection it deliberately preserved, so acting on one account would ignore what the user
  /// just picked. Right-clicking outside it selects that row only, so this still reads as one.
  targets() {
    return State.selected.has(RowMenu.username) && State.selected.size > 1
      ? Array.from(State.selected)
      : [RowMenu.username];
  },

  async run(id) {
    const username = RowMenu.username;
    if (!username) return;

    const copy = id.startsWith('copy-') ? id.slice(5) : null;
    const set = id.startsWith('set-') ? id.slice(4) : null;

    const many = RowMenu.targets();
    const bulk = many.length > 1;

    try {
      if (copy) {
        const ok = await RAM.call('accounts.copy', bulk ? { accounts: many, what: copy } : { username, what: copy });

        toast(ok
          ? (bulk ? I18N.fmt('Copied the {what} of {n} accounts', { what: copy, n: many.length }) : I18N.fmt('Copied the {what}', { what: copy }))
          : I18N.fmt('{name} has no {what} stored', { name: username, what: copy }), ok ? 'ok' : 'error');
      } else if (set) {
        await RowMenu.edit(username, set, many);
      } else switch (id) {
        case 'launch':
        case 'launch-here':
          RowMenu.close();
          await launch(many);
          break;

        case 'open-profile': {
          const account = State.accounts.find(a => a.username === username);
          if (account) await RAM.call('app.openUrl', { url: `https://www.roblox.com/users/${account.userId}/profile` });
          break;
        }

        case 'refresh-one':
        case 'revalidate': {
          // One call, not a loop: each check blocks for up to 15 seconds inside the app, so looping from here
          // froze the window for minutes and hit Roblox once per account with nothing between them. The app now
          // paces the selection itself on a background thread.
          RowMenu.close();

          toast(bulk ? I18N.fmt('Checking {n} accounts…', { n: many.length }) : I18N.fmt('Checking {name}…', { name: username }));

          const result = await RAM.call('accounts.revalidate', bulk ? { accounts: many } : { username }, 10 * 60 * 1000);

          if (!result) break;

          if (bulk) {
            const good = (result.results || []).filter(one => one.valid).length;

            toast(I18N.fmt('{good} of {n} still work', { good, n: result.results ? result.results.length : many.length }),
              good === many.length ? 'ok' : 'error');
          } else {
            toast(`${username}: ${result.reason}`, result.valid ? 'ok' : 'error');
          }

          break;
        }

        case 'remove': {
          if (!confirm(bulk
            ? I18N.fmt('Remove {n} accounts from the manager?', { n: many.length })
            : I18N.fmt('Remove {name} from the manager?', { name: username }))) break;

          const gone = await RAM.call('accounts.remove', bulk ? { accounts: many } : { username });

          State.selected.clear();

          toast(bulk ? I18N.fmt('{n} accounts removed', { n: gone && gone.removed }) : I18N.fmt('{name} removed', { name: username }), 'ok');
          break;
        }

        case 'join-job': {
          const place = hook('place-id');
          const placeId = place ? place.value.trim() : '';

          if (!placeId) { toast('Set a place id in the launcher first', 'error'); break; }

          RowMenu.close();

          const typed = await Prompt.ask(`Job id for ${username}`, (hook('job-id') || {}).value || '');
          if (typed === null || !typed) break;

          // "VIP:<code>" is how the old window and the server list name a private server, and it is a different
          // parameter — not a job id. Going through launch() would collapse the two, so this calls directly.
          const vip = /^VIP:/i.test(typed);

          toast(`Launching ${username}…`);

          await RAM.call('accounts.launch', {
            username,
            placeId,
            jobId: vip ? typed.slice(4) : typed,
            joinVip: vip
          }, 180000);

          toast(`${username}: client started`, 'ok');
          break;
        }

        case 'follow': {
          const target = await Prompt.ask(`Which player should ${username} follow?`, '');
          if (target === null || !target) break;

          RowMenu.close();
          toast(`${username} is following ${target}…`);

          await RAM.call('accounts.follow', { username, target }, 180000);
          toast(`${username} joined ${target}`, 'ok');
          break;
        }

        case 'signout':
          if (!confirm(`Sign ${username} out of all other sessions?\n\nAnyone else using this account is kicked out, and its cookie is replaced.`)) break;

          toast(`Signing ${username} out everywhere…`);
          await RAM.call('accounts.signOutOthers', { username }, 60000);
          toast(`${username}: other sessions signed out`, 'ok');
          break;

        default:
          if (id in RowMenu.tools) {
            RowMenu.close();

            const url = RowMenu.tools[id] === 'ask'
              ? await Prompt.ask(`Open which page as ${username}?`, 'https://www.roblox.com/')
              : RowMenu.tools[id];

            if (url === null) break;

            await RAM.call('accounts.openBrowser', { username, url });
            toast(`A browser is opening as ${username}`);
            break;
          }

          // The design offers more than the app exposes yet; say so instead of doing nothing.
          toast(`"${id}" is not wired up yet`, 'error');
      }

      await refresh();
    } catch (e) { toast(e.message, 'error'); }

    RowMenu.close();
  },

  // set-alias / set-description / set-group / set-proxy all need one line of text. WebView2 blocks window.prompt,
  // so this is a minimal inline prompt built from the design's own tokens.
  async edit(username, what, many) {
    // Refuse BEFORE the prompt. These two have no setter behind them, and asking first meant the user typed a
    // real account password into a box that then threw it away.
    if (what === 'password' || what === 'flags')
      return toast(`Setting the ${what === 'password' ? 'account password' : 'launch flags'} is only in the old window for now`, 'error');

    // A group or a proxy is worth setting on a whole selection. An alias or a description is what tells two
    // accounts apart, so giving twenty accounts the same one would destroy exactly the information it carries —
    // those stay on the row that was right-clicked.
    const targets = (what === 'group' || what === 'proxy') && many && many.length > 1 ? many : [username];
    const bulk = targets.length > 1;

    const account = State.accounts.find(a => a.username === username) || {};
    const current = what === 'alias' ? account.alias
      : what === 'description' ? account.description
      : what === 'group' ? account.group
      : what === 'proxy' ? (account.proxy || '') : '';

    const value = await Prompt.ask(bulk
      ? I18N.fmt('Set {what} for {n} accounts', { what, n: targets.length })
      : I18N.fmt('Set {what} for {name}', { what, name: username }), bulk ? '' : (current || ''));

    if (value === null) return;

    if (what === 'group') {
      const result = await RAM.call('accounts.setGroup', bulk ? { accounts: targets, group: value } : { username, group: value });

      toast(bulk ? I18N.fmt('{n} accounts moved to {name}', { n: result.changed, name: result.group })
                 : I18N.fmt('{name}: group set', { name: username }), 'ok');
      return;
    }

    const method = what === 'alias' ? 'accounts.setAlias'
      : what === 'description' ? 'accounts.setDescription'
      : null;

    if (method) {
      const params = { username };
      params[what] = value;

      await RAM.call(method, params);
      toast(I18N.fmt('{name}: {what} set', { name: username, what }), 'ok');
      return;
    }

    if (what === 'proxy') {
      // The current value is shown masked (scheme://user:***@host:port) because the real password never leaves
      // the app. Saving that mask back would store the literal "***" as the password. So a value still carrying
      // the mask means "no change" (or an ambiguous half-edit) — never a write.
      if (value.includes('***')) {
        if (value === (current || '')) return;                    // untouched — nothing to do
        toast('Type the full proxy to change it, or clear the box to remove it', 'error');
        return;
      }

      // Several proxies may be pasted at once; assign hands them out round-robin across the selection.
      const proxies = value ? value.split(/[\n,]/).map(one => one.trim()).filter(Boolean) : [];

      const result = await RAM.call('proxies.assign', { accounts: targets, proxies });

      toast(proxies.length
        ? I18N.fmt('{n} accounts given a proxy', { n: result.assigned })
        : I18N.fmt('Proxy removed from {n} accounts', { n: result.assigned }), 'ok');
      return;
    }

    toast(`Setting ${what} is not wired up yet`, 'error');
  },

  wire() {
    document.addEventListener('contextmenu', e => {
      const row = e.target.closest('[data-ram-row]');
      if (!row) return;

      e.preventDefault();

      if (!State.selected.has(row.dataset.username)) {
        State.selected.clear();
        State.selected.add(row.dataset.username);
        renderAccounts();
      }

      RowMenu.open(row.dataset.username, e.clientX, e.clientY);
    });

    // the ⋮ inside a row
    document.addEventListener('click', e => {
      const dots = e.target.closest('[data-ram-field="menu"]');

      if (dots) {
        const row = dots.closest('[data-ram-row]');
        if (!row) return;

        e.stopPropagation();

        // Same rule as the right-click path: opening the menu on a row outside the selection makes that row the
        // selection. Without this, ⋮ on an unselected row would run bulk commands against somebody else's rows.
        if (!State.selected.has(row.dataset.username)) {
          State.selected.clear();
          State.selected.add(row.dataset.username);
          renderAccounts();
        }

        const box = dots.getBoundingClientRect();
        RowMenu.open(row.dataset.username, box.left, box.bottom + 4);

        return;
      }

      if (!e.target.closest('[data-ram="row-menu"]') && !e.target.closest('[data-ram-submenu]')) RowMenu.close();
    });

    document.addEventListener('keydown', e => { if (e.key === 'Escape') RowMenu.close(); });

    document.addEventListener('mouseover', e => {
      const item = e.target.closest('[data-ram-menu]');
      if (!item) return;

      const id = item.getAttribute('data-ram-menu');

      if (id === 'copy' || id === 'set' || id === 'tools') RowMenu.showSub(id, item);
      else if (!e.target.closest('[data-ram-submenu]')) RowMenu.closeSubs();
    });

    document.addEventListener('click', e => {
      const item = e.target.closest('[data-ram-menu]');
      if (!item) return;

      const id = item.getAttribute('data-ram-menu');

      if (id === 'copy' || id === 'set' || id === 'tools') return; // parents only open their submenu

      RowMenu.run(id);
    });
  }
};

// ---------------------------------------------------------------- interface language
//
// The design drew a language picker but never wired it, and the chip in the title bar did not even open. The
// dictionary lives in i18n.js; this is the part that reads the choice, offers it, and stores it.

const Language = {
  // What the chip shows, and what the picker offers. Adding a language means adding its dictionary to i18n.js.
  choices: [
    { code: 'en', badge: 'EN', native: 'English', english: 'English' },
    { code: 'ru', badge: 'RU', native: 'Русский', english: 'Russian' }
  ],

  // Stamped once in wire(), because the obvious lookups stop working the moment the page is translated: the
  // chip is found by its English title, and apply() rewrites titles.
  chip() {
    return document.querySelector('[data-ram="lang-chip"]');
  },

  find() {
    return document.querySelector('[title="Interface language"]')
      || Array.from(document.querySelectorAll('div, span')).find(n => !n.children.length && n.textContent.trim() === 'EN');
  },

  async load() {
    let saved = 'en';

    try { saved = String(await RAM.call('settings.get', { section: 'General', key: 'UiLanguage' }) || 'en'); }
    catch { /* an unreadable setting just means English */ }

    if (!this.choices.some(c => c.code === saved)) saved = 'en';

    I18N.lang = saved;

    const chip = this.chip();
    const badge = this.choices.find(c => c.code === saved);

    // The chip's own label — the design writes "EN" into a text node inside it.
    if (chip && badge) {
      const label = Array.from(chip.childNodes).find(n => n.nodeType === 3 && n.textContent.trim())
        || chip.querySelector('span');

      if (label) label.textContent = badge.badge;
    }

    I18N.apply();
  },

  async choose(code) {
    if (code === I18N.lang) return this.close();

    try {
      await RAM.call('settings.set', { section: 'General', key: 'UiLanguage', value: code });

      // Reloading is the honest way to switch: every screen is rebuilt from the design's English markup and
      // translated once, instead of leaving half the page in the language it was rendered in.
      location.reload();
    } catch (error) {
      toast(String(error && error.message || error), 'error');
      this.close();
    }
  },

  close() {
    const open = document.querySelector('[data-ram="lang-menu"]');

    if (open) open.remove();
  },

  open() {
    if (document.querySelector('[data-ram="lang-menu"]')) return this.close();

    const chip = this.chip();
    if (!chip) return;

    const box = chip.getBoundingClientRect();
    const menu = document.createElement('div');

    menu.setAttribute('data-ram', 'lang-menu');
    menu.style.cssText = `position:fixed;z-index:80;top:${Math.round(box.bottom + 6)}px;right:${Math.round(window.innerWidth - box.right)}px;
      width:240px;padding:6px;border-radius:var(--radius-lg);background:color-mix(in srgb, var(--color-surface) 94%, transparent);
      backdrop-filter:blur(20px) saturate(112%);border:1px solid var(--color-neutral-700);box-shadow:rgb(0,0,0) 0 24px 54px -24px`;

    menu.innerHTML = `<div style="padding:7px 9px 6px;font-size:9px;font-weight:500;letter-spacing:.16em;
      text-transform:uppercase;color:var(--color-neutral-600)">${t('Language')}</div>`;

    this.choices.forEach(choice => {
      const on = choice.code === I18N.lang;
      const row = document.createElement('div');

      row.style.cssText = `display:flex;align-items:center;gap:11px;padding:8px 9px;border-radius:var(--radius-md);
        cursor:pointer;font-size:12.5px;color:var(${on ? '--color-accent-200' : '--color-neutral-200'})`;

      row.innerHTML = `
        <span style="width:32px;height:22px;flex:0 0 auto;display:grid;place-items:center;border-radius:var(--radius-sm);
              font-size:9.5px;font-weight:600;letter-spacing:.08em;background:color-mix(in srgb, var(--color-bg) 60%, transparent);
              border:1px solid var(--color-neutral-800);color:var(--color-neutral-500)">${choice.badge}</span>
        <div style="flex:1 1 0%;min-width:0">
          <div style="font-weight:500">${choice.native}</div>
          <div style="font-size:10.5px;color:var(--color-neutral-600)">${choice.english}</div>
        </div>
        <i class="ph ph-check" style="font-size:14px;color:var(--color-accent-300);${on ? '' : 'visibility:hidden'}"></i>`;

      row.addEventListener('mouseenter', () => row.style.background = 'color-mix(in srgb, var(--color-accent-900) 60%, transparent)');
      row.addEventListener('mouseleave', () => row.style.background = 'transparent');
      row.addEventListener('click', () => this.choose(choice.code));

      menu.appendChild(row);
    });

    document.body.appendChild(menu);
  },

  wire() {
    const chip = this.find();

    if (chip) {
      chip.setAttribute('data-ram', 'lang-chip');
      chip.style.cursor = 'pointer';
      chip.addEventListener('click', event => { event.stopPropagation(); this.open(); });
    }

    document.addEventListener('click', event => {
      if (!event.target.closest('[data-ram="lang-menu"]')) this.close();
    });
  }
};

// ---------------------------------------------------------------- screens
//
// The rail picks a screen and the tab strip picks a pane of the accounts screen. Both were separate states in
// the prototype and are one page here, so switching is a matter of which block is hidden.

const Screens = {
  // rail title -> what to show. Everything not listed has no screen in this build yet.
  destinations: {
    'Accounts': { screen: 'accounts', tab: 'accounts' },
    'Launcher': { screen: 'launcher', tab: 'launcher' },
    'Server list': { screen: 'launcher', tab: 'launcher' },
    'Watcher': { screen: 'watcher', tab: 'watcher' },
    'Settings': { screen: 'settings' },
    'Proxies': { screen: 'proxies' },
    'Free items': { screen: 'freeitems' }
  },

  current: 'accounts',

  // the accounts screen and its siblings are the only ones the tab strip belongs to
  tabbed: new Set(['accounts', 'launcher', 'watcher']),

  show(name) {
    this.current = name;

    document.querySelectorAll('[data-ram-screen]').forEach(block =>
      block.hidden = block.getAttribute('data-ram-screen') !== name);

    const strip = hook('tabstrip');
    if (strip) strip.hidden = !this.tabbed.has(name);

    document.querySelectorAll('[data-ram-tab]').forEach(tab => {
      const on = tab.getAttribute('data-ram-tab') === name;
      tab.setAttribute('style', tab.getAttribute(on ? 'data-ram-on-style' : 'data-ram-off-style') || '');
    });

    document.querySelectorAll('[data-ram-rail]').forEach(item => {
      const dest = this.destinations[item.getAttribute('data-ram-rail')];
      const on = dest && dest.screen === name;
      item.setAttribute('style', item.getAttribute(on ? 'data-ram-on-style' : 'data-ram-off-style') || '');
    });

    // The watcher pane has no design of its own yet; the tab is real, so it says so rather than showing nothing.
    const empty = document.querySelector('[data-ram-screen="watcher"]');
    if (empty) empty.hidden = name !== 'watcher';

    if (name === 'settings') SettingsScreen.open();
    if (name === 'proxies') ProxyScreen.open();
    if (name === 'launcher') ServerScreen.open();
    if (name === 'freeitems') FreeItemsScreen.open();
  },

  wire() {
    // The watcher tab is an empty state, so it is built here rather than imported.
    const host = document.querySelector('[data-ram-screen="accounts"]');

    if (host && !document.querySelector('[data-ram-screen="watcher"]')) {
      const block = document.createElement('div');
      block.setAttribute('data-ram-screen', 'watcher');
      block.hidden = true;
      block.style.cssText = 'flex:1 1 0%;display:grid;place-items:center;color:var(--color-neutral-600);font-size:13px;text-align:center;padding:60px 20px';
      block.innerHTML = '<div><div style="font-size:15px;color:var(--color-neutral-400);margin-bottom:6px">Watcher</div>' +
        '<div>Rejoining and the client sweepers run in the background.<br>Their switches are under Settings.</div></div>';

      host.parentElement.appendChild(block);

    I18N.apply(block);
    }

    document.querySelectorAll('[data-ram-rail]').forEach(item => item.addEventListener('click', () => {
      const title = item.getAttribute('data-ram-rail');
      const dest = this.destinations[title];

      if (!dest) return toast(`${title} is not in this build yet`);

      this.show(dest.screen);
    }));

    document.querySelectorAll('[data-ram-tab]').forEach(tab =>
      tab.addEventListener('click', () => this.show(tab.getAttribute('data-ram-tab'))));

    this.show('accounts');
  }
};

// ---------------------------------------------------------------- free items
//
// The collection itself has been in the app for a while but was reachable only from the old window's context
// menu. The design never drew this screen, so it is built here — from the design's own tokens, never invented
// colours, so it still matches after a re-import.

const FreeItemsScreen = {
  built: false,
  running: false,

  build() {
    if (this.built) return;

    const host = document.querySelector('[data-ram-screen="accounts"]');
    if (!host) return;

    const block = document.createElement('div');

    block.setAttribute('data-ram-screen', 'freeitems');
    block.hidden = true;
    block.style.cssText = 'flex:1 1 0%;min-height:0;display:flex;flex-direction:column;gap:14px;padding:0 2px';

    block.innerHTML = `
      <div style="flex:0 0 auto;display:flex;align-items:center;gap:14px;padding:18px 20px;border-radius:var(--radius-lg);
                  border:1px solid var(--color-neutral-800);background:color-mix(in srgb, var(--color-bg) 46%, transparent)">
        <div style="width:44px;height:44px;flex:0 0 auto;display:grid;place-items:center;border-radius:calc(var(--radius-md) * 1.25);
                    background:var(--color-accent-900);border:1px solid var(--color-accent-800);font-size:19px;color:var(--color-accent-300)">
          <i class="ph ph-gift"></i>
        </div>
        <div style="flex:1 1 0%;min-width:0">
          <div style="font-size:22px;font-weight:600;color:var(--color-neutral-100)">Free items</div>
          <div data-ram="fi-subtitle" style="font-size:12.5px;color:var(--color-neutral-500);margin-top:2px">
            Buys every zero-price catalog item on the accounts you ticked. No Robux is ever spent.
          </div>
        </div>
        <button class="btn btn-secondary" data-ram="fi-stop" hidden
                style="border-radius:var(--radius-md);padding:9px 16px;font-size:12.5px">
          <i class="ph ph-stop-circle"></i> Stop
        </button>
        <button class="btn btn-primary" data-ram="fi-start"
                style="border-radius:var(--radius-md);padding:9px 16px;font-size:12.5px">
          <i class="ph ph-gift"></i> Collect
        </button>
      </div>

      <div style="flex:1 1 0%;min-height:0;display:flex;flex-direction:column;padding:16px 18px;border-radius:var(--radius-lg);
                  border:1px solid var(--color-neutral-800);background:color-mix(in srgb, var(--color-bg) 46%, transparent)">
        <div style="flex:0 0 auto;display:flex;align-items:center;gap:10px;margin-bottom:10px">
          <div style="font-size:9px;font-weight:500;letter-spacing:.16em;text-transform:uppercase;color:var(--color-neutral-600)">Progress</div>
          <div style="flex:1 1 0%;height:1px;background:var(--color-neutral-900)"></div>
          <div data-ram="fi-count" style="font-size:11px;color:var(--color-neutral-600);font-variant-numeric:tabular-nums"></div>
        </div>
        <div data-ram="fi-log" style="flex:1 1 0%;min-height:0;overflow:auto;font-size:11.5px;line-height:1.9;
                    color:var(--color-neutral-400);font-variant-numeric:tabular-nums"></div>
      </div>`;

    host.parentElement.appendChild(block);

    const start = block.querySelector('[data-ram="fi-start"]');
    const stop = block.querySelector('[data-ram="fi-stop"]');

    if (start) start.addEventListener('click', () => this.start());
    if (stop) stop.addEventListener('click', () => this.stop());

    this.built = true;
  },

  async open() {
    this.build();

    try { this.paint(await RAM.call('freeitems.state')); }
    catch (error) { toast(String(error && error.message || error), 'error'); }
  },

  paint(state) {
    if (!state) return;

    this.running = !!state.running;

    const start = hook('fi-start');
    const stop = hook('fi-stop');

    if (start) start.hidden = this.running;
    if (stop) stop.hidden = !this.running;

    const log = hook('fi-log');

    if (log && Array.isArray(state.lines)) {
      log.textContent = '';
      state.lines.forEach(line => this.line(line));
    }

    this.say(this.running
      ? `Collecting on ${state.accounts} account${state.accounts === 1 ? '' : 's'} — this is deliberately slow, the catalog is rate limited.`
      : 'Buys every zero-price catalog item on the accounts you ticked. No Robux is ever spent.');
  },

  say(text) {
    const line = hook('fi-subtitle');

    if (line) line.textContent = text;
  },

  line(text) {
    const log = hook('fi-log');
    if (!log) return;

    const row = document.createElement('div');

    row.textContent = text;
    row.style.whiteSpace = 'pre-wrap';

    log.appendChild(row);

    // a long run scrolls itself, so the newest line stays in view
    log.scrollTop = log.scrollHeight;

    const count = hook('fi-count');
    if (count) count.textContent = `${log.childElementCount} lines`;
  },

  async start() {
    const accounts = Array.from(State.selected);

    if (!accounts.length) return toast('Tick the accounts to collect for on the Accounts tab', 'error');

    try {
      const result = await RAM.call('freeitems.start', { accounts });

      this.running = true;

      const log = hook('fi-log');
      if (log) log.textContent = '';

      this.paint({ running: true, accounts: result.accounts, lines: [] });

      toast(`Collecting up to ${result.maxPerRun} items on ${result.accounts} accounts`, 'ok');
    } catch (error) {
      toast(String(error && error.message || error), 'error');
    }
  },

  async stop() {
    try { await RAM.call('freeitems.stop'); toast('Stopping after the current item…'); }
    catch (error) { toast(String(error && error.message || error), 'error'); }
  },

  wire() {
    // Built now rather than on first open: Screens.show() sets hidden on every [data-ram-screen] before calling
    // open(), so a block that did not exist yet would be created hidden and never appear.
    this.build();

    RAM.on('freeitems.progress', data => { if (data && data.line) this.line(data.line); });

    RAM.on('freeitems.done', data => {
      this.running = false;

      const start = hook('fi-start');
      const stop = hook('fi-stop');

      if (start) start.hidden = false;
      if (stop) stop.hidden = true;

      if (!data) return;

      const summary = data.error
        ? data.error
        : `${data.collected} collected, ${data.skipped} already owned, ${data.failed} failed across ${data.accounts} accounts`;

      this.line('— ' + summary);
      this.say(summary);

      toast(data.error ? `Free items: ${data.error}` : `Free items: ${summary}`, data.error ? 'error' : 'ok');
    });
  }
};

// ---------------------------------------------------------------- settings
//
// The design ships six invented sections; what is listed here is what the manager actually stores. Every entry
// is a real key in RAMSettings.ini, and its explanation is the comment written beside it there, so this file
// never has a second copy of the truth.

const SettingsScreen = {
  sections: [
    { name: 'Client launch', ini: 'General', keys: [
      ['EnableMultiRbx', 'Multi Roblox', 'toggle'],
      ['DirectLaunch', 'Direct launch', 'toggle'],
      ['AccountJoinDelay', 'Delay between launches', 'input'],
      ['AsyncJoin', 'Launch all at once', 'toggle'],
      ['AutoCloseLastProcess', 'Close a running client first', 'toggle'],
      ['WatchLaunchResult', 'Watch how each launch ends', 'toggle']
    ]},
    { name: 'Client windows', ini: 'General', keys: [
      ['RenameClientWindows', 'Name the windows', 'toggle'],
      ['ClientWindowTitle', 'Title template', 'input'],
      ['UseSeparateDesktop', 'Launch off screen', 'toggle'],
      ['ClientDesktopName', 'Desktop name', 'input'],
      ['CloseRobloxErrorDialogs', 'Close error dialogs', 'toggle']
    ]},
    { name: 'Proxies', ini: 'General', keys: [
      ['UseAccountProxies', 'Use the proxy on each account', 'toggle'],
      ['ProxySplitTunnel', 'Split tunnelling', 'toggle'],
      ['ProxyVerifyExitIP', 'Check the exit address first', 'toggle'],
      ['ProxyIdentityHosts', 'Hosts that must go through it', 'input']
    ]},
    { name: 'Relaunching', ini: 'General', keys: [
      ['RelaunchEnabled', 'Put accounts back', 'toggle'],
      ['RelaunchOnCrash', 'After a crash', 'toggle'],
      ['RelaunchOnStuck', 'After a stuck client', 'toggle'],
      ['RelaunchCooldownSeconds', 'Cooldown per account', 'input'],
      ['RelaunchMaxPerHour', 'Ceiling per hour', 'input'],
      ['RelaunchGiveUpAfter', 'Give up after', 'input']
    ]},
    { name: 'Dead clients', ini: 'General', keys: [
      ['ReaperEnabled', 'Close crashed clients', 'toggle'],
      ['ReaperDryRun', 'Report only', 'toggle'],
      ['ReaperGraceSeconds', 'Grace period', 'input'],
      ['ReaperIntervalSeconds', 'Sweep every', 'input'],
      ['ReaperKillOrphanCrashHandlers', 'Clean up crash handlers', 'toggle'],
      ['ReaperWhitelist', 'Never touch these process ids', 'input'],
      ['StuckDetectorEnabled', 'Read the clients’ own logs', 'toggle'],
      ['StuckKillAuthErrors', 'Close refused clients', 'toggle'],
      ['StuckKillStuck', 'Close clients that never joined', 'toggle'],
      ['StuckLaunchGraceSeconds', 'Still starting until', 'input'],
      ['StuckAfterSeconds', 'Counts as stuck after', 'input'],
      ['StuckIntervalSeconds', 'Classify every', 'input']
    ]},
    { name: 'Web server', ini: 'WebServer', keys: [
      ['WebServerPort', 'Port', 'input'],
      ['Password', 'Password', 'input'],
      ['EveryRequestRequiresPassword', 'Every request needs it', 'toggle'],
      ['AllowExternalConnections', 'Accept other machines', 'toggle'],
      ['AllowGetAccounts', 'May list accounts', 'toggle'],
      ['AllowGetCookie', 'May read cookies', 'toggle'],
      ['AllowLaunchAccount', 'May launch accounts', 'toggle'],
      ['AllowAccountEditing', 'May edit accounts', 'toggle']
    ]},
    { name: 'Interface', ini: 'General', keys: [
      ['ShowPresence', 'Show what accounts are doing', 'toggle'],
      ['PresenceUpdateRate', 'Check presence every', 'input'],
      ['AutoCookieRefresh', 'Keep cookies fresh', 'toggle'],
      ['SavePasswords', 'Store passwords', 'toggle'],
      ['DefaultGroup', 'New accounts go to', 'input'],
      ['MaxRecentGames', 'Recent games kept', 'input'],
      ['DisableAgingAlert', 'Hide the ageing warning', 'toggle']
    ]}
  ],

  shapes: {},          // one row of each kind, taken from the design
  values: {},          // ini section -> { key: { value, comment } }
  section: null,
  opened: false,

  // The design's panes are six fixed lists; they are used as a parts bin and then dropped.
  capture() {
    const host = hook('settings');
    if (!host) return;

    const kindOf = row => {
      const control = row.querySelector('[data-ram-setting]');
      if (!control) return null;
      if (control.tagName === 'INPUT') return 'input';
      return control.querySelector('span') ? 'select' : 'toggle';
    };

    document.querySelectorAll('[data-ram-settings-pane]').forEach(pane => {
      Array.from(pane.content.children).forEach(row => {
        const kind = kindOf(row);
        if (kind && !this.shapes[kind]) this.shapes[kind] = row.cloneNode(true);
      });
    });

    // The design's "Reset section" button has nothing behind it — the manager keeps no defaults to go back to.
    // It is a <button> whose only child is an icon, so the old search for a matching <div> never found it.
    const reset = Array.from(document.querySelectorAll('[data-ram-screen="settings"] button'))
      .find(node => node.textContent.trim() === 'Reset section');

    if (reset) reset.hidden = true;

    this.style = {
      toggleOn: host.getAttribute('data-ram-toggle-on') || '',
      toggleOff: host.getAttribute('data-ram-toggle-off') || '',
      knobOn: host.getAttribute('data-ram-knob-on') || '',
      knobOff: host.getAttribute('data-ram-knob-off') || ''
    };

    // The section list is six rows in the design and a different number here, so one row is the template.
    const nav = document.querySelector('[data-ram-settings-section]');

    if (nav) {
      this.navShape = nav.cloneNode(true);
      this.navHost = nav.parentElement;
      this.navIcons = Array.from(document.querySelectorAll('[data-ram-settings-section] i'))
        .map(icon => icon.className);
    }
  },

  async open() {
    if (!this.section) this.section = this.sections[0].name;

    if (!this.opened) {
      this.renderNav();
      this.opened = true;
    }

    await this.render();
  },

  renderNav() {
    if (!this.navHost || !this.navShape) return;

    this.navHost.querySelectorAll('[data-ram-settings-section]').forEach(row => row.remove());

    this.sections.forEach((section, index) => {
      const row = this.navShape.cloneNode(true);

      row.setAttribute('data-ram-settings-section', section.name);

      const icon = row.querySelector('i');
      if (icon && this.navIcons.length) icon.className = this.navIcons[index % this.navIcons.length];

      const label = row.querySelector('span') || row;
      label.textContent = section.name;

      row.addEventListener('click', () => { this.section = section.name; this.open(); });

      this.navHost.appendChild(row);
    });

    I18N.apply(this.navHost);
  },

  syncNav() {
    document.querySelectorAll('[data-ram-settings-section]').forEach(row => {
      const on = row.getAttribute('data-ram-settings-section') === this.section;
      row.setAttribute('style', row.getAttribute(on ? 'data-ram-on-style' : 'data-ram-off-style') || '');
    });
  },

  async load(iniSection) {
    if (this.values[iniSection]) return this.values[iniSection];

    try {
      this.values[iniSection] = (await RAM.call('settings.describe', { section: iniSection })) || {};
    } catch (error) {
      this.values[iniSection] = {};
      toast(String(error && error.message || error), 'error');
    }

    return this.values[iniSection];
  },

  async render() {
    const host = hook('settings');
    const section = this.sections.find(s => s.name === this.section);

    if (!host || !section) return;

    this.syncNav();

    const stored = await this.load(section.ini);

    host.querySelectorAll('[data-ram-row-setting]').forEach(row => row.remove());

    section.keys.forEach(([key, label, kind]) => {
      const shape = this.shapes[kind] || this.shapes.input;
      if (!shape) return;

      const row = shape.cloneNode(true);
      const known = stored[key];

      row.setAttribute('data-ram-row-setting', key);

      const text = row.firstElementChild;

      if (text && text.children.length >= 2) {
        text.children[0].textContent = label;
        // no comment in the ini means no explanation to show: the row keeps its title only
        text.children[1].textContent = (known && known.comment) || '';
        text.children[1].hidden = !(known && known.comment);
      }

      const control = row.querySelector('[data-ram-setting]');
      control.setAttribute('data-ram-setting', key);
      control.setAttribute('data-ram-ini', section.ini);

      const value = known ? String(known.value ?? '') : '';

      if (kind === 'toggle') {
        this.paintToggle(control, value === 'true');
        control.addEventListener('click', () => this.write(control, control.getAttribute('data-ram-value') !== 'true'));
      } else {
        control.value = value;
        control.addEventListener('change', () => this.write(control, control.value));
        control.addEventListener('keydown', event => { if (event.key === 'Enter') control.blur(); });
      }

      host.appendChild(row);
    });

    I18N.apply(host);
  },

  paintToggle(control, on) {
    control.setAttribute('style', on ? this.style.toggleOn : this.style.toggleOff);
    control.setAttribute('data-ram-value', String(on));

    const knob = control.firstElementChild;
    if (knob) knob.setAttribute('style', on ? this.style.knobOn : this.style.knobOff);
  },

  async write(control, value) {
    const key = control.getAttribute('data-ram-setting');
    const iniSection = control.getAttribute('data-ram-ini');
    const text = typeof value === 'boolean' ? String(value) : String(value);

    try {
      const result = await RAM.call('settings.set', { section: iniSection, key, value: text });

      if (this.values[iniSection] && this.values[iniSection][key]) this.values[iniSection][key].value = text;

      if (typeof value === 'boolean') this.paintToggle(control, value);

      // A setting can be stored and still not be in force — Multi Roblox cannot take the lock once Roblox is
      // open. The switch keeps the new value, because it was saved, but the reason is said out loud.
      if (result && result.warning) toast(t(result.warning), 'error');
      else toast(I18N.fmt('{key} saved', { key }), 'ok');
    } catch (error) {
      toast(String(error && error.message || error), 'error');

      // the app refused it, so the control must not keep showing the new value
      if (typeof value === 'boolean') this.paintToggle(control, !value);
    }
  }
};

// ---------------------------------------------------------------- proxies
//
// There is no proxy store: a proxy exists because an account carries it. The list is therefore the accounts,
// grouped by what they are using, and everything credential-shaped stays on the app's side of the bridge.

const ProxyScreen = {
  rows: [],
  checked: {},        // proxy -> when this page last checked it
  busy: false,

  async open() {
    const host = hook('proxy-rows');
    const template = hook('proxy-row');

    if (!host || !template) return;

    try {
      this.rows = (await RAM.call('proxies.list')) || [];
    } catch (error) {
      return toast(String(error && error.message || error), 'error');
    }

    host.querySelectorAll('[data-ram-proxy-row]').forEach(row => row.remove());

    this.rows.forEach(row => {
      const node = template.content.firstElementChild.cloneNode(true);

      node.dataset.proxy = row.proxy;

      const badge = node.querySelector('span');
      if (badge) badge.textContent = ({ http: 'HTTP', https: 'TLS', socks4: 'S4', socks4a: 'S4A', socks5: 'S5' })[row.scheme] || '?';

      this.fill(node, row);

      host.appendChild(node);
    });

    this.count();
  },

  fill(node, row) {
    const set = (field, text) => {
      const cell = node.querySelector(`[data-ram-field="${field}"]`);
      if (cell) cell.textContent = text;
    };

    set('proxy-host', row.host);
    set('proxy-ip', row.exitIP || 'exit address unknown');
    set('proxy-type', (row.scheme || '').toUpperCase());
    set('proxy-accounts', String(row.accounts));
    set('proxy-checked', this.checked[row.proxy] || '');

    this.paint(node, row.alive === true ? 'Alive' : row.alive === false ? 'Dead' : 'Untested');
  },

  paint(node, word) {
    const cell = node.querySelector('[data-ram-field="proxy-status"]');
    if (!cell) return;

    const dot = cell.querySelector('span');
    const colour = word === 'Alive' ? Style.dotOk : word === 'Dead' ? Style.dotBad : Style.dotIdle;

    if (dot) dot.style.background = colour;

    // The design's status cell is a dot and then a word. Pretty-printed markup leaves whitespace text nodes
    // around the dot, so writing into "the first text node" puts the word in front of it: clear them all and
    // append one.
    Array.from(cell.childNodes).filter(child => child.nodeType === 3).forEach(child => child.remove());

    cell.appendChild(document.createTextNode(t(word)));
    cell.style.color = word === 'Dead' ? Style.dotBad : 'var(--color-neutral-300)';
  },

  count() {
    const label = hook('proxy-count');
    if (!label) return;

    const alive = this.rows.filter(row => row.alive === true).length;

    label.textContent = `${alive} alive · ${this.rows.length} total`;
  },

  async checkAll() {
    if (this.busy) return;

    const nodes = Array.from(document.querySelectorAll('[data-ram-proxy-row]'));

    if (!nodes.length) return toast('No proxies are assigned to any account');

    this.busy = true;

    let alive = 0;

    for (const node of nodes) {
      this.paint(node, 'Checking');

      try {
        const result = await RAM.call('proxies.check', { proxy: node.dataset.proxy });
        const row = this.rows.find(r => r.proxy === node.dataset.proxy);

        if (row) { row.exitIP = result.exitIP; row.alive = result.alive; }

        this.checked[node.dataset.proxy] = 'just now';

        if (row) this.fill(node, row);
        if (result.alive) alive++;
      } catch (error) {
        this.paint(node, 'Dead');
      }

      this.count();
    }

    this.busy = false;

    toast(`${alive} of ${nodes.length} proxies answered`, alive === nodes.length ? 'ok' : 'error');
  },

  lines() {
    const box = hook('proxy-bulk');

    return box ? box.value.split('\n').map(line => line.trim()).filter(Boolean) : [];
  },

  async assign() {
    const accounts = Array.from(State.selected);
    const proxies = this.lines();

    if (!accounts.length) return toast('Tick the accounts to give these proxies to, on the Accounts tab', 'error');

    try {
      const result = await RAM.call('proxies.assign', { accounts, proxies });

      toast(result.cleared
        ? `Proxy removed from ${result.assigned} accounts`
        : `${proxies.length} proxies handed out to ${result.assigned} accounts`, 'ok');

      await this.open();
      await refresh();
    } catch (error) {
      toast(String(error && error.message || error), 'error');
    }
  },

  wire() {
    const box = hook('proxy-bulk');
    const parsed = hook('proxy-parsed');

    if (box && parsed) {
      const recount = () => parsed.textContent = `${this.lines().length} parsed`;

      box.addEventListener('input', recount);
      recount();
    }

    const check = hook('proxy-check-all'); if (check) check.addEventListener('click', () => this.checkAll());
    const give = hook('proxy-assign'); if (give) give.addEventListener('click', () => this.assign());

    const add = hook('proxy-import');
    if (add && box) add.addEventListener('click', () => { box.focus(); box.scrollIntoView({ block: 'nearest' }); });
  }
};

// ---------------------------------------------------------------- the server list
//
// Picking a server is picking a job id: the launcher already knows how to join one, so this screen only has to
// find them. Roblox's public list gives how full each server is, its frames and its id — not where it is, which
// costs a join request per server, so that column stays honest about not knowing.

const ServerScreen = {
  servers: [],
  filter: 'All',
  search: '',
  loaded: false,

  placeId() {
    const place = hook('place-id');

    return place ? place.value.trim() : '';
  },

  async open() {
    await this.games();

    // Opening the screen after changing the place should show that place, not what was loaded last time.
    if (this.loaded !== this.placeId()) await this.load();
  },

  async load() {
    const placeId = this.placeId();
    const host = hook('servers');
    const template = hook('server-row');

    if (!host || !template) return;

    this.loaded = placeId;

    if (!placeId) {
      host.querySelectorAll('[data-ram-server-row]').forEach(row => row.remove());
      this.servers = [];

      this.say('Nothing picked yet — type a place id or a Roblox link in the box on the left, or choose a game under it.');

      return this.count('no game');
    }

    this.say('Loading the servers of this place…');
    this.count('loading…');

    try {
      this.servers = (await RAM.call('servers.list', { placeId }, 30000)) || [];
    } catch (error) {
      this.servers = [];
      toast(String(error && error.message || error), 'error');
      this.say(String(error && error.message || error));
    }

    this.render();

    // the header says what is being shown, since the list itself is only numbers and ids
    try {
      const game = await RAM.call('games.info', { placeId });

      this.say(game && game.known
        ? `${game.name} · ${this.servers.length} servers · click a row to take its job id, or Join to launch the ticked accounts there`
        : `Place ${placeId} — Roblox does not know it`);
    } catch { /* the list is still usable without the name */ }
  },

  say(text) {
    const line = hook('server-subtitle');

    if (line) line.textContent = text;
  },

  matching() {
    return this.servers.filter(server => {
      if (this.filter === 'Has room' && server.playing >= server.maxPlayers) return false;
      if (this.filter === 'Low ping' && !(server.ping > 0 && server.ping < 120)) return false;
      if (this.search && !server.id.startsWith(this.search)) return false;

      return true;
    });
  },

  render() {
    const host = hook('servers');
    const template = hook('server-row');

    if (!host || !template) return;

    const shown = this.matching();

    host.querySelectorAll('[data-ram-server-row]').forEach(row => row.remove());

    shown.forEach(server => {
      const row = template.content.firstElementChild.cloneNode(true);
      const set = (field, text) => {
        const cell = row.querySelector(`[data-ram-field="${field}"]`);
        if (cell) cell.textContent = text;
      };

      const full = server.playing >= server.maxPlayers;

      set('server-fill', `${server.playing} / ${server.maxPlayers}`);
      set('server-region', server.id);
      set('server-job', server.fps ? `${Math.round(Number(server.fps))} fps` : 'public server');
      set('server-ping', server.ping > 0 ? `${server.ping} ms` : '—');

      const bar = row.querySelector('[data-ram-field="server-bar"]');

      if (bar) {
        bar.style.width = `${Math.min(100, Math.round(server.playing / Math.max(1, server.maxPlayers) * 100))}%`;
        bar.style.background = full ? Style.dotBad : 'var(--color-accent-600)';
      }

      const join = row.querySelector('[data-ram-field="server-join"]');

      if (join) join.addEventListener('click', event => {
        event.stopPropagation();

        const job = hook('job-id');
        if (job) job.value = server.id;

        launch();
      });

      row.addEventListener('click', () => {
        const job = hook('job-id');

        if (job) job.value = server.id;

        toast(`Job id ${server.id.slice(0, 8)}… is in the launcher`);
      });

      host.appendChild(row);
    });

    I18N.apply(host);

    this.count(`${shown.length} of ${this.servers.length}`);
  },

  count(text) {
    const label = hook('server-count');

    if (label) label.textContent = text;
  },

  // ---------- the games beside the list
  async games() {
    const favourites = hook('game-favourites');
    const recent = hook('game-recent');
    const template = hook('game-row');

    if (!template) return;

    const [saved, lately] = await Promise.all([
      RAM.call('games.favourites').catch(() => []),
      RAM.call('games.recent').catch(() => [])
    ]);

    this.list(favourites, saved || [], true);
    this.list(recent, lately || [], false);
  },

  list(host, games, favourite) {
    if (!host) return;

    const template = hook('game-row');

    host.querySelectorAll('[data-ram-game-row]').forEach(row => row.remove());

    const wanted = this.search.toLowerCase();

    games
      .filter(game => !wanted || String(game.name).toLowerCase().includes(wanted) || String(game.placeId).startsWith(wanted))
      .forEach(game => {
        const row = template.content.firstElementChild.cloneNode(true);

        row.querySelector('[data-ram-field="game-row-name"]').textContent = game.name;
        row.querySelector('[data-ram-field="game-row-place"]').textContent = String(game.placeId);

        // the design's sample row is the selected one; only the place actually loaded should look that way
        apply(row, String(game.placeId) === this.placeId() ? Style.rowOn : Style.rowOff);

        row.addEventListener('click', () => this.pick(game.placeId));

        // right-clicking pins a game, which is the only way this list grows
        row.addEventListener('contextmenu', async event => {
          event.preventDefault();

          try {
            const result = await RAM.call('games.favourite', { placeId: String(game.placeId), on: !favourite });

            toast(result.favourite ? `${game.name} pinned` : `${game.name} unpinned`, 'ok');

            await this.games();
          } catch (error) { toast(String(error && error.message || error), 'error'); }
        });

        host.appendChild(row);
      });
  },

  async pick(placeId) {
    const place = hook('place-id');

    if (place) { place.value = String(placeId); rememberPlace(); loadGame(); }

    await this.games();          // the pinned/recent lists mark which one is loaded
    await this.load();
  },

  wire() {
    const again = hook('server-refresh');
    if (again) again.addEventListener('click', () => this.load());

    const search = hook('game-search');

    if (search) search.addEventListener('input', () => {
      this.search = search.value.trim();

      // a bare number is a place id, so typing one and pressing enter goes straight there
      this.games();
      this.render();
    });

    if (search) search.addEventListener('keydown', event => {
      if (event.key !== 'Enter') return;

      const typed = placeIdIn(search.value);

      if (typed) { search.value = ''; this.search = ''; this.pick(typed); }
      else toast('Type a place id, or paste a Roblox game link', 'error');
    });

    const screen = document.querySelector('[data-ram-screen="launcher"]');

    // "Private link": a VIP code is just another way to name a server, and the launcher already takes one
    const priv = screen && Array.from(screen.querySelectorAll('button'))
      .find(button => button.textContent.trim() === 'Private link');

    if (priv) priv.addEventListener('click', async () => {
      const vip = hook('vip-code');
      const typed = await Prompt.ask('Private server link or code', vip ? vip.value : '');

      if (typed === null) return;
      if (vip) vip.value = typed;

      toast(typed ? 'The launch will use that private server' : 'Private server cleared');
    });

    // the design's filter pills carry no hook, so they are taken by their words
    if (screen) Array.from(screen.querySelectorAll('div')).forEach(node => {
      const word = node.textContent.trim();

      if (node.children.length || !['All', 'Has room', 'Low ping'].includes(word)) return;

      node.dataset.ramServerFilter = word;

      node.addEventListener('click', () => {
        this.filter = word;
        this.syncFilters();
        this.render();
      });
    });
  },

  syncFilters() {
    document.querySelectorAll('[data-ram-server-filter]').forEach(pill =>
      apply(pill, pill.dataset.ramServerFilter === this.filter ? Style.chipOn : Style.chipOff));
  }
};

// A one-line prompt, since WebView2 blocks window.prompt and the design has no generic one.
const Prompt = {
  ask(title, value) {
    return new Promise(resolve => {
      const back = document.createElement('div');
      back.style.cssText = 'position:fixed;inset:0;z-index:70;display:grid;place-items:center;background:color-mix(in srgb, var(--color-bg) 66%, transparent);backdrop-filter:blur(4px)';

      back.innerHTML = `
        <div style="width:min(420px,92vw);padding:20px;border-radius:var(--radius-lg);background:var(--color-surface);
             border:1px solid var(--color-neutral-800);box-shadow:0 40px 80px -40px #000">
          <div style="font-size:15px;font-weight:500;margin-bottom:12px"></div>
          <input class="input" style="width:100%;height:38px;border-radius:var(--radius-md);font-size:13px">
          <div style="display:flex;gap:8px;justify-content:flex-end;margin-top:14px">
            <button class="btn btn-secondary" data-cancel style="border-radius:var(--radius-md);padding:8px 16px">Cancel</button>
            <button class="btn btn-primary" data-ok style="border-radius:var(--radius-md);padding:8px 16px">Save</button>
          </div>
        </div>`;

      back.querySelector('div > div').textContent = title;

      const input = back.querySelector('input');
      input.value = value || '';

      const done = result => { back.remove(); resolve(result); };

      back.querySelector('[data-ok]').addEventListener('click', () => done(input.value.trim()));
      back.querySelector('[data-cancel]').addEventListener('click', () => done(null));
      back.addEventListener('click', e => { if (e.target === back) done(null); });
      input.addEventListener('keydown', e => {
        if (e.key === 'Enter') done(input.value.trim());
        if (e.key === 'Escape') done(null);
      });

      document.body.appendChild(back);
      setTimeout(() => input.focus(), 50);
    });
  }
};

// ---------------------------------------------------------------- wiring

function wire() {
  [hook('account-search'), hook('global-search')].forEach(input => {
    if (!input) return;
    input.addEventListener('input', () => { State.search = input.value; renderAccounts(); });
  });

  document.querySelectorAll('[data-ram-filter]').forEach(chip => chip.addEventListener('click', () => {
    State.status = chip.getAttribute('data-ram-filter');
    syncChips();
    renderAccounts();
  }));

  document.querySelectorAll('[data-ram-group]').forEach(chip => chip.addEventListener('click', () => {
    State.group = chip.getAttribute('data-ram-group');
    syncChips();
    renderAccounts();
  }));

  const join = hook('join');
  if (join) join.addEventListener('click', () => Batch.running ? Batch.stop() : launch());
  const again = hook('refresh'); if (again) again.addEventListener('click', refresh);

  const place = hook('place-id');
  if (place) {
    let debounce = null;

    place.addEventListener('input', () => {
      clearTimeout(debounce);

      debounce = setTimeout(() => {
        // a pasted link is turned into the id it contains, so the rest of the app never sees a URL
        const found = placeIdIn(place.value);

        if (found && found !== place.value.trim()) place.value = found;

        rememberPlace();
        loadGame();

        if (Screens.current === 'launcher') ServerScreen.load();
      }, 500);
    });
  }

  const min = hook('win-min'); if (min) min.addEventListener('click', () => RAM.call('window.minimize'));
  const max = hook('win-max'); if (max) max.addEventListener('click', () => RAM.call('window.maximize'));
  const close = hook('win-close'); if (close) close.addEventListener('click', () => RAM.call('window.close'));

  RAM.on('accounts.changed', refresh);
  RAM.on('launch.result', result => {
    if (!result) return;

    toast(`${result.username}: ${result.detail}`, result.outcome === 'Joined' ? 'ok' : 'error');
    refresh();
  });
}

document.addEventListener('DOMContentLoaded', async () => {
  wire();
  Security.wire();
  AddAccount.wire();
  RowMenu.wire();
  SettingsScreen.capture();
  ProxyScreen.wire();
  ServerScreen.wire();
  FreeItemsScreen.wire();
  GroupMenu.wire();
  Batch.wire();
  Language.wire();
  Screens.wire();
  syncChips();

  // Before anything is shown: the whole page is translated once here, and each render translates what it added.
  await Language.load();

  // A batch may already be running — the page can be reloaded (a language change does exactly that) while the
  // app is still launching.
  await Batch.sync();

  // Nothing else is meaningful until the store is open, so this runs first.
  await Security.sync();

  await refresh();

  // The launcher opens where it was left, on the setting the old window also uses.
  try {
    const saved = await RAM.call('settings.get', { section: 'General', key: 'SavedPlaceId' });
    const place = hook('place-id');

    if (place && saved && !place.value.trim()) place.value = String(saved).trim();
  } catch { /* an unreadable setting is not worth blocking the interface for */ }

  loadGame();

  // Presence and cookie validity move on the app's own timers; a slow poll keeps the list honest.
  setInterval(refresh, 15000);
});
