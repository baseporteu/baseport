const ui = (() => {
    /* toasts */

    const ICONS = {
        success: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="m20 6-11 11-5-5"/></svg>',
        error: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6 6 18M6 6l12 12"/></svg>',
        info: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 16v-5M12 8h.01"/><circle cx="12" cy="12" r="9"/></svg>',
        copy: '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="12" height="12" x="8" y="8" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
        dismiss: '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6 6 18M6 6l12 12"/></svg>',
    };

    function host() {
        let el = document.getElementById('toasts');
        if (!el) {
            el = document.createElement('div');
            el.id = 'toasts';
            el.className = 'toasts';
            el.setAttribute('role', 'status');
            el.setAttribute('aria-live', 'polite');
            document.body.appendChild(el);
        }
        return el;
    }

    const TOAST_MAX_MS = 60000;

    function toast(message, kind = 'info', timeout = 4500) {
        const text = Array.isArray(message) ? message.join(' ') : String(message ?? '');
        if (!text.trim()) return;

        const el = document.createElement('div');
        el.className = `toast toast-${kind}`;
        el.innerHTML = `<span class="toast-icon">${ICONS[kind] || ICONS.info}</span><span class="toast-text"></span>`;
        el.querySelector('.toast-text').textContent = text;

        el.onclick = () => copy(el, text);

        const actions = document.createElement('div');
        actions.className = 'toast-actions';
        actions.append(
            toastButton('Copy', ICONS.copy, () => copy(el, text)),
            toastButton('Dismiss', ICONS.dismiss, () => dismiss(el)),
        );
        el.appendChild(actions);

        host().appendChild(el);
        setTimeout(() => dismiss(el), kind === 'error' || !timeout ? TOAST_MAX_MS : Math.min(timeout, TOAST_MAX_MS));
        return el;
    }

    function toastButton(label, icon, onClick) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'toast-btn';
        btn.title = label;
        btn.setAttribute('aria-label', label);
        btn.innerHTML = icon;
        btn.onclick = (ev) => {
            ev.stopPropagation();
            onClick();
        };
        return btn;
    }

    async function copy(el, text) {
        try {
            await navigator.clipboard.writeText(text);
        } catch (e) {
            const range = document.createRange();
            range.selectNodeContents(el.querySelector('.toast-text'));
            const selection = getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            return;
        }
        const icon = el.querySelector('.toast-icon');
        const kindIcon = icon.innerHTML;
        icon.innerHTML = ICONS.success;
        el.classList.add('toast-copied');
        setTimeout(() => {
            icon.innerHTML = kindIcon;
            el.classList.remove('toast-copied');
        }, 1200);
    }

    function dismiss(el) {
        if (!el || !el.parentNode) return;
        el.classList.add('toast-out');
        setTimeout(() => el.remove(), 160);
    }

    async function copyValue(text) {
        const value = String(text ?? '').trim();
        if (!value) return false;
        try {
            await navigator.clipboard.writeText(value);
        } catch (e) {
            toast('Could not reach the clipboard. Select the value and press Ctrl+C.', 'error');
            return false;
        }
        toast(`Copied ${value.length > 48 ? value.slice(0, 48) + '...' : value}`, 'success', 2000);
        return true;
    }

    function switchTrack(input) {
        const track = el('span', 'switch');
        track.append(input, el('span', 'track'), el('span', 'thumb'));
        return track;
    }

    function switchRow(label, {
        id,
        checked = false,
        disabled = false
    } = {}) {
        const row = el('label', 'switch-row');
        const input = el('input', '', {
            type: 'checkbox'
        });
        if (id) input.id = id;
        input.checked = !!checked;
        input.disabled = !!disabled;
        row.append(switchTrack(input), el('span', null, {
            textContent: label
        }));
        row.ctrl = input;
        return row;
    }

    function copyable(block, text) {
        const wrap = el('div', 'copy-wrap');
        const btn = el('button', 'copy-btn', {
            type: 'button',
            title: 'Copy'
        });
        btn.setAttribute('aria-label', 'Copy');
        btn.innerHTML = ICONS.copy;
        btn.onclick = () => copyValue(typeof text === 'function' ? text() : text);
        wrap.append(block, btn);
        return wrap;
    }

    document.addEventListener('dblclick', (ev) => {
        const cell = ev.target.closest && ev.target.closest('.table td');
        if (!cell || cell.querySelector('button, input, select, textarea, a')) return;
        copyValue(cell.innerText);
    });

    /* a unified single response handler */

    function markInvalid(names, scope) {
        const root = scope || document.querySelector('.sheet') || document;
        root.querySelectorAll('.input-invalid').forEach((el) => el.classList.remove('input-invalid'));
        (names || []).forEach((name) => {
            const el = root.querySelector(`[data-field="${String(name).toLowerCase()}"]`);
            if (el) el.classList.add('input-invalid');
        });
    }

    async function handle(res, {
        success,
        failure = 'Something went wrong.'
    } = {}) {
        let body = null;
        try {
            body = await res.json();
        } catch (e) {
            /* empty body is fine */
        }

        if (!res.ok) {
            const errors = body && body.errors && body.errors.length ? body.errors : [failure];
            toast(errors, 'error');
            if (body && body.invalid && body.invalid.length) markInvalid(body.invalid);
            return null;
        }
        if (success) toast(success, 'success');
        markInvalid([]);
        return body ?? {};
    }

    // fetch + handle, for the common case.
    async function send(url, {
        method = 'GET',
        body,
        success,
        failure
    } = {}) {
        const init = {
            method
        };
        if (body !== undefined) {
            init.headers = {
                'Content-Type': 'application/json'
            };
            init.body = JSON.stringify(body);
        }
        return handle(await fetch(url, init), {
            success,
            failure
        });
    }

    /* uncaught failures */

    let lastError = '';

    function reportError(source, error) {
        const message = (error && (error.message || error)) || 'Unknown error';
        const frame = (error && error.stack || '').split('\n')[1];
        const text = `${source}: ${message}` + (frame ? ` (${frame.trim()})` : '');
        if (text === lastError) return;
        lastError = text;
        setTimeout(() => {
            lastError = '';
        }, 2000);

        console.error(source, error);
        toast(text, 'error');
        sendError(text);
    }

    function sendError(text) {
        if (!navigator.sendBeacon) return;
        try {
            navigator.sendBeacon('/api/client-errors', new Blob([JSON.stringify({
                message: text,
                page: location.pathname,
            })], {
                type: 'application/json'
            }));
        } catch (e) {
        }
    }

    window.addEventListener('error', (ev) => {
        const where = ev.filename ? `${ev.filename.split('/').pop()}:${ev.lineno}` : 'script';
        reportError(where, ev.error || ev.message);
    });

    window.addEventListener('unhandledrejection', (ev) => reportError('Unhandled rejection', ev.reason));

    /* theme */

    const THEME_KEY = 'baseport.theme';

    function theme() {
        return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
    }

    function themeChoice() {
        try {
            const stored = localStorage.getItem(THEME_KEY);
            return stored === 'dark' || stored === 'light' ? stored : 'system';
        } catch (e) {
            return 'system';
        }
    }

    function systemTheme() {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function setTheme(next, {
        remember = true
    } = {}) {
        const system = next === 'system';
        document.documentElement.dataset.theme = system ? systemTheme() : next === 'dark' ? 'dark' : 'light';
        if (!remember) return;
        try {
            if (system) localStorage.removeItem(THEME_KEY);
            else localStorage.setItem(THEME_KEY, next);
        } catch (e) {
            /* private mode: this session only */
        }
    }

    function toggleTheme() {
        setTheme(theme() === 'dark' ? 'light' : 'dark');
    }

    if (window.matchMedia) {
        const system = window.matchMedia('(prefers-color-scheme: dark)');
        const follow = (ev) => {
            let stored = null;
            try {
                stored = localStorage.getItem(THEME_KEY);
            } catch (e) {}
            if (!stored) setTheme(ev.matches ? 'dark' : 'light', {
                remember: false
            });
        };
        if (system.addEventListener) system.addEventListener('change', follow);
        else if (system.addListener) system.addListener(follow);
    }

    function formatNums(root) {
        root.querySelectorAll('.num[data-n]').forEach((el) => {
            const raw = el.dataset.n;
            const decimals = raw.includes('.') ? raw.split('.')[1].length : 0;
            el.textContent = new Intl.NumberFormat(navigator.language || undefined, {
                minimumFractionDigits: decimals,
                maximumFractionDigits: decimals,
            }).format(Number(raw));
        });
    }

    async function fragment(targetId, url, options = {}) {
        const target = document.getElementById(targetId);
        if (!target) return null;
        const failure = options.failure || 'Could not load the list.';
        try {
            const res = await fetch(
                url,
                options.body === undefined ?
                undefined : {
                    method: options.method || 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(options.body),
                },
            );
            if (!res.ok) {
                if (options.onError) {
                    const data = await res.json().catch(() => ({}));
                    options.onError((data.errors || [failure]).join(' '));
                } else {
                    await handle(res, {
                        failure
                    });
                }
                return null;
            }
            // server-rendered and server-escaped; see Api/Html.cs.
            target.innerHTML = await res.text();
            formatNums(target);
            const n = (h) => Number(res.headers.get(h) || 0);
            return {
                page: n('X-Page'),
                pageSize: n('X-Page-Size'),
                total: n('X-Total'),
                totalPages: n('X-Total-Pages'),
                hasMore: res.headers.get('X-Has-More') === '1',
                countExact: res.headers.get('X-Count-Exact') !== '0',
                header: (h) => res.headers.get(h),
            };
        } catch (e) {
            if (options.onError) options.onError(failure);
            else toast(failure, 'error');
            return null;
        }
    }

    /* controls */

    function escape(s) {
        return String(s ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function el(tag, className, props = {}) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        Object.assign(node, props);
        return node;
    }

    function field(label, {
        id,
        type = 'text',
        value = '',
        placeholder = '',
        help = '',
        options,
        mono,
        rows = 3,
        name
    } = {}) {
        const wrap = el('label', 'field');
        wrap.append(el('span', 'field-label-text', {
            textContent: label
        }));

        let input;
        if (type === 'textarea') {
            input = el('textarea', 'input' + (mono ? ' mono' : ''), {
                rows
            });
        } else if (type === 'select') {
            input = el('select', 'input');
            (options || []).forEach(([v, l]) => input.append(el('option', null, {
                value: v,
                textContent: l
            })));
        } else {
            input = el('input', type === 'checkbox' ? '' : 'input' + (mono ? ' mono' : ''), {
                type
            });
        }
        if (id) input.id = id;
        if (name) input.dataset.field = name.toLowerCase();
        if (placeholder) input.placeholder = placeholder;
        if (type === 'checkbox') input.checked = !!value;
        else input.value = value ?? '';

        wrap.append(type === 'checkbox' ? switchTrack(input) : input);
        if (help) wrap.append(el('span', 'field-help', {
            textContent: help
        }));
        wrap.ctrl = input;
        return wrap;
    }

    function combobox(label, {
        id,
        value = '',
        valueLabel = '',
        placeholder = '',
        help = '',
        fetchOptions,
        browseAll = false
    } = {}) {
        const wrap = el('label', 'field combobox');
        if (label) wrap.append(el('span', 'field-label-text', {
            textContent: label
        }));

        const box = el('div', 'combobox-box');
        const search = el('input', 'input' + (value ? ' hidden' : ''), {
            type: 'text',
            placeholder,
            autocomplete: 'off',
        });
        const hidden = el('input', null, {
            type: 'hidden',
            value
        });
        if (id) hidden.id = id;

        const chip = el('div', 'combobox-chip' + (value ? '' : ' hidden'));
        const chipLabel = el('span', 'combobox-chip-label', {
            textContent: valueLabel || value
        });
        const chipRemove = el('button', 'combobox-chip-remove', {
            type: 'button',
            textContent: '×'
        });
        chipRemove.setAttribute('aria-label', 'Remove');
        chip.append(chipLabel, chipRemove);

        const spinner = el('span', 'btn-spinner combobox-spinner hidden');
        const list = el('ul', 'combobox-list hidden');
        box.append(chip, search, hidden, spinner, list);
        wrap.append(box);
        if (help) wrap.append(el('span', 'field-help', {
            textContent: help
        }));
        wrap.ctrl = hidden;

        let controller = null;
        let debounceTimer = null;
        let active = -1;

        function closeList() {
            list.classList.add('hidden');
            list.innerHTML = '';
            active = -1;
        }

        function markValidity() {
            search.classList.toggle('input-invalid', search.value.trim() !== '' && !hidden.value);
        }

        function showChip(label) {
            chipLabel.textContent = label;
            chip.classList.remove('hidden');
            search.classList.add('hidden');
            search.value = '';
        }

        function hideChip() {
            chip.classList.add('hidden');
            search.classList.remove('hidden');
        }

        let pendingValue = null;
        let pendingLabel = null;

        function selectOption(v, l) {
            hidden.value = v;
            hidden.dispatchEvent(new Event('change', {
                bubbles: true
            }));
            showChip(l);
            closeList();
            markValidity();
            pendingValue = null;
            pendingLabel = null;
        }

        chipRemove.addEventListener('click', (e) => {
            e.preventDefault();
            if (search.disabled) return; // locked by the caller (e.g. a systemid field's type can't be changed)
            pendingValue = hidden.value;
            pendingLabel = chipLabel.textContent;
            hidden.value = '';
            hidden.dispatchEvent(new Event('change', {
                bubbles: true
            }));
            hideChip();
            search.classList.remove('input-invalid'); // an explicit clear, not an error
            search.focus();
        });

        function renderOptions(rows) {
            list.innerHTML = '';
            if (!rows.length) {
                list.append(el('li', 'combobox-empty', {
                    textContent: 'No matches.'
                }));
            } else {
                rows.forEach((r) => {
                    if (r.group) {
                        list.append(el('li', 'combobox-group', {
                            textContent: r.label
                        }));
                        return;
                    }
                    const li = el('li', 'combobox-option' + (String(r.id) === String(hidden.value) && hidden.value !== '' ? ' selected' : ''), {
                        textContent: r.label
                    });
                    li.addEventListener('mousedown', (e) => {
                        e.preventDefault();
                        selectOption(r.id, r.label);
                    });
                    list.append(li);
                });
            }
            active = -1;
            list.classList.remove('hidden');
            const selected = list.querySelector('.combobox-option.selected');
            if (selected) selected.scrollIntoView({
                block: 'nearest'
            });
        }

        function runSearch(query) {
            if (controller) controller.abort();
            controller = new AbortController();
            spinner.classList.remove('hidden');
            Promise.resolve(fetchOptions(query, controller.signal))
                .then((rows) => {
                    spinner.classList.add('hidden');
                    renderOptions(rows || []);
                })
                .catch((err) => {
                    if (err && err.name === 'AbortError') return;
                    spinner.classList.add('hidden');
                });
        }

        search.addEventListener('input', () => {
            if (hidden.value) {
                hidden.value = '';
                hidden.dispatchEvent(new Event('change', {
                    bubbles: true
                }));
            }
            search.classList.remove('input-invalid'); // reds only on blur, not while still typing
            clearTimeout(debounceTimer);
            const query = search.value.trim();
            if (!query) {
                closeList();
                return;
            }
            debounceTimer = setTimeout(() => runSearch(query), 250);
        });

        search.addEventListener('keydown', (e) => {
            const options = Array.from(list.children).filter((li) => li.classList.contains('combobox-option'));
            if (e.key === 'ArrowDown' && options.length) {
                e.preventDefault();
                active = (active + 1) % options.length;
            } else if (e.key === 'ArrowUp' && options.length) {
                e.preventDefault();
                active = (active - 1 + options.length) % options.length;
            } else if (e.key === 'Enter') {
                const idx = active >= 0 ? active : (search.value.trim() ? 0 : -1);
                if (idx >= 0 && options[idx]) {
                    e.preventDefault();
                    options[idx].dispatchEvent(new Event('mousedown'));
                }
                return;
            } else if (e.key === 'Escape') {
                closeList();
                return;
            } else {
                return;
            }
            options.forEach((li, i) => li.classList.toggle('active', i === active));
            if (options[active]) options[active].scrollIntoView({
                block: 'nearest'
            });
        });

        search.addEventListener('blur', () => {
            setTimeout(() => {
                closeList();
                if (!hidden.value && pendingValue) {
                    hidden.value = pendingValue;
                    hidden.dispatchEvent(new Event('change', {
                        bubbles: true
                    }));
                    showChip(pendingLabel);
                    pendingValue = null;
                    pendingLabel = null;
                }
                markValidity();
            }, 100);
        });

        if (browseAll) {
            search.addEventListener('focus', () => runSearch(''));
            search.addEventListener('mousedown', () => {
                if (document.activeElement === search && list.classList.contains('hidden')) runSearch('');
            });
        }

        return wrap;
    }

    function button(label, onClick, {
        variant = '',
        size = '',
        type = 'button',
        title
    } = {}) {
        const b = el('button', ['btn', variant, size].filter(Boolean).join(' '), {
            type,
            textContent: label
        });
        if (title) b.title = title;
        if (onClick) b.onclick = onClick;
        return b;
    }

    async function busy(btn, fn) {
        if (!btn) return fn();
        const original = btn.innerHTML;
        const wasDisabled = btn.disabled;
        btn.disabled = true;
        btn.innerHTML = '<span class="btn-spinner"></span>';
        try {
            return await fn();
        } finally {
            if (document.body.contains(btn)) {
                btn.disabled = wasDisabled;
                btn.innerHTML = original;
            }
        }
    }

    /* overlays */

    let sheetDirty = false;

    function markSheetDirty() {
        sheetDirty = true;
    }

    function attemptCloseSheet() {
        if (sheetDirty) {
            toast('You have unsaved changes. Use Cancel to discard them.', 'info');
            return;
        }
        closeSheet();
    }

    function sheet(title, bodyEl, actionsEl) {
        closeSheet();
        sheetDirty = false;
        const overlay = el('div', 'overlay', {
            id: 'sheetOverlay'
        });
        overlay.onclick = attemptCloseSheet;

        const panel = el('div', 'sheet');
        const head = el('div', 'sheet-head');
        head.append(
            el('h3', null, {
                textContent: title
            }),
            button('×', attemptCloseSheet, {
                variant: 'btn-ghost',
                size: 'btn-sm'
            }),
        );

        const body = el('div', 'sheet-body');
        body.append(bodyEl);
        body.addEventListener('input', markSheetDirty);
        body.addEventListener('change', markSheetDirty);

        panel.append(head, body);
        if (actionsEl) {
            const actions = el('div', 'sheet-actions');
            actions.append(actionsEl);
            panel.append(actions);
        }
        document.body.append(overlay, panel);
        requestAnimationFrame(() => {
            overlay.classList.add('open');
            panel.classList.add('open');
        });
        document.addEventListener('keydown', escapeToClose);
        const first = panel.querySelector('input, select, textarea, button');
        if (first) first.focus();
        return panel;
    }

    function closeSheet() {
        document.getElementById('sheetOverlay')?.remove();
        document.querySelector('.sheet')?.remove();
        document.removeEventListener('keydown', escapeToClose);
        sheetDirty = false;
    }

    function escapeToClose(ev) {
        if (ev.key === 'Escape') attemptCloseSheet();
    }

    function renderModal(title, bodyEl, actionsEl) {
        closeModal();
        const overlay = el('div', 'modal-overlay');
        overlay.onclick = closeModal;

        const panel = el('div', 'modal');
        panel.addEventListener('click', (ev) => ev.stopPropagation());
        panel.append(
            el('h3', 'select-none', {
                textContent: title
            }),
        );
        
        const body = el('div', 'modal-body');
        body.append(bodyEl);
        panel.append(body);
        if (actionsEl) {
            const actions = el('div', 'modal-actions');
            actions.append(actionsEl);
            panel.append(actions);
        }
        overlay.append(panel);
        document.body.append(overlay);
        requestAnimationFrame(() => overlay.classList.add('open'));
        document.addEventListener('keydown', escapeModalToClose);
        const first = panel.querySelector('input, select, textarea, button');
        if (first) first.focus();
        return panel;
    }

    function closeModal() {
        document.querySelector('.modal-overlay')?.remove();
        document.removeEventListener('keydown', escapeModalToClose);
    }

    function escapeModalToClose(ev) {
        if (ev.key === 'Escape') closeModal();
    }

    function ask({
        title,
        label,
        value = '',
        placeholder = '',
        confirmLabel = 'Save',
        type = 'text',
        help = ''
    }) {
        return new Promise((resolve) => {
            const body = el('div');
            const input = field(label, {
                value,
                placeholder,
                type,
                help
            });
            body.append(input);

            let settled = false;
            const done = (result) => {
                if (!settled) {
                    settled = true;
                    closeSheet();
                    resolve(result);
                }
            };

            const actions = el('div', 'row');
            actions.append(
                button('Cancel', () => done(null), {
                    variant: 'btn-outline'
                }),
                button(confirmLabel, () => done(input.ctrl.value.trim() || null)),
            );
            input.ctrl.addEventListener('keydown', (ev) => {
                if (ev.key === 'Enter') {
                    ev.preventDefault();
                    done(input.ctrl.value.trim() || null);
                }
            });

            sheet(title, body, actions);
            input.ctrl.focus();
            input.ctrl.select();
        });
    }

    let instanceZone = null;
    let whenFormat = null;

    function timeZone(zone) {
        if (zone === undefined) return instanceZone;
        instanceZone = zone || null;
        whenFormat = null;
        return instanceZone;
    }

    function whenFormatter() {
        if (whenFormat) return whenFormat;
        const parts = {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
            hour: '2-digit',
            minute: '2-digit'
        };
        const local = Intl.DateTimeFormat().resolvedOptions().timeZone;
        if (instanceZone && instanceZone !== local) {
            parts.timeZone = instanceZone;
            parts.timeZoneName = 'short';
        }
        try {
            whenFormat = new Intl.DateTimeFormat(navigator.language || undefined, parts);
        } catch (e) {
            delete parts.timeZone;
            delete parts.timeZoneName;
            whenFormat = new Intl.DateTimeFormat(navigator.language || undefined, parts);
        }
        return whenFormat;
    }

    function when(iso) {
        if (!iso) return '';
        const d = new Date(iso);
        return isNaN(d) ? String(iso) : whenFormatter().format(d);
    }

    function currencyOptions() {
        const codes = Intl.supportedValuesOf ? Intl.supportedValuesOf('currency') : [];
        let names = null;
        try {
            names = new Intl.DisplayNames(navigator.language || 'en', {
                type: 'currency'
            });
        } catch (e) {}
        return codes.map((code) => {
            const name = names ? names.of(code) : code;
            return [code, name && name !== code ? `${code} - ${name}` : code];
        });
    }

    function timeZoneOptions() {
        const zones = Intl.supportedValuesOf ? Intl.supportedValuesOf('timeZone') : [];
        return ['UTC'].concat(zones.filter((z) => z !== 'UTC')).map((z) => [z, z]);
    }

    function fillOptions(select, options, value) {
        const list = options.slice();
        if (value && !list.some(([v]) => v === value)) list.unshift([value, value]);
        select.innerHTML = '';
        list.forEach(([v, label]) => select.append(el('option', null, {
            value: v,
            textContent: label
        })));
        select.value = value || (list[0] ? list[0][0] : '');
    }

    function confirm({
        title,
        message,
        confirmLabel = 'Confirm',
        cancelLabel = 'Cancel',
        danger = false
    }) {
        return new Promise((resolve) => {
            const body = el('p', 'muted', {
                textContent: message
            });
            const actions = el('div', 'row');
            const close = closeModal;
            actions.append(
                button(
                    cancelLabel,
                    () => {
                        close();
                        resolve(false);
                    }, {
                        variant: 'btn-outline'
                    },
                ),
                button(
                    confirmLabel,
                    () => {
                        close();
                        resolve(true);
                    }, {
                        variant: danger ? 'btn-danger' : ''
                    },
                ),
            );
            renderModal(title, body, actions);
        });
    }

    return {
        toast,
        dismiss,
        copyValue,
        copyable,
        switchRow,
        handle,
        markInvalid,
        send,
        fragment,
        escape,
        el,
        field,
        combobox,
        button,
        busy,
        sheet,
        closeSheet,
        modal: renderModal,
        closeModal,
        confirm,
        ask,
        when,
        timeZone,
        currencyOptions,
        timeZoneOptions,
        fillOptions,
        theme,
        themeChoice,
        setTheme,
        toggleTheme,
    };
})();

if (typeof window !== 'undefined') window.ui = ui;

if (typeof window !== 'undefined') window.toggleTheme = ui.toggleTheme;