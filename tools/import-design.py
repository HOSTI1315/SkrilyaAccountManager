"""
Assembles the shipped ui/index.html out of several frozen snapshots of the designer's prototype.

The prototype is React under a design-composer runtime: its modal, its per-tab panes, its context menu and each
of its screens only exist in the DOM while the matching state is on, so each was captured separately (see
"Re-importing a new export" in ui/README.md for how). This merges them into one static page where every block
is present and hidden, which is what app.js expects.

    python tools/import-design.py <folder with the snapshots> [output index.html]

Needs beautifulsoup4 and lxml. Every line it prints starting with "!!" means the design moved and a hook has
to be re-attached before the page will work.
"""

import io, os, re, sys
from bs4 import BeautifulSoup

HERE = os.path.dirname(os.path.abspath(__file__))

SCRATCH = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, '..', 'RBX Alt Manager', 'ui', 'index.html')

def load(name):
    return BeautifulSoup(io.open(os.path.join(SCRATCH, name), encoding='utf-8').read(), 'lxml')

base = load('dump3-default.html')
cookie = load('dump-add.html')
bulk = load('dump-tab-bulk.html')
login = load('dump-tab-login.html')
menu = load('dump-menu.html')

report = []

def hook(soup, name):
    return soup.select_one('[data-ram="%s"]' % name)

# ---------------------------------------------------------------- head
head = base.head
for tag in head.select('script'):
    tag.decompose()
for tag in head.select('link[rel="stylesheet"]'):
    tag.decompose()
for style in head.select('style'):
    if 'sc-placeholder' in style.get_text():          # the composer's own loading chrome
        style.decompose()

for href in ('ds.css', 'icons/phosphor.css'):
    link = base.new_tag('link', rel='stylesheet', href=href)
    head.insert(0, link)

title = head.find('title') or base.new_tag('title')
title.string = 'SkrilyaAccountManager'
head.append(title)

# The window has no native frame: these rules are what Windows drags the window by, and they are ours, not the
# design's, so they are re-applied on every import.
drag = base.new_tag('style')
drag.string = """
  [data-ram="titlebar"], [data-dc-tpl="7"] { app-region: drag; -webkit-app-region: drag; }
  [data-ram="titlebar"] input, [data-dc-tpl="7"] input,
  [data-ram="win-min"], [data-ram="win-max"], [data-ram="win-close"] { app-region: no-drag; -webkit-app-region: no-drag; }
  [data-ram="win-min"], [data-ram="win-max"], [data-ram="win-close"] { cursor: pointer !important; }
  [data-ram-row] { user-select: none; }
  [hidden] { display: none !important; }
"""
head.append(drag)

# ---------------------------------------------------------------- account row -> template
rows = base.select('[data-ram-row]')
report.append('sample rows in the export: %d' % len(rows))

if rows:
    template = base.new_tag('template')
    template['data-ram'] = 'account-row'
    first = rows[0].extract()
    template.append(first)
    base.body.append(template)          # in the body, not in the list it fills — see the row templates below

    for extra in rows[1:]:
        extra.decompose()

    report.append('row template built, %d sample rows dropped' % (len(rows) - 1))

# ---------------------------------------------------------------- group chips -> template
# The design draws its group chips as draggable divs with no hook of their own — the "All" chip is the only one
# it marks. So the first draggable chip becomes the template and the rest of the samples go; the app renders one
# per real group and keeps the design's own drag affordance.
container = base.select_one('[data-ram="group-chips"]')
samples = [c for c in container.find_all(recursive=False) if c.get('draggable') == 'true'] if container else []

report.append('group chip samples in the export: %d' % len(samples))

if samples:
    template = base.new_tag('template')
    template['data-ram'] = 'group-chip'
    template.append(samples[0].extract())

    for extra in samples[1:]:
        extra.decompose()

    container.append(template)

    report.append('group-chip template built, %d samples dropped' % (len(samples) - 1))
else:
    report.append('!! no draggable group chip found to use as a template')

# ---------------------------------------------------------------- add-account modal
modal = hook(cookie, 'add-modal')

if modal is None:
    report.append('!! add-modal missing from the cookie snapshot')
