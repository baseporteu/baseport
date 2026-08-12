/* Table overview, settings, the field builder and its editor. */
function selectTable(table) {
    currentTablePublicId = table.id;
    currentTableProxyUrl = table.proxyUrl || '';
    document.getElementById('recordsTableName').innerText = table.name;
    document.getElementById('detailTableName').innerText = table.name;
    document.getElementById('page-sub').innerHTML = table.isProxy ?
        `Building <strong>${table.name}</strong>, a proxy to <code>${table.proxyMethod || 'POST'} ${table.proxyUrl}</code>.` :
        `Building <strong>${table.name}</strong>, configure fields, tune API exposure, and inspect submissions.`;
    editingFieldId = null;
    fieldDraft = (table.fields || []).map(cloneField);
    fieldOriginal = {};
    fieldDraft.forEach((f) => (fieldOriginal[fieldKey(f)] = cloneField(f)));
    fieldsDirty = false;
    tableDirty = false;
    document.getElementById('saveFieldsBtn').disabled = true;
    document.getElementById('settingsTableName').innerText = table.name;
    document.getElementById('tableDescription').value = table.description || '';
    document.getElementById('tableApiEnabled').checked = !!table.apiEnabled;
    applyProxySettings(table);
    document.getElementById('tableFormsHint').innerText =
        (table.formCount || 0) === 0 ?
        '' :
        `${table.formCount} form(s) associated with this table.`;
    document.getElementById('saveTableBtn').disabled = true;
    document.getElementById('fieldName').value = '';
    setTypeComboboxValue('fieldType', 'text');
    renderFields(fieldDraft);
}

// Proxy targets stay editable: a rotated token must not force deleting the table.
function applyProxySettings(table) {
    const panel = document.getElementById('proxySettings');
    panel.classList.toggle('hidden', !table.isProxy);
    if (!table.isProxy) return;

    document.getElementById('proxyMethod').value = table.proxyMethod || 'POST';
    document.getElementById('proxyUrl').value = table.proxyUrl || '';
    document.getElementById('proxyReadUrl').value = table.proxyReadUrl || '';
    const token = document.getElementById('proxyToken');
    token.value = '';
    delete token.dataset.clear;
    document.getElementById('proxyTokenState').innerText = table.hasProxyToken ?
        'A token is set. Type a new one to replace it; the current one is never shown.' :
        'No token set. The remote API will be called unauthenticated.';
}

function clearProxyToken() {
    const token = document.getElementById('proxyToken');
    token.value = '';
    token.dataset.clear = 'true';
    markTableDirty();
    ui.toast('Token will be cleared when you save.', 'info');
}

// Mirrors FieldValidation.ApiNamePattern; the server decides, this stops a doomed save.
const API_NAME_PATTERN = /^[a-z][a-z0-9-]{1,62}$/;

// A name is only required while the table's API is published; an unpublished table may have none at all.
function apiNameIsValid(name, required) {
    return !required || API_NAME_PATTERN.test(name);
}

// Typed input is shaped, not policed: an author need not know the rule.
function normalizeApiName(input) {
    const before = input.value;
    const caret = input.selectionStart;
    const after = before
        .toLowerCase()
        .replace(/[\s_]+/g, '-')
        .replace(/[^a-z0-9-]/g, '');
    if (after !== before) {
        input.value = after;
        const at = Math.max(0, caret - (before.length - after.length));
        input.setSelectionRange(at, at);
    }
    markTableDirty();
}

function tableSettingsPayload() {
    const body = {
        description: document.getElementById('tableDescription').value,
        apiEnabled: document.getElementById('tableApiEnabled').checked,
    };
    const table = currentTables.find((t) => t.id === currentTablePublicId);
    if (table && table.isProxy) {
        const token = document.getElementById('proxyToken');
        body.proxyMethod = document.getElementById('proxyMethod').value;
        body.proxyUrl = document.getElementById('proxyUrl').value;
        body.proxyReadUrl = document.getElementById('proxyReadUrl').value;
        // An empty box means "keep the current token"; clearing is explicit.
        if (token.value.trim()) body.proxyToken = token.value.trim();
        if (token.dataset.clear === 'true') body.clearProxyToken = true;
    }
    return body;
}

/* the published endpoint: a sheet, opened from the table's own settings, not a separate list row */

function switchField(label, help, checked) {
    const wrap = ui.el('label', 'field');
    wrap.append(ui.el('span', 'field-label-text', {
        textContent: label
    }));
    const sw = ui.el('label', 'switch');
    const box = ui.el('input', null, {
        type: 'checkbox',
        checked
    });
    sw.append(box, ui.el('span', 'track'), ui.el('span', 'thumb'));
    wrap.append(sw);
    if (help) wrap.append(ui.el('span', 'field-help', {
        textContent: help
    }));
    wrap.ctrl = box;
    return wrap;
}

