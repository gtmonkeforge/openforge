import { api, applyAccent, auth, copy, esc, icon, readAccent, toast } from './core.js';
import { bindTests, collect, fields, imapTest, smtpTest, waitForRestart } from './admin/settings-form.js';

const app = document.querySelector('#app');
const tokenKey = 'monkeforge.setup-token';

// Every step after the account is a page of settings, saved through the same validation as the admin panel.
const steps = [
    { id: 'token', title: 'Claim this server' },
    { id: 'account', title: 'Root admin account' },
    {
        id: 'site', title: 'Your site', intro: 'The name and address people will see. The public URL matters most: sign-in, passkeys, links and emails all use it.',
        keys: ['PUBLIC_URL', 'BRAND_NAME', 'BRAND_DESCRIPTION', 'BRAND_GAME', 'COOKIE_SECURE'],
    },
    {
        id: 'mail', title: 'Email', optional: true, intro: 'Outgoing mail sends sign-up confirmations, password resets, receipts and announcements. With Purelymail, use smtp.purelymail.com on port 465 (SSL) and the mailbox’s address and password.',
        keys: ['SMTP_HOST', 'SMTP_PORT', 'SMTP_SECURITY', 'SMTP_USERNAME', 'SMTP_PASSWORD', 'MAIL_FROM', 'MAIL_FROM_NAME'],
        extra: () => smtpTest('smtp'),
        more: {
            title: 'Support desk mailbox (optional)', intro: 'Email to this address becomes tickets in the admin panel. With Purelymail: imap.purelymail.com, port 993.',
            keys: ['SUPPORT_EMAIL', 'SUPPORT_IMAP_HOST', 'SUPPORT_IMAP_PORT', 'SUPPORT_MAIL_USERNAME', 'SUPPORT_MAIL_PASSWORD', 'SUPPORT_SMTP_HOST', 'SUPPORT_SMTP_PORT'],
            extra: () => imapTest() + smtpTest('support-smtp', 'Send a test reply'),
        },
    },
    {
        id: 'signin', title: 'Sign-in', optional: true, intro: 'Email accounts and passkeys work out of the box. Discord sign-in needs an application from the Discord Developer Portal.',
        keys: ['EMAIL_SIGNUP', 'DISCORD_CLIENT_ID', 'DISCORD_CLIENT_SECRET'],
        notice: d => `<div class="notice"><strong>Discord:</strong> create an application, then under OAuth2 add the redirect <button type="button" class="pill mono" data-copy="${esc(d.discord_redirect)}">${esc(d.discord_redirect)}</button> and copy the client ID and secret here.<br>
            <strong>Passkeys</strong> are tied to <span class="mono">${esc(d.passkey_rp_id)}</span> and work on <span class="mono">${esc(d.passkey_origin)}</span>. Browsers require HTTPS except on localhost, so set the public URL before anyone adds one.</div>`,
    },
    {
        id: 'admin', title: 'Admin panel & server', optional: true, intro: 'The admin panel runs on its own port so you can keep it off the internet. Reach it over an SSH tunnel, a VPN, or a reverse proxy with its own address.',
        keys: ['ADMIN_PORT', 'ADMIN_BIND', 'ADMIN_URL', 'ASPNETCORE_URLS', 'DATA_DIR'],
    },
    { id: 'finish', title: 'Finish' },
];

let status;
let token = '';

try {
    token = sessionStorage.getItem(tokenKey) || '';
} catch {
}

async function refresh() {
    Object.assign(auth, await api('/api/v1/session'));
    status = await api('/api/v1/setup');
}

function stepper(index) {
    return `<ol class="setup-steps">${steps.map((s, i) => `<li class="${i < index ? 'done' : i === index ? 'current' : ''}">${i < index ? icon('check') : `<span>${i + 1}</span>`}${esc(s.title)}</li>`).join('')}</ol>`;
}

