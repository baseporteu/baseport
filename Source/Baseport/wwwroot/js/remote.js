const PROTOCOLS = [['rest', 'REST'], ['odata', 'OData'], ['baseport', 'Baseport'],
    ['sqlite', 'SQLite'], ['sqlserver', 'SQL Server'], ['postgres', 'PostgreSQL']];
const SQL_PROTOCOLS = ['sqlite', 'sqlserver', 'postgres'];
const COLUMN_CHOICES = [['', 'Choose'], ['text', 'Store as text'], ['skip', 'Skip']];

function isSql(protocol) {
    return SQL_PROTOCOLS.includes(protocol);
}

function connectionById(id) {
    return connectionData.find((x) => x.id === id);
}
const AUTH_KINDS = [['none', 'None'], ['bearer', 'Bearer token'], ['basic', 'Basic'], ['header', 'Header']];
const PAGING = [['auto', 'Detect'], ['odata', 'OData next link'], ['link', 'Link header'], ['next', 'Next URL in body'],
    ['cursor', 'Cursor'], ['page', 'Page number'], ['offset', 'Offset'], ['none', 'Single page']];
const CLONE_MODES = [['upsert', 'Upsert'], ['mirror', 'Mirror'], ['append', 'Append']];
const SECRET_REF = /^\{\{([a-z][a-z0-9-]*)\}\}$/;

let connectionData = [];
let cloneData = [];

async function loadConnections() {
    const body = document.getElementById('connectionBody');
    if (!body) return;
    const loaded = await ui.send('/api/_admin/connections', {
        method: 'GET',
        failure: 'Could not load connections.'
    });
    if (!loaded) return;
    connectionData = loaded;
    body.innerHTML = '';
    document.getElementById('connectionEmpty').classList.toggle('hidden', loaded.length > 0);

    loaded.forEach((c) => {
        const tr = ui.el('tr', 'row-link');
        tr.onclick = () => openConnectionSheet(c.id);
        tr.append(
            ui.el('td', 'mono', { textContent: c.name }),
            ui.el('td', 'muted mono', { textContent: isSql(c.protocol) ? 'Connection string in a secret' : c.baseUrl }),
            ui.el('td', null, { textContent: label(PROTOCOLS, c.protocol) }),
            ui.el('td', 'muted', { textContent: label(AUTH_KINDS, c.authKind) }),
            ui.el('td'));
        body.append(tr);
    });
}

function label(pairs, value) {
    return (pairs.find(([v]) => v === value) || [value, value])[1];
}

async function secretsForSelect() {
    const secrets = await ui.send('/api/_admin/secrets', {
        method: 'GET',
        failure: 'Could not load secrets.'
    });
    return secrets || [];
}

function headersToText(headers, secrets) {
    return (headers || []).map((h) => {
        if (!h.secretId) return `${h.name}: ${h.value || ''}`;
        const s = secrets.find((x) => x.id === h.secretId);
        return `${h.name}: {{${s ? s.name : h.secretId}}}`;
    }).join('\n');
}

function headersFromText(text, secrets) {
    const headers = [];
    for (const raw of text.split('\n')) {
        const line = raw.trim();
        if (!line) continue;
        const at = line.indexOf(':');
        if (at < 1) return { error: `"${line}" is not in the form Name: value.` };
        const name = line.slice(0, at).trim();
        const value = line.slice(at + 1).trim();
        const ref = SECRET_REF.exec(value);
        if (!ref) {
            headers.push({ name, value });
            continue;
        }
        const s = secrets.find((x) => x.name === ref[1]);
        if (!s) return { error: `No secret named ${ref[1]}.` };
        headers.push({ name, secretId: s.id });
    }
    return { headers };
}

