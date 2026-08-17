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
    resetLayoutHistory();
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
    resetLayoutHistory();
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
    // A form's layout is a property of the form, not of the submit toggle: it's built once and takes effect
    // whenever submit is later turned on, so the builder stays visible (and its data stays saved, see
    // formSnapshot) regardless of which actions happen to be on right now.
    document.getElementById('kindSubmit').classList.toggle('hidden', formKind !== 'form');
    document.getElementById('kindLookup').classList.toggle('hidden', formKind !== 'form' || !doesLookup);
    document.getElementById('kindList').classList.toggle('hidden', formKind !== 'list');
    document.getElementById('submitInactiveHint').classList.toggle('hidden', formKind !== 'form' || doesSubmit);

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

    if (formKind === 'form') renderCanvas();
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
    // Columns, actions, filters and the lookup result order name the previous table's fields, so they cannot survive the switch.
    listColumns = [];
    listActions = [];
    lookupResultOrder = [];
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
    document.getElementById('lookupMatchFields').onchange = () => syncLookupOnboardNav();
    lookupResultOrder = (cfg.resultFields || []).filter((n) => selectableFields().some((f) => f.name === n));
    renderLookupResultBuilder();
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

    // A brand-new lookup (nothing chosen yet) walks through Match on / Show / Not-found one at a time;
    // an already-configured one shows them flat, so re-opening a working form never re-triggers the wizard.
    lookupOnboardStep = (cfg.matchFields || []).length === 0 && (cfg.resultFields || []).length === 0 ? 0 : -1;
    renderLookupOnboardNav();
}

/* LOOKUP kind: first-run onboarding wizard around Match on / Show / Not-found. */

let lookupOnboardStep = -1; // -1 = flat panel (default for an already-configured lookup), 0-2 = wizard step

function renderLookupOnboardNav() {
    const nav = document.getElementById('lookupOnboardNav');
    const onboarding = lookupOnboardStep >= 0;
    nav.classList.toggle('hidden', !onboarding);
    ['lookupStepMatch', 'lookupStepShow', 'lookupStepNotFound'].forEach((id, i) => {
        document.getElementById(id).classList.toggle('hidden', onboarding && lookupOnboardStep !== i);
    });
    if (!onboarding) return;
    nav.querySelectorAll('.seg-btn').forEach((b) => b.classList.toggle('active', Number(b.dataset.step) === lookupOnboardStep));
    document.getElementById('lookupOnboardBack').classList.toggle('hidden', lookupOnboardStep === 0);
    document.getElementById('lookupOnboardNext').innerText = lookupOnboardStep === 2 ? 'Finish setup' : 'Next';
    syncLookupOnboardNav();
}

// Gates Next on the current step actually having something in it, so onboarding can't complete an empty lookup.
function syncLookupOnboardNav() {
    if (lookupOnboardStep < 0) return;
    const nextBtn = document.getElementById('lookupOnboardNext');
    if (lookupOnboardStep === 0) nextBtn.disabled = checkedValues('lookupMatchFields').length === 0;
    else if (lookupOnboardStep === 1) nextBtn.disabled = lookupResultOrder.length === 0;
    else nextBtn.disabled = false;
}

function lookupOnboardNext() {
    lookupOnboardStep = lookupOnboardStep >= 2 ? -1 : lookupOnboardStep + 1;
    renderLookupOnboardNav();
}

function lookupOnboardBack() {
    if (lookupOnboardStep > 0) lookupOnboardStep--;
    renderLookupOnboardNav();
}

function lookupOnboardSkip() {
    lookupOnboardStep = -1;
    renderLookupOnboardNav();
}

/* LOOKUP kind: "Show" field order. Same palette-and-canvas drag-reorder shape as the list column builder
   below, kept as its own small copy rather than parameterizing that one: two call sites don't earn a
   generalized abstraction, and the list builder's tests pin its exact functions. */

let lookupResultOrder = []; // field names, in display order
let lookupResultDragFrom = null;

