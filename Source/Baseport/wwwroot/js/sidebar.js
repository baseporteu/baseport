/* one sidebar shell, contents per section; add new nav items here rather than growing their own inner nav */

const SIDEBARS = {
  tables: {
    title: 'Tables',
    empty: 'No tables yet.',
    add: () => {
      const wrap = ui.el('div', 'nav-add');
      wrap.innerHTML = `<input type='text' id='tableName' class='input' placeholder='New table name'>
                        <div class='nav-add-btn'>
                            <button class='btn btn-outline btn-sm' id='createTableBtn' onclick='createTable()' title='Create table'>+</button>
                            <button class='btn btn-outline btn-sm' onclick='toggleCreateMenu(event)' title='More create options'>&#9662;</button>
                            <div id='createMenu' class='create-menu hidden'>
                                <button type='button' onclick='createTable()'>Create empty table</button>
                                <button type='button' onclick='openProxySheet()'>Proxy from OpenAPI…</button>
                            </div>
                        </div>`;
      return wrap;
    },
    items: () =>
      [
        {
          label: 'All tables',
          pinned: true,
          active: !currentTablePublicId,
          onSelect: () => navigate('/tables'),
        },
      ].concat(
        [...currentTables]
          .sort((a, b) => a.name.localeCompare(b.name))
          .map((t) => ({
            label: t.name,
            badge: t.isProxy ? 'proxy' : null,
            active: t.id === currentTablePublicId,
            onSelect: () => navigate(`/tables/${t.id}`),
          })),
      ),
  },

  forms: {
    title: 'Forms',
    empty: 'No forms yet.',
    items: () =>
      [
        {
          label: 'All forms',
          pinned: true,
          active: !formEditingId && routePath() === '/forms',
          onSelect: () => navigate('/forms'),
        },
      ].concat(
        (typeof formsAll === 'undefined' ? [] : formsAll)
          .slice()
          .sort((a, b) => (a.title || '').localeCompare(b.title || ''))
          .map((f) => ({
            label: f.title || 'Untitled form',
            badge: f.kind === 'list' ? 'List' : 'Form',
            active: f.id === formEditingId,
            onSelect: () => navigate(`/forms/${f.id}`),
          })),
      ),
  },

  sql: {
    title: 'Queries',
    empty: 'No saved queries yet.',
    items: () =>
      [
        {
          label: 'All queries',
          pinned: true,
          active: !currentQueryId,
          onSelect: () => navigate('/sql'),
        },
      ].concat(
        savedQueries.map((q) => ({
          label: q.name,
          sub: q.lastExecutedAt ? `Executed ${new Date(q.lastExecutedAt).toLocaleDateString()}` : null,
          active: q.id === currentQueryId,
          onSelect: () => navigate(`/sql/${q.id}`),
          actions: [
            { icon: pencilIcon, title: 'Rename', run: () => renameQuery(q) },
            { icon: trashIcon, title: 'Delete', danger: true, run: () => deleteQuery(q) },
          ],
        })),
      ),
  },

  settings: {
    title: 'Settings',
    items: () =>
      [
        ['host', 'Host'],
        ['auth', 'Authentication'],
        ['sites', 'Sites'],
        ['jobs', 'Jobs'],
        ['backups', 'Backups'],
      ].map(([page, label]) => ({
        label,
        active: settingsCurrentPage === page,
        onSelect: () => navigate(`/settings/${page}`),
      })),
  },
};

function renderSidebar(section) {
  const spec = SIDEBARS[section];
  const sidebar = document.getElementById('sidebar');
  sidebar.hidden = !spec;
  if (!spec) return;

  document.getElementById('sidebarTitle').innerText = spec.title;

  const action = document.getElementById('sidebarAction');
  action.innerHTML = '';
  if (spec.action) action.append(spec.action());

  const add = document.getElementById('sidebarAdd');
  add.innerHTML = '';
  if (spec.add) add.append(spec.add());

  const items = spec.items ? spec.items() : [];
  const list = document.getElementById('sidebarList');
  list.innerHTML = '';
  items.forEach((item) => list.append(sidebarItem(item)));

  const empty = document.getElementById('sidebarEmpty');
  empty.classList.toggle('hidden', items.length > 0 || !spec.empty);
  empty.innerText = spec.empty || '';
}

function sidebarItem({ label, sub, badge, active, onSelect, actions, pinned }) {
  const li = ui.el('li', 'nav-item' + (pinned ? ' nav-item-pinned' : ''));

  const button = ui.el('button', 'nav-link' + (active ? ' active' : ''), { type: 'button' });
  button.append(ui.el('span', 'nav-link-text', { textContent: label }));
  if (badge) button.append(ui.el('span', 'tag', { textContent: badge }));
  if (sub) button.append(ui.el('small', 'nav-link-sub', { textContent: sub }));
  if (onSelect) button.onclick = onSelect;
  li.append(button);

  (actions || []).forEach((a) => {
    const b = ui.el('button', 'icon-btn' + (a.danger ? ' danger' : ''), { type: 'button', title: a.title });
    b.innerHTML = a.icon;
    b.onclick = (ev) => {
      ev.stopPropagation();
      a.run();
    };
    li.append(b);
  });
  return li;
}

// Repaints the sidebar when the section it is showing owns the changed data.
function refreshSidebar(section) {
  if (currentSection === section) renderSidebar(section);
}

function toggleRail() {
  const rail = document.getElementById('rail');
  rail.classList.toggle('expanded');
  try {
    localStorage.setItem('baserowRailExpanded', rail.classList.contains('expanded') ? '1' : '0');
  } catch (e) {}
}
function applyRailState() {
  const rail = document.getElementById('rail');
  if (!rail) return;
  let expanded = false;
  try {
    expanded = localStorage.getItem('baserowRailExpanded') === '1';
  } catch (e) {}
  rail.classList.toggle('expanded', expanded);
}
