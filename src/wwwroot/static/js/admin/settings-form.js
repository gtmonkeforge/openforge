import { api, esc, icon, toast } from '../core.js';

const sources = {
    environment: ['bad', 'Set by environment'],
    file: ['accent', '.env'],
    appsettings: ['', 'appsettings.json'],
    default: ['', 'Default'],
};

// One form field for a setting from the server's registry.
export function field(s) {
    const id = 'set-' + s.key;
    const [tone, label] = sources[s.source] || sources.default;
    const locked = s.locked ? 'disabled' : '';
    const value = s.value ?? '';
    let input;

    if (s.type === 'bool') {
        const current = value === '' ? '' : String(value).toLowerCase() === 'true' ? 'true' : 'false';
        input = `<select id="${id}" data-key="${s.key}" ${locked}><option value="">Default${s.default ? ` (${s.default === 'true' ? 'on' : 'off'})` : ''}</option><option value="true" ${current === 'true' ? 'selected' : ''}>On</option><option value="false" ${current === 'false' ? 'selected' : ''}>Off</option></select>`;
    } else if (s.type === 'select') {
        input = `<select id="${id}" data-key="${s.key}" ${locked}>${(s.choices || []).map(c => `<option value="${esc(c)}" ${c === value || (c === '' && !value) ? 'selected' : ''}>${c === '' ? 'Default' : esc(c)}</option>`).join('')}${s.choices?.includes('') ? '' : `<option value="" ${value ? '' : 'selected'}>Default${s.default ? ` (${esc(s.default)})` : ''}</option>`}</select>`;
    } else if (s.type === 'textarea') {
        input = `<textarea id="${id}" data-key="${s.key}" rows="3" ${locked} placeholder="${esc(s.default || '')}">${esc(value)}</textarea>`;
    } else if (s.type === 'password') {
        input = `<div class="row"><input id="${id}" class="grow" type="password" autocomplete="new-password" data-key="${s.key}" data-secret ${locked} placeholder="${s.is_set ? 'Saved. Type to replace it.' : 'Not set'}">
            ${s.is_set && !s.locked ? `<label class="check"><input type="checkbox" data-clear="${s.key}">Clear</label>` : ''}</div>`;
    } else {
        const type = s.type === 'number' ? 'number' : s.type === 'email' ? 'email' : s.type === 'url' ? 'url' : 'text';
        input = `<input id="${id}" type="${type}" data-key="${s.key}" value="${esc(value)}" ${locked} placeholder="${esc(s.default || '')}" ${type === 'number' ? 'min="0"' : ''}>`;
    }

    return `<div class="field setting ${s.danger ? 'danger' : ''}" data-setting="${s.key}">
        <label for="${id}">${esc(s.label)} <span class="mono">${esc(s.key)}</span></label>
        ${input}
        <div class="setting-meta"><span class="pill ${tone}">${esc(label)}</span>${s.pending ? '<span class="pill warn">Restart to apply</span>' : ''}${s.danger ? '<span class="pill bad">Careful</span>' : ''}
            ${s.help ? `<span class="hint">${esc(s.help)}</span>` : ''}${s.locked ? '<span class="hint">Change it where the server is started (systemd unit, Docker or shell): that overrides the settings file.</span>' : ''}</div>
    </div>`;
}

export function fields(settings, keys) {
    return keys.map(key => settings.find(s => s.key === key)).filter(Boolean).map(field).join('');
}

// Only what the admin changed, so untouched settings keep their source (default, appsettings.json, …).
export function collect(root, settings) {
    const values = {};

    root.querySelectorAll('[data-key]').forEach(el => {
        const s = settings.find(x => x.key === el.dataset.key);

        if (!s || s.locked)
            return;

        if (el.dataset.secret !== undefined) {
            if (el.value !== '')
                values[s.key] = el.value;
            else if (root.querySelector(`[data-clear="${s.key}"]`)?.checked)
                values[s.key] = null;

            return;
        }

        const before = s.type === 'bool' ? (s.value == null || s.value === '' ? '' : String(s.value).toLowerCase() === 'true' ? 'true' : 'false') : String(s.value ?? '');
        const after = el.value.trim();

        if (after !== before)
            values[s.key] = after === '' ? null : after;
    });

    return values;
}