function renderLookupResultPalette() {
    const ul = document.getElementById('lookupResultPalette');
    if (!ul) return;
    ul.innerHTML = '';
    selectableFields()
        .filter((f) => !lookupResultOrder.includes(f.name))
        .forEach((f) => {
            const li = document.createElement('li');
            li.className = 'palette-item';
            li.draggable = true;
            li.innerText = `${f.name} · ${f.dataType}`;
            li.addEventListener('dragstart', (ev) => ev.dataTransfer.setData('text/lookup-result', f.name));
            ul.appendChild(li);
        });
    if (!ul.children.length) {
        const li = document.createElement('li');
        li.className = 'muted';
        li.innerText = 'Every field is already shown.';
        ul.appendChild(li);
    }
}

function renderLookupResultCanvas() {
    const canvas = document.getElementById('lookupResultCanvas');
    if (!canvas) return;
    canvas.innerHTML = '';
    if (!lookupResultOrder.length) {
        const empty = document.createElement('div');
        empty.className = 'canvas-empty';
        empty.innerText = 'Drag fields here to show them when a record is found.';
        canvas.appendChild(empty);
    }

    lookupResultOrder.forEach((name, index) => {
        const field = formTableFields.find((f) => f.name === name);
        const row = document.createElement('div');
        row.className = 'brow column-row';
        row.draggable = true;
        row.dataset.index = index;

        const head = document.createElement('div');
        head.className = 'brow-head';
        const label = document.createElement('span');
        label.className = 'brow-field-name';
        label.innerText = `${field ? field.label || field.name : name}${field ? ' · ' + field.dataType : ''}`;
        const actions = document.createElement('div');
        actions.className = 'brow-actions';

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'btn btn-outline btn-sm';
        remove.innerText = '✕';
        remove.title = 'Remove';
        remove.onclick = () => {
            lookupResultOrder.splice(index, 1);
            renderLookupResultBuilder();
        };
        actions.appendChild(remove);
        head.appendChild(label);
        head.appendChild(actions);
        row.appendChild(head);

        wireLookupResultDrag(row);
        canvas.appendChild(row);
    });
}

function renderLookupResultBuilder() {
    renderLookupResultPalette();
    renderLookupResultCanvas();
    syncLookupOnboardNav();
}

function wireLookupResultDrag(row) {
    row.addEventListener('dragstart', (ev) => {
        lookupResultDragFrom = Number(row.dataset.index);
        row.classList.add('dragging');
        ev.dataTransfer.setData('text/move-lookup-result', String(lookupResultDragFrom));
    });
    row.addEventListener('dragend', () => {
        lookupResultDragFrom = null;
        document.querySelectorAll('#lookupResultCanvas .column-row').forEach((r) => r.classList.remove('dragging'));
    });
    row.addEventListener('dragover', (ev) => ev.preventDefault());
    row.addEventListener('drop', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        const over = Number(row.dataset.index);

        const added = ev.dataTransfer.getData('text/lookup-result');
        if (added) return insertLookupResultField(added, over);

        if (lookupResultDragFrom === null) return;
        const moved = lookupResultOrder.splice(lookupResultDragFrom, 1)[0];
        lookupResultOrder.splice(over, 0, moved);
        renderLookupResultBuilder();
    });
}

function insertLookupResultField(name, at) {
    if (lookupResultOrder.includes(name)) return;
    lookupResultOrder.splice(at === undefined ? lookupResultOrder.length : at, 0, name);
    renderLookupResultBuilder();
}

(function wireLookupResultCanvasDrop() {
    const canvas = document.getElementById('lookupResultCanvas');
    if (!canvas) return;
    canvas.addEventListener('dragover', (ev) => ev.preventDefault());
    canvas.addEventListener('drop', (ev) => {
        ev.preventDefault();
        const added = ev.dataTransfer.getData('text/lookup-result');
        if (added) insertLookupResultField(added);
    });
})();

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
        label.className = 'brow-field-name';
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
            cfg.resultFields = lookupResultOrder.slice();
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
        layoutJson: formKind === 'form' ? JSON.stringify(layout) : '[]',
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
        // Legacy layouts were an array of rows of field names.
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
    else if (type === 'container') row = {
        t: 'container',
        title: 'Section',
        rows: [{
            t: 'row',
            cols: [emptyCol()]
        }]
    };
    else if (type === 'line_items') row = {
        t: 'line_items',
        field: ''
    };
    else if (type === 'button_bar') row = {
        t: 'button_bar',
        align: 'flex-end',
        buttons: [{
            label: 'Submit',
            action: 'submit'
        }]
    };
    if (!row) return;
    layout.rows.splice(atIndex, 0, row);
    renderCanvas();
}

