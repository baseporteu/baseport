/* Activity log and instance settings. */
/* Logs: server-side filter / sort / pagination */

let logsPage = 1;
let logsPerPage = 25;
let logsSort = 'createdAt';
let logsOrder = 'desc';

function sortLogs(field) {
    if (logsSort === field) logsOrder = logsOrder === 'desc' ? 'asc' : 'desc';
    else {
        logsSort = field;
        logsOrder = 'desc';
    }
    document.querySelectorAll('.logs-table th.th-sort').forEach((th) => {
        th.classList.toggle('sort-asc', th.dataset.sort === logsSort && logsOrder === 'asc');
        th.classList.toggle('sort-desc', th.dataset.sort === logsSort && logsOrder === 'desc');
    });
    loadLogs(1);
}

async function loadLogs(page) {
    logsPage = page || logsPage;
    const filter = (document.getElementById('logsFilter').value || '').trim();
    // Rows arrive rendered; paging comes back in headers.
    const meta = await ui.fragment(
        'logsList',
        `/api/_admin/fragments/logs?page=${logsPage}&perPage=${logsPerPage}&sort=${logsSort}&order=${logsOrder}` +
        (filter ? `&filter=${encodeURIComponent(filter)}` : ''),
    );
    if (!meta) return;

    document.getElementById('logsEmpty').classList.toggle('hidden', meta.total > 0);
    makePager(document.getElementById('logsPager'), {
        page: meta.page,
        total: meta.total,
        perPage: meta.pageSize,
        onPage: (p) => loadLogs(p),
        onPerPage: (n) => {
            logsPerPage = n;
            loadLogs(1);
        },
    });
}

/* Settings: Host / Auth / Jobs / Backups */

let settingsData = null;

function settingsPage(page) {
    navigate(`/settings/${page}`);
}

// A link round trip comes back here instead of to a sign-in screen, the console reports its own outcome. Taken out of the address bar so a reload does not repeat it.
function reportLinkOutcome() {
    const code = new URLSearchParams(location.search).get('sso');
    if (!code) return;

    const url = new URL(location.href);
    url.searchParams.delete('sso');
    history.replaceState(null, '', url);

    if (code === 'linked') ui.toast('Your account is now linked. Every other session has been signed out.', 'success', 8000);
    else ui.toast(SSO_PROBLEMS[code] || SSO_PROBLEMS.failed, 'error', 8000);
}

function applySettingsPage(page) {
    settingsCurrentPage = page;
    if (page === 'auth') reportLinkOutcome();
    document.querySelectorAll('.settings-pane').forEach((p) => p.classList.toggle('hidden', p.dataset.pane !== page));
    const titles = {
        host: 'Host',
        auth: 'Authentication',
        providers: 'Providers',
        sites: 'Sites',
        jobs: 'Jobs',
        backups: 'Backups'
    };
    const subs = {
        host: 'Application and database overview.',
        auth: 'User accounts, REST API tokens and per-table access.',
        providers: 'Native database wire-protocol listeners.',
        sites: 'Where published forms may be embedded.',
        jobs: 'Background maintenance tasks.',
        backups: 'Stored snapshots of the database.',
    };
    document.getElementById('settingsTitle').innerText = titles[page];
    document.getElementById('settingsSub').innerText = subs[page];
}

