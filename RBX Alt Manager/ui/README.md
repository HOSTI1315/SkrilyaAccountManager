# The interface

Everything the user sees lives here. The app serves this folder at `https://ram.local/` inside a WebView2
window, so it behaves like an ordinary web page: relative paths, `fetch`, ES modules and dev tools all work.

Current skin: **Nocturne**, exported from the designer's tool. `index.html`, `ds.css` and `icons/` come from
that export. `bridge.js` and `app.js` are ours and survive every re-import.

## Files

| File | Owner | What it is |
|---|---|---|
| `index.html` | design (+ our hooks) | the page, with `data-ram` attributes added |
| `ds.css` | design | the design system's tokens and component styles |
| `icons/` | design | Phosphor icon font, vendored locally (no CDN) |
| `bridge.js` | app | transport to C#. Never edited when the design changes |
| `app.js` | app | fills the design with real data. Depends only on the hooks below |

## Taking a new export from the designer

The export is a `.dc.html` that renders through the designer tool's own runtime (`<sc-for>`, `<sc-if>`,
`support.js`). That runtime must not ship inside the app, so the page is frozen once, after it has finished
templating:

1. Open the export in a browser, let it settle, and save `document.documentElement.outerHTML`
   (the app's repo has a throwaway tool for this — see the git history of `scratchpad/domdump`, or use the
   browser console).
2. Keep the design's `<style>` blocks, drop `support.js`, `_ds_bundle.js` and the placeholder CSS.
3. Point the stylesheet links at `ds.css` and `icons/phosphor.css` (never at a CDN — the shell blocks
   external requests).
4. Re-apply the `data-ram` hooks. The export keeps stable `data-dc-tpl="N"` numbers on every element, so the
   hooks can be re-attached by number rather than by hand.
5. Reduce the repeated sample rows to one and wrap it in `<template data-ram="account-row">`.

Nothing in this list needs a rebuild of the app: the folder is copied next to the executable and read at run time.

## Hooks

