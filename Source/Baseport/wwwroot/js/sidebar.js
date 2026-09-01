
const SECTION_ICONS = {
    tables: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><rect x='3' y='3' width='16' height='16' rx='2'/><path d='M3 9h18M9 21V9'/></svg>",
    forms: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><rect x='4' y='3' width='16' height='18' rx='2'/><path d='M8 8h8M8 12h8M8 16h4'/></svg>",
    actions: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><path d='M13 2 3 14h8l-1 8 10-12h-8z'/></svg>",
    sql: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><path d='M17 3a2.8 2.8 0 1 0 0 5.6 2.8 2.8 0 1 0 0-5.6M3 21l9-9M12.2 6.3 11 5l-3.5 3.5 1.2 1.2z'/><path d='M5 3l1.5 1.5M5 3 3.5 4.5M12.8 17.3 14 18.6l3.5-3.5-1.2-1.2zM14.5 12.5h2M17 21l-1.5-1.5M17 21l1.5-1.5'/></svg>",
    schema: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><circle cx='12' cy='5' r='2'/><circle cx='5' cy='19' r='2'/><circle cx='19' cy='19' r='2'/><path d='M12 7v6M5 17l2.5-4M19 17l-2.5-4M12 13l-4.5 4M12 13l4.5 4'/></svg>",
    auth: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><path d='M5 7a4 4 0 1 0 8 0 4 4 0 1 0-8 0M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2M16 3.13a4 4 0 0 1 0 7.75M21 21v-2a4 4 0 0 0-3-3.85'/></svg>",
    logs: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><path d='m4 16 6-7 5 5 5-6'/><path d='M15 14a1 1 0 1 0 2 0 1 1 0 1 0-2 0M9 9a1 1 0 1 0 2 0 1 1 0 1 0-2 0M3 16a1 1 0 1 0 2 0 1 1 0 1 0-2 0M19 8a1 1 0 1 0 2 0 1 1 0 1 0-2 0'/></svg>",
    settings: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h.01a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/></svg>",
};

const OBJECT_ICONS = {
    table: SECTION_ICONS.tables,
    form: SECTION_ICONS.forms,
    list: "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='16' height='16'><circle cx='4' cy='6' r='1' fill='currentColor' stroke='none'/><circle cx='4' cy='12' r='1' fill='currentColor' stroke='none'/><circle cx='4' cy='18' r='1' fill='currentColor' stroke='none'/><path d='M9 6h11M9 12h11M9 18h11'/></svg>",
    query: SECTION_ICONS.sql,
};

const SECTION_GROUPS = [
    ['Workspace', [
        ['tables', 'Tables'],
        ['forms', 'Forms'],
        ['actions', 'Actions'],
        ['sql', 'Query'],
    ]],
    ['System', [
        ['schema', 'Schema'],
        ['auth', 'Users'],
        ['logs', 'Logs'],
        ['settings', 'Settings'],
    ]],
];

const SECTIONS = SECTION_GROUPS.flatMap(([, rows]) => rows);

const SORT_ICON = "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='1.75' viewBox='0 0 24 24' width='13' height='13'><path d='M7 4v16m0 0-3.5-3.5M7 20l3.5-3.5M17 20V4m0 0-3.5 3.5M17 4l3.5 3.5'/></svg>";

const CLEAR = "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='12' height='12'><path d='m6 6 12 12M18 6 6 18'/></svg>";

const CHEVRON = "<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2.5' viewBox='0 0 24 24' width='12' height='12'><path d='m9 18 6-6-6-6'/></svg>";

const SIDEBARS = {
    tables: {
        group: 'Tables',
        items: () => currentTables.map((t) => ({
            id: t.id,
            label: t.name,
            icon: OBJECT_ICONS.table,
            badge: t.isProxy ? 'proxy' : null,
            createdAt: t.createdAt,
            count: t.recordCount,
            active: t.id === currentTablePublicId,
            onSelect: () => navigate(`/tables/${t.id}`),
        })),
    },

    forms: {
        group: 'Forms',
        items: () => (typeof formsAll === 'undefined' ? [] : formsAll).map((f) => ({
            id: f.id,
            label: f.title || 'Untitled form',
            icon: f.kind === 'list' ? OBJECT_ICONS.list : OBJECT_ICONS.form,
            createdAt: f.createdAt,
            active: f.id === formEditingId,
            onSelect: () => navigate(`/forms/${f.id}`),
        })),
    },

    actions: {
        group: 'Actions',
        items: () => (typeof actionsAll === 'undefined' ? [] : actionsAll).map((a) => ({
            id: a.id,
            label: a.name || 'Untitled action',
            icon: SECTION_ICONS.actions,
            createdAt: a.createdAt,
            active: a.id === actionEditingId,
            onSelect: () => navigate(`/actions/${a.id}`),
        })),
    },

    sql: {
        group: 'Saved queries',
        items: () => savedQueries.map((q) => ({
            id: q.id,
            label: q.name,
            icon: OBJECT_ICONS.query,
            createdAt: q.createdAt,
            active: q.id === currentQueryId,
            onSelect: () => navigate(`/sql/${q.id}`),
        })),
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
            id: page,
            label,
            active: settingsCurrentPage === page,
            onSelect: () => navigate(`/settings/${page}`),
        })),
    },

    schema: { items: () => [] },
    auth: { items: () => [] },
    logs: { items: () => [] },
};