async function loadSettings() {
    settingsData = await fetch('/api/_admin/settings').then((r) => r.json());
    renderSettingsInfo();
    document.getElementById('settingsAppName').value = settingsData.appName || 'Baseport';
    document.getElementById('settingsSiteUrl').value = settingsData.siteUrl || '';
    document.getElementById('settingsLogRetention').value = settingsData.logRetentionSec ?? 0;
    // The browser ships ISO 4217 and the IANA zone list, neither is ours to carry or keep current.
    ui.fillOptions(document.getElementById('settingsCurrency'), ui.currencyOptions(), settingsData.currency || 'EUR');
    ui.fillOptions(document.getElementById('settingsTimeZone'), ui.timeZoneOptions(), settingsData.timeZone || 'UTC');
    ui.timeZone(settingsData.timeZone || 'UTC');
    document.getElementById('settingsAllowedOrigins').value = settingsData.allowedOrigins || '';
    renderAllowedOrigins(settingsData.allowedOrigins || '');
    document.getElementById('settingsBackupRetention').value = settingsData.backupRetention ?? 5;
    document.getElementById('settingsOpenApiEnabled').checked = settingsData.openApiEnabled !== false;
    document.getElementById('settingsProxyPrivateTargetsEnabled').checked = settingsData.proxyPrivateTargetsEnabled === true;
    document.getElementById('settingsApiTitle').value = settingsData.apiTitle || '';
    document.getElementById('settingsApiDescription').value = settingsData.apiDescription || '';
    document.getElementById('settingsPublicAuthEnabled').checked = settingsData.publicAuthEnabled === true;
    document.getElementById('settingsPublicRegistrationEnabled').checked = settingsData.publicRegistrationEnabled === true;
    document.getElementById('settingsAnonymousAuthEnabled').checked = settingsData.anonymousAuthEnabled === true;
    document.getElementById('settingsAnonymousRetention').value = settingsData.anonymousRetentionDays ?? 30;
    document.getElementById('settingsAuthIssuer').value = settingsData.authIssuer || 'baseport';
    document.getElementById('settingsAuthTokenLifetime').value = settingsData.authTokenLifetimeSec ?? 3600;
    document.getElementById('settingsAuthRefreshLifetime').value = settingsData.authRefreshLifetimeDays ?? 30;
    document.getElementById('settingsAuthJwks').textContent = settingsData.authJwksPath || '/api/auth/v1/jwks.json';
    document.getElementById('settingsPostgresEnabled').checked = settingsData.postgresEnabled === true;
    document.getElementById('settingsPostgresPort').value = settingsData.postgresPort ?? 5432;
    document.getElementById('settingsPostgresBindAddress').value = settingsData.postgresBindAddress || '127.0.0.1';
    document.getElementById('settingsTdsEnabled').checked = settingsData.tdsEnabled === true;
    document.getElementById('settingsTdsPort').value = settingsData.tdsPort ?? 1433;
    document.getElementById('settingsTdsBindAddress').value = settingsData.tdsBindAddress || '127.0.0.1';
    await loadApiTables();
    await loadOidcProviders();
    await loadJobs();
    await loadBackups();
}

function formatUptime(u) {
    if (!u) return '0s';
    const m = String(u).match(/(?:(\d+)\.)?(\d+):(\d+):(\d+)/);
    if (!m) return String(u);
    const [, d, h, min, sec] = m;
    const parts = [];
    if (Number(d) > 0) parts.push(Number(d) + 'd');
    if (Number(h) > 0 || Number(d) > 0) parts.push(Number(h) + 'h');
    if (Number(min) > 0 || Number(h) > 0) parts.push(Number(min) + 'm');
    parts.push(Number(sec) + 's');
    return parts.join(' ');
}

function renderSettingsInfo() {
    const s = settingsData;
    const rows = [
        ['Version', s.version],
        ['Uptime', formatUptime(s.uptime)],
        ['OpenAPI spec', s.openapiPath],
        ['API reference', s.docsPath],
        ['Database path', s.dbPath],
        ['Database size', s.dbSizeBytes != null ? fmtSize(s.dbSizeBytes) : 'n/a'],
        ['Free disk space', s.freeDiskBytes != null ? fmtSize(s.freeDiskBytes) : 'n/a'],
        ['Estimated index size', s.estimatedIndexBytes != null ? fmtSize(s.estimatedIndexBytes) : 'n/a'],
        ['Tables', (s.tables ?? 0).toLocaleString()],
        ['Fields', (s.fields ?? 0).toLocaleString()],
        ['Forms', (s.forms ?? 0).toLocaleString()],
        ['Records', (s.records ?? 0).toLocaleString()],
        ['Tables with API enabled', (s.apiEnabledTables ?? 0).toLocaleString()],
    ];
    const el = document.getElementById('settingsInfoRows');
    el.innerHTML = rows
        .map(([k, v]) => `<span class="k">${escapeHtml(k)}</span><span class="v">${escapeHtml(String(v))}</span>`)
        .join('');
}

