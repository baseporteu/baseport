// Forms, rail item 2. Kinds: submit (writes), lookup (matches one record on an identifier), list (paged overview). Everything a form reveals is chosen here and enforced server-side.
let formsAll = [];
let formEditingId = null;
let formKind = 'form';
let formActions = ['submit'];
// Working config kept across action toggles; enabling lookup must reveal a filled-in panel.
let formConfigDraft = {};
let formTableFields = [];
let formTableIsProxy = false;
let layout = {
    rows: []
};
let formOriginalSnapshot = null; // set once the editor finishes loading; hasUnsavedFormChanges diffs against it

const ACTION_HINTS = {
    submit: 'Collects a new record. Design the matrix below.',
    lookup: 'Finds one record by an identifier the visitor already has. An unknown value returns the same not-found message every time.',
    both: 'Does both: the visitor can look an existing record up, or create a new one, from the same embed.',
    list: 'A paged overview of records. Choose the columns, what the search box matches, and how many rows a page holds.',
};

/* listing */

async function loadForms() {
    formsAll = await fetch('/api/_admin/forms').then((r) => r.json());
    renderFormsList();
    refreshSidebar('forms');
}

async function renderFormsList() {
    const filter = document.getElementById('formsFilterKind').value;
    const sort = sortState('forms', 'created');
    initSortableHeaders('formsHead', 'forms', 'created', () => renderFormsList());
    const params = new URLSearchParams({
        sort: sort.key,
        order: sort.dir
    });
    if (filter) params.set('kind', filter);
    await ui.fragment('formsRows', `/api/_admin/fragments/forms?${params}`);

    const shown = formsAll.filter((f) => !filter || f.kind === filter);
    document.getElementById('formsEmpty').classList.toggle('hidden', shown.length > 0);
    document.getElementById('formsEmpty').innerText = filter ?
        `No ${filter === 'list' ? 'lists' : 'forms'} yet.` :
        'No forms yet. Create one to get started.';
}

function openPreview(id) {
    fetch(`/api/_admin/forms/${id}/preview-token`)
        .then((r) => r.json())
        .then((d) => {
            if (d && d.url) window.open(d.url, '_blank');
        });
}

function deleteForm(id) {
    const f = formsAll.find((x) => x.id === id);
    openModal({
        title: 'Delete form',
        message: `Are you sure you want to delete the form "${f ? f.title : id}"? Its embed code and preview link stop working immediately. This will not affect your saved records.`,
        confirmLabel: 'Delete',
        danger: true,
        onConfirm: async () => {
            const res = await fetch(`/api/_admin/forms/${id}`, {
                method: 'DELETE'
            });
            if (!(await ui.handle(res, {
                    success: 'Form deleted.',
                    failure: 'The form could not be deleted.'
                }))) return;
            if (formEditingId === id) return navigate('/forms');
            await loadForms();
        },
    });
}

function deleteCurrentForm() {
    if (formEditingId) deleteForm(formEditingId);
}

/* editor shell */

function newForm() {
    formEditingId = null;
    document.getElementById('formPreviewBtn').classList.add('hidden');
    document.getElementById('formDeleteBtn').classList.add('hidden');
    layout = {
        rows: []
    };
    document.getElementById('formEditorTitle').innerText = 'New form';
    document.getElementById('formTitle').value = '';
    document.getElementById('formDescription').value = '';
    document.getElementById('formPublished').checked = true;
    document.getElementById('saveFormBtn').innerText = 'Publish form';
    document.getElementById('formEditor').classList.remove('hidden');
    document.getElementById('formKinds').classList.remove('hidden');
    document.getElementById('formKindBadge').classList.add('hidden');
    document.getElementById('formTable').disabled = false;
    fillTableSelect();
    applyFormShape('form', ['submit']);
    onFormTableChange();
    document.getElementById('formEditor').scrollIntoView({
        behavior: 'smooth'
    });
    formOriginalSnapshot = JSON.stringify(formSnapshot());
}

async function editForm(id) {
    const f = await fetch(`/api/_admin/forms/${id}`)
        .then((r) => r.json())
        .catch(() => null);
    if (!f || !f.id) return navigate('/forms', {
        replace: true
    });

    formEditingId = f.id;
    document.getElementById('formPreviewBtn').classList.remove('hidden');
    document.getElementById('formDeleteBtn').classList.remove('hidden');
    refreshSidebar('forms');
    document.getElementById('formEditorTitle').innerText = 'Edit form';
    document.getElementById('formTitle').value = f.title || '';
    document.getElementById('formDescription').value = f.description || '';
    document.getElementById('formPublished').checked = !!f.isPublished;
    document.getElementById('saveFormBtn').innerText = 'Save changes';
    document.getElementById('formEditor').classList.remove('hidden');

    // Table and kind are fixed after creation: changing either would orphan every field the form references.
    document.getElementById('formKinds').classList.add('hidden');
    document.getElementById('formKindBadge').classList.remove('hidden');
    document.getElementById('formKindBadge').innerText = f.kind === 'list' ? 'List' : 'Form';
    fillTableSelect(f.tableId);
    document.getElementById('formTable').disabled = true;
    layout = parseLayout(f.layoutJson);
    applyFormShape(f.kind, f.actions);
    await loadTableFields(f.tableId);
    applyKindConfig(parseConfig(f.configJson));
    document.getElementById('formEditor').scrollIntoView({
        behavior: 'smooth'
    });
    formOriginalSnapshot = JSON.stringify(formSnapshot());
}

