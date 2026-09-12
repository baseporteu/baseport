// Table and record import utility functions

const IMPORT_ACCEPT = '.csv,.tsv,.txt,.json,.xml';

// The file stays in the browser between preview and creation; the same File object is posted twice.
let importFile = null;

function importForm(extra) {
    const data = new FormData();
    data.append('file', importFile);
    Object.entries(extra || {}).forEach(([k, v]) => data.append(k, v));
    return data;
}

function importFileRow(onPick) {
    const row = ui.field('File', {
        id: 'impFile',
        type: 'file',
        help: 'CSV, tab or semicolon separated, JSON or XML. Column types are read from the file.',
    });
    row.ctrl.accept = IMPORT_ACCEPT;
    row.ctrl.onchange = () => {
        importFile = row.ctrl.files && row.ctrl.files[0];
        if (importFile) onPick();
    };
    return row;
}

// Reference to sheet elements. Note: ui.field wraps checkboxes in a switch element.
let importEls = null;

function openImportDefinition() {
    importFile = null;
    const body = ui.el('div');
    body.appendChild(importFileRow(() => previewImport()));

    const nameRow = ui.field('Table name', {
        id: 'impName',
        placeholder: 'Taken from the file name'
    });
    nameRow.hidden = true;
    body.appendChild(nameRow);

    const preview = ui.el('div', null, {
        id: 'impPreview'
    });
    body.appendChild(preview);

    const withRowsRow = ui.field('Also import the rows', {
        id: 'impWithRows',
        type: 'checkbox',
        value: true
    });
    withRowsRow.hidden = true;
    body.appendChild(withRowsRow);

    const create = ui.button('Create table', () => ui.busy(create, () => createFromImport()));
    create.hidden = true;
    create.id = 'impCreate';

    importEls = {
        nameRow,
        name: nameRow.ctrl,
        preview,
        withRowsRow,
        withRows: withRowsRow.ctrl,
        create
    };

    const actions = ui.el('div', 'form-actions');
    actions.appendChild(ui.button('Cancel', () => ui.closeSheet(), {
        variant: 'btn-outline'
    }));
    actions.appendChild(create);

    ui.sheet('Import', body, actions);
}

async function previewImport() {
    const els = importEls;
    els.preview.textContent = 'Reading the file...';

    const res = await fetch('/api/_admin/tables/import', {
        method: 'POST',
        body: importForm({
            preview: 'true'
        })
    });
    const data = await ui.handle(res, {
        failure: 'The file could not be read.'
    });
    if (!data) {
        els.preview.textContent = '';
        return;
    }

    renderImportPreview(els.preview, data);
    els.nameRow.hidden = false;
    if (!els.name.value.trim()) els.name.value = data.name || '';
    els.withRowsRow.hidden = false;
    els.withRowsRow.querySelector('.field-label-text').textContent = `Also import the ${data.rowCount} rows`;
    els.create.hidden = false;
}

function renderImportPreview(target, data) {
    target.textContent = '';
    const note = ui.el('p', 'sheet-note', {
        textContent: `${data.fields.length} columns, ${data.rowCount} rows.`
    });
    target.appendChild(note);

    const wrap = ui.el('div', 'table-wrap');
    const table = ui.el('table', 'table');
    const head = ui.el('tr');
    ['Column', 'Field', 'Type', 'Required'].forEach((h) => head.appendChild(ui.el('th', null, {
        textContent: h
    })));
    table.appendChild(ui.el('thead')).appendChild(head);

    const tbody = ui.el('tbody');
    (data.fields || []).forEach((f) => {
        const tr = ui.el('tr');
        [f.label || f.name, f.name, f.dataType, f.isRequired ? 'Yes' : ''].forEach((v) => tr.appendChild(ui.el('td', null, {
            textContent: v
        })));
        tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    target.appendChild(wrap);

    (data.errors || []).forEach((e) => ui.toast(e, 'error'));
}

async function createFromImport() {
    const name = importEls.name.value.trim();
    const withRecords = importEls.withRows.checked;

    const res = await fetch('/api/_admin/tables/import', {
        method: 'POST',
        body: importForm({
            name,
            withRecords: withRecords ? 'true' : 'false'
        })
    });
    const data = await ui.handle(res, {
        failure: 'The table could not be created.'
    });
    if (!data) return;

    ui.toast(`Imported ${data.fieldCount} fields and ${data.recordCount} rows.`, 'success');
    ui.closeSheet();
    await loadTables();
    selectTable(data.table);
}

function openImportRecords() {
    importFile = null;
    const body = ui.el('div');
    const submit = ui.button('Import', () => ui.busy(submit, () => importRecords()));
    submit.hidden = true;

    body.appendChild(importFileRow(() => {
        submit.hidden = false;
    }));
    body.appendChild(ui.el('p', 'sheet-note', {
        textContent: 'Columns are matched to this table’s fields by name. Every row is checked before any row is stored, a file with a bad row imports nothing.',
    }));

    const existing = currentTables.find((t) => t.id === currentTablePublicId)?.recordCount || 0;
    if (existing > 0) {
        body.appendChild(ui.el('p', 'sheet-note sheet-warning', {
            textContent: `This table already holds ${existing} record(s). Import adds rows on top of them; it does not update or remove existing ones.`,
        }));
    }

    const actions = ui.el('div', 'form-actions');
    actions.appendChild(ui.button('Cancel', () => ui.closeSheet(), {
        variant: 'btn-outline'
    }));
    actions.appendChild(submit);
    ui.sheet('Import records', body, actions);
}

async function importRecords() {
    const existing = currentTables.find((t) => t.id === currentTablePublicId)?.recordCount || 0;
    if (existing > 0) {
        const ok = await ui.confirm({
            title: 'Import into a table with data',
            message: `This table already holds ${existing} record(s). The imported rows will be added on top of them. Continue?`,
            confirmLabel: 'Import',
        });
        if (!ok) return;
    }

    const res = await fetch(`/api/_admin/tables/${currentTablePublicId}/records/import`, {
        method: 'POST',
        body: importForm()
    });
    const data = await ui.handle(res, {
        failure: 'The rows could not be imported.'
    });
    if (!data) return;

    ui.toast(`Imported ${data.imported} rows into ${(data.fields || []).join(', ')}.`, 'success');
    ui.closeSheet();
    await loadRecords();
}