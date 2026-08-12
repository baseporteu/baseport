/* sign in, sign out, and the boot sequence */

// Nothing loads until the session is confirmed; this reads the payload the server rendered into the page (present on a full load, absent after a sign-in).
function bootstrap() {
  const el = document.getElementById('bootstrap');
  if (!el) return null;
  try {
    return JSON.parse(el.textContent);
  } catch (e) {
    return null;
  }
}

// The console and the login card live on separate routes now, so the shell and every console script never load for a signed-out visitor.
function isAuthPage() {
  return !document.getElementById('appShell');
}

async function boot() {
  // Server-rendered on a full load, so the first paint costs no round trips.
  const me =
    bootstrap() ||
    (await fetch('/api/auth/me')
      .then((r) => r.json())
      .catch(() => ({ authenticated: false })));
  // Each page forwards to the other: the console to the login page when the session is gone, the login page to the console once it is back.
  if (!me.authenticated) {
    if (!isAuthPage()) {
      location.replace('/_/auth');
      return;
    }
    showLogin();
    return;
  }
  if (isAuthPage()) {
    // Pending session: replace the seeded password before the console loads.
    if (me.authenticated && me.mustChangePassword) {
      showChangePassword();
      return;
    }
    location.replace('/_/admin');
    return;
  }
  document.getElementById('appShell').hidden = false;

  const avatar = document.getElementById('railAvatar');
  // The bootstrap payload nests the account under `user`; the /api/auth/me reply is flat and capitalised. Read both shapes.
  const username = me.username || me.user?.username || me.Username || '';
  avatar.innerText = (username || 'A').slice(0, 1).toUpperCase();
  avatar.title = `Signed in as ${username}`;

  // Tables and settings came down with the page, so the router paints straight away.
  if (me.tables) currentTables = me.tables;
  if (me.settings) settingsData = { ...(settingsData || {}), ...me.settings };

  greet();
  applyRailState();
  // guarded: a stale cached tables.js without this function shouldn't throw here
  if (typeof initFieldTypeCombobox === 'function') initFieldTypeCombobox();
  wireFieldExprValidation();
  // The router paints from the URL, so a deep link survives a reload.
  await render();
}

function showLogin() {
  // On the login page only: the shell no longer carries the card.
  const login = document.getElementById('loginScreen');
  if (login) login.hidden = false;
  const user = document.getElementById('loginUser');
  if (user) user.focus();
}

// Shown in place of the login card for a session still on the seeded password.
function showChangePassword() {
  document.getElementById('loginForm').hidden = true;
  document.getElementById('forgotCard').hidden = true;
  const card = document.getElementById('changeCard');
  card.hidden = false;
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
  if (next.length < PASSWORD_MIN) return { field: 'newPass', message: `Use at least ${PASSWORD_MIN} characters.` };
  if (next.length > PASSWORD_MAX) return { field: 'newPass', message: `Use at most ${PASSWORD_MAX} characters.` };
  if (next === current) return { field: 'newPass', message: 'The new password must be different from the current one.' };
  if (live && !again) return null;
  if (again !== next) return { field: 'newPass2', message: 'The two new passwords do not match.' };
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
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      currentPassword: document.getElementById('curPass').value,
      newPassword: document.getElementById('newPass').value,
    }),
  });
  if (!(await ui.handle(res, { failure: 'Could not change the password.' }))) {
    hintChange({ field: 'curPass', message: 'The current password is incorrect.' }, true);
    return false;
  }
  for (const field of CHANGE_FIELDS) document.getElementById(field).value = '';
  // The server re-issued the session without the pending flag; the console loads.
  location.replace('/_/admin');
  return false;
}

// True once a code has been requested, so the same button then signs in with it.
let otpRequested = false;
let otpExpiryTimer = null;

function resetOtpFlow() {
  otpRequested = false;
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
  document.getElementById('loginForm').hidden = true;
  document.getElementById('forgotCard').hidden = false;
}

function backToLogin() {
  document.getElementById('forgotCard').hidden = true;
  document.getElementById('loginForm').hidden = false;
  resetOtpFlow();
}

async function signIn(ev) {
  ev.preventDefault();
  const btn = document.getElementById('loginBtn');
  const username = document.getElementById('loginUser').value;

  // First press in code mode asks for one; the second signs in with it.
  if (authMode() === 'otp' && !otpRequested) {
    btn.disabled = true;
    try {
      const sent = await ui.send('/api/auth/otp', {
        method: 'POST',
        body: { username },
        failure: 'Could not request a code.',
      });
      if (!sent) return false;
      otpRequested = true;
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
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(
        authMode() === 'otp'
          ? { username, otp: document.getElementById('otpCode').value }
          : { username, password: document.getElementById('loginPass').value },
      ),
    });
    if (!(await ui.handle(res, { failure: 'Sign-in failed.' }))) {
      // A wrong or stale code is spent, so the next attempt needs a new one.
      if (authMode() === 'otp') {
        expireOtpFlow();
      }
      return false;
    }
    document.getElementById('loginPass').value = '';
    document.getElementById('otpCode').value = '';
    // The login page was rendered for a signed-out visitor, so go to the console rather than re-running boot() against a stale payload.
    location.replace('/_/admin');
    return false;
  } finally {
    btn.disabled = false;
  }
  return false;
}

async function signOut() {
  await fetch('/api/auth/logout', { method: 'POST' });
  location.reload();
}

// A 401 mid-session means the session lapsed: send the console back to the login page.
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

boot();