function openEndpointSheet(id) {
    const table = currentTables.find((t) => t.id === (id || currentTablePublicId));
    if (!table) return;

    const body = ui.el('div', 'sheet-form');

    const apiName = ui.field('Endpoint name', {
        id: 'sheetApiName',
        value: table.apiName || '',
        placeholder: 'e.g. sales-orders',
        help: 'The route this table answers at: /api/v1/{name}.',
    });
    const apiNameHelp = apiName.querySelector('.field-help');
    const apiNameHelpDefault = apiNameHelp.textContent;
    // A published table doing without a name would 400 on save; an unpublished one needs no name at all.
    function refreshEndpointSaveState() {
        const name = apiName.ctrl.value.trim();
        const valid = apiNameIsValid(name, table.apiEnabled);
        saveBtn.disabled = !valid;
        apiNameHelp.textContent = valid ?
            apiNameHelpDefault :
            name ?
            'Lowercase letters, digits and hyphens only, starting with a letter.' :
            "Required while this table's API is enabled.";
        apiNameHelp.style.color = valid ? '' : '#d63d3d';
    }
    apiName.ctrl.addEventListener('input', () => {
        normalizeApiName(apiName.ctrl);
        refreshEndpointSaveState();
    });

    const docsEnabled = switchField('Show in API docs', "Off keeps the endpoint live but out of the OpenAPI document -- for an integration you don't want advertised.", table.apiDocsEnabled !== false);

    const displayName = ui.field('Name', {
        value: table.apiDisplayName || '',
        placeholder: table.apiName || 'Sales orders',
        help: 'Shown in the reference instead of the route name.',
    });
    const namespace = ui.field('Namespace', {
        value: table.apiNamespace || '',
        placeholder: 'Sales',
        help: 'Groups this endpoint with others under one heading.',
    });
    const documentation = ui.field('Documentation', {
        type: 'textarea',
        rows: 10,
        value: table.apiDocumentation || '',
        placeholder: 'What this endpoint is for, and how to use it.',
    });

    const methods = ui.el('div', 'field');
    methods.append(ui.el('span', 'field-label-text', {
        textContent: 'Methods'
    }));
    const list = ui.el('ul', 'api-table-list');
    const boxes = {};
    ['GET', 'POST', 'PATCH', 'PUT', 'DELETE'].forEach((method) => {
        const row = ui.el('li', 'api-table-row');
        row.append(ui.el('span', 'api-table-name mono', {
            textContent: method
        }));
        const label = ui.el('label', 'switch');
        const box = ui.el('input', null, {
            type: 'checkbox',
            checked: (table.apiMethods || []).includes(method)
        });
        label.append(box, ui.el('span', 'track'), ui.el('span', 'thumb'));
        row.append(label);
        list.append(row);
        boxes[method] = box;
    });
    methods.append(list);
    methods.append(ui.el('span', 'field-help', {
        textContent: 'A method that is off is absent from the reference and refused by the API.'
    }));

    body.append(apiName, docsEnabled, displayName, namespace, documentation, methods);

    const actions = ui.el('div', 'form-actions');
    const saveBtn = ui.button('Save', () =>
        ui.busy(saveBtn, async () => {
            const saved = await ui.send(`/api/_admin/tables/${table.id}`, {
                method: 'PATCH',
                body: {
                    apiName: apiName.ctrl.value.trim().toLowerCase(),
                    apiDocsEnabled: docsEnabled.ctrl.checked,
                    apiDisplayName: displayName.ctrl.value,
                    apiNamespace: namespace.ctrl.value,
                    apiDocumentation: documentation.ctrl.value,
                    apiMethods: Object.keys(boxes).filter((m) => boxes[m].checked),
                },
                success: 'Endpoint updated.',
            });
            if (!saved) return;
            ui.closeSheet();
            await loadTables();
        }),
    );
    actions.append(ui.button('Cancel', ui.closeSheet, {
        variant: 'btn-outline'
    }), saveBtn);
    refreshEndpointSaveState();

    ui.sheet(`${table.name} endpoint`, body, actions);
}

const OPTIONS_SHOWN = 3;

function fieldConfig(f) {
    if (f.expression) return `<code class="field-expr">${escapeHtml(f.expression)}</code>`;
    if (f.optionsJson) {
        try {
            const o = JSON.parse(f.optionsJson);
            if (Array.isArray(o) && o.length) {
                const rest = o.length - OPTIONS_SHOWN;
                const shown = o.slice(0, OPTIONS_SHOWN).map(escapeHtml).join(', ');
                const more = rest > 0 ? ` <span class="muted">+${rest} more</span>` : '';
                return `<span class="option-list" title="${escapeHtml(o.join(', '))}">${shown}${more}</span>`;
            }
            if (o && o.tableId) {
                const tb = currentTables.find((x) => x.id === o.tableId);
                return `<span class="field-ref">${escapeHtml(tb ? tb.name : o.tableId)}</span>`;
            }
            if (o && o.sourceField) return `<span class="field-ref">from ${escapeHtml(o.sourceField)}</span>`;
        } catch (e) {}
    }
    if (f.pattern) return `<code class="field-expr">${escapeHtml(f.pattern)}</code>`;
    return '';
}

async function renderTablesOverview() {
    const sort = sortState('tables', 'name');
    initSortableHeaders('tablesHead', 'tables', 'name', () => renderTablesOverview());
    await ui.fragment('tablesRows', `/api/_admin/fragments/tables?sort=${sort.key}&order=${sort.dir}`);
    document.getElementById('tablesEmpty').classList.toggle('hidden', currentTables.length > 0);
}

const TYPE_LABELS = new Map([
    ['text', 'Short Text'],
    ['longtext', 'Long Text / Markdown'],
    ['number', 'Number'],
    ['currency', 'Currency / Price'],
    ['boolean', 'Boolean'],
    ['date', 'Date'],
    ['datetime', 'Date / Timestamp'],
    ['time', 'Time'],
    ['select', 'Select (single)'],
    ['multiselect', 'Multi-select'],
    ['file', 'Media / File URL'],
    ['reference', 'Reference / Relation'],
    ['calculated', 'Calculated / Formula'],
    ['derived', 'Derived (hidden, computed at submit)'],
    ['systemid', 'System ID'],
    ['email', 'Email'],
    ['phone', 'Phone'],
    ['url', 'URL'],
    ['color', 'Color'],
    ['rating', 'Rating'],
    ['slug', 'Slug'],
    ['richtext', 'Rich Text / HTML'],
    ['json', 'JSON / Object'],
    ['array', 'Array / List'],
    ['password', 'Password / Encrypted'],
]);

// local search, nothing to ask the server for
function fieldTypeOptions(query) {
    const q = (query || '').trim().toLowerCase();
    const rows = [...TYPE_LABELS].map(([v, l]) => ({
        id: v,
        label: l
    }));
    if (!q) return rows;
    return rows.filter((r) => r.label.toLowerCase().includes(q) || r.id.includes(q));
}

// sets both the hidden value and the visible search text
function setTypeComboboxValue(id, value) {
    const hidden = document.getElementById(id);
    if (!hidden) return;
    hidden.value = value;
    const search = hidden.closest('.combobox-box')?.querySelector('input[type="text"]');
    if (search) search.value = TYPE_LABELS.get(value) || value;
}

function initFieldTypeCombobox() {
    const mount = document.getElementById('fieldTypeRow');
    if (!mount) return;
    const row = ui.combobox('', {
        id: 'fieldType',
        value: 'text',
        valueLabel: TYPE_LABELS.get('text'),
        placeholder: 'Data type',
        browseAll: true,
        fetchOptions: (q) => fieldTypeOptions(q),
    });
    mount.replaceWith(row);
    row.id = 'fieldTypeRow';
}

