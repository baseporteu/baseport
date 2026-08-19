const bpAuth = (() => {
    const KEY = 'baseport.user.tokens';
    const API = '/api/auth/v1';

    function tokens() {
        try {
            return JSON.parse(localStorage.getItem(KEY) || 'null');
        } catch (e) {
            return null;
        }
    }

    function store(next) {
        if (next) localStorage.setItem(KEY, JSON.stringify(next));
        else localStorage.removeItem(KEY);
        return next;
    }

    async function refresh() {
        const current = tokens();
        if (!current || !current.refresh_token) return null;
        const res = await fetch(`${API}/refresh`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                refresh_token: current.refresh_token
            }),
        });
        if (!res.ok) return store(null);
        return store(await res.json());
    }

    async function authFetch(path, options = {}) {
        let current = tokens();
        if (current && current.expires_at && current.expires_at - 60 <= Math.floor(Date.now() / 1000)) {
            current = await refresh();
        }
        const init = {
            method: options.method || 'GET',
            headers: {}
        };
        if (current) init.headers.Authorization = `Bearer ${current.auth_token}`;
        if (options.body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(options.body);
        }
        return fetch(API + path, init);
    }

    async function post(path, body) {
        const res = await fetch(API + path, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(body),
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            ui.toast((data.errors || ['Something went wrong.']).join(' '), 'error');
            return null;
        }
        return data;
    }

    return {
        tokens,
        store,
        refresh,
        authFetch,
        post,
        signedIn: () => tokens() !== null
    };
})();

async function bpSignIn(event) {
    event.preventDefault();
    const tokens = await bpAuth.post('/login', {
        email_or_username: document.getElementById('authHandle').value.trim(),
        password: document.getElementById('authPassword').value,
    });
    if (!tokens) return false;
    bpAuth.store(tokens);
    location.href = '/auth/profile';
    return false;
}

async function bpRegister(event) {
    event.preventDefault();
    const tokens = await bpAuth.post('/register', {
        email: document.getElementById('authEmail').value.trim(),
        username: document.getElementById('authUsername').value.trim(),
        password: document.getElementById('authPassword').value,
    });
    if (!tokens) return false;
    bpAuth.store(tokens);
    location.href = '/auth/profile';
    return false;
}

// /auth redirects here unconditionally, so a signed-in visitor would otherwise be asked to sign in again. The session may be a cookie rather than stored tokens, which is what a console sign-in leaves behind, so the server is asked when there is nothing local.
async function bpGuestOnly() {
    if (bpAuth.signedIn()) {
        location.replace('/auth/profile');
        return;
    }
    const res = await fetch('/api/auth/v1/status').catch(() => null);
    const data = res && res.ok ? await res.json().catch(() => ({})) : {};
    if (data.authenticated) location.replace('/auth/profile');
}

async function bpLoadProfile() {
    // No stored tokens is not signed out: the cookie the sign-in set is sent with this request anyway.
    const res = await bpAuth.authFetch('/status');
    const data = await res.json().catch(() => ({}));
    if (!res.ok || !data.authenticated) {
        bpAuth.store(null);
        location.href = '/auth/login';
        return;
    }
    document.getElementById('profileUsername').textContent = data.username || '';
    document.getElementById('profileEmail').textContent = data.email || 'No email address set';
    document.getElementById('profileId').textContent = data.sub || '';
}

async function bpChangePassword(event) {
    event.preventDefault();
    const res = await bpAuth.authFetch('/change_password', {
        method: 'POST',
        body: {
            current_password: document.getElementById('currentPassword').value,
            new_password: document.getElementById('newPassword').value,
        },
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
        ui.toast((data.errors || ['Could not change the password.']).join(' '), 'error');
        return false;
    }
    bpAuth.store(data);
    document.getElementById('currentPassword').value = '';
    document.getElementById('newPassword').value = '';
    ui.toast('Your password has been changed.', 'success');
    return false;
}

async function bpSignOut() {
    const current = bpAuth.tokens();
    // Called unconditionally: a cookie session has nothing stored locally, and the server is what clears it.
    await fetch('/api/auth/v1/logout', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            refresh_token: current ? current.refresh_token : ''
        }),
    }).catch(() => {});
    bpAuth.store(null);
    location.href = '/auth/login';
}

async function bpDeleteAccount() {
    const ok = await ui.confirm({
        title: 'Delete your account?',
        message: 'Your account and every session on it are removed. This cannot be undone.',
        confirmLabel: 'Delete',
        danger: true,
    });
    if (!ok) return;
    const res = await bpAuth.authFetch('/delete', {
        method: 'DELETE'
    });
    if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        ui.toast((data.errors || ['Could not delete the account.']).join(' '), 'error');
        return;
    }
    bpAuth.store(null);
    location.href = '/auth/login';
}
