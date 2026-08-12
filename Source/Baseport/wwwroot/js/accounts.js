/* user accounts, API tokens and access: a filterable, paginated table */

let accountsData = [];
let accountsPage = 1;
let accountsPerPage = 10;

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
  per.innerHTML = '<label>Rows per page</label><select>';
  const sel = per.querySelector('select');
  [10, 25, 50, 100].forEach((n) => {
    const o = document.createElement('option');
    o.value = n;
    o.textContent = n;
    if (n === perPage) o.selected = true;
    sel.appendChild(o);
  });
  sel.onchange = () => onPerPage(Number(sel.value));
  right.appendChild(per);
  container.appendChild(left);
  container.appendChild(right);
}

async function loadAccounts() {
  accountsData = await fetch('/api/_admin/accounts').then((r) => r.json());
  renderAccounts();
}

async function renderAccounts() {
  // Rows come rendered; accountsData still backs the edit sheet.
  const term = (document.getElementById('accountsFilter').value || '').trim();
  const meta = await ui.fragment(
    'accountsBody',
    `/api/_admin/fragments/accounts?page=${accountsPage}&pageSize=${accountsPerPage}` +
      (term ? `&q=${encodeURIComponent(term)}` : ''),
  );
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

function openAccountForm(pid) {
  const a = pid ? accountsData.find((x) => x.id === pid) : null;
  const body = document.createElement('div');
  body.appendChild(fieldInputRow('Username', 'accUsername', a ? a.username : '', 'e.g. jane', false, 'username'));
  body.appendChild(fieldInputRow('Email', 'accEmail', a ? a.email || '' : '', 'e.g. jane@example.com', false, 'email'));
  body.appendChild(
    ui.field('Role', {
      id: 'accRole',
      type: 'select',
      value: a ? a.role : 'consumer',
      options: [
        ['admin', 'Admin (signs in to the console)'],
        ['consumer', 'Consumer (API token only)'],
      ],
      help: 'If in doubt, choose Consumer.',
    }),
  );

  if (a) {
    const disabledLab = document.createElement('label');
    disabledLab.className = 'field-label';
    disabledLab.style.cssText = 'display:flex;align-items:center;gap:.5rem';
    disabledLab.innerHTML = `<input type="checkbox" id="accDisabled"${a.isDisabled ? ' checked' : ''}> Disabled`;
    body.appendChild(disabledLab);
    body.appendChild(apiTokenPanel(a));
  }

  const actions = document.createElement('div');
  actions.className = 'form-actions';
  // Deletion sits with the account it removes, not in the row list where it is one mis-click from edit.
  if (a) actions.appendChild(ui.button('Delete', () => deleteAccount(a.id, a.username), { variant: 'btn-danger' }));
  actions.appendChild(ui.button('Cancel', closeSheet, { variant: 'btn-outline' }));
  const saveBtn = ui.button(pid ? 'Save' : 'Create user', () => ui.busy(saveBtn, () => submitAccount(pid)));
  actions.appendChild(saveBtn);

  openSheet(a ? `Edit ${a.username}` : 'New user', body, actions);
  setTimeout(() => document.getElementById('accUsername').focus(), 50);
}

// Token lifecycle for one account: generate with an expiry, or revoke.
function apiTokenPanel(a) {
  const wrap = ui.el('div', 'token-panel');
  wrap.append(ui.el('h4', null, { textContent: 'REST API access' }));

  const state = ui.el('p', 'muted');
  state.textContent = !a.hasApiToken
    ? 'No token. This account cannot use the REST API.'
    : a.apiTokenExpired
      ? 'This account has a token, but it has expired.'
      : a.apiTokenExpiresAt
        ? `Token active until ${new Date(a.apiTokenExpiresAt).toLocaleDateString()}.`
        : 'This account has a token with no expiry date. Regenerate it to set one.';
  wrap.append(state);

  // A date, not a day count: the operator knows when access should end.
  const expiry = ui.field('Expires on', {
    id: 'accTokenExpiry',
    type: 'date',
    value: new Date(Date.now() + 90 * 86400000).toISOString().slice(0, 10),
    help: 'Defaults to 90 days from today. Maximum ten years.',
  });
  expiry.control.min = new Date(Date.now() + 86400000).toISOString().slice(0, 10);
  expiry.control.max = new Date(Date.now() + 3650 * 86400000).toISOString().slice(0, 10);
  wrap.append(expiry);

  const row = ui.el('div', 'row');
  const genBtn = ui.button(a.hasApiToken ? 'Regenerate token' : 'Generate token', () =>
    ui.busy(genBtn, async () => {
      const created = await ui.send(`/api/_admin/accounts/${a.id}/token`, {
        method: 'POST',
        body: { expiresAt: expiry.control.value },
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
          if (
            !(await ui.confirm({
              title: 'Revoke API token',
              message: `Revoke ${a.username}'s token? Any integration using it stops working immediately.`,
              confirmLabel: 'Revoke',
              danger: true,
            }))
          )
            return;
          if (!(await ui.send(`/api/_admin/accounts/${a.id}/token`, { method: 'DELETE', success: 'Token revoked.' })))
            return;
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

// Shown once, because the server never returns the token again.
function showGeneratedToken(token, expiresAt) {
  const body = ui.el('div');
  body.append(
    ui.el('p', 'muted', {
      textContent: `Copy this now. It is not shown again. Valid until ${new Date(expiresAt).toLocaleDateString()}.`,
    }),
  );
  const box = ui.el('input', 'input embed-input mono', { value: token, readOnly: true });
  box.onclick = () => box.select();
  body.append(box);
  ui.sheet('API token', body, ui.button('Done', ui.closeSheet));
}

async function submitAccount(pid) {
  const body = {
    username: document.getElementById('accUsername').value.trim(),
    email: document.getElementById('accEmail').value.trim(),
    role: document.getElementById('accRole').value,
  };
  const disabled = document.getElementById('accDisabled');
  if (disabled) body.isDisabled = disabled.checked;

  // Every rule here is enforced by the API; this call just reports what it says.
  const saved = await ui.send(pid ? `/api/_admin/accounts/${pid}` : '/api/_admin/accounts', {
    method: pid ? 'PATCH' : 'POST',
    body,
    success: pid ? 'Account saved.' : 'Account created.',
    failure: 'Failed to save account.',
  });
  if (!saved) return;
  closeSheet();
  loadAccounts();
}

async function deleteAccount(pid, username) {
  const confirmed = await ui.confirm({
    title: 'Delete account',
    message: `Are you sure you want to delete the account "${username}"? This will immediately end any active sessions and associated API tokens. This action cannot be undone.`,
    confirmLabel: 'Delete',
    danger: true,
  });
  if (!confirmed) return;
  // The server refuses to remove the last enabled account; its message is what the toast shows.
  if (!(await ui.send(`/api/_admin/accounts/${pid}`, { method: 'DELETE', success: 'Account deleted.' }))) return;
  closeSheet();
  loadAccounts();
}