function moveRowIn(arr, ri, dir) {
    const to = ri + dir;
    if (to < 0 || to >= arr.length) return;
    const r = arr.splice(ri, 1)[0];
    arr.splice(to, 0, r);
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
                ['run', 'Run expression'],
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
        } else if (row.action === 'run') {
            // A blank button: no fixed outcome, just this expression evaluated on click and shown as a toast.
            const exprLab = labeledInput('Expression', row, 'expr', "'Total: ' + (data.Qty * data.Price)");
            exprLab.appendChild(testExprButton(() => row.expr));
            div.appendChild(exprLab);
        }
    } else if (row.t === 'group') {
        div.appendChild(labeledInput('Title', row, 'title', 'Group'));
    } else if (row.t === 'line_items') {
        const candidates = lineItemFieldCandidates();
        if (!candidates.length) {
            const hint = document.createElement('p');
            hint.className = 'muted field-hint';
            hint.innerText = 'No array field with line-item columns yet. Add columns to an array field in the table schema first.';
            div.appendChild(hint);
        } else {
            if (!row.field || !candidates.some((f) => f.name === row.field)) row.field = candidates[0].name;
            const lab = document.createElement('label');
            lab.className = 'brow-field-label';
            lab.innerText = 'Line-item field';
            const sel = document.createElement('select');
            sel.className = 'input input-sm';
            sel.innerHTML = candidates
                .map((f) => `<option value="${f.name}" ${row.field === f.name ? 'selected' : ''}>${f.label || f.name}</option>`)
                .join('');
            sel.onchange = () => {
                row.field = sel.value;
            };
            lab.appendChild(sel);
            div.appendChild(lab);
        }
    } else if (row.t === 'button_bar') {
        div.appendChild(buttonBarEditor(row));
    }
    return div;
}

// Columns configured on an array field (line-items sub-schema), same shape FieldValidation.ArrayColumns parses server-side.
function clientArrayColumns(optionsJson) {
    try {
        const o = JSON.parse(optionsJson || '{}');
        return Array.isArray(o.columns) && o.columns.length ? o.columns : null;
    } catch (e) {
        return null;
    }
}

function lineItemFieldCandidates() {
    return formTableFields.filter((f) => f.dataType === 'array' && clientArrayColumns(f.optionsJson));
}

// A button_bar groups several buttons behind one alignment, unlike a standalone "button" block which is one-per-row.
function buttonBarEditor(row) {
    const wrap = document.createElement('div');
    if (!Array.isArray(row.buttons)) row.buttons = [];

    const alignLab = document.createElement('label');
    alignLab.className = 'brow-field-label';
    alignLab.innerText = 'Alignment';
    const alignSel = document.createElement('select');
    alignSel.className = 'input input-sm';
    alignSel.innerHTML = [
            ['flex-start', 'Left'],
            ['center', 'Center'],
            ['flex-end', 'Right'],
            ['space-between', 'Space between'],
        ]
        .map(([v, l]) => `<option value="${v}" ${(row.align || 'flex-end') === v ? 'selected' : ''}>${l}</option>`)
        .join('');
    alignSel.onchange = () => {
        row.align = alignSel.value;
    };
    alignLab.appendChild(alignSel);
    wrap.appendChild(alignLab);

    const list = document.createElement('div');
    wrap.appendChild(list);

    function renderButtons() {
        list.innerHTML = '';
        row.buttons.forEach((btn, i) => {
            const line = document.createElement('div');
            line.className = 'brow-fields';
            line.appendChild(labeledInput('Label', btn, 'label', 'Save'));

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
                    ['run', 'Run expression'],
                ]
                .map(([v, l]) => `<option value="${v}" ${(btn.action || 'submit') === v ? 'selected' : ''}>${l}</option>`)
                .join('');
            actionSel.value = btn.action || 'submit';
            actionSel.onchange = () => {
                btn.action = actionSel.value;
                renderButtons();
            };
            actionLab.appendChild(actionSel);
            line.appendChild(actionLab);

            if (btn.action === 'cancel') {
                line.appendChild(labeledInput('Href (blank = go back)', btn, 'href', '/thanks'));
            } else if (btn.action === 'link') {
                const hrefLab = labeledInput('URL expression', btn, 'hrefExpr', LINK_EXPR_PLACEHOLDER);
                hrefLab.appendChild(testExprButton(() => btn.hrefExpr));
                line.appendChild(hrefLab);
            } else if (btn.action === 'run') {
                const exprLab = labeledInput('Expression', btn, 'expr', "'Total: ' + (data.Qty * data.Price)");
                exprLab.appendChild(testExprButton(() => btn.expr));
                line.appendChild(exprLab);
            }

            const rm = document.createElement('button');
            rm.type = 'button';
            rm.className = 'btn btn-ghost btn-sm';
            rm.title = 'Remove button';
            rm.innerText = '✕';
            rm.onclick = () => {
                row.buttons.splice(i, 1);
                renderButtons();
            };
            line.appendChild(rm);

            list.appendChild(line);
        });
    }
    renderButtons();

    const addBtn = document.createElement('button');
    addBtn.type = 'button';
    addBtn.className = 'btn btn-outline btn-sm';
    addBtn.innerText = '+ Add button';
    addBtn.onclick = () => {
        row.buttons.push({
            label: 'Button',
            action: 'submit'
        });
        renderButtons();
    };
    wrap.appendChild(addBtn);

    return wrap;
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

    layout.rows.forEach((row, ri) => canvas.appendChild(buildRowElement(row, ri, layout.rows, [ri])));
    document.getElementById('formLayout').value = JSON.stringify(layout);
    pushLayoutHistory();
}