// one pictogram per type family, so 24 types stay scannable by shape instead of by reading each pill
const TYPE_ICON_FAMILY = {
    text: 'text', longtext: 'text', richtext: 'text', slug: 'text',
    number: 'hash', currency: 'hash', rating: 'hash',
    boolean: 'toggle',
    date: 'calendar', datetime: 'calendar',
    time: 'clock',
    select: 'list', multiselect: 'list',
    file: 'paperclip',
    reference: 'arrow',
    calculated: 'fx', derived: 'fx',
    systemid: 'key',
    email: 'at',
    phone: 'phone',
    url: 'link',
    color: 'swatch',
    json: 'braces',
    array: 'brackets',
    password: 'lock',
};

const TYPE_ICON_PATHS = {
    text: '<path d="M4 6h16M4 12h10M4 18h13"/>',
    hash: '<path d="M5 9h14M5 15h14M10 4 8 20M16 4l-2 16"/>',
    toggle: '<rect x="2" y="7" width="20" height="10" rx="5"/><circle cx="16" cy="12" r="3"/>',
    calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l4 2"/>',
    list: '<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>',
    paperclip: '<path d="M21 12.5 12.5 21a5 5 0 0 1-7-7L14 5.5a3.5 3.5 0 0 1 5 5L10.5 19a2 2 0 0 1-3-3L15 8.5"/>',
    arrow: '<path d="M5 12h13M13 6l6 6-6 6"/>',
    fx: '<circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="12" r="2.5"/><path d="M8.5 12h7"/>',
    key: '<path d="m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4"/><path d="m21 2-9.6 9.6"/><circle cx="7.5" cy="15.5" r="5.5"/>',
    at: '<circle cx="12" cy="12" r="4"/><path d="M16 12v1.5a2.5 2.5 0 0 0 5 0V12a9 9 0 1 0-4 7.5"/>',
    phone: '<path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2Z"/>',
    link: '<path d="M9 15 15 9M11 6l1-1a4 4 0 0 1 6 6l-1 1M13 18l-1 1a4 4 0 0 1-6-6l1-1"/>',
    swatch: '<path d="M12 3c3 4 6 7.5 6 11a6 6 0 0 1-12 0c0-3.5 3-7 6-11Z"/>',
    braces: '<path d="M8 4C6 4 5 5 5 7v3c0 1-.5 2-2 2 1.5 0 2 1 2 2v3c0 2 1 3 3 3M16 4c2 0 3 1 3 3v3c0 1 .5 2 2 2-1.5 0-2 1-2 2v3c0 2-1 3-3 3"/>',
    brackets: '<path d="M8 4H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h2M16 4h2a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-2"/>',
    lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
};

function typeIcon(dataType) {
    const family = TYPE_ICON_FAMILY[dataType] || 'text';
    return `<svg class="type-icon" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${TYPE_ICON_PATHS[family]}</svg>`;
}

const HIDDEN_ICON =
    '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/><path d="M4 4l16 16"/></svg>';

// type carries the icon+label; required/unique/hidden stay as small muted text instead of competing pills; identifier reuses the key glyph systemid already uses.
function fieldBadges(f) {
    const isFn = f.dataType === 'calculated' || f.dataType === 'derived';
    const label = escapeHtml(TYPE_LABELS.get(f.dataType) || f.dataType);
    let cell = `<span class="type-cell${isFn ? ' calc' : ''}">${typeIcon(f.dataType)}<span class="type-label">${label}${f.isRequired ? ' *' : ''}</span>`;
    if (f.isIdentifier) cell += `<span class="type-flag-icon" title="Identifier">${typeIcon('systemid')}</span>`;
    if (f.isHidden) cell += `<span class="type-flag-icon" title="Hidden">${HIDDEN_ICON}</span>`;
    cell += '</span>';
    const words = [f.isUnique ? 'unique' : ''].filter(Boolean);
    if (words.length) cell += `<div class="type-flags">${words.join(' · ')}</div>`;
    return cell;
}

const TEXT_LENGTH_TYPES = new Set(['text', 'longtext', 'richtext', 'slug', 'email', 'phone', 'url', 'color', 'password']);

// Bounds mean value for numbers and length for text; the summary picks the reading.
function fieldLimits(f) {
    const unit = TEXT_LENGTH_TYPES.has(f.dataType) ? ' chars' : '';
    if (f.min !== null && f.min !== undefined && f.max !== null && f.max !== undefined) return `${f.min}-${f.max}${unit}`;
    if (f.min !== null && f.min !== undefined) return `≥ ${f.min}${unit}`;
    if (f.max !== null && f.max !== undefined) return `≤ ${f.max}${unit}`;
    return '';
}

function renderFields(fields) {
    const tbody = document.getElementById('fieldsList');
    const empty = document.getElementById('fieldsEmpty');
    tbody.innerHTML = '';
    empty.classList.toggle('hidden', fields.length > 0);
    fields.forEach((f, i) => {
        const tr = document.createElement('tr');
        tr.className = 'field-row';
        tr.draggable = true;
        tr.dataset.index = i;
        tr.tabIndex = 0;
        const key = fieldKey(f);
        const extras = [fieldLimits(f), f.defaultValue ? `default: ${f.defaultValue}` : ''].filter(Boolean).join(' · ');
        tr.innerHTML = `<td class="drag-cell" title="Drag to reorder, or Alt+Arrow" aria-hidden="true">⋮⋮</td>
                    <td>
                        <strong>${escapeHtml(f.name)}</strong>
                        ${f.label ? `<div class="muted">${escapeHtml(f.label)}</div>` : `<div class="muted" style="opacity: .7;">${escapeHtml(f.name)}</div>`}
                    </td>
                    <td>${fieldBadges(f)}</td>
                    <td class="field-config">
                        ${fieldConfig(f)}
                        ${extras ? `<div class="muted">${escapeHtml(extras)}</div>` : ''}
                    </td>
                    <td><div class="field-row-actions">
                        <button class="icon-btn" title="Edit" onclick="openFieldEditor('${key}')">${pencilIcon}</button>
                        <button class="icon-btn danger" title="Delete" onclick="deleteField('${key}')">${trashIcon}</button>
                    </div></td>`;
        wireFieldDrag(tr);
        tbody.appendChild(tr);
    });
    if (fields.length > 0) {
        const sys = document.createElement('tr');
        sys.className = 'field-row system-field';
        sys.innerHTML = `<td class="drag-cell" aria-hidden="true"></td>
        <td><strong>Created</strong><div class="muted">System column, added to every record automatically.</div></td>
        <td><span class="type-cell">${typeIcon('datetime')}<span class="type-label">Timestamp</span></span></td>
        <td><code>Date.now()</code></td>
        <td></td>`;
        tbody.appendChild(sys);
    }
}