async function submitSettings(btn) {
    await ui.busy(btn, async () => {
        const body = {
            appName: document.getElementById('settingsAppName').value,
            siteUrl: document.getElementById('settingsSiteUrl').value,
            logRetentionSec: Number(document.getElementById('settingsLogRetention').value) || 0,
            currency: document.getElementById('settingsCurrency').value.trim().toUpperCase(),
            timeZone: document.getElementById('settingsTimeZone').value,
            backupRetention: Number(document.getElementById('settingsBackupRetention').value) || 5,
        };
        const res = await fetch('/api/_admin/settings', {
            method: 'PUT',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(body),
        });
        const data = await res.json();
        if (!res.ok) {
            openModal({
                title: 'Could not save settings',
                message: (data.errors || ['Failed to save settings.']).join(' '),
                confirmLabel: 'OK',
            });
            return;
        }
        settingsData = {
            ...settingsData,
            ...data
        };
        renderSettingsInfo();
        ui.toast('Application settings have been updated.', 'success');
    });
}

// Separate from the instance settings save: this group is published to anyone who opens /docs, and saving it should not depend on the Host page being valid.
async function submitApiInfo(btn) {
    await ui.busy(btn, async () => {
        const saved = await ui.send('/api/_admin/settings', {
            method: 'PUT',
            body: {
                apiTitle: document.getElementById('settingsApiTitle').value,
                apiDescription: document.getElementById('settingsApiDescription').value,
                openApiEnabled: document.getElementById('settingsOpenApiEnabled').checked,
            },
            success: 'The API reference has been updated.',
        });
        if (!saved) return;
        settingsData = {
            ...settingsData,
            ...saved
        };
        document.getElementById('settingsApiTitle').value = saved.apiTitle || '';
        document.getElementById('settingsApiDescription').value = saved.apiDescription || '';
    });
}

async function submitAuthSettings(btn) {
    await ui.busy(btn, async () => {
        const saved = await ui.send('/api/_admin/settings', {
            method: 'PUT',
            body: {
                publicAuthEnabled: document.getElementById('settingsPublicAuthEnabled').checked,
                publicRegistrationEnabled: document.getElementById('settingsPublicRegistrationEnabled').checked,
                anonymousAuthEnabled: document.getElementById('settingsAnonymousAuthEnabled').checked,
                anonymousRetentionDays: Number(document.getElementById('settingsAnonymousRetention').value) || 0,
                authIssuer: document.getElementById('settingsAuthIssuer').value.trim() || 'baseport',
                authTokenLifetimeSec: Number(document.getElementById('settingsAuthTokenLifetime').value) || 3600,
                authRefreshLifetimeDays: Number(document.getElementById('settingsAuthRefreshLifetime').value) || 30,
            },
            success: 'End-user authentication has been updated.',
        });
        if (!saved) return;
        settingsData = {
            ...settingsData,
            ...saved
        };
        document.getElementById('settingsAuthIssuer').value = saved.authIssuer || 'baseport';
        document.getElementById('settingsAuthTokenLifetime').value = saved.authTokenLifetimeSec ?? 3600;
        document.getElementById('settingsAuthRefreshLifetime').value = saved.authRefreshLifetimeDays ?? 30;
    });
}

async function rotateAuthKey(btn) {
    const ok = await ui.confirm({
        title: 'Rotate the signing key?',
        message: 'Every access and refresh token issued so far stops working, and every signed-in user has to sign in again.',
        confirmLabel: 'Rotate',
        danger: true,
    });
    if (!ok) return;
    await ui.busy(btn, async () => {
        await ui.send('/api/_admin/settings/auth-key', {
            method: 'POST',
            success: 'A new signing key is in use.',
        });
    });
}

async function saveProviderSettings(btn) {
    await ui.busy(btn, async () => {
        const saved = await ui.send('/api/_admin/settings', {
            method: 'PUT',
            body: {
                postgresEnabled: document.getElementById('settingsPostgresEnabled').checked,
                postgresPort: Number(document.getElementById('settingsPostgresPort').value) || 5432,
                postgresBindAddress: document.getElementById('settingsPostgresBindAddress').value.trim() || '127.0.0.1',
                tdsEnabled: document.getElementById('settingsTdsEnabled').checked,
                tdsPort: Number(document.getElementById('settingsTdsPort').value) || 1433,
                tdsBindAddress: document.getElementById('settingsTdsBindAddress').value.trim() || '127.0.0.1',
                proxyPrivateTargetsEnabled: document.getElementById('settingsProxyPrivateTargetsEnabled').checked,
            },
            success: 'Provider settings have been updated. Listening ports apply within a few seconds.',
        });
        if (!saved) return;
        settingsData = {
            ...settingsData,
            ...saved
        };
    });
}

