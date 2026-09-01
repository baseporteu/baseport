const nativeFetch = window.fetch.bind(window);

let connectionOk = true;
let connectionProbe = null;

function isNetworkFailure(err) {
    return Boolean(err) && err.name !== 'AbortError' && err instanceof TypeError;
}

window.fetch = function (...args) {
    return nativeFetch(...args).then(
        (res) => {
            setConnection(true);
            return res;
        },
        (err) => {
            if (isNetworkFailure(err)) setConnection(false);
            throw err;
        },
    );
};

function connectionBar() {
    let bar = document.getElementById('connectionBar');
    if (bar) return bar;
    bar = document.createElement('div');
    bar.id = 'connectionBar';
    bar.className = 'connection-bar';
    bar.setAttribute('role', 'status');
    bar.setAttribute('aria-live', 'polite');
    bar.textContent = 'No connection to the server. This page will pick up where it left off once it is back.';
    bar.hidden = true;
    document.body.append(bar);
    return bar;
}

function setConnection(next) {
    if (next === connectionOk) return;
    connectionOk = next;
    connectionBar().hidden = next;
    if (!next) {
        startConnectionProbe();
        return;
    }
    stopConnectionProbe();
    resumeAfterConnection();
}

function startConnectionProbe() {
    if (connectionProbe) return;
    connectionProbe = setInterval(() => {
        nativeFetch('/api/auth/me', { cache: 'no-store' })
            .then(() => setConnection(true))
            .catch(() => {});
    }, 5000);
}

function stopConnectionProbe() {
    clearInterval(connectionProbe);
    connectionProbe = null;
}

function resumeAfterConnection() {
    const shell = document.getElementById('appShell');
    try {
        if (shell && shell.hidden && typeof boot === 'function') Promise.resolve(boot()).catch(() => {});
        else if (typeof render === 'function') Promise.resolve(render()).catch(() => {});
    } catch (e) {}
}

window.addEventListener('offline', () => setConnection(false));
window.addEventListener('online', () => {
    nativeFetch('/api/auth/me', { cache: 'no-store' })
        .then(() => setConnection(true))
        .catch(() => {});
});
