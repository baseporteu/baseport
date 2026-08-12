/* records grid: server-paged, server-searched */

function debouncedRecordSearch() {
  clearTimeout(recordSearchTimer);
  recordSearchTimer = setTimeout(() => {
    recordPage = 1;
    loadRecords();
  }, 250);
}

async function loadRecords(page) {
  if (!currentTablePublicId) return;
  const proxyNote = document.getElementById('recordsProxyNote');
  const wrap = document.querySelector('#recordsView .table-wrap');
  if (currentTableProxyUrl) {
    proxyNote.classList.remove('hidden');
    proxyNote.innerText = `Proxy table, submissions are forwarded to ${currentTableProxyUrl} and nothing is stored locally.`;
    wrap.classList.add('hidden');
    document.getElementById('recordsEmpty').classList.add('hidden');
    document.getElementById('recordsPager').innerHTML = '';
    return;
  }
  proxyNote.classList.add('hidden');
  wrap.classList.remove('hidden');

  recordPage = page || recordPage || 1;
  const q = (document.getElementById('recordSearch').value || '').trim();

  const table = currentTables.find((t) => t.id === currentTablePublicId);
  const columns = (table ? table.fields : []).filter((f) => !f.isHidden);
  document.getElementById('recordsHead').innerHTML =
    columns.map((f) => `<th>${escapeHtml(f.label || f.name)}</th>`).join('') + '<th>Created</th><th></th>';

  // Rows arrive rendered; the browser assigns one string.
  const meta = await ui.fragment(
    'recordsBody',
    `/api/_admin/fragments/records/${currentTablePublicId}?page=${recordPage}&pageSize=25` +
      (q ? `&q=${encodeURIComponent(q)}` : ''),
  );
  if (!meta) return;

  document.getElementById('recordsEmpty').classList.toggle('hidden', meta.total > 0);
  document.getElementById('recordsEmpty').innerText = q ? 'No records match that search.' : 'No records yet.';
  renderRecordPager(meta);
}

function renderRecordPager(data) {
  const el = document.getElementById('recordsPager');
  const pages = data.totalPages || 1;
  if (!data.total) {
    el.innerHTML = '';
    return;
  }
  // The count is capped, so past the ceiling the total is a floor and the page count with it.
  const from = (data.page - 1) * data.pageSize + 1;
  const to = data.countExact ? Math.min(data.page * data.pageSize, data.total) : data.page * data.pageSize;
  const label = data.countExact ? `${data.total}` : `${data.total}+`;
  const last = data.countExact ? data.page >= pages : !data.hasMore;
  el.innerHTML = `<span class="muted">${from}-${Math.max(from, to)} of ${label}</span>
                <button class="btn btn-outline btn-sm" ${data.page <= 1 ? 'disabled' : ''} onclick="loadRecords(${data.page - 1})">Previous</button>
                <button class="btn btn-outline btn-sm" ${last ? 'disabled' : ''} onclick="loadRecords(${data.page + 1})">Next</button>`;
}

function deleteRecord(rid) {
  openModal({
    title: 'Delete record',
    // To-do: if we have a PK column defined, we should print this here "to delete record #123" or similar.
    message: 'Are you sure you want to delete this record? This cannot be undone.',
    confirmLabel: 'Delete',
    danger: true,
    onConfirm: async () => {
      await fetch(`/api/_admin/tables/${currentTablePublicId}/records/${rid}`, { method: 'DELETE' });
      await loadRecords();
      await loadTables();
    },
  });
}

/* New record: one input per writable field, same write path as the REST API and embedded forms. */

const NON_WRITABLE_TYPES = new Set(['calculated', 'formula', 'derived', 'internal', 'systemid', 'system_id']);

function normalizeFieldType(t) {
  return (
    { markdown: 'longtext', price: 'currency', checkbox: 'boolean', timestamp: 'datetime', tags: 'multiselect', media: 'file', relation: 'reference' }[t] || t
  );
}

function parseFieldOptions(json) {
  try {
    const o = JSON.parse(json || '[]');
    return Array.isArray(o) ? o.filter((x) => typeof x === 'string') : [];
  } catch (e) {
    return [];
  }
}

function refTableId(json) {
  try {
    const o = JSON.parse(json || '{}');
    return o && typeof o === 'object' && !Array.isArray(o) ? o.tableId || '' : '';
  } catch (e) {
    return '';
  }
}