function apiSwitch(id, checked, onChange) {
    const sw = document.createElement('label');
    sw.className = 'switch';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.id = id;
    cb.checked = checked;
    cb.onchange = () => onChange(cb.checked);
    sw.append(cb, document.createElement('span'), document.createElement('span'));
    sw.children[1].className = 'track';
    sw.children[2].className = 'thumb';
    return sw;
}

// just the two live/docs toggles plus a way into the endpoint sheet; name, docs and methods live there
async function loadApiTables() {
    const tables = await fetch('/api/_admin/tables').then((r) => r.json());
    const list = document.getElementById('apiTableList');
    list.innerHTML = '';
    document.getElementById('apiTablesEmpty').classList.toggle('hidden', tables.length > 0);
    tables.forEach((t) => {
        const li = document.createElement('li');
        li.className = 'api-table-row';
        const name = document.createElement('span');
        name.className = 'api-table-name';
        name.textContent = t.name;

        const apiGroup = document.createElement('span');
        apiGroup.className = 'api-table-state';
        apiGroup.append('REST API');
        const apiToggle = apiSwitch(`apiEnabled-${t.id}`, !!t.apiEnabled, (checked) => toggleTableApi(t.id, checked));
        apiGroup.append(apiToggle);

        const docsGroup = document.createElement('span');
        docsGroup.className = 'api-table-state';
        docsGroup.title = 'Whether this table appears in the OpenAPI document. It stays live at /api/v1 either way.';
        docsGroup.append('OpenAPI');
        const docsToggle = apiSwitch(`apiDocs-${t.id}`, t.apiDocsEnabled !== false, (checked) => toggleTableApiDocs(t.id, checked));
        docsGroup.append(docsToggle);

        const configureBtn = ui.button('Configure', () => openEndpointSheet(t.id), {
            size: 'btn-sm',
            variant: 'btn-outline'
        });

        li.append(name, apiGroup, docsGroup, configureBtn);
        list.appendChild(li);
    });
}

// refreshes currentTables too, opening the table right after doesn't show stale data until a hard refresh
async function toggleTableApi(pid, enabled) {
    const res = await fetch(`/api/_admin/tables/${pid}/api`, {
        method: 'PUT',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            enabled
        }),
    });
    if (!res.ok) {
        ui.toast('The change could not be saved.', 'error');
        await loadApiTables();
        return;
    }
    if (settingsData) settingsData.apiEnabledTables = (settingsData.apiEnabledTables || 0) + (enabled ? 1 : -1);
    if (settingsData) renderSettingsInfo();
    await loadTables();
}

async function toggleTableApiDocs(pid, enabled) {
    const saved = await ui.send(`/api/_admin/tables/${pid}`, {
        method: 'PATCH',
        body: {
            apiDocsEnabled: enabled
        },
        failure: 'The change could not be saved.',
    });
    if (!saved) {
        await loadApiTables();
        return;
    }
    await loadTables();
}

/* Jobs: cron schedules, run now, enabled toggle */

function formatWhen(iso) {
    return iso ? ui.when(iso) : 'never';
}

function switchHtml(id, checked) {
    const label = document.createElement('label');
    label.className = 'switch';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.id = id;
    input.checked = checked;
    const track = document.createElement('span');
    track.className = 'track';
    const thumb = document.createElement('span');
    thumb.className = 'thumb';
    label.append(input, track, thumb);
    return label;
}