else:
    modal = modal.extract()
    modal['hidden'] = ''

    # The design ships the submit button disabled; app.js has no "enable when valid" rule and never clears it in
    # markup, so a leftover disabled attribute would leave the whole modal unusable. Strip it on import.
    submit_button = modal.select_one('[data-ram="add-submit"]')

    if submit_button is not None and submit_button.has_attr('disabled'):
        del submit_button['disabled']
        report.append('add-submit re-enabled')

    def pane_of(root, hook_name):
        """The block that holds one tab's controls: walk up until the parent also holds the tab strip."""
        node = root.select_one('[data-ram="%s"]' % hook_name)
        if node is None:
            return None
        while node.parent is not None and not node.parent.select('[data-ram-add-tab]'):
            node = node.parent
        return node

    cookie_pane = pane_of(modal, 'add-cookie')
    bulk_pane = pane_of(bulk, 'add-bulk')
    login_pane = pane_of(login, 'add-login')

    for name, pane in (('cookie', cookie_pane), ('bulk', bulk_pane), ('login', login_pane)):
        if pane is None:
            report.append('!! %s pane not found' % name)
            continue

        pane['data-ram-add-pane'] = name

        if name != 'cookie':
            pane['hidden'] = ''
            cookie_pane.insert_after(pane.extract())


    # The design draws the active tab differently, and only it knows how. Each snapshot has exactly one tab
    # active, so the two variants of every tab can be read straight off them and handed to app.js.
    active_in = {'cookie': modal, 'bulk': bulk, 'login': login}

    for name, soup in active_in.items():
        on = soup.select_one('[data-ram-add-tab="%s"]' % name)
        off = (bulk if name != 'bulk' else modal).select_one('[data-ram-add-tab="%s"]' % name)
        tab = modal.select_one('[data-ram-add-tab="%s"]' % name)

        if tab is None or on is None or off is None:
            report.append('!! tab %s: could not read both states' % name)
            continue

        tab['data-ram-on-style'] = on.get('style', '')
        tab['data-ram-off-style'] = off.get('style', '')

    report.append('tab states captured for: ' + ', '.join(
        t['data-ram-add-tab'] for t in modal.select('[data-ram-on-style]')))

    report.append('add-modal assembled with panes: ' + ', '.join(
        p['data-ram-add-pane'] for p in modal.select('[data-ram-add-pane]')))

    base.body.append(modal)

# ---------------------------------------------------------------- context menu
menu = load('dump-sub2.html')          # the snapshot where all 12 top-level commands had rendered
menu_rows = [r for r in menu.select('[data-ram-menu]') if '-' not in r['data-ram-menu'] or r['data-ram-menu'] in ('launch-here', 'join-job', 'open-profile', 'refresh-one')]
report.append('menu rows captured: %d' % len(menu_rows))

if menu_rows:
    root = menu_rows[0]
    while root.parent is not None and len(root.select('[data-ram-menu]')) < len(menu_rows):
        root = root.parent

    root = root.extract()
    root['data-ram'] = 'row-menu'
    root['hidden'] = ''

    base.body.append(root)

    report.append('menu root captured with %d rows' % len(root.select('[data-ram-menu]')))


# ---------------------------------------------------------------- submenus
# Only the hovered parent keeps its submenu open, so each was captured in its own snapshot.
for parent, prefix in (('copy', 'copy-'), ('set', 'set-'), ('tools', 'tool-')):
    soup = load('dump-sub-%s.html' % parent)
    items = soup.select('[data-ram-menu^="%s"]' % prefix)

    if not items:
        report.append('!! submenu %s: nothing captured' % parent)
        continue

    root = items[0]

    while root.parent is not None and len(root.select('[data-ram-menu^="%s"]' % prefix)) < len(items):
        root = root.parent

    # walking up must stop before the main menu is swallowed
    if root.select_one('[data-ram-menu="launch"]'):
        report.append('!! submenu %s: could not isolate it from the main menu' % parent)
        continue

    root = root.extract()
    root['data-ram-submenu'] = parent
    root['hidden'] = ''

    base.body.append(root)

    report.append('submenu %s: %d items' % (parent, len(items)))