async function openConnectionSheet(id) {
    const c = id ? connectionData.find((x) => x.id === id) : null;
    const secrets = await secretsForSelect();
    const body = ui.el('div');

    const name = ui.field('Name', { value: c ? c.name : '', placeholder: 'crm' });
    const baseUrl = ui.field('Base URL', { value: c ? c.baseUrl : '', placeholder: 'https://api.example.com/v1', mono: true });
    const protocol = ui.field('Protocol', { type: 'select', options: PROTOCOLS, value: c ? c.protocol : 'rest' });
    const authKind = ui.field('Authentication', { type: 'select', options: AUTH_KINDS, value: c ? c.authKind : 'none' });
    const headerName = ui.field('Header name', { value: c ? c.authHeaderName : '', placeholder: 'X-Api-Key', mono: true });
    const username = ui.field('Username', { value: c ? c.basicUsername : '' });
    const secret = ui.field('Secret', {
        type: 'select',
        options: [['', 'Choose a secret'], ...secrets.map((s) => [s.id, s.name])],
        value: c ? c.authSecretId : '',
        help: secrets.length ? '' : 'Add one under Settings › Secrets first.'
    });
    const headers = ui.field('Headers', {
        type: 'textarea',
        mono: true,
        rows: 3,
        value: c ? headersToText(c.headers, secrets) : '',
        placeholder: 'Accept-Language: nl\nX-Client: {{client-key}}',
        help: 'One per line. {{name}} sends a secret.'
    });

    const secretHelp = secret.querySelector('.field-help') || secret.appendChild(ui.el('span', 'field-help'));
    const sync = () => {
        const sql = isSql(protocol.ctrl.value);
        const kind = sql ? 'none' : authKind.ctrl.value;
        baseUrl.hidden = sql;
        authKind.hidden = sql;
        headers.hidden = sql;
        headerName.hidden = kind !== 'header';
        username.hidden = kind !== 'basic';
        secret.hidden = !sql && kind === 'none';
        secretHelp.textContent = sql
            ? 'Holds the connection string. SQLite: Data Source=/path/to/file.db.'
            : (secrets.length ? '' : 'Add one under Settings › Secrets first.');
    };
    authKind.ctrl.addEventListener('change', sync);
    protocol.ctrl.addEventListener('change', sync);
    sync();
    body.append(name, baseUrl, protocol, authKind, headerName, username, secret, headers);

    const read = () => {
        const parsed = headersFromText(headers.ctrl.value, secrets);
        if (parsed.error) {
            ui.toast(parsed.error, 'error');
            return null;
        }
        const sql = isSql(protocol.ctrl.value);
        const kind = sql ? 'none' : authKind.ctrl.value;
        return {
            name: name.ctrl.value.trim(),
            baseUrl: sql ? '' : baseUrl.ctrl.value.trim(),
            protocol: protocol.ctrl.value,
            authKind: kind,
            authHeaderName: kind === 'header' ? headerName.ctrl.value.trim() : '',
            basicUsername: kind === 'basic' ? username.ctrl.value.trim() : '',
            authSecretId: sql || kind !== 'none' ? secret.ctrl.value : '',
            headers: sql ? [] : parsed.headers
        };
    };

    const actions = ui.el('div', 'form-actions');
    if (c) {
        actions.append(ui.button('Delete', async () => {
            const ok = await ui.confirm({
                title: 'Delete connection',
                message: `Remove ${c.name}?`,
                confirmLabel: 'Delete',
                danger: true,
            });
            if (!ok) return;
            const done = await ui.send(`/api/_admin/connections/${c.id}`, {
                method: 'DELETE',
                success: 'Connection deleted.',
                failure: 'Could not delete the connection.',
            });
            if (!done) return;
            ui.closeSheet();
            await loadConnections();
        }, { variant: 'btn-danger' }), ui.el('div', 'form-actions-spacer'));
        const test = ui.button('Test', () => ui.busy(test, () => testConnection(c)), { variant: 'btn-outline' });
        actions.append(test);
    }
    actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }));
    const save = ui.button(c ? 'Save' : 'Add connection', () => ui.busy(save, async () => {
        const payload = read();
        if (!payload) return;
        const saved = await ui.send(c ? `/api/_admin/connections/${c.id}` : '/api/_admin/connections', {
            method: c ? 'PATCH' : 'POST',
            body: payload,
            success: c ? 'Connection saved.' : 'Connection added.',
            failure: 'Could not save the connection.',
        });
        if (!saved) return;
        ui.closeSheet();
        await loadConnections();
    }));
    actions.append(save);

    ui.sheet(c ? c.name : 'Add connection', body, actions);
}