function closeFormEditor() {
    formEditingId = null;
    formOriginalSnapshot = null;
    refreshSidebar('forms');
    document.getElementById('formEditor').classList.add('hidden');
}

// Cancel returns to the forms index, which is a route, not a hidden div.
function cancelFormEditor() {
    navigate('/forms');
}

// Preview the form currently open in the editor.
function previewCurrentForm() {
    if (formEditingId) openPreview(formEditingId);
}

function fillTableSelect(selected) {
    const sel = document.getElementById('formTable');
    sel.innerHTML = currentTables
        .map((t) => `<option value="${t.id}" ${t.id === selected ? 'selected' : ''}>${escapeHtml(t.name)}</option>`)
        .join('');
}

// Kind decides the rendering; for a form the enabled actions decide which panels show, and both may be on at once.
function applyFormShape(kind, actions) {
    formKind = kind === 'list' ? 'list' : 'form';
    formActions = formKind === 'list' ? [] : normalizeActions(actions);

    const doesSubmit = formActions.includes('submit');
    const doesLookup = formActions.includes('lookup');
    document.getElementById('kindSubmit').classList.toggle('hidden', formKind !== 'form' || !doesSubmit);
    document.getElementById('kindLookup').classList.toggle('hidden', formKind !== 'form' || !doesLookup);
    document.getElementById('kindList').classList.toggle('hidden', formKind !== 'list');

    document
        .querySelectorAll('#formKinds .seg-btn')
        .forEach((b) => b.classList.toggle('active', b.dataset.kind === formKind));
    document.querySelectorAll('#formActions input').forEach((cb) => {
        cb.checked = formActions.includes(cb.value);
    });
    document.getElementById('formActions').classList.toggle('hidden', formKind !== 'form');

    document.getElementById('formKindHint').innerText =
        formKind === 'list' ?
        ACTION_HINTS.list :
        doesSubmit && doesLookup ?
        ACTION_HINTS.both :
        ACTION_HINTS[formActions[0]] || '';

    if (formKind === 'form' && doesSubmit) renderCanvas();
}

// At least one action, or the form does nothing at all.
function normalizeActions(actions) {
    const valid = (actions || []).filter((a) => a === 'submit' || a === 'lookup');
    return valid.length ? valid : ['submit'];
}

function chooseKind(kind) {
    applyFormShape(kind, formActions);
}

function onActionsChange() {
    const picked = Array.from(document.querySelectorAll('#formActions input:checked')).map((cb) => cb.value);
    if (!picked.length) {
        ui.toast('A form needs at least one action.', 'error');
        applyFormShape(formKind, formActions);
        return;
    }
    // Save the visible panels' state before switching, so a revealed panel arrives filled in.
    formConfigDraft = {
        ...formConfigDraft,
        ...collectConfig()
    };
    applyFormShape(formKind, picked);
    applyKindConfig(formConfigDraft);
}

async function onFormTableChange() {
    // Columns, actions and filters name the previous table's fields, so they cannot survive the switch.
    listColumns = [];
    listActions = [];
    await loadTableFields(document.getElementById('formTable').value);
    applyKindConfig({});
}

async function loadTableFields(tableId) {
    const table =
        currentTables.find((t) => t.id === tableId) ||
        (await fetch('/api/_admin/tables').then((r) => r.json())).find((t) => t.id === tableId);
    formTableFields = table ? table.fields || [] : [];
    formTableIsProxy = !!(table && table.isProxy);
    renderPalette();
    renderKindFieldPickers();
    renderListBuilder();
    applyProxyNotice(table);
}

// Proxy tables delegate ordering to the remote API, so sort/filters are dead controls there.
function applyProxyNotice(table) {
    const note = document.getElementById('formProxyNote');
    note.classList.toggle('hidden', !formTableIsProxy);
    if (formTableIsProxy) {
        note.innerText = `${table.name} is a proxy table. Submissions are forwarded to ${table.proxyUrl} and nothing is stored here; lookups and lists read live from the remote API, which decides ordering.`;
    }
    document.getElementById('listSortField').disabled = formTableIsProxy;
    document.getElementById('listSortDir').disabled = formTableIsProxy;
}

/* kind-specific config */

