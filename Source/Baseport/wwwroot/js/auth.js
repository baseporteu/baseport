
function bootstrap() {
    const el = document.getElementById('bootstrap');
    if (!el) return null;
    try {
        return JSON.parse(el.textContent);
    } catch (e) {
        return null;
    }
}

function isAuthPage() {
    return !document.getElementById('appShell');
}

async function boot() {
    const me = bootstrap() || (await fetch('/api/auth/me').then((r) => r.json()).catch(() => null));
    if (!me) return;
    if (!me.authenticated) {
        if (!isAuthPage()) {
            location.replace('/_/auth');
            return;
        }
        showLogin();
        return;
    }
    if (isAuthPage()) {
        if (me.authenticated && me.mustChangePassword) {
            showChangePassword();
            return;
        }
        location.replace('/_/admin');
        return;
    }
    document.getElementById('appShell').hidden = false;

    const avatar = document.getElementById('sidebarAvatar');
    const username = me.username || me.user?.username || me.Username || '';
    const avatarUri = me.avatar || me.user?.avatar || me.Avatar || '';
    if (avatarUri) {
        avatar.replaceChildren(Object.assign(new Image(32, 32), { src: avatarUri, alt: '' }));
    } else {
        avatar.innerText = (username || 'A').slice(0, 1).toUpperCase();
    }
    avatar.title = `Signed in as ${username}`;

    const accountName = document.getElementById('sidebarAccountName');
    if (accountName) accountName.textContent = username;

    const email = me.email || me.user?.email || me.Email || '';
    const menuName = document.getElementById('accountMenuName');
    if (menuName) menuName.textContent = username;
    const menuEmail = document.getElementById('accountMenuEmail');
    if (menuEmail) menuEmail.textContent = email || 'No email address set';
    markAppearance();

    if (me.user) currentAccount = me.user;
    if (me.tables) currentTables = me.tables;
    if (me.forms) formsAll = me.forms;
    if (me.actions) actionsAll = me.actions;
    if (me.sql) savedQueries = me.sql;
    // server-rendered alongside the tables
    if (me.stats) summaryStats = me.stats;
    if (me.settings) settingsData = {
        ...(settingsData || {}),
        ...me.settings
    };
    if (settingsData) ui.timeZone(settingsData.timeZone || 'UTC');

    if (me.fieldTypes && typeof setFieldTypes === 'function') setFieldTypes(me.fieldTypes, me.fieldTypeGroups);

    greet(username);
    applySidebarState();
    if (typeof initFieldTypeCombobox === 'function') initFieldTypeCombobox();
    await render();
}

function showPanel(name) {
    document.getElementById('loginForm').hidden = name !== 'login';
    document.getElementById('forgotCard').hidden = name !== 'forgot';
    document.getElementById('changeCard').hidden = name !== 'change';
    const sso = document.getElementById('ssoBlock');
    if (sso) sso.hidden = name !== 'login' || ssoProviders().length === 0;
}

function showLogin() {
    const login = document.getElementById('loginScreen');
    if (login) login.hidden = false;
    showPanel('login');
    const user = document.getElementById('loginUser');
    if (user) user.focus();
}

function switchAuthMode(mode) {
    const otp = mode === 'otp';
    hideTotpField();
    document.getElementById('tabPassword').classList.toggle('active', !otp);
    document.getElementById('tabOtp').classList.toggle('active', otp);

    const password = document.getElementById('loginPass');
    const code = document.getElementById('otpCode');
    document.getElementById('passwordContainer').hidden = otp;
    document.getElementById('otpContainer').hidden = !otp;
    password.toggleAttribute('required', !otp);
    code.toggleAttribute('required', otp);

    document.getElementById('loginBtn').textContent = otp ? 'Request code' : 'Sign in';
    resetOtpFlow();
}

function showChangePassword() {
    showPanel('change');
    const card = document.getElementById('changeCard');
    card.addEventListener('input', () => refreshChangeState(true));
    document.getElementById('curPass').focus();
}

const PASSWORD_MIN = 10;
const PASSWORD_MAX = 128;
const CHANGE_FIELDS = ['curPass', 'newPass', 'newPass2'];

function changeProblem(live) {
    const current = document.getElementById('curPass').value;
    const next = document.getElementById('newPass').value;
    const again = document.getElementById('newPass2').value;
    if (live && !next) return null;
    if (next.length < PASSWORD_MIN) return {
        field: 'newPass',
        message: `Use at least ${PASSWORD_MIN} characters.`
    };
    if (next.length > PASSWORD_MAX) return {
        field: 'newPass',
        message: `Use at most ${PASSWORD_MAX} characters.`
    };
    if (next === current) return {
        field: 'newPass',
        message: 'The new password must be different from the current one.'
    };
    if (live && !again) return null;
    if (again !== next) return {
        field: 'newPass2',
        message: 'The two new passwords do not match.'
    };
    return null;
}

