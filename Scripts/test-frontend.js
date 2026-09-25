
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const {
    install,
    element
} = require('./dom-stub');

const SRC = path.join(__dirname, '..', 'Source', 'Baseport');
const wwwroot = path.join(SRC, 'wwwroot');
const readSource = (...parts) => {
    const file = path.join(SRC, ...parts);
    if (!fs.existsSync(file)) throw new Error(`missing ${path.relative(SRC, file)}; update the path in this test`);
    return fs.readFileSync(file, 'utf8');
};
const ADMIN_SCRIPTS = ['ui.js', 'js/core.js', 'js/proxy.js', 'js/tables.js', 'js/records.js', 'js/import.js',
    'js/sidebar.js', 'js/schema.js', 'js/sql.js', 'js/accounts.js', 'js/actions.js',
    'js/settings.js', 'forms.js', 'js/auth.js'
];
const readAll = () => ADMIN_SCRIPTS.map(read).join('\n');

const HTML_PARTS = ['admin/_shell.html', 'admin/views/tables.html', 'admin/views/forms.html',
    'admin/views/sql.html', 'admin/views/schema.html', 'admin/views/auth.html',
    'admin/views/logs.html', 'admin/views/settings.html', 'admin/views/actions.html',
    'admin/_footer.html'
];
const AUTH_PART = ['admin/_auth.html'];
const readHtml = () => [...HTML_PARTS, ...AUTH_PART].map(read).join('\n');
const read = f => fs.readFileSync(path.join(wwwroot, f), 'utf8');

let passed = 0,
    failed = 0;

const queue = [];

