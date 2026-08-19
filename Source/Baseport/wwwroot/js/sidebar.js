/* one shell: the section rail in the sidebar and the per-section subbar above the content share it. Section buttons come from SECTIONS, subbar contents from SIDEBARS. */

const SECTION_ICONS = {
    tables: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><rect x='3' y='3' width='18' height='18' rx='2'/><path d='M3 9h18M9 21V9'/></svg>",
    forms: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><rect x='4' y='3' width='16' height='18' rx='2'/><path d='M8 8h8M8 12h8M8 16h4'/></svg>",
    sql: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><path d='M17 3a2.8 2.8 0 1 0 0 5.6 2.8 2.8 0 1 0 0-5.6M3 21l9-9M12.2 6.3 11 5l-3.5 3.5 1.2 1.2z'/><path d='M5 3l1.5 1.5M5 3 3.5 4.5M12.8 17.3 14 18.6l3.5-3.5-1.2-1.2zM14.5 12.5h2M17 21l-1.5-1.5M17 21l1.5-1.5'/></svg>",
    schema: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><circle cx='12' cy='5' r='2'/><circle cx='5' cy='19' r='2'/><circle cx='19' cy='19' r='2'/><path d='M12 7v6M5 17l2.5-4M19 17l-2.5-4M12 13l-4.5 4M12 13l4.5 4'/></svg>",
    auth: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><path d='M5 7a4 4 0 1 0 8 0 4 4 0 1 0-8 0M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2M16 3.13a4 4 0 0 1 0 7.75M21 21v-2a4 4 0 0 0-3-3.85'/></svg>",
    logs: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><path d='m4 16 6-7 5 5 5-6'/><path d='M15 14a1 1 0 1 0 2 0 1 1 0 1 0-2 0M9 9a1 1 0 1 0 2 0 1 1 0 1 0-2 0M3 16a1 1 0 1 0 2 0 1 1 0 1 0-2 0M19 8a1 1 0 1 0 2 0 1 1 0 1 0-2 0'/></svg>",
    settings: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h.01a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/></svg>",
};

const OBJECT_ICONS = {
    folder: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><path d='M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.7-.9L9.6 3.9A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'/></svg>",
    table: SECTION_ICONS.tables,
    form: SECTION_ICONS.forms,
    // Deliberately not a bordered rect like the form icon: with a text badge gone, this is the only
    // thing telling a form and a list apart at a glance, so it needs its own silhouette, not a near-twin.
    list: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><circle cx='4' cy='6' r='1' fill='currentColor' stroke='none'/><circle cx='4' cy='12' r='1' fill='currentColor' stroke='none'/><circle cx='4' cy='18' r='1' fill='currentColor' stroke='none'/><path d='M9 6h11M9 12h11M9 18h11'/></svg>",
    query: SECTION_ICONS.sql,
};

const SECTIONS = [
    ['tables', 'Tables'],
    ['forms', 'Forms'],
    ['sql', 'Query'],
    ['schema', 'Schema'],
    ['auth', 'Users'],
    ['logs', 'Logs'],
    ['settings', 'Settings'],
];

const SIDEBARS = {
    tables: {
        group: 'Tables',
        items: () => [{
            label: 'Show all',
            icon: OBJECT_ICONS.folder,
            root: true,
            active: !currentTablePublicId,
            onSelect: () => navigate('/tables'),
        }, ].concat(
            [...currentTables]
            .sort((a, b) => a.name.localeCompare(b.name))
            .map((t) => ({
                label: t.name,
                icon: OBJECT_ICONS.table,
                badge: t.isProxy ? 'proxy' : null,
                active: t.id === currentTablePublicId,
                onSelect: () => navigate(`/tables/${t.id}`),
            })),
        ),
    },

    forms: {
        group: 'Forms',
        items: () => {
            const forms = (typeof formsAll === 'undefined' ? [] : formsAll).slice()
                .sort((a, b) => (a.title || '').localeCompare(b.title || ''));
            return [{
                label: 'Show all',
                icon: OBJECT_ICONS.folder,
                root: true,
                active: !formEditingId && routePath() === '/forms',
                onSelect: () => navigate('/forms'),
            }].concat(forms.map((f) => ({
                label: f.title || 'Untitled form',
                // The icon alone carries form-vs-list now; a text tag saying the same thing next to it was redundant.
                icon: f.kind === 'list' ? OBJECT_ICONS.list : OBJECT_ICONS.form,
                active: f.id === formEditingId,
                onSelect: () => navigate(`/forms/${f.id}`),
            })));
        },
    },

    sql: {
        group: 'Saved queries',
        items: () => [{
            label: 'Show all',
            icon: OBJECT_ICONS.folder,
            root: true,
            active: !currentQueryId,
            onSelect: () => navigate('/sql'),
        }, ].concat(
            savedQueries.map((q) => ({
                label: q.name,
                icon: OBJECT_ICONS.query,
                active: q.id === currentQueryId,
                onSelect: () => navigate(`/sql/${q.id}`),
            })),
        ),
    },

    settings: {
        items: () => [
            ['host', 'Host'],
            ['auth', 'Authentication'],
            ['providers', 'Providers'],
            ['sites', 'Sites'],
            ['jobs', 'Jobs'],
            ['backups', 'Backups'],
        ].map(([page, label]) => ({
            label,
            active: settingsCurrentPage === page,
            onSelect: () => navigate(`/settings/${page}`),
        })),
    },

    schema: { items: () => [] },
    auth: { items: () => [] },
    logs: { items: () => [] },
};