| Attribute | Goes on | What it does |
|---|---|---|
| `data-ram="accounts"` | container | account rows are appended here |
| `data-ram="account-row"` | `<template>` | markup for one row |
| `data-ram-field="username"` | in a row | account name (alias when set) |
| `data-ram-field="userId"` | in a row | numeric id |
| `data-ram-field="avatar"` | in a row | first letter of the name |
| `data-ram-field="presence"` | in a row | Offline / Online / place name / Signed out |
| `data-ram-field="robux"` | in a row | `R$ n` |
| `data-ram-field="proxy"` | in a row | shown only when a proxy is assigned; full value in `title` |
| `data-ram-field="status"` | in a row | label next to the dot |
| `data-ram-field="dot"` | in a row | status dot; its `background` is set |
| `data-ram-field="check"` / `"check-icon"` | in a row | selection box and its tick |
| `data-ram="account-search"` / `"global-search"` | `<input>` | filter as you type |
| `data-ram-filter="all\|valid\|invalid\|banned"` | buttons | status filter |
| `data-ram="group-chips"` + `data-ram="group-chip"` | container + `<template>` | one chip per account group |
| `data-ram-group="<name>"` | a chip | static chip for that group (the design's "All") |
| `data-ram="account-count"` | any | "2 / 3 valid" |
| `data-ram="selected-count"` | any | "3 shown" / "2 selected" |
| `data-ram="place-id"` / `"job-id"` / `"vip-code"` | `<input>` | launch parameters |
| `data-ram="join"` | button | launches the selected accounts |
| `data-ram="refresh"` | button | reloads the list |
| `data-ram="game-name"` / `"game-place"` | any | filled from the place id |
| `data-ram="game-online"` / `"game-favorites"` / `"game-likes"` | any | live counts for that place |
| `data-ram="launch-hint"` | any | "Launches 2 selected accounts" |
| `data-ram="toast"` | any | transient messages |
| `data-ram="win-min"` / `"win-max"` / `"win-close"` | any | window buttons |

Selection: click selects, ctrl-click adds, double-click launches that one.

**Bulk actions.** Opening the row menu on a row inside the selection makes every command act on the whole
selection (`RowMenu.targets()`); opening it outside makes that row the selection first, so it still reads as
one. The menu header says "N accounts selected" whenever it is more than one — otherwise "Remove" reads as
"remove this one" right up until twenty accounts disappear.

Copy, remove and set-group take an `accounts` array so the store is written once instead of once per account;
revalidate is looped deliberately, one request at a time, because it hits Roblox. **Alias and description are
never bulk-applied** — they are what tells two accounts apart, so giving twenty the same one would destroy
exactly the information they carry.

The row menu (right-click) reaches the app through these, beyond the copy/set commands:

```js
await RAM.call('accounts.openBrowser',   { username, url });   // every Tools command; empty url = Roblox home
await RAM.call('accounts.follow',        { username, target }); // target is a USERNAME; the app resolves it
await RAM.call('accounts.signOutOthers', { username });         // rotates the cookie, so the store is saved
```

`join-job` calls `accounts.launch` directly rather than filling the Job ID box and reusing `launch()`: a
private server is `VIP:<code>`, which is a different parameter (`joinVip`), and `launch()` collapses the two.
`accounts.follow` takes a username because `JoinServer` carries the target's **user id in its placeId
parameter** when following — that oddity is kept on the app's side of the bridge on purpose.

## Language

`i18n.js` holds the dictionaries, keyed by the **English source text** rather than by invented ids — the page is
re-imported from a designer's export regularly, and any id sprinkled into that markup would be lost on the next
import, while the English text is what the export actually contains. Anything without a translation simply stays
in English instead of showing a raw key.

```js
t('Refresh')                       // one string
I18N.fmt('{n} selected', { n })    // a translation may move the number
I18N.status('Valid')               // a row status, which needs a different word from the filter pill
I18N.apply(root)                   // translates the design's own markup, text and placeholder/title attributes
```

`apply()` replaces exact matches only, so account names, game names and other user data can never be hit, and
running it twice is harmless. Call it after any render that inserts English text. Attributes the app routes by —
`data-ram-rail`, `data-ram-settings-section` — stay English on purpose; only their visible text is translated.

The chip in the title bar is the picker. It is stamped `data-ram="lang-chip"` at wire time because the obvious
lookups stop working once the page is translated: it is found by its English `title`, and `apply()` rewrites
titles. Choosing a language writes `General/UiLanguage` and reloads the page, so nothing is left half-rendered
in the previous language. The same `UiLanguage` setting is also applied to caption text in the legacy WinForms
window through `LegacyI18n`; text boxes and account data are deliberately never translated.

Setting descriptions come from the ini comments, so their Russian lives in the dictionary keyed by the English
comment: change a comment in `AccountManager.cs` and the dictionary key must change with it.

## Screens

The rail switches whole screens and the tab strip switches panes of the accounts screen. In the export these
were separate states of one component; here they are siblings, all but one hidden.

| Attribute | Goes on | What it does |
|---|---|---|
| `data-ram-screen="accounts\|launcher\|settings\|proxies\|watcher\|dashboard\|relauncher\|macros\|resources\|freeitems"` | a block | one screen; the app hides the rest |
| `data-ram="tabstrip"` + `data-ram-tab="accounts\|launcher\|watcher"` | the pills | hidden while a rail-only screen is up |
| `data-ram-rail="<title>"` | a rail icon | its destination, taken from the icon's own `title` |
| `data-ram-on-style` / `data-ram-off-style` | tabs, rail, section rows | the design's own two states, read at import |

The original export did not include operational layouts for Dashboard, Relauncher, Watcher, Macros, Resources
or Free items. `app.js` now builds those screens from the existing design tokens: Dashboard shows live health,
Relauncher/Watcher expose recovery diagnostics, Macros hosts Anti-AFK and botting, and Resources contains Job
limits, profile cleanup, version management and the optional BloxGen client.

`<template>` elements always live in `<body>`, never inside the container they fill — emptying that container
to redraw it would take the template with it, and every redraw after the first would find nothing to clone.

## Launching

One account is a single call. **Several is a batch, and the app runs it** — the page must not loop, because a
loop has no pause between launches, and a dozen accounts asking Roblox for a dozen tickets from one address in
a second is what gets that address rate limited.

```js
await RAM.call('accounts.launch',      { username, placeId, jobId, joinVip });   // one, answers when it started
await RAM.call('accounts.launchBatch', { accounts, placeId, jobId, joinVip });   // many, returns at once
await RAM.call('accounts.cancelLaunch');
await RAM.call('accounts.launchState');                                          // { running, started, failed, total }
RAM.on('launch.progress', s => s.username + ' ' + s.state);
RAM.on('launch.batch',    s => s.cancelled);
```

The pause between launches is **per exit address, not global**: accounts are grouped by the proxy they will use
(everything direct shares one group), each group is paced on its own with `AccountJoinDelay`, and the groups run
side by side. Ten accounts on ten proxies therefore start almost together — making them queue behind each other
would buy nothing, since the pause exists to protect one address.

While a batch runs the Join button becomes Stop. `Batch.sync()` asks `accounts.launchState` at boot, because the
batch outlives the page: changing the language reloads it, and the shell can be closed and reopened.

## Free items

Collecting free catalog items has been in the app for a while (`Classes/FreeItems.cs`); this screen is the way
to reach it without the old window. The run is minutes long, so the call only starts it and the page follows
along through events.

```js
await RAM.call('freeitems.start', { accounts });   // the ticked accounts; returns at once
await RAM.call('freeitems.stop');
await RAM.call('freeitems.state');                 // { running, stage, accounts, lines } — for a page opened mid-run
RAM.on('freeitems.progress', d => d.line);
RAM.on('freeitems.done',     d => d.error || `${d.collected} collected, ${d.skipped} owned, ${d.failed} failed`);
```

The design never drew this screen, so `app.js` builds it (`FreeItemsScreen.build`) using the design's tokens
rather than invented colours. Two rules that matter for any screen built this way: build it in `wire()`, not on
first open — `Screens.show()` sets `hidden` on every `[data-ram-screen]` before calling `open()`, so a block
created later is created hidden and never appears — and give it `data-ram-screen` plus an entry in
`Screens.destinations` keyed by the rail item's `title`.

## Settings

The design ships six invented sections; the page renders the manager's real ones. `app.js` holds the list of
keys and their labels, and every explanation under a switch is the comment written beside that key in
`RAMSettings.ini` — one source, not two.

```js
await RAM.call('settings.describe', { section: 'General' });   // { key: { value, comment }, … }
await RAM.call('settings.set', { section: 'WebServer', key: 'WebServerPort', value: '7963' });
```

Hooks: `settings` (the rows go here), `data-ram-settings-pane="<name>"` (a `<template>` per section of the
design's own rows, used as a parts bin for the three control shapes), `data-ram-settings-section="<name>"`,
`data-ram-setting="<key>"`, `data-ram-ini="<section>"`, and on the rows container `data-ram-toggle-on/off` and
`data-ram-knob-on/off` — a toggle is a `div`, so the app repaints it with the design's own two styles.

The design's "Reset section" button is hidden: the manager keeps no defaults to go back to.

## Proxies

There is no proxy store. A proxy exists because an account carries it, so the list is the accounts, grouped.
Credentials never cross the bridge — the page sees `scheme://user:***@host:port` and the app does the checking.

```js
await RAM.call('proxies.list');                                  // [{ proxy, scheme, host, accounts, exitIP, alive }]
await RAM.call('proxies.check',  { proxy });                     // re-reads the exit address, writes it back
await RAM.call('proxies.assign', { accounts, proxies });         // round-robin; an empty list clears
```

Hooks: `proxies`, `proxy-rows` (the tbody), `proxy-row` (`<template>`), fields `proxy-host`, `proxy-ip`,
`proxy-type`, `proxy-accounts`, `proxy-status`, `proxy-checked`, plus `proxy-check-all`, `proxy-import`,
`proxy-bulk`, `proxy-parsed`, `proxy-assign`, `proxy-count`.

`alive` is three-valued: never checked is not the same as dead, and the status column says Untested.

## The server list

Picking a server is picking a job id, which the launcher already knows how to join.

```js
await RAM.call('servers.list', { placeId });                     // [{ id, playing, maxPlayers, ping, fps }]
await RAM.call('games.recent');                                  // what you have launched lately
await RAM.call('games.favourites');                              // pinned; right-clicking a game toggles it
await RAM.call('games.favourite', { placeId, on });
```

Hooks: `servers`, `server-row` (`<template>`), fields `server-fill`, `server-bar`, `server-region`,
`server-job`, `server-ping`, `server-join`; `server-refresh`, `server-count`; `game-search`,
`game-favourites`, `game-recent`, `game-row` (`<template>`) with fields `game-row-name`, `game-row-place`.

Roblox's public list does not say where a server is — that costs a join request per server, which is why the
old window only does it on demand. The region line therefore shows the job id, and the line under it the
server's frames. Ping is shown only when Roblox reports one; `—` means unmeasured, not fast.

The window has no native frame, so the header is the title bar: it carries `app-region: drag`, and the inputs
and window buttons carry `app-region: no-drag`. Keep those rules when re-importing, or the window stops moving.

## First run and unlocking

The account store is protected either by this Windows account (DPAPI) or by a password, and until it is open
there is nothing to show. That flow is a modal in this page, driven by four methods:

```js
await RAM.call('security.state');                          // { step: Ready | ChooseProtection | SetPassword | Unlock }
await RAM.call('security.useDefault');                     // this Windows account only
await RAM.call('security.choosePassword');                 // move on to setting a password
await RAM.call('security.setPassword', { password });      // confirmation is done in the page, sent once
await RAM.call('security.unlock',      { password });      // throws with a reason when wrong
RAM.on('security.changed', () => {});                      // the old window changed it
```

Hooks: `security` (the backdrop), `security-title`, `security-text`, `security-choose`,
`security-default`, `security-choose-password`, `security-password`, `security-input`,
`security-confirm`, `security-submit`, `security-error`.

One rule this depends on: the design styles nearly everything with an inline `display:flex`, which outranks
the browser's own rule for `[hidden]`. The page therefore carries `[hidden] { display: none !important; }` —
without it, hiding a step of this dialog does nothing.

## Adding accounts

Three routes, all validated by the app the same way the old window does it:

```js
await RAM.call('accounts.add',       { cookie, group });   // one .ROBLOSECURITY
await RAM.call('accounts.addBulk',   { text, group });     // a pasted pile; returns { found, added, failed }
await RAM.call('accounts.addBrowser');                     // opens the sign-in browser; the account appears when done
await RAM.call('accounts.remove',    { username });
```

Hooks: `add-account` (opens), `add-modal`, `add-close`, `data-ram-add-tab="cookie|bulk|login"`,
`data-ram-add-pane="cookie|bulk|login"`, `add-cookie`, `add-bulk`, `add-bulk-count`,
`data-ram-add-group="<name>"`, `add-login`, `add-submit`, `add-status`, `add-status-text`.

The "N recognised" counter is computed in the page (same pattern the app uses: `_|WARNING:\S+`), so it updates
per keystroke without a round trip. Bulk import is deliberately slow — every cookie is checked against Roblox
with a delay, and rate-limited ones are retried — so the call is given a 15 minute budget.

## Re-importing a new export

The export is a React prototype under the composer's runtime, and its conditional blocks (the add-account
modal, each of its tabs, the row menu) exist in the DOM **only while that state is on**. A single snapshot
therefore misses them. The recipe that works:

1. Freeze the default page (that gives the layout, the rows, the security dialog and the toast).
2. Flip the prop default in a copy of the .dc.html — `"showAddModal":{"default":true}` — and freeze again to
   capture the modal. Clicking the button does not work headlessly: the modal opens from component state.
3. For each tab, call the tab element's React `onClick` (`__reactProps$…`) and freeze once per tab.
4. For the row menu, dispatch a real `contextmenu` event on `[data-ram-row]` and freeze.
5. Merge: one row becomes `<template data-ram="account-row">`, the panes are collected into one modal and
   marked `data-ram-add-pane`, and the menu is appended hidden.

Each tab's active and inactive inline styles are captured during the merge into `data-ram-on-style` /
`data-ram-off-style`, so the page keeps the design's own idea of what an active tab looks like.

The same applies to whole screens: the rail's destinations, each settings section, and the open dropdown all
need their own snapshot, driven the same way (`__reactProps$…`'s `onClick` on the rail item, the section row,
or the select). The merge is `tools/import-design.py`:

```
python tools/import-design.py <folder with the snapshots>
```

It prints what it found; every line starting with `!!` means the design moved and a hook needs re-attaching.
The snapshot file names it expects are listed at the top of the script.

One trap when checking the result: a headless render with `--virtual-time-budget` does not advance CSS
transitions, and the design animates `background-color` on every `div`. Anything the page restyles from script
photographs in its *old* colour. Use WebView2 (which is what ships) to check state, and treat those renders as
layout checks only.

## Groups

`accounts.groups` returns objects, not strings: `{ name, title, accounts }`. `name` is what accounts store,
`title` is what to show — a leading number is an ordering prefix and is hidden, the way the old window does it
("001 Farm" reads as "Farm"). Never write `title` back to an account.

```js
await RAM.call('groups.reorder', { order: ['Default', '001 Farm'] });  // raw names, in the new order
await RAM.call('groups.defaultGroup');                                 // reads where new accounts go
await RAM.call('groups.defaultGroup', { name: '001 Farm' });           // sets it
await RAM.call('groups.rename', { from: '001 Farm', to: '002 Farming' });
await RAM.call('groups.delete', { name: '001 Farm', into: 'Default' });
```

A group is **not an object anywhere** — it is a string carried by each account, plus two settings (`GroupOrder`,
`DefaultGroup`). Three consequences the page has to respect: renaming rewrites every account that carries the
name *and* both settings; deleting cannot delete the accounts, so they move to another group and the page says
which; and a group with no accounts is not stored at all, so "+ New group" asks for a name and moves the ticked
accounts into it rather than creating an empty chip that would vanish on the next refresh.

Right-clicking a chip opens rename / delete / show-only / make-default. The design's chip promises that in its
tooltip but ships no menu, so `GroupMenu` builds one from the design's tokens.

The chips are the design's own draggable ones: the first is taken as `<template data-ram="group-chip">` on
import, and the app renders one per group. Dropping one chip on another saves the new order.

## Calling the app directly

```js
const accounts = await RAM.call('accounts.list');            // array
await RAM.call('accounts.launch', { username, placeId });    // throws on failure
await RAM.call('settings.set', { section: 'General', key: 'DirectLaunch', value: 'true' });
RAM.on('launch.result', r => console.log(r.username, r.outcome, r.detail));
```

`await RAM.call('app.info')` returns the full list of available methods.

Opening `index.html` straight in a browser works too — the bridge sees no host, calls resolve empty, and the
page renders its empty state. Good enough for styling work without running the app.

## Where the app looks for this folder

Next to the executable, in `ui`. To point it at a working copy instead, set `UiFolder` in the `[General]`
section of `RAMSettings.ini` to an absolute path.