function test(name, fn) {
    queue.push([name, fn]);
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
        'listUndo', 'listRedo', 'lookupResultUndo', 'lookupResultRedo',
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
module.goToLookupStep = goToLookupStep;
module.insertColumn = insertColumn;
module.getListColumns = () => listColumns;
module.undoListColumns = undoListColumns;
module.redoListColumns = redoListColumns;
module.undoLookupResult = undoLookupResult;
module.redoLookupResult = redoLookupResult;
`);
    return {
        dom,
        module
    };
}

test('a lookup-only form still shows its builder, flagged inactive instead of hidden', () => {
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

test('the list filter value input includes the filter-value class', () => {
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
    const quiet = { ...console, error() {} };
    const ui = eval('(function (console) {' + read('ui.js').replace(/if \(typeof window[^\n]*\n/g, '') + '; return ui; })')(quiet);

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
    const runtime = new Set(['toasts', 'sheetOverlay', 'pwCurrent', 'pwNew', 'fieldEditError',
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
    const slice = core.slice(core.indexOf("const BASE = "), core.indexOf('const SECTION_ROUTES'));
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
    const tables = read('js/tables.js');
    const fn = tables.slice(tables.indexOf('function openEndpointSheet'), tables.indexOf('const OPTIONS_SHOWN'));
    assert.ok(/apiNameIsValid\(name, exposed\.ctrl\.checked\)/.test(fn), 'openEndpointSheet no longer checks apiNameIsValid');
    assert.ok(/saveBtn\.disabled = !valid/.test(fn), 'openEndpointSheet no longer disables Save on an invalid name');
});

test('typed input is shaped into a valid API name', () => {
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
    const core = read('js/core.js');
    const slice = core.slice(core.indexOf('function splitOptions'), core.indexOf('function sortState'));
    const module = {};
    eval(slice + '\n;module.splitOptions = splitOptions; module.joinOptions = joinOptions;');

    assert.deepStrictEqual(module.splitOptions('red, blue, green'), ['red', 'blue', 'green']);
    assert.deepStrictEqual(module.splitOptions('Rotterdam\\, Zuid-Holland, Utrecht'), ['Rotterdam, Zuid-Holland', 'Utrecht']);
    assert.deepStrictEqual(module.splitOptions(''), []);
    const withCommas = ['Rotterdam, Zuid-Holland', 'a\\b', 'plain'];
    assert.deepStrictEqual(module.splitOptions(module.joinOptions(withCommas)), withCommas);
});

test('the client mirrors the API name pattern the server enforces', () => {
    const server = readSource('Engine', 'Validation', 'FieldValidation.cs');
    const serverPattern = /new\(@"\^\[a-z\]\[a-z0-9-\]\{1,62\}\$"/.test(server);
    const clientPattern = /\/\^\[a-z\]\[a-z0-9-\]\{1,62\}\$\//.test(read('js/tables.js'));
    assert.ok(serverPattern, 'the server pattern changed; update the console to match');
    assert.ok(clientPattern, 'the console pattern changed; update the server to match');
});

/* the OTP login flow */

function loadAuthModule() {
    const dom = install(['curPass', 'newPass', 'newPass2', 'changeHint',
        'loginScreen', 'loginForm', 'forgotCard', 'changeCard',
        'tabPassword', 'tabOtp', 'passwordContainer', 'otpContainer',
        'loginUser', 'loginPass', 'otpCode', 'otpCodeRow', 'loginBtn', 'totpContainer', 'totpCode'
    ]);
    global.ui = {
        toast() {},
        handle: async () => null
    };
    global.ssoInit = () => {};
    global.ssoProviders = () => [];
    const module = {};
    eval(read('js/auth.js').replace(/\bboot\(\);\s*$/, '') +
        '\n;module.changeProblem = changeProblem; module.refreshChangeState = refreshChangeState; module.signIn = signIn;');
    return {
        dom,
        module
    };
}

test('an account with two-factor gets a code field after its password is accepted', async () => {
    const { dom, module } = loadAuthModule();
    dom.byId.totpContainer.hidden = true;
    const sent = [];
    global.fetch = async (url, init) => {
        sent.push(JSON.parse(init.body));
        const reply = { errors: ['Enter the code from your authenticator app.'], totp: true };
        return { ok: false, status: 401, clone: () => ({ json: async () => reply }), json: async () => reply };
    };
    await module.signIn({ preventDefault() {} });
    assert.strictEqual(dom.byId.totpContainer.hidden, false, 'the code field stayed hidden');
    assert.ok('code' in sent[0], 'the sign-in does not send the code');
    assert.ok(read('admin/_auth.html').includes("id='totpContainer' hidden"), 'the code field is visible before it is asked for');
});

test('the account menu offers two-factor', () => {
    const shell = read('admin/_shell.html');
    assert.ok(/id='accountMenu'[\s\S]*onclick='openTwoFactor\(\)'/.test(shell), 'the menu has no two-factor item');
    assert.ok(/function openTwoFactor/.test(read('js/auth.js')), 'the two-factor handler is not defined');
});

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
    assert.ok(/\.signin-submit\s*{[^}]*margin-top:\s*1\.25rem/.test(css), 'the button lost its breathing room');
});

test('the forgot link swaps to an explanatory card and back, clearing a pending code', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(page.includes("href='/forgot-password'"), 'the forgot link is gone');
    assert.ok(/onclick='showForgot\(\);return false/.test(page), 'the link no longer swaps the card in place');
    assert.ok(page.includes("id='forgotCard' hidden"), 'the forgot card is not hidden behind the login form');
    const forgot = page.slice(page.indexOf("id='forgotCard'"), page.indexOf("id='changeCard'"));
    assert.ok(/command line|shell|baseport accounts/i.test(forgot), 'the card no longer explains the only reset path');
    assert.ok(!/e-?mail|inbox|link will be sent/i.test(forgot), 'the card implies a reset email Baseport cannot send');
    assert.ok(auth.includes('function showForgot'), 'showForgot is gone');
    assert.ok(auth.includes('function backToLogin'), 'backToLogin is gone');
    assert.ok(/function backToLogin\(\) \{[\s\S]*resetOtpFlow\(\)/.test(auth), 'returning no longer clears a pending code');
});

test('tabbing a password form goes username, password, submit before the forgot link', () => {
    const page = read('admin/_auth.html');
    assert.ok(!/field-label-row/.test(page), 'the forgot link is back inside the password label');
    assert.ok(page.indexOf("id='loginBtn'") < page.indexOf('forgot-password'), 'the forgot link tabs before the submit button');
});

test('the password tab is initialised so a hidden required field never blocks submit', () => {
    const auth = read('js/auth.js');
    assert.ok(/switchAuthMode\('password'\)/.test(auth), 'the active tab is never initialised');
});

test('a session still on the one-time password is forced to change it', () => {
    const page = read('admin/_auth.html');
    const auth = read('js/auth.js');
    assert.ok(/id='changeCard'[^>]*hidden/.test(page), 'the change card is missing from the auth page');
    assert.ok(auth.includes('me.authenticated && me.mustChangePassword'), 'boot() does not branch to the change card');
    assert.ok(auth.includes('function showChangePassword'), 'showChangePassword is gone');
    assert.ok(auth.includes('function changePassword'), 'changePassword is gone');
    assert.ok(auth.includes("'/api/auth/password'"), 'the change flow does not call the password endpoint');
});

test('the change card is a form, Enter submits it and Tab walks it', () => {
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

test('double-clicking a table cell copies it, except where the cell stores controls', () => {
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

test('a toast includes its kind in tokens, not in a coloured stripe', () => {
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

test('the login card is a page of its own, and the shell no longer includes it', () => {
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
    assert.ok(!footer.includes("id='loginScreen'"), 'the footer still includes the login card');
});

test('the switch is one component, not two', () => {
    const css = read('app.css');
    assert.ok(!css.includes('.switch-track'), 'a second switch implementation crept back in');
    ['ui.js', 'js/tables.js', 'js/settings.js'].forEach((file) => {
        const js = read(file);
        assert.ok(/'track'/.test(js) && /'thumb'/.test(js), `${file} dropped the shared switch markup`);
    });
});

test('the auth page is labelled Authentication, not Auth', () => {
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

test('an account includes a role, and the console names it the way the API does', () => {
    const accounts = read('js/accounts.js');
    assert.ok(/<th>Role<\/th>/.test(read('admin/views/auth.html')), 'the accounts list no longer shows a role column');
    assert.ok(/id: 'accRole'[\s\S]*?type: 'select'/.test(accounts), 'the role is no longer a two-option control');
    assert.ok(/\['admin',[\s\S]*?\['consumer',/.test(accounts), 'the role control lost admin or consumer');
    assert.ok(/body\.role = document\.getElementById\('accRole'\)\.value/.test(accounts), 'the account editor no longer sends a role');
    assert.ok(!/isAdmin/.test(accounts), 'the isAdmin flag came back');
});

test('create controls live in the page header, not the sidebar', () => {
    const sidebar = read('js/sidebar.js');
    assert.ok(!/action: \(\) => ui\.button\('New'/.test(sidebar), 'a New button crept back into the sidebar');
    const sql = read('admin/views/sql.html');
    assert.ok(/<div class='page-actions'>/.test(sql), 'the sql header includes no actions');
    assert.ok(/onclick='newQuery\(\)'/.test(sql), 'the sql header lost its new-query button');
    assert.ok(/onclick='saveQuery\(\)'[\s\S]*>Save/.test(sql), 'the sql header lost its save button');
    assert.ok(/onclick='runSql\(\)'[\s\S]*>Execute/.test(sql), 'the sql header lost its execute button');
    assert.ok(!/class='sql-actions'/.test(sql), 'the sql actions still live in the card bar');
    assert.ok(/onclick='renameCurrentQuery\(\)'[\s\S]*>Rename/.test(sql),
        'Rename is not in the sql header actions');
    assert.ok(/title='Delete query'[^>]*onclick='deleteCurrentQuery\(\)'/.test(sql),
        'Delete is not an icon-only action in the sql header');
    assert.ok(!/deleteCurrentQuery\(\)'[\s\S]*>Delete\b/.test(sql),
        'Delete still includes its text label');
    assert.ok(!/actions: \[/.test(sidebar), 'the subbar still includes per-item action buttons');
});

test('the console chrome is one sidebar and a topbar, not a rail and a panel', () => {
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
    assert.ok(/newTable\(\)/.test(tables), 'the tables overview lost its New table action');
    assert.ok(/function\s+newTable/.test(core), 'newTable is gone');
    assert.ok(!/createMenu/.test(read('js/sidebar.js')), 'the sidebar still builds a create menu');
});

test('the sql query actions show only while a saved query is open', () => {
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

test('the form editor header includes Delete, hidden for a new form', () => {
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

test('the forms overview row includes one Open button, like the tables list', () => {
    const fragments = readSource('Api', 'Endpoints', 'FragmentEndpoints.cs');
    const formsRow = fragments.slice(fragments.indexOf("fragments/forms"), fragments.indexOf("fragments/accounts"));
    assert.ok(/class=\\"row-link\\" onclick=\\"navigate\('\/forms\//.test(formsRow),
        'the forms row is not a row-link that navigates to the editor');
    assert.ok(/btn-ghost btn-sm[^>]*>Open<\/button>/.test(formsRow),
        'the forms row no longer includes the single Open button');
    assert.ok(!/Html\.Button/.test(formsRow),
        'Preview, Edit and Delete still sit in the forms row actions');
    const tablesRow = fragments.slice(fragments.indexOf("fragments/tables"), fragments.indexOf("fragments/records"));
    assert.ok(/btn-ghost btn-sm[^>]*>Open<\/button>/.test(tablesRow),
        'the tables row Open button is not the same pattern');
    assert.ok(!/function\s+selectForm/.test(read('forms.js')),
        'selectForm survived as a dead navigation alias');
});

test('page-header actions order secondaries left and the primary rightmost', () => {
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

test('the forms subbar lists every form flat under Show all', () => {
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
        'Show all', 'Customers - Create new', 'Customers - Search',
        'Orders - Overview status open', 'Orders - Worklist',
    ], 'forms are not a flat, title-sorted list');
    assert.ok(!items.some((i) => i.header), 'a group header came back');
    const builder = src.slice(src.indexOf('function sidebarItem'), src.indexOf('function filterBar'));
    assert.ok(!/subbar-group|subbar-sep/.test(builder), 'sidebarItem still builds group headers');
    assert.strictEqual(items[0].icon, global.__sidebar.OBJECT_ICONS.folder,
        'Show all does not carry the folder icon');
    assert.strictEqual(items.find((i) => i.label === 'Orders - Overview status open').icon,
        global.__sidebar.OBJECT_ICONS.list, 'a list form does not carry the list icon');
    assert.strictEqual(items.find((i) => i.label === 'Customers - Search').icon,
        global.__sidebar.OBJECT_ICONS.form, 'a submit form does not carry the form icon');
});

test('a subbar pill includes the original full name as its tooltip', () => {
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
        ['Show all', 'Customers', 'Orders', 'Portway'], 'tables are not name-sorted');
    assert.strictEqual(items[0].active, false, 'Show all is not inactive while a table is open');
    assert.strictEqual(items.find((i) => i.label === 'Customers').active, true,
        'the open table is not marked active');
    assert.strictEqual(items.find((i) => i.label === 'Portway').badge, 'proxy',
        'the proxy table is not badged');
    assert.strictEqual(items[0].icon, global.__sidebar.OBJECT_ICONS.folder,
        'Show all does not carry the folder icon');
    assert.ok(items.slice(1).every((i) => i.icon === global.__sidebar.OBJECT_ICONS.table),
        'a table item lacks the table icon');
});

test('the topbar stores the API reference and the account popout stays open', () => {
    const shell = read('admin/_shell.html');
    assert.ok(/class='topbar-actions'[\s\S]*window\.open\('\/docs'/.test(shell),
        'the topbar no longer links the API reference');
    assert.ok(/onclick='toggleAccountMenu\(event\)'/.test(shell),
        'the account trigger drops the click event, the popout closes instantly');
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

test('the console links to the reference instead of the raw spec', () => {
    const html = readHtml();
    assert.ok(html.includes("window.open('/docs'"), 'the rail no longer opens the API reference');
    assert.ok(!html.includes("window.open('/api/openapi.json'"), 'the rail still opens raw JSON');
});

test('every handler a server-rendered row calls is defined', () => {
    const fragments = readSource('Api', 'Endpoints', 'FragmentEndpoints.cs');
    const js = readAll();
    const defined = new Set([...js.matchAll(/function\s+([A-Za-z_$][\w$]*)/g)].map(m => m[1]));

    const called = new Set([
        ...[...fragments.matchAll(/Html\.Button\("[^"]*",\s*"(\w+)"/g)].map(m => m[1]),
        ...[...fragments.matchAll(/Html\.IconButton\([^,]+,\s*"[^"]*",\s*"(\w+)"/g)].map(m => m[1]),
        // onclick written inline in the fragment markup
        ...[...fragments.matchAll(/onclick=\\"(?:event\.stopPropagation\(\);\s*)?(\w+)\(/g)].map(m => m[1])
    ]);

    const missing = [...called].filter(fn => !defined.has(fn) && fn !== 'this');
    assert.deepStrictEqual(missing, [], `server-rendered rows call functions that do not exist: ${missing.join(', ')}`);
});

test('both places that reach a table\'s endpoint config open the same sheet', () => {
    assert.ok(read('admin/views/tables.html').includes("onclick='openEndpointSheet()'"), "a table's own settings page has no endpoint button");
    assert.ok(read('js/settings.js').includes('openEndpointSheet(t.id)'), 'Settings > API has no endpoint button');
    const definitions = [...readAll().matchAll(/function\s+openEndpointSheet\b/g)];
    assert.strictEqual(definitions.length, 1, 'openEndpointSheet is defined more than once');
});

test('a render expression cannot inject script into the host page', () => {
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
    const sql = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'js', 'sql.js'), 'utf8');
    const core = fs.readFileSync(
        path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'js', 'core.js'), 'utf8');

    assert.ok(!/^initSqlEditor\(\);\s*$/m.test(sql),
        'initSqlEditor runs at load again, before the view is visible');
    assert.ok(/sql:\s*async[^}]*initSqlEditor\(\)/s.test(core),
        'the sql route no longer builds the editor');
    assert.ok(/function initSqlEditor\(\)\s*\{\s*if \(sqlEditor\) return;/.test(sql),
        'initSqlEditor is no longer idempotent, revisiting /sql would rebuild it');
});

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
    assert.ok(/actionSel\.onchange = \(\) => \{[\s\S]*?renderCanvas\(\)/.test(forms),
        'the action select does not re-render, switching to link/cancel would not reveal its field');
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
    const guarded = [
        /row\.action === 'cancel'[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /row\.action === 'link'[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /b\.onclick = \(\) => \{[\s\S]{0,200}isUnsafeUrl\(url\)/,
        /cfg\.onSuccessRedirect[\s\S]{0,300}isUnsafeUrl\(url\)/,
    ];
    guarded.forEach((re, i) => assert.ok(re.test(embed), `href call site ${i} is missing the isUnsafeUrl guard`));
});

test('a lookup form auto-runs from ?q= on load, a deep link lands on a result', () => {
    const embed = read('embed.js');
    const lookup = embed.slice(embed.indexOf('function renderLookup'), embed.indexOf('function renderRecordTable'));
    assert.ok(lookup.includes('function runLookup'), 'the lookup logic was never extracted into a reusable function');
    assert.ok(lookup.includes('new URLSearchParams(window.location.search)'), 'the lookup never reads the URL');
    assert.ok(/if \(qs\) runLookup\(qs\)/.test(lookup), 'a ?q= in the URL does not trigger the lookup');
});

test('list row actions read the raw row data, not the HTML-escaped renderer copy', () => {
    const embed = read('embed.js');
    const actionBlock = embed.slice(embed.indexOf('actions.forEach((a) => {'), embed.indexOf('tr.appendChild(td);', embed.indexOf('actions.forEach((a) => {')));
    assert.ok(actionBlock.includes('safeEval(a.hrefExpr, row.data)'), 'list actions no longer read row.data directly');
});

test('both field-type pickers read the server list, and the console keeps no copy of it', () => {
    const js = read('js/tables.js');
    assert.ok(!/\[['"]derived['"],\s*['"]/.test(js), 'the console hardcodes a field-type list again');
    assert.ok(/fieldTypeRows = types \|\| \[\]/.test(js), 'setFieldTypes keeps no server rows to offer');
    assert.ok(/setFieldTypes\(me\.fieldTypes/.test(read('js/auth.js')), 'the bootstrap never feeds the type list');
    assert.strictEqual(
        [...js.matchAll(/fetchOptions:\s*\(q\)\s*=>\s*fieldTypeOptions\(q\)/g)].length,
        2,
        'quick-add and the field editor no longer share one type list',
    );
});

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

test('signing out always reaches the server', () => {
    const js = read('js/userauth.js');
    const block = js.slice(js.indexOf('async function bpSignOut'), js.indexOf('async function bpDeleteAccount'));
    assert.ok(!/if \(current\) \{/.test(block), 'sign-out is still conditional on stored tokens');
    assert.ok(block.includes("fetch('/api/auth/v1/logout'"), 'sign-out never calls logout');
});

test('both guest pages call the guard, neither drifts out of it', () => {
    ['auth/login.html', 'auth/register.html'].forEach(page => {
        assert.ok(read(page).includes('bpGuestOnly()'), `${page} never calls bpGuestOnly`);
    });
});

test('an admin sheet greys the refused fields and keeps the token panel', () => {
    const js = read('js/accounts.js');
    const open = js.slice(js.indexOf('function openAccountForm'), js.indexOf('function adminNotice'));
    assert.ok(/const locked = !!a && a\.role === 'admin'/.test(open), 'an admin is no longer detected');
    assert.ok(!/if \(a && a\.role === 'admin'\) return/.test(open), 'the sheet returns early and hides the token panel');
    assert.ok(open.includes('apiTokenPanel(a)'), 'the token panel is gone');
    assert.ok(/input\.disabled = true/.test(open), 'the refused fields are not greyed out');
    assert.ok(open.includes('adminNotice(a)'), 'nothing points an operator at the CLI');
});

test('the admin lock covers what takes an account over, and nothing else', () => {
    const js = read('js/accounts.js');
    const open = js.slice(js.indexOf('function openAccountForm'), js.indexOf('function adminNotice'));
    const greyed = open.match(/\[([^\]]*)\]\s*\n\s*\.forEach\(\(id\) => \{/);
    assert.ok(greyed, 'the greyed list is gone');
    assert.ok(!/accUsername|accEmail/.test(greyed[1]), 'the name or the address is greyed again');
    for (const id of ['accRole', 'accPassword', 'accDisabled'])
        assert.ok(greyed[1].includes(id), `${id} is no longer greyed on an admin`);

    const submit = js.slice(js.indexOf('async function submitAccount'));
    assert.ok(/const locked =[\s\S]{0,120}role === 'admin'/.test(submit), 'the save no longer knows it is editing an admin');
    assert.ok(/if \(!locked\) \{[\s\S]*?body\.role/.test(submit), 'a refused role is sent anyway');
    assert.ok(/if \(!locked\) \{[\s\S]*?body\.isDisabled/.test(submit), 'a refused disabled switch is sent anyway');
    assert.ok(/if \(!locked\) \{[\s\S]*?body\.password/.test(submit), 'a refused password is sent anyway');

    assert.ok(!/if \(!locked\) \{\s*const saveBtn/.test(open), 'an admin sheet still has no way to save');
});

test('the role select offers admin only when creating', () => {
    const js = read('js/accounts.js');
    const roleField = js.slice(js.indexOf("id: 'accRole'"), js.indexOf("if (a) {"));
    const adminOption = roleField.indexOf("['admin',");
    const branch = roleField.indexOf('options: a');
    assert.ok(branch >= 0 && adminOption > branch, 'admin is offered unconditionally on the role select');
});

test('a blank password field is not sent, saving never clears a password', () => {
    const js = read('js/accounts.js');
    const submit = js.slice(js.indexOf('async function submitAccount'), js.indexOf('async function deleteAccount'));
    assert.ok(/if \(password(?: && password\.value|\?\.value)\) body\.password/.test(submit), 'an empty password field is submitted');
});

test('the generated admin password clears the server minimum', () => {
    const js = read('js/accounts.js');
    const fn = js.slice(js.indexOf('function randomPassword'), js.indexOf('function adminNotice'));
    const module = {};
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
    assert.ok(lengths.size > 1, 'the length never varies, the range is decorative');
});

test('the shell commands carry a hover copy button', () => {
    const accounts = read('js/accounts.js');
    assert.ok(/ui\.copyable\(commands/.test(accounts), 'the command block has no copy affordance');
    const ui_ = read('ui.js');
    assert.ok(ui_.includes('function copyable('), 'ui.js has no copyable primitive');
    assert.ok(/copyable,/.test(ui_.slice(ui_.lastIndexOf('return {'))), 'copyable is not exported');
    assert.ok(read('ui.css').includes('.copy-btn'), 'the copy button has no primitive styling');
    assert.ok(!read('app.css').includes('.copy-btn'), 'app.css redefines the copy button');
});

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

/* child_table builder block  */
test('the palette offers a child table block', () => {
    const html = read('admin/views/forms.html');
    assert.ok(/data-block=['"]child_table['"]/.test(html), "no palette entry for 'child_table'");
});

test('addRow builds a child_table block with table, refField and columns', () => {
    const forms = read('forms.js');
    const addRow = forms.slice(forms.indexOf('function addRow('), forms.indexOf('function moveRowIn('));
    assert.ok(/type === 'child_table'[\s\S]*?table:\s*''[\s\S]*?refField:\s*''[\s\S]*?columns:\s*\[\]/.test(addRow),
        "child_table is not created with 'table', 'refField' and 'columns'");
});

test('a child table block only offers other tables that reference this one back', () => {
    const forms = read('forms.js');
    assert.ok(/function childTableCandidates\(\)\s*\{/.test(forms), 'childTableCandidates is missing');
    assert.ok(/f\.dataType === 'reference' && refTargetId\(f\.optionsJson\) === formTableId/.test(forms),
        'childTableCandidates no longer filters on a reference field pointing back at this table');
});

test('embed.js renders child_table blocks and stages their rows for a post-submit flush', () => {
    const embed = read('embed.js');
    assert.ok(embed.includes("row.t === 'child_table'"), 'embed.js does not render child_table blocks');
    assert.ok(embed.includes('function renderChildTable('), 'renderChildTable is missing');
    assert.ok(embed.includes('function flushChildTables('), 'flushChildTables is missing');
    assert.ok(embed.includes('pendingChildTables.push('), 'renderChildTable does not stage its rows for the post-submit flush');
    assert.ok(/api\/forms\/\$\{formId\}\/child\/\$\{entry\.table\}\?refId=/.test(embed),
        'flushChildTables does not post to the per-form child-table route with the new header id');
});

test('embed.js loads its own Preact/htm dependencies before rendering a block that needs them', () => {
    const embed = read('embed.js');
    assert.ok(embed.includes('function ensurePreactHtm('), 'ensurePreactHtm is missing');
    assert.ok(embed.includes('function loadVendorScript('), 'loadVendorScript is missing');
    assert.ok(/js\/vendor\/preact\.min\.js/.test(embed) && /js\/vendor\/htm\.js/.test(embed),
        'embed.js no longer points at the vendored preact/htm files');
    assert.ok(embed.includes('function usesPreact('), 'usesPreact is missing');
    assert.ok(/line_items\|child_table/.test(embed), 'usesPreact no longer checks for both Preact-based block types');
    assert.ok(/usesPreact\(data\.form\.layoutJson\) \? ensurePreactHtm\(apiBase\)/.test(embed),
        'the schema fetch no longer waits for ensurePreactHtm before rendering');
});

test('undo/redo snapshots each builder at the same choke point every one of its mutations already renders through', () => {
    const forms = read('forms.js');
    assert.ok(forms.includes('function createHistory(undoBtnId, redoBtnId)'), 'the shared history factory is missing');

    const renderCanvas = forms.slice(forms.indexOf('function renderCanvas('), forms.indexOf('function buildRowElement('));
    assert.ok(renderCanvas.includes('layoutHistory.push()'), 'renderCanvas no longer snapshots layout history');
    assert.ok(forms.includes('function undoLayout()') && forms.includes('function redoLayout()'), 'layout undo/redo entry points are missing');

    const renderListBuilder = forms.slice(forms.indexOf('function renderListBuilder('), forms.indexOf('const LINK_EXPR_PLACEHOLDER'));
    assert.ok(renderListBuilder.includes('listColumnsHistory.push()'), 'renderListBuilder no longer snapshots list-column history');
    assert.ok(forms.includes('function undoListColumns()') && forms.includes('function redoListColumns()'), 'list-column undo/redo entry points are missing');

    const renderLookupResultBuilder = forms.slice(forms.indexOf('function renderLookupResultBuilder('), forms.indexOf('function wireLookupResultDrag('));
    assert.ok(renderLookupResultBuilder.includes('lookupResultHistory.push()'), 'renderLookupResultBuilder no longer snapshots lookup-result history');
    assert.ok(forms.includes('function undoLookupResult()') && forms.includes('function redoLookupResult()'), 'lookup-result undo/redo entry points are missing');
});

test('embed.js renders container, line_items and button_bar without duplicating the button handler', () => {
    const embed = read('embed.js');
    assert.ok(embed.includes("row.t === 'container'"), 'embed.js does not render container blocks');
    assert.ok(embed.includes("row.t === 'line_items'"), 'embed.js does not render line_items blocks');
    assert.ok(embed.includes("row.t === 'button_bar'"), 'embed.js does not render button_bar blocks');
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

test('undo/redo buttons show a disabled state instead of silently no-opping, for every builder', () => {
    const html = read('admin/views/forms.html');
    [
        ['builderUndo', 'builderRedo'], // submit layout
        ['listUndo', 'listRedo'], // list columns
        ['lookupResultUndo', 'lookupResultRedo'], // lookup show fields
    ].forEach(([undoId, redoId]) => {
        assert.ok(new RegExp(`id='${undoId}'[^>]*disabled`).test(html), `${undoId} does not start disabled`);
        assert.ok(new RegExp(`id='${redoId}'[^>]*disabled`).test(html), `${redoId} does not start disabled`);
    });
    const forms = read('forms.js');
    assert.ok(/undoBtn\.disabled = index <= 0/.test(forms), 'the shared history factory does not disable undo at the start of history');
    assert.ok(/redoBtn\.disabled = index >= stack\.length - 1/.test(forms), 'the shared history factory does not disable redo at the end of history');
    const appCss = read('app.css');
    assert.ok(/\.seg-btn:disabled\s*\{/.test(appCss), 'a disabled seg-btn (Undo/Redo, viewport toggle) has no visual treatment');
});

test('the Blocks palette heading gets top spacing, not just an adjacent-h4 selector that never matches', () => {
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
    const forms = read('forms.js');
    assert.ok(forms.includes('function dropFieldAt(ev, targetIndex)'), 'the shared reorder-aware drop handler is missing');
    assert.ok(/col\.items\.forEach\(\(item, itemIdx\) => \{/.test(forms), "each chip's own index is no longer tracked");
    assert.ok(/chip\.addEventListener\('drop', \(ev\) => \{[\s\S]{0,250}dropFieldAt\(ev, itemIdx\)/.test(forms),
        'a chip no longer has its own drop handler inserting before itself');
    assert.ok(/colEl\.addEventListener\('drop', \(ev\) => \{[\s\S]{0,150}dropFieldAt\(ev, undefined\)/.test(forms),
        'the column background no longer appends to the end');
    assert.ok(!/JSON\.stringify\(moved\.path\) === JSON\.stringify\(path\)\)\s*\{\s*renderCanvas\(\);\s*return;\s*\}/.test(forms),
        'a same-column drop still just re-renders without reordering');
});

test('dragging over a chip shows a drop indicator, not just a silent reorder', () => {
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
    const forms = read('forms.js');
    assert.ok(/colEl\.addEventListener\('dragleave', \(ev\) => \{[\s\S]{0,400}colEl\.contains\(ev\.relatedTarget\)[\s\S]{0,150}colEl\.classList\.remove\('drop-hover'\)/.test(forms),
        "the column's dragleave still clears drop-hover on every child-boundary crossing");
    assert.ok(/chip\.addEventListener\('dragleave', \(ev\) => \{[\s\S]{0,400}chip\.contains\(ev\.relatedTarget\)[\s\S]{0,150}chip\.classList\.remove\('drop-before'\)/.test(forms),
        "a chip's dragleave still clears drop-before on every child-boundary crossing (e.g. its own x button)");
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
    const forms = read('forms.js');
    assert.ok(!forms.includes("label.className = 'brow-type';"), "a field-name label still borrows the block-type eyebrow style");
    const matches = forms.match(/label\.className = 'brow-field-name';/g) || [];
    assert.strictEqual(matches.length, 2, 'expected exactly the list-column and lookup-result canvases to use brow-field-name');
    const appCss = read('app.css');
    assert.ok(/\.brow-field-name\s*\{[^}]*text-transform/.test(appCss) === false, 'brow-field-name still forces a text-transform like the eyebrow tag it replaced');
});

test('the onboarding step indicator is a real control (jumps steps), not a decoration that only looks clickable', () => {
    const html = read('admin/views/forms.html');
    assert.ok(/data-step='0' onclick='goToLookupStep\(0\)'/.test(html), 'step 0 indicator has no click handler');
    assert.ok(/data-step='1' onclick='goToLookupStep\(1\)'/.test(html), 'step 1 indicator has no click handler');
    assert.ok(/data-step='2' onclick='goToLookupStep\(2\)'/.test(html), 'step 2 indicator has no click handler');

    const {
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text',
        isIdentifier: true
    }]);
    module.applyFormShape('form', ['lookup']);
    module.applyKindConfig({});
    assert.strictEqual(module.getLookupOnboardStep(), 0);

    module.goToLookupStep(2);
    assert.strictEqual(module.getLookupOnboardStep(), 2, 'clicking the step indicator does not jump to that step');

    module.goToLookupStep(0);
    assert.strictEqual(module.getLookupOnboardStep(), 0, 'the step indicator cannot jump backward either');
});

test('list columns and lookup Show fields get their own independent undo, not just the submit layout', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text'
    }, {
        name: 'Name',
        dataType: 'text'
    }]);

    module.applyFormShape('list', []);
    module.applyKindConfig({});
    assert.strictEqual(dom.byId.listUndo.disabled, true, 'list undo is not disabled with nothing to undo yet');

    module.insertColumn('Sku');
    assert.deepStrictEqual(module.getListColumns().map((c) => c.name), ['Sku']);
    assert.strictEqual(dom.byId.listUndo.disabled, false, 'inserting a column does not enable list undo');

    module.undoListColumns();
    assert.deepStrictEqual(module.getListColumns(), [], 'list undo did not remove the inserted column');
    assert.strictEqual(dom.byId.listRedo.disabled, false, 'undoing does not enable list redo');

    module.redoListColumns();
    assert.deepStrictEqual(module.getListColumns().map((c) => c.name), ['Sku'], 'list redo did not restore the column');
});

test('lookup Show fields undo independently of the list and submit builders', () => {
    const {
        dom,
        module
    } = loadFormsModule();
    module.setTableFields([{
        name: 'Sku',
        dataType: 'text'
    }, {
        name: 'Name',
        dataType: 'text'
    }]);
    module.applyFormShape('form', ['lookup']);
    module.applyKindConfig({
        matchFields: ['Sku'],
        resultFields: []
    });
    assert.strictEqual(dom.byId.lookupResultUndo.disabled, true, 'lookup-result undo is not disabled with nothing to undo yet');

    module.insertLookupResultField('Name');
    assert.strictEqual(dom.byId.lookupResultUndo.disabled, false, 'inserting a Show field does not enable lookup-result undo');

    module.undoLookupResult();
    assert.deepStrictEqual(module.getLookupResultOrder(), [], 'lookup-result undo did not remove the inserted field');

    module.redoLookupResult();
    assert.deepStrictEqual(module.getLookupResultOrder(), ['Name'], 'lookup-result redo did not restore the field');
});

/* single sign-on and the redesigned sign-in screens */

test('the sign-in screens are a column on the page, not a card', () => {
    const css = read('app.css');
    for (const page of ['admin/_auth.html', 'auth/login.html', 'auth/register.html', 'auth/profile.html'])
        assert.ok(!/login-card|login-screen/.test(read(page)), `${page} still renders the login card`);
    assert.ok(!/\.login-card\s*{/.test(css), 'the login card styles are back');
    assert.ok(/\.signin-panel\s*{[^}]*max-width/.test(css), 'the sign-in column has no measure');
    assert.ok(!/\.signin-panel\s*{[^}]*border:/.test(css), 'the sign-in column drew a border again');
});

test('the wordmark is masked, it takes the theme instead of a fixed navy', () => {
    const css = read('app.css');
    const svg = read('baseport.svg');
    assert.ok(/\.signin-mark\s*{[\s\S]*?mask:\s*url\('\/baseport\.svg'\)/.test(css), 'the wordmark is not painted through a mask');
    assert.ok(/\.signin-mark\s*{[\s\S]*?background-color:\s*hsl\(var\(--foreground\)\)/.test(css), 'the wordmark ignores the foreground token');
    assert.ok(!/<text/.test(svg), 'the wordmark is text again, not paths');
    assert.ok(!/#000155/.test(svg), 'the wordmark includes a hard-coded colour');
});

test('both sign-in screens offer the same providers from the same script', () => {
    const consolePage = read('admin/_auth.html');
    const publicPage = read('auth/login.html');
    for (const page of [consolePage, publicPage]) {
        assert.ok(/id='ssoBlock' hidden/.test(page), 'the provider block is shown before it is known to have anything in it');
        assert.ok(page.includes("id='ssoProviders'"), 'the provider block has nowhere to render into');
        assert.ok(page.includes("src='/js/sso.js'"), 'the page does not load the provider script');
        assert.ok(page.includes('<!--__BOOTSTRAP__-->'), 'the page has no server-rendered payload to read the providers from');
    }
    assert.ok(/ssoInit\('public'\)/.test(publicPage), 'the end-user screen does not say which door it is');
    assert.ok(/ssoInit\('console'\)/.test(read('js/auth.js')), 'the console screen does not say which door it is');
});

test('the provider buttons come from the rendered payload, never from a fetch', () => {
    const sso = read('js/sso.js');
    assert.ok(!/fetch\(/.test(sso), 'the sign-in screen asks the server for its providers');
    assert.ok(/getElementById\('bootstrap'\)/.test(sso), 'the providers are not read from the rendered payload');
    assert.ok(/\/api\/auth\/oidc\/\$\{encodeURIComponent\(provider\.slug\)\}\/start\?surface=/.test(sso), 'a button does not point at the start route for its surface');
    assert.ok(/textContent = `Continue with/.test(sso), 'a provider name is interpolated as HTML instead of set as text');
});