// Builds one block. `path` locates the row for column drag/move: [ri] at top level, [ri, nestedRi] for a row
// nested inside a container - renderColumn appends its own column index to get a full column path.
// `ownerArray` is the array `row` actually lives in (layout.rows, or a container's own row.rows), so move/
// remove act on the right list regardless of nesting.
function buildRowElement(row, index, ownerArray, path) {
    const el = document.createElement('div');
    el.className = 'brow' + (row.t === 'group' ? ' brow-group' : '');
    el.addEventListener('dragover', (ev) => ev.preventDefault());
    el.addEventListener('drop', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        // Only the top-level canvas accepts a new block dropped from the palette; a nested row is already inside one.
        if (path.length > 1) return;
        const block = ev.dataTransfer.getData('text/block');
        if (block) addRow(block, index);
    });

    const head = document.createElement('div');
    head.className = 'brow-head';
    const type = document.createElement('span');
    type.className = 'brow-type';
    type.innerText = row.t;
    const actions = document.createElement('div');
    actions.className = 'brow-actions';

    const mkBtn = (label, fn, title) => {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'btn btn-outline btn-sm';
        b.innerText = label;
        if (title) b.title = title;
        b.onclick = fn;
        actions.appendChild(b);
    };
    if (row.t === 'row' || row.t === 'group') {
        const presetSel = document.createElement('select');
        presetSel.className = 'input input-sm';
        presetSel.title = 'Apply a column layout preset';
        presetSel.innerHTML = [
                ['', 'Preset…'],
                ['12', '1 column'],
                ['6-6', '2 columns (half)'],
                ['4-4-4', '3 columns (thirds)'],
                ['3-3-3-3', '4 columns (quarters)'],
                ['8-4', '2 columns (main/side)'],
            ]
            .map(([v, l]) => `<option value="${v}">${l}</option>`)
            .join('');
        presetSel.onchange = () => {
            applyRowPreset(row, presetSel.value);
            presetSel.value = '';
        };
        actions.appendChild(presetSel);
        mkBtn('+ col', () => {
            row.cols.push(emptyCol());
            renderCanvas();
        });
    }
    if (row.t === 'container')
        mkBtn('+ row', () => {
            row.rows.push({
                t: 'row',
                cols: [emptyCol()]
            });
            renderCanvas();
        });
    mkBtn('↑', () => moveRowIn(ownerArray, index, -1), 'Move up');
    mkBtn('↓', () => moveRowIn(ownerArray, index, 1), 'Move down');
    mkBtn('✕', () => {
        ownerArray.splice(index, 1);
        renderCanvas();
    }, 'Remove');

    head.appendChild(type);
    head.appendChild(actions);
    el.appendChild(head);

    if (row.t === 'container') {
        if (!Array.isArray(row.rows)) row.rows = [];
        el.appendChild(labeledInput('Section title', row, 'title', 'Section'));
        const body = document.createElement('div');
        body.className = 'brow-container-rows';
        row.rows.forEach((nrow, nri) => body.appendChild(buildRowElement(nrow, nri, row.rows, [...path, nri])));
        if (!row.rows.length) {
            const empty = document.createElement('div');
            empty.className = 'canvas-empty';
            empty.innerText = 'Use "+ row" to add this section\'s first row.';
            body.appendChild(empty);
        }
        el.appendChild(body);
    } else if (row.t === 'subtotal' || row.t === 'button' || row.t === 'line_items' || row.t === 'button_bar') {
        el.appendChild(rowFields(row));
    } else {
        const body = document.createElement('div');
        body.className = 'brow-cols';
        row.cols.forEach((col, ci) => body.appendChild(renderColumn(row, [...path, ci], col, ci)));
        el.appendChild(body);
    }
    return el;
}

