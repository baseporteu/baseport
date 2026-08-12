/* schema canvas: interactive pan / zoom / drag */

const pencilIcon =
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/></svg>';
const trashIcon =
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6"/><path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"/></svg>';

let savedQueries = [];
let settingsCurrentPage = 'host';
let schemaNodes = {};
let schemaLayout = {};
let schemaZoomLevel = 1;
let schemaPan = {
    x: 48,
    y: 48
};
let schemaDrag = null;
let schemaContext = null;

function applySchemaTransform() {
    const stage = document.getElementById('schemaStage');
    if (stage) stage.style.transform = `translate(${schemaPan.x}px, ${schemaPan.y}px) scale(${schemaZoomLevel})`;
    const pct = document.getElementById('schemaZoomPct');
    if (pct) pct.innerText = Math.round(schemaZoomLevel * 100) + '%';
}

function schemaZoom(factor) {
    schemaZoomLevel = Math.min(2.5, Math.max(0.25, schemaZoomLevel * factor));
    applySchemaTransform();
    renderSchemaLinks();
}

function positionSchemaNodes() {
    document.querySelectorAll('.schema-node').forEach((el) => {
        const n = schemaNodes[el.dataset.pid];
        if (n) {
            el.style.left = n.x + 'px';
            el.style.top = n.y + 'px';
        }
    });
}

async function loadSchema() {
    const tables = await fetch('/api/_admin/tables').then((r) => r.json());
    schemaNodes = {};
    schemaZoomLevel = 1;
    schemaPan = {
        x: 48,
        y: 48
    };
    const nodesEl = document.getElementById('schemaNodes');
    const linksEl = document.getElementById('schemaLinks');
    nodesEl.innerHTML = '';
    if (!tables.length) {
        linksEl.innerHTML = '';
        applySchemaTransform();
        return;
    }
    const cols = Math.max(1, Math.ceil(Math.sqrt(tables.length)));
    const rows = Math.ceil(tables.length / cols);
    const heights = tables.map((t) => 48 + t.fields.length * 27 + 10);
    // grid-row layout: a row's height is its tallest node, so a short node below a tall one never lands inside it
    const rowHeight = new Array(rows).fill(0);
    tables.forEach((t, i) => {
        const row = Math.floor(i / cols);
        rowHeight[row] = Math.max(rowHeight[row], heights[i]);
    });
    const rowY = new Array(rows).fill(0);
    for (let r = 1; r < rows; r++) rowY[r] = rowY[r - 1] + rowHeight[r - 1] + 44;

    tables.forEach((t, i) => {
        const col = i % cols;
        const row = Math.floor(i / cols);
        schemaNodes[t.id] = {
            x: col * 280,
            y: rowY[row],
            w: 240,
            h: heights[i]
        };
        const card = document.createElement('div');
        card.className = 'schema-node';
        card.dataset.pid = t.id;
        let head = `<div class="schema-node-head"><span class="schema-node-name">${escapeHtml(t.name)}</span>${t.isProxy ? '<span class="type-badge">proxy</span>' : ''}</div>`;
        let body = '<div class="schema-node-body">';
        t.fields.forEach((f) => {
            const flags = [];
            if (f.isRequired) flags.push('required');
            if (f.isHidden) flags.push('hidden');
            if (f.dataType === 'calculated' || f.dataType === 'derived') flags.push(f.dataType);
            body += `<div class="schema-field" data-field="${escapeHtml(f.name)}"><code>${escapeHtml(f.name)}</code><span class="type-badge">${escapeHtml(f.dataType)}</span>${flags.length ? `<span class="flag-badge"> · ${escapeHtml(flags.join(' · '))}</span>` : ''}</div>`;
        });
        body += '</div>';
        card.innerHTML = head + body;
        card.addEventListener('pointerdown', (ev) => {
            if (ev.button !== 0) return;
            ev.preventDefault();
            const n = schemaNodes[t.id];
            schemaDrag = {
                type: 'node',
                pid: t.id,
                sx: ev.clientX,
                sy: ev.clientY,
                ox: n.x,
                oy: n.y
            };
            document.addEventListener('pointermove', onSchemaDragMove);
            document.addEventListener('pointerup', onSchemaDragEnd);
        });
        nodesEl.appendChild(card);
    });
    positionSchemaNodes();
    schemaLayout = JSON.parse(JSON.stringify(schemaNodes));
    applySchemaTransform();
    collectSchemaRefs(tables);
    renderSchemaLinks();
}

