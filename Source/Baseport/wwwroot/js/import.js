/* importing a table, or rows for one, from a file the author already has */

const IMPORT_ACCEPT = '.csv,.tsv,.txt,.json,.xml';

// The file never leaves the browser between the preview and the create: the same File is posted twice, the server stores no upload between two calls.
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

// The sheet's own elements. Walking up from an input by id does not work for all of them: ui.field wraps a checkbox in a switch, the input's parent is that switch and not the field.
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

    ui.sheet('Import from definition', body, actions);
}

async function previewImport() {
    const els = importEls;
    els.preview.textContent = 'Reading the file…';

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

    const actions = ui.el('div', 'form-actions');
    actions.appendChild(ui.button('Cancel', () => ui.closeSheet(), {
        variant: 'btn-outline'
    }));
    actions.appendChild(submit);
    ui.sheet('Import records', body, actions);
}

async function importRecords() {
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