const navFilters = {};

const navSort = readNavStore('baseport.nav.sort');
const navOrder = readNavStore('baseport.nav.order');

let openedSection = null;
let sortMenuSection = null;
let dragging = null;

function readNavStore(key) {
    try {
        const v = JSON.parse(localStorage.getItem(key));
        return v && typeof v === 'object' && !Array.isArray(v) ? v : {};
    } catch (e) {
        return {};
    }
}

function writeNavStore(key, value) {
    try {
        localStorage.setItem(key, JSON.stringify(value));
    } catch (e) {}
}

function prefersReducedMotion() {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function sectionItems(section) {
    const spec = SIDEBARS[section];
    if (!spec || !spec.items) return [];
    try {
        return spec.items();
    } catch (e) {
        return [];
    }
}

function sortsFor(objects) {
    const sorts = [
        ['name', 'Name A to Z'],
        ['created', 'Newest first'],
    ];
    if (objects.some((i) => typeof i.count === 'number')) sorts.push(['records', 'Most records']);
    sorts.push(['manual', 'Manual']);
    return sorts;
}

function byName(a, b) {
    return a.label.localeCompare(b.label);
}

function sortObjects(section, objects) {
    const mode = navSort[section] || 'name';
    const list = [...objects];
    if (mode === 'created') return list.sort((a, b) => String(b.createdAt || '').localeCompare(String(a.createdAt || '')) || byName(a, b));
    if (mode === 'records') return list.sort((a, b) => (b.count || 0) - (a.count || 0) || byName(a, b));
    if (mode !== 'manual') return list.sort(byName);

    const order = navOrder[section] || [];
    return list.sort((a, b) => {
        const ia = order.indexOf(a.id);
        const ib = order.indexOf(b.id);
        if (ia < 0 && ib < 0) return byName(a, b);
        if (ia < 0) return 1;
        if (ib < 0) return -1;
        return ia - ib;
    });
}

function sectionObjects(section) {
    const raw = sectionItems(section);
    return SIDEBARS[section] && SIDEBARS[section].group ? sortObjects(section, raw) : raw;
}

function renderSidebar(section) {
    const nav = document.getElementById('sectionNav');
    if (!nav) return;
    const current = section || currentSection;
    const next = document.createElement('div');
    let opened = null;

    SECTION_GROUPS.forEach(([group, rows]) => {
        next.append(ui.el('div', 'nav-eyebrow', {
            textContent: group
        }));
        rows.forEach(([id, label]) => {
            const objects = sectionObjects(id);
            const open = id === current && objects.length > 0;
            if (open) opened = id;
            next.append(sectionRow(id, label, objects, open, id === current));
            if (open) next.append(sectionKids(id, objects));
        });
    });

    if (next.innerHTML === nav.innerHTML) return;

    const scroll = nav.scrollTop;
    nav.replaceChildren(...next.childNodes);
    nav.scrollTop = scroll;

    if (opened && opened !== openedSection && !prefersReducedMotion()) {
        nav.querySelector('.nav-kids').animate(
            [{ opacity: 0, transform: 'translateY(-.25rem)' }, {}],
            { duration: 160, easing: 'ease-out' },
        );
    }
    openedSection = opened;
}

function navLabel(text) {
    const wrap = ui.el('span', 'nav-label');
    const tail = text.length > 8 ? text.slice(-7) : '';
    wrap.append(ui.el('span', 'nav-label-head', {
        textContent: tail ? text.slice(0, -7) : text
    }));
    if (tail) wrap.append(ui.el('span', 'nav-label-tail', {
        textContent: tail
    }));
    return wrap;
}

function sectionRow(section, label, objects, open, active) {
    const b = ui.el('button', 'side-nav-btn', {
        type: 'button',
        title: label
    });
    b.dataset.section = section;
    if (active) b.classList.add('active');
    if (open) b.classList.add('open');
    if (open && objects.some((i) => i.active)) b.classList.add('delegated');
    if (active && !b.classList.contains('delegated')) b.setAttribute('aria-current', 'page');
    b.append(ui.el('span', 'nav-icon', {
        innerHTML: SECTION_ICONS[section]
    }));
    b.append(navLabel(label));
    if (objects.length) {
        if (SIDEBARS[section].group) b.append(ui.el('span', 'nav-count', {
            textContent: String(objects.length)
        }));
        b.append(ui.el('span', 'nav-chevron', {
            innerHTML: CHEVRON
        }));
        b.setAttribute('aria-expanded', String(open));
    }
    b.onclick = () => goSection(section);
    return b;
}

function sectionKids(section, objects) {
    const wrap = ui.el('div', 'nav-kids');
    const grouped = SIDEBARS[section].group;

    if (grouped) {
        wrap.append(navFilter(section, grouped, objects));
        if (sortMenuSection === section) wrap.append(sortMenu(section, objects));
    }

    const term = (navFilters[section] || '').trim().toLowerCase();
    const matching = term ? objects.filter((i) => i.label.toLowerCase().includes(term)) : objects;
    matching.forEach((item) => wrap.append(navItem(section, item, grouped && !term)));

    if (term && matching.length === 0) wrap.append(ui.el('p', 'nav-empty', {
        textContent: `Nothing matches "${term}".`
    }));
    return wrap;
}

function navFilter(section, group, objects) {
    const wrap = ui.el('div', 'nav-filter');
    const field = ui.el('div', 'nav-field');
    const input = ui.el('input', 'input input-sm', {
        type: 'search',
        value: navFilters[section] || '',
        placeholder: 'Filter…',
    });
    input.setAttribute('aria-label', `Filter ${group.toLowerCase()}`);
    input.oninput = () => setFilter(section, input.value);
    input.onkeydown = (ev) => {
        if (ev.key !== 'Escape' || !input.value) return;
        ev.stopPropagation();
        setFilter(section, '');
    };
    field.append(input);

    if (navFilters[section]) {
        const clear = ui.el('button', 'nav-clear', {
            type: 'button',
            title: 'Clear filter',
            innerHTML: CLEAR,
        });
        clear.setAttribute('aria-label', 'Clear filter');
        clear.onclick = () => setFilter(section, '');
        field.append(clear);
    }
    wrap.append(field);

    const open = sortMenuSection === section;
    const label = (sortsFor(objects).find(([m]) => m === (navSort[section] || 'name')) || [])[1];
    const sort = ui.el('button', 'nav-sort' + (open ? ' open' : ''), {
        type: 'button',
        title: `Sort: ${label}`,
        innerHTML: SORT_ICON,
    });
    sort.setAttribute('aria-label', `Sort: ${label}`);
    sort.setAttribute('aria-expanded', String(open));
    sort.onclick = (ev) => {
        ev.stopPropagation();
        sortMenuSection = open ? null : section;
        renderSidebar(section);
    };
    wrap.append(sort);
    return wrap;
}

function sortMenu(section, objects) {
    const menu = ui.el('div', 'nav-sort-menu', {
        role: 'menu'
    });
    const mode = navSort[section] || 'name';
    sortsFor(objects).forEach(([id, label]) => {
        const b = ui.el('button', 'nav-sort-option' + (id === mode ? ' checked' : ''), {
            type: 'button',
            textContent: label,
        });
        b.setAttribute('role', 'menuitemradio');
        b.setAttribute('aria-checked', String(id === mode));
        b.onclick = (ev) => {
            ev.stopPropagation();
            navSort[section] = id;
            writeNavStore('baseport.nav.sort', navSort);
            sortMenuSection = null;
            renderSidebar(section);
        };
        menu.append(b);
    });
    return menu;
}

function navItem(section, item, orderable) {
    const b = ui.el('button', 'nav-item' + (item.active ? ' active' : ''), {
        type: 'button',
        title: item.label
    });
    if (item.active) b.setAttribute('aria-current', 'page');
    if (item.icon) b.append(ui.el('span', 'nav-icon', {
        innerHTML: item.icon
    }));
    b.append(navLabel(item.label));
    if (item.badge) b.append(ui.el('span', 'nav-count', {
        textContent: item.badge
    }));
    if (item.onSelect) b.onclick = item.onSelect;
    if (orderable) attachOrdering(b, section, item.id);
    return b;
}

function attachOrdering(b, section, id) {
    b.draggable = true;
    b.dataset.id = id;

    b.ondragstart = (ev) => {
        dragging = { section, id };
        ev.dataTransfer.effectAllowed = 'move';
        ev.dataTransfer.setData('text/plain', id);
        b.classList.add('dragging');
    };
    b.ondragend = () => {
        dragging = null;
        b.classList.remove('dragging');
        clearDropMarks();
    };
    b.ondragover = (ev) => {
        if (!dragging || dragging.section !== section || dragging.id === id) return;
        ev.preventDefault();
        ev.dataTransfer.dropEffect = 'move';
        const after = ev.clientY > b.getBoundingClientRect().top + b.offsetHeight / 2;
        b.classList.toggle('drop-after', after);
        b.classList.toggle('drop-before', !after);
    };
    b.ondragleave = () => b.classList.remove('drop-before', 'drop-after');
    b.ondrop = (ev) => {
        ev.preventDefault();
        const after = b.classList.contains('drop-after');
        clearDropMarks();
        if (!dragging || dragging.section !== section || dragging.id === id) return;
        moveBeside(section, dragging.id, id, after);
    };
    b.onkeydown = (ev) => {
        if (!ev.altKey || (ev.key !== 'ArrowUp' && ev.key !== 'ArrowDown')) return;
        ev.preventDefault();
        moveBy(section, id, ev.key === 'ArrowUp' ? -1 : 1);
    };
}

function clearDropMarks() {
    document.querySelectorAll('.nav-item.drop-before, .nav-item.drop-after')
        .forEach((el) => el.classList.remove('drop-before', 'drop-after'));
}

function commitOrder(section, ids, focusId) {
    navOrder[section] = ids;
    navSort[section] = 'manual';
    writeNavStore('baseport.nav.order', navOrder);
    writeNavStore('baseport.nav.sort', navSort);
    renderSidebar(section);
    if (focusId) document.querySelector(`.nav-item[data-id="${focusId}"]`)?.focus();
}

function moveBeside(section, id, targetId, after) {
    const ids = sectionObjects(section).map((i) => i.id);
    const from = ids.indexOf(id);
    if (from < 0) return;
    ids.splice(from, 1);
    const to = ids.indexOf(targetId);
    if (to < 0) return;
    ids.splice(after ? to + 1 : to, 0, id);
    commitOrder(section, ids);
}

function moveBy(section, id, delta) {
    const ids = sectionObjects(section).map((i) => i.id);
    const from = ids.indexOf(id);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= ids.length) return;
    ids.splice(to, 0, ids.splice(from, 1)[0]);
    commitOrder(section, ids, id);
}