/* Field reordering: rows are dragged (Alt+Arrow for keyboard) */
let dragFrom = null;

function wireFieldDrag(tr) {
    tr.addEventListener('dragstart', (ev) => {
        dragFrom = Number(tr.dataset.index);
        tr.classList.add('dragging');
        ev.dataTransfer.effectAllowed = 'move';
        // Firefox 153.x in this case refuses to begin a drag with no payload set
        ev.dataTransfer.setData('text/plain', String(dragFrom));
    });
    tr.addEventListener('dragend', () => {
        dragFrom = null;
        document.querySelectorAll('.field-row').forEach((r) => r.classList.remove('dragging', 'drop-above', 'drop-below'));
    });
    tr.addEventListener('dragover', (ev) => {
        if (dragFrom === null) return;
        ev.preventDefault();
        const midpoint = tr.getBoundingClientRect().top + tr.offsetHeight / 2;
        tr.classList.toggle('drop-above', ev.clientY < midpoint);
        tr.classList.toggle('drop-below', ev.clientY >= midpoint);
    });
    tr.addEventListener('dragleave', () => tr.classList.remove('drop-above', 'drop-below'));
    tr.addEventListener('drop', (ev) => {
        ev.preventDefault();
        if (dragFrom === null) return;
        const over = Number(tr.dataset.index);
        const midpoint = tr.getBoundingClientRect().top + tr.offsetHeight / 2;
        let to = ev.clientY < midpoint ? over : over + 1;

        if (dragFrom < to) to -= 1;
        moveFieldTo(dragFrom, to);
    });
    tr.addEventListener('keydown', (ev) => {
        if (!ev.altKey || (ev.key !== 'ArrowUp' && ev.key !== 'ArrowDown')) return;
        ev.preventDefault();
        const from = Number(tr.dataset.index);
        const to = from + (ev.key === 'ArrowUp' ? -1 : 1);
        moveFieldTo(from, to).then(() => document.querySelectorAll('.field-row')[to]?.focus());
    });
}

// Order persists immediately: it is independent of the staged field edits.
async function moveFieldTo(from, to) {
    if (from === to || to < 0 || to > fieldDraft.length - 1) return;
    const moved = fieldDraft.splice(from, 1)[0];
    fieldDraft.splice(to, 0, moved);
    renderFields(fieldDraft);

    const saved = fieldDraft.filter((f) => f.id).map((f) => f.id);
    if (!saved.length) return;
    await fetch(`/api/_admin/tables/${currentTablePublicId}/fields/order`, {
        method: 'PUT',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(saved),
    });
}

// Name and type only; everything else needs server checks and belongs in the field editor instead.
async function addField() {
    const typeEl = document.getElementById('fieldType');
    if (!typeEl) { ui.toast('The page is out of date. Reload and try again.', 'error'); return; }
    const name = document.getElementById('fieldName').value.trim();
    const dataType = typeEl.value;
    if (!currentTablePublicId) return;
    if (!name) {
        ui.toast('Enter a field name first.', 'error');
        document.getElementById('fieldName').focus();
        return;
    }
    if (fieldDraft.some((x) => x.name === name)) {
        ui.toast(`Field name '${name}' already exists.`, 'error');
        document.getElementById('fieldName').focus();
        return;
    }

    // staged locally; committed via "Save field changes"
    fieldDraft.push(cloneField({
        id: null,
        key: 'tmp-' + ++fieldSeq,
        name,
        dataType,
        expression: '',
        optionsJson: '[]',
        isRequired: false,
        pattern: '',
        isHidden: false,
    }));
    document.getElementById('fieldName').value = '';
    renderFields(fieldDraft);
    markFieldsDirty();
}

/* Reusable side sheet */

// ui.js owns overlays; these remain as the names the feature code calls.
function openSheet(title, bodyEl, actionsEl) {
    return ui.sheet(title, bodyEl, actionsEl);
}

function closeSheet() {
    ui.closeSheet();
    editingFieldId = null;
}

// the browser's back/forward buttons change location.href before this fires, bypassing navigate()'s unsaved-changes guard entirely; restore the URL bar if the user backs out
window.addEventListener('popstate', async () => {
    if (hasUnsavedChanges()) {
        const leave = await ui.confirm({
            title: 'Discard changes?',
            message: 'You have unsaved changes here. Leave without saving?',
            confirmLabel: 'Discard',
            danger: true,
        });
        if (!leave) {
            history.pushState({}, '', lastRenderedUrl);
            return;
        }
    }
    render();
});

/* Reusable modal */

// Thin adapter so existing call sites read the same; the dialog is ui.confirm().
function openModal({
    title,
    message,
    confirmLabel,
    cancelLabel,
    danger,
    onConfirm
}) {
    ui.confirm({
        title,
        message,
        confirmLabel: confirmLabel || 'Confirm',
        cancelLabel,
        danger
    }).then((ok) => {
        if (ok && onConfirm) onConfirm();
    });
}

function closeModal() {
    ui.closeSheet();
}

// dataField, if given, is what a server's { invalid: [...] } names this field as -- see ui.markInvalid.
function fieldInputRow(label, id, value, placeholder, mono, dataField) {
    const lab = document.createElement('label');
    lab.className = 'field-label';
    lab.innerText = label;
    const inp = document.createElement('input');
    inp.className = 'input' + (mono ? ' embed-input' : '');
    inp.id = id;
    if (dataField) inp.dataset.field = dataField;
    inp.value = value || '';
    inp.placeholder = placeholder || '';
    lab.appendChild(inp);
    return lab;
}