# ---------------------------------------------------------------- screens
# The rail switches whole screens and the tab strip switches panes within the accounts screen. Both are state
# in the prototype, so each screen was frozen on its own; here they become siblings under the one content host,
# all but the accounts screen hidden.
HOST = '[data-dc-tpl="83"]'

host = base.select_one(HOST)

def screen_of(soup, hook_name):
    """The direct child of the content host that holds this hook."""
    node = soup.select_one('[data-ram="%s"]' % hook_name)

    if node is None:
        return None

    while node.parent is not None and node.parent.get('data-dc-tpl') != '83':
        node = node.parent

    return node

accounts_screen = screen_of(base, 'accounts')

if accounts_screen is None:
    report.append('!! accounts screen not found under the content host')
else:
    accounts_screen['data-ram-screen'] = 'accounts'

# the tab strip is the host's other child, and it belongs to the accounts screen only
strip = next((c for c in host.find_all(recursive=False) if c is not accounts_screen), None)

if strip is not None:
    strip['data-ram'] = 'tabstrip'

    launcher_soup = load('dump3-launcher.html')
    on_strip = launcher_soup.select_one(HOST).find_all(recursive=False)[0]

    for name, here, there in zip(('accounts', 'launcher', 'watcher'),
                                 strip.find_all(recursive=False),
                                 on_strip.find_all(recursive=False)):
        here['data-ram-tab'] = name
        # 'accounts' is the active one in this snapshot and 'launcher' in the other, so between the two every
        # tab is seen in both states
        here['data-ram-on-style'] = (here if name == 'accounts' else there).get('style', '')
        here['data-ram-off-style'] = (there if name == 'accounts' else here).get('style', '')

    report.append('tab strip: ' + ', '.join(t['data-ram-tab'] for t in strip.select('[data-ram-tab]')))

for hook_name, screen, source in (('servers', 'launcher', 'dump3-launcher.html'),
                                  ('settings', 'settings', 'sec-client_launch.html'),
                                  ('proxies', 'proxies', 'dump3-proxies.html')):
    soup = load(source)
    root = screen_of(soup, hook_name)

    if root is None:
        report.append('!! screen %s not found in %s' % (screen, source))
        continue

    root = root.extract()
    root['data-ram-screen'] = screen
    root['hidden'] = ''

    host.append(root)

    report.append('screen %s imported from %s' % (screen, source))

# ---------------------------------------------------------------- the rail
# Every destination is one icon with a title; the app needs to know which is which and how the design draws the
# one you are on.
settings_soup = load('sec-client_launch.html')

on_rail = settings_soup.select_one('[title="Settings"]')
off_rail = base.select_one('[title="Settings"]')

anchor = base.select_one('[title="Dashboard"]')

if on_rail is not None and off_rail is not None and anchor is not None:
    # only the rail's own children: the group chips carry a title too, and so does the button below them
    for item in anchor.parent.find_all(recursive=False):
        title = item.get('title')

        if not title or item.find('i') is None:
            continue

        item['data-ram-rail'] = title
        item['data-ram-on-style'] = on_rail.get('style', '')
        item['data-ram-off-style'] = off_rail.get('style', '')

    report.append('rail items: ' + ', '.join(i['data-ram-rail'] for i in base.select('[data-ram-rail]')))
else:
    report.append('!! could not read both rail states')

# ---------------------------------------------------------------- settings sections
# Only the open section's rows exist in the DOM, so each section was frozen separately and becomes a template.
SECTIONS = ['Client launch', 'Client windows', 'Proxies', 'Watching', 'Web server', 'Appearance']

rows_host = base.select_one('[data-ram="settings"]')

if rows_host is None:
    report.append('!! settings rows container missing')