async function loadJobs() {
    const jobs = await fetch('/api/_admin/jobs').then((r) => r.json());
    const body = document.getElementById('jobsBody');
    body.innerHTML = '';
    jobs.forEach((job) => {
        const tr = document.createElement('tr');

        const name = document.createElement('td');
        name.textContent = job.name;
        tr.appendChild(name);

        const scheduleTd = document.createElement('td');
        const schedule = document.createElement('input');
        schedule.type = 'text';
        schedule.className = 'input input-sm';
        schedule.value = job.schedule;
        scheduleTd.appendChild(schedule);
        tr.appendChild(scheduleTd);

        const next = document.createElement('td');
        next.className = 'muted';
        next.textContent = formatWhen(job.nextRunAt);
        tr.appendChild(next);

        const last = document.createElement('td');
        last.className = 'muted';
        last.textContent = formatWhen(job.lastRunAt);
        if (job.lastResult) last.title = job.lastResult;
        tr.appendChild(last);

        const enabledTd = document.createElement('td');
        const toggle = switchHtml(job.key, job.enabled);
        toggle.querySelector('input').addEventListener('change', (ev) => saveJob(job.key, {
            enabled: ev.target.checked
        }));
        enabledTd.appendChild(toggle);
        tr.appendChild(enabledTd);

        const actionTd = document.createElement('td');
        actionTd.className = 'cell-actions end';
        const save = ui.button('Save', () => saveJob(job.key, {
            schedule: schedule.value
        }, save), {
            size: 'btn-sm'
        });
        save.disabled = true;
        schedule.addEventListener('input', () => {
            save.disabled = schedule.value === job.schedule;
        });
        schedule.addEventListener('keydown', (ev) => {
            if (ev.key !== 'Enter') return;
            if (schedule.value !== job.schedule) saveJob(job.key, {
                schedule: schedule.value
            }, save);
        });
        const run = ui.button('Run now', () => runJobNow(job.key, job.name, run), {
            size: 'btn-sm'
        });
        actionTd.append(save, run);
        tr.appendChild(actionTd);

        body.appendChild(tr);
    });
}

async function saveJob(key, patch, btn) {
    await ui.busy(btn, async () => {
        const res = await ui.send(`/api/_admin/jobs/${encodeURIComponent(key)}`, {
            method: 'PUT',
            body: patch,
            failure: 'The job could not be saved.',
        });
        if (!res) return;
        ui.toast('Job updated.', 'success');
        loadJobs();
    });
}

async function runJobNow(key, name, btn) {
    const ok = await ui.confirm({
        title: 'Run job now',
        message: `Run "${name}" now, outside its schedule?`,
        confirmLabel: 'Run now',
    });
    if (!ok) return;
    await ui.busy(btn, async () => {
        const res = await ui.send(`/api/_admin/jobs/${encodeURIComponent(key)}/run`, {
            method: 'POST',
            failure: 'The job could not be run.',
        });
        if (!res) return;
        ui.toast('Job ran.', 'success');
        loadJobs();
    });
}

/* Backups: stored snapshots on a rolling window */

function fmtSize(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / 1024 / 1024).toFixed(1) + ' MB';
}

async function loadBackups() {
    const data = await fetch('/api/_admin/backups').then((r) => r.json());
    const list = data.backups || [];
    document.getElementById('backupsEmpty').classList.toggle('hidden', list.length > 0);
    const body = document.getElementById('backupsBody');
    body.innerHTML = '';
    list.forEach((b) => {
        const tr = document.createElement('tr');
        const time = document.createElement('td');
        time.textContent = formatWhen(b.createdAt);
        const size = document.createElement('td');
        size.className = 'muted';
        size.textContent = fmtSize(b.size);
        const actions = document.createElement('td');
        actions.className = 'cell-actions end';
        actions.append(
            ui.button('Download', () => downloadBackup(b.name), {
                size: 'btn-sm',
                variant: 'btn-outline'
            }),
            ui.button('Delete', () => deleteBackup(b.name), {
                size: 'btn-sm',
                variant: 'btn-danger'
            }),
        );
        tr.append(time, size, actions);
        body.appendChild(tr);
    });
}

async function triggerBackup() {
    const size = settingsData && settingsData.dbSizeBytes != null ? fmtSize(settingsData.dbSizeBytes) : null;
    const free = settingsData && settingsData.freeDiskBytes != null ? fmtSize(settingsData.freeDiskBytes) : null;
    const retention = (settingsData && settingsData.backupRetention) || 5;
    const ok = await ui.confirm({
        // A snapshot is a second full copy of the store, and on a tight disk that is the number worth seeing before pressing the button.
        title: 'Trigger backup',
        message: [
            size ? `Copies the whole database, about ${size}${free ? `, with ${free} free` : ''}.` : 'Copies the whole database.',
            `The newest ${retention} snapshots are kept; older ones are deleted.`,
        ].join(' '),
        confirmLabel: 'Trigger backup',
    });
    if (!ok) return;
    await ui.busy(document.getElementById('triggerBackupBtn'), async () => {
        const res = await ui.send('/api/_admin/backups', {
            method: 'POST',
            failure: 'The backup could not be created.',
        });
        if (!res) return;
        ui.toast('Backup created.', 'success');
        loadBackups();
    });
}