test('a failed sign-in reports a code the screen owns, and clears it from the address bar', () => {
    const sso = read('js/sso.js');
    for (const code of ['failed', 'denied', 'no_account', 'disabled', 'no_console'])
        assert.ok(new RegExp(`\\b${code}:`).test(sso), `the ${code} outcome has no wording`);
    assert.ok(/searchParams\.delete\('sso'\)/.test(sso), 'the outcome stays in the address bar');
    assert.ok(/history\.replaceState/.test(sso), 'clearing the outcome adds a history entry');
    assert.ok(/ui\.toast\(/.test(sso), 'the outcome is not reported through the one feedback surface');
});

test('one panel at a time, and the providers belong to the sign-in panel', () => {
    const auth = read('js/auth.js');
    assert.ok(/function showPanel/.test(auth), 'the panels are toggled ad hoc again');
    assert.ok(/ssoBlock[\s\S]{0,120}name !== 'login'/.test(auth), 'the provider block outlives the sign-in panel');
    for (const panel of ['loginForm', 'forgotCard', 'changeCard'])
        assert.ok(new RegExp(`getElementById\\('${panel}'\\).hidden`).test(auth), `${panel} is not part of the panel switch`);
});

test('the username stops moving once a one-time code is out', () => {
    const auth = read('js/auth.js');
    assert.ok(/function lockUsername/.test(auth), 'nothing locks the username field');
    assert.ok(/otpRequested = true;\s*\n\s*lockUsername\(true\)/.test(auth), 'the field stays editable while a code is pending');
    for (const flow of ['resetOtpFlow', 'expireOtpFlow']) {
        const body = auth.slice(auth.indexOf(`function ${flow}`), auth.indexOf('}', auth.indexOf(`function ${flow}`)));
        assert.ok(/lockUsername\(false\)/.test(body), `${flow} leaves the username locked with no code to use`);
    }
    assert.ok(/\.input\[readonly\]/.test(read('app.css')), 'a locked field looks exactly like an editable one');
});

test('a confirmation is a modal, asking never destroys what it asks about', () => {
    const ui = read('ui.js');
    const confirm = ui.slice(ui.indexOf('function confirm('), ui.indexOf('return {', ui.indexOf('function confirm(')));
    assert.ok(/renderModal\(title, body, actions\)/.test(confirm), 'a confirmation does not render as a modal');
    assert.ok(!/sheet\(title, body, actions\)/.test(confirm), 'a confirmation still renders as a sheet');
    assert.ok(!/modal = false/.test(confirm), 'the sheet path is still reachable through an option');
    assert.ok(/function sheet\([\s\S]{0,80}closeSheet\(\)/.test(ui), 'sheet() no longer closes the open sheet, which was the reason');
    const css = read('ui.css');
    const z = (sel) => Number(css.slice(css.indexOf(sel)).match(/z-index:\s*(\d+)/)[1]);
    assert.ok(z('.modal-overlay {') > z('.sheet {'), 'a confirmation renders behind the sheet that asked for it');
    assert.ok(z('.modal-overlay {') < z('.toasts {'), 'a confirmation covers the toasts that report its outcome');
});

test('the currency and time zone lists come from the browser, not from a list we keep current', () => {
    const settings = read('js/settings.js');
    const html = read('admin/views/settings.html');
    assert.ok(/<select class='input' id='settingsCurrency'>/.test(html), 'the currency is typed in again');
    assert.ok(/<select class='input' id='settingsTimeZone'>/.test(html), 'there is nowhere to set the instance zone');
    assert.ok(/ui\.fillOptions\(document\.getElementById\('settingsCurrency'\), ui\.currencyOptions\(\)/.test(settings), 'the currency list is not filled from the browser');
    assert.ok(/ui\.fillOptions\(document\.getElementById\('settingsTimeZone'\), ui\.timeZoneOptions\(\)/.test(settings), 'the zone list is not filled from the browser');
    assert.ok(/timeZone: document\.getElementById\('settingsTimeZone'\)\.value/.test(settings), 'the zone is never saved');
    assert.ok(/supportedValuesOf/.test(read('ui.js')), 'the lists are carried in our own source');
    assert.ok(/function when\(iso\)/.test(read('ui.js')), 'there is no shared timestamp formatter');
    for (const file of ['js/settings.js', 'js/sql.js', 'js/accounts.js'])
        assert.ok(!/new Date\([^)]*\)\.toLocale/.test(read(file)), `${file} formats a timestamp in the reader's own zone`);
});

test('a new provider is offered somewhere by default', () => {
    const settings = read('js/settings.js');
    const sheet = settings.slice(settings.indexOf('function openOidcSheet'), settings.indexOf('function callbackFor'));
    assert.ok(/oidcIsEnabled'[\s\S]{0,80}checked: p \? p\.isEnabled : true/.test(sheet), 'a new provider is added switched off');
    assert.ok(/oidcConsoleEnabled'[\s\S]{0,80}checked: p \? p\.consoleEnabled : true/.test(sheet), 'a new provider is offered nowhere');
    assert.ok(/!p\.isEnabled \? 'Off'/.test(settings), 'a parked provider still reads as misconfigured');
});

test('the provider sheet never reads a secret back, and never clears one by omission', () => {
    const settings = read('js/settings.js');
    assert.ok(/hasClientSecret/.test(settings), 'the sheet cannot tell whether a secret is set');
    assert.ok(!/value: p \? p\.clientSecret/.test(settings), 'the sheet renders the stored secret');
    assert.ok(/clientSecret\.ctrl\.value \? \{ clientSecret/.test(settings), 'an untouched secret field is sent, which would clear the stored one');
});

/* the account menu behind the avatar */

function loadAccountMenu(stored) {
    const dom = install(['accountMenu', 'appearanceMenu', 'appearanceTrigger']);
    dom.byId.accountMenu.classList.add('hidden');
    dom.byId.appearanceMenu.classList.add('hidden');
    if (stored) dom.store['baseport.theme'] = stored;

    global.window.matchMedia = () => ({
        matches: true,
        addEventListener() {},
        addListener() {}
    });

    const rows = ['light', 'dark', 'system'].map((name) => {
        const row = dom.element('button');
        row.dataset.appearance = name;
        return row;
    });
    global.document.querySelectorAll = (sel) => (sel.includes('data-appearance') ? rows : []);

    global.ui = eval(read('ui.js').replace(/if \(typeof window[^\n]*\n/g, '') + '; ui');
    const module = {};
    eval(read('js/sidebar.js') +
        '\n;module.toggleAccountMenu = toggleAccountMenu; module.closeAccountMenu = closeAccountMenu;' +
        'module.toggleAppearance = toggleAppearance; module.chooseAppearance = chooseAppearance;' +
        'module.markAppearance = markAppearance;');
    return { dom, module, rows };
}

test('the avatar opens a menu that names the account and its address', () => {
    const shell = read('admin/_shell.html');
    const auth = read('js/auth.js');
    assert.ok(/id='accountMenu'[^>]*role='menu'/.test(shell), 'the account menu is not a menu');
    assert.ok(shell.includes("id='accountMenuName'"), 'the menu does not name the account');
    assert.ok(shell.includes("id='accountMenuEmail'"), 'the menu does not show the address');
    assert.ok(/accountMenuName[\s\S]{0,120}textContent/.test(auth), 'the name is never filled in');
    assert.ok(/'No email address set'/.test(auth), 'an account without an address leaves a blank line');
});

test('the appearance choice offers system, and the tick follows the choice not the screen', () => {
    const { module, rows } = loadAccountMenu(null);
    const tick = () => rows.filter((r) => r.classList.contains('checked')).map((r) => r.dataset.appearance);

    module.markAppearance();
    assert.deepStrictEqual(tick(), ['system'], 'nothing stored is not System');

    module.chooseAppearance('dark');
    assert.deepStrictEqual(tick(), ['dark'], 'choosing Dark did not move the tick');
    assert.strictEqual(localStorage.getItem('baseport.theme'), 'dark', 'the choice was not remembered');

    module.chooseAppearance('system');
    assert.deepStrictEqual(tick(), ['system'], 'choosing System did not move the tick');
    assert.strictEqual(localStorage.getItem('baseport.theme'), null, 'System left an explicit choice behind');
    assert.strictEqual(document.documentElement.dataset.theme, 'dark', 'System did not apply the system theme');
});

test('the submenu never outlives the menu that owns it', () => {
    const { dom, module } = loadAccountMenu('light');
    const open = (el) => !el.classList.contains('hidden');

    module.toggleAccountMenu();
    module.toggleAppearance();
    assert.ok(open(dom.byId.accountMenu) && open(dom.byId.appearanceMenu), 'the submenu did not open');
    assert.strictEqual(dom.byId.appearanceTrigger.getAttribute('aria-expanded'), 'true', 'the trigger does not report its submenu');

    module.closeAccountMenu();
    assert.ok(!open(dom.byId.appearanceMenu), 'the submenu survived its menu');
    assert.strictEqual(dom.byId.appearanceTrigger.getAttribute('aria-expanded'), 'false', 'the trigger still claims a submenu is open');

    module.toggleAccountMenu();
    assert.ok(open(dom.byId.accountMenu) && !open(dom.byId.appearanceMenu), 'reopening restored a stale submenu');

    module.toggleAppearance();
    module.chooseAppearance('dark');
    assert.ok(!open(dom.byId.accountMenu) && !open(dom.byId.appearanceMenu), 'choosing left the menu open');
});

test('the menu has a keyboard exit', () => {
    const sidebar = read('js/sidebar.js');
    assert.ok(/keydown[\s\S]{0,120}Escape[\s\S]{0,80}closeAccountMenu/.test(sidebar), 'Escape does not close the account menu');
});

/* uncaught script failures reach the server */

function loadReporter() {
    const dom = install([]);
    const sent = [];
    const listeners = {};
    global.navigator.sendBeacon = (url, blob) => {
        sent.push({ url, body: JSON.parse(blob.parts.join('')) });
        return true;
    };
    global.Blob = function (parts, opts) {
        this.parts = parts;
        this.type = opts && opts.type;
    };
    global.window.addEventListener = (name, fn) => {
        listeners[name] = fn;
    };
    global.window.location = { pathname: '/_/admin/settings/auth', search: '?token=secret' };
    global.location = global.window.location;
    global.document.createElement = (tag) => {
        const node = element(tag);
        node.querySelector = () => element('span');
        return node;
    };

    const quiet = { ...console, error() {} };
    const ui = eval('(function (console) {' + read('ui.js').replace(/if \(typeof window[^\n]*\n/g, '') + '; return ui; })')(quiet);
    return { ui, sent, listeners, dom };
}

test('an uncaught failure is reported to the server as well as to the screen', () => {
    const { sent, listeners } = loadReporter();
    const reason = new Error('ui.themeChoice is not a function');
    reason.stack = 'TypeError: ui.themeChoice is not a function\n    at markAppearance (http://host/js/sidebar.js:251:26)';

    listeners.unhandledrejection({ reason });

    assert.strictEqual(sent.length, 1, 'the failure never left the browser');
    assert.strictEqual(sent[0].url, '/api/client-errors', 'the failure went somewhere else');
    assert.ok(/ui\.themeChoice is not a function/.test(sent[0].body.message), 'the message was not sent');
    assert.ok(/sidebar\.js:251/.test(sent[0].body.message), 'the throwing frame was dropped');
});

test('the report includes the path and never the query', () => {
    const { sent, listeners } = loadReporter();
    listeners.unhandledrejection({ reason: new Error('boom') });

    assert.strictEqual(sent[0].body.page, '/_/admin/settings/auth', 'the page is not the path');
    assert.ok(!/token=secret/.test(JSON.stringify(sent[0].body)), 'a query token reached the report');
});

test('a failure that repeats every frame is reported once, not once a frame', () => {
    const { sent, listeners } = loadReporter();
    for (let i = 0; i < 5; i++) listeners.unhandledrejection({ reason: new Error('boom') });
    assert.strictEqual(sent.length, 1, 'a throwing loop floods the server');
});

test('reporting uses sendBeacon, a failed report cannot become the next failure', () => {
    const js = read('ui.js');
    const reporter = js.slice(js.indexOf('function sendError'), js.indexOf('/* theme */'));
    assert.ok(/navigator\.sendBeacon/.test(reporter), 'the report is not fire-and-forget');
    assert.ok(!/fetch\(/.test(reporter), 'the report goes through fetch and can reject');
    assert.ok(/try \{/.test(reporter), 'a beacon that throws takes the handler with it');
});

/* the section subbar */

test('a subbar row is the same control as a primary nav row', () => {
    const css = read('app.css');
    const pill = css.slice(css.indexOf('.subbar-pill {'), css.indexOf('.subbar-pill:hover'));
    assert.ok(/border:\s*none/.test(pill), 'a subbar row draws its own border again');
    assert.ok(/background:\s*transparent/.test(pill), 'a subbar row paints its own surface again');
    const nav = css.slice(css.indexOf('.side-nav-btn {'), css.indexOf('.side-nav-btn svg'));
    for (const prop of ['padding', 'border-radius', 'gap'])
        assert.strictEqual(
            (pill.match(new RegExp(`${prop}:([^;]*);`)) || [])[1],
            (nav.match(new RegExp(`${prop}:([^;]*);`)) || [])[1],
            `the subbar row and the nav row disagree on ${prop}`);
});

function loadSidebar() {
    const dom = install(['subbar']);
    global.ui = {
        el: (tag, className, props) => {
            const e = dom.element(tag);
            if (className) e.className = className;
            Object.assign(e, props || {});
            return e;
        },
        escape: s => String(s == null ? '' : s),
    };
    global.navigate = () => {};
    global.routePath = () => '/';
    global.currentTables = [];
    global.currentTablePublicId = null;
    global.formsAll = [];
    global.formEditingId = null;
    global.actionsAll = [];
    global.actionEditingId = null;
    global.savedQueries = [];
    global.currentQueryId = null;
    global.settingsCurrentPage = 'host';

    const js = read('js/sidebar.js');
    const src = js.slice(js.indexOf('const SECTION_ICONS'), js.indexOf('function refreshSidebar'));
    const module = {};
    eval(src + `
;module.renderSidebar = renderSidebar;
module.filters = subbarFilters;
module.sections = SIDEBARS;
`);
    return { dom, module, subbar: dom.byId.subbar };
}

const pills = (bar) => bar.children.filter((c) => c.classList.contains('subbar-pill'));

test('the section root is not a peer of the list it heads', () => {
    const { module, subbar } = loadSidebar();
    global.currentTables = [{ id: 't1', name: 'Orders' }, { id: 't2', name: 'Customers' }];

    module.renderSidebar('tables');

    assert.strictEqual(subbar.children[0].className.includes('subbar-pill'), true, 'the root is not rendered first');
    assert.strictEqual(pills(subbar).length, 3, 'the root and both tables should each be a row');

    assert.ok(!subbar.children[1].classList.contains('subbar-pill'),
        'the root and its list run together');
});

test('every section with a root also names a group, something always divides them', () => {
    const { module } = loadSidebar();
    const rooted = Object.entries(module.sections)
        .filter(([, spec]) => (spec.items ? spec.items() : []).some((i) => i.root))
        .filter(([, spec]) => !spec.group)
        .map(([name]) => name);
    assert.deepStrictEqual(rooted, [], `rooted sections with no group: ${rooted.join(', ')}`);
});

test('a short list gets the same filter box as a long one', () => {
    const { module, subbar } = loadSidebar();
    global.savedQueries = [{ id: 'q1', name: 'Revenue' }];

    module.renderSidebar('sql');

    assert.strictEqual(subbar.children.filter((c) => c.classList.contains('subbar-filter')).length, 1,
        'a short list is left without the box its longer siblings get');
});

test('a filter that matches nothing says so', () => {
    const { module, subbar } = loadSidebar();
    global.currentTables = [{ id: 't1', name: 'Orders' }];
    module.filters.tables = 'zzz';

    module.renderSidebar('tables');

    assert.strictEqual(pills(subbar).length, 1, 'a non-matching table is still listed');
    assert.ok(subbar.children.some((c) => c.classList.contains('subbar-empty')),
        'a filter that matches nothing leaves the list silently empty');
});

test('typing in the filter keeps the caret', () => {
    const js = read('js/sidebar.js');
    assert.ok(/next\.setSelectionRange/.test(js), 'typing in the filter loses the caret');
});

test('the overview summary paints from the payload, not from a later fetch', () => {
    const auth = read('js/auth.js');
    const core = read('js/core.js');
    assert.ok(/me\.stats[\s\S]{0,60}summaryStats = me\.stats/.test(auth), 'the payload stats are never read');
    assert.ok(/if \(me\.tables\) currentTables = me\.tables/.test(auth), 'the payload tables are no longer read either');
    assert.ok(/updateSummary\(currentTables\)/.test(core), 'the overview no longer paints its summary');
    assert.ok(/summaryStats = await fetch\('\/api\/_admin\/settings'\)/.test(core), 'an in-session reload no longer refreshes the stats');
});

test('a field following a settings row clears that row\'s bottom border', () => {
    const css = read('app.css');
    assert.ok(/\.setting-row \+ \.field \{[^}]*margin-top/.test(css), 'a field after a settings row has no top margin');

    const sheet = read('js/tables.js');
    assert.ok(/body\.append\(exposed, apiName, docsEnabled, displayName/.test(sheet),
        'the endpoint sheet no longer appends fields as siblings of its settings rows');
});

test('the import sheet stores its rows, instead of walking up from an input', () => {
    const src = read('js/import.js');
    assert.ok(!/parentElement/.test(src), 'the import sheet reaches a row through an input again');
    assert.ok(/importEls = \{/.test(src), 'the sheet no longer keeps references to its own rows');
    assert.ok(!/getElementById\('imp/.test(src), 'the sheet is re-querying its own elements by id');
});

test('nothing in the runtime whitelist is also a real element', () => {
    const html = readHtml();
    const runtime = ['toasts', 'sheetOverlay', 'pwCurrent', 'pwNew', 'fieldEditError', 'bootstrap', 'fieldType'];
    const present = new Set([...html.matchAll(/id=['"]([\w-]+)['"]/g)].map(m => m[1]));
    const clashes = runtime.filter(id => present.has(id));
    assert.deepStrictEqual(clashes, [], `whitelisted as runtime but rendered in markup: ${clashes.join(', ')}`);
});

test('no id is claimed twice in the composed page', () => {
    const ids = [...readHtml().matchAll(/id=['"]([\w-]+)['"]/g)].map(m => m[1]);
    const seen = new Set();
    const dupes = [...new Set(ids.filter(id => !seen.add(id)))];
    assert.deepStrictEqual(dupes, [], `id used more than once: ${dupes.join(', ')}`);
});

test('unsaved state is tracked by the dirty flags, never sniffed off an input', () => {
    const core = read('js/core.js');
    const fn = core.slice(core.indexOf('function hasUnsavedChanges'), core.indexOf('async function navigate'));
    assert.ok(!/getElementById/.test(fn), 'hasUnsavedChanges reads an element again');
    assert.ok(/tableDirty \|\| fieldsDirty/.test(fn), 'the dirty flags are no longer what it checks');

    const tables = read('js/tables.js');
    const open = tables.slice(tables.indexOf('function selectTable'), tables.indexOf('function paintTableName'));
    assert.ok(/tableDirty = false/.test(open), 'opening a table no longer clears the dirty flag');
});

test('the field editor greys what the server refuses on a computed type', () => {
    const src = read('js/tables.js');
    assert.ok(/function syncComputedGuards/.test(src), 'the editor no longer mirrors the computed guard');
    assert.ok(/syncComputedGuards\(t\)/.test(src), 'the guard never runs when the type changes');
    const fn = src.slice(src.indexOf('function syncComputedGuards'), src.indexOf('function setFieldTypes'));
    assert.ok(/feRequired/.test(fn) && /feIdentifier/.test(fn), 'both refused switches are not covered');
    assert.ok(/el\.checked = false/.test(fn), 'a ticked switch survives the change to a computed type');
});

test('ticking Identifier ticks Required, and the validate call carries both key flags', () => {
    const src = read('js/tables.js');
    const editor = src.slice(src.indexOf("settingSwitch('feUnique'"), src.indexOf("settingSwitch('feHidden'"));
    assert.ok(/required\.checked = true/.test(editor), 'ticking Identifier no longer ticks Required');

    const body = src.slice(src.indexOf('async function validateFieldEditor'), src.indexOf("fetch('/api/_admin/validate-field'"));
    assert.ok(/isUnique: document\.getElementById\('feUnique'\)/.test(body), 'Validate never sends isUnique');
    assert.ok(/isIdentifier: document\.getElementById\('feIdentifier'\)/.test(body), 'Validate never sends isIdentifier');
});

test('the OpenAPI switch is dead while the table is not exposed', () => {
    const tables = read('js/tables.js');
    const sheet = tables.slice(tables.indexOf("'sheetApiDocsEnabled'"), tables.indexOf('const displayName'));
    assert.ok(!/Hides endpoint/.test(sheet), 'the help text still describes the off state as the action');
    assert.ok(/docsEnabled\.ctrl\.disabled = !exposed\.ctrl\.checked/.test(tables), 'the sheet no longer greys the docs switch');

    const settings = read('js/settings.js');
    assert.ok(/docsToggle\.querySelector\('input'\)\.disabled = !t\.apiEnabled/.test(settings), 'the settings list no longer greys the docs switch');
    assert.ok(/await loadApiTables\(\);\s*\n\s*await loadTables\(\);/.test(settings), 'flipping the API switch never repaints the docs switch');
});

test('the API reference preselects the security scheme the document defines', () => {
    const spec = readSource('Api', 'Support', 'OpenApiSpec.cs');
    const match = spec.match(/SecurityScheme\s*=\s*"([^"]+)"/);
    assert.ok(match, 'OpenApiSpec no longer defines SecurityScheme');

    const docs = fs.readFileSync(path.join(__dirname, '..', 'Source', 'Baseport', 'wwwroot', 'docs.html'), 'utf8');
    assert.ok(
        docs.includes(`preferredSecurityScheme: '${match[1]}'`),
        `docs.html does not preselect '${match[1]}'`,
    );
});

test('which types are computed comes from the payload, not a copy in the console', () => {
    const src = read('js/tables.js');
    assert.ok(/if \(t\.computed\) COMPUTED_TYPES\.push\(t\.name\)/.test(src), 'the console no longer reads computed from the payload');
    assert.ok(!/\['calculated', ?'derived', ?'systemid'\]/.test(src), 'the console keeps its own list of computed types');

    const console_ = readSource('Api', 'Endpoints', 'ConsoleEndpoints.cs');
    assert.ok(/fieldTypes = FieldTypes\.All\.Select[^\n]*t\.Computed/.test(console_), 'the bootstrap payload no longer carries Computed');
});

test('a table can be renamed, and every place showing the name repaints together', () => {
    const html = readHtml();
    const tables = read('js/tables.js');
    const records = read('js/records.js');
    assert.ok(/id='tableName'/.test(html), 'the table name is not editable');
    assert.ok(/oninput='markTableDirty\(\)'/.test(html), 'renaming does not arm the save button');
    assert.ok(/name: document\.getElementById\('tableName'\)/.test(tables), 'the rename is never sent');
    assert.ok(/function paintTableName/.test(tables), 'the name is painted in more than one place');
    assert.ok(/paintTableName\(table\)/.test(records), 'a saved rename never repaints the heading');
});

test('the table name reaches innerHTML escaped', () => {
    const tables = read('js/tables.js');
    const sub = tables.slice(tables.indexOf('function paintTableName'), tables.indexOf('function tableSettingsPayload'));
    assert.ok(/ui\.escape\(name\)/.test(sub), 'the table name goes into innerHTML unescaped');
    assert.ok(!/\$\{table\.name\}/.test(sub), 'a raw table name is still interpolated into markup');
});

test('the import sheet posts the file itself, never a JSON body', () => {
    const src = read('js/import.js');
    assert.ok(/new FormData\(\)/.test(src), 'the file is not posted as form data');
    assert.ok(!/Content-Type/.test(src), 'a hand-set content type would drop the multipart boundary');
});

test('preview and create post the same file to the same route, no upload is held server-side', () => {
    const src = read('js/import.js');
    const posts = [...src.matchAll(/fetch\('\/api\/_admin\/tables\/import'/g)];
    assert.strictEqual(posts.length, 2, 'preview and create no longer post the same route');
    assert.ok(/preview: 'true'/.test(src), 'the preview call no longer asks for a preview');
    assert.ok(/importForm\(\{\s*name,/.test(src), 'the create call no longer re-sends the file');
});

test('every import result goes through ui.handle, a rejected file is always surfaced', () => {
    const src = read('js/import.js');
    assert.ok(!/res\.ok/.test(src), 'a fetch result is read by hand instead of through ui.handle');
    const fetches = (src.match(/await fetch\(/g) || []).length;
    const handled = (src.match(/await ui\.handle\(/g) || []).length;
    assert.strictEqual(fetches, handled, 'a fetch in the import flow is not handled');
});

test('the import stub is gone, and nothing still opens a modal that does nothing', () => {
    const src = readAll();
    assert.ok(!/coming soon/i.test(src), 'the "coming soon" import stub is still shipping');
    assert.ok(/function openImportDefinition/.test(read('js/import.js')), 'the definition import is not defined');
    assert.ok(/function openImportRecords/.test(read('js/import.js')), 'the record import is not defined');
});

test('a dropdown trigger transports a caret, and the caret follows the menu instead of a handler', () => {
    const html = readHtml();
    const css = read('app.css');
    const triggers = (html.match(/onclick='toggleDropdown\(event\)'/g) || []).length;
    const carets = (html.match(/class='dropdown-caret'/g) || []).length;
    assert.ok(triggers > 0, 'no dropdown triggers left to check');
    assert.strictEqual(carets, triggers, 'a dropdown trigger is missing its caret');
    assert.ok(/\.dropdown:has\(\.dropdown-menu:not\(\.hidden\)\) \.dropdown-caret/.test(css), 'the caret never rotates');
    assert.ok(!/dropdown-caret/.test(read('js/tables.js')), 'a handler took over rotating the caret');
});

test('New record opens a dropdown whose entries are both defined', () => {
    const html = readHtml();
    assert.ok(/id='newRecordMenu'/.test(html), 'the record dropdown is gone');
    assert.ok(/closeDropdown\(\); openNewRecordModal\(\)/.test(html), 'creating a record by hand is no longer offered');
    assert.ok(/closeDropdown\(\); openImportRecords\(\)/.test(html), 'importing records is not offered');
});

/* httpRequest action step */
test('the action editor offers an HTTP request step', () => {
    const html = read('admin/views/actions.html');
    assert.ok(/onclick='addActionStep\("httpRequest"\)'/.test(html), 'no button adds an httpRequest step');
});

test('addActionStep builds an httpRequest step with url, method, headers and bodyTemplate', () => {
    const js = read('js/actions.js');
    const addStep = js.slice(js.indexOf('function addActionStep('), js.indexOf('const STEP_LABELS'));
    assert.ok(/type:\s*'httpRequest'.*url:\s*''.*method:\s*'POST'.*headers:\s*\{\}.*bodyTemplate:\s*\{\}/.test(addStep),
        "httpRequest is not created with url/method/headers/bodyTemplate");
});

test('the httpRequest step editor renders headers and bodyTemplate as key/value lists', () => {
    const js = read('js/actions.js');
    assert.ok(js.includes('function actionHttpRequestEditor('), 'actionHttpRequestEditor is missing');
    assert.ok(js.includes('function actionKeyValueList('), 'actionKeyValueList is missing');
    assert.ok(/actionKeyValueList\('Headers', step\.headers/.test(js), 'headers is not rendered through the key/value list');
    assert.ok(/actionKeyValueList\('Body', step\.bodyTemplate/.test(js), 'bodyTemplate is not rendered through the key/value list');
});

test('renaming a key/value row does not rebuild the row (a rename fires on blur, into the value input next to it)', () => {
    const js = read('js/actions.js');
    const list = js.slice(js.indexOf('function actionKeyValueList('), js.indexOf('async function saveAction('));
    const onchange = list.slice(list.indexOf('keyInp.onchange'), list.indexOf('row.appendChild(keyInp)'));
    assert.ok(!onchange.includes('renderRows()'), 'renaming a key still rebuilds every row in the list');
    assert.ok(/let currentKey = initialKey/.test(list), 'each row no longer tracks its own current key across a rename');
});

// runs embed.js functions by name
function embedFunctions(...names) {
    const src = read('embed.js');
    const bodies = names.map((name) => {
        const start = src.indexOf(`function ${name}(`);
        assert.ok(start >= 0, `embed.js no longer defines ${name}`);
        let depth = 0;
        for (let i = src.indexOf('{', start); i < src.length; i++) {
            if (src[i] === '{') depth++;
            if (src[i] === '}' && --depth === 0) return src.slice(start, i + 1);
        }
        throw new Error(`unbalanced braces in ${name}`);
    });
    return new Function(`const displayValue = (v) => String(v); ${bodies.join('\n')} return { ${names.join(', ')} };`)();
}

test('a multiselect value cannot inject markup through a render expression', () => {
    const { renderCell } = embedFunctions('renderCell', 'escapeDeep', 'escapeHtml');
    const out = renderCell("'<td>' + data.tags + '</td>'", { tags: ['ok', '<a href=https://evil.example style=position:fixed>win</a>'] });

    assert.ok(!out.includes('<a '), `array item reached the markup raw: ${out}`);
    assert.ok(out.includes('ok'), 'the harmless item was lost');
});

test('a json field value is escaped before a render expression sees it', () => {
    const { renderCell } = embedFunctions('renderCell', 'escapeDeep', 'escapeHtml');
    const out = renderCell("'<b>' + data.meta.note + '</b>'", { meta: { note: '<img src=x onerror=alert(1)>' } });

    assert.ok(!out.includes('<img'), `object property reached the markup raw: ${out}`);
});

test('setSafeHtml strips style attributes', () => {
    const src = read('embed.js');
    const body = src.slice(src.indexOf('function setSafeHtml('), src.indexOf('function safeEval('));
    assert.ok(/name === 'style'/.test(body), 'setSafeHtml keeps style attributes');
});

let stray = 0;
process.on('unhandledRejection', (reason) => {
    console.log(`  FAIL unhandled rejection outside a test\n       ${reason && reason.message || reason}`);
    stray++;
});

(async () => {
    for (const [name, fn] of queue) {
        try {
            await fn();
            console.log(`  ok   ${name}`);
            passed++;
        } catch (e) {
            console.log(`  FAIL ${name}\n       ${e.message}`);
            failed++;
        }
    }
    await new Promise((resolve) => setImmediate(resolve));
    failed += stray;
    console.log(`\n${passed} passed, ${failed} failed`);
    process.exit(failed ? 1 : 0);
})();