function openFieldEditor(fieldId) {
    const f = fieldDraft.find((x) => String(fieldKey(x)) === String(fieldId));
    if (!f) return;
    editingFieldId = fieldKey(f);
    let exprRequestId = 0; // read by validateExprLive below, which is declared after this function's own early return

    const wrap = document.createElement('div');

    wrap.appendChild(fieldInputRow('Field name', 'feName', f.name, 'e.g. UnitPrice'));
    wrap.appendChild(fieldInputRow('Label (shown externally)', 'feLabel', f.label, 'Unit price'));
    wrap.appendChild(fieldInputRow('Help text', 'feHelp', f.helpText, 'Excluding VAT'));

    const typeRow = ui.combobox('Data type', {
        id: 'feType',
        value: f.dataType,
        valueLabel: TYPE_LABELS.get(f.dataType) || f.dataType,
        placeholder: 'Search types…',
        browseAll: true,
        fetchOptions: (q) => fieldTypeOptions(q),
    });
    const typeSel = typeRow.ctrl; // hidden input, .value works like the old <select>
    const owningTable = currentTables.find((t) => t.id === currentTablePublicId);
    const hasData = !!f.id && (owningTable?.recordCount || 0) > 0;
    if (f.dataType === 'systemid' || hasData) {
        typeRow.querySelector('.combobox-box input[type="text"]').disabled = true;
        typeRow.querySelector('.combobox-chip-remove').classList.add('hidden');
        if (hasData)
            typeRow.title = `"${f.name}" already has data in ${owningTable.recordCount} record(s); its type can't be changed.`;
    }
    typeSel.onchange = () => syncFeConfig();
    wrap.appendChild(typeRow);

    const cfgRow = document.createElement('div');
    cfgRow.id = 'feCfgRow';
    wrap.appendChild(cfgRow);

    // Stacked settings-style rows (label + one-line description + a slider), matching the table settings panel.
    const settingSwitch = (id, checked, label, desc) => {
        const row = document.createElement('div');
        row.className = 'setting-row';
        const info = document.createElement('div');
        info.className = 'setting-label';
        const lab = document.createElement('label');
        lab.setAttribute('for', id);
        lab.innerText = label;
        const p = document.createElement('p');
        p.className = 'muted';
        p.innerText = desc;
        info.append(lab, p);
        const sw = document.createElement('label');
        sw.className = 'switch';
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.id = id;
        cb.checked = !!checked;
        sw.append(cb, document.createElement('span'), document.createElement('span'));
        sw.children[1].className = 'track';
        sw.children[2].className = 'thumb';
        row.append(info, sw);
        return row;
    };
    wrap.appendChild(settingSwitch('feRequired', f.isRequired, 'Required', 'Submissions without a value are rejected.'));

    const defaultRow = document.createElement('div');
    defaultRow.id = 'feDefaultRow';
    wrap.appendChild(defaultRow);
    wrap.appendChild(fieldInputRow('Currency code', 'feCurrency', f.currency, 'EUR'));
    const currencyHint = document.createElement('p');
    currencyHint.className = 'sheet-note';
    currencyHint.id = 'feCurrencyHint';
    wrap.appendChild(currencyHint);

    // One pair of bounds: value range for numbers, length range for text.
    const boundsRow = document.createElement('div');
    boundsRow.className = 'grid-form two';
    boundsRow.appendChild(
        fieldInputRow('Minimum', 'feMin', f.min === null || f.min === undefined ? '' : String(f.min), ''),
    );
    boundsRow.appendChild(
        fieldInputRow('Maximum', 'feMax', f.max === null || f.max === undefined ? '' : String(f.max), ''),
    );
    wrap.appendChild(boundsRow);
    const boundsHint = document.createElement('p');
    boundsHint.className = 'sheet-note';
    boundsHint.id = 'feBoundsHint';
    wrap.appendChild(boundsHint);

    wrap.appendChild(
        fieldInputRow('Validation pattern (regex, optional)', 'fePattern', f.pattern, 'e.g. ^[A-Z]{2}[0-9]{9}$', true),
    );
    const patHint = document.createElement('p');
    patHint.className = 'sheet-note';
    patHint.id = 'fePatternHint';
    patHint.innerText = 'Leave blank for no format check. Validated on submit and against the example value.';
    wrap.appendChild(patHint);

    wrap.appendChild(settingSwitch('feUnique', f.isUnique, 'Unique', 'Reject a submission whose value already exists.'));
    wrap.appendChild(settingSwitch('feIdentifier', f.isIdentifier, 'Identifier', 'Offer this field as a match key in lookup forms.'));
    wrap.appendChild(settingSwitch('feHidden', f.isHidden, 'Hidden', 'Not rendered in forms; value set via API or server only.'));

    const valPanel = document.createElement('div');
    valPanel.id = 'feValidation';
    valPanel.className = 'fe-validation';
    wrap.appendChild(valPanel);

    const validateBtn = document.createElement('button');
    validateBtn.type = 'button';
    validateBtn.className = 'btn btn-outline';
    validateBtn.innerText = 'Validate';
    validateBtn.onclick = () => validateFieldEditor();

    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'btn btn-outline';
    cancelBtn.innerText = 'Cancel';
    cancelBtn.onclick = () => closeSheet();

    const saveBtn = document.createElement('button');
    saveBtn.className = 'btn';
    saveBtn.innerText = 'Save';
    saveBtn.title = 'Updates this field in the draft below. Nothing reaches the server until you click Save on the table page.';
    saveBtn.onclick = () => saveFieldChanges();
    const actions = document.createElement('div');
    actions.className = 'form-actions';
    actions.appendChild(validateBtn);
    actions.appendChild(cancelBtn);
    actions.appendChild(saveBtn);

    openSheet('Edit field · ' + f.name, wrap, actions);
    syncFeConfig();
    syncFeDefault();
    syncBoundsHint();
    typeSel.addEventListener('change', syncFeDefault);
    typeSel.addEventListener('change', syncBoundsHint);
    return;

    // Respects the type's actual allowed values instead of being a blank free-text box.
    function syncFeDefault() {
        const t = typeSel.value;
        const row = document.getElementById('feDefaultRow');
        const existing = document.getElementById('feDefault');
        const current = existing ? existing.value : f.defaultValue || '';
        row.innerHTML = '';
        if (t === 'boolean') {
            row.appendChild(defaultSelectRow(['true', 'false'], current));
            return;
        }
        if (t === 'select' || t === 'multiselect') {
            const opts = currentOptionDraft();
            if (!opts.length) {
                row.appendChild(fieldInputRow('Default value', 'feDefault', current, 'Define options above first'));
                return;
            }
            if (t === 'select') {
                row.appendChild(defaultSelectRow(opts, current));
                return;
            }
            row.appendChild(fieldInputRow('Default value', 'feDefault', current, opts.join(', ')));
            const hint = document.createElement('p');
            hint.className = 'sheet-note';
            hint.innerText = `Allowed: ${opts.join(', ')}. One value, or a JSON array for more than one.`;
            row.appendChild(hint);
            return;
        }
        row.appendChild(fieldInputRow('Default value', 'feDefault', current, 'Applied when the submission omits this field'));
    }

    // options as currently drafted in the config row when it's live text, else the field's saved options
    function currentOptionDraft() {
        const cfg = document.getElementById('feConfig');
        if (cfg && cfg.tagName === 'INPUT' && (typeSel.value === 'select' || typeSel.value === 'multiselect'))
            return splitOptions(cfg.value);
        try {
            const o = JSON.parse(f.optionsJson || '[]');
            return Array.isArray(o) ? o : [];
        } catch (e) {
            return [];
        }
    }

    function defaultSelectRow(options, current) {
        const lab = document.createElement('label');
        lab.className = 'field-label';
        lab.innerText = 'Default value';
        const sel = document.createElement('select');
        sel.className = 'input';
        sel.id = 'feDefault';
        const none = document.createElement('option');
        none.value = '';
        none.innerText = '- Unset -';
        sel.appendChild(none);
        options.forEach((o) => {
            const op = document.createElement('option');
            op.value = o;
            op.innerText = o;
            if (o === current) op.selected = true;
            sel.appendChild(op);
        });
        lab.appendChild(sel);
        return lab;
    }

    function syncBoundsHint() {
        const t = typeSel.value;
        const currencyRow =
            document.getElementById('feCurrency').closest('.field-label') ||
            document.getElementById('feCurrency').parentElement;
        const isCurrency = t === 'currency';
        currencyRow.classList.toggle('hidden', !isCurrency);
        document.getElementById('feCurrencyHint').classList.toggle('hidden', !isCurrency);
        document.getElementById('feCurrencyHint').innerText =
            `Three-letter ISO code. Leave empty to use the instance default (${settingsData && settingsData.currency ? settingsData.currency : 'EUR'}).`;
        const hint = document.getElementById('feBoundsHint');
        if (!hint) return;
        // matches the Min/Max cases FieldValidation.cs actually checks -- everything else silently ignores them
        hint.innerText =
            t === 'number' || t === 'currency' || t === 'rating' ?
            'Smallest and largest accepted value. Leave blank for no bound.' :
            t === 'text' || t === 'longtext' || t === 'richtext' ?
            'Shortest and longest accepted length in characters. Leave blank for no bound.' :
            t === 'slug' || t === 'password' || t === 'json' ?
            'Longest accepted length in characters (Minimum is ignored). Leave blank for no bound.' :
            t === 'array' ?
            'Most items accepted (Minimum is ignored). Leave blank for no bound.' :
            'Bounds do not apply to this type.';
    }

    function syncFeConfig() {
        const t = typeSel.value;
        const row = document.getElementById('feCfgRow');
        row.innerHTML = '';
        if (t === 'calculated' || t === 'derived') {
            row.appendChild(
                fieldInputRow(
                    'JS expression',
                    'feConfig',
                    f.expression,
                    t === 'derived' ? 'data.Name ? "complete" : "incomplete"' : 'data.Qty * 2',
                    true,
                ),
            );
            const hint = document.createElement('p');
            hint.className = 'expr-status';
            hint.id = 'feExprStatus';
            row.appendChild(hint);
            const inp = document.getElementById('feConfig');
            inp.addEventListener('input', debounceExprValidate);
            // leaving the field must judge what it holds now, not wait out a debounce that tabbing away skips
            inp.addEventListener('blur', () => {
                clearTimeout(debounceExprValidate._t);
                validateExprLive();
            });
            debounceExprValidate();
        } else if (t === 'select' || t === 'multiselect') {
            row.appendChild(
                fieldInputRow(
                    'Options (comma separated; use \\, for a literal comma)',
                    'feConfig',
                    (() => {
                        try {
                            const o = JSON.parse(f.optionsJson || '[]');
                            return Array.isArray(o) ? joinOptions(o) : '';
                        } catch (e) {
                            return '';
                        }
                    })(),
                    'red, blue, green',
                ),
            );
            document.getElementById('feConfig').addEventListener('input', syncFeDefault);
        } else if (t === 'reference') {
            const sel = document.createElement('select');
            sel.className = 'input';
            sel.id = 'feConfig';
            try {
                const o = JSON.parse(f.optionsJson || '{}');
                const cur = o.tableId;
                currentTables.forEach((tb) => {
                    const op = document.createElement('option');
                    op.value = tb.name;
                    op.innerText = tb.name;
                    if (tb.id === cur) op.selected = true;
                    sel.appendChild(op);
                });
            } catch (e) {}
            const lab = document.createElement('label');
            lab.className = 'field-label';
            lab.innerText = 'Reference target';
            lab.appendChild(sel);
            row.appendChild(lab);
        } else if (t === 'slug') {
            const sel = document.createElement('select');
            sel.className = 'input';
            sel.id = 'feConfig';
            const none = document.createElement('option');
            none.value = '';
            none.innerText = '- Manual entry only -';
            sel.appendChild(none);
            let cur = '';
            try {
                cur = JSON.parse(f.optionsJson || '{}').sourceField || '';
            } catch (e) {}
            fieldDraft
                .filter((x) => String(fieldKey(x)) !== String(editingFieldId))
                .forEach((x) => {
                    const op = document.createElement('option');
                    op.value = x.name;
                    op.innerText = x.name;
                    if (x.name === cur) op.selected = true;
                    sel.appendChild(op);
                });
            const lab = document.createElement('label');
            lab.className = 'field-label';
            lab.innerText = 'Auto-generate from field (optional)';
            lab.appendChild(sel);
            row.appendChild(lab);
        } else {
            const hint = document.createElement('p');
            hint.className = 'sheet-note';
            // array/json have no expression or options, but Min/Max below still caps them
            hint.innerText =
                t === 'array' ? 'No expression needed; use Maximum below to cap the number of items.' :
                t === 'json' ? 'No expression needed; use Maximum below to cap the serialized size.' :
                'No additional configuration needed for this type.';
            row.appendChild(hint);
        }
    }

    function debounceExprValidate() {
        clearTimeout(debounceExprValidate._t);
        debounceExprValidate._t = setTimeout(validateExprLive, 400);
    }

    async function validateExprLive() {
        const inp = document.getElementById('feConfig');
        if (!inp) return;
        const expr = inp.value.trim();
        const hint = document.getElementById('feExprStatus');
        if (!hint) return;
        const id = ++exprRequestId;
        if (!expr) {
            hint.className = 'expr-status';
            hint.innerText = '';
            return;
        }
        const fieldNames = fieldDraft.filter((x) => String(fieldKey(x)) !== String(editingFieldId)).map((x) => x.name);
        const r = await fetch('/api/_admin/validate-expression', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                expression: expr,
                fieldNames,
                tableId: currentTablePublicId
            }),
        }).then((res) => res.json());
        if (id !== exprRequestId) return; // a newer request already landed; this reply is stale
        if (r.valid) {
            hint.className = 'expr-status ok';
            hint.innerText =
                '✓ Valid, ' +
                (r.referencedFields.join(', ') || 'no field references') +
                (r.sampleOutput ? ` · example result: ${r.sampleOutput}` : '');
        } else {
            hint.className = 'expr-status bad';
            hint.innerText = '✕ ' + (r.errors || []).join('; ');
        }
    }

    async function validateFieldEditor() {
        const panel = document.getElementById('feValidation');
        if (!panel) return;
        panel.className = 'fe-validation checking';
        panel.innerText = 'Checking…';
        const type = document.getElementById('feType').value;
        const cfg = document.getElementById('feConfig');
        const val = cfg ? cfg.value.trim() : '';
        let optionsJson = '[]';
        if (type === 'select' || type === 'multiselect')
            optionsJson = JSON.stringify(splitOptions(val));
        else if (type === 'reference') {
            const target = currentTables.find((t) => t.name === val);
            optionsJson = target ? JSON.stringify({
                tableId: target.id
            }) : '{}';
        } else if (type === 'slug') {
            optionsJson = val ? JSON.stringify({
                sourceField: val
            }) : '{}';
        }
        const body = {
            name: document.getElementById('feName').value.trim(),
            dataType: type,
            expression: type === 'calculated' || type === 'derived' ? val : '',
            optionsJson,
            isRequired: document.getElementById('feRequired').checked,
            pattern: document.getElementById('fePattern').value.trim(),
            isHidden: document.getElementById('feHidden').checked,
            tableId: currentTablePublicId,
            fieldId: String(editingFieldId),
        };
        const r = await fetch('/api/_admin/validate-field', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(body),
            })
            .then((res) => res.json())
            .catch(() => null);
        if (!r) {
            panel.className = 'fe-validation bad';
            panel.innerText = '✕ Validation could not be run.';
            return;
        }
        if (r.valid) {
            const refs = (r.referencedFields || []).length ? ', references ' + r.referencedFields.join(', ') : '';
            panel.className = 'fe-validation ok';
            panel.innerText = '✓ Valid' + (r.dataType ? ' (' + r.dataType + ')' : '') + refs;
        } else {
            panel.className = 'fe-validation bad';
            panel.innerText = '✕ ' + (r.errors || []).join('; ');
        }
    }
}