// A form-local value, falling back to what's saved, for the test buttons.
export function current(root, settings, key) {
    const el = root.querySelector(`[data-key="${key}"]`);
    return el && el.value.trim() !== '' ? el.value.trim() : el?.dataset.secret !== undefined ? '' : settings.find(s => s.key === key)?.value || '';
}

export function bindTests(root, settings, base) {
    root.querySelectorAll('[data-test]').forEach(button => button.onclick = async () => {
        const kind = button.dataset.test;
        const out = root.querySelector(`[data-test-result="${kind}"]`);
        const v = key => current(root, settings, key);
        let json;

        if (kind === 'imap') {
            json = { host: v('SUPPORT_IMAP_HOST'), port: v('SUPPORT_IMAP_PORT'), username: v('SUPPORT_MAIL_USERNAME'), email: v('SUPPORT_EMAIL'), password: v('SUPPORT_MAIL_PASSWORD') };
        } else {
            const to = root.querySelector(`[data-test-to="${kind}"]`)?.value.trim();

            if (!to) {
                toast('Enter an address to send the test to.', 'error');
                return;
            }

            json = kind === 'support-smtp'
                ? { account: 'support', host: v('SUPPORT_SMTP_HOST'), port: v('SUPPORT_SMTP_PORT'), security: v('SUPPORT_SMTP_SECURITY'), username: v('SUPPORT_MAIL_USERNAME'), from: v('SUPPORT_EMAIL'), password: v('SUPPORT_MAIL_PASSWORD'), to }
                : { host: v('SMTP_HOST'), port: v('SMTP_PORT'), security: v('SMTP_SECURITY'), username: v('SMTP_USERNAME'), from: v('MAIL_FROM'), password: v('SMTP_PASSWORD'), to };
        }

        button.disabled = true;
        out.innerHTML = '<span class="spinner"></span>';

        try {
            const result = await api(base + '/test/' + (kind === 'imap' ? 'imap' : 'smtp'), { method: 'POST', json });
            out.innerHTML = result.ok ? `<span class="pill good">${icon('check')}${esc(result.message)}</span>` : `<span class="error-text">${esc(result.error)}</span>`;
        } catch (error) {
            out.innerHTML = `<span class="error-text">${esc(error.message)}</span>`;
        }

        button.disabled = false;
    });
}

export function smtpTest(kind, label = 'Send a test email') {
    return `<div class="test-row"><input type="email" data-test-to="${kind}" placeholder="Send the test to…" aria-label="Test recipient"><button type="button" class="button subtle small" data-test="${kind}">${icon('send')}${esc(label)}</button><span data-test-result="${kind}"></span></div>`;
}

export function imapTest() {
    return `<div class="test-row"><button type="button" class="button subtle small" data-test="imap">${icon('mail')}Test mailbox sign-in</button><span data-test-result="imap"></span></div>`;
}

// Waits for the server to come back after a restart, then calls done (or reports where to look).
export function waitForRestart(message, done) {
    const overlay = document.createElement('div');
    overlay.className = 'modal';
    overlay.innerHTML = `<div class="modal-card"><h2>Restarting…</h2><p>${esc(message)}</p><div class="page-loading"><span class="spinner"></span><span id="restart-state">Waiting for the server</span></div></div>`;
    document.body.append(overlay);
    const started = Date.now();

    const poll = async () => {
        await new Promise(r => setTimeout(r, 1500));

        try {
            const response = await fetch('/api/v1/health', { cache: 'no-store' });

            if (response.ok && Date.now() - started > 2500) {
                done();
                return;
            }
        } catch {
        }

        if (Date.now() - started > 90000) {
            overlay.querySelector('.page-loading').innerHTML = '<p class="error-text">The server hasn’t come back here. If you changed the listen URL, admin port or public URL, open the new address. Otherwise check the server log: a setting may have stopped it from starting.</p>';
            return;
        }

        poll();
    };

    poll();
}
