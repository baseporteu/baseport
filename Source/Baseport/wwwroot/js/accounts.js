/* user accounts, API tokens and access: a filterable, paginated table */

// Global pagination and cached account state for form references.
let accountsData = [];
let accountsPage = 1;
let accountsPerPage = 10;

// Builds pagination controls and row-count selectors for tabular views.
function makePager(container, opts) {
    container.innerHTML = '';
    if (!opts.total) return;

    const { page, total, perPage, onPage, onPerPage } = opts;
    const pages = Math.max(1, Math.ceil(total / perPage));

    const mkBtn = (label, title, target, disabled) => {
        const b = document.createElement('button');
        b.className = 'pager-btn';
        b.title = title;
        b.disabled = disabled;
        b.textContent = label;
        b.onclick = () => onPage(target);
        return b;
    };

    const left = document.createElement('div');
    left.className = 'pager-controls';
    left.appendChild(mkBtn('«', 'First page', 1, page <= 1));
    left.appendChild(mkBtn('‹', 'Previous page', page - 1, page <= 1));

    const counter = document.createElement('span');
    counter.textContent = `${page} / ${pages}`;
    left.appendChild(counter);

    left.appendChild(mkBtn('›', 'Next page', page + 1, page >= pages));
    left.appendChild(mkBtn('»', 'Last page', pages, page >= pages));

    const right = document.createElement('div');
    right.className = 'pager-controls';

    const rows = document.createElement('span');
    rows.textContent = `${total} row${total === 1 ? '' : 's'}`;
    right.appendChild(rows);

    const per = document.createElement('div');
    per.className = 'per-page';
    per.innerHTML = '<label>Rows per page</label><select></select>';

    const sel = per.querySelector('select');
    [10, 25, 50, 100].forEach((n) => {
        const o = document.createElement('option');
        o.value = String(n);
        o.textContent = String(n);
        o.selected = n === perPage;
        sel.appendChild(o);
    });
    sel.onchange = () => onPerPage(Number(sel.value));

    right.appendChild(per);
    container.appendChild(left);
    container.appendChild(right);
}

// Fetches raw account dataset to back edit sheets and triggers page render.
async function loadAccounts() {
    accountsData = await fetch('/api/_admin/accounts').then((r) => r.json());
    renderAccounts();
}

// Fetches rendered HTML table fragment and updates pagination state.
async function renderAccounts() {
    const term = (document.getElementById('accountsFilter').value || '').trim();
    const query = new URLSearchParams({
        page: String(accountsPage),
        pageSize: String(accountsPerPage),
        ...(term ? { q: term } : {}),
    });

    const meta = await ui.fragment('accountsBody', `/api/_admin/fragments/accounts?${query}`);
    if (!meta) return;

    document.getElementById('accountsEmpty').classList.toggle('hidden', meta.total > 0);

    makePager(document.getElementById('accountsPager'), {
        page: meta.page,
        total: meta.total,
        perPage: meta.pageSize,
        onPage: (p) => {
            accountsPage = p;
            renderAccounts();
        },
        onPerPage: (n) => {
            accountsPerPage = n;
            accountsPage = 1;
            renderAccounts();
        },
    });
}