function hintChange(problem, focus) {
    for (const field of CHANGE_FIELDS)
        document.getElementById(field).classList.toggle('input-invalid', problem?.field === field);
    const hint = document.getElementById('changeHint');
    hint.innerText = problem ? problem.message : '';
    hint.classList.toggle('hidden', !problem);
    if (problem && focus) document.getElementById(problem.field).focus();
}

function refreshChangeState(live) {
    const problem = changeProblem(live);
    hintChange(problem, !live);
    return !problem;
}

async function changePassword(ev) {
    ev.preventDefault();
    if (!refreshChangeState(false)) return false;
    const res = await fetch('/api/auth/password', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            currentPassword: document.getElementById('curPass').value,
            newPassword: document.getElementById('newPass').value,
        }),
    });
    if (!(await ui.handle(res, {
            failure: 'Could not change the password.'
        }))) {
        hintChange({
            field: 'curPass',
            message: 'The current password is incorrect.'
        }, true);
        return false;
    }
    for (const field of CHANGE_FIELDS) document.getElementById(field).value = '';
    location.replace('/_/admin');
    return false;
}

let otpRequested = false;
let otpExpiryTimer = null;

function lockUsername(locked) {
    const user = document.getElementById('loginUser');
    if (!user) return;
    user.readOnly = locked;
}

function resetOtpFlow() {
    otpRequested = false;
    lockUsername(false);
    clearTimeout(otpExpiryTimer);
    otpExpiryTimer = null;
    const row = document.getElementById('otpCodeRow');
    if (row) row.hidden = true;
    const code = document.getElementById('otpCode');
    if (code) {
        code.value = '';
        code.disabled = true;
        code.placeholder = 'Enter the code';
    }
    const btn = document.getElementById('loginBtn');
    if (btn) btn.textContent = authMode() === 'otp' ? 'Request code' : 'Sign in';
}

function expireOtpFlow() {
    otpRequested = false;
    lockUsername(false);
    clearTimeout(otpExpiryTimer);
    otpExpiryTimer = null;
    const code = document.getElementById('otpCode');
    if (code) {
        code.value = '';
        code.disabled = true;
        code.placeholder = 'Expired';
    }
    const btn = document.getElementById('loginBtn');
    if (btn) btn.textContent = authMode() === 'otp' ? 'Request code' : 'Sign in';
}

function authMode() {
    return document.getElementById('tabOtp').classList.contains('active') ? 'otp' : 'password';
}

function showForgot() {
    showPanel('forgot');
}

function backToLogin() {
    showPanel('login');
    resetOtpFlow();
}

async function signIn(ev) {
    ev.preventDefault();
    const btn = document.getElementById('loginBtn');
    const username = document.getElementById('loginUser').value;

    if (authMode() === 'otp' && !otpRequested) {
        btn.disabled = true;
        try {
            const sent = await ui.send('/api/auth/otp', {
                method: 'POST',
                body: {
                    username
                },
                failure: 'Could not request a code.',
            });
            if (!sent) return false;
            otpRequested = true;
            lockUsername(true);
            const row = document.getElementById('otpCodeRow');
            if (row) row.hidden = false;
            const code = document.getElementById('otpCode');
            code.disabled = false;
            const seconds = sent.expiresInSeconds || 60;
            code.placeholder = `Enter the code in ${seconds}s.`;
            document.getElementById('loginBtn').textContent = 'Sign in';
            otpExpiryTimer = setTimeout(expireOtpFlow, seconds * 1000);
            code.focus();
        } finally {
            btn.disabled = false;
        }
        return false;
    }

    btn.disabled = true;
    try {
        const res = await fetch('/api/auth/login', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(
                authMode() === 'otp' ?
                {
                    username,
                    otp: document.getElementById('otpCode').value
                } :
                {
                    username,
                    password: document.getElementById('loginPass').value,
                    code: document.getElementById('totpCode').value
                },
            ),
        });
        const reply = await res.clone().json().catch(() => null);
        if (!(await ui.handle(res, {
                failure: 'Sign-in failed.'
            }))) {
            if (reply?.totp) showTotpField();
            if (authMode() === 'otp') {
                expireOtpFlow();
            }
            return false;
        }
        document.getElementById('loginPass').value = '';
        document.getElementById('otpCode').value = '';
        location.replace('/_/admin');
        return false;
    } finally {
        btn.disabled = false;
    }
    return false;
}

