/* Shared state, the router, and table loading. */
let currentTablePublicId = null;
let currentTableProxyUrl = '';
let currentTables = [];
let currentSection = 'tables';
let lastRenderedUrl = location.href; // where popstate falls back to if the user cancels leaving unsaved changes
let proxyOps = [];
let editingFieldId = null;
let tableDirty = false;
let recordPage = 1;
let recordSearchTimer = null;

// Staged field CRUD; nothing hits the API until Save.
let fieldDraft = [];
let fieldOriginal = {};
let fieldSeq = 0;
let fieldsDirty = false;

// Comma-separated option/value lists: a comma inside one entry survives as \, (a literal backslash is \\).
function splitOptions(str) {
    return (str || '')
        .split(/(?<!\\),/)
        .map((s) => s.trim().replace(/\\(.)/g, '$1'))
        .filter(Boolean);
}

function joinOptions(list) {
    return (list || []).map((s) => String(s).replace(/([\\,])/g, '\\$1')).join(', ');
}

/* Sortable list headers: click a <th data-sort="key"> to sort, remembered per list across visits. */

function sortState(listKey, defaultKey) {
    try {
        return JSON.parse(localStorage.getItem('bp.sort.' + listKey)) || {
            key: defaultKey,
            dir: 'asc'
        };
    } catch (e) {
        return {
            key: defaultKey,
            dir: 'asc'
        };
    }
}

// wires <th data-sort> clicks and repaints the active arrow; safe to call on every render, not just once
function initSortableHeaders(headRowId, listKey, defaultKey, onChange) {
    const headRow = document.getElementById(headRowId);
    if (!headRow) return;
    const state = sortState(listKey, defaultKey);
    headRow.querySelectorAll('th[data-sort]').forEach((th) => {
        th.classList.add('sortable');
        if (!th.dataset.baseLabel) th.dataset.baseLabel = th.textContent;
        const active = th.dataset.sort === state.key;
        th.classList.toggle('sort-active', active);
        th.textContent = th.dataset.baseLabel + (active ? (state.dir === 'desc' ? ' ▼' : ' ▲') : '');
        th.onclick = () => {
            const key = th.dataset.sort;
            const dir = state.key === key && state.dir === 'asc' ? 'desc' : 'asc';
            localStorage.setItem('bp.sort.' + listKey, JSON.stringify({
                key,
                dir
            }));
            onChange(key, dir);
        };
    });
}

function cloneField(f) {
    return {
        id: f.id || null,
        key: f.key || f.id || 'tmp-' + ++fieldSeq,
        name: f.name,
        dataType: f.dataType || 'text',
        expression: f.expression || '',
        optionsJson: f.optionsJson || '[]',
        label: f.label || '',
        helpText: f.helpText || '',
        defaultValue: f.defaultValue || '',
        currency: f.currency || '',
        min: f.min === undefined ? null : f.min,
        max: f.max === undefined ? null : f.max,
        position: f.position || 0,
        isRequired: !!f.isRequired,
        pattern: f.pattern || '',
        isHidden: !!f.isHidden,
        isUnique: !!f.isUnique,
        isIdentifier: !!f.isIdentifier,
    };
}

// Saved fields carry a server id; staged ones a local key, so a row is addressable either way.
function fieldKey(f) {
    return f.key || f.id;
}

function markFieldsDirty() {
    fieldsDirty = true;
    updateSaveButtons();
}

function markTableDirty() {
    tableDirty = true;
    updateSaveButtons();
}

function updateSaveButtons() {
    const fb = document.getElementById('saveFieldsBtn');
    if (fb) fb.disabled = !fieldsDirty;
    const tb = document.getElementById('saveTableBtn');
    if (tb) tb.disabled = !tableDirty;
}

function greet(username) {
    const h = new Date().getHours();
    const g = h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
    document.getElementById('greeting').innerText = `${g}, ${username || 'builder'}`;
}

// Views are a route, not a toggle: the URL decides which one shows.
function setView(v) {
    if (!currentTablePublicId) return;
    navigate(v === 'records' ? `/tables/${currentTablePublicId}/records` : `/tables/${currentTablePublicId}`);
}

function applyView(v) {
    document.getElementById('builderView').classList.toggle('hidden', v !== 'builder');
    document.getElementById('recordsView').classList.toggle('hidden', v !== 'records');
    document.querySelectorAll('#viewToggle .seg-btn').forEach((b) => b.classList.toggle('active', b.dataset.view === v));
    if (v === 'records') loadRecords();
}

/* Router: the URL is the single source of truth; every selection navigates and render() rebuilds from the path, so deep links work and back/forward behave. Paths: /, /tables/{id}, /tables/{id}/records, /forms, /forms/{id}, /sql, /sql/{id}, /settings/{page}, /schema, /auth, /logs. */

// The console is mounted under a prefix; parseRoute strips it and navigate puts it back, so every route above stays written as if the console owned the root.
const BASE = '/_/admin';

function routePath() {
    const p = location.pathname;
    return (p.startsWith(BASE) ? p.slice(BASE.length) : p).replace(/\/+$/, '') || '/';
}

const ROUTES = [
    [/^\/(?:tables)?$/, () => ({
        section: 'tables'
    })],
    [
        /^\/tables\/([\w-]+)(?:\/(records|builder))?$/,
        (m) => ({
            section: 'tables',
            id: m[1],
            view: m[2] === 'records' ? 'records' : 'builder'
        }),
    ],
    [/^\/forms(?:\/([\w-]+))?$/, (m) => ({
        section: 'forms',
        id: m[1]
    })],
    [/^\/sql(?:\/([\w-]+))?$/, (m) => ({
        section: 'sql',
        id: m[1]
    })],
    [/^\/settings(?:\/([\w-]+))?$/, (m) => ({
        section: 'settings',
        id: m[1] || 'host'
    })],
    [/^\/(schema|auth|logs)$/, (m) => ({
        section: m[1]
    })],
];