// Constructs and presents the account creation or edit form sheet.
function openAccountForm(pid) {
    const a = pid ? accountsData.find((x) => x.id === pid) : null;
    const locked = !!a && a.role === 'admin';

    const body = document.createElement('div');
    body.appendChild(fieldInputRow('Username', 'accUsername', a ? a.username : '', 'e.g. jane', false, 'username'));
    body.appendChild(fieldInputRow('Email', 'accEmail', a ? a.email || '' : '', 'e.g. jane@example.com', false, 'email'));

    // Existing accounts cannot be promoted to admin via the UI.
    body.appendChild(
        ui.field('Role', {
            id: 'accRole',
            type: 'select',
            value: a ? a.role : 'consumer',
            options: a
                ? (a.role === 'admin'
                      ? [
                            ['admin', 'Admin (signs in to console)'],
                            ['consumer', 'Consumer (API token only)'],
                            ['user', 'User (public API only)'],
                        ]
                      : [
                            ['consumer', 'Consumer (API token only)'],
                            ['user', 'User (public API only)'],
                        ])
                : [
                      ['admin', 'Admin (signs in to console)'],
                      ['consumer', 'Consumer (API token only)'],
                  ],
            help: a ? 'Promote via CLI: baseport accounts promote <username>' : 'If in doubt, select Consumer.',
        }),
    );

    if (a) {
        body.appendChild(
            ui.field('Set password', {
                id: 'accPassword',
                type: 'password',
                value: '',
                placeholder: 'Leave blank to keep current password',
                help: 'One-time password. Requires change at next sign-in and ends existing sessions.',
            }),
        );

        body.appendChild(ui.switchRow('Disabled', {
            id: 'accDisabled',
            checked: a.isDisabled,
            disabled: locked,
        }));

        // Token operations remain accessible for admin accounts.
        body.appendChild(apiTokenPanel(a));
    }

    if (locked) {
        // Disables fields that the API locks for admin accounts.
        ['accRole', 'accPassword', 'accDisabled']
            .forEach((id) => {
                const input = body.querySelector(`#${id}`);
                if (input) input.disabled = true;
            });
        body.appendChild(adminNotice(a));
    }

    const actions = document.createElement('div');
    actions.className = 'form-actions';

    if (a && !locked) {
        actions.appendChild(ui.button('Delete', () => deleteAccount(a.id, a.username), {
            variant: 'btn-danger',
        }));
    }
    actions.appendChild(ui.button('Cancel', closeSheet, { variant: 'btn-outline' }));

    const saveBtn = ui.button(pid ? 'Save' : 'Create user', () => ui.busy(saveBtn, () => submitAccount(pid)));
    actions.appendChild(saveBtn);

    openSheet(a ? `Edit ${a.username}` : 'New user', body, actions);
    setTimeout(() => document.getElementById('accUsername')?.focus(), 50);
}

// 10 to 12 characters, because AccountValidation.PasswordMin is 10 and the command would refuse anything shorter.
function randomPassword() {
    const alphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
    const bytes = crypto.getRandomValues(new Uint8Array(12));
    return [...bytes].map((b) => alphabet[b % alphabet.length]).join('').slice(0, 10 + (bytes[0] % 3));
}

// Explains restricted fields and directs privileged changes to the CLI.
function adminNotice(a) {
    const wrap = ui.el('div', 'token-panel');
    
    wrap.append(ui.el('p', 'muted', {
        textContent: 'Password, role, and status changes are disabled for admin accounts to prevent console takeovers. Use the shell commands below. Name, address, and API token remain editable here.',
    }));

    // Pre-populates runnable commands with the targeted username.
    const commands = ui.el('pre', 'code-block');
    commands.textContent = [
        `baseport accounts password ${a.username} ${randomPassword()}`,
        `baseport accounts demote ${a.username}`,
    ].join('\n');

    wrap.append(ui.copyable(commands, () => commands.textContent));
    return wrap;
}

