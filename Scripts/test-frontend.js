/* Frontend checks: node Scripts/test-frontend.js

   Covers the decision logic that has actually broken in production, each case
   named after the bug it would have caught. Not a browser test: no layout, no
   real events, just the branches that decide what gets rendered. */

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const {
    install
} = require('./dom-stub');

const wwwroot = path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot');
// The console is split across files that share one scope; tests read them all.
const ADMIN_SCRIPTS = ['ui.js', 'js/core.js', 'js/proxy.js', 'js/tables.js', 'js/records.js',
    'js/sidebar.js', 'js/schema.js', 'js/sql.js', 'js/accounts.js',
    'js/settings.js', 'forms.js', 'js/auth.js'
];
const readAll = () => ADMIN_SCRIPTS.map(read).join('\n');

// The console page is composed server-side from these parts, in this order.
// Tests read the same list, so a part added to one and not the other shows up.
const HTML_PARTS = ['admin/_shell.html', 'admin/views/tables.html', 'admin/views/forms.html',
    'admin/views/sql.html', 'admin/views/schema.html', 'admin/views/auth.html',
    'admin/views/logs.html', 'admin/views/settings.html', 'admin/_footer.html'
];
// The login card is a separate page that never loads the console scripts.
const AUTH_PART = ['admin/_auth.html'];
const readHtml = () => [...HTML_PARTS, ...AUTH_PART].map(read).join('\n');
const read = f => fs.readFileSync(path.join(wwwroot, f), 'utf8');

let passed = 0,
    failed = 0;

function test(name, fn) {
    try {
        fn();
        console.log(`  ok   ${name}`);
        passed++;
    } catch (e) {
        console.log(`  FAIL ${name}\n       ${e.message}`);
        failed++;
    }
}

/* forms.js: which panel the editor shows */

function loadFormsModule() {
    const ids = ['kindSubmit', 'kindLookup', 'kindList', 'formKinds', 'formActions',
        'formKindHint', 'formKindBadge', 'formProxyNote', 'listSortField', 'listSortDir',
        'listPageSize', 'lookupNotFound', 'listFilters', 'listPalette', 'listCanvas',
        'formTable', 'formLayout', 'layoutCanvas', 'paletteFields', 'submitInactiveHint',
        'lookupMatchFields', 'listSearchFields', 'lookupResultPalette', 'lookupResultCanvas',
        'lookupOnboardNav', 'lookupOnboardBack', 'lookupOnboardNext', 'lookupOnboardSkip',
        'lookupStepMatch', 'lookupStepShow', 'lookupStepNotFound', 'formSuccessRedirect',
    ];
    const dom = install(ids);
    global.ui = {
        toast() {},
        el: (t, c) => {
            const e = dom.element(t);
            if (c) e.className = c;
            return e;
        }
    };
    global.escapeHtml = s => String(s == null ? '' : s);
    global.currentTables = [];
    global.refreshSidebar = () => {};
    global.navigate = () => {};
    global.renderCanvas = () => {};
    global.renderPalette = () => {};

    const src = read('forms.js')
        .replace(/^\(function wireListCanvasDrop[\s\S]*?\}\)\(\);/m, '')
        .replace(/^\(function wireLookupResultCanvasDrop[\s\S]*?\}\)\(\);/m, '')
        .replace(/^document\.getElementById\('formLayout'\)[\s\S]*?\}\);/m, '')
        .replace(/^document\.querySelectorAll\('\.builder-palette[\s\S]*?\}\);/m, '')
        .replace(/^\(function wireSuccessRedirectTest[\s\S]*?\}\)\(\);/m, '');
    const module = {
        applyFormShape: null,
        normalizeActions: null
    };
    eval(src + `
;module.applyFormShape = applyFormShape;
module.normalizeActions = normalizeActions;
module.applyKindConfig = applyKindConfig;
module.filterRow = filterRow;
module.collectListFilters = collectListFilters;
module.setTableFields = (f) => { formTableFields = f; };
module.lookupOnboardNext = lookupOnboardNext;
module.lookupOnboardBack = lookupOnboardBack;
module.lookupOnboardSkip = lookupOnboardSkip;
module.insertLookupResultField = insertLookupResultField;
module.getLookupResultOrder = () => lookupResultOrder;
module.getLookupOnboardStep = () => lookupOnboardStep;
module.checkedValues = checkedValues;
`);
    return {
        dom,
        module
    };
}

test('a lookup-only form still shows its builder, flagged inactive rather than hidden', () => {
    // A form's layout belongs to the form, not to whichever action happens to be on: it's built once and
    // takes effect the moment "Create records" is turned on, so hiding it entirely used to also mean saving
    // it as '[]' - silently discarding whatever the author had already built. Both bugs, fixed together.
    const {
        dom,
        module
    } = loadFormsModule();
    module.applyFormShape('form', ['lookup']);
    assert.ok(dom.byId.kindLookup.classList.contains('hidden') === false, 'lookup panel hidden');
    assert.ok(!dom.byId.kindSubmit.classList.contains('hidden'), 'submit builder hidden for a lookup-only form');
    assert.ok(!dom.byId.submitInactiveHint.classList.contains('hidden'), 'no hint that the layout is currently inactive');
    assert.ok(dom.byId.kindList.classList.contains('hidden'), 'list panel shown');
});

test('a submit-only form shows the submit panel with no inactive hint', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.applyFormShape('form', ['submit']);
    assert.ok(!dom.byId.kindSubmit.classList.contains('hidden'));
    assert.ok(dom.byId.kindLookup.classList.contains('hidden'));
    assert.ok(dom.byId.submitInactiveHint.classList.contains('hidden'), 'the inactive hint shows while submit is on');
});

test('an enumerated list filter collects its value select, not a missing input', () => {
    const {
        module
    } = loadFormsModule();
    const row = global.document.createElement('div');
    row.className = 'filter-row';
    const field = global.document.createElement('select');
    field.value = 'status';
    const op = global.document.createElement('select');
    op.value = 'eq';
    const val = global.document.createElement('select');
    val.className = 'filter-value';
    val.value = 'open';
    row.append(field, op, val);
    row.querySelector = (sel) => (sel === '.filter-value' ? val : null);
    row.querySelectorAll = () => [field, op];
    global.document.querySelectorAll = (sel) => (sel === '#listFilters .filter-row' ? [row] : []);
    assert.deepStrictEqual(module.collectListFilters(),
        [{ field: 'status', op: 'eq', value: 'open' }],
        'an enum filter value select was not collected');
});

test('the list filter value input carries the filter-value class', () => {
    const {
        module
    } = loadFormsModule();
    module.setTableFields([{ name: 'note', dataType: 'text' }]);
    const row = module.filterRow({ field: 'note', op: 'contains', value: '' }, 0);
    assert.ok(row.children.some((c) => (c.className || '').split(/\s+/).includes('filter-value')),
        'the text value input lacks the filter-value class');
});

test('a form with both actions shows both panels', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.applyFormShape('form', ['submit', 'lookup']);
    assert.ok(!dom.byId.kindSubmit.classList.contains('hidden'), 'submit panel hidden');
    assert.ok(!dom.byId.kindLookup.classList.contains('hidden'), 'lookup panel hidden');
});

test('a list shows only the list panel and hides the action picker', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.applyFormShape('list', []);
    assert.ok(!dom.byId.kindList.classList.contains('hidden'));
    assert.ok(dom.byId.kindSubmit.classList.contains('hidden'));
    assert.ok(dom.byId.formActions.classList.contains('hidden'), 'a list has no actions to pick');
});

test('enabling a second action repopulates its panel instead of leaving it blank', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.applyFormShape('form', ['submit']);
    module.applyFormShape('form', ['submit', 'lookup']);
    assert.ok(!dom.byId.kindSubmit.classList.contains('hidden'), 'submit panel hidden');
    assert.ok(!dom.byId.kindLookup.classList.contains('hidden'), 'lookup panel hidden');
    // Both pickers must have been rendered for the panel to be usable.
    assert.ok(dom.byId.lookupMatchFields.innerHTML !== undefined, 'lookup pickers never rendered');
});

test('a form is never left with no action', () => {
    const {
        module
    } = loadFormsModule();
    assert.deepStrictEqual(module.normalizeActions([]), ['submit']);
    assert.deepStrictEqual(module.normalizeActions(['nonsense']), ['submit']);
    assert.deepStrictEqual(module.normalizeActions(['lookup']), ['lookup']);
});

/* embed.js: which renderer a schema selects */

// Mirrors the dispatch in embed.js. Kept in step by the assertion below, which fails if the source stops branching this way.
function chooseRenderer(form) {
    if (form.kind === 'list') return 'list';
    const actions = form.actions || ['submit'];
    return [actions.includes('lookup') ? 'lookup' : null, actions.includes('submit') ? 'form' : null]
        .filter(Boolean).join('+');
}

test('the embed dispatches on kind then actions', () => {
    assert.strictEqual(chooseRenderer({
        kind: 'list'
    }), 'list');
    assert.strictEqual(chooseRenderer({
        kind: 'form',
        actions: ['lookup']
    }), 'lookup');
    assert.strictEqual(chooseRenderer({
        kind: 'form',
        actions: ['submit']
    }), 'form');
    assert.strictEqual(chooseRenderer({
        kind: 'form',
        actions: ['submit', 'lookup']
    }), 'lookup+form');
    assert.strictEqual(chooseRenderer({
        kind: 'form'
    }), 'form');
});

test('embed.js still branches on kind and actions, not the old mode field', () => {
    const src = read('embed.js');
    assert.ok(src.includes("data.form.kind === 'list'"), 'list dispatch missing');
    assert.ok(src.includes("actions.includes('lookup')"), 'lookup dispatch missing');
    assert.ok(!/form\.mode\s*===/.test(src), 'embed still reads the removed mode field');
});

test('embed toasts cap at eight, stack newest at the bottom, dismiss by kind and copy on click', () => {
    const src = read('embed.js');
    assert.ok(src.includes('host.children.length >= 8'), 'no eight-toast cap');
    assert.ok(src.includes('host.firstChild.remove()'), 'oldest toast is not trimmed');
    assert.ok(src.includes('host.appendChild(el)'), 'newest toast is not appended last');
    assert.ok(src.includes("kind === 'error' ? 8000 : 4500"), 'dismissal is not per-kind');
    assert.ok(src.includes("window.isSecureContext"), 'https clipboard path missing');
    assert.ok(src.includes("document.execCommand('copy')"), 'plain-http copy fallback missing');
});

test('embed paints the invalid fields red, from client validation and from the server', () => {
    const src = read('embed.js');
    assert.ok(src.includes("markInvalid(result.invalid)"), 'client-side invalid fields are not marked');
    assert.ok(src.includes('markInvalid(res.invalid || [])'), 'server invalid fields are not marked');
    assert.ok(src.includes("el.classList.add('baserow-invalid')"), 'invalid class is never added');
    assert.ok(src.includes("classList.remove('baserow-invalid')"), 'invalid class is never cleared on input');
    const css = read('embed.js');
    assert.ok(/\.baserow-embed \.baserow-invalid/.test(css), 'no styled invalid input');
});

/* ui.js: theme persistence */