// Server-computed and hidden fields are absent from pickers: a visitor can neither type nor see them, so offering them builds a form the server refuses.
function selectableFields() {
    return formTableFields.filter((f) => !f.isHidden && f.dataType !== 'derived');
}

function identifierCandidates() {
    return selectableFields().filter(
        (f) => f.isIdentifier || !['multiselect', 'file', 'calculated', 'systemid', 'boolean'].includes(f.dataType),
    );
}

function checkGrid(containerId, fields, checked, emptyText) {
    const el = document.getElementById(containerId);
    if (!fields.length) {
        el.innerHTML = `<p class="muted">${emptyText}</p>`;
        return;
    }
    el.innerHTML = fields
        .map(
            (f) => `
        <label class="check-inline">
            <input type="checkbox" value="${escapeHtml(f.name)}" ${checked.includes(f.name) ? 'checked' : ''}>
            ${escapeHtml(f.label || f.name)}
            <span class="muted">${escapeHtml(f.dataType)}${f.isIdentifier ? ' · identifier' : ''}</span>
        </label>`,
        )
        .join('');
}

function checkedValues(containerId) {
    return Array.from(document.querySelectorAll(`#${containerId} input:checked`)).map((i) => i.value);
}

function renderKindFieldPickers(config) {
    const cfg = config || {};
    checkGrid(
        'lookupMatchFields',
        identifierCandidates(),
        cfg.matchFields || [],
        'This table has no field a visitor could type. Mark one as an identifier in the table builder.',
    );
    checkGrid('lookupResultFields', selectableFields(), cfg.resultFields || [], 'This table has no visible fields yet.');
    checkGrid('listSearchFields', selectableFields(), cfg.searchFields || [], 'This table has no visible fields yet.');

    const sort = document.getElementById('listSortField');
    sort.innerHTML =
        '<option value="">Newest first (created date)</option>' +
        selectableFields()
        .map(
            (f) =>
            `<option value="${escapeHtml(f.name)}" ${cfg.sortField === f.name ? 'selected' : ''}>${escapeHtml(f.label || f.name)}</option>`,
        )
        .join('');
}

function applyKindConfig(cfg) {
    formConfigDraft = cfg || {};
    const renderers = cfg.renderers || {};
    listColumns = (cfg.columns || []).map((name) => ({
        name,
        render: renderers[name] || ''
    }));
    listActions = (cfg.actions || []).map((a) => ({
        label: a.label || '',
        hrefExpr: a.hrefExpr || ''
    }));
    renderKindFieldPickers(cfg);
    renderListBuilder();
    renderListActions();
    renderListFilters(cfg.filters);
    document.getElementById('lookupNotFound').value = cfg.notFoundText || '';
    document.getElementById('listSortDir').value = cfg.sortDir === 'asc' ? 'asc' : 'desc';
    document.getElementById('listPageSize').value = cfg.pageSize || 25;
    document.getElementById('formSuccessRedirect').value = cfg.onSuccessRedirect || '';
}

/* LIST kind: column builder. Same palette-and-canvas shape as the submit builder. */

let listColumns = []; // [{ name, render }] in display order
let listActions = []; // [{ label, hrefExpr }] — per-row buttons, not bound to a single column

function renderListPalette() {
    const ul = document.getElementById('listPalette');
    if (!ul) return;
    ul.innerHTML = '';
    selectableFields()
        .filter((f) => !listColumns.some((c) => c.name === f.name))
        .forEach((f) => {
            const li = document.createElement('li');
            li.className = 'palette-item';
            li.draggable = true;
            li.innerText = `${f.name} · ${f.dataType}`;
            li.addEventListener('dragstart', (ev) => ev.dataTransfer.setData('text/column', f.name));
            ul.appendChild(li);
        });
    if (!ul.children.length) {
        const li = document.createElement('li');
        li.className = 'muted';
        li.innerText = 'Every field is already a column.';
        ul.appendChild(li);
    }
}

function renderListCanvas() {
    const canvas = document.getElementById('listCanvas');
    if (!canvas) return;
    canvas.innerHTML = '';

    if (!listColumns.length) {
        const empty = document.createElement('div');
        empty.className = 'canvas-empty';
        empty.innerText = 'Drag fields here to make them columns.';
        canvas.appendChild(empty);
    }

    listColumns.forEach((col, index) => {
        const field = formTableFields.find((f) => f.name === col.name);
        const row = document.createElement('div');
        row.className = 'brow column-row';
        row.draggable = true;
        row.dataset.index = index;

        const head = document.createElement('div');
        head.className = 'brow-head';
        const label = document.createElement('span');
        label.className = 'brow-type';
        label.innerText = `${col.name}${field ? ' · ' + field.dataType : ''}`;
        const actions = document.createElement('div');
        actions.className = 'brow-actions';

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn btn-outline btn-sm';
        remove.innerText = '✕';
        remove.title = 'Remove column';
        remove.onclick = () => {
            listColumns.splice(index, 1);
            renderListBuilder();
        };
        actions.appendChild(remove);
        head.appendChild(label);
        head.appendChild(actions);
        row.appendChild(head);

        const renderRow = document.createElement('label');
        renderRow.className = 'brow-field-label';
        renderRow.innerText = 'Render expression (optional)';
        const input = document.createElement('input');
        input.className = 'input input-sm mono';
        input.value = col.render || '';
        input.placeholder = `'<strong>' + data.${col.name} + '</strong>'`;
        input.onchange = () => {
            col.render = input.value.trim();
        };
        renderRow.appendChild(input);
        row.appendChild(renderRow);

        wireColumnDrag(row);
        canvas.appendChild(row);
    });
}