// Searches the target table as the visitor types, instead of loading every row up front.
function fetchReferenceOptions(targetId, query, signal) {
  return fetch(`/api/_admin/tables/${targetId}/records?q=${encodeURIComponent(query)}&pageSize=20`, { signal })
    .then((r) => r.json())
    .then((data) => (data.rows || []).map((rec) => ({ id: rec.id, label: recordLabel(rec) })));
}

function recordLabel(rec) {
  const vals = Object.values(rec.data || {}).map((v) => String(v)).filter(Boolean);
  return (vals[0] || 'Record').slice(0, 40);
}

// Matches the toast icon style (stroke-based, 2.5 weight); a Lucide-shaped key, small enough to sit inline with a label.
const IDENTIFIER_ICON =
  '<svg class="field-id-icon" viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><title>Identifier field</title><path d="m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4"/><path d="m21 2-9.6 9.6"/><circle cx="7.5" cy="15.5" r="5.5"/></svg>';

function openNewRecordModal() {
  const table = currentTables.find((t) => t.id === currentTablePublicId);
  if (!table) return;
  const fields = (table.fields || [])
    .filter((f) => !f.isHidden && !NON_WRITABLE_TYPES.has(f.dataType))
    .sort((a, b) => a.position - b.position);

  const body = document.createElement('div');
  const inputs = {};

  fields.forEach((f) => {
    const type = normalizeFieldType(f.dataType);
    const label = (f.label || f.name) + (f.isRequired ? ' *' : '');
    const id = 'rf_' + f.name;
    let row;
    if (type === 'longtext') row = ui.field(label, { id, type: 'textarea', help: f.helpText });
    else if (type === 'boolean') row = ui.field(label, { id, type: 'checkbox', help: f.helpText });
    else if (type === 'number') row = ui.field(label, { id, type: 'number', help: f.helpText });
    else if (type === 'currency') row = ui.field(label, { id, type: 'number', placeholder: '0.00', help: f.helpText || f.currency || '' });
    else if (type === 'date') row = ui.field(label, { id, type: 'date', help: f.helpText });
    else if (type === 'datetime') row = ui.field(label, { id, type: 'datetime-local', help: f.helpText });
    else if (type === 'select') {
      const opts = parseFieldOptions(f.optionsJson);
      row = ui.field(label, { id, type: 'select', options: [['', '- Select -']].concat(opts.map((o) => [o, o])), help: f.helpText });
    } else if (type === 'multiselect') {
      const opts = parseFieldOptions(f.optionsJson);
      row = ui.field(label, { id, type: 'text', placeholder: opts.length ? opts.join(', ') : '', help: ((f.helpText ? f.helpText + ' ' : '') + 'Comma-separated.').trim() });
    } else if (type === 'file') {
      row = ui.field(label, { id, type: 'file', help: f.helpText });
    } else if (type === 'reference') {
      const targetId = refTableId(f.optionsJson);
      row = ui.combobox(label, {
        id,
        placeholder: 'Search…',
        help: f.helpText,
        fetchOptions: (query, signal) => fetchReferenceOptions(targetId, query, signal),
      });
    } else if (type === 'email') row = ui.field(label, { id, type: 'email', help: f.helpText });
    else if (type === 'phone') row = ui.field(label, { id, type: 'tel', help: f.helpText });
    else if (type === 'url') row = ui.field(label, { id, type: 'url', help: f.helpText });
    else if (type === 'color') row = ui.field(label, { id, type: 'color', help: f.helpText });
    else if (type === 'time') row = ui.field(label, { id, type: 'time', help: f.helpText });
    else if (type === 'password') row = ui.field(label, { id, type: 'password', help: f.helpText });
    else if (type === 'rating') {
      row = ui.field(label, { id, type: 'number', placeholder: `${f.min ?? 1}-${f.max ?? 5}`, help: f.helpText });
      row.control.min = f.min ?? 1;
      row.control.max = f.max ?? 5;
      row.control.step = 1;
    } else if (type === 'slug') {
      row = ui.field(label, { id, type: 'text', placeholder: 'auto-generated if left blank', help: f.helpText });
    } else if (type === 'richtext') {
      row = ui.field(label, { id, type: 'textarea', help: ((f.helpText ? f.helpText + ' ' : '') + 'HTML is sanitized on save.').trim() });
    } else if (type === 'json') {
      row = ui.field(label, { id, type: 'textarea', placeholder: '{ }', help: ((f.helpText ? f.helpText + ' ' : '') + 'Raw JSON object.').trim() });
    } else if (type === 'array') {
      row = ui.field(label, { id, type: 'textarea', placeholder: '["a", "b"]', help: ((f.helpText ? f.helpText + ' ' : '') + 'JSON array of text/number/boolean values.').trim() });
    } else {
      row = ui.field(label, { id, type: 'text', help: f.helpText });
    }
    row.control.dataset.field = f.name.toLowerCase();
    if (f.isIdentifier) {
      const labelText = row.querySelector('.field-label-text');
      if (labelText) labelText.insertAdjacentHTML('beforeend', ' ' + IDENTIFIER_ICON);
    }
    inputs[f.name] = { type, control: row.control };
    body.appendChild(row);
  });

  if (!fields.length) body.appendChild(Object.assign(document.createElement('p'), { className: 'muted', innerText: 'This table has no editable fields.' }));

  const actions = document.createElement('div');
  actions.className = 'form-actions';
  const createBtn = ui.button('Create', () => ui.busy(createBtn, () => submitNewRecord(inputs)));
  actions.appendChild(ui.button('Cancel', closeSheet, { variant: 'btn-outline' }));
  actions.appendChild(createBtn);

  openSheet('New record', body, actions);
}

