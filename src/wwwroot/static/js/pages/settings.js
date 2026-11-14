import { DEFAULT_ACCENT, api, app, applyAccent, auth, avatar, date, esc, icon, readAccent, requireSignIn, setTitle, signInUrl, storeAccent, toast } from '../core.js';

const sections = [
    ['appearance', 'Appearance', 'palette'],
    ['keys', 'Access keys', 'key'],
    ['channels', 'Release channels', 'lock'],
    ['account', 'Account', 'user'],
];

const swatches = ['#dc5c3b', '#f59e0b', '#22c55e', '#14b8a6', '#60cdff', '#6366f1', '#a855f7', '#ec4899', '#e5e7eb'];

export async function render(section = 'appearance') {
    if (!sections.some(([key]) => key === section))
        section = 'appearance';

    setTitle('Settings');
    app.innerHTML = `
        <div class="page-head"><div><h1>${esc(sections.find(([key]) => key === section)[1])}</h1></div></div>
        <div class="settings-layout">
            <nav class="side-nav surface" aria-label="Settings">${sections.map(([key, label, glyph]) => `<a href="/settings${key === 'appearance' ? '' : '/' + key}" ${key === section ? 'aria-current="page"' : ''}>${icon(glyph)}${label}</a>`).join('')}</nav>
            <div id="settings-body"></div>
        </div>`;

    const body = app.querySelector('#settings-body');

    if (section === 'appearance')
        appearance(body);
    else if (section === 'keys')
        await keys(body);
    else if (section === 'channels')
        channels(body);
    else
        account(body);
}