async function saveBackupSettings(btn) {
    await ui.busy(btn, async () => {
        const retention = Number(document.getElementById('settingsBackupRetention').value);
        const res = await ui.send('/api/_admin/settings', {
            method: 'PUT',
            body: {
                backupRetention: retention
            },
            failure: 'The retention window could not be saved.',
        });
        if (!res) return;
        ui.toast('Backup retention updated.', 'success');
        if (settingsData) settingsData.backupRetention = retention;
    });
}

async function downloadBackup(name) {
    const ok = await ui.confirm({
        title: 'Download backup',
        message: `Download "${name}"? It contains a full copy of your database.`,
        confirmLabel: 'Download',
    });
    if (!ok) return;
    location.href = '/api/_admin/backups/' + encodeURIComponent(name);
}

async function deleteBackup(name) {
    const ok = await ui.confirm({
        title: 'Delete backup',
        message: `Delete the backup "${name}"? There is no way back.`,
        confirmLabel: 'Delete',
        danger: true,
    });
    if (!ok) return;
    const res = await ui.send(`/api/_admin/backups/${encodeURIComponent(name)}`, {
        method: 'DELETE',
        failure: 'The backup could not be deleted.',
    });
    if (!res) return;
    ui.toast('Backup deleted.', 'success');
    loadBackups();
}

function deleteCurrentTable() {
    if (!currentTablePublicId) return;
    const table = currentTables.find((t) => t.id === currentTablePublicId);
    openModal({
        title: 'Delete table',
        message: `Are you sure you want to delete the table "${table ? table.name : ''}"? This will irreversibly delete all the data contained. There is no way back.`,
        confirmLabel: 'Delete',
        cancelLabel: 'Cancel',
        danger: true,
        onConfirm: async () => {
            const res = await fetch(`/api/_admin/tables/${currentTablePublicId}`, {
                method: 'DELETE'
            });
            if (!res.ok) {
                ui.toast('The table could not be deleted.', 'error');
                return;
            }
            currentTablePublicId = null;
            await loadTables();
            await navigate('/tables');
        },
    });
}


function renderAllowedOrigins(stored) {
    const list = document.getElementById('allowedOriginList');
    const empty = document.getElementById('allowedOriginsEmpty');
    if (!list) return;
    const origins = (stored || '').split('\n').map((o) => o.trim()).filter(Boolean);
    list.replaceChildren(
        ...origins.map((origin) => {
            const li = document.createElement('li');
            li.textContent = origin;
            return li;
        }),
    );
    empty.classList.toggle('hidden', origins.length > 0);
}

async function saveAllowedOrigins(btn) {
    await ui.busy(btn, async () => {
        const saved = await ui.send('/api/_admin/settings', {
            method: 'PUT',
            body: {
                allowedOrigins: document.getElementById('settingsAllowedOrigins').value
            },
            ok: 'Allowed sites saved.',
        });
        if (!saved) return;
        // Re-read instead of echo the textarea: the server normalises what was typed, and an author should see what is actually in force.
        document.getElementById('settingsAllowedOrigins').value = saved.allowedOrigins || '';
        renderAllowedOrigins(saved.allowedOrigins || '');
    });
}


/* Single sign-on: OpenID Connect providers */

let oidcData = [];