function showTotpField() {
    document.getElementById('loginUser').readOnly = true;
    document.getElementById('totpContainer').hidden = false;
    const code = document.getElementById('totpCode');
    code.value = '';
    code.focus();
}

function hideTotpField() {
    const row = document.getElementById('totpContainer');
    if (!row) return;
    row.hidden = true;
    document.getElementById('totpCode').value = '';
    lockUsername(false);
}

async function openTwoFactor() {
    const me = await ui.send('/api/auth/me', { failure: 'Could not read your account.' });
    if (!me) return;
    if (me.totp) twoFactorOffSheet();
    else twoFactorIntroSheet();
}

function twoFactorIntroSheet() {
    const body = ui.el('div', 'token-panel');
    body.append(ui.el('p', 'muted', {
        textContent: 'Ask for a code from an authenticator app after your password, at every sign-in.'
    }));
    const actions = ui.el('div', 'form-actions');
    const start = ui.button('Set up', () => ui.busy(start, twoFactorSetup));
    actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }), start);
    ui.sheet('Two-factor', body, actions);
}

async function twoFactorSetup() {
    const setup = await ui.send('/api/auth/totp/setup', { method: 'POST', failure: 'Could not start the setup.' });
    if (!setup) return;

    const body = ui.el('div', 'token-panel');
    body.append(ui.el('p', 'muted', {
        textContent: 'Add this key to your authenticator app, then enter the code it shows.'
    }));
    const key = ui.el('pre', 'code-block', { textContent: setup.secret });
    body.append(ui.copyable(key, setup.secret));
    const uri = ui.el('pre', 'code-block', { textContent: setup.uri });
    body.append(ui.copyable(uri, setup.uri));
    const code = ui.field('Code', { id: 'totpConfirmCode', placeholder: '123456' });
    code.ctrl.inputMode = 'numeric';
    code.ctrl.autocomplete = 'one-time-code';
    body.append(code);

    const actions = ui.el('div', 'form-actions');
    const confirm = ui.button('Turn on', () => ui.busy(confirm, async () => {
        const done = await ui.send('/api/auth/totp/confirm', {
            method: 'POST',
            body: { code: code.ctrl.value.trim() },
            success: 'Two-factor sign-in is on. Other sessions were signed out.',
            failure: 'Could not turn on two-factor sign-in.'
        });
        if (done) ui.closeSheet();
    }));
    actions.append(ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }), confirm);
    ui.sheet('Two-factor', body, actions);
    code.ctrl.focus();
}

function twoFactorOffSheet() {
    const body = ui.el('div', 'token-panel');
    body.append(ui.el('p', 'muted', {
        textContent: 'Two-factor sign-in is on. Turning it off needs your password and a current code.'
    }));
    const password = ui.field('Password', { id: 'totpOffPassword', type: 'password' });
    password.ctrl.autocomplete = 'current-password';
    const code = ui.field('Code', { id: 'totpOffCode', placeholder: '123456' });
    code.ctrl.inputMode = 'numeric';
    code.ctrl.autocomplete = 'one-time-code';
    body.append(password, code);

    const actions = ui.el('div', 'form-actions');
    const off = ui.button('Turn off', () => ui.busy(off, async () => {
        const done = await ui.send('/api/auth/totp', {
            method: 'DELETE',
            body: { password: password.ctrl.value, code: code.ctrl.value.trim() },
            success: 'Two-factor sign-in is off.',
            failure: 'Could not turn off two-factor sign-in.'
        });
        if (done) ui.closeSheet();
    }), { variant: 'btn-danger' });
    actions.append(off, ui.button('Cancel', ui.closeSheet, { variant: 'btn-outline' }));
    ui.sheet('Two-factor', body, actions);
}

async function signOut() {
    const ok = await ui.confirm({
        title: 'Sign out',
        message: 'End this session?',
        confirmLabel: 'Sign out',
        danger: true,
    });
    if (!ok) return;
    await fetch('/api/auth/logout', {
        method: 'POST'
    });
    location.reload();
}

const rawFetch = window.fetch;
window.fetch = async (...args) => {
    const res = await rawFetch(...args);
    const url = typeof args[0] === 'string' ? args[0] : (args[0] && args[0].url) || '';
    if (res.status === 401 && url.startsWith('/api') && !url.startsWith('/api/auth/')) {
        if (isAuthPage()) showLogin();
        else location.replace('/_/auth');
    }
    return res;
};

if (document.getElementById('loginForm')) {
    ssoInit('console');
    switchAuthMode('password');
}

boot();