function parseRoute() {
    const path = routePath();
    for (const [pattern, build] of ROUTES) {
        const m = path.match(pattern);
        if (m) return build(m);
    }
    return {
        section: 'tables'
    };
}

// none of these are visible to the browser's own "leave site?" prompt since nothing was submitted
function hasUnsavedChanges() {
    if (tableDirty || fieldsDirty) return true;
    const draftName = document.getElementById('tableName');
    if (draftName && draftName.value.trim()) return true;
    return typeof hasUnsavedFormChanges === 'function' && hasUnsavedFormChanges();
}

async function navigate(path, {
    replace = false
} = {}) {
    if (hasUnsavedChanges()) {
        const leave = await ui.confirm({
            title: 'Discard changes?',
            message: 'You have unsaved changes here. Leave without saving?',
            confirmLabel: 'Discard',
            danger: true,
        });
        if (!leave) return;
    }
    const url = BASE + (path === '/' ? '' : path);
    if (location.pathname !== url) {
        history[replace ? 'replaceState' : 'pushState']({}, '', url);
    }
    return render();
}

// Each section owns its route; loading happens here so a deep link works on a cold page.
const SECTION_ROUTES = {
    // Overview and editor are separate pages: an index has no business rendering the chrome of the thing it indexes.
    tables: async (id) => {
        // Already rendered on a full load; only fetch when stale or navigating in-session.
        if (!currentTables.length) await loadTables();
        else renderSidebar('tables');
        const overview = !id;
        document.getElementById('tablesOverview').classList.toggle('hidden', !overview);
        document.getElementById('tableDetail').classList.toggle('hidden', overview);
        if (overview) {
            currentTablePublicId = null;
            // runs here too since a full page load fills currentTables without calling loadTables()
            updateSummary(currentTables);
            renderTablesOverview();
            return;
        }

        const table = currentTables.find((t) => t.id === id);
        if (!table) return navigate('/tables', {
            replace: true
        });
        selectTable(table);
        applyView(parseRoute().view || 'builder');
    },
    forms: async (id) => {
        await loadForms();
        const overview = !id;
        document.getElementById('formsOverview').classList.toggle('hidden', !overview);
        document.getElementById('formEditor').classList.toggle('hidden', overview);
        if (overview) {
            closeFormEditor();
            return;
        }
        if (id === 'new') {
            newForm();
            return;
        }
        await editForm(id);
    },
    sql: async (id) => {
        initSqlEditor();
        await loadSavedQueries();
        const query = savedQueries.find((q) => q.id === id);
        if (query) applyQuery(query);
        else clearQuery();
        // rAF gives layout a chance to settle before CodeMirror re-measures a freshly-shown container
        if (sqlEditor) requestAnimationFrame(() => sqlEditor.refresh());
    },
    settings: async (page) => {
        await loadSettings();
        applySettingsPage(page);
    },
    schema: () => loadSchema(),
    auth: () => loadAccounts(),
    logs: () => loadLogs(),
};

async function render() {
    const route = parseRoute();
    currentSection = route.section;

    renderSectionNav();
    const isTables = route.section === 'tables';
    document.getElementById('tablesArea').classList.toggle('hidden', !isTables);
    document
        .querySelectorAll('.side-nav-btn')
        .forEach((b) => b.classList.toggle('active', b.dataset.section === route.section));
    ['forms', 'sql', 'schema', 'auth', 'logs', 'settings'].forEach((v) =>
        document.getElementById(v + 'View').classList.toggle('active', v === route.section),
    );

    renderSidebar(route.section);
    renderBreadcrumb(route);
    await SECTION_ROUTES[route.section](route.id);
    renderSidebar(route.section); // the loader may have changed what is listed or active
    renderBreadcrumb(route);
    lastRenderedUrl = location.href;
}

// Kept because the rail markup and call sites read better this way.
function goSection(section) {
    return navigate('/' + section);
}

function clearTableSelection() {
    currentTablePublicId = null;
    currentTableProxyUrl = '';
}

async function loadTables() {
    const res = await fetch('/api/_admin/tables');
    const tables = await res.json();
    currentTables = tables;
    renderSidebar(currentSection);
    updateSummary(currentTables);
}

// Four counts on the tables overview: what data-model work exists so far, at a glance.
function updateSummary(tables) {
    const el = document.getElementById('tablesSummary');
    if (!el) return;
    const records = tables.reduce((n, t) => n + (t.recordCount || 0), 0);
    const forms = tables.reduce((n, t) => n + (t.formCount || 0), 0);
    const apiEnabled = tables.filter((t) => t.apiEnabled).length;
    el.innerHTML = [
        ['Tables', tables.length],
        ['Records', records],
        ['Forms', forms],
        ['API enabled', apiEnabled],
    ].map(([label, value]) => `<div class="summary-card"><div class="summary-value">${value.toLocaleString()}</div><div class="summary-label">${label}</div></div>`).join('');
}

async function newTable() {
    const name = await ui.ask({
        title: 'New table',
        label: 'Table name',
        placeholder: 'e.g. Customers',
        confirmLabel: 'Create',
    });
    if (!name) return;
    const res = await fetch('/api/_admin/tables', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            name
        }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
        ui.toast(data.errors || ['Failed to create table.'], 'error');
        return;
    }
    await loadTables();
    navigate(`/tables/${data.id}`);
}