function renderSchemaLinks() {
    const svg = document.getElementById('schemaLinks');
    if (!svg) return;
    let paths = '';
    let maxX = 0,
        maxY = 0;
    for (const pid in schemaNodes) {
        const n = schemaNodes[pid];
        maxX = Math.max(maxX, n.x + n.w);
        maxY = Math.max(maxY, n.y + n.h);
    }
    const nodeEls = document.querySelectorAll('.schema-node');
    nodeEls.forEach((nodeEl) => {
        const n = schemaNodes[nodeEl.dataset.pid];
        if (!n) return;
        nodeEl.querySelectorAll('.schema-field').forEach((fieldEl) => {
            const fr = fieldEl.getBoundingClientRect();
            const nr = nodeEl.getBoundingClientRect();
            const sx = n.x + (fr.right - nr.left);
            const sy = n.y + (fr.top - nr.top) + fr.height / 2;
            // reference target resolved from the table's fields at layout time
            const target = schemaRefs[nodeEl.dataset.pid]?.[fieldEl.dataset.field];
            if (!target || !schemaNodes[target]) return;
            const t = schemaNodes[target];
            const tx = t.x;
            const ty = t.y + t.h / 2;
            const dx = Math.max(40, (tx - sx) / 2);
            paths += `<path d="M ${sx.toFixed(1)} ${sy.toFixed(1)} C ${(sx + dx).toFixed(1)} ${sy.toFixed(1)}, ${(tx - dx).toFixed(1)} ${ty.toFixed(1)}, ${tx.toFixed(1)} ${ty.toFixed(1)}" fill="none" stroke="hsl(var(--muted-foreground) / .55)" stroke-width="1.5"/>`;
        });
    });
    svg.innerHTML = paths;
    svg.setAttribute('width', maxX + 400);
    svg.setAttribute('height', maxY + 400);
}

let schemaRefs = {};

function collectSchemaRefs(tables) {
    schemaRefs = {};
    tables.forEach((t) => {
        schemaRefs[t.id] = {};
        t.fields.forEach((f) => {
            if (f.dataType !== 'reference') return;
            try {
                const o = JSON.parse(f.optionsJson || '[]');
                if (Array.isArray(o)) return;
                if (o && o.tableId) schemaRefs[t.id][f.name] = o.tableId;
            } catch (e) {}
        });
    });
}

function onSchemaDragMove(ev) {
    if (!schemaDrag) return;
    if (schemaDrag.type === 'node') {
        const n = schemaNodes[schemaDrag.pid];
        n.x = schemaDrag.ox + (ev.clientX - schemaDrag.sx) / schemaZoomLevel;
        n.y = schemaDrag.oy + (ev.clientY - schemaDrag.sy) / schemaZoomLevel;
        positionSchemaNodes();
        renderSchemaLinks();
    } else {
        schemaPan.x = schemaDrag.ox + (ev.clientX - schemaDrag.sx);
        schemaPan.y = schemaDrag.oy + (ev.clientY - schemaDrag.sy);
        applySchemaTransform();
    }
}

function onSchemaDragEnd() {
    schemaDrag = null;
    document.removeEventListener('pointermove', onSchemaDragMove);
    document.removeEventListener('pointerup', onSchemaDragEnd);
}

function closeSchemaContext() {
    if (schemaContext) {
        schemaContext.remove();
        schemaContext = null;
    }
}

function openSchemaContext(clientX, clientY) {
    const canvas = document.getElementById('schemaCanvas');
    if (!canvas) return;
    closeSchemaContext();
    const menu = document.createElement('div');
    menu.className = 'schema-context';
    menu.innerHTML =
        "<button type='button' class='btn btn-ghost btn-sm' onclick='resetSchemaLayout()'>Reset layout</button>" +
        "<button type='button' class='btn btn-ghost btn-sm' onclick='exportSchemaWebp()'>Export as WebP</button>";
    canvas.appendChild(menu);
    const rect = canvas.getBoundingClientRect();
    menu.style.left = Math.min(clientX - rect.left, rect.width - menu.offsetWidth - 8) + 'px';
    menu.style.top = Math.min(clientY - rect.top, rect.height - menu.offsetHeight - 8) + 'px';
    schemaContext = menu;
}

function resetSchemaLayout() {
    for (const pid in schemaNodes) {
        if (schemaLayout[pid]) {
            schemaNodes[pid].x = schemaLayout[pid].x;
            schemaNodes[pid].y = schemaLayout[pid].y;
        }
    }
    schemaZoomLevel = 1;
    schemaPan = {
        x: 48,
        y: 48
    };
    positionSchemaNodes();
    applySchemaTransform();
    renderSchemaLinks();
}