test('an explicit theme choice survives a reload and outranks the system', () => {
    const dom = install([]);
    let systemDark = true;
    global.window.matchMedia = () => ({
        matches: systemDark,
        addEventListener() {},
        addListener() {}
    });

    const boot = () => {
        const stored = localStorage.getItem('baseport.theme');
        const dark = stored ? stored === 'dark' : window.matchMedia().matches;
        document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    };
    const ui = eval(read('ui.js').replace(/if \(typeof window[^\n]*\n/g, '') + '; ui');

    boot();
    assert.strictEqual(document.documentElement.dataset.theme, 'dark', 'should follow the system');
    ui.toggleTheme();
    assert.strictEqual(dom.store['baseport.theme'], 'light', 'choice not stored');
    boot();
    assert.strictEqual(document.documentElement.dataset.theme, 'light', 'choice lost on reload');
    systemDark = false;
    boot();
    assert.strictEqual(document.documentElement.dataset.theme, 'light', 'system overrode an explicit choice');
});

/* markup and script agree */

test('every id the scripts read exists in the markup or is created at runtime', () => {
    const html = readHtml();
    const js = readAll();
    const runtime = new Set(['toasts', 'sheetOverlay', 'tableName', 'pwCurrent', 'pwNew', 'fieldEditError',
        'bootstrap', 'fieldType'
    ]);
    const present = new Set([...html.matchAll(/id=['"]([\w-]+)['"]/g)].map(m => m[1]));
    const missing = [...new Set([...js.matchAll(/getElementById\('([\w-]+)'\)/g)].map(m => m[1]))]
        .filter(id => !present.has(id) && !runtime.has(id) && !/^(fe|px|acc)/.test(id));
    assert.deepStrictEqual(missing, [], `ids read but never rendered: ${missing.join(', ')}`);
});

test('every inline handler in the markup is a defined function', () => {
    const html = readHtml();
    const js = readAll();

    const inline = [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g)].map(m => m[1]).join('\n');
    const defined = new Set([...(js + inline).matchAll(/function\s+([A-Za-z_$][\w$]*)/g)].map(m => m[1]));
    defined.add('toggleTheme');
    const missing = [...new Set([...html.matchAll(/on\w+='([A-Za-z_$][\w$]*)\(/g)].map(m => m[1]))]
        .filter(fn => !defined.has(fn));
    assert.deepStrictEqual(missing, [], `handlers referenced but not defined: ${missing.join(', ')}`);
});

test('every script the markup loads exists', () => {
    const html = readHtml();
    const missing = [...html.matchAll(/<script src='([^']+)'/g)]
        .map(m => m[1].replace(/^\//, ''))
        .filter(src => !fs.existsSync(path.join(wwwroot, src)));
    assert.deepStrictEqual(missing, [], `index.html loads scripts that do not exist: ${missing.join(', ')}`);
});

test('the split scripts load in an order that satisfies their dependencies', () => {
    const consoleHtml = HTML_PARTS.map(read).join('\n');
    const order = [...consoleHtml.matchAll(/<script src='\/([^']+)'/g)].map(m => m[1]);
    assert.ok(order.indexOf('ui.js') === 0, 'ui.js must load first: every other file calls it');
    assert.ok(order.indexOf('js/auth.js') === order.length - 1, 'auth.js must load last: it boots the app');
    assert.ok(order.indexOf('forms.js') < order.indexOf('js/auth.js'), 'forms.js must precede auth.js');
    assert.ok(order.indexOf('js/core.js') < order.indexOf('js/tables.js'), 'core.js defines the router the rest calls');
});

test('the scripts read the ids the API actually returns', () => {
    const js = readAll() + read('embed.js');
    const stale = [...js.matchAll(/\.(publicId|PublicId)\b/g)].map(m => m[0]);
    assert.deepStrictEqual(stale, [], `scripts still read a property the API no longer returns: ${stale.join(', ')}`);
});

test('an embed component class outranks the generic element rule', () => {
    const css = read('embed.js');
    const componentClasses = [...new Set([...read('embed.js').matchAll(/className = '(baserow-[\w-]+)'/g)].map(m => m[1]))];

    const hasElementRule = /\.baserow-embed (button|input|table)\s*\{/.test(css);
    if (!hasElementRule) return;

    const unscoped = componentClasses.filter(c =>
        (c === 'baserow-btn' || c === 'baserow-search') &&
        !new RegExp(`\\.baserow-embed \\.${c}(?![\\w-])`).test(css));
    assert.deepStrictEqual(unscoped, [], `component classes an element rule outranks: ${unscoped.join(', ')}`);
});

test('a feature stylesheet never redefines a ui primitive', () => {
    const primitives = [...read('ui.css').matchAll(/^\.([\w-]+)\s*\{/gm)].map(m => m[1]);
    const appRules = new Set([...read('app.css').matchAll(/^\s*\.([\w-]+)\s*\{/gm)].map(m => m[1]));
    const clashes = primitives.filter(p => appRules.has(p));
    assert.deepStrictEqual(clashes, [], `app.css redefines ui.css primitives: ${clashes.join(', ')}`);
});

/* the router's mount prefix */

function loadRouter(pathname) {
    const core = read('js/core.js');
    const slice = core.slice(core.indexOf("const BASE = "), core.indexOf('// Each section owns its route'));
    const module = {};
    const written = [];
    global.location = {
        pathname
    };
    global.history = {
        pushState: (s, t, u) => written.push(u),
        replaceState: (s, t, u) => written.push(u)
    };
    global.render = () => {};
    // navigate() gates on hasUnsavedChanges(), which needs these two and a document stub
    global.tableDirty = false;
    global.fieldsDirty = false;
    global.document = {
        getElementById: () => null
    };
    eval(slice + '\n;module.parseRoute = parseRoute; module.navigate = navigate;');
    return {
        module,
        written
    };
}

test('the console is mounted under /_/admin and routes are still written from the root', () => {
    // The bug it guards: a route string that forgets the prefix silently
    // navigates out of the console, and a deep link parses as the root section.
    assert.deepStrictEqual(loadRouter('/_/admin').module.parseRoute(), {
        section: 'tables'
    });
    assert.deepStrictEqual(loadRouter('/_/admin/tables/abc/records').module.parseRoute(), {
        section: 'tables',
        id: 'abc',
        view: 'records'
    });
    assert.deepStrictEqual(loadRouter('/_/admin/settings').module.parseRoute(), {
        section: 'settings',
        id: 'host'
    });

    const nav = loadRouter('/_/admin');
    nav.module.navigate('/forms/xyz');
    assert.deepStrictEqual(nav.written, ['/_/admin/forms/xyz'], 'navigate dropped the mount prefix');
});

/* the API name guard */
// Now lives in openEndpointSheet's own Save button, not the removed main-panel tableApiName field.

function loadApiNameGuard() {
    const tables = read('js/tables.js');
    const slice = tables.slice(tables.indexOf('const API_NAME_PATTERN'),
        tables.indexOf('function tableSettingsPayload'));
    const module = {};
    global.markTableDirty = () => {};
    eval(slice + '\n;module.apiNameIsValid = apiNameIsValid; module.normalizeApiName = normalizeApiName;');
    return module;
}

test('a name is required while the table\'s API is published', () => {
    // bug prevent: clearing the API name of a published table sent a save the API rejected.
    const module = loadApiNameGuard();
    assert.strictEqual(module.apiNameIsValid('', true), false, 'a published table with no API name was accepted');
});

test('the API name is free once it is valid', () => {
    const module = loadApiNameGuard();
    assert.strictEqual(module.apiNameIsValid('sales-orders', true), true, 'a valid API name was still refused');
});

test('an unpublished table may have no API name at all', () => {
    const module = loadApiNameGuard();
    assert.strictEqual(module.apiNameIsValid('', false), true, 'clearing the name of an unpublished table was blocked');
});

test('the endpoint sheet wires the guard into its own Save button', () => {
    // The guard lives with the field it guards: the sheet that owns sheetApiName also owns disabling its own Save.
    const tables = read('js/tables.js');
    const fn = tables.slice(tables.indexOf('function openEndpointSheet'), tables.indexOf('const OPTIONS_SHOWN'));
    assert.ok(/apiNameIsValid\(name, table\.apiEnabled\)/.test(fn), 'openEndpointSheet no longer checks apiNameIsValid');
    assert.ok(/saveBtn\.disabled = !valid/.test(fn), 'openEndpointSheet no longer disables Save on an invalid name');
});

test('typed input is shaped into a valid API name', () => {
    // The author should not have to know the rule to satisfy it.
    const module = loadApiNameGuard();
    const input = {
        value: 'Sales Orders_2024!',
        selectionStart: 0,
        setSelectionRange() {}
    };
    module.normalizeApiName(input);

    assert.strictEqual(input.value, 'sales-orders-2024');
});

test('an option list survives a comma inside one option', () => {
    // A backslash escapes a comma that belongs to the option itself; splitOptions/joinOptions round-trip it.
    const core = read('js/core.js');
    const slice = core.slice(core.indexOf('function splitOptions'), core.indexOf('/* Sortable list headers'));
    const module = {};
    eval(slice + '\n;module.splitOptions = splitOptions; module.joinOptions = joinOptions;');

    assert.deepStrictEqual(module.splitOptions('red, blue, green'), ['red', 'blue', 'green']);
    assert.deepStrictEqual(module.splitOptions('Rotterdam\\, Zuid-Holland, Utrecht'), ['Rotterdam, Zuid-Holland', 'Utrecht']);
    assert.deepStrictEqual(module.splitOptions(''), []);
    const withCommas = ['Rotterdam, Zuid-Holland', 'a\\b', 'plain'];
    assert.deepStrictEqual(module.splitOptions(module.joinOptions(withCommas)), withCommas);
});

test('the client mirrors the API name pattern the server enforces', () => {
    // Two copies of one rule: they must not drift, or the console blocks a save
    // the API would take, or offers one it would reject.
    const server = fs.readFileSync(path.join(__dirname, '..', 'Source', 'Baseport', 'Engine', 'FieldValidation.cs'), 'utf8');
    const serverPattern = /new\(@"\^\[a-z\]\[a-z0-9-\]\{1,62\}\$"/.test(server);
    const clientPattern = /\/\^\[a-z\]\[a-z0-9-\]\{1,62\}\$\//.test(read('js/tables.js'));
    assert.ok(serverPattern, 'the server pattern changed; update the console to match');
    assert.ok(clientPattern, 'the console pattern changed; update the server to match');
});

/* the OTP login flow */

// auth.js boots itself on load; the checks below want its functions, not its boot.
function loadAuthModule() {
    const dom = install(['curPass', 'newPass', 'newPass2', 'changeHint',
        'loginScreen', 'loginForm', 'forgotCard', 'changeCard'
    ]);
    global.ui = {
        toast() {},
        handle: async () => null
    };
    const module = {};
    eval(read('js/auth.js').replace(/\bboot\(\);\s*$/, '') +
        '\n;module.changeProblem = changeProblem; module.refreshChangeState = refreshChangeState;');
    return {
        dom,
        module
    };
}

test('the login card never tells a visitor where the code went', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(!/server log/.test(page + auth), 'the login surface mentions the server log');
    assert.ok(!/read it from/.test(page + auth), 'the login surface tells the visitor to read something');
});

test('the OTP tab is a two-step flow: request a code, then enter it', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(page.includes("id='otpCodeRow' hidden"), 'the code field is not hidden until a code is requested');
    assert.ok(!page.includes('Press Request Code'), 'the redundant step-one hint is back');
    assert.ok(/id='otpCode'[^>]*required/.test(page), 'the code field is not required');
    assert.ok(auth.includes("'Request code' : 'Sign in'"), 'the button never reverts to Request code');
    assert.ok(auth.includes('row.hidden = false'), 'requesting a code no longer reveals the field');
    assert.ok(auth.includes('code.disabled = true'), 'the hidden field is not barred from validation');
    assert.ok(!page.includes('otpHint'), 'a text line below the code field is back');
});

test('a requested code expires on the client, not only when the server rejects it', () => {
    // The field used to stay enabled and say "Sign in" long after the 60-second
    // code was dead, so a visitor only found out by submitting a spent code.
    const auth = read('js/auth.js');
    assert.ok(auth.includes('otpExpiryTimer'), 'the code has no expiry timer');
    assert.ok(auth.includes('clearTimeout(otpExpiryTimer)'), 'a stale timer outlives a reset');
    assert.ok(auth.includes('expiresInSeconds'), 'the expiry no longer counts down');
    assert.ok(auth.includes("placeholder = `Enter the code in ${seconds}s.`"), 'the countdown is not in the placeholder');
    assert.ok(auth.includes("placeholder = 'Expired'"), 'an expired code does not say so in the placeholder');
});

test('the login form has room before the button and none wasted after the tabs', () => {
    const page = read('admin/_auth.html');
    const css = read('app.css');
    assert.ok(!/loginBtn'[^>]*margin-top:\s*-/.test(page), 'the negative-margin spacing hack is back');
    assert.ok(!/auth-tabs\s*{([^}]*margin-bottom:\s*1\.5rem)/s.test(page), 'the tabs gap was restored');
    assert.ok(/\.login-card > \.btn\s*{[^}]*margin-top:\s*\.5rem/.test(css), 'the button lost its breathing room');
});

test('the forgot link swaps to an explanatory card and back, clearing a pending code', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(page.includes("href='/forgot-password'"), 'the forgot link is gone');
    assert.ok(/onclick='showForgot\(\);return false/.test(page), 'the link no longer swaps the card in place');
    assert.ok(page.includes("id='forgotCard' hidden"), 'the forgot card is not hidden behind the login form');
    assert.ok(page.includes('You can change your password by changing the configuration.'), 'the card no longer explains the only reset path');
    assert.ok(auth.includes('function showForgot'), 'showForgot is gone');
    assert.ok(auth.includes('function backToLogin'), 'backToLogin is gone');
    assert.ok(/function backToLogin\(\) \{[\s\S]*resetOtpFlow\(\)/.test(auth), 'returning no longer clears a pending code');
});

test('tabbing a password form goes username, password, submit before the forgot link', () => {
    // The link used to sit inside the password label row, so Tab from the
    // password field landed on it instead of the submit button.
    const page = read('admin/_auth.html');
    assert.ok(!/field-label-row/.test(page), 'the forgot link is back inside the password label');
    assert.ok(page.indexOf("id='loginBtn'") < page.indexOf('forgot-password'), 'the forgot link tabs before the submit button');
});

test('the password tab is initialised so a hidden required field never blocks submit', () => {
    // The OTP code input is required in the markup; until the active tab syncs
    // the required attributes, a hidden required control makes the browser
    // refuse the submit with "an invalid form control is not focusable".
    const page = read('admin/_auth.html');
    assert.ok(/switchAuthMode\('password'\)/.test(page), 'the active tab is never initialised');
});

test('a session still on the one-time password is forced to change it', () => {
    // The seeded password used to render the full console and then 403 every
    // console call, with no screen that could replace it. The auth page now
    // carries the change card and boot() shows it before it can redirect away.
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(/id='changeCard'[^>]*hidden/.test(page), 'the change card is missing from the auth page');
    assert.ok(auth.includes('me.authenticated && me.mustChangePassword'), 'boot() does not branch to the change card');
    assert.ok(auth.includes('function showChangePassword'), 'showChangePassword is gone');
    assert.ok(auth.includes('function changePassword'), 'changePassword is gone');
    assert.ok(auth.includes("'/api/auth/password'"), 'the change flow does not call the password endpoint');
});

test('the change card is a form, so Enter submits it and Tab walks it', () => {
    const page = read('admin/_auth.html');
    const card = page.slice(page.indexOf("id='changeCard'"));
    assert.ok(/<form[^>]*id='changeCard'/.test(page), 'the change card is not a form');
    assert.ok(/onsubmit='return changePassword\(event\)'/.test(page), 'the change card has no submit handler');
    assert.ok(/<button[^>]*type='submit'/.test(card.slice(0, card.indexOf('</form>'))), 'the set-password button never submits');
    assert.ok(/id='newPass2'/.test(page), 'the new password is confirmed only once');
    assert.ok(/onclick='signOut\(\)'>Cancel/.test(card), 'the change card has no way out');
});

test('the change card wires live validation in script, not in the markup', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(!/id='changeCard'[^>]*oninput/.test(page), 'the change card still validates from an inline handler');
    assert.ok(auth.includes("card.addEventListener('input'"), 'the change card lost its live validation');
});

test('a toast can be copied, and nothing bars its text from selection', () => {
    const js = read('ui.js');
    const css = read('ui.css');
    assert.ok(js.includes('navigator.clipboard.writeText'), 'a toast cannot be copied');
    assert.ok(/el\.onclick = \(\) => copy\(el, text\)/.test(js), 'clicking a toast no longer copies it');
    assert.ok(/toastButton\('Copy'/.test(js), 'copying is mouse-only again');
    assert.ok(/ev\.stopPropagation\(\)/.test(js), 'dismissing a toast also copies it');
    assert.ok(/\.toast-text\s*{[^}]*user-select:\s*text/.test(css), 'the page-wide selection bar still applies to a toast');
    assert.ok(/\.toast\s*{[^}]*cursor:\s*pointer/.test(css), 'a toast does not look clickable');
    assert.ok(/\.toast-btn:focus-visible\s*{[^}]*outline/.test(css), 'the toast buttons have no focus ring');
});

test('double-clicking a table cell copies it, except where the cell holds controls', () => {
    const js = read('ui.js');
    assert.ok(/addEventListener\('dblclick'/.test(js), 'table cells no longer copy on double click');
    assert.ok(/closest\('\.table td'\)/.test(js), 'the copy shortcut is not scoped to table cells');
    assert.ok(/querySelector\('button, input, select, textarea, a'\)/.test(js), 'an action cell copies its button labels');
});

test('the field config column shows structure, not one long code pill', () => {
    const js = read('js/tables.js');
    const css = read('app.css');
    assert.ok(!/<code>\$\{escapeHtml\(fieldConfig\(f\)\)\}<\/code>/.test(js), 'the config cell is a single code pill again');
    assert.ok(/OPTIONS_SHOWN/.test(js), 'every select option is listed again');
    assert.ok(/title="\$\{escapeHtml\(o\.join/.test(js), 'the truncated options have no way to be read in full');
    assert.ok(/\.field-config\s*{[^}]*max-width/.test(css), 'the config column can stretch the table again');
    assert.ok(/\.field-expr\s*{[^}]*overflow-wrap/.test(css), 'a long expression cannot wrap');
});

test('a toast carries its kind in tokens, not in a coloured stripe', () => {
    const css = read('ui.css');
    const app = read('app.css');
    assert.ok(!/border-left:\s*3px/.test(css), 'the alert stripe is back');
    assert.ok(!/#[0-9a-f]{6}/i.test(css.slice(css.indexOf('.toast'), css.indexOf('.field {'))), 'a toast colour bypasses the tokens');
    assert.ok(/--success:/.test(app), 'the success hue has no token');
});

test('the change card reds the field that is wrong', () => {
    const {
        dom,
        module
    } = loadAuthModule();
    const set = (id, value) => {
        dom.byId[id].value = value;
    };

    set('curPass', 'one-time-pass');
    set('newPass', 'short');
    assert.strictEqual(module.changeProblem(false).field, 'newPass', 'a too-short password is accepted');

    set('newPass', 'one-time-pass');
    assert.strictEqual(module.changeProblem(false).field, 'newPass', 'reusing the current password is accepted');

    set('newPass', 'a-real-password');
    set('newPass2', 'a-real-passwerd');
    assert.strictEqual(module.changeProblem(false).field, 'newPass2', 'a mismatched confirmation is accepted');

    assert.strictEqual(module.refreshChangeState(false), false, 'a broken form reports as valid');
    assert.ok(dom.byId.newPass2.classList.contains('input-invalid'), 'the wrong field is not marked');
    assert.ok(!dom.byId.newPass.classList.contains('input-invalid'), 'a valid field is marked too');
    assert.ok(!dom.byId.changeHint.classList.contains('hidden'), 'the reason stays hidden');
    assert.ok(dom.byId.changeHint.innerText.length > 0, 'the hint says nothing');

    set('newPass2', 'a-real-password');
    assert.strictEqual(module.refreshChangeState(false), true, 'a good form still reports a problem');
    assert.ok(!dom.byId.newPass2.classList.contains('input-invalid'), 'the red mark outlives the fix');
    assert.ok(dom.byId.changeHint.classList.contains('hidden'), 'the hint outlives the fix');
});

test('typing does not red a field that is merely unfinished', () => {
    const {
        dom,
        module
    } = loadAuthModule();
    dom.byId.curPass.value = 'one-time-pass';
    assert.strictEqual(module.changeProblem(true), null, 'an empty new password is called wrong while typing');
    dom.byId.newPass.value = 'a-real-password';
    assert.strictEqual(module.changeProblem(true), null, 'an empty confirmation is called wrong while typing');
    assert.ok(module.changeProblem(false), 'an empty confirmation passes on submit');
});

test('the login card is a page of its own, and the shell no longer carries it', () => {
    // The bug it guards: a signed-out visitor loading the console must not pull
    // the sidebar, the sheet or any console script, and the login page must not
    // need any of them either.
    const shell = read('admin/_shell.html');
    const footer = read('admin/_footer.html');
    const page = read('admin/_auth.html');
    assert.ok(!/id='loginScreen'/.test(shell), 'the shell still renders the login card');
    assert.ok(!/id='forgotCard'/.test(shell), 'the shell still renders the forgot card');
    assert.ok(!/switchAuthMode/.test(shell), 'the shell still defines the auth tabs');
    assert.ok(page.includes("id='loginScreen'"), 'the login page lost its card');
    assert.ok(page.includes("id='forgotCard'"), 'the login page lost the forgot card');
    assert.ok(!page.includes("id='appShell'"), 'the login page still renders the console shell');
    assert.ok(!page.includes("id='sheet'"), 'the login page still renders the sheet');
    assert.ok(!page.includes('vendor/codemirror'), 'the login page still loads the code editor');
    assert.ok(!footer.includes("id='loginScreen'"), 'the footer still carries the login card');
});

test('the switch is one component, not two', () => {
    // The old .switch forces a fixed label size, so a second markup variant
    // (.switch-track) gets squeezed and its thumb runs past the track edge.
    const css = read('app.css');
    const tables = read('admin/views/tables.html');
    const settings = read('js/settings.js');
    assert.ok(!css.includes('.switch-track'), 'a second switch implementation crept back in');
    assert.ok(tables.includes("<span class='track'></span>"), 'tables.html dropped the shared switch markup');
    assert.ok(settings.includes("className = 'track'"), 'settings.js still builds the old markup');
});

test('the auth page is labelled Authentication, not Auth', () => {
    // The pane used the informal shorthand while the page it points at is the
    // account and token surface; a sidebar that says one and a title that says
    // the other reads as a bug.
    const sidebar = read('js/sidebar.js');
    const settings = read('js/settings.js');
    assert.ok(/\[\s*'auth',\s*'Authentication'/.test(sidebar), 'the sidebar still says Auth');
    assert.ok(settings.includes("auth: 'Authentication'"), 'the settings pane title still says Auth');
});

test('the jobs pane lists every job with a schedule, a next run and a run-now', () => {
    const html = read('admin/views/settings.html');
    const settings = read('js/settings.js');
    assert.ok(/<tbody id='jobsBody'><\/tbody>/.test(html), 'the jobs table body is gone');
    assert.ok(html.includes('<th>Next run</th>'), 'the jobs table lost its next-run column');
    assert.ok(html.includes('<th>Last run</th>'), 'the jobs table lost its last-run column');
    assert.ok(html.includes('<th>Enabled</th>'), 'the jobs table lost its enabled column');
    assert.ok(settings.includes('function loadJobs()'), 'loadJobs is gone');
    assert.ok(settings.includes('function runJobNow('), 'runJobNow is gone');
    assert.ok(settings.includes('/api/_admin/jobs'), 'jobs no longer talk to the jobs API');
});

test('a schedule saves explicitly, not as a side effect of running', () => {
    // The old loadJobs auto-saved on change, so the only way an operator could
    // commit a new cron was by running the job. A save must stand alone, gated
    // on an edit, and Enter must commit it.
    const settings = read('js/settings.js');
    assert.ok(!settings.includes("addEventListener('change', () => saveJob"), 'schedule still auto-saves on blur');
    assert.ok(settings.includes("ui.button('Save'"), 'the jobs table has no explicit Save button');
    assert.ok(settings.includes('save.disabled = schedule.value === job.schedule'), 'Save is not gated on an actual edit');
    assert.ok(settings.includes("ev.key !== 'Enter'"), 'Enter no longer commits a schedule edit');
});

test('the backups pane stores snapshots on a rolling window', () => {
    const html = read('admin/views/settings.html');
    const settings = read('js/settings.js');
    assert.ok(html.includes('id=\'settingsBackupRetention\''), 'the retention input is gone');
    assert.ok(html.includes("onclick='triggerBackup()'>Trigger backup"), 'the trigger control is gone');
    assert.ok(/<tbody id='backupsBody'><\/tbody>/.test(html), 'the backups table body is gone');
    assert.ok(settings.includes("'/api/_admin/backups'"), 'backups no longer read the store');
    assert.ok(settings.includes('function deleteBackup('), 'deleteBackup is gone');
    assert.ok(settings.includes('function downloadBackup(name)'), 'downloadBackup no longer takes a name');
    assert.ok(!settings.includes("/api/_admin/backup'"), 'the one-off snapshot download endpoint is still called');
});

test('the backups pane separates settings from the snapshot list', () => {
    // The pane used to cram the retention form and the table into one card,
    // Save and Trigger flush against each other and against the rows. It now
    // has a settings card and a snapshots card, like auth and jobs.
    const html = read('admin/views/settings.html');
    const css = read('app.css');
    const pane = html.slice(html.indexOf("data-pane='backups'"));
    assert.ok(pane.includes('<h2>Backup settings</h2>'), 'the retention form lost its own card');
    assert.ok(pane.includes('<h2>Snapshots</h2>'), 'the snapshot list lost its own card');
    assert.ok(pane.indexOf('<h2>Backup settings</h2>') < pane.indexOf('<h2>Snapshots</h2>'), 'the settings card no longer leads');
    assert.ok(pane.includes("onclick='triggerBackup()'>Trigger backup"), 'the trigger control is gone');
    assert.ok(/\.settings-form-footer\s*\{[^}]*gap:\s*\.5rem/.test(css), 'footer buttons can still touch');
});

test('the summary cards update on a full load, not only after a re-fetch', () => {
    // boot() fills currentTables from the server payload, so the tables route
    // skips loadTables() and its updateSummary; the overview must call it itself.
    const core = read('js/core.js');
    assert.ok(/updateSummary\(currentTables\)/.test(core), 'the overview route no longer updates the summary');
    assert.ok(/function updateSummary\(tables\)/.test(core), 'updateSummary is gone');
});

test('the summary cards report weight, not just how many tables exist', () => {
    const core = read('js/core.js');
    assert.ok(/\['Records',/.test(core), 'records no longer leads the summary');
    assert.ok(/\['Database size',/.test(core), 'the database size card is gone');
    assert.ok(/\['Index size',/.test(core), 'the index size card is gone');
    assert.ok(/\['Users enabled',/.test(core), 'the enabled-users card is gone');
    assert.ok(/summaryStats\.dbSizeBytes/.test(core), 'the sizes are not read from the settings payload');
});

test('the users page inherits the logs layout: actions in the header, pager in the toolbar', () => {
    const auth = read('admin/views/auth.html');
    assert.ok(/<div class='page-actions'>/.test(auth), 'the users header has no actions');
    assert.ok(/onclick='loadAccounts\(\)'>Refresh/.test(auth), 'the users header lost its refresh button');
    assert.ok(/onclick='openAccountForm\(\)'>New user/.test(auth), 'the add-user control is no longer next to refresh');
    assert.ok(/accounts-toolbar[\s\S]*?<div class='table-pager' id='accountsPager'>/.test(auth), 'the pager is no longer inside the toolbar');
});

test('an account carries a role, and the console names it the way the API does', () => {
    // The bug it guards: the editor still posting the old isAdmin flag, which the
    // API ignores, so every saved account silently falls back to consumer.
    const accounts = read('js/accounts.js');
    assert.ok(/<th>Role<\/th>/.test(read('admin/views/auth.html')), 'the accounts list no longer shows a role column');
    assert.ok(/id: 'accRole'[\s\S]*?type: 'select'/.test(accounts), 'the role is no longer a two-option control');
    assert.ok(/\['admin',[\s\S]*?\['consumer',/.test(accounts), 'the role control lost admin or consumer');
    assert.ok(/role: document\.getElementById\('accRole'\)\.value/.test(accounts), 'the account editor no longer sends a role');
    assert.ok(!/isAdmin/.test(accounts), 'the isAdmin flag came back');
});

test('create controls live in the page header, not the sidebar', () => {
    const sidebar = read('js/sidebar.js');
    assert.ok(!/action: \(\) => ui\.button\('New'/.test(sidebar), 'a New button crept back into the sidebar');
    const sql = read('admin/views/sql.html');
    assert.ok(/<div class='page-actions'>/.test(sql), 'the sql header carries no actions');
    assert.ok(/onclick='newQuery\(\)'[^>]*>New query/.test(sql), 'the sql header lost its new-query button');
    assert.ok(/onclick='saveQuery\(\)'[\s\S]*>Save/.test(sql), 'the sql header lost its save button');
    assert.ok(/onclick='runSql\(\)'[\s\S]*>Execute/.test(sql), 'the sql header lost its execute button');
    assert.ok(!/class='sql-actions'/.test(sql), 'the sql actions still live in the card bar');
    assert.ok(/onclick='renameCurrentQuery\(\)'[\s\S]*>Rename/.test(sql),
        'Rename is not in the sql header actions');
    assert.ok(/title='Delete query'[^>]*onclick='deleteCurrentQuery\(\)'/.test(sql),
        'Delete is not an icon-only action in the sql header');
    assert.ok(!/deleteCurrentQuery\(\)'[\s\S]*>Delete\b/.test(sql),
        'Delete still carries its text label');
    assert.ok(!/actions: \[/.test(sidebar), 'the subbar still carries per-item action buttons');
});

test('the console chrome is one sidebar and a topbar, not a rail and a panel', () => {
    // The bug it guards: a second rail-and-panel structure surviving next to the
    // merged sidebar, so the console boots two fixed columns and the workspace
    // is squeezed off the right edge.
    const shell = read('admin/_shell.html');
    const footer = read('admin/_footer.html');
    const sidebar = read('js/sidebar.js');
    assert.ok(/id='appShell'[\s\S]*class='sidebar'/.test(shell), 'the shell lost the merged sidebar');
    assert.ok(shell.includes("class='topbar'"), 'the shell has no topbar');
    assert.ok(!shell.includes("class='rail'"), 'the icon rail survived the merge');
    assert.ok(!shell.includes('sidebarAdd'), 'the sidebar quick-add survived the merge');
    assert.ok(!shell.includes('sidebar-context'), 'the per-section list is still glued to the sidebar');
    assert.ok(/<main class='main'>[\s\S]*id='subbar'[\s\S]*class='main-content'/.test(shell),
        'the per-section list is not a left subbar inside main, ahead of the content');
    assert.ok(/<\/div>\s*<\/main>\s*<\/div>\s*<\/div>/s.test(footer),
        'the footer no longer closes the main-content wrapper, the workspace and the shell');
    assert.ok(!/createTable|toggleCreateMenu|closeCreateMenu/.test(readAll()), 'a create-menu handler survived in a script');
});

test('a new table is started from the overview, not the sidebar', () => {
    const tables = read('admin/views/tables.html');
    const core = read('js/core.js');
    assert.ok(/onclick='newTable\(\)'[^>]*>New table/.test(tables), 'the tables overview lost its New table button');
    assert.ok(/function\s+newTable/.test(core), 'newTable is gone');
    assert.ok(!/createMenu/.test(read('js/sidebar.js')), 'the sidebar still builds a create menu');
});

test('the sql query actions show only while a saved query is open', () => {
    // The bug it guards: Rename and Delete lived as icon buttons in the subbar,
    // so the destructive action never reached the page header and a mid-size
    // screen hid it entirely. The header now carries them, gated on an open
    // query so a blank editor never offers Delete.
    const { install } = require('./dom-stub');
    install(['sqlQueryName', 'sqlQueryActions', 'sqlInput', 'sqlStatus', 'sqlResult', 'sqlNoData']);
    global.escapeHtml = (s) => String(s == null ? '' : s);
    const src = read('js/sql.js');
    eval(src + '\n;global.__sql = { applyQuery, clearQuery };');
    const actions = global.document.getElementById('sqlQueryActions');
    assert.ok(/id='sqlQueryActions'[\s\S]*class='hidden'/.test(read('admin/views/sql.html')),
        'query actions do not start hidden in the markup');
    actions.classList.add('hidden');
    global.__sql.applyQuery({ id: 'q1', name: 'Counts', sql: 'select 1' });
    assert.ok(!actions.classList.contains('hidden'), 'query actions hidden while a query is open');
    global.__sql.clearQuery();
    assert.ok(actions.classList.contains('hidden'), 'query actions visible again after clearing');
});

test('the form editor header carries Delete, hidden for a new form', () => {
    // The bug it guards: the form editor had Preview/Cancel/Publish but no
    // Delete, so the destructive action only lived in the index row actions and
    // an open editor could not remove the form it was editing. It hides on the
    // create flow, where there is nothing to delete yet.
    const forms = read('admin/views/forms.html');
    assert.ok(/class='[^']*hidden[^']*' id='formDeleteBtn' onclick='deleteCurrentForm\(\)'[\s\S]*>Delete/.test(forms),
        'the form editor header lost its Delete button');
    const formsJs = read('forms.js');
    assert.ok(/function\s+deleteCurrentForm/.test(formsJs), 'deleteCurrentForm is gone');
    assert.ok(/getElementById\('formDeleteBtn'\)\.classList\.remove\('hidden'\)/.test(formsJs),
        'Delete is not revealed when editing');
    assert.ok(/getElementById\('formDeleteBtn'\)\.classList\.add\('hidden'\)/.test(formsJs),
        'Delete is not hidden for a new form');
    const tables = read('admin/views/tables.html');
    assert.ok(!/btn-ghost/.test(tables), 'the endpoint Configure button still uses the ghost variant');
});

test('the forms overview row carries one Open button, like the tables list', () => {
    // The bug it guards: the forms rows shipped Preview, Edit and Delete while
    // the tables rows carried a single Open, so the two indexes read as two
    // different lists. Edit is Open now, and Delete/Preview live in the editor
    // header. The fragment is C#, read outside wwwroot.
    const fragments = fs.readFileSync(path.join(__dirname, '..', 'Source', 'Baseport', 'Api', 'FragmentEndpoints.cs'), 'utf8');
    const formsRow = fragments.slice(fragments.indexOf("fragments/forms"), fragments.indexOf("fragments/accounts"));
    assert.ok(/class=\\"row-link\\" onclick=\\"navigate\('\/forms\//.test(formsRow),
        'the forms row is not a row-link that navigates to the editor');
    assert.ok(/btn-ghost btn-sm[^>]*>Open<\/button>/.test(formsRow),
        'the forms row no longer carries the single Open button');
    assert.ok(!/Html\.Button/.test(formsRow),
        'Preview, Edit and Delete still sit in the forms row actions');
    const tablesRow = fragments.slice(fragments.indexOf("fragments/tables"), fragments.indexOf("fragments/records"));
    assert.ok(/btn-ghost btn-sm[^>]*>Open<\/button>/.test(tablesRow),
        'the tables row Open button is not the same pattern');
    assert.ok(!/function\s+selectForm/.test(read('forms.js')),
        'selectForm survived as a dead navigation alias');
});

test('page-header actions order secondaries left and the primary rightmost', () => {
    // The bug it guards: the tables overview led with the primary and trailed
    // with Refresh while auth and logs lead with Refresh, so two pages read
    // left-to-right as opposites. Every header now puts the primary action
    // rightmost, with outlines (Refresh etc.) to its left.
    const tables = read('admin/views/tables.html');
    const sql = read('admin/views/sql.html');
    const actions = (src) => src.slice(src.indexOf("class='page-actions'"));
    const after = (src, a, b) => src.indexOf(a) > src.indexOf(b);
    assert.ok(after(actions(tables), 'Refresh', 'Proxy import'), 'tables: Proxy import must sit left of Refresh');
    assert.ok(after(actions(tables), 'New table', 'Refresh'), 'tables: Refresh must sit left of the primary New table');
    assert.ok(after(actions(sql), 'Save', 'New query'), 'sql: New query must sit left of Save');
    assert.ok(after(actions(sql), 'Execute', 'Save'), 'sql: Save must sit left of the primary Execute');
    const saved = actions(sql).slice(actions(sql).indexOf("id='sqlQueryActions'"));
    assert.ok(after(saved, 'Rename', 'Delete query'), 'sql: the delete icon must sit left of Rename');
    assert.ok(after(saved, 'Save', 'Rename'), 'sql: Rename must sit left of Save');
});

test('the forms subbar lists every form flat under All forms', () => {
    // The bug it guards: the Forms subbar pinning a table-name header between
    // groups, so the rail read as a tree and the empty group label rendered as
    // a stray blank line. It is a flat list sorted by title, like the tables
    // subbar sorts by name.
    const { install } = require('./dom-stub');
    install([]);
    global.ui = {
        el: (tag, c) => {
            const e = global.document.createElement(tag);
            if (c) e.className = c;
            return e;
        }
    };
    global.navigate = () => {};
    global.routePath = () => '/forms';
    global.formEditingId = 'form-search';
    global.formsAll = [
        { id: 'form-search', title: 'Customers - Search', kind: 'form', tableName: 'Customers' },
        { id: 'form-overview', title: 'Orders - Overview status open', kind: 'list', tableName: 'Orders' },
        { id: 'form-create', title: 'Customers - Create new', kind: 'form', tableName: 'Customers' },
        { id: 'form-worklist', title: 'Orders - Worklist', kind: 'list', tableName: 'Orders' },
    ];
    const src = read('js/sidebar.js');
    eval(src + '\n;global.__sidebar = { SIDEBARS, sidebarItem, OBJECT_ICONS };');
    const items = global.__sidebar.SIDEBARS.forms.items();
    const structure = items.map((i) => i.label);
    assert.deepStrictEqual(structure, [
        'All forms', 'Customers - Create new', 'Customers - Search',
        'Orders - Overview status open', 'Orders - Worklist',
    ], 'forms are not a flat, title-sorted list');
    assert.ok(!items.some((i) => i.header), 'a group header came back');
    assert.ok(!src.includes('subbar-group'), 'sidebarItem still builds group headers');
    assert.strictEqual(items[0].icon, global.__sidebar.OBJECT_ICONS.folder,
        'All forms does not carry the folder icon');
    assert.strictEqual(items.find((i) => i.label === 'Orders - Overview status open').icon,
        global.__sidebar.OBJECT_ICONS.list, 'a list form does not carry the list icon');
    assert.strictEqual(items.find((i) => i.label === 'Customers - Search').icon,
        global.__sidebar.OBJECT_ICONS.form, 'a submit form does not carry the form icon');
});

test('a subbar pill carries the original full name as its tooltip', () => {
    // The bug it guards: the subbar pill ellipsizes long names and a hover had
    // nothing, so a stored title and its truncated shell looked identical. The
    // tooltip is the browser's, via the title attribute: no toast spam on every
    // hover, the full name appears when you rest on the pill.
    const { install } = require('./dom-stub');
    install([]);
    global.ui = {
        el: (tag, cls, attrs) => {
            const n = { tagName: tag, className: cls, ...attrs, children: [], append(c) { this.children.push(c); } };
            return n;
        },
    };
    const src = read('js/sidebar.js');
    eval(src + '\n;global.__sidebar = { SIDEBARS, sidebarItem, OBJECT_ICONS };');
    const pill = global.__sidebar.sidebarItem({ label: 'Orders - Overview status open', badge: 'List', onSelect: () => {} });
    assert.strictEqual(pill.title, 'Orders - Overview status open',
        'the pill does not carry the original name as its tooltip');
    assert.ok(!pill.onmouseenter, 'a hover handler still toasts the name');
});

test('the tables subbar marks proxy tables and leads with the table icon', () => {
    // The bug it guards: the Tables subbar rendering plain text names, so a
    // stored table and a forwarded proxy table were indistinguishable in the
    // rail. Each object carries the table icon; proxies are badged.
    const { install } = require('./dom-stub');
    install([]);
    global.ui = {
        el: (tag, c) => {
            const e = global.document.createElement(tag);
            if (c) e.className = c;
            return e;
        }
    };
    global.navigate = () => {};
    global.currentTablePublicId = 't-customers';
    global.currentTables = [
        { id: 't-orders', name: 'Orders', isProxy: false },
        { id: 't-customers', name: 'Customers', isProxy: false },
        { id: 't-portway', name: 'Portway', isProxy: true },
    ];
    const src = read('js/sidebar.js');
    eval(src + '\n;global.__sidebar = { SIDEBARS, OBJECT_ICONS };');
    const items = global.__sidebar.SIDEBARS.tables.items();
    assert.deepStrictEqual(items.map((i) => i.label),
        ['All tables', 'Customers', 'Orders', 'Portway'], 'tables are not name-sorted');
    assert.strictEqual(items[0].active, false, 'All tables is not inactive while a table is open');
    assert.strictEqual(items.find((i) => i.label === 'Customers').active, true,
        'the open table is not marked active');
    assert.strictEqual(items.find((i) => i.label === 'Portway').badge, 'proxy',
        'the proxy table is not badged');
    assert.strictEqual(items[0].icon, global.__sidebar.OBJECT_ICONS.folder,
        'All tables does not carry the folder icon');
    assert.ok(items.slice(1).every((i) => i.icon === global.__sidebar.OBJECT_ICONS.table),
        'a table item lacks the table icon');
});

test('the topbar holds the API reference and the account popout stays open', () => {
    // The bug it guards: the account trigger calling toggleAccountMenu() without
    // the event, so stopPropagation never ran and the document click listener
    // closed the popout in the same tick it opened — the menu could never show.
    const shell = read('admin/_shell.html');
    assert.ok(/class='topbar-actions'[\s\S]*window\.open\('\/docs'/.test(shell),
        'the topbar no longer links the API reference');
    assert.ok(/onclick='toggleAccountMenu\(event\)'/.test(shell),
        'the account trigger drops the click event, so the popout closes instantly');
    assert.ok(/id='accountMenu'[\s\S]*onclick='signOut\(\)'/.test(shell),
        'the account popout lost its Sign out action');
});

test('the sidebar collapse persists and the breadcrumb names the open record', () => {
    const sidebar = read('js/sidebar.js');
    const core = read('js/core.js');
    assert.ok(/localStorage\.setItem\('baseport\.sidebar'/.test(sidebar), 'the collapse state is not persisted');
    assert.ok(/function\s+toggleSidebar/.test(sidebar), 'toggleSidebar is gone');
    assert.ok(/function\s+applySidebarState/.test(sidebar), 'applySidebarState is gone');
    assert.ok(/function\s+renderBreadcrumb/.test(sidebar), 'renderBreadcrumb is gone');
    assert.ok(core.includes('renderBreadcrumb(route)'), 'the router never paints the breadcrumb');
});

test('the sql editor is a CodeMirror editor and every value goes through it', () => {
    // The bug it guards: a console that reverts to a raw textarea loses tab
    // support, highlighting, and the Ctrl+Enter/Ctrl+S shortcuts, because the
    // keyboard handling moved out of onkeydown and into the editor's keymap.
    const sql = read('js/sql.js');
    assert.ok(/sqlEditor = CodeMirror\.fromTextArea/.test(sql), 'the editor is no longer a CodeMirror instance');
    assert.ok(/extraKeys:\s*\{\s*'Ctrl-Enter': \(\) => runSql\(\)/.test(sql), 'Ctrl+Enter no longer runs the query');
    assert.ok(/const sql = sqlValue\(\)/.test(sql), 'runSql reads a raw textarea value again');
    assert.ok(!/onkeydown='handleSqlKey\(event\)'/.test(read('admin/views/sql.html')), 'keyboard handling is still a markup attribute');
});

test('the schema canvas fills the viewport and offers reset and export on right-click', () => {
    const css = read('app.css');
    const js = read('js/schema.js');
    assert.ok(/\.schema-canvas\s*\{[^}]*flex: 1/.test(css), 'the canvas no longer stretches to the viewport bottom');
    assert.ok(!/\.schema-canvas\s*\{[^}]*height: 62vh/.test(css), 'the half-screen 62vh height is back');
    assert.ok(/canvas\.addEventListener\('contextmenu'/.test(js), 'the right-click menu is gone');
    assert.ok(/function resetSchemaLayout\(\)/.test(js), 'reset layout is gone');
    assert.ok(/function exportSchemaWebp\(\)/.test(js), 'the webp export is gone');
    assert.ok(/ev\.button !== 0/.test(js), 'a right-click still starts a drag');
});

/* the public API reference */

test('the API reference is served entirely from this origin', () => {
    // The page is public and shows an auth panel. A CDN script tag, a remote
    // font or Scalar's request proxy would each put a third party in the
    // request path of a page we publish, and the proxy would see whatever
    // token a reader pastes in.
    const docs = read('docs.html');
    const external = [...docs.matchAll(/(?:src|href)=['"](https?:)?\/\//g)].map(m => m[0]);
    assert.deepStrictEqual(external, [], `docs.html loads something off-origin: ${external.join(', ')}`);
    assert.ok(docs.includes("src='/js/vendor/scalar-api-reference.js'"), 'the vendored Scalar bundle is not loaded');
    assert.ok(fs.existsSync(path.join(wwwroot, 'js/vendor/scalar-api-reference.js')),
        'the vendored bundle is missing; run Scripts/pull-vendors.sh');
    assert.ok(/withDefaultFonts:\s*false/.test(docs), 'Scalar would fetch fonts from fonts.scalar.com');
    assert.ok(/proxyUrl:\s*''/.test(docs), "Scalar would route try-it requests through proxy.scalar.com");
});

test('the API reference documents the published spec, not an internal one', () => {
    const docs = read('docs.html');
    assert.ok(docs.includes("data-url='/api/openapi.json'"), 'docs.html points somewhere other than the published spec');
});

test('the console links to the reference rather than the raw spec', () => {
    const html = readHtml();
    assert.ok(html.includes("window.open('/docs'"), 'the rail no longer opens the API reference');
    assert.ok(!html.includes("window.open('/api/openapi.json'"), 'the rail still opens raw JSON');
});

test('every handler a server-rendered row calls is defined', () => {
    // Rows are built in C# and assigned with innerHTML, so their onclick names
    // are never seen by the markup scan above. A renamed console function would
    // leave a button that throws on click and nothing would catch it.
    const fragments = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'Api', 'FragmentEndpoints.cs'), 'utf8');
    const js = readAll();
    const defined = new Set([...js.matchAll(/function\s+([A-Za-z_$][\w$]*)/g)].map(m => m[1]));

    const called = new Set([
        // Html.Button("Label", "fn", ...) and Html.IconButton(icon, "title", "fn", ...)
        ...[...fragments.matchAll(/Html\.Button\("[^"]*",\s*"(\w+)"/g)].map(m => m[1]),
        ...[...fragments.matchAll(/Html\.IconButton\([^,]+,\s*"[^"]*",\s*"(\w+)"/g)].map(m => m[1]),
        // onclick written inline in the fragment markup
        ...[...fragments.matchAll(/onclick=\\"(?:event\.stopPropagation\(\);\s*)?(\w+)\(/g)].map(m => m[1])
    ]);

    const missing = [...called].filter(fn => !defined.has(fn) && fn !== 'this');
    assert.deepStrictEqual(missing, [], `server-rendered rows call functions that do not exist: ${missing.join(', ')}`);
});

test('both places that reach a table\'s endpoint config open the same sheet', () => {
    // One sheet decides how an endpoint documents itself; two would drift.
    assert.ok(read('admin/views/tables.html').includes("onclick='openEndpointSheet()'"), "a table's own settings page has no endpoint button");
    assert.ok(read('js/settings.js').includes('openEndpointSheet(t.id)'), 'Settings > API has no endpoint button');
    const definitions = [...readAll().matchAll(/function\s+openEndpointSheet\b/g)];
    assert.strictEqual(definitions.length, 1, 'openEndpointSheet is defined more than once');
});

test('a render expression cannot inject script into the host page', () => {
    // The expression is author-written and its output lands on the customer's
    // site, so a compromised console must not become XSS on every embedding page.
    const embed = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'embed.js'), 'utf8');

    assert.ok(!/innerHTML\s*=\s*renderCell/.test(embed),
        'renderCell output is assigned straight to innerHTML again');
    assert.ok(/setSafeHtml\(td,\s*renderCell\(/.test(embed),
        'the render expression no longer goes through setSafeHtml');
    assert.ok(/new DOMParser\(\)\.parseFromString/.test(embed),
        'sanitising no longer parses to an inert document first');

    for (const guard of ['SCRIPT', 'IFRAME', 'OBJECT', "startsWith('on')", "javascript:"]) {
        assert.ok(embed.includes(guard), `sanitiser no longer guards ${guard}`);
    }
});


test('the sql editor is built only once the view is on screen', () => {
    // .view is display:none, so a CodeMirror built at load time measures a
    // zero-height container and renders partially until something forces a redraw.
    const sql = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'js', 'sql.js'), 'utf8');
    const core = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'js', 'core.js'), 'utf8');

    assert.ok(!/^initSqlEditor\(\);\s*$/m.test(sql),
        'initSqlEditor runs at load again, before the view is visible');
    assert.ok(/sql:\s*async[^}]*initSqlEditor\(\)/s.test(core),
        'the sql route no longer builds the editor');
    assert.ok(/function initSqlEditor\(\)\s*\{\s*if \(sqlEditor\) return;/.test(sql),
        'initSqlEditor is no longer idempotent, so revisiting /sql would rebuild it');
});

/* form round-tripping: button actions, redirects, and per-row list actions */

test('the button action dropdown offers cancel, validate, link and run, not just submit and reset', () => {
    const forms = read('forms.js');
    const btn = forms.slice(forms.indexOf("row.t === 'button'"), forms.indexOf("row.t === 'group'"));
    ['submit', 'reset', 'cancel', 'validate', 'link', 'run'].forEach((action) => {
        assert.ok(btn.includes(`'${action}'`), `button action dropdown is missing '${action}'`);
    });
});

test('switching a button to link or cancel reveals its own extra field', () => {
    const forms = read('forms.js');
    const btn = forms.slice(forms.indexOf("row.t === 'button'"), forms.indexOf("row.t === 'group'"));
    assert.ok(/row\.action === 'cancel'[\s\S]*?'href'/.test(btn), 'cancel never gets an href field');
    assert.ok(/row\.action === 'link'[\s\S]*?'hrefExpr'/.test(btn), 'link never gets an hrefExpr field');
    // The bug this catches: dropdown() only ever writes row[prop] and never re-renders, so a dropdown() call here would leave the extra field stuck.
    assert.ok(/actionSel\.onchange = \(\) => \{[\s\S]*?renderCanvas\(\)/.test(forms),
        'the action select does not re-render, so switching to link/cancel would not reveal its field');
});

test('a column-width select writes col.w, not stuck at the dead default', () => {
    const forms = read('forms.js');
    assert.ok(/widthSel\.onchange = \(\) => \{\s*col\.w = Number\(widthSel\.value\)/.test(forms),
        'the width picker no longer writes col.w');
    assert.ok(forms.includes("colEl.style.flexGrow = col.w || 12"), 'the admin canvas no longer mirrors the ratio it will render');
});

test('layout palette blocks are click-to-add, not drag-only', () => {
    const forms = read('forms.js');
    const wiring = forms.slice(
        forms.indexOf("querySelectorAll('.builder-palette [data-block]')"),
        forms.indexOf("wireCanvasDrops"));
    assert.ok(wiring.includes("addEventListener('dragstart'"), 'drag-to-add is gone');
    assert.ok(wiring.includes("addEventListener('click'") && wiring.includes('addRow('),
        'a palette block can only be dragged in, never clicked');
});

test('onSuccessRedirect only evaluates after a successful submit, never after a failed one', () => {
    const embed = read('embed.js');
    const submitHandler = embed.slice(embed.indexOf('formEl.onsubmit'), embed.indexOf('parent.appendChild(formEl)'));
    const successBranch = submitHandler.slice(submitHandler.indexOf('if (ok) {'), submitHandler.indexOf('} else {'));
    const failureBranch = submitHandler.slice(submitHandler.indexOf('} else {'));
    assert.ok(successBranch.includes('cfg.onSuccessRedirect'), 'a successful submit never checks onSuccessRedirect');
    assert.ok(!failureBranch.includes('onSuccessRedirect'), 'a failed submit can still redirect');
});

test('embed guards link, cancel and action-column hrefs against javascript: and data: URLs', () => {
    const embed = read('embed.js');
    // Every place a URL is built from author config or an expression must pass through the same guard.
    const guarded = [
        /row\.action === 'cancel'[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /row\.action === 'link'[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /b\.onclick = \(\) => \{[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /cfg\.onSuccessRedirect[\s\S]{0,300}isUnsafeUrl\(url\)/,
    ];
    guarded.forEach((re, i) => assert.ok(re.test(embed), `href call site ${i} is missing the isUnsafeUrl guard`));
});

test('a lookup form auto-runs from ?q= on load, so a deep link lands on a result', () => {
    const embed = read('embed.js');
    const lookup = embed.slice(embed.indexOf('function renderLookup'), embed.indexOf('function renderRecordTable'));
    assert.ok(lookup.includes('function runLookup'), 'the lookup logic was never extracted into a reusable function');
    assert.ok(lookup.includes('new URLSearchParams(window.location.search)'), 'the lookup never reads the URL');
    assert.ok(/if \(qs\) runLookup\(qs\)/.test(lookup), 'a ?q= in the URL does not trigger the lookup');
});

test('list row actions read the raw row data, not the HTML-escaped renderer copy', () => {
    // A renderer builds markup, so its data is escaped first. An action button
    // builds a URL, not markup, so escaping it would double-encode every value.
    const embed = read('embed.js');
    const actionBlock = embed.slice(embed.indexOf('actions.forEach((a) => {'), embed.indexOf('tr.appendChild(td);', embed.indexOf('actions.forEach((a) => {')));
    assert.ok(actionBlock.includes('safeEval(a.hrefExpr, row.data)'), 'list actions no longer read row.data directly');
});

test('the field-type quick-add includes derived, matching the full editor', () => {
    // Both pickers are now the same searchable combobox, sourced from one fetcher over TYPE_LABELS,
    // so "matching the full editor" is structural rather than something either markup can drift out of.
    const js = read('js/tables.js');
    assert.ok(/\['derived',/.test(js), 'TYPE_LABELS is missing derived');
    assert.strictEqual(
        [...js.matchAll(/fetchOptions:\s*\(q\)\s*=>\s*fieldTypeOptions\(q\)/g)].length,
        2,
        'quick-add and the field editor no longer share one type list',
    );
});

/* the public auth pages: /auth redirects to the login card unconditionally */

function loadUserAuth(stored, statusAuthenticated) {
    const store = stored ? {
        'baseport.user.tokens': JSON.stringify(stored)
    } : {};
    global.localStorage = {
        getItem: k => (k in store ? store[k] : null),
        setItem: (k, v) => {
            store[k] = String(v);
        },
        removeItem: k => {
            delete store[k];
        }
    };
    const replaced = [];
    global.location = {
        replace: u => replaced.push(u),
        href: '/auth/login'
    };
    // The cookie is HttpOnly, so the page cannot read it; the server is what answers.
    global.fetch = async () => ({
        ok: true,
        json: async () => ({
            authenticated: !!statusAuthenticated
        })
    });
    const module = {};
    eval(read('js/userauth.js') + '\n;module.bpGuestOnly = bpGuestOnly;');
    return {
        module,
        replaced
    };
}

test('a visitor holding tokens is not asked to sign in again on /auth', async () => {
    const {
        module,
        replaced
    } = loadUserAuth({
        auth_token: 't',
        refresh_token: 'r',
        expires_at: 9999999999
    }, false);
    await module.bpGuestOnly();
    assert.deepStrictEqual(replaced, ['/auth/profile'], '/auth/login keeps a signed-in user on the login card');
});

// The console sign-in leaves a cookie and nothing in localStorage, so a page that only reads localStorage shows an operator a login form they do not need.
test('a cookie session is recognised on /auth even with nothing stored locally', async () => {
    const {
        module,
        replaced
    } = loadUserAuth(null, true);
    await module.bpGuestOnly();
    assert.deepStrictEqual(replaced, ['/auth/profile'], 'a cookie session still gets the login card');
});

test('a guest stays on the login card', async () => {
    const {
        module,
        replaced
    } = loadUserAuth(null, false);
    await module.bpGuestOnly();
    assert.deepStrictEqual(replaced, [], 'a visitor with no session is bounced to a profile they cannot load');
});

// A cookie session has no stored refresh token, so a sign-out that only fires when tokens exist leaves the cookie alive.
test('signing out always reaches the server', () => {
    const js = read('js/userauth.js');
    const block = js.slice(js.indexOf('async function bpSignOut'), js.indexOf('async function bpDeleteAccount'));
    assert.ok(!/if \(current\) \{/.test(block), 'sign-out is still conditional on stored tokens');
    assert.ok(block.includes("fetch('/api/auth/v1/logout'"), 'sign-out never calls logout');
});

test('both guest pages call the guard, so neither drifts out of it', () => {
    ['auth/login.html', 'auth/register.html'].forEach(page => {
        assert.ok(read(page).includes('bpGuestOnly()'), `${page} never calls bpGuestOnly`);
    });
});

/* the console mirrors the guards the accounts API enforces */

// TrailBase opens the same sheet for everyone and lets the server refuse. Baseport greys the refused fields instead, but must not hide the ones the API still accepts: the token routes carry no admin guard, so an early return took away token management that still worked.
test('an admin sheet greys the refused fields and keeps the token panel', () => {
    const js = read('js/accounts.js');
    const open = js.slice(js.indexOf('function openAccountForm'), js.indexOf('function adminNotice'));
    assert.ok(/const locked = !!a && a\.role === 'admin'/.test(open), 'an admin is no longer detected');
    assert.ok(!/if \(a && a\.role === 'admin'\) return/.test(open), 'the sheet returns early and hides the token panel');
    assert.ok(open.includes('apiTokenPanel(a)'), 'the token panel is gone');
    assert.ok(/input\.disabled = true/.test(open), 'the refused fields are not greyed out');
    assert.ok(open.includes('adminNotice(a)'), 'nothing points an operator at the CLI');
});

// Offering Admin when editing would build a promotion the API refuses, which is the "greyed out, never cleared" rule in AGENTS.md.
test('the role select offers admin only when creating', () => {
    const js = read('js/accounts.js');
    const roleField = js.slice(js.indexOf("id: 'accRole'"), js.indexOf("if (a) {"));
    const adminOption = roleField.indexOf("['admin',");
    const branch = roleField.indexOf('options: a');
    assert.ok(branch >= 0 && adminOption > branch, 'admin is offered unconditionally on the role select');
});

test('a blank password field is not sent, so saving never clears a password', () => {
    const js = read('js/accounts.js');
    const submit = js.slice(js.indexOf('async function submitAccount'), js.indexOf('async function deleteAccount'));
    assert.ok(/if \(password && password\.value\) body\.password/.test(submit), 'an empty password field is submitted');
});

// The generated password is pasted straight into the command, so anything under PasswordMin would be refused by the command it appears in.
test('the generated admin password clears the server minimum', () => {
    const js = read('js/accounts.js');
    const fn = js.slice(js.indexOf('function randomPassword'), js.indexOf('function adminNotice'));
    const module = {};
    // Varies per call, so the length branch is actually exercised rather than pinned to one value.
    let seed = 0;
    global.crypto = {
        getRandomValues: (a) => {
            for (let i = 0; i < a.length; i++) a[i] = (seed * 53 + i * 37 + 11) % 256;
            seed++;
            return a;
        }
    };
    eval(fn + '\n;module.randomPassword = randomPassword;');
    const lengths = new Set();
    for (let i = 0; i < 60; i++) {
        const pw = module.randomPassword();
        assert.ok(pw.length >= 10 && pw.length <= 12, `generated ${pw.length} characters: ${pw}`);
        assert.ok(/^[A-Za-z0-9]+$/.test(pw), `generated something unquotable: ${pw}`);
        lengths.add(pw.length);
    }
    assert.ok(lengths.size > 1, 'the length never varies, so the range is decorative');
});

test('the shell commands carry a hover copy button', () => {
    const accounts = read('js/accounts.js');
    assert.ok(/ui\.copyable\(commands/.test(accounts), 'the command block has no copy affordance');
    const ui_ = read('ui.js');
    assert.ok(ui_.includes('function copyable('), 'ui.js has no copyable primitive');
    assert.ok(/copyable,/.test(ui_.slice(ui_.lastIndexOf('return {'))), 'copyable is not exported');
    // Feature stylesheets must not redefine a primitive, so the styling belongs in ui.css.
    assert.ok(read('ui.css').includes('.copy-btn'), 'the copy button has no primitive styling');
    assert.ok(!read('app.css').includes('.copy-btn'), 'app.css redefines the copy button');
});

// The switch was defined twice in app.css and had no disabled state. It is a primitive now, so it lives in ui.css and nowhere else.
test('the switch is a single primitive, not a feature-stylesheet duplicate', () => {
    const uiCss = read('ui.css');
    const appCss = read('app.css');
    const count = (css) => (css.match(/^\.switch[^{]*\{/gm) || []).length;
    assert.strictEqual(count(appCss), 0, 'app.css still styles .switch');
    assert.ok(count(uiCss) > 0, 'ui.css does not style .switch');
    assert.strictEqual((uiCss.match(/^\.switch \{/gm) || []).length, 1, '.switch is declared more than once');
    assert.ok(uiCss.includes('input:disabled'), 'a disabled switch still looks operable');
});

test('the accounts sheet uses the switch primitive, not hand-written checkbox markup', () => {
    const js = read('js/accounts.js');
    assert.ok(js.includes("ui.switchRow('Disabled'"), 'the disabled toggle is not the primitive');
    assert.ok(!/type="checkbox" id="accDisabled"/.test(js), 'hand-written checkbox markup is still there');
    assert.ok(read('ui.js').includes('function switchRow('), 'ui.js has no switchRow primitive');
});

/* container / line_items / button_bar builder blocks */

test('the palette offers container, line items and button bar blocks', () => {
    const html = read('admin/views/forms.html');
    ['container', 'line_items', 'button_bar'].forEach((block) => {
        assert.ok(new RegExp(`data-block=['"]${block}['"]`).test(html), `no palette entry for '${block}'`);
    });
});

test('addRow builds every new block type with the shape ValidateLayout expects', () => {
    const forms = read('forms.js');
    const addRow = forms.slice(forms.indexOf('function addRow('), forms.indexOf('function moveRowIn('));
    assert.ok(/type === 'container'[\s\S]*?rows:\s*\[/.test(addRow), "container is not created with a nested 'rows' array");
    assert.ok(/type === 'line_items'[\s\S]*?field:/.test(addRow), "line_items is not created with a 'field' property");
    assert.ok(/type === 'button_bar'[\s\S]*?buttons:\s*\[/.test(addRow), "button_bar is not created with a 'buttons' array");
});

test('a line-items block only offers array fields that have line-item columns configured', () => {
    const forms = read('forms.js');
    assert.ok(/function lineItemFieldCandidates\(\)\s*\{\s*return formTableFields\.filter\(\(f\) => f\.dataType === 'array' && clientArrayColumns\(f\.optionsJson\)\)/.test(forms),
        'lineItemFieldCandidates no longer filters on array type + configured columns');
});

test('undo/redo snapshots the layout at the same choke point every mutation already renders through', () => {
    const forms = read('forms.js');
    const renderCanvas = forms.slice(forms.indexOf('function renderCanvas('), forms.indexOf('function buildRowElement('));
    assert.ok(renderCanvas.includes('pushLayoutHistory()'), 'renderCanvas no longer snapshots layout history');
    assert.ok(forms.includes('function undoLayout()') && forms.includes('function redoLayout()'), 'undo/redo entry points are missing');
});

test('embed.js renders container, line_items and button_bar without duplicating the button handler', () => {
    const embed = read('embed.js');
    assert.ok(embed.includes("row.t === 'container'"), 'embed.js does not render container blocks');
    assert.ok(embed.includes("row.t === 'line_items'"), 'embed.js does not render line_items blocks');
    assert.ok(embed.includes("row.t === 'button_bar'"), 'embed.js does not render button_bar blocks');
    // Both the standalone button and every button inside a bar must funnel through one handler,
    // the same guarded window.location.href logic the javascript:/data: URL test checks.
    const definitions = [...embed.matchAll(/function\s+buildActionButton\b/g)];
    assert.strictEqual(definitions.length, 1, 'buildActionButton is defined more than once');
    assert.ok(/row\.t === 'button'\)\s*return buildActionButton\(row\)/.test(embed), "the standalone button no longer reuses buildActionButton");
    assert.ok(/\(row\.buttons \|\| \[\]\)\.forEach\(\(btnCfg\) => bar\.appendChild\(buildActionButton\(btnCfg\)\)\)/.test(embed),
        'button_bar no longer reuses buildActionButton for each of its buttons');
});

test('a submit missing only from inside a button_bar still suppresses the default Submit Data button', () => {
    const embed = read('embed.js');
    const loop = embed.slice(embed.indexOf('layout.rows.forEach((row) => {'), embed.indexOf('if (!hasSubmitButton)'));
    assert.ok(/row\.t === 'button_bar'[\s\S]*?some\(\(b\) => b\.action === 'submit'\)/.test(loop),
        "hasSubmitButton does not look inside a button_bar's buttons");
});

test("SUM(Field, 'Column') is rewritten to a property access, not left as a free identifier call", () => {
    const embed = read('embed.js');
    assert.ok(embed.includes('function sumOverColumn('), 'sumOverColumn helper is missing');
    assert.ok(/replace\(\/\\bSUM\\\(/.test(embed), 'safeEval no longer rewrites bare-identifier SUM(...) calls');
    assert.ok(/new Function\('data', 'SUM',/.test(embed), 'SUM is no longer injected into the evaluated function scope');
});

test('undo/redo buttons show a disabled state instead of silently no-opping', () => {
    const html = read('admin/views/forms.html');
    assert.ok(/id='builderUndo'[^>]*disabled/.test(html), 'the undo button does not start disabled');
    assert.ok(/id='builderRedo'[^>]*disabled/.test(html), 'the redo button does not start disabled');
    const forms = read('forms.js');
    assert.ok(forms.includes('function syncHistoryButtons()'), 'syncHistoryButtons is missing');
    assert.ok(/builderUndo[\s\S]*?\.disabled = layoutHistoryIndex <= 0/.test(forms), 'undo does not disable at the start of history');
    assert.ok(/builderRedo[\s\S]*?\.disabled = layoutHistoryIndex >= layoutHistory\.length - 1/.test(forms), 'redo does not disable at the end of history');
    const appCss = read('app.css');
    assert.ok(/\.seg-btn:disabled\s*\{/.test(appCss), 'a disabled seg-btn (Undo/Redo, viewport toggle) has no visual treatment');
});

test('the Blocks palette heading gets top spacing, not just an adjacent-h4 selector that never matches', () => {
    // Fields h4 is followed by a <ul>, then Blocks h4 - so 'h4+h4' never matches either heading.
    const appCss = read('app.css');
    assert.ok(!/\.builder-palette h4\+h4/.test(appCss), 'the dead h4+h4 selector is still there');
    assert.ok(/\.builder-palette h4:not\(:first-child\)\s*\{\s*margin-top:\s*1rem;/.test(appCss),
        'Blocks has no rule giving it top spacing away from the Fields list above it');
});

test('raw layout JSON lives inside the builder box it edits, not as a bare line below it', () => {
    const html = read('admin/views/forms.html');
    const builderMain = html.slice(html.indexOf("class='builder-main'"), html.indexOf('Redirect on success'));
    assert.ok(builderMain.includes("id='layoutCanvas'"), 'sanity: builder-main no longer contains the canvas');
    assert.ok(builderMain.includes("id='formLayout'"), 'the raw layout JSON textarea moved out of builder-main');
    assert.ok(builderMain.includes("class='builder-raw-json'"), "the raw JSON details lost its container class");
    const appCss = read('app.css');
    assert.ok(/\.builder-raw-json\s*\{/.test(appCss), 'builder-raw-json has no spacing/border of its own');
});

test('the forms sidebar tells Form and List apart by icon alone, with no redundant text tag', () => {
    const sidebar = read('js/sidebar.js');
    const formsSpec = sidebar.slice(sidebar.indexOf('forms: {'), sidebar.indexOf('sql: {'));
    assert.ok(!/badge:/.test(formsSpec), 'a redundant Form/List text tag is still built for every forms sidebar item');
    assert.ok(/icon: f\.kind === 'list' \? OBJECT_ICONS\.list : OBJECT_ICONS\.form/.test(formsSpec),
        'the sidebar item no longer picks a kind-specific icon');
    // The list icon must not be a near-copy of the form icon now that it is the only signal left.
    const formIcon = /form: SECTION_ICONS\.forms/.test(sidebar);
    assert.ok(formIcon, 'sanity: form icon definition moved');
    assert.ok(!sidebar.includes("list: \"<svg fill='none' stroke='currentColor' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' viewBox='0 0 24 24' width='18' height='18'><rect x='3' y='3' width='18' height='18' rx='2'/><path d='M8 9h8M8 13h8M8 17h4'/></svg>\""),
        'the list icon is still a near-duplicate of the form icon (rect + 3 lines)');
});

test('saving a form never discards its layout just because submit happens to be off', () => {
    const forms = read('forms.js');
    assert.ok(!/layoutJson:\s*formKind === 'form' && formActions\.includes\('submit'\)/.test(forms),
        "formSnapshot still zeroes the layout to '[]' whenever submit is off");
    assert.ok(/layoutJson:\s*formKind === 'form' \? JSON\.stringify\(layout\) : '\[\]'/.test(forms),
        'formSnapshot no longer saves the layout for every form kind, regardless of which actions are on');
});

test('a brand-new lookup starts the onboarding wizard; an already-configured one goes straight to the flat panel', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text',
        isIdentifier: true
    }, {
        name: 'Name',
        dataType: 'text'
    }]);
    module.applyFormShape('form', ['lookup']);

    module.applyKindConfig({});
    assert.strictEqual(module.getLookupOnboardStep(), 0, 'a fresh lookup should start onboarding at step 0');
    assert.ok(!dom.byId.lookupOnboardNav.classList.contains('hidden'), 'onboarding nav should show for a fresh lookup');
    assert.ok(!dom.byId.lookupStepMatch.classList.contains('hidden'), 'step 0 should show Match on');
    assert.ok(dom.byId.lookupStepShow.classList.contains('hidden'), 'step 0 should hide Show');
    assert.ok(dom.byId.lookupStepNotFound.classList.contains('hidden'), 'step 0 should hide Not-found');

    module.applyKindConfig({
        matchFields: ['Sku'],
        resultFields: ['Name']
    });
    assert.strictEqual(module.getLookupOnboardStep(), -1, 'an already-configured lookup should skip onboarding');
    assert.ok(dom.byId.lookupOnboardNav.classList.contains('hidden'), 'onboarding nav should hide once already configured');
    assert.ok(!dom.byId.lookupStepMatch.classList.contains('hidden'), 'the flat panel should show Match on');
    assert.ok(!dom.byId.lookupStepShow.classList.contains('hidden'), 'the flat panel should show Show');
    assert.ok(!dom.byId.lookupStepNotFound.classList.contains('hidden'), 'the flat panel should show Not-found');
});

test('onboarding Next is gated on the current step, and Skip drops straight to the flat panel', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text',
        isIdentifier: true
    }, {
        name: 'Name',
        dataType: 'text'
    }]);
    module.applyFormShape('form', ['lookup']);
    module.applyKindConfig({});

    module.lookupOnboardNext();
    assert.strictEqual(module.getLookupOnboardStep(), 1, 'Next did not advance from Match on to Show');
    assert.strictEqual(dom.byId.lookupOnboardNext.disabled, true, 'Next is enabled with nothing chosen to show yet');

    module.insertLookupResultField('Name');
    assert.strictEqual(dom.byId.lookupOnboardNext.disabled, false, 'Next stays disabled once a field is chosen to show');

    module.lookupOnboardBack();
    assert.strictEqual(module.getLookupOnboardStep(), 0, 'Back did not return to the previous step');

    module.lookupOnboardSkip();
    assert.strictEqual(module.getLookupOnboardStep(), -1, 'Skip did not drop straight to the flat panel');
    assert.ok(dom.byId.lookupOnboardNav.classList.contains('hidden'), 'the wizard nav is still showing after Skip');
});

test('the Show field order loaded from a saved config is exactly what gets re-saved', () => {
    const {
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text'
    }, {
        name: 'Name',
        dataType: 'text'
    }, {
        name: 'Category',
        dataType: 'text'
    }]);
    module.applyFormShape('form', ['lookup']);
    module.applyKindConfig({
        matchFields: ['Sku'],
        resultFields: ['Category', 'Name']
    });
    assert.deepStrictEqual(module.getLookupResultOrder(), ['Category', 'Name'], 'the saved Show order was not preserved on load');
});

test('dropping a chip on another chip reorders instead of silently doing nothing', () => {
    // The bug: a same-column drop used to hit `moved.path === path` and just re-render, no reorder at all -
    // a column's own field order was permanently fixed to insertion order. Every chip is now its own drop
    // target (insert-before), and the column background stays a plain append.
    const forms = read('forms.js');
    assert.ok(forms.includes('function dropFieldAt(ev, targetIndex)'), 'the shared reorder-aware drop handler is missing');
    assert.ok(/col\.items\.forEach\(\(item, itemIdx\) => \{/.test(forms), "each chip's own index is no longer tracked");
    assert.ok(/chip\.addEventListener\('drop', \(ev\) => \{[\s\S]{0,250}dropFieldAt\(ev, itemIdx\)/.test(forms),
        'a chip no longer has its own drop handler inserting before itself');
    assert.ok(/colEl\.addEventListener\('drop', \(ev\) => \{[\s\S]{0,150}dropFieldAt\(ev, undefined\)/.test(forms),
        'the column background no longer appends to the end');
    // The old dead-end must be gone, not just shadowed by the new code.
    assert.ok(!/JSON\.stringify\(moved\.path\) === JSON\.stringify\(path\)\)\s*\{\s*renderCanvas\(\);\s*return;\s*\}/.test(forms),
        'a same-column drop still just re-renders without reordering');
});

test('dragging over a chip shows a drop indicator, not just a silent reorder', () => {
    // Every chip calls stopPropagation() on its own dragover (needed so its own drop handler, not the
    // column's, wins), which also meant the column's drop-hover highlight could never fire while hovering
    // a chip - the exact moment a same-column reorder happens had zero visual feedback.
    const forms = read('forms.js');
    assert.ok(/chip\.addEventListener\('dragover', \(ev\) => \{[\s\S]{0,80}chip\.classList\.add\('drop-before'\)/.test(forms),
        'a chip no longer marks itself as a drop target while dragged over');
    assert.ok(/chip\.addEventListener\('dragleave'[\s\S]{0,300}chip\.classList\.remove\('drop-before'\)/.test(forms),
        'the drop indicator is never cleared on dragleave');
    assert.ok(/chip\.addEventListener\('dragend', \(\) => \{[\s\S]{0,120}drop-before/.test(forms),
        'a cancelled drag has no cleanup sweep for a stuck drop indicator');
    const appCss = read('app.css');
    assert.ok(/\.chip\.drop-before\s*\{/.test(appCss), 'the drop-before marker has no visual style');
});

test('a column\'s drop-hover border does not flicker off when the drag crosses onto a child chip', () => {
    // dragleave fires the instant the pointer crosses onto ANY child element (a chip, even the chip's own
    // x button), which used to blindly clear drop-hover/drop-before even though the drag never actually left
    // the column/chip - relatedTarget tells you where the pointer really went.
    const forms = read('forms.js');
    assert.ok(/colEl\.addEventListener\('dragleave', \(ev\) => \{[\s\S]{0,400}colEl\.contains\(ev\.relatedTarget\)[\s\S]{0,150}colEl\.classList\.remove\('drop-hover'\)/.test(forms),
        "the column's dragleave still clears drop-hover on every child-boundary crossing");
    assert.ok(/chip\.addEventListener\('dragleave', \(ev\) => \{[\s\S]{0,400}chip\.contains\(ev\.relatedTarget\)[\s\S]{0,150}chip\.classList\.remove\('drop-before'\)/.test(forms),
        "a chip's dragleave still clears drop-before on every child-boundary crossing (e.g. its own x button)");
    // A drag cancelled outside any valid target must not leave the highlight stuck forever.
    assert.ok(/chip\.addEventListener\('dragend', \(\) => \{[\s\S]{0,300}bcol\.drop-hover/.test(forms),
        'the dragend safety sweep no longer clears a stuck column border too');
});

test('a blank "run" button and both its editors (standalone button, button_bar) all reveal an expression field', () => {
    const forms = read('forms.js');
    ["row.action === 'run'", "btn.action === 'run'"].forEach((needle) => {
        assert.ok(forms.includes(needle), `${needle} editor branch is missing`);
    });
    const btnEditor = forms.slice(forms.indexOf("row.t === 'button'"), forms.indexOf("row.t === 'group'"));
    assert.ok(/row\.action === 'run'\)[\s\S]{0,150}labeledInput\('Expression', row, 'expr'/.test(btnEditor),
        "the standalone button's run action has no expression input");
    const barEditor = forms.slice(forms.indexOf('function buttonBarEditor'), forms.indexOf('function renderCanvas('));
    assert.ok(/btn\.action === 'run'\)[\s\S]{0,150}labeledInput\('Expression', btn, 'expr'/.test(barEditor),
        "a button_bar button's run action has no expression input");
});

test('a run button evaluates its expression and shows the result as a toast, with no forced navigation', () => {
    const embed = read('embed.js');
    assert.ok(/row\.action === 'run'\)[\s\S]{0,350}toast\(String\(result\), 'info'\)/.test(embed),
        "the run action does not surface its expression's result as a toast");
    assert.ok(!/row\.action === 'run'\)[\s\S]{0,350}window\.location\.href/.test(embed),
        'the run action forces navigation like a link, defeating the point of a blank button');
});

test('the list and lookup canvases label a field with normal-case body text, not the ROW/CONTAINER eyebrow style', () => {
    // .brow-type is an all-caps, letter-spaced tag sized for a fixed short word like ROW or CONTAINER.
    // A field's own name is the primary content of that row, not a category label.
    const forms = read('forms.js');
    assert.ok(!forms.includes("label.className = 'brow-type';"), "a field-name label still borrows the block-type eyebrow style");
    const matches = forms.match(/label\.className = 'brow-field-name';/g) || [];
    assert.strictEqual(matches.length, 2, 'expected exactly the list-column and lookup-result canvases to use brow-field-name');
    const appCss = read('app.css');
    assert.ok(/\.brow-field-name\s*\{[^}]*text-transform/.test(appCss) === false, 'brow-field-name still forces a text-transform like the eyebrow tag it replaced');
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);