function renderListBuilder() {
    renderListPalette();
    renderListCanvas();
}

// data.Id isn't a real field, so the placeholder names an actual field instead of implying it'd resolve
const LINK_EXPR_PLACEHOLDER = "'/view?ref=' + encodeURIComponent(data.YourIdentifierField)";

// Per-row buttons (e.g. "View", "Approve"): a label and a URL built from that row's own data, not tied to one column.
function addListAction() {
    listActions.push({
        label: '',
        hrefExpr: ''
    });
    renderListActions();
}

function renderListActions() {
    const wrap = document.getElementById('listActions');
    if (!wrap) return;
    wrap.innerHTML = '';
    listActions.forEach((action, index) => {
        const row = document.createElement('div');
        row.className = 'brow-fields';

        const labelLab = labeledInput('Label', action, 'label', 'View');
        row.appendChild(labelLab);

        const hrefLab = labeledInput('URL expression', action, 'hrefExpr', LINK_EXPR_PLACEHOLDER);
        hrefLab.appendChild(testExprButton(() => action.hrefExpr));
        row.appendChild(hrefLab);

        const rm = document.createElement('button');
        rm.type = 'button';
        rm.className = 'btn btn-ghost btn-sm';
        rm.title = 'Remove action';
        rm.innerText = '✕';
        rm.onclick = () => {
            listActions.splice(index, 1);
            renderListActions();
        };
        row.appendChild(rm);

        wrap.appendChild(row);
    });
}

let columnDragFrom = null;

function wireColumnDrag(row) {
    row.addEventListener('dragstart', (ev) => {
        columnDragFrom = Number(row.dataset.index);
        row.classList.add('dragging');
        ev.dataTransfer.setData('text/move-column', String(columnDragFrom));
    });
    row.addEventListener('dragend', () => {
        columnDragFrom = null;
        document.querySelectorAll('.column-row').forEach((r) => r.classList.remove('dragging'));
    });
    row.addEventListener('dragover', (ev) => ev.preventDefault());
    row.addEventListener('drop', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        const over = Number(row.dataset.index);

        const added = ev.dataTransfer.getData('text/column');
        if (added) return insertColumn(added, over);

        if (columnDragFrom === null) return;
        const moved = listColumns.splice(columnDragFrom, 1)[0];
        listColumns.splice(over, 0, moved);
        renderListBuilder();
    });
}

function insertColumn(name, at) {
    if (listColumns.some((c) => c.name === name)) return;
    listColumns.splice(at === undefined ? listColumns.length : at, 0, {
        name,
        render: ''
    });
    renderListBuilder();
}

(function wireListCanvasDrop() {
    const canvas = document.getElementById('listCanvas');
    if (!canvas) return;
    canvas.addEventListener('dragover', (ev) => ev.preventDefault());
    canvas.addEventListener('drop', (ev) => {
        ev.preventDefault();
        const added = ev.dataTransfer.getData('text/column');
        if (added) insertColumn(added);
    });
})();

/* list filters */

const FILTER_OPS = [
    ['eq', 'equals'],
    ['ne', 'does not equal'],
    ['gt', 'greater than'],
    ['lt', 'less than'],
    ['contains', 'contains'],
];

function renderListFilters(filters) {
    const host = document.getElementById('listFilters');
    host.innerHTML = '';
    (filters || []).forEach((f, i) => host.appendChild(filterRow(f, i)));
}