function appearance(body) {
    const saved = auth.user?.accent_color || readAccent();

    body.innerHTML = `<section class="surface panel">
        <h2>Accent color</h2>
        <p class="hint">Used for buttons, links, highlights and the background glow.</p>
        <div class="swatches" role="group" aria-label="Preset colors">${swatches.map(c => `<button type="button" class="swatch" data-swatch="${c}" aria-label="${c}" aria-pressed="${c === saved}"></button>`).join('')}</div>
        <div class="gap"></div>
        <div class="color-input"><input type="color" id="accent-picker" value="${esc(saved)}" aria-label="Custom color"><input type="text" id="accent-hex" class="mono-input" value="${esc(saved)}" maxlength="7" aria-label="Hex code"><button class="button ghost small" id="accent-reset">Reset</button></div>
        <div class="gap"></div>
        <div class="row"><button class="button">Primary</button><span class="pill accent">Selected</span><a class="hint" href="#" id="sample-link">A link</a></div>
        <div class="row sticky-actions"><span class="hint">${auth.user ? 'Saved to your account and used on every device.' : 'Saved in this browser. Sign in to sync it.'}</span><span class="spacer"></span><button class="button ghost" id="accent-cancel">Cancel</button><button class="button" id="accent-save">Save</button></div>
    </section>`;

    body.querySelectorAll('[data-swatch]').forEach(button => button.style.background = button.dataset.swatch);

    const picker = body.querySelector('#accent-picker');
    const hex = body.querySelector('#accent-hex');
    const set = color => {
        picker.value = hex.value = color;
        applyAccent(color);
        body.querySelectorAll('[data-swatch]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.swatch === color)));
    };

    body.querySelectorAll('[data-swatch]').forEach(b => b.onclick = () => set(b.dataset.swatch));
    picker.oninput = () => set(picker.value);
    hex.oninput = () => {
        if (/^#[\da-f]{6}$/i.test(hex.value))
            set(hex.value.toLowerCase());
    };
    body.querySelector('#accent-reset').onclick = () => set(DEFAULT_ACCENT);
    body.querySelector('#accent-cancel').onclick = () => set(auth.user?.accent_color || readAccent());
    body.querySelector('#sample-link').onclick = event => event.preventDefault();
    body.querySelector('#accent-save').onclick = async () => {
        const color = hex.value.toLowerCase();

        try {
            if (auth.user) {
                await api('/api/v1/settings', { method: 'PUT', json: { accent_color: color } });
                auth.user.accent_color = color;
            }

            if (!storeAccent(color) && !auth.user)
                throw new Error('Browser storage is unavailable. Sign in to save settings to your account.');

            toast('Accent saved.');
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

async function keys(body) {
    if (!auth.user) {
        body.innerHTML = requireSignIn('Access keys', 'Sign in to create keys for publishing from scripts and GitHub Actions.');
        return;
    }

    body.innerHTML = `<section class="surface panel">
            <h2>Create an access key</h2>
            <p class="hint">Keys can publish releases to your mods from scripts or CI. A key is shown once and can’t be retrieved later.</p>
            <form class="row" id="key-form"><label class="sr-only" for="key-name">Key name</label><input id="key-name" required maxlength="80" placeholder="GitHub Actions"><button class="button" type="submit">${icon('plus')}Create key</button></form>
            <div class="new-key" id="new-key" hidden><p><strong>Copy this key now.</strong> Store it in your repository secrets; it won’t be shown again.</p><div class="copy-field"><input id="key-token" readonly aria-label="New key" spellcheck="false"><button class="button subtle" id="key-copy">${icon('copy')}Copy</button></div><p><button class="button ghost small" id="key-done">I saved it</button></p></div>
        </section>
        <section class="surface panel"><h2>Your keys</h2><div id="key-list"><p class="hint">Loading…</p></div></section>
        <section class="surface panel"><h2>Using a key</h2><div class="terminal"><header><i></i><i></i><i></i><span>publish.sh</span></header><pre><span class="prompt">$</span> curl -X POST "${esc(location.origin)}/api/v1/mods/com.you.mod/releases" \\
    -H "Authorization: Bearer $MONKEFORGE_KEY" \\
    -F version=1.2.0 -F channel=stable \\
    -F downloads_bepinex5=@bin/YourMod.dll</pre></div></section>`;

    const list = body.querySelector('#key-list');

    const load = async () => {
        const result = await api('/api/v1/api-keys');
        list.innerHTML = result.items.length ? result.items.map(k => `<div class="key-item">${icon('key')}<div class="grow"><strong>${esc(k.name)}</strong> <code class="faint">${esc(k.prefix)}…</code><div class="hint">Created ${esc(date(k.created_at))} · ${k.last_used_at ? 'last used ' + esc(date(k.last_used_at)) : 'never used'}</div></div>${k.revoked_at ? '<span class="pill bad">Revoked</span>' : `<button class="button danger small" data-revoke="${esc(k.id)}">Revoke</button>`}</div>`).join('') : '<p class="hint">No keys yet.</p>';
        list.querySelectorAll('[data-revoke]').forEach(b => b.onclick = async () => {
            if (!confirm('Revoke this key? Anything using it will stop working.'))
                return;

            try {
                await api('/api/v1/api-keys/' + encodeURIComponent(b.dataset.revoke), { method: 'DELETE' });
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
    };

    body.querySelector('#key-form').onsubmit = async event => {
        event.preventDefault();

        try {
            const result = await api('/api/v1/api-keys', { method: 'POST', json: { name: body.querySelector('#key-name').value } });
            body.querySelector('#key-token').value = result.token;
            body.querySelector('#new-key').hidden = false;
            event.target.reset();
            await load();
        } catch (error) {
            toast(error.message, 'error');
        }
    };
    body.querySelector('#key-copy').onclick = () => navigator.clipboard.writeText(body.querySelector('#key-token').value).then(() => toast('Key copied.'));
    body.querySelector('#key-done').onclick = () => {
        body.querySelector('#key-token').value = '';
        body.querySelector('#new-key').hidden = true;
    };

    await load().catch(error => list.innerHTML = `<p class="error">${esc(error.message)}</p>`);
}

function channels(body) {
    if (!auth.user) {
        body.innerHTML = requireSignIn('Unlock a release channel', 'Sign in to redeem a channel code shared by a mod author.');
        return;
    }

    body.innerHTML = `<section class="surface panel">
        <h2>Unlock a release channel</h2>
        <p class="hint">Mod authors can share 25-character codes for private betas. Paste the whole code into the first box.</p>
        <form id="unlock-form">
            <div class="code-boxes">${Array.from({ length: 5 }, (_, i) => `${i ? '<span aria-hidden="true">–</span>' : ''}<input aria-label="Code group ${i + 1} of 5" maxlength="5" autocomplete="off" autocapitalize="characters" spellcheck="false" required>`).join('')}</div>
            <div class="gap"></div>
            <button class="button" type="submit">${icon('lock')}Unlock channel</button>
            <p class="hint" id="unlock-status" role="status"></p>
        </form>
    </section>`;

    const form = body.querySelector('#unlock-form');
    const inputs = [...form.querySelectorAll('input')];

    inputs.forEach((input, i) => {
        input.oninput = () => {
            input.value = input.value.replace(/[^a-z0-9]/gi, '').toUpperCase();

            if (input.value.length === 5)
                inputs[i + 1]?.focus();
        };
        input.onkeydown = event => {
            if (event.key === 'Backspace' && !input.value)
                inputs[i - 1]?.focus();
        };
        input.onpaste = event => {
            const value = event.clipboardData.getData('text').trim();

            if (/^(?:[a-z0-9]{25}|[a-z0-9]{5}(?:-[a-z0-9]{5}){4})$/i.test(value)) {
                event.preventDefault();
                const code = value.replace(/-/g, '').toUpperCase();
                inputs.forEach((box, j) => box.value = code.slice(j * 5, j * 5 + 5));
                inputs[4].focus();
            }
        };
    });

    form.onsubmit = async event => {
        event.preventDefault();
        const status = form.querySelector('#unlock-status');

        try {
            const result = await api('/api/v1/channels/unlock', { method: 'POST', json: { code: inputs.map(i => i.value).join('-') } });
            status.innerHTML = `Unlocked <strong>${esc(result.name)}</strong>. <a href="${esc(result.url)}">Open it →</a>`;
            form.reset();
        } catch (error) {
            status.textContent = error.message;
        }
    };
}

function account(body) {
    if (!auth.user) {
        body.innerHTML = `<section class="surface panel"><h2>Not signed in</h2><p class="hint">Sign in with Discord to publish mods, rate, comment and sync settings.</p><a class="button discord" href="${signInUrl()}">${icon('discord')}Sign in with Discord</a></section>`;
        return;
    }

    const user = auth.user;
    body.innerHTML = `<section class="surface panel">
            <div class="row">${avatar(user)}<div><strong>${esc(user.display_name)}</strong><div class="hint mono">@${esc(user.username)} · Discord @${esc(user.discord_username)}</div></div><span class="spacer"></span><a class="button subtle" href="${esc(user.profile_url)}">${icon('user')}View profile</a></div>
        </section>
        <section class="surface panel">
            <h2>Account ID</h2>
            <p class="hint">Share this with mod authors who want to add you to a private release channel.</p>
            <div class="copy-field"><input readonly value="${esc(user.id)}" aria-label="Account ID"><button class="button subtle" id="copy-id">${icon('copy')}Copy</button></div>
        </section>
        <section class="surface panel">
            <h2>Session</h2>
            <p class="hint">You stay signed in for 30 days on this browser. Signing out ends this session only.</p>
            <button class="button danger" id="sign-out-here">${icon('logout')}Sign out</button>
        </section>`;

    body.querySelector('#copy-id').onclick = () => navigator.clipboard.writeText(user.id).then(() => toast('Account ID copied.'));
    body.querySelector('#sign-out-here').onclick = async () => {
        await api('/api/v1/logout', { method: 'POST' });
        location.assign('/browse');
    };
}