async function testConnection(c) {
    if (c.protocol === 'baseport' || isSql(c.protocol)) {
        const tables = await ui.send(`/api/_admin/connections/${c.id}/tables`, {
            method: 'GET',
            failure: 'The connection did not answer.'
        });
        if (tables) ui.toast(`Connected. ${tables.length} ${c.protocol === 'baseport' ? 'published ' : ''}table(s).`, 'success');
        return;
    }
    const result = await ui.send(`/api/_admin/connections/${c.id}/test`, {
        method: 'POST',
        body: { path: '', paging: 'auto' },
        failure: 'The connection did not answer.'
    });
    if (result) ui.toast(`Connected. ${result.rows} record(s) on the first page.`, 'success');
}

async function pathField(connectionSelect, apiOnly) {
    const path = ui.field('Path', { placeholder: 'orders', mono: true, help: 'Relative to the base URL.' });
    const list = ui.el('datalist', null, { id: 'remotePathOptions' });
    path.ctrl.setAttribute('list', list.id);
    path.append(list);
    const refresh = async () => {
        list.innerHTML = '';
        const c = connectionById(connectionSelect.value);
        const sql = Boolean(c) && isSql(c.protocol);
        path.querySelector('.field-label-text').textContent = sql ? 'Table' : 'Path';
        path.querySelector('.field-help').textContent = sql ? 'From the database catalog.' : 'Relative to the base URL.';
        apiOnly.forEach((f) => { f.hidden = sql; });
        if (!c || (c.protocol !== 'baseport' && !sql)) return;
        const tables = await ui.send(`/api/_admin/connections/${c.id}/tables`, {
            method: 'GET',
            failure: 'Could not list the tables.'
        });
        (tables || []).forEach((t) => list.append(ui.el('option', null, { value: t.apiName, textContent: t.title || t.apiName })));
    };
    connectionSelect.addEventListener('change', refresh);
    await refresh();
    return path;
}

async function openRemoteImport() {
    await loadConnections();
    if (!connectionData.length) {
        ui.toast('Add a connection under Settings › Connections first.', 'error');
        return;
    }
    const body = ui.el('div');
    const connection = ui.field('Connection', {
        type: 'select',
        options: connectionData.map((c) => [c.id, c.name]),
        value: connectionData[0].id
    });
    const paging = ui.field('Paging', { type: 'select', options: PAGING, value: 'auto' });
    const pointer = ui.field('Records at', { placeholder: 'Detected', mono: true, help: 'Property holding the list, such as data/items.' });
    const path = await pathField(connection.ctrl, [paging, pointer]);
    const choices = columnChoices([]);
    const target = ui.field('Into', {
        type: 'select',
        options: [['', 'A new table'], ...currentTables.filter((t) => !t.isProxy).map((t) => [t.id, t.name])]
    });
    const tableName = ui.field('Table name', { placeholder: 'Taken from the connection' });
    target.ctrl.addEventListener('change', () => { tableName.hidden = !!target.ctrl.value; });
    const preview = ui.el('div');
    body.append(connection, path, paging, pointer, target, tableName, choices.el, preview);

    const payload = () => ({
        connectionId: connection.ctrl.value,
        path: path.ctrl.value.trim(),
        paging: paging.ctrl.value,
        recordsPointer: pointer.ctrl.value.trim(),
        columns: choices.value(),
        ...(target.ctrl.value ? { tableId: target.ctrl.value } : { tableName: tableName.ctrl.value.trim() || undefined })
    });

    const actions = ui.el('div', 'form-actions');
    actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }));
    const check = ui.button('Preview', () => ui.busy(check, async () => {
        const data = await ui.send('/api/_admin/imports/preview', {
            method: 'POST',
            body: payload(),
            failure: 'The API could not be read.'
        });
        if (!data) return;
        choices.show(data.columns || []);
        renderImportPreview(preview, { ...data, rowCount: data.firstPageRows });
        if (!tableName.ctrl.value.trim()) tableName.ctrl.value = data.name || '';
    }), { variant: 'btn-outline' });
    const run = ui.button('Import', () => ui.busy(run, async () => {
        const started = await ui.send('/api/_admin/imports', {
            method: 'POST',
            body: payload(),
            failure: 'The import could not start.'
        });
        if (!started) return;
        ui.closeSheet();
        ui.toast('Import started.', 'success');
        await loadTables();
        selectTable(started.table);
        await followRun(started.run.id);
    }));
    actions.append(check, run);
    ui.sheet('Import from API', body, actions);
}