// Applied to a whole row at once: replaces its columns with one per span, keeping only the first column's
// fields (the rest start empty) - the same trade-off preview.html's setRowPreset makes.
function applyRowPreset(row, preset) {
    if (!preset) return;
    const allFields = row.cols.flatMap((c) => c.items);
    const spans = preset.split('-').map(Number);
    row.cols = spans.map((w, idx) => ({
        t: 'col',
        w,
        items: idx === 0 ? allFields : []
    }));
    renderCanvas();
}

/* viewport preview: cosmetic only, toggles the canvas's own max-width */

function setBuilderViewport(size, btn) {
    document.querySelectorAll('#builderViewport .seg-btn').forEach((b) => b.classList.remove('active'));
    if (btn) btn.classList.add('active');
    const canvas = document.getElementById('layoutCanvas');
    canvas.classList.remove('tablet', 'mobile');
    if (size !== 'desktop') canvas.classList.add(size);
}

/* undo/redo: every layout mutation already ends by calling renderCanvas(), so that single choke point is
   where a snapshot is taken - nothing else has to call into history bookkeeping directly. */

let layoutHistory = [];
let layoutHistoryIndex = -1;
let suppressLayoutHistory = false;

function resetLayoutHistory() {
    layoutHistory = [];
    layoutHistoryIndex = -1;
    syncHistoryButtons();
}

// A button that silently no-ops on click (nothing left to undo/redo) is worse than no button: it looks live
// but gives no feedback. Disabled state is the only signal the bound is real.
function syncHistoryButtons() {
    const undoBtn = document.getElementById('builderUndo');
    const redoBtn = document.getElementById('builderRedo');
    if (undoBtn) undoBtn.disabled = layoutHistoryIndex <= 0;
    if (redoBtn) redoBtn.disabled = layoutHistoryIndex >= layoutHistory.length - 1;
}

function pushLayoutHistory() {
    if (suppressLayoutHistory) return;
    const snap = JSON.stringify(layout);
    if (layoutHistory[layoutHistoryIndex] === snap) {
        syncHistoryButtons();
        return;
    }
    layoutHistory = layoutHistory.slice(0, layoutHistoryIndex + 1);
    layoutHistory.push(snap);
    layoutHistoryIndex++;
    syncHistoryButtons();
}

function restoreLayoutSnapshot(index) {
    if (index < 0 || index >= layoutHistory.length) return;
    layoutHistoryIndex = index;
    suppressLayoutHistory = true;
    layout = JSON.parse(layoutHistory[index]);
    renderCanvas();
    suppressLayoutHistory = false;
    syncHistoryButtons();
}

function undoLayout() {
    restoreLayoutSnapshot(layoutHistoryIndex - 1);
}

function redoLayout() {
    restoreLayoutSnapshot(layoutHistoryIndex + 1);
}

const COL_WIDTHS = [
    [12, 'Full'],
    [9, 'Three quarters'],
    [8, 'Two thirds'],
    [6, 'Half'],
    [4, 'One third'],
    [3, 'One quarter'],
];