// Section buttons render once; the rail and the topbar toggle both need them.
function renderSectionNav() {
    const nav = document.getElementById('sectionNav');
    if (!nav || nav.dataset.rendered) return;
    nav.dataset.rendered = '1';
    SECTIONS.forEach(([section, label]) => {
        const b = ui.el('button', 'side-nav-btn', {
            type: 'button',
            title: label
        });
        b.dataset.section = section;
        b.innerHTML = SECTION_ICONS[section] + `<span class='side-nav-label'>${label}</span>`;
        b.onclick = () => goSection(section);
        nav.append(b);
    });
}

// A list long enough to scroll past is long enough to need finding rather than scanning.
const SUBBAR_FILTER_FROM = 8;
const subbarFilters = {};

function renderSidebar(section) {
    const spec = SIDEBARS[section] || SIDEBARS.tables;

    const items = spec.items ? spec.items() : [];
    const bar = document.getElementById('subbar');
    bar.innerHTML = '';
    bar.hidden = items.length === 0;
    if (items.length === 0) return;

    const roots = items.filter((i) => i.root);
    const rest = items.filter((i) => !i.root);
    roots.forEach((item) => bar.append(sidebarItem(item)));

    const term = (subbarFilters[section] || '').trim().toLowerCase();
    const matching = term ? rest.filter((i) => i.label.toLowerCase().includes(term)) : rest;

    const filtered = rest.length >= SUBBAR_FILTER_FROM;
    // The filter's own box already divides the list from the root above it; a rule as well is two lines doing one job.
    if (roots.length && rest.length && !filtered) bar.append(ui.el('div', 'subbar-sep'));

    if (filtered) bar.append(subbarFilter(section, spec.group));
    else if (spec.group && rest.length) bar.append(ui.el('div', 'subbar-group', {
        textContent: spec.group
    }));

    matching.forEach((item) => bar.append(sidebarItem(item)));

    // A filter that hides everything has to say so, or it reads as a list that emptied itself.
    if (term && matching.length === 0) bar.append(ui.el('p', 'subbar-empty', {
        textContent: `Nothing matches "${term}".`
    }));
}

function subbarFilter(section, group) {
    const wrap = ui.el('div', 'subbar-filter');
    const input = ui.el('input', 'input input-sm', {
        type: 'search',
        value: subbarFilters[section] || '',
        placeholder: `Filter ${(group || 'items').toLowerCase()}`,
    });
    input.oninput = () => {
        subbarFilters[section] = input.value;
        renderSidebar(section);
        // Repainting the bar replaces the field, so the caret has to be put back.
        const next = document.querySelector('.subbar-filter input');
        if (next) {
            next.focus();
            next.setSelectionRange(next.value.length, next.value.length);
        }
    };
    wrap.append(input);
    return wrap;
}

function sidebarItem({
    label,
    icon,
    badge,
    active,
    onSelect
}) {
    const pill = ui.el('button', 'subbar-pill' + (active ? ' active' : ''), {
        type: 'button'
    });
    if (active) pill.setAttribute('aria-current', 'page');
    if (icon) pill.append(ui.el('span', 'subbar-icon', {
        innerHTML: icon
    }));
    const text = ui.el('span', 'subbar-pill-text', {
        textContent: label
    });
    pill.append(text);
    if (badge) pill.append(ui.el('span', 'tag', {
        textContent: badge
    }));
    if (onSelect) pill.onclick = onSelect;
    // The pill ellipsizes; the title tooltip reveals the original full name.
    pill.title = label;
    return pill;
}

