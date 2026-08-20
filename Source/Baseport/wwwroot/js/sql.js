/* SQL console: saved queries + execution */

let currentQueryId = null;
let currentQueryName = '';
let sqlEditor = null;

function sqlValue() {
    return sqlEditor ? sqlEditor.getValue() : (document.getElementById('sqlInput') || {}).value || '';
}

function setSqlValue(sql) {
    if (sqlEditor) sqlEditor.setValue(sql);
    else document.getElementById('sqlInput').value = sql;
}

// only fetch when needed.
let codeMirrorLoad = null;

function loadCodeMirror() {
    if (typeof CodeMirror !== 'undefined') return Promise.resolve();
    codeMirrorLoad ||= new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = '/vendor/codemirror-bundle.js';
        s.onload = resolve;
        s.onerror = reject;
        document.head.append(s);
    });
    return codeMirrorLoad;
}

async function initSqlEditor() {
    if (sqlEditor) return;
    const ta = document.getElementById('sqlInput');
    if (!ta) return;
    // A failed fetch leaves the plain textarea, which still runs queries.
    await loadCodeMirror().catch(() => {});
    if (sqlEditor || typeof CodeMirror === 'undefined') return;
    sqlEditor = CodeMirror.fromTextArea(ta, {
        mode: 'text/x-sql',
        lineNumbers: true,
        lineWrapping: true,
        matchBrackets: true,
        placeholder: ta.placeholder,
        indentUnit: 2,
        extraKeys: {
            'Ctrl-Enter': () => runSql(),
            'Ctrl-S': () => {
                saveQuery();
                return false;
            },
        },
    });
    sqlEditor.on('keyup', (cm, e) => {
        if (e.ctrlKey || e.metaKey || e.altKey) return;
        if (e.key === ' ' || e.key === '.') cm.showHint({
            completeSingle: false
        });
    });
}

function renderQueryBreadcrumb() {
    const el = document.getElementById('sqlQueryName');
    if (!el) return;
    el.innerHTML = currentQueryId ?
        `Editor <span class="muted">›</span> <strong>${escapeHtml(currentQueryName)}</strong>` :
        `<strong>${escapeHtml(currentQueryName || 'New Query')}</strong>`;
    syncQueryActions();
}

function syncQueryActions() {
    const el = document.getElementById('sqlQueryActions');
    if (el) el.classList.toggle('hidden', !currentQueryId);
}

function renameCurrentQuery() {
    if (currentQueryId) renameQuery({ id: currentQueryId, name: currentQueryName });
}

function deleteCurrentQuery() {
    if (currentQueryId) deleteQuery({ id: currentQueryId, name: currentQueryName });
}

function clearSqlOutput() {
    const status = document.getElementById('sqlStatus');
    status.className = 'field-hint';
    status.style.color = '';
    status.innerText = '';
    document.getElementById('sqlResult').classList.add('hidden');
    document.getElementById('sqlNoData').classList.add('hidden');
}

function showSqlError(msg) {
    const status = document.getElementById('sqlStatus');
    status.classList.add('invalid');
    status.innerText = msg;
}

function showQueryPlaceholder(name) {
    const result = document.getElementById('sqlResult');
    if (!result) return;
    result.innerHTML =
        '<div class="table-wrap"><table class="table">' +
        `<thead><tr><th>${escapeHtml(name || 'Query')}</th></tr></thead>` +
        '<tbody><tr><td class="muted">Run the query to see results.</td></tr></tbody>' +
        '</table></div>';
    result.classList.remove('hidden');
}

async function loadSavedQueries() {
    savedQueries = await fetch('/api/_admin/queries').then((r) => r.json());
    refreshSidebar('sql');
}

function selectQuery(q) {
    navigate(`/sql/${q.id}`);
}

function applyQuery(q) {
    currentQueryId = q.id;
    currentQueryName = q.name;
    setSqlValue(q.sql);
    clearSqlOutput();
    showQueryPlaceholder(q.name);
    renderQueryBreadcrumb();
    renderSchedule(q);
}

function renderSchedule(q) {
    const panel = document.getElementById('sqlSchedule');
    if (!panel) return;
    panel.classList.toggle('hidden', !q);
    if (!q) return;
    document.getElementById('sqlScheduleCron').value = q.schedule || '';
    document.getElementById('sqlScheduleWebhook').value = q.webhookUrl || '';
    document.getElementById('sqlScheduleEnabled').checked = q.scheduleEnabled === true;
    showScheduleStatus(q);
}