function setFilter(section, value) {
    navFilters[section] = value;
    renderSidebar(section);
    const input = document.querySelector('.nav-filter input');
    if (!input) return;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
}

function refreshSidebar(section) {
    if (currentSection === section) renderSidebar(section);
}

function sectionLabel(section) {
    const s = SECTIONS.find(([s]) => s === section);
    return s ? s[1] : section;
}

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

function isNarrowViewport() {
    return window.matchMedia('(max-width: 768px)').matches;
}

function setNavExpanded(open) {
    document.querySelectorAll('.brand-trigger, .topbar-trigger').forEach((el) =>
        el.setAttribute('aria-expanded', String(open)),
    );
}

function toggleSidebar() {
    const shell = document.getElementById('appShell');
    if (!shell) return;
    if (isNarrowViewport()) {
        setNavExpanded(shell.classList.toggle('nav-open'));
        return;
    }
    const collapsed = shell.classList.toggle('sidebar-collapsed');
    try {
        localStorage.setItem('baseport.sidebar', collapsed ? '1' : '0');
    } catch (e) {}
    setNavExpanded(!collapsed);
}

function closeNavDrawer() {
    const shell = document.getElementById('appShell');
    if (!shell || !shell.classList.contains('nav-open')) return;
    shell.classList.remove('nav-open');
    setNavExpanded(false);
}