function filterRow(f, index) {
    const row = document.createElement('div');
    row.className = 'filter-row';

    const field = document.createElement('select');
    field.className = 'input';
    field.innerHTML = selectableFields()
        .map(
            (x) =>
            `<option value="${escapeHtml(x.name)}" ${x.name === f.field ? 'selected' : ''}>${escapeHtml(x.label || x.name)}</option>`,
        )
        .join('');

    const op = document.createElement('select');
    op.className = 'input';

    const value = document.createElement('input');
    value.className = 'input filter-value';

    // The control follows the field: "greater than" on a select, or free text on an enum, invites filters that can never match.
    function syncToField() {
        const chosen = formTableFields.find((x) => x.name === field.value);
        const type = chosen ? chosen.dataType : 'text';
        const numeric = type === 'number' || type === 'currency';
        const enumerated = type === 'select' || type === 'multiselect';

        const allowed = numeric ?
            FILTER_OPS :
            enumerated ?
            FILTER_OPS.filter(([v]) => v === 'eq' || v === 'ne') :
            FILTER_OPS.filter(([v]) => v !== 'gt' && v !== 'lt');
        const keep = allowed.some(([v]) => v === op.value) ? op.value : allowed[0][0];
        op.innerHTML = allowed
            .map(([v, l]) => `<option value="${v}" ${v === keep ? 'selected' : ''}>${l}</option>`)
            .join('');

        if (enumerated) {
            const options = parseFieldOptions(chosen);
            const current = value.value;
            const select = document.createElement('select');
            select.className = 'input filter-value';
            select.innerHTML = options
                .map((o) => `<option value="${escapeHtml(o)}" ${o === current ? 'selected' : ''}>${escapeHtml(o)}</option>`)
                .join('');
            value.replaceWith(select);
            return;
        }
        value.type = numeric ? 'number' : 'text';
        value.placeholder = numeric ? '0' : 'Value';
        // Suggest values already in the table; "contains" keeps partial matches working.
        value.setAttribute('list', `filter-values-${index}`);
    }

    field.onchange = syncToField;
    value.value = f.value || '';

    const suggestions = document.createElement('datalist');
    suggestions.id = `filter-values-${index}`;

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'btn btn-ghost btn-sm';
    remove.innerText = '×';
    remove.title = 'Remove filter';
    remove.onclick = () => {
        const all = collectListFilters();
        all.splice(index, 1);
        renderListFilters(all);
    };

    row.append(field, op, value, suggestions, remove);
    op.value = f.op || 'eq';
    syncToField();
    op.value = f.op || op.value;
    loadFilterSuggestions(field.value, suggestions);
    return row;
}

function parseFieldOptions(field) {
    try {
        const parsed = JSON.parse((field && field.optionsJson) || '[]');
        return Array.isArray(parsed) ? parsed : [];
    } catch (e) {
        return [];
    }
}

// Distinct stored values for a field, so a filter is picked rather than typed.
async function loadFilterSuggestions(fieldName, datalist) {
    const table = document.getElementById('formTable').value;
    if (!table || !fieldName) return;
    const data = await fetch(`/api/_admin/tables/${table}/records?pageSize=50`)
        .then((r) => r.json())
        .catch(() => null);
    if (!data || !data.rows) return;
    const seen = [
        ...new Set(
            data.rows.map((r) => r.data && r.data[fieldName]).filter((v) => v !== null && v !== undefined && v !== ''),
        ),
    ];
    datalist.innerHTML = seen
        .slice(0, 25)
        .map((v) => `<option value="${escapeHtml(String(v))}"></option>`)
        .join('');
}

function addListFilter() {
    if (selectableFields().length === 0) return;
    renderListFilters(collectListFilters().concat({
        field: selectableFields()[0].name,
        op: 'eq',
        value: ''
    }));
}

function collectListFilters() {
    return Array.from(document.querySelectorAll('#listFilters .filter-row')).map((row) => {
        const [field, op] = row.querySelectorAll('select');
        const value = row.querySelector('.filter-value');
        return {
            field: field.value,
            op: op.value,
            value: value.value
        };
    });
}

function collectConfig() {
    if (formKind === 'form') {
        const cfg = {};
        if (formActions.includes('lookup')) {
            cfg.matchFields = checkedValues('lookupMatchFields');
            cfg.resultFields = checkedValues('lookupResultFields');
            cfg.notFoundText = document.getElementById('lookupNotFound').value.trim();
        }
        if (formActions.includes('submit')) {
            cfg.onSuccessRedirect = document.getElementById('formSuccessRedirect').value.trim();
        }
        return cfg;
    }
    if (formKind === 'list') {
        const renderers = {};
        listColumns
            .filter((c) => c.render)
            .forEach((c) => {
                renderers[c.name] = c.render;
            });
        return {
            columns: listColumns.map((c) => c.name),
            renderers,
            actions: listActions.filter((a) => a.label.trim() && a.hrefExpr.trim()),
            searchFields: checkedValues('listSearchFields'),
            filters: collectListFilters().filter((f) => f.field),
            sortField: document.getElementById('listSortField').value,
            sortDir: document.getElementById('listSortDir').value,
            pageSize: Number(document.getElementById('listPageSize').value) || 25,
        };
    }
    return {};
}

function parseConfig(json) {
    try {
        return JSON.parse(json || '{}') || {};
    } catch (e) {
        return {};
    }
}

/* save */

