// Actions, rail item "Actions". An action watches one table's own record events (create/update/delete) and
// runs an ordered list of steps against the record: runExpression (a JsExpr, no stored effect - a guard or
// something to see land in a run's log) or updateRecord (field -> JsExpr expression, evaluated against the
// record's current state and written back). Server-side: ActionEndpoints.cs, ActionRunner.cs, JobScheduler's
// 30s tick. This view is the only way to author one without hand-writing StepsJson.
let actionsAll = [];
let actionEditingId = null;
let actionSteps = []; // [{ type: 'runExpression', expr }] | [{ type: 'updateRecord', setJson: { Field: expr, ... } }]
let actionTableFieldsList = [];

async function loadActions() {
    actionsAll = await fetch('/api/_admin/actions').then((r) => r.json());
    renderActionsList();
    refreshSidebar('actions');
}

function actionTableName(tableId) {
    const t = currentTables.find((x) => x.id === tableId);
    return t ? t.name : tableId;
}

const TRIGGER_LABELS = {
    onCreate: 'On create',
    onUpdate: 'On update',
    onDelete: 'On delete',
};

function renderActionsList() {
    const rows = document.getElementById('actionsRows');
    rows.innerHTML = actionsAll
        .map((a) => {
            const status = a.isEnabled ?
                '<span class="badge badge-ok">Enabled</span>' :
                '<span class="badge">Disabled</span>';
            return `<tr>
                <td><a href="#" onclick='navigate("/actions/${a.id}"); return false;'>${escapeHtml(a.name || 'Untitled action')}</a></td>
                <td>${escapeHtml(actionTableName(a.tableId))}</td>
                <td>${TRIGGER_LABELS[a.triggerKind] || a.triggerKind}</td>
                <td>${status}</td>
                <td><button class="btn btn-outline btn-sm btn-danger" onclick='deleteAction("${a.id}")'>Delete</button></td>
            </tr>`;
        })
        .join('');
    document.getElementById('actionsEmpty').classList.toggle('hidden', actionsAll.length > 0);
}

function deleteAction(id) {
    const a = actionsAll.find((x) => x.id === id);
    openModal({
        title: 'Delete action',
        message: `Are you sure you want to delete the action "${a ? a.name : id}"? Its run history goes with it.`,
        confirmLabel: 'Delete',
        danger: true,
        onConfirm: async () => {
            const res = await fetch(`/api/_admin/actions/${id}`, {
                method: 'DELETE'
            });
            if (!(await ui.handle(res, {
                    success: 'Action deleted.',
                    failure: 'The action could not be deleted.'
                }))) return;
            if (actionEditingId === id) return navigate('/actions');
            await loadActions();
        },
    });
}

function deleteCurrentAction() {
    if (actionEditingId) deleteAction(actionEditingId);
}

/* editor shell */

function newAction() {
    actionEditingId = null;
    document.getElementById('actionDeleteBtn').classList.add('hidden');
    document.getElementById('actionRunsPanel').classList.add('hidden');
    document.getElementById('actionEditorTitle').innerText = 'New action';
    document.getElementById('actionName').value = '';
    document.getElementById('actionTrigger').value = 'onCreate';
    document.getElementById('actionEnabled').checked = true;
    document.getElementById('saveActionBtn').innerText = 'Save action';
    document.getElementById('actionTable').disabled = false;
    fillActionTableSelect();
    onActionTableChange();
    actionSteps = [];
    renderActionSteps();
    document.getElementById('actionEditor').classList.remove('hidden');
    document.getElementById('actionEditor').scrollIntoView({
        behavior: 'smooth'
    });
}