// Resolves a column from a path built by buildRowElement/renderColumn: [ri, ci] at top level, [ri, nestedRi, ci] nested.
function colAtPath(path) {
    const ci = path[path.length - 1];
    const row = path.length === 2 ? layout.rows[path[0]] : (layout.rows[path[0]] || {}).rows && layout.rows[path[0]].rows[path[1]];
    return row && row.cols && row.cols[ci];
}

function renderColumn(row, path, col, ci) {
    const colEl = document.createElement('div');
    colEl.className = 'bcol';
    colEl.style.flexGrow = col.w || 12;

    // Shared by every drop target in this column (each chip, and the column's own empty background): a chip
    // drop inserts before that chip, so a same-column drop actually reorders instead of the no-op it used to
    // be; the column background stays a plain append, for dropping past the last chip or into an empty column.
    function dropFieldAt(ev, targetIndex) {
        const field = ev.dataTransfer.getData('text/field');
        if (!field) return;

        // A chip dragged from another column (or the same one) carries its origin path.
        let moved = null;
        try {
            moved = JSON.parse(ev.dataTransfer.getData('text/movefield') || 'null');
        } catch (e) {}

        if (moved && JSON.stringify(moved.path) === JSON.stringify(path)) {
            const fromIdx = col.items.indexOf(field);
            if (fromIdx === -1) { renderCanvas(); return; }
            col.items.splice(fromIdx, 1);
            let insertAt = targetIndex === undefined ? col.items.length : targetIndex;
            if (fromIdx < insertAt) insertAt--; // removing the source first shifts every later index down by one
            col.items.splice(insertAt, 0, field);
            renderCanvas();
            return;
        }

        if (moved) {
            const src = colAtPath(moved.path);
            if (src) {
                const idx = src.items.indexOf(field);
                if (idx >= 0) src.items.splice(idx, 1);
            }
        }
        col.items.splice(targetIndex === undefined ? col.items.length : targetIndex, 0, field);
        renderCanvas();
    }

    colEl.addEventListener('dragover', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        colEl.classList.add('drop-hover');
    });
    colEl.addEventListener('dragleave', (ev) => {
        // dragleave fires the instant the pointer crosses onto a child element (a chip, even the chip's own
        // × button) - relatedTarget is where the pointer actually went, so a still-inside move is not a real leave.
        if (ev.relatedTarget && colEl.contains(ev.relatedTarget)) return;
        colEl.classList.remove('drop-hover');
    });
    colEl.addEventListener('drop', (ev) => {
        ev.preventDefault();
        ev.stopPropagation();
        colEl.classList.remove('drop-hover');
        dropFieldAt(ev, undefined);
    });

    col.items.forEach((item, itemIdx) => {
        const chip = document.createElement('span');
        chip.className = 'chip';
        chip.innerText = item;
        chip.draggable = true;
        chip.addEventListener('dragstart', (ev) => {
            ev.stopPropagation();
            ev.dataTransfer.setData('text/field', item);
            ev.dataTransfer.setData('text/movefield', JSON.stringify({
                path
            }));
        });
        // Safety net: a drag that ends outside any valid target (cancelled, dropped off-canvas) still fires
        // dragend on the chip being dragged, so this is the one place guaranteed to run and clear every marker.
        chip.addEventListener('dragend', () => {
            document.querySelectorAll('.chip.drop-before').forEach((c) => c.classList.remove('drop-before'));
            document.querySelectorAll('.bcol.drop-hover').forEach((c) => c.classList.remove('drop-hover'));
        });
        chip.addEventListener('dragover', (ev) => {
            ev.preventDefault();
            ev.stopPropagation();
            chip.classList.add('drop-before');
        });
        chip.addEventListener('dragleave', (ev) => {
            ev.stopPropagation();
            // Same relatedTarget check as the column: crossing onto the chip's own × button is not a real leave.
            if (ev.relatedTarget && chip.contains(ev.relatedTarget)) return;
            chip.classList.remove('drop-before');
        });
        chip.addEventListener('drop', (ev) => {
            ev.preventDefault();
            ev.stopPropagation();
            chip.classList.remove('drop-before');
            colEl.classList.remove('drop-hover');
            dropFieldAt(ev, itemIdx);
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