// Manages API token lifecycle including generation, expiration, and revocation.
function apiTokenPanel(a) {
    const wrap = ui.el('div', 'token-panel');
    wrap.append(ui.el('h4', null, { textContent: 'REST API access' }));

    const state = ui.el('p', 'muted');
    state.textContent = !a.hasApiToken
        ? 'No token active. REST API access is disabled.'
        : a.apiTokenExpired
        ? 'Token is expired. Generate a new token to restore access.'
        : a.apiTokenExpiresAt
        ? `Token active until ${ui.when(a.apiTokenExpiresAt)}.`
        : 'Token has no expiration date. Regenerate to set one.';
    wrap.append(state);

    // Date picker defaults to 90 days with a 1-day minimum and 10-year maximum.
    const expiry = ui.field('Expires on', {
        id: 'accTokenExpiry',
        type: 'date',
        value: new Date(Date.now() + 90 * 86400000).toISOString().slice(0, 10),
        help: 'Defaults to 90 days from today (10 year maximum).',
    });
    expiry.ctrl.min = new Date(Date.now() + 86400000).toISOString().slice(0, 10);
    expiry.ctrl.max = new Date(Date.now() + 3650 * 86400000).toISOString().slice(0, 10);
    wrap.append(expiry);

    const row = ui.el('div', 'row');
    const genBtn = ui.button(a.hasApiToken ? 'Regenerate token' : 'Generate token', () =>
        ui.busy(genBtn, async () => {
            const created = await ui.send(`/api/_admin/accounts/${a.id}/token`, {
                method: 'POST',
                body: { expiresAt: expiry.ctrl.value },
                failure: 'Could not generate a token.',
            });
            if (!created) return;
            showGeneratedToken(created.apiToken, created.expiresAt);
            await loadAccounts();
        }),
    );
    row.append(genBtn);

    if (a.hasApiToken) {
        row.append(
            ui.button(
                'Revoke',
                async () => {
                    const confirmed = await ui.confirm({
                        title: 'Revoke API token',
                        message: `Revoke ${a.username}'s token? Integrations using it will stop working immediately.`,
                        confirmLabel: 'Revoke',
                        danger: true,
                    });
                    if (!confirmed) return;

                    const deleted = await ui.send(`/api/_admin/accounts/${a.id}/token`, {
                        method: 'DELETE',
                        success: 'Token revoked.',
                    });
                    if (!deleted) return;

                    closeSheet();
                    await loadAccounts();
                },
                { variant: 'btn-outline' },
            ),
        );
    }

    wrap.append(row);
    return wrap;
}

// Displays the secret token once; it cannot be retrieved from the server later.
function showGeneratedToken(token, expiresAt) {
    const body = ui.el('div');
    body.append(
        ui.el('p', 'muted', {
            textContent: `Copy this token now. It will not be shown again. Valid until ${ui.when(expiresAt)}.`,
        }),
    );

    const box = ui.el('input', 'input embed-input mono', {
        value: token,
        readOnly: true,
    });
    box.onclick = () => box.select();
    body.append(box);

    ui.sheet('API token', body, ui.button('Done', ui.closeSheet));
}

// Prepares account payload, omitting fields that admins are restricted from updating via UI.
async function submitAccount(pid) {
    const body = {
        username: document.getElementById('accUsername').value.trim(),
        email: document.getElementById('accEmail').value.trim(),
    };

    // Prevents sending restricted field mutations when targeting an admin account.
    const locked = pid && accountsData.find((x) => x.id === pid)?.role === 'admin';
    if (!locked) {
        body.role = document.getElementById('accRole').value;
        const disabled = document.getElementById('accDisabled');
        if (disabled) body.isDisabled = disabled.checked;
        const password = document.getElementById('accPassword');
        if (password?.value) body.password = password.value;
    }

    // Sends payload to the REST API and reports backend validation results.
    const saved = await ui.send(pid ? `/api/_admin/accounts/${pid}` : '/api/_admin/accounts', {
        method: pid ? 'PATCH' : 'POST',
        body,
        success: pid ? 'Account saved.' : 'Account created.',
        failure: 'Failed to save account.',
    });
    if (!saved) return;

    closeSheet();
    await loadAccounts();
}

// Requests account deletion and displays server-side validation feedback.
async function deleteAccount(pid, username) {
    const confirmed = await ui.confirm({
        title: 'Delete account',
        message: `Delete "${username}"? Active sessions and API tokens will terminate immediately. This cannot be undone.`,
        confirmLabel: 'Delete',
        danger: true,
    });
    if (!confirmed) return;

    // Server rejects deletion if this is the last enabled account.
    const deleted = await ui.send(`/api/_admin/accounts/${pid}`, {
        method: 'DELETE',
        success: 'Account deleted.',
    });
    if (!deleted) return;

    closeSheet();
    await loadAccounts();
}