async function submitNewRecord(inputs) {
  // Any file field switches the whole submission to multipart, same as a curl -F upload against the REST API.
  const hasFile = Object.values(inputs).some((i) => i.type === 'file');
  const url = `/api/_admin/tables/${currentTablePublicId}/records`;
  let res;

  if (hasFile) {
    const fd = new FormData();
    for (const name in inputs) {
      const { type, control } = inputs[name];
      if (type === 'file') {
        if (control.files[0]) fd.append(name, control.files[0]);
      } else if (type === 'boolean') {
        if (control.checked) fd.append(name, 'true');
      } else if (type === 'multiselect') {
        control.value.split(',').map((s) => s.trim()).filter(Boolean).forEach((v) => fd.append(name, v));
      } else if (control.value !== '') {
        fd.append(name, control.value);
      }
    }
    res = await fetch(url, { method: 'POST', body: fd });
  } else {
    const payload = {};
    for (const name in inputs) {
      const { type, control } = inputs[name];
      if (type === 'boolean') {
        payload[name] = control.checked;
        continue;
      }
      const raw = control.value;
      if (raw === '' || raw === null || raw === undefined) continue;
      if (type === 'number' || type === 'currency' || type === 'rating') payload[name] = Number(raw);
      else if (type === 'multiselect') payload[name] = raw.split(',').map((s) => s.trim()).filter(Boolean);
      else if (type === 'json' || type === 'array') {
        try {
          payload[name] = JSON.parse(raw);
        } catch (e) {
          payload[name] = raw; // not valid json, let the server reject it
        }
      } else payload[name] = raw;
    }
    res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
  }

  const created = await ui.handle(res, { success: 'Record created.', failure: 'Could not create the record.' });
  if (!created) return;
  closeSheet();
  await loadRecords();
  await loadTables();
}

/* Table settings: description + REST API exposure */

async function saveTableSettings(btn) {
  if (!currentTablePublicId) return;
  let saved = false;
  await ui.busy(btn, async () => {
    const res = await fetch(`/api/_admin/tables/${currentTablePublicId}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(tableSettingsPayload()),
    });
    saved = await ui.handle(res, { success: 'Table settings saved.', failure: 'Could not save the table settings.' });
    if (!saved) return;
    tableDirty = false;
    await loadTables();
    applyProxySettings(currentTables.find((t) => t.id === currentTablePublicId) || {});
  });
  // Outside the busy wrapper so it has the last word over ui.busy's own disabled-state restore.
  if (saved) updateSaveButtons();
}

async function refreshAll() {
  await loadTables();
  if (!currentTablePublicId) return;
  const table = currentTables.find((x) => x.id === currentTablePublicId);
  if (table) renderFields(table.fields);
  loadRecords();
}

/* Icon rail sections (sql / schema / auth / logs / settings) */

function escapeHtml(s) {
  return ui.escape(s);
}