async function saveFieldChanges() {
    const draft = fieldDraft.find((x) => String(fieldKey(x)) === String(editingFieldId));
    if (!draft) {
        closeSheet();
        return;
    }

    const newName = document.getElementById('feName').value.trim();
    const newType = document.getElementById('feType').value;
    const cfg = document.getElementById('feConfig');

    // Local name-uniqueness and required checks; authority is the server on commit.
    const dup = fieldDraft.find((x) => String(fieldKey(x)) !== String(editingFieldId) && x.name === newName);
    if (!newName) {
        ui.toast('Field name is required.', 'error');
        return;
    }
    if (dup) {
        ui.toast(`Field name '${newName}' already exists.`, 'error');
        return;
    }

    if (newType === 'calculated' || newType === 'derived') {
        const expr = cfg ? cfg.value.trim() : '';
        const fieldNames = fieldDraft.filter((x) => String(fieldKey(x)) !== String(editingFieldId)).map((x) => x.name);
        const r = await fetch('/api/_admin/validate-expression', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                expression: expr,
                fieldNames
            }),
        }).then((res) => res.json());
        if (!r.valid) {
            ui.toast(r.errors || ['The expression is not valid.'], 'error');
            return;
        }
    }

    if (newType === 'select' || newType === 'multiselect') {
        const opts = cfg ? splitOptions(cfg.value) : [];
        if (!opts.length) {
            ui.toast('At least one option is required for this field type.', 'error');
            return;
        }
    }

    const newPattern = document.getElementById('fePattern').value.trim();
    if (newPattern) {
        try {
            new RegExp(newPattern);
        } catch (e) {
            ui.toast('Pattern is not a valid regular expression.', 'error');
            return;
        }
    }

    const num = (id) => {
        const raw = document.getElementById(id).value.trim();
        if (!raw) return null;
        const n = Number(raw);
        return Number.isFinite(n) ? n : null;
    };
    const min = num('feMin');
    const max = num('feMax');
    if (min !== null && max !== null && min > max) {
        ui.toast('Minimum cannot be greater than maximum.', 'error');
        return;
    }

    // Captured now, before any await: ui.confirm() below opens through the same shared
    // sheet panel this form is rendered in, which removes this form's DOM as a side effect
    // of opening -- every element read has to happen before that, not after.
    const newLabel = document.getElementById('feLabel').value.trim();
    const newHelp = document.getElementById('feHelp').value.trim();
    const newDefault = document.getElementById('feDefault').value;
    const newCurrency = newType === 'currency' ? document.getElementById('feCurrency').value.trim().toUpperCase() : '';
    const newRequired = document.getElementById('feRequired').checked;
    const newUnique = document.getElementById('feUnique').checked;
    const newIdentifier = document.getElementById('feIdentifier').checked;
    const newHidden = document.getElementById('feHidden').checked;

    // Defensive: the picker is already locked in the sheet whenever the field has data.
    if (draft.id && draft.dataType !== newType) {
        const table = currentTables.find((t) => t.id === currentTablePublicId);
        if ((table?.recordCount || 0) > 0) {
            ui.toast(`"${draft.name}" already has data in ${table.recordCount} record(s); its type can't be changed.`, 'error');
            return;
        }
    }

    draft.name = newName;
    draft.dataType = newType;
    draft.label = newLabel;
    draft.helpText = newHelp;
    draft.defaultValue = newDefault;
    draft.currency = newCurrency;
    draft.min = min;
    draft.max = max;
    draft.isRequired = newRequired;
    draft.isUnique = newUnique;
    draft.isIdentifier = newIdentifier;
    draft.isHidden = newHidden;
    draft.pattern = newPattern;
    if (cfg && (newType === 'calculated' || newType === 'derived')) draft.expression = cfg.value.trim();
    else if (cfg && (newType === 'select' || newType === 'multiselect'))
        draft.optionsJson = JSON.stringify(splitOptions(cfg.value));
    else if (cfg && newType === 'reference') {
        const target = currentTables.find((t) => t.name === cfg.value);
        draft.optionsJson = target ? JSON.stringify({
            tableId: target.id
        }) : '{}';
    } else if (cfg && newType === 'slug') {
        draft.optionsJson = cfg.value ? JSON.stringify({
            sourceField: cfg.value
        }) : '{}';
    }

    closeSheet();
    renderFields(fieldDraft);
    markFieldsDirty();
}