async function editAction(id) {
    const a = await fetch(`/api/_admin/actions/${id}`)
        .then((r) => r.json())
        .catch(() => null);
    if (!a || !a.id) return navigate('/actions', {
        replace: true
    });

    actionEditingId = a.id;
    document.getElementById('actionDeleteBtn').classList.remove('hidden');
    refreshSidebar('actions');
    document.getElementById('actionEditorTitle').innerText = 'Edit action';
    document.getElementById('actionName').value = a.name || '';
    document.getElementById('actionTrigger').value = a.triggerKind;
    document.getElementById('actionEnabled').checked = !!a.isEnabled;
    document.getElementById('saveActionBtn').innerText = 'Save changes';
    fillActionTableSelect(a.tableId);
    document.getElementById('actionTable').disabled = true; // fixed after creation, same reason as a form's table
    onActionTableChange();
    actionSteps = parseStepsJson(a.stepsJson);
    renderActionSteps();
    document.getElementById('actionRunsPanel').classList.remove('hidden');
    await loadActionRuns(id);
    document.getElementById('actionEditor').classList.remove('hidden');
    document.getElementById('actionEditor').scrollIntoView({
        behavior: 'smooth'
    });
}

function closeActionEditor() {
    actionEditingId = null;
    refreshSidebar('actions');
    document.getElementById('actionEditor').classList.add('hidden');
}

function cancelActionEditor() {
    navigate('/actions');
}

function fillActionTableSelect(selected) {
    const sel = document.getElementById('actionTable');
    sel.innerHTML = currentTables
        .map((t) => `<option value="${t.id}" ${t.id === selected ? 'selected' : ''}>${escapeHtml(t.name)}</option>`)
        .join('');
}

function onActionTableChange() {
    const tableId = document.getElementById('actionTable').value;
    const table = currentTables.find((t) => t.id === tableId);
    actionTableFieldsList = table ? table.fields || [] : [];
    renderActionSteps(); // field pickers inside updateRecord steps depend on the table
}

async function loadActionRuns(id) {
    const runs = await fetch(`/api/_admin/actions/${id}/runs`).then((r) => r.json()).catch(() => []);
    const rows = document.getElementById('actionRunsRows');
    rows.innerHTML = runs
        .map((r) => {
            const badgeClass = r.status === 'done' ? 'badge-ok' : r.status === 'failed' ? 'badge-danger' : '';
            return `<tr>
                <td>${escapeHtml(r.recordId)}</td>
                <td>${TRIGGER_LABELS[r.triggerKind] || r.triggerKind}</td>
                <td><span class="badge ${badgeClass}">${escapeHtml(r.status)}</span></td>
                <td>${r.attempts}</td>
                <td>${r.status === 'pending' ? new Date(r.nextAttemptAt).toLocaleString() : '-'}</td>
                <td>${escapeHtml(r.lastError || '')}</td>
            </tr>`;
        })
        .join('');
    document.getElementById('actionRunsEmpty').classList.toggle('hidden', runs.length > 0);
}

/* steps */

function parseStepsJson(stepsJson) {
    try {
        const parsed = JSON.parse(stepsJson || '[]');
        return Array.isArray(parsed) ? parsed : [];
    } catch (e) {
        return [];
    }
}

function stepsToJson() {
    return JSON.stringify(actionSteps);
}

function addActionStep(type) {
    actionSteps.push(type === 'updateRecord' ? {
        type: 'updateRecord',
        setJson: {}
    } : {
        type: 'runExpression',
        expr: ''
    });
    renderActionSteps();
}

function removeActionStep(i) {
    actionSteps.splice(i, 1);
    renderActionSteps();
}

