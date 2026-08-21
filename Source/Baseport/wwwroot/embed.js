(function() {
    const script = document.currentScript;
    const urlParams = new URL(script.src);
    const formId = urlParams.searchParams.get('id');
    const apiBase = urlParams.origin;

    if (!document.getElementById('baserow-embed-style')) {
        const style = document.createElement('style');
        style.id = 'baserow-embed-style';
        style.innerText = `
            .baserow-embed {
                /* Override any of these on .baserow-embed (or a parent) to restyle the whole embed without touching a single rule. */
                --baserow-font: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
                --baserow-fg: #1a1a1a;
                --baserow-muted: #6b7280;
                --baserow-border: #e5e7eb;
                --baserow-surface: #fff;
                --baserow-accent: #1a1a1a;
                --baserow-accent-fg: #fff;
                --baserow-radius: .5rem;
                --baserow-gap: .75rem;

                font-family: var(--baserow-font);
                font-size: 16px;
                line-height: 1.5;
                color: var(--baserow-fg);
                color-scheme: light;
                max-width: 46rem;
                width: 100%;
            }
            .baserow-embed form { margin: 0; }
            .baserow-toasts { position: fixed; bottom: 1rem; right: 1rem; z-index: 2147483000; display: flex; flex-direction: column; gap: .5rem; max-width: min(24rem, calc(100vw - 2rem)); font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; font-size: .875rem; }
            .baserow-toast { padding: .625rem .75rem; border-radius: .5rem; color: #fff; box-shadow: 0 4px 16px rgb(0 0 0 / .18); cursor: pointer; }
            .baserow-toast-success { background: #2d9d5f; }
            .baserow-toast-error { background: #d63d3d; }
            .baserow-toast-info { background: #334155; }
            .baserow-readonly { display: flex; justify-content: space-between; gap: 1rem; padding: .5rem 0; border-bottom: 1px solid var(--baserow-border); }
            .baserow-readonly-label { color: var(--baserow-muted); }
            .baserow-readonly-value { font-weight: 500; text-align: right; }

            /* ---- list and lookup ---- */
            .baserow-head { margin: 0 0 var(--baserow-gap); }
            .baserow-desc { margin: 0 0 var(--baserow-gap); color: var(--baserow-muted); font-size: .875rem; }
            .baserow-toolbar { display: flex; gap: .5rem; align-items: center; margin-bottom: var(--baserow-gap); }
            .baserow-table-wrap { width: 100%; overflow-x: auto; border: 1px solid var(--baserow-border); border-radius: var(--baserow-radius); background: var(--baserow-surface); }
            .baserow-table { width: 100%; border-collapse: collapse; font-size: .875rem; }
            .baserow-table th, .baserow-table td { padding: .5rem .75rem; text-align: left; border-bottom: 1px solid var(--baserow-border); vertical-align: top; }
            .baserow-table th { font-weight: 600; color: var(--baserow-muted); font-size: .8125rem; white-space: nowrap; }
            .baserow-table tbody tr:last-child td { border-bottom: none; }
            .baserow-table tbody tr:hover { background: color-mix(in srgb, var(--baserow-border) 35%, transparent); }
            .baserow-empty { padding: 1.25rem; text-align: center; color: var(--baserow-muted); font-size: .875rem; }
            .baserow-pager { display: flex; align-items: center; justify-content: flex-end; gap: .5rem; margin-top: var(--baserow-gap); font-size: .875rem; }
            .baserow-pager-status { color: var(--baserow-muted); }
            .baserow-embed .baserow-search {
                flex: 1;
                min-width: 0;
                width: 100%;
                margin: 0;
                padding: .5rem .625rem;
                font: inherit;
                font-size: .875rem;
                color: var(--baserow-fg);
                background: var(--baserow-surface);
                border: 1px solid var(--baserow-border);
                border-radius: var(--baserow-radius);
                box-sizing: border-box;
            }
            .baserow-embed .baserow-search:focus { outline: 2px solid var(--baserow-accent); outline-offset: -1px; }
            .baserow-embed .baserow-btn,
            .baserow-toasts .baserow-btn {
                margin: 0;
                padding: .375rem .75rem;
                font: inherit;
                font-size: .8125rem;
                font-weight: 500;
                color: var(--baserow-fg);
                background: var(--baserow-surface);
                border: 1px solid var(--baserow-border);
                border-radius: var(--baserow-radius);
                cursor: pointer;
            }
            .baserow-embed .baserow-btn:hover:not(:disabled) { background: color-mix(in srgb, var(--baserow-border) 40%, transparent); }
            .baserow-embed .baserow-btn:disabled { opacity: .45; cursor: default; }
            .baserow-record { width: 100%; border-collapse: collapse; font-size: .875rem; margin-top: var(--baserow-gap); border: 1px solid var(--baserow-border); border-radius: var(--baserow-radius); background: var(--baserow-surface); }
            .baserow-record th { width: 40%; text-align: left; font-weight: 500; color: var(--baserow-muted); padding: .5rem .75rem; border-bottom: 1px solid var(--baserow-border); }
            .baserow-record td { padding: .5rem .75rem; border-bottom: 1px solid var(--baserow-border); }
            .baserow-record tr:last-child th, .baserow-record tr:last-child td { border-bottom: none; }
            .baserow-section + .baserow-section { margin-top: 1.75rem; padding-top: 1.5rem; border-top: 1px solid var(--baserow-border); }
            .baserow-embed h3 { font-size: 1.25rem; font-weight: 600; margin: 0 0 1rem; color: inherit; }
            .baserow-embed label {
                display: block;
                font-size: .875rem;
                font-weight: 500;
                margin: 0 0 .375rem;
                color: inherit;
            }
            .baserow-embed input[type='text'],
            .baserow-embed input[type='number'],
            .baserow-embed input[type='date'],
            .baserow-embed input[type='datetime-local'],
            .baserow-embed input[type='url'],
            .baserow-embed input[type='email'],
            .baserow-embed input[type='tel'],
            .baserow-embed input[type='time'],
            .baserow-embed input[type='password'],
            .baserow-embed select,
            .baserow-embed textarea {
                display: block;
                width: 100%;
                box-sizing: border-box;
                padding: .5rem .625rem;
                font-size: .9375rem;
                font-family: inherit;
                color: #1a1a1a;
                background: #fff;
                border: 1px solid #c9c9c9;
                border-radius: .375rem;
                margin: 0 0 .75rem;
                transition: border-color .15s, box-shadow .15s;
            }
            .baserow-embed [hidden] { display: none !important; }
            .baserow-embed input:focus-visible,
            .baserow-embed select:focus-visible,
            .baserow-embed textarea:focus-visible {
                outline: none;
                border-color: #4a4a4a;
                box-shadow: 0 0 0 2px rgb(0 0 0 / .08);
            }
            .baserow-embed .baserow-invalid { border-color: #d63d3d; }
            .baserow-embed .baserow-ms.baserow-invalid { border: 1px solid #d63d3d; border-radius: .375rem; padding: .375rem .5rem; }
            .baserow-embed textarea { min-height: 4rem; resize: vertical; }
            .baserow-embed input[type='checkbox'] { width: auto; margin: 0 .375rem 0 0; accent-color: #1a1a1a; }
            .baserow-embed input[readonly] { background: #f4f4f4; color: #555; }
            .baserow-embed button {
                font-family: inherit;
                font-size: .9375rem;
                font-weight: 500;
                padding: .5rem 1.25rem;
                border-radius: .375rem;
                border: 1px solid transparent;
                cursor: pointer;
                margin: .25rem .5rem .25rem 0;
            }
            .baserow-embed button[type='submit'] { background: #1a1a1a; color: #fff; }
            .baserow-embed button[type='submit']:hover { background: #333; }
            .baserow-embed .baserow-btn-custom { background: #fff; color: #1a1a1a; border-color: #c9c9c9; }
            .baserow-embed .baserow-btn-custom:hover { background: #f4f4f4; }
            .baserow-row { display: flex; flex-wrap: wrap; gap: 0 1.25rem; margin-bottom: .5rem; }
            .baserow-col { flex: 1 1 12rem; min-width: 0; }
            .baserow-group {
                border: 1px solid #d3d3d3;
                border-radius: .5rem;
                padding: .75rem .875rem .25rem;
                margin: 0 0 1rem;
                min-width: 0;
            }
            .baserow-group legend { font-weight: 600; font-size: .875rem; padding: 0 .25rem; color: inherit; }
            .baserow-subtotal {
                display: flex;
                justify-content: space-between;
                align-items: baseline;
                gap: 1rem;
                padding: .625rem .875rem;
                margin: .5rem 0 1rem;
                border-top: 1px solid #d3d3d3;
                background: #f8f8f8;
                border-radius: .375rem;
            }
            .baserow-subtotal span { font-weight: 500; font-size: .9375rem; }
            .baserow-subtotal-value { font-weight: 700; font-variant-numeric: tabular-nums; }
            .baserow-ms-opt { display: flex; align-items: center; font-weight: 400; font-size: .875rem; margin: 0 0 .25rem; }
            .baserow-ms-opt input { flex-shrink: 0; }
            .baserow-embed article { font-size: .875rem; padding: .75rem; border-radius: .375rem; margin-top: .75rem; }
            .baserow-combobox { position: relative; margin: 0 0 .75rem; }
            .baserow-combobox input[type='text'] { margin-bottom: 0; }
            .baserow-combobox-list { position: absolute; z-index: 20; top: calc(100% + .25rem); left: 0; right: 0; max-height: 14rem; overflow-y: auto; margin: 0; padding: .25rem; list-style: none; background: var(--baserow-surface); border: 1px solid var(--baserow-border); border-radius: var(--baserow-radius); box-shadow: 0 4px 12px rgb(0 0 0 / .12); }
            .baserow-combobox-list li { padding: .375rem .5rem; border-radius: .25rem; cursor: pointer; font-size: .875rem; }
            .baserow-combobox-list li:hover, .baserow-combobox-list li.active { background: var(--baserow-border); }
            .baserow-combobox-empty { color: var(--baserow-muted); cursor: default !important; }
            .baserow-combobox-empty:hover { background: transparent !important; }
            .baserow-combobox-chip { display: flex; align-items: center; justify-content: space-between; gap: .5rem; padding: .5rem .5rem .5rem .625rem; margin: 0; font-size: .9375rem; border: 1px solid #c9c9c9; border-radius: .375rem; background: var(--baserow-border); }
            .baserow-combobox-chip-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .baserow-embed .baserow-combobox-chip-remove { flex-shrink: 0; display: inline-flex; align-items: center; justify-content: center; width: 1.25rem; height: 1.25rem; padding: 0; margin: 0; border: none; border-radius: 999px; background: transparent; color: var(--baserow-muted); font-size: 1rem; line-height: 1; cursor: pointer; }
            .baserow-embed .baserow-combobox-chip-remove:hover { background: rgb(0 0 0 / .08); color: var(--baserow-fg); }
        `;
        document.head.appendChild(style);
    }

    const container = document.createElement('div');
    container.className = 'baserow-embed';
    script.parentNode.insertBefore(container, script.nextSibling);

    // Feedback is a toast here too, a host page's layout is never disturbed by a message box appearing inside the form.
    function toastHost() {
        let host = document.querySelector('.baserow-toasts');
        if (!host) {
            host = document.createElement('div');
            host.className = 'baserow-toasts';
            host.setAttribute('role', 'status');
            host.setAttribute('aria-live', 'polite');
            document.body.appendChild(host);
        }
        return host;
    }

    // Toasts stack newest at the bottom, capped at eight on screen.
    function toast(message, kind) {
        const text = Array.isArray(message) ? message.join(' ') : String(message || '');
        if (!text.trim()) return;
        const host = toastHost();
        // The oldest sits at the top, trimming the first child keeps the freshest eight.
        while (host.children.length >= 8) host.firstChild.remove();
        const el = document.createElement('div');
        el.className = 'baserow-toast baserow-toast-' + (kind || 'info');
        el.textContent = text;
        el.title = 'Click to copy';
        el.addEventListener('click', () => copyToast(el, text));
        host.appendChild(el);
        setTimeout(() => el.remove(), kind === 'error' ? 8000 : 4500);
    }

    // navigator.clipboard is only offered on secure contexts, a plain-http host falls back to a hidden textarea and execCommand, which still works there.
    function copyToast(el, text) {
        const flash = () => {
            const original = el.textContent;
            el.textContent = 'Copied to clipboard';
            setTimeout(() => (el.textContent = original), 1200);
        };
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(flash).catch(() => legacyCopy(text, flash));
        } else {
            legacyCopy(text, flash);
        }
    }

    function legacyCopy(text, done) {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        try {
            document.execCommand('copy');
        } catch (e) {}
        ta.remove();
        done();
    }

    let formEl = null;
    let tableSchema = null;
    let formIsReadOnly = false;
    let formCurrency = 'EUR';
    let readOnlyData = null;

    const TEXT_LENGTH_TYPES = new Set(['text', 'longtext', 'richtext', 'slug', 'email', 'url', 'password']);

    fetch(`${apiBase}/api/forms/${formId}/schema`)
        .then((res) => (res.ok ? res.json() : Promise.reject(res.status)))
        .then((data) => {
            tableSchema = data.table;
            formIsReadOnly = !!data.form.isReadOnly;
            formCurrency = data.currency || 'EUR';
            // One script tag, three behaviours; the server decides the kind, and both actions may be on, both render: one RMA form can look an existing case up and raise a new one.
            if (data.form.kind === 'list') {
                renderList(data.form, data.table, container);
            } else {
                const actions = data.form.actions || ['submit'];
                if (actions.includes('lookup')) renderLookup(data.form, data.table, container);
                if (actions.includes('submit')) renderForm(data.form, data.table, container);
            }
            // The hosted page at /f/{id} ships its rows in the html so a crawler and a scriptless reader get them. It stays on screen until this render replaces it, or the page would blank for the length of the fetch.
            document.getElementById('baseport-ssr')?.remove();
        })
        .catch(() => {
            container.innerHTML = '<p>This form is not available.</p>';
        });

    function parseConfig(json) {
        try {
            return JSON.parse(json || '{}') || {};
        } catch (e) {
            return {};
        }
    }

    function fieldLabel(f) {
        return f.label || f.name;
    }

    // Parses to an inert document first: nothing runs and nothing loads while we prune.
    const BANNED = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'LINK', 'META', 'BASE', 'FORM', 'SVG']);

    // javascript: and data: are script in a URL; shared by the markup sanitizer and every href built from an expression.
    function isUnsafeUrl(v) {
        const s = String(v == null ? '' : v).replace(/[\s\u0000-\u001f]/g, '').toLowerCase();
        return s.startsWith('javascript:') || s.startsWith('data:text/html');
    }

    function setSafeHtml(target, html) {
        const parsed = new DOMParser().parseFromString(String(html), 'text/html');
        parsed.body.querySelectorAll('*').forEach((el) => {
            if (BANNED.has(el.tagName)) {
                el.remove();
                return;
            }
            [...el.attributes].forEach((attr) => {
                const name = attr.name.toLowerCase();
                // on* is script by another name.
                if (name.startsWith('on') || isUnsafeUrl(attr.value)) {
                    el.removeAttribute(attr.name);
                }
            });
        });
        target.replaceChildren(...parsed.body.childNodes);
    }

    // Evaluates an author expression against raw (unescaped) row data -- used to build a URL, never to inject markup.
    function safeEval(expression, data) {
        try {
            return new Function('data', 'return ' + expression)(data || {});
        } catch (e) {
            return null;
        }
    }

    function renderCell(expression, data) {
        const safe = {};
        Object.keys(data || {}).forEach((k) => {
            const v = data[k];
            safe[k] = typeof v === 'string' ? escapeHtml(v) : v;
        });
        try {
            const out = new Function('data', 'return ' + expression)(safe);
            return out === null || out === undefined ? '' : String(out);
        } catch (e) {
            return escapeHtml(displayValue(data ? data[Object.keys(data)[0]] : ''));
        }
    }

    function escapeHtml(s) {
        return String(s === null || s === undefined ? '' : s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function displayValue(v) {
        if (v === null || v === undefined || v === '') return '-';
        if (Array.isArray(v)) return v.join(', ');
        if (typeof v === 'object') return JSON.stringify(v);
        return String(v);
    }

    function heading(text) {
        const h = document.createElement('h3');
        h.className = 'baserow-head';
        h.innerText = text;
        return h;
    }

    function description(text) {
        const p = document.createElement('p');
        p.className = 'baserow-desc';
        p.innerText = text;
        return p;
    }

    function emptyNote(text) {
        const p = document.createElement('p');
        p.className = 'baserow-empty';
        p.innerText = text;
        return p;
    }

    /* LOOKUP */

    function renderLookup(form, table, parent) {
        const cfg = parseConfig(form.configJson);
        const both = (form.actions || []).includes('submit') && (form.actions || []).includes('lookup');
        const wrap = document.createElement('div');
        wrap.className = 'baserow-section';
        if (form.title) wrap.appendChild(heading(both ? cfg.lookupTitle || 'Find an existing record' : form.title));
        if (form.description && !both) wrap.appendChild(description(form.description));

        const searchForm = document.createElement('form');
        searchForm.className = 'baserow-toolbar';
        const input = document.createElement('input');
        input.className = 'baserow-search';
        input.type = 'search';
        input.required = true;
        input.placeholder = (cfg.matchFields || []).length ?
            'Enter your ' + cfg.matchFields.map((n) => {
                const f = (table.fields || []).find((x) => x.name === n);
                return f ? fieldLabel(f) : n;
            }).join(' or ') :
            'Enter your reference';
        input.setAttribute('aria-label', input.placeholder);
        const btn = document.createElement('button');
        btn.type = 'submit';
        btn.innerText = 'Look up';
        searchForm.appendChild(input);
        searchForm.appendChild(btn);

        const result = document.createElement('div');
        result.setAttribute('aria-live', 'polite');

        function runLookup(term) {
            input.value = term;
            // A "follow" list widget elsewhere on the page (config: followLookup) mirrors whatever gets looked up here.
            window.dispatchEvent(new CustomEvent('baseport:lookup', {
                detail: {
                    formId,
                    term
                }
            }));
            btn.setAttribute('aria-busy', 'true');
            fetch(`${apiBase}/api/forms/${formId}/form?mode=lookup&q=${encodeURIComponent(term)}`)
                .then((r) => r.json().then((body) => ({
                    ok: r.ok,
                    body
                })))
                .then(({
                    ok,
                    body
                }) => {
                    btn.removeAttribute('aria-busy');
                    if (!ok || !body.found) {
                        result.innerHTML = '';
                        toast(body.message || (body.errors || []).join(' ') || 'No matching record was found.', 'error');
                        return;
                    }
                    result.innerHTML = '';
                    result.appendChild(renderRecordTable(table.fields, body.data));
                })
                .catch(() => {
                    btn.removeAttribute('aria-busy');
                    toast('Lookup failed. Please try again.', 'error');
                });
        }

        searchForm.onsubmit = (ev) => {
            ev.preventDefault();
            const term = input.value.trim();
            if (term) runLookup(term);
        };

        wrap.appendChild(searchForm);
        wrap.appendChild(result);
        parent.appendChild(wrap);

        // A row-action link elsewhere can deep-link straight into a result via ?q=.
        const qs = (new URLSearchParams(window.location.search).get('q') || '').trim();
        if (qs) runLookup(qs);
    }

    function renderRecordTable(fields, data) {
        const table = document.createElement('table');
        table.className = 'baserow-record';
        const tbody = document.createElement('tbody');
        fields.forEach((f) => {
            const tr = document.createElement('tr');
            const th = document.createElement('th');
            th.scope = 'row';
            th.innerText = fieldLabel(f);
            const td = document.createElement('td');
            const raw = data ? data[f.name] : null;
            td.innerText = f.dataType === 'currency' && raw !== null && raw !== undefined && raw !== '' ?
                fmtCurrency(raw, f.currency) :
                displayValue(raw);
            tr.appendChild(th);
            tr.appendChild(td);
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        return table;
    }

    /* LIST */

    function renderList(form, table, parent) {
        const cfg = parseConfig(form.configJson);
        const wrap = document.createElement('div');
        if (form.title) wrap.appendChild(heading(form.title));
        if (form.description) wrap.appendChild(description(form.description));

        const toolbar = document.createElement('div');
        toolbar.className = 'baserow-toolbar';
        const search = document.createElement('input');
        search.className = 'baserow-search';
        search.type = 'search';
        search.placeholder = 'Search…';
        search.setAttribute('aria-label', 'Search the list');
        toolbar.appendChild(search);

        const body = document.createElement('div');
        body.setAttribute('aria-live', 'polite');
        const pager = document.createElement('div');
        pager.className = 'baserow-pager';

        let page = 1;
        let timer = null;
        search.addEventListener('input', () => {
            clearTimeout(timer);
            timer = setTimeout(() => {
                page = 1;
                load();
            }, 250);
        });

        // re-filters live when the linked lookup widget (cfg.followLookup names its form id) submits a new value
        if (cfg.followLookup) {
            window.addEventListener('baseport:lookup', (e) => {
                if (e.detail.formId !== cfg.followLookup) return;
                page = 1;
                search.value = e.detail.term;
                load();
            });
        }

        function load() {
            const q = search.value.trim();
            if (cfg.requireQuery && !q) {
                body.innerHTML = '';
                pager.innerHTML = '';
                body.appendChild(emptyNote('Enter a value to search.'));
                return;
            }
            const url = `${apiBase}/api/forms/${formId}/list?page=${page}` + (q ? `&q=${encodeURIComponent(q)}` : '');
            fetch(url)
                .then((r) => r.json())
                .then((data) => {
                    body.innerHTML = '';
                    pager.innerHTML = '';
                    if (!data.rows || !data.rows.length) {
                        body.appendChild(emptyNote(q ? 'Nothing matches that search.' : 'Nothing to show yet.'));
                        return;
                    }
                    body.appendChild(buildListTable(data));
                    buildPager(data);
                })
                .catch(() => toast('Could not load the list.', 'error'));
        }

        function buildListTable(data) {
            // Wrapped so a wide table scrolls inside the embed, not the host page.
            const wrapper = document.createElement('div');
            wrapper.className = 'baserow-table-wrap';
            const t = document.createElement('table');
            t.className = 'baserow-table';
            const actions = data.actions || [];
            const thead = document.createElement('thead');
            const hr = document.createElement('tr');
            data.columns.forEach((c) => {
                const th = document.createElement('th');
                th.scope = 'col';
                th.innerText = c.label;
                hr.appendChild(th);
            });
            if (actions.length) hr.appendChild(document.createElement('th'));
            thead.appendChild(hr);
            const tbody = document.createElement('tbody');
            data.rows.forEach((row) => {
                const tr = document.createElement('tr');
                data.columns.forEach((c) => {
                    const td = document.createElement('td');
                    const raw = row.data ? row.data[c.name] : null;
                    // A render expression emits markup on purpose, it cannot be text. It is author-written, and this runs on the customer's page.
                    if (c.render) setSafeHtml(td, renderCell(c.render, row.data));
                    else if (c.dataType === 'currency' && raw !== null && raw !== undefined && raw !== '') td.innerText = fmtCurrency(raw, c.currency);
                    else td.innerText = displayValue(raw);
                    tr.appendChild(td);
                });
                if (actions.length) {
                    const td = document.createElement('td');
                    actions.forEach((a) => {
                        const b = document.createElement('button');
                        b.type = 'button';
                        b.className = 'baserow-btn';
                        b.innerText = a.label;
                        // Builds a URL from the row's real data, never the HTML-escaped copy renderCell uses for markup.
                        b.onclick = () => {
                            const url = safeEval(a.hrefExpr, row.data);
                            if (typeof url === 'string' && url && !isUnsafeUrl(url)) window.location.href = url;
                        };
                        td.appendChild(b);
                    });
                    tr.appendChild(td);
                }
                tbody.appendChild(tr);
            });
            t.appendChild(thead);
            t.appendChild(tbody);
            wrapper.appendChild(t);
            return wrapper;
        }

        function buildPager(data) {
            if (data.paged === false) return;
            const prev = document.createElement('button');
            prev.type = 'button';
            prev.className = 'baserow-btn';
            prev.innerText = 'Previous';
            prev.disabled = data.page <= 1;
            prev.onclick = () => {
                page = data.page - 1;
                load();
            };

            const next = document.createElement('button');
            next.type = 'button';
            next.className = 'baserow-btn';
            next.innerText = 'Next';
            next.disabled = data.page >= data.totalPages;
            next.onclick = () => {
                page = data.page + 1;
                load();
            };

            const status = document.createElement('span');
            status.className = 'baserow-pager-status';
            status.innerText = `Page ${data.page} of ${data.totalPages} · ${data.total} records`;

            pager.appendChild(status);
            pager.appendChild(prev);
            pager.appendChild(next);
        }

        wrap.appendChild(toolbar);
        wrap.appendChild(body);
        wrap.appendChild(pager);
        parent.appendChild(wrap);

        // A row-action link elsewhere can deep-link straight into a filtered list via ?q=.
        const qs = (new URLSearchParams(window.location.search).get('q') || '').trim();
        if (qs) search.value = qs;
        load();
    }

    function parseLayout(layoutJson, table) {
        try {
            const p = JSON.parse(layoutJson || '[]');
            if (p && Array.isArray(p.rows)) return p;
            if (Array.isArray(p)) {
                const rows = p.map((r) => ({
                    t: 'row',
                    cols: [{
                        t: 'col',
                        w: 12,
                        items: r.filter((x) => x !== 'spacer')
                    }]
                }));
                return {
                    rows
                };
            }
        } catch (e) {}
        return {
            rows: [{
                t: 'row',
                cols: [{
                    t: 'col',
                    w: 12,
                    items: table.fields.map((f) => f.name)
                }]
            }]
        };
    }

    function renderForm(formConfig, table, parent) {
        const cfg = parseConfig(formConfig.configJson);
        const both = (formConfig.actions || []).includes('submit') && (formConfig.actions || []).includes('lookup');
        formEl = document.createElement('form');
        formEl.className = 'baserow-section';

        const shownTitle = both ?
            cfg.submitTitle || 'Or create a new one' :
            formConfig.title;
        if (shownTitle) {
            const title = document.createElement('h3');
            title.className = 'baserow-head';
            title.innerText = shownTitle;
            formEl.appendChild(title);
        }

        const layout = parseLayout(formConfig.layoutJson, table);
        let hasSubmitButton = false;

        layout.rows.forEach((row) => {
            const node = renderLayoutRow(row, table);
            if (!node) return;
            if (row.t === 'button' && row.action === 'submit') hasSubmitButton = true;
            if (row.t === 'button_bar' && (row.buttons || []).some((b) => b.action === 'submit')) hasSubmitButton = true;
            formEl.appendChild(node);
        });

        if (!hasSubmitButton) {
            const saveBtn = document.createElement('button');
            saveBtn.type = 'submit';
            saveBtn.innerText = 'Submit Data';
            formEl.appendChild(saveBtn);
        }

        formEl.addEventListener('input', (e) => {
            if (e.target && e.target.classList) e.target.classList.remove('baserow-invalid');
            triggerReactiveUpdate();
        });
        triggerReactiveUpdate();

        formEl.onsubmit = (e) => {
            e.preventDefault();
            const result = validate();
            if (result.errors.length) {
                markInvalid(result.invalid);
                toast(result.errors, 'error');
                return;
            }
            const submitBtn = formEl.querySelector('button[type="submit"]');
            if (submitBtn) submitBtn.setAttribute('aria-busy', 'true');
            const data = extractFormData();

            // A file field's value is a File object; sending one forces multipart/form-data for the whole submission.
            const hasFile = Object.values(data).some((v) => typeof File !== 'undefined' && v instanceof File);
            const fetchOptions = hasFile ? {
                method: 'POST',
                body: toFormData(data)
            } : {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(data)
            };

            fetch(`${apiBase}/api/forms/${formId}/form`, fetchOptions)
                .then((r) => r.json().then((res) => ({
                    ok: r.ok,
                    res
                })))
                .then(({
                    ok,
                    res
                }) => {
                    if (submitBtn) submitBtn.removeAttribute('aria-busy');
                    if (ok) {
                        if (cfg.onSuccessRedirect) {
                            const url = safeEval(cfg.onSuccessRedirect, data);
                            if (typeof url === 'string' && url && !isUnsafeUrl(url)) {
                                window.location.href = url;
                                return;
                            }
                        }
                        toast('Thanks, your submission was received.', 'success');
                        formEl.reset();
                        triggerReactiveUpdate();
                    } else {
                        // The server names every field that failed alongside its message, the same inputs that errored on submit are painted red immediately.
                        markInvalid(res.invalid || []);
                        toast(res && res.errors && res.errors.length ? res.errors : ['Submit failed. Please try again.'], 'error');
                    }
                })
                .catch(() => {
                    if (submitBtn) submitBtn.removeAttribute('aria-busy');
                    toast('Submit failed. Please try again.', 'error');
                });
        };

        parent.appendChild(formEl);
    }

    // Shared by a standalone "button" block and every button inside a "button_bar".
    function buildActionButton(row) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'baserow-btn-custom';
        btn.innerText = row.label || 'Button';
        btn.onclick = () => {
            if (row.action === 'submit') formEl.requestSubmit();
            else if (row.action === 'reset') {
                formEl.reset();
                triggerReactiveUpdate();
            } else if (row.action === 'cancel') {
                const url = (row.href || '').trim();
                if (!url) window.history.back();
                else if (!isUnsafeUrl(url)) window.location.href = url;
            } else if (row.action === 'validate') {
                const result = validate();
                if (result.errors.length) {
                    markInvalid(result.invalid);
                    toast(result.errors, 'error');
                } else {
                    toast('Looks good.', 'success');
                }
            } else if (row.action === 'link') {
                const url = safeEval(row.hrefExpr, extractFormData());
                if (typeof url === 'string' && url && !isUnsafeUrl(url)) window.location.href = url;
            } else if (row.action === 'run') {
                // A blank button: no fixed outcome, just this expression's result surfaced as a toast.
                const result = safeEval(row.expr, extractFormData());
                if (result !== '' && result !== null && result !== undefined) toast(String(result), 'info');
            }
        };
        return btn;
    }

    // The sub-schema of an array field; null means a plain scalar-list array field.
    function arrayColumns(field) {
        try {
            const o = JSON.parse(field.optionsJson || '{}');
            return Array.isArray(o.fields) && o.fields.length ? o.fields : null;
        } catch (e) {
            return null;
        }
    }

    function lineItemInputType(dataType) {
        if (dataType === 'number' || dataType === 'currency') return 'number';
        if (dataType === 'boolean') return 'checkbox';
        if (dataType === 'date') return 'date';
        return 'text'; // also covers 'select': the server accepts any string for a line-item cell of that type
    }

    // Renders an add/remove-row table bound to one array field. The rows live only in this closure; every
    // mutation re-serializes into a hidden data-kind="json" input, which extractFormData()/toFormData()
    // already know how to turn into that field's array-of-objects value, same as any other json/array field.
    function renderLineItems(rowCfg, table) {
        const field = table.fields.find((f) => f.name === rowCfg.field);
        const columns = field ? arrayColumns(field) : null;
        if (!field || !columns) return null;

        const wrap = document.createElement('div');
        const caption = document.createElement('label');
        caption.innerText = field.label || field.name;
        wrap.appendChild(caption);

        const hidden = document.createElement('input');
        hidden.type = 'hidden';
        hidden.dataset.name = field.name;
        hidden.dataset.kind = 'json';
        let rows = [];

        function sync() {
            hidden.value = JSON.stringify(rows);
            hidden.dispatchEvent(new Event('input', {
                bubbles: true
            }));
        }

        const tableWrap = document.createElement('div');
        tableWrap.className = 'baserow-table-wrap';
        const tableEl = document.createElement('table');
        tableEl.className = 'baserow-table';
        const thead = document.createElement('thead');
        const headRow = document.createElement('tr');
        columns.forEach((c) => {
            const th = document.createElement('th');
            th.innerText = c.label || c.name;
            headRow.appendChild(th);
        });
        headRow.appendChild(document.createElement('th'));
        thead.appendChild(headRow);
        tableEl.appendChild(thead);

        const tbody = document.createElement('tbody');
        tableEl.appendChild(tbody);

        function renderRows() {
            tbody.innerHTML = '';
            rows.forEach((r, i) => {
                const tr = document.createElement('tr');
                columns.forEach((c) => {
                    const td = document.createElement('td');
                    const type = lineItemInputType(c.dataType);
                    const inp = document.createElement('input');
                    inp.type = type;
                    if (type === 'checkbox') inp.checked = !!r[c.name];
                    else inp.value = r[c.name] === undefined || r[c.name] === null ? '' : r[c.name];
                    inp.oninput = () => {
                        r[c.name] = type === 'checkbox' ? inp.checked : type === 'number' ? (inp.value === '' ? '' : Number(inp.value)) : inp.value;
                        sync();
                    };
                    td.appendChild(inp);
                    tr.appendChild(td);
                });
                const rmTd = document.createElement('td');
                const rm = document.createElement('button');
                rm.type = 'button';
                rm.className = 'baserow-btn-custom';
                rm.innerText = '✕';
                rm.onclick = () => {
                    rows.splice(i, 1);
                    sync();
                    renderRows();
                };
                rmTd.appendChild(rm);
                tr.appendChild(rmTd);
                tbody.appendChild(tr);
            });
        }
        renderRows();
        tableWrap.appendChild(tableEl);
        wrap.appendChild(tableWrap);

        const addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'baserow-btn-custom';
        addBtn.innerText = '+ Add row';
        addBtn.onclick = () => {
            rows.push({});
            sync();
            renderRows();
        };
        wrap.appendChild(addBtn);
        wrap.appendChild(hidden);
        sync();
        return wrap;
    }

    function renderLayoutRow(row, table) {
        if (row.t === 'subtotal') {
            const div = document.createElement('div');
            div.className = 'baserow-subtotal';
            const lab = document.createElement('span');
            lab.innerText = row.label || 'Total';
            const val = document.createElement('strong');
            val.className = 'baserow-subtotal-value';
            val.dataset.expr = row.expr || '0';
            val.dataset.format = row.format || '';
            if (row.currency) val.dataset.currency = row.currency;
            div.appendChild(lab);
            div.appendChild(val);
            return div;
        }

        if (row.t === 'button') return buildActionButton(row);

        if (row.t === 'button_bar') {
            const bar = document.createElement('div');
            bar.style.display = 'flex';
            bar.style.gap = '.5rem';
            bar.style.flexWrap = 'wrap';
            bar.style.justifyContent = row.align || 'flex-end';
            (row.buttons || []).forEach((btnCfg) => bar.appendChild(buildActionButton(btnCfg)));
            return bar;
        }

        if (row.t === 'container') {
            const wrap = document.createElement('fieldset');
            wrap.className = 'baserow-group';
            if (row.title) {
                const leg = document.createElement('legend');
                leg.innerText = row.title;
                wrap.appendChild(leg);
            }
            (row.rows || []).forEach((nrow) => {
                const node = renderLayoutRow(nrow, table);
                if (node) wrap.appendChild(node);
            });
            return wrap;
        }

        if (row.t === 'line_items') return renderLineItems(row, table);

        if (row.t !== 'row' && row.t !== 'group') return null;

        const wrap = document.createElement(row.t === 'group' ? 'fieldset' : 'div');
        wrap.className = row.t === 'group' ? 'baserow-group' : 'baserow-row';
        if (row.t === 'group' && row.title) {
            const leg = document.createElement('legend');
            leg.innerText = row.title;
            wrap.appendChild(leg);
        }
        (row.cols || []).forEach((col) => {
            const colDiv = document.createElement('div');
            colDiv.className = 'baserow-col';
            const total = row.cols.length || 1;
            colDiv.style.flexGrow = col.w || 12 / total;
            (col.items || []).forEach((item) => {
                if (typeof item === 'string') renderField(item, table, colDiv);
            });
            wrap.appendChild(colDiv);
        });
        return wrap;
    }

    function renderField(name, table, parent) {
        const field = table.fields.find((f) => f.name === name);
        if (!field) return;
        if (field.isHidden || field.dataType === 'derived') return;
        field.options = parseOptions(field);

        // Read-only renders the value, never an input: a disabled input still invites editing.
        if (formIsReadOnly || field.isReadOnly) {
            const row = document.createElement('div');
            row.className = 'baserow-readonly';
            const label = document.createElement('span');
            label.className = 'baserow-readonly-label';
            label.innerText = fieldLabel(field);
            const value = document.createElement('span');
            value.className = 'baserow-readonly-value';
            value.dataset.name = field.name;
            const raw = readOnlyData ? readOnlyData[field.name] : null;
            value.innerText =
                field.dataType === 'currency' && raw !== null && raw !== undefined && raw !== '' ?
                fmtCurrency(raw, field.currency) :
                displayValue(raw);
            row.appendChild(label);
            row.appendChild(value);
            parent.appendChild(row);
            return;
        }
        const label = document.createElement('label');
        label.innerText = fieldLabel(field) + (field.isRequired ? ' *' : '');
        const input = createFieldInput(field);

        const help = field.helpText ? document.createElement('small') : null;
        if (help) {
            help.innerText = field.helpText;
            help.id = 'help-' + field.name;
            input.setAttribute('aria-describedby', help.id);
        }

        if (input.dataset && (input.dataset.multiselect || input.dataset.standalone)) {
            parent.appendChild(label);
            parent.appendChild(input);
        } else {
            label.appendChild(input);
            parent.appendChild(label);
        }
        if (help) parent.appendChild(help);
    }

    function parseOptions(field) {
        try {
            const o = JSON.parse(field.optionsJson || '[]');
            return Array.isArray(o) ? o : o || {};
        } catch (e) {
            return [];
        }
    }

    function slugSourceField(field) {
        try {
            return JSON.parse(field.optionsJson || '{}').sourceField || '';
        } catch (e) {
            return '';
        }
    }

    function createFieldInput(field) {
        const type = field.dataType || 'text';
        let el;

        if (type === 'longtext' || type === 'markdown') {
            el = document.createElement('textarea');
            el.rows = 3;
        } else if (type === 'boolean' || type === 'checkbox') {
            el = document.createElement('input');
            el.type = 'checkbox';
        } else if (type === 'number') {
            el = document.createElement('input');
            el.type = 'number';
            el.step = 'any';
            el.dataset.kind = 'num';
        } else if (type === 'currency' || type === 'price') {
            el = document.createElement('input');
            el.type = 'number';
            el.step = '0.01';
            el.inputMode = 'decimal';
            el.placeholder = '0.00';
            el.dataset.kind = 'num';
            el.dataset.currency = field.currency || formCurrency;
        } else if (type === 'date') {
            el = document.createElement('input');
            el.type = 'date';
        } else if (type === 'datetime' || type === 'timestamp') {
            el = document.createElement('input');
            el.type = 'datetime-local';
        } else if (type === 'select') {
            el = document.createElement('select');
            const ph = document.createElement('option');
            ph.value = '';
            ph.innerText = '- Select -';
            el.appendChild(ph);
            (Array.isArray(field.options) ? field.options : []).forEach((o) => {
                const op = document.createElement('option');
                op.value = o;
                op.innerText = o;
                el.appendChild(op);
            });
        } else if (type === 'multiselect') {
            el = document.createElement('div');
            el.className = 'baserow-ms';
            el.dataset.multiselect = field.name;
            (Array.isArray(field.options) ? field.options : []).forEach((o) => {
                const lab = document.createElement('label');
                lab.className = 'baserow-ms-opt';
                const c = document.createElement('input');
                c.type = 'checkbox';
                c.value = o;
                c.dataset.name = field.name;
                lab.appendChild(c);
                lab.appendChild(document.createTextNode(' ' + o));
                el.appendChild(lab);
            });
            return el;
        } else if (type === 'file' || type === 'media') {
            el = document.createElement('input');
            el.type = 'file';
        } else if (type === 'reference' || type === 'relation') {
            return createReferenceCombobox(field);
        } else if (type === 'calculated' || type === 'formula') {
            el = document.createElement('input');
            el.type = 'text';
            el.readOnly = true;
            el.dataset.expr = field.expression;
            el.dataset.kind = 'calc';
        } else if (type === 'systemid') {
            el = document.createElement('input');
            el.type = 'text';
            el.readOnly = true;
            el.dataset.kind = 'sysid';
            el.value = generateShortId();
        } else if (type === 'email') {
            el = document.createElement('input');
            el.type = 'email';
        } else if (type === 'url') {
            el = document.createElement('input');
            el.type = 'url';
        } else if (type === 'time') {
            el = document.createElement('input');
            el.type = 'time';
        } else if (type === 'password') {
            el = document.createElement('input');
            el.type = 'password';
        } else if (type === 'slug') {
            el = document.createElement('input');
            el.type = 'text';
            el.placeholder = 'auto-generated if left blank';
        } else if (type === 'richtext') {
            el = document.createElement('textarea');
            el.rows = 4;
        } else if (type === 'json') {
            el = document.createElement('textarea');
            el.rows = 4;
            el.placeholder = '{ }';
            el.dataset.kind = 'json';
        } else if (type === 'array') {
            el = document.createElement('textarea');
            el.rows = 3;
            el.placeholder = '["a", "b"]';
            el.dataset.kind = 'json';
        } else {
            el = document.createElement('input');
            el.type = 'text';
        }

        el.dataset.name = field.name;

        // Native constraints give immediate browser feedback; the server re-checks every one.
        if (field.isRequired && type !== 'calculated' && type !== 'systemid') el.required = true;
        if (field.pattern && el.tagName === 'INPUT' && ['text', 'url', 'search', 'email', 'tel'].includes(el.type))
            el.pattern = field.pattern;
        if (field.min !== null && field.min !== undefined) {
            if (el.type === 'number') el.min = field.min;
            else if (TEXT_LENGTH_TYPES.has(type)) el.minLength = field.min;
        }
        if (field.max !== null && field.max !== undefined) {
            if (el.type === 'number') el.max = field.max;
            else if (TEXT_LENGTH_TYPES.has(type)) el.maxLength = field.max;
        }
        if (field.defaultValue && !el.value && el.type !== 'checkbox') el.value = field.defaultValue;
        if (field.defaultValue && el.type === 'checkbox') el.checked = field.defaultValue === 'true';

        return el;
    }

    // searches server-side as the visitor types; dataset.name lives on the hidden input so extractFormData needs no special case
    function createReferenceCombobox(field) {
        const wrap = document.createElement('div');
        wrap.className = 'baserow-combobox';
        wrap.dataset.standalone = 'true';

        const search = document.createElement('input');
        search.type = 'text';
        search.placeholder = 'Search…';
        search.autocomplete = 'off';

        const hidden = document.createElement('input');
        hidden.type = 'hidden';
        hidden.dataset.name = field.name;

        // a single chosen value renders as a removable chip, not editable text: there is only ever one reference, there
        // is nothing to reselect until the current pick is explicitly cleared
        const chip = document.createElement('div');
        chip.className = 'baserow-combobox-chip';
        chip.hidden = true;
        const chipLabel = document.createElement('span');
        chipLabel.className = 'baserow-combobox-chip-label';
        const chipRemove = document.createElement('button');
        chipRemove.type = 'button';
        chipRemove.className = 'baserow-combobox-chip-remove';
        chipRemove.setAttribute('aria-label', 'Remove');
        chipRemove.innerText = '×';
        chip.append(chipLabel, chipRemove);

        const list = document.createElement('ul');
        list.className = 'baserow-combobox-list';
        list.hidden = true;

        wrap.append(chip, search, hidden, list);

        let debounceTimer = null;
        let controller = null;
        let active = -1;

        function closeList() {
            list.hidden = true;
            list.innerHTML = '';
            active = -1;
        }

        function showChip(label) {
            chipLabel.innerText = label;
            chip.hidden = false;
            search.hidden = true;
            search.value = '';
        }

        function hideChip() {
            chip.hidden = true;
            search.hidden = false;
        }

        function selectOption(id, label) {
            hidden.value = id;
            hidden.dispatchEvent(new Event('change', {
                bubbles: true
            }));
            showChip(label);
            search.classList.remove('baserow-invalid');
            closeList();
        }

        chipRemove.addEventListener('click', (e) => {
            e.preventDefault();
            hidden.value = '';
            hidden.dispatchEvent(new Event('change', {
                bubbles: true
            }));
            hideChip();
            search.focus();
        });

        function renderOptions(rows) {
            list.innerHTML = '';
            if (!rows.length) {
                const li = document.createElement('li');
                li.className = 'baserow-combobox-empty';
                li.innerText = 'No matches.';
                list.appendChild(li);
            } else {
                rows.forEach((r) => {
                    const li = document.createElement('li');
                    li.innerText = r.label;
                    // mousedown, not click: it fires before the search input's blur, the list is still open to read from.
                    li.addEventListener('mousedown', (e) => {
                        e.preventDefault();
                        selectOption(r.id, r.label);
                    });
                    list.appendChild(li);
                });
            }
            active = -1;
            list.hidden = false;
        }

        search.addEventListener('input', () => {
            if (hidden.value) {
                hidden.value = '';
                hidden.dispatchEvent(new Event('change', {
                    bubbles: true
                }));
            }
            clearTimeout(debounceTimer);
            const query = search.value.trim();
            if (!query) {
                closeList();
                return;
            }
            debounceTimer = setTimeout(() => {
                if (controller) controller.abort();
                controller = new AbortController();
                fetch(`${apiBase}/api/forms/${formId}/reference/${encodeURIComponent(field.name)}?q=${encodeURIComponent(query)}`, {
                        signal: controller.signal,
                    })
                    .then((r) => r.json())
                    .then((data) => renderOptions((data && data.rows) || []))
                    .catch((err) => {
                        if (err.name !== 'AbortError') closeList();
                    });
            }, 250);
        });

        search.addEventListener('keydown', (e) => {
            const items = Array.from(list.children).filter((li) => !li.classList.contains('baserow-combobox-empty'));
            if (e.key === 'ArrowDown' && items.length) {
                e.preventDefault();
                active = (active + 1) % items.length;
            } else if (e.key === 'ArrowUp' && items.length) {
                e.preventDefault();
                active = (active - 1 + items.length) % items.length;
            } else if (e.key === 'Enter') {
                if (active >= 0 && items[active]) {
                    e.preventDefault();
                    items[active].dispatchEvent(new Event('mousedown'));
                }
                return;
            } else if (e.key === 'Escape') {
                closeList();
                return;
            } else {
                return;
            }
            items.forEach((li, i) => li.classList.toggle('active', i === active));
            if (items[active]) items[active].scrollIntoView({
                block: 'nearest'
            });
        });

        search.addEventListener('blur', () => setTimeout(closeList, 100));

        return wrap;
    }

    function extractFormData() {
        const groups = {};
        formEl.querySelectorAll('input, select, textarea').forEach((el) => {
            if (!el.dataset.name) return;
            (groups[el.dataset.name] = groups[el.dataset.name] || []).push(el);
        });
        const data = {};
        Object.keys(groups).forEach((name) => {
            const els = groups[name];
            if (els.length === 1) {
                const el = els[0];
                if (el.type === 'checkbox') data[name] = el.checked;
                else if (el.type === 'file') data[name] = el.files[0] || null;
                else if (el.dataset.kind === 'num' || el.dataset.kind === 'calc') {
                    const v = el.value;
                    data[name] = v !== '' && !isNaN(Number(v)) ? Number(v) : v;
                } else if (el.dataset.kind === 'json') {
                    if (el.value === '') data[name] = '';
                    else {
                        try {
                            data[name] = JSON.parse(el.value);
                        } catch (e) {
                            data[name] = el.value; // not valid json, let the server reject it
                        }
                    }
                } else data[name] = el.value;
            } else {
                data[name] = els.filter((el) => el.type === 'checkbox' && el.checked).map((el) => el.value);
            }
        });
        return data;
    }

    // multiselect arrays become repeated keys, json/array fields become one JSON-stringified entry, rest as-is
    function toFormData(data) {
        const fd = new FormData();
        const compound = new Set((tableSchema.fields || []).filter((f) => f.dataType === 'json' || f.dataType === 'array').map((f) => f.name));
        Object.keys(data).forEach((name) => {
            const v = data[name];
            if (v === null || v === undefined || v === '') return;
            if (compound.has(name)) fd.append(name, JSON.stringify(v));
            else if (Array.isArray(v)) v.forEach((item) => fd.append(name, item));
            else if (typeof v === 'boolean') fd.append(name, String(v));
            else fd.append(name, v);
        });
        return fd;
    }

    // SUM(Field, 'Column') mirrors the server-side JsExpr grammar: a bare field-name argument, not data.Field.
    // Rewritten to a normal property access here so a plain `new Function` sees it, not a free identifier.
    function sumOverColumn(arr, col) {
        return (Array.isArray(arr) ? arr : []).reduce((total, row) => total + (Number(row && row[col]) || 0), 0);
    }

    function safeEval(expr, data) {
        const sData = new Proxy(data || {}, {
            get: (t, p) => (t[p] === undefined ? '' : t[p])
        });
        try {
            const rewritten = (expr || '').replace(/\bSUM\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,/g, 'SUM(data.$1,');
            const val = new Function('data', 'SUM', `return ${rewritten}`)(sData, sumOverColumn);
            if (typeof val === 'number' && !Number.isInteger(val)) return Math.round(val * 100) / 100;
            return val;
        } catch (e) {
            return '';
        }
    }

    // Intl places the symbol where locale and currency demand, amounts read correctly.
    function fmtCurrency(n, code) {
        const num = Number(n);
        if (isNaN(num)) return '';
        const currency = code || formCurrency || 'EUR';
        try {
            return new Intl.NumberFormat(navigator.language || 'en', {
                style: 'currency',
                currency
            }).format(num);
        } catch (e) {
            return currency + ' ' + num.toFixed(2);
        }
    }

    function triggerReactiveUpdate() {
        if (!formEl) return;
        const data = extractFormData();
        formEl.querySelectorAll('input[data-expr], .baserow-subtotal-value[data-expr]').forEach((el) => {
            if (el.dataset.expr === 'GETDATE()') {
                el.value = new Date().toISOString().split('T')[0];
                return;
            }
            const val = safeEval(el.dataset.expr, data);
            if (el.classList.contains('baserow-subtotal-value')) {
                el.innerText =
                    el.dataset.format === 'currency' && typeof val === 'number' ? fmtCurrency(val, el.dataset.currency) : val;
            } else if (el.dataset.kind === 'num') el.value = val === '' ? '' : val;
            else el.value = val;
        });
    }

    // The validation result pairs each message with the storage name of the field it belongs to, a failing field can be painted red, not just complained about.
    function markInvalid(names) {
        (names || []).forEach((name) => {
            formEl.querySelectorAll('input, select, textarea').forEach((el) => {
                if (el.dataset.name === name) el.classList.add('baserow-invalid');
            });
        });
    }

    function validate() {
        const errors = [];
        const invalid = [];
        const els = formEl.querySelectorAll('input, select, textarea');
        (tableSchema.fields || []).forEach((field) => {
            if (
                field.dataType === 'calculated' ||
                field.dataType === 'systemid' ||
                field.dataType === 'derived' ||
                field.isHidden
            )
                return;
            const group = Array.from(els).filter((el) => el.dataset.name === field.name);
            if (!group.length) return;
            if (field.dataType === 'boolean') {
                if (field.isRequired && !group.some((el) => el.type === 'checkbox' && el.checked)) {
                    errors.push(field.name + ' is required.');
                    invalid.push(field.name);
                }
                return;
            }
            const empty = group.every((el) => (el.type === 'checkbox' ? !el.checked : el.value === ''));
            if (empty) {
                // a slug with a source field auto-fills server-side when left blank
                const slugAutoFills = field.dataType === 'slug' && slugSourceField(field);
                if (field.isRequired && !slugAutoFills) {
                    errors.push(field.name + ' is required.');
                    invalid.push(field.name);
                }
                return;
            }
            if (field.dataType === 'json') {
                try {
                    const parsed = JSON.parse(group[0].value);
                    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) throw 0;
                } catch (e) {
                    errors.push(field.name + ' must be a JSON object.');
                    invalid.push(field.name);
                    return;
                }
            }
            if (field.dataType === 'array') {
                try {
                    const parsed = JSON.parse(group[0].value);
                    if (!Array.isArray(parsed) || parsed.some((x) => x !== null && typeof x === 'object')) throw 0;
                } catch (e) {
                    errors.push(field.name + ' must be a JSON array of text/number/boolean values.');
                    invalid.push(field.name);
                    return;
                }
            }
            if (field.pattern) {
                try {
                    if (!group.every((el) => el.type === 'checkbox' || new RegExp(field.pattern).test(el.value))) {
                        errors.push(field.name + ' does not match the required format.');
                        invalid.push(field.name);
                    }
                } catch (e) {}
            }
            if (
                (field.dataType === 'number' || field.dataType === 'currency') &&
                !group.every((el) => el.value === '' || !isNaN(Number(el.value)))
            ) {
                errors.push(field.name + ' must be a number.');
                invalid.push(field.name);
            }
            if (field.dataType === 'select') {
                const sel = group.find((el) => el.tagName === 'SELECT');
                if (sel && sel.value && !(field.options || []).includes(sel.value)) {
                    errors.push(field.name + ' has an invalid selection.');
                    invalid.push(field.name);
                }
            }
            if (field.dataType === 'multiselect') {
                const chosen = group.filter((el) => el.type === 'checkbox' && el.checked).map((el) => el.value);
                if (chosen.some((v) => !(field.options || []).includes(v))) {
                    errors.push(field.name + ' has an invalid selection.');
                    invalid.push(field.name);
                }
            }
        });
        return {
            errors,
            invalid
        };
    }

    function generateShortId(len) {
        len = len || 10;
        const chars = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-';
        const arr = new Uint8Array(len);
        if (typeof crypto !== 'undefined' && crypto.getRandomValues) crypto.getRandomValues(arr);
        else
            for (let i = 0; i < len; i++) arr[i] = Math.floor(Math.random() * 256);
        let out = '';
        for (let i = 0; i < len; i++) out += chars[arr[i] % chars.length];
        return out;
    }
})();