else:
    for section in SECTIONS:
        soup = load('sec-%s.html' % section.replace(' ', '_').lower())
        source_host = soup.select_one('[data-ram="settings"]')

        if source_host is None:
            report.append('!! section %s: no rows container' % section)
            continue

        template = base.new_tag('template')
        template['data-ram-settings-pane'] = section

        for row in list(source_host.find_all(recursive=False)):
            template.append(row.extract())

        rows_host.append(template)

        report.append('section %-16s %d controls' % (section, len(template.select('[data-ram-setting]'))))

    # the container starts empty; the app renders whichever section is open
    for stray in [c for c in rows_host.find_all(recursive=False) if c.name != 'template']:
        stray.decompose()

    # A toggle is a div, so the app has to repaint it by hand. Both states are read off the design rather than
    # written here, so a restyle in a later export carries over on its own.
    windows = load('sec-client_windows.html')
    on_toggle = windows.select_one('[data-ram-setting="MuteClients"]')      # v: true
    off_toggle = windows.select_one('[data-ram-setting="AlwaysOnTop"]')     # v: false

    if on_toggle is not None and off_toggle is not None:
        rows_host['data-ram-toggle-on'] = on_toggle.get('style', '')
        rows_host['data-ram-toggle-off'] = off_toggle.get('style', '')
        rows_host['data-ram-knob-on'] = on_toggle.find('div').get('style', '')
        rows_host['data-ram-knob-off'] = off_toggle.find('div').get('style', '')

        report.append('toggle states captured')
    else:
        report.append('!! could not read both toggle states')

# the section list: one row is active in the snapshot, the rest are not
nav_on = settings_soup.select_one('[data-ram-settings-section="Client launch"]')
nav_off = settings_soup.select_one('[data-ram-settings-section="Appearance"]')

if nav_on is not None and nav_off is not None:
    for row in base.select('[data-ram-settings-section]'):
        row['data-ram-on-style'] = nav_on.get('style', '')
        row['data-ram-off-style'] = nav_off.get('style', '')

    report.append('section nav: %d rows' % len(base.select('[data-ram-settings-section]')))

# ---------------------------------------------------------------- dropdown
# The select popup is the same shape as the row menu: a floating panel of rows, one per option.
select_soup = load('sec-select-open.html')
options = select_soup.select('[data-ram-menu^="opt-"]')

if not options:
    report.append('!! dropdown popup not captured')
else:
    root = options[0]

    while root.parent is not None and len(root.select('[data-ram-menu^="opt-"]')) < len(options):
        root = root.parent

    root = root.extract()
    root['data-ram'] = 'select-menu'
    root['hidden'] = ''

    chosen = root.select_one('[data-ram-menu="opt-Protocol"]')        # the selected one in this snapshot
    other = root.select_one('[data-ram-menu="opt-Player path"]')

    title = root.find('div')
    if title is not None and not title.get('data-ram-menu'):
        title['data-ram'] = 'select-menu-title'

    template = base.new_tag('template')
    template['data-ram'] = 'select-option'
    template['data-ram-on-style'] = chosen.get('style', '')
    template['data-ram-off-style'] = other.get('style', '')
    template['data-ram-tick-on'] = chosen.find('i').get('style', '')
    template['data-ram-tick-off'] = other.find('i').get('style', '')

    for row in list(root.select('[data-ram-menu^="opt-"]'))[1:]:
        row.decompose()

    template.append(root.select_one('[data-ram-menu^="opt-"]').extract())
    root.append(template)

    base.body.append(root)

    report.append('dropdown captured with %d option states' % len(options))

# ---------------------------------------------------------------- proxy and server rows
for hook_name, attr in (('proxies', 'data-ram-proxy-row'),
                        ('servers', 'data-ram-server-row'),
                        ('servers', 'data-ram-game-row')):
    samples = base.select('[%s]' % attr)

    if not samples:
        report.append('!! no sample for %s' % attr)
        continue

    template = base.new_tag('template')
    template['data-ram'] = attr.replace('data-ram-', '')
    template.append(samples[0].extract())

    for extra in samples[1:]:
        extra.decompose()

    # Templates live in the body, never inside the container they fill: emptying that container to redraw it
    # would take the template with it, and every redraw after the first would find nothing to clone.
    base.body.append(template)

    report.append('%s: template built, %d samples dropped' % (attr, len(samples) - 1))

# ---------------------------------------------------------------- the places rows go
# The design marks the panels but not the exact node a row lands in, nor its counters. Those are named here
# rather than found by their sample text in app.js, which would break the moment the design is retranslated.
def name_it(screen, selector, hook_name, pick=None):
    root = base.select_one('[data-ram-screen="%s"]' % screen)
    node = (pick or (lambda r: r.select_one(selector)))(root) if root else None

    if node is None:
        report.append('!! %s: nothing to name %s' % (screen, hook_name))
        return

    node['data-ram'] = hook_name
    report.append('named %s' % hook_name)