async function followRun(id) {
    for (let i = 0; i < 3600; i++) {
        await new Promise((r) => setTimeout(r, 1000));
        const run = await ui.send(`/api/_admin/imports/${id}`, { method: 'GET', failure: 'Lost track of the import.' });
        if (!run) return;
        if (run.status !== 'done' && run.status !== 'failed') continue;
        if (run.status === 'done') ui.toast(runSummary(run), 'success', 8000);
        else ui.toast(run.message || 'The import failed.', 'error', 10000);
        run.errors.forEach((e) => ui.toast(e, 'error', 10000));
        await loadTables();
        if (currentTablePublicId === run.tableId) {
            const table = currentTables.find((t) => t.id === run.tableId);
            if (table) selectTable(table);
        }
        return;
    }
}

function runSummary(run) {
    const unchanged = run.rows - run.inserted - run.updated - run.rejected;
    const parts = [`${run.rows} read`, `${run.inserted} added`, `${run.updated} updated`];
    if (unchanged > 0) parts.push(`${unchanged} unchanged`);
    if (run.deleted) parts.push(`${run.deleted} deleted`);
    if (run.rejected) parts.push(`${run.rejected} rejected`);
    return parts.join(', ') + '.';
}

async function loadClones() {
    const body = document.getElementById('cloneBody');
    if (!body) return;
    const loaded = await ui.send('/api/_admin/clones', { method: 'GET', failure: 'Could not load clones.' });
    if (!loaded) return;
    cloneData = loaded;
    body.innerHTML = '';
    document.getElementById('cloneEmpty').classList.toggle('hidden', loaded.length > 0);

    loaded.forEach((c) => {
        const tr = ui.el('tr', 'row-link');
        tr.onclick = (ev) => {
            if (ev.target.closest('button')) return;
            openCloneSheet(c.id);
        };
        const actions = ui.el('td', 'cell-actions end');
        const run = ui.button('Run now', () => ui.busy(run, () => runClone(c)), { size: 'btn-sm' });
        actions.append(run);
        tr.append(
            ui.el('td', null, { textContent: c.name }),
            ui.el('td', null, { textContent: label(CLONE_MODES, c.mode) }),
            ui.el('td', 'mono', { textContent: c.schedule }),
            ui.el('td', 'muted', { textContent: c.enabled ? formatWhen(c.nextRunAt) : 'Paused' }),
            ui.el('td', 'muted', { textContent: formatWhen(c.lastRunAt) }),
            actions);
        body.append(tr);
    });
}

async function runClone(c) {
    const run = await ui.send(`/api/_admin/clones/${c.id}/run`, {
        method: 'POST',
        success: `${c.name} queued.`,
        failure: 'Could not start the clone.'
    });
    if (!run) return;
    await followRun(run.id);
    await loadClones();
}