// Repaints the sidebar when the section it is showing owns the changed data.
function refreshSidebar(section) {
    if (currentSection === section) renderSidebar(section);
}

function sectionLabel(section) {
    const s = SECTIONS.find(([s]) => s === section);
    return s ? s[1] : section;
}

// The top header names where you are: section, then the record or pane you have open.
function renderBreadcrumb(route) {
    const el = document.getElementById('breadcrumb');
    if (!el) return;
    const crumbs = [sectionLabel(route.section)];
    if (route.section === 'tables' && route.id) {
        const t = currentTables.find((t) => t.id === route.id);
        crumbs.push(t ? t.name : 'Table');
    } else if (route.section === 'forms' && route.id === 'new') {
        crumbs.push('New form');
    } else if (route.section === 'forms' && route.id) {
        const f = (typeof formsAll === 'undefined' ? [] : formsAll).find((f) => f.id === route.id);
        crumbs.push(f ? (f.title || 'Untitled form') : 'Form');
    } else if (route.section === 'sql' && route.id) {
        const q = savedQueries.find((q) => q.id === route.id);
        crumbs.push(q ? q.name : 'Query');
    } else if (route.section === 'settings') {
        const page = SIDEBARS.settings.items().find((p) => p.active);
        crumbs.push(page ? page.label : 'Host');
    }
    el.innerHTML = crumbs.map((c) => `<span class='crumb'>${ui.escape(c)}</span>`).join('');
}

function toggleSidebar() {
    const shell = document.getElementById('appShell');
    const collapsed = shell.classList.toggle('sidebar-collapsed');
    try {
        localStorage.setItem('baseport.sidebar', collapsed ? '1' : '0');
    } catch (e) {}
    document.querySelectorAll('[aria-expanded]').forEach((el) =>
        el.setAttribute('aria-expanded', String(!collapsed)),
    );
}

function applySidebarState() {
    const shell = document.getElementById('appShell');
    if (!shell) return;
    let collapsed = false;
    try {
        collapsed = localStorage.getItem('baseport.sidebar') === '1';
    } catch (e) {}
    shell.classList.toggle('sidebar-collapsed', collapsed);
    document.querySelectorAll('[aria-expanded]').forEach((el) =>
        el.setAttribute('aria-expanded', String(!collapsed)),
    );
}

function toggleAccountMenu(e) {
    if (e) e.stopPropagation();
    const menu = document.getElementById('accountMenu');
    if (!menu) return;
    const opening = menu.classList.contains('hidden');
    // The submenu belongs to the menu that owns it: reopening must not leave the last flyout hanging.
    closeAppearance();
    menu.classList.toggle('hidden', !opening);
}

function closeAccountMenu() {
    document.getElementById('accountMenu')?.classList.add('hidden');
    closeAppearance();
}

function closeAppearance() {
    document.getElementById('appearanceMenu')?.classList.add('hidden');
    document.getElementById('appearanceTrigger')?.setAttribute('aria-expanded', 'false');
}

function toggleAppearance(e) {
    if (e) e.stopPropagation();
    const menu = document.getElementById('appearanceMenu');
    if (!menu) return;
    const opening = menu.classList.contains('hidden');
    menu.classList.toggle('hidden', !opening);
    document.getElementById('appearanceTrigger')?.setAttribute('aria-expanded', String(opening));
    if (opening) markAppearance();
}

// The choice, not what is on screen: with System picked the dark row must not read as selected at night.
function markAppearance() {
    const chosen = ui.themeChoice();
    document.querySelectorAll('#appearanceMenu [data-appearance]').forEach((btn) => {
        const on = btn.dataset.appearance === chosen;
        btn.classList.toggle('checked', on);
        btn.setAttribute('aria-checked', String(on));
    });
}

function chooseAppearance(next, e) {
    if (e) e.stopPropagation();
    ui.setTheme(next);
    markAppearance();
    closeAccountMenu();
}

document.addEventListener('click', () => closeAccountMenu());
// An overlay with no keyboard exit is a trap, the same rule the sheet follows.
document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') closeAccountMenu();
});