// same shape saveForm posts; also doubles as the dirty-check snapshot so both read one definition of "the form's state"
function formSnapshot() {
    return {
        tableId: document.getElementById('formTable').value,
        kind: formKind,
        actions: formActions,
        title: document.getElementById('formTitle').value.trim(),
        description: document.getElementById('formDescription').value.trim(),
        layoutJson: formKind === 'form' && formActions.includes('submit') ? JSON.stringify(layout) : '[]',
        configJson: JSON.stringify({
            ...formConfigDraft,
            ...collectConfig()
        }),
        isPublished: document.getElementById('formPublished').checked,
    };
}

function hasUnsavedFormChanges() {
    const editor = document.getElementById('formEditor');
    if (!editor || editor.classList.contains('hidden')) return false;
    return formOriginalSnapshot !== null && JSON.stringify(formSnapshot()) !== formOriginalSnapshot;
}

async function saveForm(btn) {
    await ui.busy(btn, async () => {
        const body = formSnapshot();

        if (!body.title) {
            ui.toast('Form title is required.', 'error');
            return;
        }

        const res = formEditingId ?
            await fetch(`/api/_admin/forms/${formEditingId}`, {
                method: 'PATCH',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(body),
            }) :
            await fetch('/api/_admin/forms', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(body),
            });

        const saved = await ui.handle(res, {
            success: formEditingId ? 'Form saved.' : 'Form published.',
            failure: 'Failed to save the form.',
        });
        if (!saved) return;
        formOriginalSnapshot = JSON.stringify(body); // just persisted; navigate() below must not think this is still unsaved
        await loadTables();
        await navigate(`/forms/${saved.id}`);
    });
}

/* SUBMIT kind: the matrix layout designer. */
function renderPalette() {
    const ul = document.getElementById('paletteFields');
    if (!ul) return;
    ul.innerHTML = '';
    selectableFields().forEach((f) => {
        const li = document.createElement('li');
        li.className = 'palette-item';
        li.draggable = true;
        li.innerText = `${f.name} · ${f.dataType}`;
        li.addEventListener('dragstart', (ev) => ev.dataTransfer.setData('text/field', f.name));
        ul.appendChild(li);
    });
}

function parseLayout(layoutJson) {
    try {
        const p = JSON.parse(layoutJson || '[]');
        if (p && Array.isArray(p.rows)) return p;
        // Legacy layouts were a plain array of rows of field names.
        if (Array.isArray(p))
            return {
                rows: p.map((r) => ({
                    t: 'row',
                    cols: [{
                        t: 'col',
                        w: 12,
                        items: r.filter((x) => x !== 'spacer')
                    }]
                })),
            };
    } catch (e) {}
    return {
        rows: []
    };
}

function emptyCol() {
    return {
        t: 'col',
        w: 12,
        items: []
    };
}

function addRow(type, atIndex) {
    let row;
    if (type === 'group') row = {
        t: 'group',
        title: 'Group',
        cols: [emptyCol()]
    };
    else if (type === 'row') row = {
        t: 'row',
        cols: [emptyCol()]
    };
    else if (type === 'subtotal') row = {
        t: 'subtotal',
        label: 'Total',
        expr: '',
        format: 'currency'
    };
    else if (type === 'button') row = {
        t: 'button',
        label: 'Button',
        action: 'submit'
    };
    if (!row) return;
    layout.rows.splice(atIndex, 0, row);
    renderCanvas();
}

function moveRow(ri, dir) {
    const to = ri + dir;
    if (to < 0 || to >= layout.rows.length) return;
    const r = layout.rows.splice(ri, 1)[0];
    layout.rows.splice(to, 0, r);
    renderCanvas();
}

function labeledInput(label, row, prop, ph) {
    const lab = document.createElement('label');
    lab.className = 'brow-field-label';
    lab.innerText = label;
    const inp = document.createElement('input');
    inp.className = 'input input-sm';
    inp.value = row[prop] || '';
    inp.placeholder = ph || '';
    inp.onchange = () => {
        row[prop] = inp.value;
    };
    lab.appendChild(inp);
    return lab;
}

// test button for url-building expressions; hidden fields are valid refs since an expression often surfaces an id nobody sees
function testExprButton(getExpr) {
    const wrap = document.createElement('span');
    wrap.className = 'expr-test';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-outline btn-sm';
    btn.innerText = 'Test';
    const hint = document.createElement('span');
    hint.className = 'muted field-hint';
    btn.onclick = async () => {
        const expr = (getExpr() || '').trim();
        if (!expr) {
            hint.innerText = 'Enter an expression to test.';
            hint.style.color = '#d63d3d';
            return;
        }
        const original = btn.innerText;
        btn.disabled = true;
        btn.innerText = '…';
        const r = await fetch('/api/_admin/validate-expression', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                expression: expr,
                fieldNames: formTableFields.map((f) => f.name),
                tableId: document.getElementById('formTable').value,
            }),
        }).then((res) => res.json());
        btn.disabled = false;
        btn.innerText = original;
        if (r.valid) {
            const refs = (r.referencedFields || []).join(', ') || 'no field references';
            hint.innerText = '✓ Valid, ' + refs + (r.sampleOutput ? ` · example: ${r.sampleOutput}` : '');
            hint.style.color = '#2d9d5f';
        } else {
            hint.innerText = '✕ ' + (r.errors || []).join('; ');
            hint.style.color = '#d63d3d';
        }
    };
    wrap.appendChild(btn);
    wrap.appendChild(hint);
    return wrap;
}