function renderActionSteps() {
    const container = document.getElementById('actionSteps');
    container.innerHTML = '';
    document.getElementById('actionStepsEmpty').classList.toggle('hidden', actionSteps.length > 0);

    actionSteps.forEach((step, i) => {
        const el = document.createElement('div');
        el.className = 'brow';

        const head = document.createElement('div');
        head.className = 'brow-head';
        const type = document.createElement('span');
        type.className = 'brow-type';
        type.innerText = step.type === 'updateRecord' ? 'update record' : 'run expression';
        head.appendChild(type);
        const actions = document.createElement('div');
        actions.className = 'brow-actions';
        const rm = document.createElement('button');
        rm.type = 'button';
        rm.className = 'btn btn-outline btn-sm';
        rm.innerText = '✕';
        rm.title = 'Remove step';
        rm.onclick = () => removeActionStep(i);
        actions.appendChild(rm);
        head.appendChild(actions);
        el.appendChild(head);

        if (step.type === 'runExpression') {
            const lab = document.createElement('label');
            lab.className = 'brow-field-label';
            lab.innerText = 'Expression';
            const inp = document.createElement('input');
            inp.className = 'input input-sm';
            inp.value = step.expr || '';
            inp.placeholder = "data.Qty > 0";
            inp.oninput = () => {
                step.expr = inp.value;
            };
            lab.appendChild(inp);
            el.appendChild(lab);
            attachFieldExprAutocomplete(inp, () => actionTableFieldsList.map((f) => f.name));
        } else {
            const rows = document.createElement('div');
            rows.className = 'brow-container-rows';
            Object.keys(step.setJson).forEach((fieldName) => {
                rows.appendChild(actionSetFieldRow(step, fieldName));
            });
            el.appendChild(rows);

            const addBtn = document.createElement('button');
            addBtn.type = 'button';
            addBtn.className = 'btn btn-outline btn-sm';
            addBtn.innerText = '+ Set field';
            addBtn.style.marginTop = '.5rem';
            addBtn.onclick = () => {
                const first = actionTableFieldsList.find((f) => !(f.name in step.setJson));
                if (!first) {
                    ui.toast('Every field on this table already has a value here.', 'error');
                    return;
                }
                step.setJson[first.name] = '';
                renderActionSteps();
            };
            el.appendChild(addBtn);
        }

        container.appendChild(el);
    });
}

function actionSetFieldRow(step, fieldName) {
    const row = document.createElement('div');
    row.className = 'brow-fields';

    const fieldLab = document.createElement('label');
    fieldLab.className = 'brow-field-label';
    fieldLab.innerText = 'Field';
    const fieldSel = document.createElement('select');
    fieldSel.className = 'input input-sm';
    fieldSel.innerHTML = actionTableFieldsList
        .map((f) => `<option value="${f.name}" ${f.name === fieldName ? 'selected' : ''}>${escapeHtml(f.label || f.name)}</option>`)
        .join('');
    fieldSel.onchange = () => {
        const expr = step.setJson[fieldName];
        delete step.setJson[fieldName];
        step.setJson[fieldSel.value] = expr;
        renderActionSteps();
    };
    fieldLab.appendChild(fieldSel);
    row.appendChild(fieldLab);

    const exprLab = document.createElement('label');
    exprLab.className = 'brow-field-label';
    exprLab.innerText = 'Expression';
    const exprInp = document.createElement('input');
    exprInp.className = 'input input-sm';
    exprInp.value = step.setJson[fieldName] || '';
    exprInp.placeholder = "'received'";
    exprInp.oninput = () => {
        step.setJson[fieldName] = exprInp.value;
    };
    exprLab.appendChild(exprInp);
    row.appendChild(exprLab);
    attachFieldExprAutocomplete(exprInp, () => actionTableFieldsList.map((f) => f.name));

    const rm = document.createElement('button');
    rm.type = 'button';
    rm.className = 'btn btn-outline btn-sm';
    rm.innerText = '✕';
    rm.title = 'Remove';
    rm.onclick = () => {
        delete step.setJson[fieldName];
        renderActionSteps();
    };
    row.appendChild(rm);

    return row;
}

async function saveAction(btn) {
    const name = document.getElementById('actionName').value.trim();
    if (!name) {
        ui.toast('Action name is required.', 'error');
        return;
    }
    if (!actionSteps.length) {
        ui.toast('An action needs at least one step.', 'error');
        return;
    }
    const body = {
        tableId: document.getElementById('actionTable').value,
        name,
        triggerKind: document.getElementById('actionTrigger').value,
        isEnabled: document.getElementById('actionEnabled').checked,
        stepsJson: stepsToJson(),
    };

    await ui.busy(btn, async () => {
        const isNew = !actionEditingId;
        const res = await fetch(isNew ? '/api/_admin/actions' : `/api/_admin/actions/${actionEditingId}`, {
            method: isNew ? 'POST' : 'PATCH',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(body),
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            ui.toast(data.errors || ['The action could not be saved.'], 'error');
            return;
        }
        ui.toast(isNew ? 'Action created.' : 'Action saved.', 'success');
        await loadActions();
        navigate(`/actions/${data.id}`, {
            replace: true
        });
    });
}