// One payload shape for add and update, so a new field option is never saved on one path and silently dropped on the other.
function fieldPayload(f) {
    return {
        name: f.name,
        label: f.label,
        helpText: f.helpText,
        dataType: f.dataType,
        expression: f.expression,
        optionsJson: f.optionsJson,
        defaultValue: f.defaultValue,
        currency: f.currency,
        min: f.min,
        max: f.max,
        isRequired: f.isRequired,
        pattern: f.pattern,
        isHidden: f.isHidden,
        isUnique: f.isUnique,
        isIdentifier: f.isIdentifier,
    };
}

async function commitFields() {
    const btn = document.getElementById('saveFieldsBtn');
    await ui.busy(btn, async () => {
        const add = [],
            patch = [],
            del = [];
        fieldDraft.forEach((f) => {
            if (!f.id) add.push(f);
            else {
                const orig = fieldOriginal[fieldKey(f)];
                const changed = !orig || JSON.stringify(orig) !== JSON.stringify(f);
                if (changed) patch.push(f);
            }
        });
        (fieldOriginal && Object.values(fieldOriginal)).forEach((o) => {
            if (!fieldDraft.some((f) => fieldKey(f) === fieldKey(o))) del.push(o);
        });

        // Mirrors FieldValidation.cs, named by field, instead of an unattributed 400 from the server.
        const nameCounts = new Map();
        fieldDraft.forEach((f) => nameCounts.set(f.name, (nameCounts.get(f.name) || 0) + 1));
        for (const [name, count] of nameCounts) {
            if (count > 1) {
                ui.toast(`Field name '${name}' is used by more than one field.`, 'error');
                return;
            }
        }
        for (const f of [...add, ...patch]) {
            if (f.dataType !== 'select' && f.dataType !== 'multiselect') continue;
            let opts = [];
            try {
                opts = JSON.parse(f.optionsJson || '[]');
            } catch (e) { /* falls through to the empty-options message below */ }
            if (!Array.isArray(opts) || !opts.length) {
                ui.toast(`"${f.name}": at least one option is required for this field type.`, 'error');
                return;
            }
        }

        for (const f of add) {
            const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(fieldPayload(f)),
            });
            if (!res.ok) {
                const data = await res.json().catch(() => ({}));
                openModal({
                    title: 'Could not save fields',
                    message: `"${f.name}": ` + (data.errors || ['Failed to add a field.']).join('\n'),
                    confirmLabel: 'OK',
                });
                return;
            }
        }
        for (const f of patch) {
            const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields/${f.id}`, {
                method: 'PATCH',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(fieldPayload(f)),
            });
            if (!res.ok) {
                const data = await res.json().catch(() => ({}));
                openModal({
                    title: 'Could not save fields',
                    message: `"${f.name}": ` + (data.errors || ['Failed to update a field.']).join('\n'),
                    confirmLabel: 'OK',
                });
                return;
            }
        }
        for (const o of del) {
            const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields/${o.id}`, {
                method: 'DELETE'
            });
            if (!res.ok) {
                const data = await res.json().catch(() => ({}));
                openModal({
                    title: 'Could not delete field',
                    message: `"${o.name}": ` + (data.errors || ['Field could not be deleted.']).join('\n'),
                    confirmLabel: 'OK',
                });
                return;
            }
        }

        const tables = await fetch('/api/_admin/tables').then((r) => r.json());
        currentTables = tables;
        const table = tables.find((x) => x.id === currentTablePublicId);
        fieldDraft = (table ? table.fields : []).map(cloneField);
        fieldOriginal = {};
        fieldDraft.forEach((f) => (fieldOriginal[fieldKey(f)] = cloneField(f)));
        fieldsDirty = false;
        renderFields(fieldDraft);
        await loadTables();
    });
    // outside ui.busy so this has the last word over its own disabled-state restore
    updateSaveButtons();
}

function deleteField(fieldId) {
    const f = fieldDraft.find((x) => String(fieldKey(x)) === String(fieldId));
    if (!f) return;
    openModal({
        title: 'Delete field',
        message: `Are you sure you want to delete the field "${f.name}"? This will irreversibly delete all the data contained in this field. There is no way back.`,
        confirmLabel: 'Delete',
        danger: true,
        onConfirm: () => {
            fieldDraft = fieldDraft.filter((x) => String(fieldKey(x)) !== String(fieldId));
            if (String(editingFieldId) === String(fieldId)) closeSheet();
            renderFields(fieldDraft);
            markFieldsDirty();
        },
    });
}