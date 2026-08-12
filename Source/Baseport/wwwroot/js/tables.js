/* Table overview, settings, the field builder and its editor. */
function selectTable(table) {
  currentTablePublicId = table.id;
  currentTableProxyUrl = table.proxyUrl || '';
  document.getElementById('recordsTableName').innerText = table.name;
  document.getElementById('detailTableName').innerText = table.name;
  document.getElementById('page-sub').innerHTML = table.isProxy
    ? `Building <strong>${table.name}</strong>, a proxy to <code>${table.proxyMethod || 'POST'} ${table.proxyUrl}</code>.`
    : `Building <strong>${table.name}</strong>, configure fields, tune API exposure, and inspect submissions.`;
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
    (table.formCount || 0) === 0
      ? ''
      : `${table.formCount} form(s) associated with this table.`;
  document.getElementById('saveTableBtn').disabled = true;
  document.getElementById('fieldName').value = '';
  document.getElementById('fieldExpr').value = '';
  setTypeComboboxValue('fieldType', 'text');
  updateFieldTypeHint();
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
  document.getElementById('proxyTokenState').innerText = table.hasProxyToken
    ? 'A token is set. Type a new one to replace it; the current one is never shown.'
    : 'No token set. The remote API will be called unauthenticated.';
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
  wrap.append(ui.el('span', 'field-label-text', { textContent: label }));
  const sw = ui.el('label', 'switch');
  const box = ui.el('input', null, { type: 'checkbox', checked });
  sw.append(box, ui.el('span', 'track'), ui.el('span', 'thumb'));
  wrap.append(sw);
  if (help) wrap.append(ui.el('span', 'field-help', { textContent: help }));
  wrap.control = box;
  return wrap;
}

function openEndpointSheet() {
  const table = currentTables.find((t) => t.id === currentTablePublicId);
  if (!table) return;

  const body = ui.el('div', 'sheet-form');

  const apiName = ui.field('Endpoint name', {
    id: 'sheetApiName',
    value: table.apiName || '',
    placeholder: 'e.g. sales-orders',
    help: 'The route this table answers at: /api/v1/{name}.',
  });
  apiName.control.addEventListener('input', () => normalizeApiName(apiName.control));

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
  methods.append(ui.el('span', 'field-label-text', { textContent: 'Methods' }));
  const list = ui.el('ul', 'api-table-list');
  const boxes = {};
  ['GET', 'POST', 'PATCH', 'PUT', 'DELETE'].forEach((method) => {
    const row = ui.el('li', 'api-table-row');
    row.append(ui.el('span', 'api-table-name mono', { textContent: method }));
    const label = ui.el('label', 'switch');
    const box = ui.el('input', null, { type: 'checkbox', checked: (table.apiMethods || []).includes(method) });
    label.append(box, ui.el('span', 'track'), ui.el('span', 'thumb'));
    row.append(label);
    list.append(row);
    boxes[method] = box;
  });
  methods.append(list);
  methods.append(ui.el('span', 'field-help', { textContent: 'A method that is off is absent from the reference and refused by the API.' }));

  body.append(apiName, docsEnabled, displayName, namespace, documentation, methods);

  const actions = ui.el('div', 'form-actions');
  const saveBtn = ui.button('Save', () =>
    ui.busy(saveBtn, async () => {
      const saved = await ui.send(`/api/_admin/tables/${table.id}`, {
        method: 'PATCH',
        body: {
          apiName: apiName.control.value.trim().toLowerCase(),
          apiDocsEnabled: docsEnabled.control.checked,
          apiDisplayName: displayName.control.value,
          apiNamespace: namespace.control.value,
          apiDocumentation: documentation.control.value,
          apiMethods: Object.keys(boxes).filter((m) => boxes[m].checked),
        },
        success: 'Endpoint updated.',
      });
      if (!saved) return;
      ui.closeSheet();
      await loadTables();
    }),
  );
  actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }), saveBtn);

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
        const shown = o.slice(0, OPTIONS_SHOWN).map((v) => `<span class="badge">${escapeHtml(v)}</span>`);
        if (rest > 0) shown.push(`<span class="badge muted-badge">+${rest}</span>`);
        return `<div class="badge-group" title="${escapeHtml(o.join(', '))}">${shown.join('')}</div>`;
      }
      if (o && o.tableId) {
        const tb = currentTables.find((x) => x.id === o.tableId);
        return `<span class="field-ref">${escapeHtml(tb ? tb.name : o.tableId)}</span>`;
      }
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
  const rows = [...TYPE_LABELS].map(([v, l]) => ({ id: v, label: l }));
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
  row.control.addEventListener('change', updateFieldTypeHint);
  mount.replaceWith(row);
  row.id = 'fieldTypeRow';
}