async function openCloneSheet(id) {
    const c = id ? cloneData.find((x) => x.id === id) : null;
    await loadConnections();
    if (!connectionData.length) {
        ui.toast('Add a connection first.', 'error');
        return;
    }
    const tables = currentTables.filter((t) => !t.isProxy);
    const body = ui.el('div');

    const name = ui.field('Name', { value: c ? c.name : '' });
    const connection = ui.field('Connection', {
        type: 'select',
        options: connectionData.map((x) => [x.id, x.name]),
        value: c ? c.connectionId : connectionData[0].id
    });
    const paging = ui.field('Paging', { type: 'select', options: PAGING, value: c ? c.paging : 'auto' });
    const pointer = ui.field('Records at', { value: c ? c.recordsPointer : '', placeholder: 'Detected', mono: true });
    const inconsistent = ui.switchRow('Allow an inconsistent source (SQL Server without snapshot isolation)', { checked: c ? c.allowInconsistentSource : false });
    const path = await pathField(connection.ctrl, [paging, pointer]);
    path.ctrl.value = c ? c.path : '';
    const choices = columnChoices(c ? c.columns : []);
    const columnsButton = ui.button('Columns', () => ui.busy(columnsButton, async () => {
        const data = await ui.send('/api/_admin/imports/preview', {
            method: 'POST',
            body: { connectionId: connection.ctrl.value, path: path.ctrl.value.trim(), columns: choices.value() },
            failure: 'The table could not be read.'
        });
        if (data) choices.show(data.columns || []);
    }), { variant: 'btn-outline', size: 'btn-sm' });
    const syncSql = () => {
        const x = connectionById(connection.ctrl.value);
        const sql = Boolean(x) && isSql(x.protocol);
        columnsButton.hidden = !sql;
        inconsistent.hidden = !(x && x.protocol === 'sqlserver');
    };
    connection.ctrl.addEventListener('change', syncSql);
    syncSql();
    const table = ui.field('Table', {
        type: 'select',
        options: [['', 'Choose a table'], ...tables.map((t) => [t.id, t.name])],
        value: c ? c.tableId : ''
    });
    const mode = ui.field('Mode', {
        type: 'select',
        options: CLONE_MODES,
        value: c ? c.mode : 'upsert',
        help: 'Mirror also deletes records the source no longer has.'
    });
    const key = ui.field('Key field', { type: 'select', options: [], help: 'Identifies a record on both sides.' });
    const fillKeys = () => {
        const t = tables.find((x) => x.id === table.ctrl.value);
        const current = key.ctrl.value || (c ? c.keyField : '');
        key.ctrl.innerHTML = '';
        [['', 'None'], ...((t && t.fields) || []).map((f) => [f.name, f.label || f.name])]
            .forEach(([v, l]) => key.ctrl.append(ui.el('option', null, { value: v, textContent: l })));
        key.ctrl.value = current;
        key.hidden = mode.ctrl.value === 'append';
    };
    table.ctrl.addEventListener('change', fillKeys);
    mode.ctrl.addEventListener('change', fillKeys);
    fillKeys();
    const schedule = ui.field('Schedule', { value: c ? c.schedule : '0 0 * * * *', mono: true, help: 'Cron with seconds.' });
    const enabled = ui.switchRow('Enabled', { checked: c ? c.enabled : true });
    const large = ui.switchRow('Allow a mirror to delete more than half the table', { checked: c ? c.allowLargeDeletes : false });
    body.append(name, connection, path, columnsButton, choices.el, paging, pointer, table, mode, key, schedule, enabled, large, inconsistent);
    if (c) body.append(await cloneRuns(c));

    const actions = ui.el('div', 'form-actions');
    if (c) {
        actions.append(ui.button('Delete', async () => {
            const ok = await ui.confirm({
                title: 'Delete clone',
                message: `Remove ${c.name}? The table and its records stay.`,
                confirmLabel: 'Delete',
                danger: true,
            });
            if (!ok) return;
            const done = await ui.send(`/api/_admin/clones/${c.id}`, {
                method: 'DELETE',
                success: 'Clone deleted.',
                failure: 'Could not delete the clone.',
            });
            if (!done) return;
            ui.closeSheet();
            await loadClones();
        }, { variant: 'btn-danger' }), ui.el('div', 'form-actions-spacer'));
    }
    actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }));
    const save = ui.button(c ? 'Save' : 'Add clone', () => ui.busy(save, async () => {
        const saved = await ui.send(c ? `/api/_admin/clones/${c.id}` : '/api/_admin/clones', {
            method: c ? 'PATCH' : 'POST',
            body: {
                name: name.ctrl.value.trim(),
                connectionId: connection.ctrl.value,
                path: path.ctrl.value.trim(),
                paging: paging.ctrl.value,
                recordsPointer: pointer.ctrl.value.trim(),
                tableId: table.ctrl.value,
                mode: mode.ctrl.value,
                keyField: mode.ctrl.value === 'append' ? '' : key.ctrl.value,
                schedule: schedule.ctrl.value.trim(),
                enabled: enabled.ctrl.checked,
                allowLargeDeletes: large.ctrl.checked,
                allowInconsistentSource: inconsistent.ctrl.checked,
                columns: choices.value()
            },
            success: c ? 'Clone saved.' : 'Clone added.',
            failure: 'Could not save the clone.',
        });
        if (!saved) return;
        ui.closeSheet();
        await loadClones();
    }));
    actions.append(save);

    ui.sheet(c ? c.name : 'Add clone', body, actions);
}