function exportSchemaWebp() {
    const canvas = document.getElementById('schemaCanvas');
    if (!canvas || !canvas.clientWidth) return;
    const dpr = window.devicePixelRatio || 1;
    const W = canvas.clientWidth;
    const H = canvas.clientHeight;
    const z = schemaZoomLevel;
    const out = document.createElement('canvas');
    out.width = Math.round(W * dpr);
    out.height = Math.round(H * dpr);
    const ctx = out.getContext('2d');
    const cs = getComputedStyle(canvas);
    const hsl = (n) => `hsl(${cs.getPropertyValue('--' + n).trim()})`;
    const card = hsl('card');
    const fg = hsl('foreground');
    const muted = hsl('muted');
    const mutedFg = hsl('muted-foreground');
    const border = hsl('border');
    const round = (x, y, w, h, r) => {
        ctx.beginPath();
        if (ctx.roundRect) ctx.roundRect(x, y, w, h, r);
        else ctx.rect(x, y, w, h);
    };

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = muted;
    ctx.fillRect(0, 0, W, H);
    ctx.beginPath();
    ctx.rect(0, 0, W, H);
    ctx.clip();
    ctx.setTransform(dpr * z, 0, 0, dpr * z, dpr * schemaPan.x, dpr * schemaPan.y);

    const nodeEls = [...document.querySelectorAll('.schema-node')];
    nodeEls.forEach((el) => {
        const n = schemaNodes[el.dataset.pid];
        if (!n) return;
        const nr = el.getBoundingClientRect();
        const anchors = [...el.querySelectorAll('.schema-field')].map((f) => {
            const fr = f.getBoundingClientRect();
            return {
                el: f,
                x: n.x + (fr.left - nr.left) / z,
                y: n.y + (fr.top - nr.top) / z + (fr.height / 2) / z,
            };
        });
        const pid = el.dataset.pid;

        ctx.strokeStyle = `hsl(${cs.getPropertyValue('--muted-foreground').trim()} / .55)`;
        ctx.lineWidth = 1.5;
        anchors.forEach((a) => {
            const target = schemaRefs[pid]?.[a.el.dataset.field];
            if (!target || !schemaNodes[target]) return;
            const t = schemaNodes[target];
            const dx = Math.max(40, (t.x - a.x) / 2);
            ctx.beginPath();
            ctx.moveTo(a.x, a.y);
            ctx.bezierCurveTo(a.x + dx, a.y, t.x - dx, t.y + t.h / 2, t.x, t.y + t.h / 2);
            ctx.stroke();
        });

        round(n.x, n.y, n.w, n.h, 6);
        ctx.fillStyle = card;
        ctx.fill();
        ctx.strokeStyle = border;
        ctx.lineWidth = 1;
        ctx.stroke();

        const hr = el.querySelector('.schema-node-head').getBoundingClientRect();
        ctx.strokeStyle = border;
        ctx.beginPath();
        ctx.moveTo(n.x, n.y + (hr.bottom - nr.top) / z);
        ctx.lineTo(n.x + n.w, n.y + (hr.bottom - nr.top) / z);
        ctx.stroke();

        ctx.textBaseline = 'middle';
        ctx.fillStyle = fg;
        ctx.font = "600 13px 'Geist', system-ui, sans-serif";
        ctx.fillText(el.querySelector('.schema-node-name').textContent, n.x + 12, n.y + (hr.top - nr.top) / z + (hr.height / 2) / z);

        anchors.forEach((a) => {
            const codeEl = a.el.querySelector('code');
            ctx.font = "12px 'Geist Mono', monospace";
            const tw = ctx.measureText(codeEl.textContent).width;
            ctx.fillStyle = muted;
            round(a.x, a.y - 8, tw + 12, 16, 3);
            ctx.fill();
            ctx.fillStyle = fg;
            ctx.fillText(codeEl.textContent, a.x + 6, a.y);
            const typeEl = a.el.querySelector('.type-badge');
            if (typeEl) {
                ctx.fillStyle = mutedFg;
                ctx.font = "11px 'Geist', system-ui, sans-serif";
                ctx.fillText(typeEl.textContent, a.x + tw + 18, a.y);
            }
        });
    });

    out.toBlob((blob) => {
        if (!blob) return;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'schema.webp';
        a.click();
        URL.revokeObjectURL(url);
    }, 'image/webp');
}

(function initSchemaCanvas() {
    const canvas = document.getElementById('schemaCanvas');
    if (!canvas) return;
    canvas.addEventListener('pointerdown', (ev) => {
        if (ev.button !== 0) return;
        if (ev.target.closest('.schema-node') || ev.target.closest('.schema-zoom')) return;
        ev.preventDefault();
        schemaDrag = {
            type: 'pan',
            sx: ev.clientX,
            sy: ev.clientY,
            ox: schemaPan.x,
            oy: schemaPan.y
        };
        document.addEventListener('pointermove', onSchemaDragMove);
        document.addEventListener('pointerup', onSchemaDragEnd);
    });
    canvas.addEventListener('contextmenu', (ev) => {
        ev.preventDefault();
        openSchemaContext(ev.clientX, ev.clientY);
    });
    document.addEventListener('click', closeSchemaContext);
    canvas.addEventListener(
        'wheel',
        (ev) => {
            ev.preventDefault();
            closeSchemaContext();
            const rect = canvas.getBoundingClientRect();
            const mx = ev.clientX - rect.left,
                my = ev.clientY - rect.top;
            const next = Math.min(2.5, Math.max(0.25, schemaZoomLevel * Math.exp(-ev.deltaY * 0.0015)));
            const k = next / schemaZoomLevel;
            schemaPan.x = mx - (mx - schemaPan.x) * k;
            schemaPan.y = my - (my - schemaPan.y) * k;
            schemaZoomLevel = next;
            applySchemaTransform();
            renderSchemaLinks();
        }, {
            passive: false
        },
    );
})();