function rowFields(row) {
    const div = document.createElement('div');
    div.className = 'brow-fields';
    const dropdown = (label, prop, options, fallback) => {
        const sel = document.createElement('select');
        sel.className = 'input input-sm';
        sel.innerHTML = options.map(([v, l]) => `<option value="${v}">${l}</option>`).join('');
        sel.value = row[prop] || fallback;
        sel.onchange = () => {
            row[prop] = sel.value;
        };
        const lab = document.createElement('label');
        lab.className = 'brow-field-label';
        lab.innerText = label;
        lab.appendChild(sel);
        return lab;
    };

    if (row.t === 'subtotal') {
        div.appendChild(labeledInput('Label', row, 'label', 'Total'));
        div.appendChild(labeledInput('Expression', row, 'expr', 'data.Quantity * data.Price'));
        div.appendChild(
            dropdown(
                'Format',
                'format',
                [
                    ['plain', 'Plain'],
                    ['currency', 'Currency'],
                ],
                'plain',
            ),
        );
    } else if (row.t === 'button') {
        div.appendChild(labeledInput('Label', row, 'label', 'Submit'));

        // Switching action reveals a different extra field, so this select re-renders the row instead of going through the generic dropdown().
        const actionLab = document.createElement('label');
        actionLab.className = 'brow-field-label';
        actionLab.innerText = 'Action';
        const actionSel = document.createElement('select');
        actionSel.className = 'input input-sm';
        actionSel.innerHTML = [
                ['submit', 'Submit'],
                ['reset', 'Reset'],
                ['cancel', 'Cancel'],
                ['validate', 'Validate'],
                ['link', 'Link'],
            ]
            .map(([v, l]) => `<option value="${v}">${l}</option>`)
            .join('');
        actionSel.value = row.action || 'submit';
        actionSel.onchange = () => {
            row.action = actionSel.value;
            renderCanvas();
        };
        actionLab.appendChild(actionSel);
        div.appendChild(actionLab);

        if (row.action === 'cancel') {
            div.appendChild(labeledInput('Href (blank = go back)', row, 'href', '/thanks'));
        } else if (row.action === 'link') {
            const hrefLab = labeledInput('URL expression', row, 'hrefExpr', LINK_EXPR_PLACEHOLDER);
            hrefLab.appendChild(testExprButton(() => row.hrefExpr));
            div.appendChild(hrefLab);
        }
    } else if (row.t === 'group') {
        div.appendChild(labeledInput('Title', row, 'title', 'Group'));
    }
    return div;
}

function renderCanvas() {
    const canvas = document.getElementById('layoutCanvas');
    if (!canvas) return;
    canvas.innerHTML = '';
    if (!layout.rows.length) {
        const empty = document.createElement('div');
        empty.className = 'canvas-empty';
        empty.innerText = 'Drag fields into columns, or drop a block here to start.';
        canvas.appendChild(empty);
    }

    layout.rows.forEach((row, ri) => {
        const el = document.createElement('div');
        el.className = 'brow' + (row.t === 'group' ? ' brow-group' : '');
        el.addEventListener('dragover', (ev) => ev.preventDefault());
        el.addEventListener('drop', (ev) => {
            ev.preventDefault();
            ev.stopPropagation();
            const block = ev.dataTransfer.getData('text/block');
            if (block) addRow(block, ri);
        });

        const head = document.createElement('div');
        head.className = 'brow-head';
        const type = document.createElement('span');
        type.className = 'brow-type';
        type.innerText = row.t;
        const actions = document.createElement('div');
        actions.className = 'brow-actions';

        const mkBtn = (label, fn) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'btn btn-outline btn-sm';
            b.innerText = label;
            b.onclick = fn;
            actions.appendChild(b);
        };
        if (row.t === 'row' || row.t === 'group')
            mkBtn('+', () => {
                row.cols.push(emptyCol());
                renderCanvas();
            });
        mkBtn('↑', () => moveRow(ri, -1));
        mkBtn('↓', () => moveRow(ri, 1));
        mkBtn('✕', () => {
            layout.rows.splice(ri, 1);
            renderCanvas();
        });

        head.appendChild(type);
        head.appendChild(actions);
        el.appendChild(head);

        if (row.t === 'subtotal' || row.t === 'button') {
            el.appendChild(rowFields(row));
        } else {
            const body = document.createElement('div');
            body.className = 'brow-cols';
            row.cols.forEach((col, ci) => body.appendChild(renderColumn(row, ri, col, ci)));
            el.appendChild(body);
        }
        canvas.appendChild(el);
    });
    document.getElementById('formLayout').value = JSON.stringify(layout);
}