function frame(index, content) {
    app.innerHTML = `<div class="setup">
        <aside class="setup-side"><div class="admin-brand"><span class="brand-mark" aria-hidden="true"></span>${esc(status.brand)}<small>Setup</small></div>${stepper(index)}</aside>
        <section class="setup-card surface">${content}</section>
    </div>`;
    app.querySelectorAll('[data-copy]').forEach(b => b.onclick = event => { event.preventDefault(); copy(b.dataset.copy, 'Copied.'); });
}

function go(index) {
    const step = steps[index];

    if (step.id === 'token')
        return tokenStep(index);

    if (step.id === 'account')
        return accountStep(index);

    if (step.id === 'finish')
        return finishStep(index);

    return settingsStep(index);
}

function tokenStep(index) {
    frame(index, `<h1>Welcome to OpenForge</h1>
        <p>This server hasn’t been set up. To prove it’s yours, enter the setup token from the server’s log.</p>
        <pre class="terminal">journalctl -u openforge | grep -A1 "setup token"\n# or\ncat ${esc(status.token_path || 'data/setup-token')}</pre>
        <form class="stack" id="token-form">
            <div class="field"><label for="token">Setup token</label><input id="token" class="mono" required autocomplete="off" value="${esc(token)}"></div>
            <div class="row"><span class="spacer"></span><button class="button">${icon('next')}Continue</button></div>
        </form>`);

    app.querySelector('#token-form').onsubmit = async event => {
        event.preventDefault();
        token = app.querySelector('#token').value.trim();

        try {
            await api('/api/v1/setup/claim', { method: 'POST', json: { token, verify_only: true } });

            try {
                sessionStorage.setItem(tokenKey, token);
            } catch {
            }

            go(index + 1);
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

function accountStep(index) {
    frame(index, `<h1>Create the root admin</h1>
        <p>This email account owns the server: it’s the only one that can change settings, edit the database and add other admins. You can sign in with Discord later from other accounts, but the root account always has a password.</p>
        <form class="stack" id="account-form">
            <div class="field"><label for="a-email">Email</label><input id="a-email" type="email" required maxlength="254" autocomplete="email"></div>
            <div class="field"><label for="a-username">Username<span>3–32 letters, digits, dots, dashes or underscores</span></label><input id="a-username" required maxlength="32" autocomplete="username"></div>
            <div class="field"><label for="a-password">Password</label><input id="a-password" type="password" required autocomplete="new-password" minlength="8"></div>
            <div class="field"><label for="a-confirm">Confirm password</label><input id="a-confirm" type="password" required autocomplete="new-password"></div>
            <div class="row"><button type="button" class="button ghost" id="back">${icon('back')}Back</button><span class="spacer"></span><button class="button" id="create">${icon('user')}Create account</button></div>
        </form>`);

    app.querySelector('#back').onclick = () => go(index - 1);
    app.querySelector('#account-form').onsubmit = async event => {
        event.preventDefault();
        const v = id => app.querySelector(id).value;

        if (v('#a-password') !== v('#a-confirm')) {
            toast('The passwords don’t match.', 'error');
            return;
        }

        const button = app.querySelector('#create');
        button.disabled = true;

        try {
            const result = await api('/api/v1/setup/claim', { method: 'POST', json: { token, email: v('#a-email'), username: v('#a-username'), password: v('#a-password') } });
            auth.csrf_token = result.csrf_token;

            try {
                sessionStorage.removeItem(tokenKey);
            } catch {
            }

            await refresh();
            toast('Root account created. You’re signed in.');
            go(index + 1);
        } catch (error) {
            toast(error.message, 'error');
            button.disabled = false;
        }
    };
}

function settingsStep(index) {
    const step = steps[index];
    const s = status.settings;

    frame(index, `<h1>${esc(step.title)}</h1>${step.intro ? `<p>${esc(step.intro)}</p>` : ''}
        ${step.notice ? step.notice(status.derived) : ''}
        <form class="stack settings-form" id="step-form">
            ${fields(s, step.keys)}${step.extra?.() || ''}
            ${step.more ? `<details class="setup-more"><summary><strong>${esc(step.more.title)}</strong></summary><p class="hint">${esc(step.more.intro)}</p>${fields(s, step.more.keys)}${step.more.extra?.() || ''}</details>` : ''}
            <div class="row"><button type="button" class="button ghost" id="back">${icon('back')}Back</button><span class="spacer"></span>
                ${step.optional ? '<button type="button" class="button ghost" id="skip">Skip for now</button>' : ''}
                <button class="button">${icon('next')}Save and continue</button></div>
        </form>`);

    const form = app.querySelector('#step-form');
    bindTests(form, s, '/api/v1/setup');
    // There's no going back to the token or account steps once the account exists.
    app.querySelector('#back').onclick = () => go(Math.max(2, index - 1));
    app.querySelector('#back').hidden = index <= 2;
    app.querySelector('#skip')?.addEventListener('click', () => go(index + 1));

    form.onsubmit = async event => {
        event.preventDefault();
        const values = collect(form, s);

        try {
            if (Object.keys(values).length) {
                await api('/api/v1/setup/settings', { method: 'PUT', json: { values } });
                await refresh();
            }

            go(index + 1);
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

function finishStep(index) {
    const p = status.platform;
    const changed = status.settings.filter(s => s.source === 'file' && s.key !== 'ROOT_ADMIN_ID');

    frame(index, `<h1>Ready to go</h1>
        <p>Your settings are saved in <span class="mono">${esc(p.settings_file)}</span>. Finishing restarts the server so they take effect. Everything here can be changed later in the admin panel under <strong>Settings</strong>.</p>
        <dl class="details-list">
            <div><dt>Root admin</dt><dd>@${esc(status.root.username)} <span class="mono hint">${esc(status.root.id)}</span></dd></div>
            <div><dt>Saved settings</dt><dd>${changed.length ? changed.map(s => `<span class="pill mono">${esc(s.key)}</span>`).join(' ') : 'Defaults only'}</dd></div>
        </dl>
        <h2>${icon('terminal')} Run OpenForge as a service</h2>
        <p class="hint">${p.linux ? 'On Linux, install this systemd unit so the server starts on boot and restarts if it stops. It’s generated for this folder, user and executable.' : 'systemd units are for Linux servers. On this system, use your platform’s service manager; the unit below shows what it needs to run.'}</p>
        <div class="row"><span class="spacer"></span><button type="button" class="button subtle small" id="copy-unit">${icon('copy')}Copy unit</button></div>
        <pre class="terminal">${esc(p.service_unit)}</pre>
        <pre class="terminal">sudo nano /etc/systemd/system/openforge.service   # paste the unit\nsudo systemctl daemon-reload\nsudo systemctl enable --now openforge</pre>
        <div class="row"><button type="button" class="button ghost" id="back">${icon('back')}Back</button><span class="spacer"></span><button class="button large" id="finish">${icon('rocket')}Finish and restart</button></div>`);

    app.querySelector('#copy-unit').onclick = () => copy(p.service_unit, 'Unit copied.');
    app.querySelector('#back').onclick = () => go(index - 1);
    app.querySelector('#finish').onclick = async () => {
        try {
            await api('/api/v1/setup/finish', { method: 'POST' });
            waitForRestart('Starting with your new settings.', () => location.assign('/admin'));
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

async function start() {
    applyAccent(readAccent());

    try {
        await refresh();
    } catch (error) {
        app.innerHTML = `<div class="error">${esc(error.message)}</div>`;
        return;
    }

    if (status.completed) {
        location.replace('/admin');
        return;
    }

    if (status.required) {
        go(0);
        return;
    }

    // Claimed but not finished: only the root admin can carry on.
    if (!status.root) {
        app.innerHTML = `<section class="signin-card surface"><span class="brand-mark" aria-hidden="true"></span><h1>Setup in progress</h1><p>This server has a root administrator but setup isn’t finished. Sign in as the root admin to continue.</p>
            <div class="signin-actions"><a class="button large" href="/login?next=/setup">${icon('user')}Sign in</a></div></section>`;
        return;
    }

    go(2);
}

start();