async function loadOidcProviders() {
    oidcData = await fetch('/api/_admin/oidc-providers').then((r) => r.json());
    const body = document.getElementById('oidcBody');
    if (!body) return;
    body.innerHTML = '';
    document.getElementById('oidcEmpty').classList.toggle('hidden', oidcData.length > 0);

    oidcData.forEach((p) => {
        const tr = document.createElement('tr');
        tr.className = 'row-link';
        tr.onclick = (ev) => {
            if (ev.target.closest('button, label')) return;
            openOidcSheet(p.id);
        };

        const name = document.createElement('td');
        name.textContent = p.name;
        tr.append(name);

        const authority = document.createElement('td');
        authority.className = 'muted';
        authority.textContent = p.authority;
        tr.append(authority);

        // Which sign-in screens offer it; a provider enabled for neither is configured but unreachable.
        const doors = document.createElement('td');
        doors.className = 'muted';
        const offered = [p.consoleEnabled && 'Console', p.publicEnabled && 'End users'].filter(Boolean);
        // Switched off, the surfaces are remembered but nothing is offered; saying "Nowhere" would read as a misconfiguration instead of a parked provider.
        doors.textContent = !p.isEnabled ? 'Off' : offered.join(', ');
        tr.append(doors);

        const enabledTd = document.createElement('td');
        const toggle = switchHtml(`oidcEnabled-${p.id}`, p.isEnabled);
        toggle.querySelector('input').addEventListener('change', (ev) =>
            saveOidcProvider(p.id, { isEnabled: ev.target.checked }));
        enabledTd.append(toggle);
        tr.append(enabledTd);

        const actions = document.createElement('td');
        actions.className = 'cell-actions end';
        // Offered only where it can work: an account already linked has to be unlinked first, and a provider not offered on the console cannot complete a console round trip.
        if (p.isEnabled && p.consoleEnabled && !currentAccount?.linked)
            actions.append(ui.button('Link my account', () => linkMyAccount(p), {
                size: 'btn-sm',
                variant: 'btn-outline'
            }));
        actions.append(ui.button('Configure', () => openOidcSheet(p.id), {
            size: 'btn-sm',
            variant: 'btn-outline'
        }));
        tr.append(actions);

        body.append(tr);
    });
}

// Binds this provider's identity to the account already signed in here. The account is fixed by the session before the redirect, nothing the provider sends chooses who gets linked.
async function linkMyAccount(p) {
    const password = await ui.ask({
        title: `Link my account to ${p.name}`,
        label: 'Your current password',
        type: 'password',
        confirmLabel: 'Continue',
        help: `You will sign in at ${p.name} once. The identity it returns is bound to the account you are signed in as here.`,
    });
    if (!password) return;

    const started = await ui.send(`/api/auth/oidc/${p.slug}/link`, {
        method: 'POST',
        body: {
            currentPassword: password
        },
    });
    if (started && started.authorizeUrl) location.href = started.authorizeUrl;
}

async function saveOidcProvider(id, body) {
    const saved = await ui.send(`/api/_admin/oidc-providers/${id}`, {
        method: 'PATCH',
        body,
        success: 'Provider saved.',
        failure: 'Could not save the provider.',
    });
    if (saved) await loadOidcProviders();
    return saved;
}