async function cloneRuns(c) {
    const wrap = ui.el('div', 'field');
    wrap.append(ui.el('span', 'field-label-text', { textContent: 'Recent runs' }));
    const runs = await ui.send(`/api/_admin/clones/${c.id}/runs`, { method: 'GET', failure: 'Could not load runs.' }) || [];
    if (!runs.length) {
        wrap.append(ui.el('p', 'muted', { textContent: 'Not run yet.' }));
        return wrap;
    }
    const table = ui.el('table', 'table');
    const head = ui.el('tr');
    ['When', 'Status', 'Result'].forEach((h) => head.append(ui.el('th', null, { textContent: h })));
    table.appendChild(ui.el('thead')).appendChild(head);
    const tbody = ui.el('tbody');
    runs.forEach((r) => {
        const tr = ui.el('tr');
        tr.append(
            ui.el('td', 'muted', { textContent: formatWhen(r.createdAt) }),
            ui.el('td', null, { textContent: r.status }),
            ui.el('td', 'muted', { textContent: r.status === 'failed' ? r.message : runSummary(r), title: r.errors.join('\n') }));
        tbody.append(tr);
    });
    table.append(tbody);
    const scroll = ui.el('div', 'table-wrap');
    scroll.append(table);
    wrap.append(scroll);
    return wrap;
}

function columnChoices(initial) {
    const el = ui.el('div', 'field');
    const picked = new Map((initial || []).map((s) => [s.column, s.choice]));
    const show = (columns) => {
        el.innerHTML = '';
        const open = columns.filter((col) => !col.fieldType);
        if (!open.length) return;
        el.append(ui.el('span', 'field-label-text', { textContent: 'Columns Baseport cannot map' }));
        open.forEach((col) => {
            const f = ui.field(`${col.name} (${col.sourceType})`, { type: 'select', options: COLUMN_CHOICES, value: picked.get(col.name) || '' });
            f.ctrl.addEventListener('change', () => picked.set(col.name, f.ctrl.value));
            el.append(f);
        });
    };
    return {
        el,
        show,
        value: () => [...picked].filter(([, choice]) => choice).map(([column, choice]) => ({ column, choice }))
    };
}