const COL_WIDTHS = [
    [12, 'Full'],
    [9, 'Three quarters'],
    [8, 'Two thirds'],
    [6, 'Half'],
    [4, 'One third'],
    [3, 'One quarter'],
];

function renderColumn(row, ri, col, ci) {
    const colEl = document.createElement('div');
    colEl.className = 'bcol';
    colEl.style.flexGrow = col.w || 12;
    colEl.addEventListener('dragover', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        colEl.classList.add('drop-hover');
    });
    colEl.addEventListener('dragleave', () => colEl.classList.remove('drop-hover'));
    colEl.addEventListener('drop', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        colEl.classList.remove('drop-hover');
        const field = ev.dataTransfer.getData('text/field');
        if (!field) return;

        // A chip dragged from another column carries its origin, so the move removes it there instead of duplicating the field.
        let moved = null;
        try {
            moved = JSON.parse(ev.dataTransfer.getData('text/movefield') || 'null');
        } catch (e) {}
        if (moved && moved.row === ri && moved.col === ci) {
            renderCanvas();
            return;
        }
        if (moved && layout.rows[moved.row] && layout.rows[moved.row].cols[moved.col]) {
            const src = layout.rows[moved.row].cols[moved.col].items;
            const idx = src.indexOf(field);
            if (idx >= 0) src.splice(idx, 1);
        }
        col.items.push(field);
        renderCanvas();
    });

    col.items.forEach((item) => {
        const chip = document.createElement('span');
        chip.className = 'chip';
        chip.innerText = item;
        chip.draggable = true;
        chip.addEventListener('dragstart', (ev) => {
            ev.dataTransfer.setData('text/field', item);
            ev.dataTransfer.setData('text/movefield', JSON.stringify({
                row: ri,
                col: ci
            }));
        });
        const x = document.createElement('span');
        x.className = 'chip-x';
        x.innerText = '×';
        x.onclick = () => {
            col.items = col.items.filter((i) => i !== item);
            renderCanvas();
        };
        chip.appendChild(x);
        colEl.appendChild(chip);
    });

    if (!col.items.length) {
        const hint = document.createElement('span');
        hint.className = 'drop-hint';
        hint.innerText = 'Drop field here';
        colEl.appendChild(hint);
    }

    const controls = document.createElement('div');
    controls.className = 'bcol-controls';
    const widthSel = document.createElement('select');
    widthSel.className = 'input input-sm';
    widthSel.title = 'Column width';
    widthSel.innerHTML = COL_WIDTHS.map(([v, l]) => `<option value="${v}">${l}</option>`).join('');
    widthSel.value = col.w || 12;
    widthSel.onchange = () => {
        col.w = Number(widthSel.value);
        renderCanvas();
    };
    controls.appendChild(widthSel);
    if (row.cols.length > 1) {
        const rm = document.createElement('button');
        rm.type = 'button';
        rm.className = 'btn btn-ghost btn-sm';
        rm.title = 'Remove column';
        rm.innerText = '−';
        rm.onclick = () => {
            row.cols.splice(ci, 1);
            renderCanvas();
        };
        controls.appendChild(rm);
    }
    colEl.appendChild(controls);
    return colEl;
}

/* designer wiring */

document.getElementById('formLayout').addEventListener('change', (ev) => {
    try {
        const p = JSON.parse(ev.target.value);
        if (!p || !Array.isArray(p.rows)) return;
        layout = p;
        renderCanvas();
    } catch (e) {
        /* invalid JSON is left for the author to fix */
    }
});

document.querySelectorAll('.builder-palette [data-block]').forEach((li) => {
    li.addEventListener('dragstart', (ev) => ev.dataTransfer.setData('text/block', li.dataset.block));
    li.addEventListener('click', () => addRow(li.dataset.block, layout.rows.length));
});

(function wireCanvasDrops() {
    const canvas = document.getElementById('layoutCanvas');
    canvas.addEventListener('dragover', (ev) => ev.preventDefault());
    canvas.addEventListener('drop', (ev) => {
        ev.preventDefault();
        const block = ev.dataTransfer.getData('text/block');
        if (block) {
            addRow(block, layout.rows.length);
            return;
        }
        const field = ev.dataTransfer.getData('text/field');
        if (field) {
            layout.rows.push({
                t: 'row',
                cols: [{
                    t: 'col',
                    w: 12,
                    items: [field]
                }]
            });
            renderCanvas();
        }
    });
})();

(function wireSuccessRedirectTest() {
    const input = document.getElementById('formSuccessRedirect');
    input.closest('.input-group').appendChild(testExprButton(() => input.value));
})();