/* importing a table from a remote OpenAPI document */

function openProxySheet() {
    closeCreateMenu();
    const wrap = document.createElement('div');
    const status = document.createElement('p');
    status.id = 'pxStatus';
    status.className = 'muted';

    const specRow = document.createElement('div');
    specRow.style.display = 'flex';
    specRow.style.gap = '.375rem';
    const specInput = document.createElement('input');
    specInput.className = 'input';
    specInput.id = 'pxSpecUrl';
    specInput.placeholder = 'https://example.com/api/openapi/v1/openapi.json';
    specInput.style.flex = '1';
    const fetchBtn = document.createElement('button');
    fetchBtn.type = 'button';
    fetchBtn.className = 'btn btn-outline btn-sm';
    fetchBtn.innerText = 'Fetch';
    fetchBtn.onclick = () => ui.busy(fetchBtn, () => fetchProxyOperations());
    specRow.appendChild(specInput);
    specRow.appendChild(fetchBtn);
    const specLab = document.createElement('label');
    specLab.className = 'field-label';
    specLab.innerText = 'OpenAPI 3.x spec URL';
    specLab.appendChild(specRow);
    wrap.appendChild(specLab);

    const opLab = document.createElement('label');
    opLab.className = 'field-label';
    opLab.innerText = 'Target operation (POST/PUT forwards the form)';
    const opSel = document.createElement('select');
    opSel.className = 'input';
    opSel.id = 'pxOperation';
    opSel.onchange = () => renderPathParams();
    opLab.appendChild(opSel);
    wrap.appendChild(opLab);

    const ppDiv = document.createElement('div');
    ppDiv.id = 'pxPathParams';
    wrap.appendChild(ppDiv);

    wrap.appendChild(fieldInputRow('Table name', 'pxName', '', 'e.g. Remote Products'));
    wrap.appendChild(
        fieldInputRow('Bearer token (optional)', 'pxToken', '', 'Paste the remote API token, stored on this table only'),
    );
    wrap.appendChild(status);

    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'btn btn-outline';
    cancelBtn.innerText = 'Cancel';
    cancelBtn.onclick = () => ui.closeSheet();

    const createBtn = document.createElement('button');
    createBtn.className = 'btn';
    createBtn.innerText = 'Create proxy table';
    createBtn.onclick = () => ui.busy(createBtn, () => createProxy());
    const actions = document.createElement('div');
    actions.className = 'form-actions';
    actions.appendChild(cancelBtn);
    actions.appendChild(createBtn);

    openSheet('Create proxy table', wrap, actions);
    specInput.focus();
    specInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') fetchProxyOperations();
    });
}

async function fetchProxyOperations() {
    const specUrl = document.getElementById('pxSpecUrl').value.trim();
    const status = document.getElementById('pxStatus');
    if (!specUrl) {
        ui.toast('Enter a spec URL first.', 'error');
        return;
    }
    status.className = 'muted';
    status.innerText = 'Fetching…';
    const res = await fetch('/api/_admin/proxy/operations', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            specUrl
        }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
        ui.toast((data.errors || ['Failed to fetch spec.']).join(' '), 'error');
        return;
    }
    proxyOps = data.operations || [];
    const sel = document.getElementById('pxOperation');
    sel.innerHTML = '';
    (proxyOps || []).forEach((op) => {
        const option = document.createElement('option');
        option.value = op.path + '|' + op.method;
        option.innerText = `${op.method} ${op.path}${op.summary ? ', ' + op.summary : ''}`;
        sel.appendChild(option);
    });
    if (!proxyOps.length) {
        ui.toast('No importable operations found in the spec.', 'error');
        return;
    }
    const first = proxyOps[0];
    if (!document.getElementById('pxName').value.trim()) {
        document.getElementById('pxName').value = first.summary || first.path.split('/').filter(Boolean).pop() || 'Proxied';
    }
    renderPathParams();
    status.className = 'muted';
    status.innerText = `Loaded ${proxyOps.length} operations.`;
}

function renderPathParams() {
    const sel = document.getElementById('pxOperation');
    const [path, method] = (sel.value || '|').split('|');
    const op = proxyOps.find((o) => o.path === path && o.method === method);
    const div = document.getElementById('pxPathParams');
    div.innerHTML = '';
    if (!op || !(op.pathParams || []).length) return;
    const title = document.createElement('p');
    title.className = 'sheet-note';
    title.innerText = 'Path parameters';
    div.appendChild(title);
    (op.pathParams || []).forEach((pp) => {
        const lab = document.createElement('label');
        lab.className = 'field-label';
        lab.innerText = pp.name + (pp.required ? ' (required)' : '');
        const inp = document.createElement('input');
        inp.className = 'input';
        inp.id = 'pp_' + pp.name;
        inp.value = pp.enumValue || pp.default || '';
        lab.appendChild(inp);
        div.appendChild(lab);
    });
}

async function createProxy() {
    const status = document.getElementById('pxStatus');
    const specUrl = document.getElementById('pxSpecUrl').value.trim();
    const name = document.getElementById('pxName').value.trim();
    const token = document.getElementById('pxToken').value.trim();
    const [path, method] = (document.getElementById('pxOperation').value || '|').split('|');
    const op = proxyOps.find((o) => o.path === path && o.method === method);
    const pathParams = {};
    (op ? op.pathParams || [] : []).forEach((pp) => {
        const inp = document.getElementById('pp_' + pp.name);
        if (inp) pathParams[pp.name] = inp.value.trim();
    });
    if (!specUrl || !path) {
        ui.toast('Fetch a spec and pick an operation first.', 'error');
        return;
    }
    if (!name) {
        ui.toast('Table name is required.', 'error');
        return;
    }
    status.className = 'muted';
    status.innerText = 'Creating…';
    const res = await fetch('/api/_admin/proxy/create', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            name,
            specUrl,
            path,
            method,
            token,
            pathParams
        }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
        ui.toast((data.errors || ['Failed to create proxy table.']).join(' '), 'error');
        return;
    }
    closeSheet();
    await loadTables();
    selectTable(data);
}