function showScheduleStatus(q) {
    const el = document.getElementById('sqlScheduleStatus');
    if (!el) return;
    const parts = [];
    if (q.scheduleEnabled && q.nextRunAt) parts.push(`Next run ${new Date(q.nextRunAt).toLocaleString()}`);
    else if (q.schedule) parts.push('Paused');
    if (q.lastResult) parts.push(q.lastResult);
    el.textContent = parts.join(' · ');
}

async function saveSchedule() {
    if (!currentQueryId) return;
    const saved = await ui.send(`/api/_admin/queries/${currentQueryId}`, {
        method: 'PATCH',
        body: {
            schedule: document.getElementById('sqlScheduleCron').value.trim(),
            webhookUrl: document.getElementById('sqlScheduleWebhook').value.trim(),
            scheduleEnabled: document.getElementById('sqlScheduleEnabled').checked,
        },
        success: 'Schedule saved.',
    });
    if (saved) renderSchedule(saved);
}

// Proves the destination answers without waiting for the cron, so a wrong url is found here rather than in tomorrow's log.
async function runScheduleNow() {
    if (!currentQueryId) return;
    const ran = await ui.send(`/api/_admin/queries/${currentQueryId}/run`, {
        method: 'POST',
        success: 'Query ran.',
    });
    if (ran) renderSchedule(ran);
}

function newQuery() {
    navigate('/sql');
}

function clearQuery() {
    currentQueryId = null;
    currentQueryName = '';
    setSqlValue('');
    clearSqlOutput();
    renderQueryBreadcrumb();
    renderSchedule(null);
}

async function saveQuery() {
    const sql = sqlValue();
    if (!sql.trim()) {
        showSqlError('Enter a query to save.');
        return;
    }
    const name = currentQueryId ?
        currentQueryName :
        await ui.ask({
            title: 'Save query',
            label: 'Query name',
            placeholder: 'Field counts'
        });
    if (!name) return;
    const res = await fetch(currentQueryId ? `/api/_admin/queries/${currentQueryId}` : '/api/_admin/queries', {
        method: currentQueryId ? 'PATCH' : 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            name,
            sql
        }),
    });
    const data = await res.json();
    if (!res.ok) {
        showSqlError((data.errors || ['Save failed.']).join(' '));
        return;
    }
    currentQueryId = data.id;
    currentQueryName = data.name;
    renderSchedule(data);
    const status = document.getElementById('sqlStatus');
    status.style.color = '';
    status.innerText = `Saved ${new Date(data.updatedAt).toLocaleTimeString()}`;
    renderQueryBreadcrumb();
    loadSavedQueries();
}

async function renameQuery(q) {
    const name = await ui.ask({
        title: 'Rename query',
        label: 'Query name',
        value: q.name
    });
    if (!name || name === q.name) return;
    await ui.send(`/api/_admin/queries/${q.id}`, {
        method: 'PATCH',
        body: {
            name
        },
        success: 'Query renamed.'
    });
    await loadSavedQueries();
}

function deleteQuery(q) {
    openModal({
        title: 'Delete query',
        message: `Delete "${q.name}"? This cannot be undone.`,
        confirmLabel: 'Delete',
        danger: true,
        onConfirm: async () => {
            await fetch(`/api/_admin/queries/${q.id}`, {
                method: 'DELETE'
            });
            if (currentQueryId === q.id) newQuery();
            else loadSavedQueries();
        },
    });
}

async function runSql() {
    const sql = sqlValue();
    clearSqlOutput();
    const status = document.getElementById('sqlStatus');
    status.innerText = 'Running…';
    // The grid arrives rendered; the result set is the widest thing this console draws.
    const meta = await ui.fragment('sqlResult', '/api/_admin/fragments/sql', {
        body: {
            sql,
            queryId: currentQueryId || null
        },
        failure: 'Query failed.',
        onError: showSqlError,
    });
    if (!meta) return;

    const time = new Date().toLocaleTimeString();
    if (Number(meta.header('X-Column-Count')) === 0) {
        status.innerText = `Executed ${time}`;
        document.getElementById('sqlNoData').classList.remove('hidden');
        if (currentQueryId) loadSavedQueries();
        return;
    }
    status.innerText =
        `${meta.header('X-Row-Count')} row(s) · Executed ${time}` +
        (meta.header('X-Truncated') === '1' ? ' (truncated at 200)' : '');
    document.getElementById('sqlResult').classList.remove('hidden');
    if (currentQueryId) loadSavedQueries();
}