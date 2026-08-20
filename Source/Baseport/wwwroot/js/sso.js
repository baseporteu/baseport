/* the identity-provider buttons on both sign-in screens, and the outcome the callback comes back with */

// The callback only ever puts a code in the address bar, so nothing the provider wrote reaches the screen and the wording lives here.
const SSO_PROBLEMS = {
    failed: 'Could not finish signing in with that provider. The server log has the reason.',
    denied: 'That sign-in was cancelled at the provider.',
    no_account: 'No account here is linked to that identity.',
    disabled: 'That account is disabled.',
    no_console: 'That account does not have console access.',
    not_linked: 'That identity was not linked. It may already belong to another account, or the session that started the link is gone.',
};

// Server-rendered into the page, so the buttons are there on the first paint instead of after a round trip.
function ssoProviders() {
    const el = document.getElementById('bootstrap');
    if (!el) return [];
    try {
        return JSON.parse(el.textContent).providers || [];
    } catch (e) {
        return [];
    }
}

function ssoRender(surface) {
    const block = document.getElementById('ssoBlock');
    const host = document.getElementById('ssoProviders');
    if (!block || !host) return;

    const providers = ssoProviders();
    block.hidden = providers.length === 0;
    host.innerHTML = '';

    for (const provider of providers) {
        const link = document.createElement('a');
        link.className = 'sso-btn';
        link.href = `/api/auth/oidc/${encodeURIComponent(provider.slug)}/start?surface=${surface}`;
        link.rel = 'nofollow';

        const label = document.createElement('span');
        label.textContent = `Continue with ${provider.name}`;
        link.append(label);

        const arrow = document.createElement('span');
        arrow.className = 'sso-btn-arrow';
        arrow.setAttribute('aria-hidden', 'true');
        arrow.innerHTML =
            "<svg width='14' height='14' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M7 17 17 7'/><path d='M8 7h9v9'/></svg>";
        link.append(arrow);

        host.append(link);
    }
}

// A failed round trip lands back here with ?sso=<code>; report it once and take it out of the address bar so a reload does not repeat it.
function ssoReportOutcome() {
    const code = new URLSearchParams(location.search).get('sso');
    if (!code) return;

    const url = new URL(location.href);
    url.searchParams.delete('sso');
    history.replaceState(null, '', url.pathname + url.search + url.hash);

    ui.toast(SSO_PROBLEMS[code] || SSO_PROBLEMS.failed, 'error', 8000);
}

// The screen that hosts the buttons owns the surface name: it decides which door a provider was offered for.
function ssoInit(surface) {
    ssoRender(surface);
    ssoReportOutcome();
}