def counter(root):
    """The '3 alive - 5 total' style label: the smallest element that holds the whole phrase."""
    for node in root.find_all(True):
        text = node.get_text(strip=True)

        if re.match(r'^\d+ (alive|of)\b', text) and not node.find(True):
            return node

    return None

# Parts of a row the design shows but does not mark: the fill bar, the job id under the region, and the two
# lines of a game row. Named by position within the template, once, here.
def mark(template_name, finder, hook_name):
    template = base.select_one('template[data-ram="%s"]' % template_name)
    node = finder(template) if template else None

    if node is None:
        report.append('!! %s: could not find %s' % (template_name, hook_name))
        return

    node['data-ram-field'] = hook_name
    report.append('marked %s in %s' % (hook_name, template_name))

def after(root, field):
    """The element following the one carrying this field."""
    node = root.select_one('[data-ram-field="%s"]' % field)

    return node.find_next_sibling() if node else None

mark('server-row', lambda t: (lambda track: track.find('div') if track else None)(after(t, 'server-fill')), 'server-bar')
mark('server-row', lambda t: after(t, 'server-region'), 'server-job')
mark('game-row', lambda t: t.select('div > div > div')[0] if t.select('div > div > div') else None, 'game-row-name')
mark('game-row', lambda t: t.select('div > div > div')[1] if len(t.select('div > div > div')) > 1 else None, 'game-row-place')

# The design ships a sample place id in the launcher box. It is not a real place, so it would sit there
# reading "Unknown place" until the user cleared it; the app fills the box from the setting the old window
# uses instead.
sample = base.select_one('[data-ram="place-id"]')

if sample is not None:
    sample['value'] = ''
    sample['placeholder'] = 'Place ID or Roblox link'
    report.append('sample place id cleared')
else:
    report.append('!! place-id input not found')

def parsed(root):
    """The '0 parsed' badge beside the paste box."""
    return next((node for node in root.find_all(True)
                 if re.match(r'^\d+ parsed$', node.get_text(strip=True)) and not node.find(True)), None)

def subtitle(root):
    """The line under a screen's title: the app replaces it with what is actually being shown."""
    return next((node for node in root.find_all('div')
                 if node.get_text(strip=True).startswith('Pick a server') and len(node.find_all()) < 2), None)

name_it('launcher', None, 'server-subtitle', subtitle)
name_it('proxies', 'tbody', 'proxy-rows')
name_it('proxies', None, 'proxy-parsed', parsed)
name_it('proxies', None, 'proxy-count', counter)
name_it('launcher', None, 'server-count', counter)

# ---------------------------------------------------------------- scripts
# i18n before app.js: app.js calls t() while it renders.
for name in ('bridge.js', 'i18n.js', 'app.js'):
    tag = base.new_tag('script', src=name)
    base.body.append(tag)

html = str(base)
io.open(OUT, 'w', encoding='utf-8').write(html)

report.append('written: %d KB -> %s' % (len(html) / 1024, OUT))

# ---------------------------------------------------------------- sanity
checks = {
    'security modal': 'data-ram="security"',
    'toast': 'data-ram="toast"',
    'row template': 'data-ram="account-row"',
    'add modal': 'data-ram="add-modal"',
    'row menu': 'data-ram="row-menu"',
    'screens': 'data-ram-screen=',
    'settings panes': 'data-ram-settings-pane=',
    'settings controls': 'data-ram-setting=',
    'rail items': 'data-ram-rail=',
    'select menu': 'data-ram="select-menu"',
    'proxy row': 'data-ram="proxy-row"',
    'server row': 'data-ram="server-row"',
    'game row': 'data-ram="game-row"',
    'hidden rule': '[hidden] { display: none !important; }',
    'no composer script': 'support.js',
    'no cdn': 'unpkg.com',
}
for label, needle in checks.items():
    report.append('%-22s %s' % (label, html.count(needle)))

print('\n'.join(report))