function fieldBadges(f) {
  const badges = [
    `<span class="badge ${f.dataType === 'calculated' || f.dataType === 'derived' ? 'calc' : ''}">${escapeHtml(TYPE_LABELS.get(f.dataType) || f.dataType)}</span>`,
  ];
  if (f.isRequired) badges.push('<span class="badge badge-required">required</span>');
  if (f.isUnique) badges.push('<span class="badge">unique</span>');
  if (f.isIdentifier) badges.push('<span class="badge">identifier</span>');
  if (f.isHidden) badges.push('<span class="badge">hidden</span>');
  return badges.join('');
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
    const key = f.id;
    const extras = [fieldLimits(f), f.defaultValue ? `default: ${f.defaultValue}` : ''].filter(Boolean).join(' · ');
    tr.innerHTML = `<td class="drag-cell" title="Drag to reorder, or Alt+Arrow" aria-hidden="true">⋮⋮</td>
                    <td>
                        <strong>${escapeHtml(f.name)}</strong>
                        ${f.label ? `<div class="muted">${escapeHtml(f.label)}</div>` : `<div class="muted" style="opacity: .7;">${escapeHtml(f.name)}</div>`}
                    </td>
                    <td><div class="badge-group">${fieldBadges(f)}</div></td>
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
        <td><span class="badge">timestamp</span></td>
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
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(saved),
  });
}

function updateFieldTypeHint() {
  const typeEl = document.getElementById('fieldType');
  const input = document.getElementById('fieldExpr');
  const hint = document.getElementById('fieldTypeHint');
  if (!typeEl || !input || !hint) return;
  const t = typeEl.value;
  const map = {
    calculated: 'JS expression e.g. data.Price * 1.2',
    derived: 'JS expression e.g. data.Name ? "complete" : "incomplete"',
    select: 'Options, comma separated e.g. red, blue, green',
    multiselect: 'Options, comma separated e.g. red, blue, green',
    reference: 'Target table name',
    systemid: 'Auto-generated short ID',
    slug: 'Source field name to auto-generate from (optional)',
  };
  input.placeholder = map[t] || 'Optional';
  input.disabled = t === 'systemid';
  hint.innerText = '';
  hint.style.color = '';
}

/* Live server-side validation of the expression/options input in the add-field row */
function wireFieldExprValidation() {
  const input = document.getElementById('fieldExpr');
  const hint = document.getElementById('fieldTypeHint');
  let timer = null;
  input.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(validateFieldExpr, 400);
  });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      testFieldExpr();
    }
  });
  async function validateFieldExpr() {
    const type = document.getElementById('fieldType').value;
    const expr = input.value.trim();
    if (!currentTablePublicId) {
      hint.innerText = '';
      hint.style.color = '';
      return;
    }
    if (!expr) {
      hint.innerText = '';
      hint.style.color = '';
      return;
    }
    if (type !== 'calculated' && type !== 'derived') {
      hint.innerText = '';
      hint.style.color = '';
      return;
    }
    const table = currentTables.find((t) => t.id === currentTablePublicId);
    const fieldNames = (table ? table.fields : []).map((f) => f.name);
    const r = await fetch('/api/_admin/validate-expression', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expression: expr, fieldNames, tableId: currentTablePublicId }),
    }).then((res) => res.json());
    if (r.valid) {
      const refs = (r.referencedFields || []).join(', ') || 'no field references';
      hint.innerText = '✓ Valid, ' + refs + (r.sampleOutput ? ` · example result: ${r.sampleOutput}` : '');
      hint.style.color = '#2d9d5f';
    } else {
      hint.innerText = '✕ ' + (r.errors || []).join('; ');
      hint.style.color = '#d63d3d';
    }
  }
}

async function testFieldExpr() {
  const type = document.getElementById('fieldType').value;
  const val = document.getElementById('fieldExpr').value.trim();
  const hint = document.getElementById('fieldTypeHint');
  const btn = document.getElementById('fieldExprTestBtn');
  if (!currentTablePublicId) {
    hint.innerText = 'Select a table first.';
    hint.style.color = '#d63d3d';
    return;
  }
  if (!val) {
    hint.innerText = 'Enter a value to test.';
    hint.style.color = '#d63d3d';
    return;
  }
  const original = btn.innerText;
  btn.disabled = true;
  btn.innerText = '…';
  try {
    if (type === 'calculated' || type === 'derived') {
      const table = currentTables.find((t) => t.id === currentTablePublicId);
      const fieldNames = (table ? table.fields : []).map((f) => f.name);
      const r = await fetch('/api/_admin/validate-expression', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expression: val, fieldNames, tableId: currentTablePublicId }),
      }).then((res) => res.json());
      if (r.valid) {
        const refs = (r.referencedFields || []).join(', ') || 'no field references';
        hint.innerText = '✓ Valid, ' + refs + (r.sampleOutput ? ` · example result: ${r.sampleOutput}` : '');
        hint.style.color = '#2d9d5f';
      } else {
        hint.innerText = '✕ ' + (r.errors || []).join('; ');
        hint.style.color = '#d63d3d';
      }
    } else if (type === 'select' || type === 'multiselect') {
      const opts = val
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      if (opts.length) {
        hint.innerText = `✓ ${opts.length} option(s) will be saved.`;
        hint.style.color = '#2d9d5f';
      } else {
        hint.innerText = '✕ No options provided.';
        hint.style.color = '#d63d3d';
      }
    } else if (type === 'reference') {
      const target = currentTables.find((t) => t.name === val.trim());
      if (target) {
        hint.innerText = `✓ References table "${target.name}".`;
        hint.style.color = '#2d9d5f';
      } else {
        hint.innerText = '✕ No table with that name.';
        hint.style.color = '#d63d3d';
      }
    } else if (type === 'systemid') {
      hint.innerText = 'Auto-generated on submit.';
      hint.style.color = '';
    } else if (type === 'slug') {
      hint.innerText = val ? `✓ Will auto-generate from field "${val}" when left blank.` : 'No source field set, slug must be entered manually.';
      hint.style.color = val ? '#2d9d5f' : '';
    } else {
      hint.innerText = 'No configuration needed for this type.';
      hint.style.color = '';
    }
  } finally {
    btn.disabled = false;
    btn.innerText = original;
  }
}

async function addField() {
  const name = document.getElementById('fieldName').value.trim();
  const dataType = document.getElementById('fieldType').value;
  const opt = document.getElementById('fieldExpr').value.trim();
  const hint = document.getElementById('fieldTypeHint');
  hint.innerText = '';
  hint.style.color = '';
  if (!currentTablePublicId) return;
  if (!name) {
    hint.innerText = 'Enter a field name first.';
    hint.style.color = '#d63d3d';
    document.getElementById('fieldName').focus();
    return;
  }

  // Rejected here, before staging: without this, a bad expression was accepted silently and only
  // surfaced much later at commit time, by which point the row had already cleared itself, looking
  // like the add had succeeded.
  if (dataType === 'calculated' || dataType === 'derived') {
    const table = currentTables.find((t) => t.id === currentTablePublicId);
    const fieldNames = (table ? table.fields : []).map((f) => f.name);
    const r = await fetch('/api/_admin/validate-expression', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expression: opt, fieldNames, tableId: currentTablePublicId }),
    }).then((res) => res.json());
    if (!r.valid) {
      hint.innerText = '✕ ' + (r.errors || ['The expression is not valid.']).join('; ');
      hint.style.color = '#d63d3d';
      return;
    }
  }

  const body = {
    id: null,
    key: 'tmp-' + ++fieldSeq,
    name,
    dataType,
    expression: '',
    optionsJson: '[]',
    isRequired: false,
    pattern: '',
    isHidden: false,
  };
  if (dataType === 'calculated' || dataType === 'derived') body.expression = opt;
  else if (dataType === 'select' || dataType === 'multiselect')
    body.optionsJson = JSON.stringify(
      opt
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
    );
  else if (dataType === 'reference') {
    const target = currentTables.find((t) => t.name === opt.trim());
    if (target) body.optionsJson = JSON.stringify({ tableId: target.id });
  } else if (dataType === 'slug' && opt) {
    body.optionsJson = JSON.stringify({ sourceField: opt });
  }

  // staged locally; committed via "Save field changes"
  fieldDraft.push(cloneField(body));
  document.getElementById('fieldName').value = '';
  document.getElementById('fieldExpr').value = '';
  updateFieldTypeHint();
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

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && document.getElementById('sheet').classList.contains('open')) closeSheet();
  if (e.key === 'Escape' && document.getElementById('modalOverlay').classList.contains('open')) closeModal();
});

window.addEventListener('popstate', () => render());

/* Reusable modal */

// Thin adapter so existing call sites read the same; the dialog is ui.confirm().
function openModal({ title, message, confirmLabel, cancelLabel, danger, onConfirm }) {
  ui.confirm({ title, message, confirmLabel: confirmLabel || 'Confirm', cancelLabel, danger }).then((ok) => {
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

  const wrap = document.createElement('div');

  wrap.appendChild(fieldInputRow('Field name', 'feName', f.name, 'e.g. UnitPrice'));
  wrap.appendChild(fieldInputRow('Label (shown to visitors)', 'feLabel', f.label, 'Unit price'));
  wrap.appendChild(fieldInputRow('Help text', 'feHelp', f.helpText, 'Excluding VAT'));
  wrap.appendChild(fieldInputRow('Expression / config', 'feConfig', '', ''));

  const typeRow = ui.combobox('Data type', {
    id: 'feType',
    value: f.dataType,
    valueLabel: TYPE_LABELS.get(f.dataType) || f.dataType,
    placeholder: 'Search types…',
    browseAll: true,
    fetchOptions: (q) => fieldTypeOptions(q),
  });
  const typeSel = typeRow.control; // hidden input, .value works like the old <select>
  if (f.dataType === 'systemid') typeRow.querySelector('.combobox-box input[type="text"]').disabled = true;
  typeSel.onchange = () => syncFeConfig();
  wrap.appendChild(typeRow);

  const cfgRow = document.createElement('div');
  cfgRow.id = 'feCfgRow';
  wrap.appendChild(cfgRow);

  const reqRow = document.createElement('label');
  reqRow.className = 'check-row';
  const reqCb = document.createElement('input');
  reqCb.type = 'checkbox';
  reqCb.id = 'feRequired';
  reqCb.checked = !!f.isRequired;
  reqRow.appendChild(reqCb);
  reqRow.appendChild(document.createTextNode(' Required, submissions without a value are rejected'));
  wrap.appendChild(reqRow);

  wrap.appendChild(
    fieldInputRow('Default value', 'feDefault', f.defaultValue, 'Applied when the submission omits this field'),
  );
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

  const checkbox = (id, checked, text) => {
    const row = document.createElement('label');
    row.className = 'check-row';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.id = id;
    cb.checked = !!checked;
    row.appendChild(cb);
    row.appendChild(document.createTextNode(' ' + text));
    return row;
  };
  wrap.appendChild(checkbox('feUnique', f.isUnique, 'Unique, reject a submission whose value already exists'));
  wrap.appendChild(
    checkbox('feIdentifier', f.isIdentifier, 'Identifier, offer this field as a match key in lookup forms'),
  );
  wrap.appendChild(checkbox('feHidden', f.isHidden, 'Hidden, not rendered in forms, value set via API / server only'));

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
  syncBoundsHint();
  typeSel.addEventListener('change', syncBoundsHint);
  return;

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
    hint.innerText =
      t === 'number' || t === 'currency'
        ? 'Smallest and largest accepted value. Leave blank for no bound.'
        : t === 'text' || t === 'longtext'
          ? 'Shortest and longest accepted length in characters. Leave blank for no bound.'
          : 'Bounds only apply to number, currency and text fields.';
  }

  function syncFeConfig() {
    const t = typeSel.value;
    const cfg = document.getElementById('feConfig');
    const row = document.getElementById('feCfgRow');
    row.innerHTML = '';
    cfg.value = '';
    if (t === 'calculated' || t === 'derived') {
      row.appendChild(
        fieldInputRow(
          'JS expression',
          'feConfig',
          f.expression,
          t === 'derived' ? 'data.Name ? "complete" : "incomplete"' : 'data.Price * 1.2',
          true,
        ),
      );
      const hint = document.createElement('p');
      hint.className = 'expr-status';
      hint.id = 'feExprStatus';
      row.appendChild(hint);
      const inp = document.getElementById('feConfig');
      inp.addEventListener('input', debounceExprValidate);
      debounceExprValidate();
    } else if (t === 'select' || t === 'multiselect') {
      row.appendChild(
        fieldInputRow(
          'Options (comma separated)',
          'feConfig',
          (() => {
            try {
              const o = JSON.parse(f.optionsJson || '[]');
              return Array.isArray(o) ? o.join(', ') : '';
            } catch (e) {
              return '';
            }
          })(),
          'red, blue, green',
        ),
      );
    } else if (t === 'reference') {
      row.appendChild(fieldInputRow('Target table name', 'feConfig', '', 'Customers'));
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
      hint.innerText = 'No additional configuration needed for this type.';
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
    if (!expr) {
      hint.className = 'expr-status';
      hint.innerText = '';
      return;
    }
    const fieldNames = fieldDraft.filter((x) => String(fieldKey(x)) !== String(editingFieldId)).map((x) => x.name);
    const r = await fetch('/api/_admin/validate-expression', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expression: expr, fieldNames, tableId: currentTablePublicId }),
    }).then((res) => res.json());
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
      optionsJson = JSON.stringify(
        val
          .split(',')
          .map((s) => s.trim())
          .filter(Boolean),
      );
    else if (type === 'reference') {
      const target = currentTables.find((t) => t.name === val);
      optionsJson = target ? JSON.stringify({ tableId: target.id }) : '{}';
    } else if (type === 'slug') {
      optionsJson = val ? JSON.stringify({ sourceField: val }) : '{}';
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
      headers: { 'Content-Type': 'application/json' },
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
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expression: expr, fieldNames }),
    }).then((res) => res.json());
    if (!r.valid) {
      ui.toast(r.errors || ['The expression is not valid.'], 'error');
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

  draft.name = newName;
  draft.dataType = newType;
  draft.label = document.getElementById('feLabel').value.trim();
  draft.helpText = document.getElementById('feHelp').value.trim();
  draft.defaultValue = document.getElementById('feDefault').value;
  draft.currency = newType === 'currency' ? document.getElementById('feCurrency').value.trim().toUpperCase() : '';
  draft.min = min;
  draft.max = max;
  draft.isRequired = document.getElementById('feRequired').checked;
  draft.isUnique = document.getElementById('feUnique').checked;
  draft.isIdentifier = document.getElementById('feIdentifier').checked;
  draft.isHidden = document.getElementById('feHidden').checked;
  draft.pattern = document.getElementById('fePattern').value.trim();
  if (cfg && (newType === 'calculated' || newType === 'derived')) draft.expression = cfg.value.trim();
  else if (cfg && (newType === 'select' || newType === 'multiselect'))
    draft.optionsJson = JSON.stringify(
      cfg.value
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
    );
  else if (cfg && newType === 'reference') {
    const target = currentTables.find((t) => t.name === cfg.value);
    draft.optionsJson = target ? JSON.stringify({ tableId: target.id }) : '{}';
  } else if (cfg && newType === 'slug') {
    draft.optionsJson = cfg.value ? JSON.stringify({ sourceField: cfg.value }) : '{}';
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
      if (!fieldDraft.some((f) => fieldKey(f) === fieldKey(o))) del.push(o.id);
    });

    for (const f of add) {
      const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(fieldPayload(f)),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        openModal({
          title: 'Could not save fields',
          message: (data.errors || ['Failed to add a field.']).join('\n'),
          confirmLabel: 'OK',
        });
        return;
      }
    }
    for (const f of patch) {
      const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields/${f.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(fieldPayload(f)),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        openModal({
          title: 'Could not save fields',
          message: (data.errors || ['Failed to update a field.']).join('\n'),
          confirmLabel: 'OK',
        });
        return;
      }
    }
    for (const id of del) {
      const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/fields/${id}`, { method: 'DELETE' });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        openModal({
          title: 'Could not delete field',
          message: (data.errors || ['Field could not be deleted.']).join('\n'),
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

