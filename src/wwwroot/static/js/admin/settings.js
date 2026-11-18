import { ago, api, auth, copy, esc, icon, toast } from '../core.js';
import { bindTests, collect, fields, imapTest, smtpTest, waitForRestart } from './settings-form.js';
import { confirmDialog, head } from './ui.js';

const extras = {
    Email: () => smtpTest('smtp'),
    'Support desk': () => imapTest() + smtpTest('support-smtp', 'Send a test reply'),
};

export async function render({ app, params, alive }) {
    if (!auth.user.is_root) {
        app.innerHTML = '<div class="empty"><h2>Root only</h2><p>Only the root administrator can change server settings.</p></div>';
        return;
    }

    const data = await api('/api/v1/admin/settings');

    if (!alive())
        return;

    const tabs = [...data.groups, 'Server'];
    let active = tabs.includes(params.get('tab')) ? params.get('tab') : tabs[0];

    const derived = group => group === 'Sign-in' ? `<div class="notice">
            <strong>Discord:</strong> add <button class="pill mono" data-copy="${esc(data.derived.discord_redirect)}">${esc(data.derived.discord_redirect)}</button> as a redirect in the Discord Developer Portal → OAuth2.<br>
            <strong>Passkeys</strong> need no keys: they're tied to <span class="mono">${esc(data.derived.passkey_rp_id)}</span> (from the public URL) and only work on <span class="mono">${esc(data.derived.passkey_origin)}</span>. Browsers require HTTPS except on localhost. Changing the public URL's host makes existing passkeys stop working.</div>`
        : group === 'Payments' ? `<div class="notice">Point both Stripe webhooks (platform and Connect) at <button class="pill mono" data-copy="${esc(data.derived.stripe_webhook)}">${esc(data.derived.stripe_webhook)}</button>. Payments turn on once the secret key and a webhook secret are set; creators can sell once every support contact detail is filled in.</div>` : '';

    const server = () => {
        const p = data.platform;
        return `<section class="surface panel">
                <h2>${icon('server')} This server</h2>
                <dl class="details-list">
                    <div><dt>Version</dt><dd class="mono">OpenForge ${esc(p.version)}</dd></div>
                    <div><dt>System</dt><dd>${esc(p.os)}</dd></div>
                    <div><dt>Running as</dt><dd class="mono">${esc(p.user)}${p.under_systemd ? ' · under systemd' : ''}</dd></div>
                    <div><dt>Folder</dt><dd class="mono">${esc(p.folder)}</dd></div>
                    <div><dt>Settings file</dt><dd class="mono">${esc(p.settings_file)}</dd></div>
                    <div><dt>Started</dt><dd>${esc(ago(p.started_at))}</dd></div>
                </dl>
                <div class="row drawer-section"><button class="button" id="restart">${icon('refresh')}Restart server</button><span class="hint">Applies saved settings. Takes a few seconds; open pages reconnect on their own.</span></div>
            </section>
            <section class="surface panel">
                <div class="row"><h2>${icon('terminal')} Run as a service</h2><span class="spacer"></span><button class="button subtle small" data-copy-unit>${icon('copy')}Copy unit</button></div>
                <p class="hint">${p.linux ? (p.systemd ? 'This unit runs this copy of OpenForge with systemd: it starts on boot and comes back if it stops.' : 'This system doesn’t seem to use systemd; adapt the unit for your init system.') : 'systemd units are for Linux servers. On this system, run OpenForge with your platform’s service manager.'} It was generated for the folder, user and executable this server is using now.</p>
                <pre class="terminal">${esc(p.service_unit)}</pre>
                <p class="hint">Install it:</p>
                <pre class="terminal">sudo tee /etc/systemd/system/openforge.service &lt;&lt;'EOF'\n…paste the unit…\nEOF\nsudo systemctl daemon-reload\nsudo systemctl enable --now openforge\njournalctl -u openforge -f   # logs, including the setup token on first run</pre>
                <p class="hint">Or use <span class="mono">deploy/install.sh</span> from a release, which creates an <span class="mono">openforge</span> user, copies the server to <span class="mono">/opt/openforge</span> and installs this unit.</p>
            </section>`;
    };

    const paint = () => {
        history.replaceState(null, '', '/admin/settings?tab=' + encodeURIComponent(active));
        const pending = data.pending;
        const inGroup = data.settings.filter(s => s.group === active);

        app.innerHTML = `${head('Settings', `Saved to <span class="mono">${esc(data.platform.settings_file)}</span>. Most changes apply after a restart.`)}
            ${pending.length ? `<div class="notice row"><span>${pending.length} saved change${pending.length === 1 ? '' : 's'} waiting for a restart: <span class="mono">${pending.map(esc).join(', ')}</span></span><span class="spacer"></span><button class="button small" id="restart-now">${icon('refresh')}Restart now</button></div>` : ''}
            <div class="tabs" role="tablist">${tabs.map(t => `<button role="tab" data-tab="${esc(t)}" aria-selected="${t === active}">${esc(t)}${data.settings.some(s => s.group === t && s.pending) ? '<span class="count">•</span>' : ''}</button>`).join('')}</div>
            ${active === 'Server' ? server() : `<form class="surface panel settings-form" id="settings-form">
                ${derived(active)}
                ${fields(data.settings, inGroup.map(s => s.key))}
                ${extras[active]?.() || ''}
                <div class="row sticky-save"><span class="hint" id="save-state"></span><span class="spacer"></span><button type="button" class="button ghost" id="reset">Undo changes</button><button class="button" id="save">${icon('check')}Save</button></div>
            </form>`}`;

        app.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => { active = b.dataset.tab; paint(); });
        app.querySelectorAll('[data-copy]').forEach(b => b.onclick = event => { event.preventDefault(); copy(b.dataset.copy, 'Copied.'); });
        app.querySelector('[data-copy-unit]')?.addEventListener('click', () => copy(data.platform.service_unit, 'Unit copied.'));

        const restart = async () => {
            if (!await confirmDialog('Restart the server?', 'The site is unavailable for a few seconds while it restarts with the saved settings.', 'Restart'))
                return;

            try {
                await api('/api/v1/admin/server/restart', { method: 'POST' });
                waitForRestart('Applying the saved settings.', () => location.reload());
            } catch (error) {
                toast(error.message, 'error');
            }
        };

        app.querySelector('#restart')?.addEventListener('click', restart);
        app.querySelector('#restart-now')?.addEventListener('click', restart);

        const form = app.querySelector('#settings-form');

        if (!form)
            return;

        bindTests(form, data.settings, '/api/v1/admin/settings');
        form.querySelector('#reset').onclick = paint;
        form.addEventListener('input', () => form.querySelector('#save-state').textContent = Object.keys(collect(form, data.settings)).length ? 'Unsaved changes' : '');
        form.onsubmit = async event => {
            event.preventDefault();
            const values = collect(form, data.settings);

            if (!Object.keys(values).length) {
                toast('Nothing to save.');
                return;
            }

            const risky = Object.keys(values).filter(key => data.settings.find(s => s.key === key)?.danger);

            if (risky.length && !await confirmDialog('Change these settings?', `${risky.join(', ')} can lock you out or move data if they’re wrong. Check them before restarting.`, 'Save anyway', true))
                return;

            try {
                await api('/api/v1/admin/settings', { method: 'PUT', json: { values } });
                toast('Saved. Restart to apply.');
                Object.assign(data, await api('/api/v1/admin/settings'));
                paint();
            } catch (error) {
                toast(error.message, 'error');
            }
        };
    };

    paint();
}