function openOidcSheet(id) {
    const p = id ? oidcData.find((x) => x.id === id) : null;
    const body = document.createElement('div');

    const name = ui.field('Name', {
        id: 'oidcName',
        value: p ? p.name : '',
        placeholder: 'Authelia',
        help: 'What the button on the sign-in screen says.',
    });
    const slug = ui.field('Key', {
        id: 'oidcSlug',
        value: p ? p.slug : '',
        placeholder: 'authelia',
        help: 'Appears in the callback URL below. Lowercase letters, digits and hyphens.',
    });
    // Shaped as it is typed instead of policed on save, the way an API name is.
    slug.ctrl.addEventListener('input', () => {
        slug.ctrl.value = slug.ctrl.value.toLowerCase().replace(/[^a-z0-9-]+/g, '-');
        redirect.ctrl.value = callbackFor(slug.ctrl.value);
    });

    const authority = ui.field('Issuer URL', {
        id: 'oidcAuthority',
        value: p ? p.authority : '',
        placeholder: 'https://auth.example.com',
        help: 'The discovery document is read from this address on save; a wrong URL is refused there instead of at sign-in.',
    });

    const redirect = ui.field('Redirect URL', {
        id: 'oidcRedirect',
        value: p ? p.redirectUri : callbackFor(''),
        mono: true,
        help: 'Register this exact address at the provider.',
    });
    redirect.ctrl.readOnly = true;

    const clientId = ui.field('Client ID', {
        id: 'oidcClientId',
        value: p ? p.clientId : ''
    });
    const clientSecret = ui.field('Client secret', {
        id: 'oidcClientSecret',
        type: 'password',
        value: '',
        placeholder: p && p.hasClientSecret ? 'Set. Type to replace it.' : 'Leave empty for a public client.'
    });

    const scopes = ui.field('Scopes', {
        id: 'oidcScopes',
        value: p ? p.scopes : 'openid profile email',
        help: 'Space separated. Must include openid.',
    });
    const usernameClaim = ui.field('Username claim', {
        id: 'oidcUsernameClaim',
        value: p ? p.usernameClaim : 'preferred_username'
    });
    const emailClaim = ui.field('Email claim', {
        id: 'oidcEmailClaim',
        value: p ? p.emailClaim : 'email'
    });

    // A new provider is on and offered on the console: that is why an operator is
    // adding one, and the server refuses an enabled provider offered nowhere anyway.
    const enabled = ui.switchRow('Enabled', {
        id: 'oidcIsEnabled',
        checked: p ? p.isEnabled : true
    });
    const console_ = ui.switchRow('Offer on the console sign-in', {
        id: 'oidcConsoleEnabled',
        checked: p ? p.consoleEnabled : true
    });
    const publicSurface = ui.switchRow('Offer on the end-user sign-in', {
        id: 'oidcPublicEnabled',
        checked: p ? p.publicEnabled : false
    });
    const createAccounts = ui.switchRow('Create accounts on first sign-in', {
        id: 'oidcCreateAccounts',
        checked: p ? p.createAccounts : false
    });

    body.append(name, slug, authority, redirect, clientId, clientSecret, scopes, usernameClaim, emailClaim,
        enabled, console_, publicSurface, createAccounts);

    const payload = () => ({
        name: name.ctrl.value.trim(),
        slug: slug.ctrl.value.trim(),
        authority: authority.ctrl.value.trim(),
        clientId: clientId.ctrl.value.trim(),
        // An untouched field leaves the stored secret alone; the server only reads the key when it is sent.
        ...(clientSecret.ctrl.value ? { clientSecret: clientSecret.ctrl.value } : {}),
        scopes: scopes.ctrl.value.trim(),
        usernameClaim: usernameClaim.ctrl.value.trim(),
        emailClaim: emailClaim.ctrl.value.trim(),
        isEnabled: enabled.ctrl.checked,
        consoleEnabled: console_.ctrl.checked,
        publicEnabled: publicSurface.ctrl.checked,
        createAccounts: createAccounts.ctrl.checked,
    });

    const actions = ui.el('div', 'form-actions');
    // Deletion sits with the provider it removes, and never next to the button that saves.
    if (p) {
        actions.append(ui.button('Delete', async () => {
            const ok = await ui.confirm({
                title: 'Delete provider',
                message: `Remove ${p.name}? Accounts linked to it keep their history and fall back to their password.`,
                confirmLabel: 'Delete',
                danger: true,
            });
            if (!ok) return;
            const done = await ui.send(`/api/_admin/oidc-providers/${p.id}`, {
                method: 'DELETE',
                success: 'Provider deleted.',
                failure: 'Could not delete the provider.',
            });
            if (!done) return;
            ui.closeSheet();
            await loadOidcProviders();
        }, {
            variant: 'btn-danger'
        }), ui.el('div', 'form-actions-spacer'));
    }
    actions.append(ui.button('Cancel', ui.closeSheet, {
        variant: 'btn-outline'
    }));
    const saveBtn = ui.button(p ? 'Save' : 'Add provider', () =>
        ui.busy(saveBtn, async () => {
            const saved = p
                ? await saveOidcProvider(p.id, payload())
                : await ui.send('/api/_admin/oidc-providers', {
                    method: 'POST',
                    body: payload(),
                    success: 'Provider added.',
                    failure: 'Could not add the provider.',
                });
            if (!saved) return;
            ui.closeSheet();
            await loadOidcProviders();
        }));
    actions.append(saveBtn);

    ui.sheet(p ? p.name : 'Add provider', body, actions);
}

function callbackFor(slug) {
    const origin = (settingsData && settingsData.siteUrl || '').trim().replace(/\/+$/, '') || location.origin;
    return `${origin}/api/auth/oidc/${slug || '<key>'}/callback`;
}