function applySidebarState() {
    const shell = document.getElementById('appShell');
    if (!shell) return;
    let collapsed = false;
    try {
        collapsed = localStorage.getItem('baseport.sidebar') === '1';
    } catch (e) {}
    shell.classList.toggle('sidebar-collapsed', collapsed);
    setNavExpanded(isNarrowViewport() ? false : !collapsed);
}

function toggleAccountMenu(e) {
    if (e) e.stopPropagation();
    const menu = document.getElementById('accountMenu');
    if (!menu) return;
    const opening = menu.classList.contains('hidden');
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

document.addEventListener('click', (ev) => {
    closeAccountMenu();
    const hit = ev.target.closest ? ev.target : document.documentElement;
    if (sortMenuSection && !hit.closest('.nav-sort-menu, .nav-sort')) {
        const section = sortMenuSection;
        sortMenuSection = null;
        renderSidebar(section);
    }
    const row = hit.closest('.nav-item, .side-nav-btn');
    if (row && !row.hasAttribute('aria-expanded')) closeNavDrawer();
});

document.addEventListener('keydown', (ev) => {
    if (ev.key !== 'Escape') return;
    closeAccountMenu();
    closeNavDrawer();
    if (sortMenuSection) {
        const section = sortMenuSection;
        sortMenuSection = null;
        renderSidebar(section);
    }
});
