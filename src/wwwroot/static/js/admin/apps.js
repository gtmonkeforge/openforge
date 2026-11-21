import { api, appLogo, esc, icon, pagination, toast, verifiedMark } from '../core.js';
import { confirmDialog, fullWhen, head } from './ui.js';

const filters = [['unverified', 'Unverified'], ['verified', 'Verified'], ['all', 'All apps']];

// OAuth apps creators have registered. Verifying one puts a checkmark next to its name when people sign in to it,
// and lets it ask for the account scope.
export async function render({ app, params, alive }) {
    app.innerHTML = `${head('OAuth apps', 'Apps people sign in to with their account. Verify an app once you’ve checked who runs it: it gets a checkmark on the sign-in screen and can request full account access.')}
        <div class="toolbar">
            <div class="toolbar-search">${icon('search')}<input id="app-search" type="search" placeholder="Search app name, client ID, owner username or ID" aria-label="Search apps" value="${esc(params.get('q') || '')}"></div>
            <select id="app-filter" aria-label="Filter">${filters.map(([key, label]) => `<option value="${key}">${label}</option>`).join('')}</select>
            <span class="count" id="app-count"></span>
        </div>
        <div class="table-wrap surface"><table class="data oauth-apps"><thead><tr><th>App</th><th>Owner</th><th>Type</th><th>Users</th><th>Redirects</th><th>Status</th></tr></thead><tbody id="app-rows"></tbody></table></div>
        <div id="app-pages"></div>`;

    const search = app.querySelector('#app-search');
    const filter = app.querySelector('#app-filter');
    filter.value = filters.some(([key]) => key === params.get('filter')) ? params.get('filter') : 'unverified';
    let page = 1;
    let timer;

    const load = async () => {
        const data = await api('/api/v1/admin/oauth-apps?' + new URLSearchParams({ q: search.value.trim(), filter: filter.value, page }));

        if (!alive())
            return;

        app.querySelector('#app-count').textContent = `${data.total} ${data.total === 1 ? 'app' : 'apps'}`;
        app.querySelector('#app-rows').innerHTML = data.items.map((a, i) => `<tr>
            <td><div class="user-cell">${appLogo(a, 'sm')}<div><strong>${esc(a.name)}</strong>${a.verified ? verifiedMark(false) : ''}
                <small class="mono">${esc(a.id)}</small>
                ${a.homepage_url ? `<small><a href="${esc(a.homepage_url)}" target="_blank" rel="noopener noreferrer">${esc(new URL(a.homepage_url).host)}</a></small>` : ''}
                ${a.description ? `<small>${esc(a.description)}</small>` : ''}</div></div></td>
            <td><a href="/admin/users?q=${encodeURIComponent(a.owner.id)}">${esc(a.owner.name || a.owner.id)}</a></td>
            <td><span class="pill">${a.public_client ? 'Public' : 'Confidential'}</span></td>
            <td class="mono">${a.users}</td>
            <td class="app-redirects">${a.redirect_uris.length ? a.redirect_uris.map(uri => `<small class="mono">${esc(uri)}</small>`).join('') : '<span class="faint">none</span>'}</td>
            <td><div class="app-status">${a.verified ? `<span class="pill good" title="${esc(`Verified ${fullWhen(a.verified_at)}${a.verified_by ? ' by ' + a.verified_by : ''}`)}">Verified</span>` : '<span class="pill">Unverified</span>'}
                <button class="button small ${a.verified ? 'ghost' : ''}" data-toggle="${i}">${a.verified ? 'Unverify' : `${icon('check')}Verify`}</button></div></td>
        </tr>`).join('') || '<tr><td colspan="6" class="hint">No apps match.</td></tr>';

        app.querySelectorAll('[data-toggle]').forEach(button => button.onclick = async () => {
            const target = data.items[Number(button.dataset.toggle)];
            const verify = !target.verified;
            const ok = await confirmDialog(verify ? `Verify ${target.name}?` : `Remove verification from ${target.name}?`,
                verify ? 'People will see a checkmark next to its name when they sign in, and it can ask for full access to their accounts. Only verify apps whose owner you’ve checked.'
                    : 'The checkmark goes away, and any account-scope access it was given stops working until it’s verified again.',
                verify ? 'Verify' : 'Remove verification', !verify);

            if (!ok)
                return;

            try {
                await api(`/api/v1/admin/oauth-apps/${encodeURIComponent(target.id)}/verified`, { method: 'PUT', json: { verified: verify } });
                toast(verify ? `${target.name} is verified.` : `${target.name} is no longer verified.`);
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });

        pagination(app.querySelector('#app-pages'), data.total, data.limit, page, p => { page = p; load(); });
    };

    filter.onchange = () => { page = 1; load(); };
    search.oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { page = 1; load(); }, 250);
    };

    await load();
}
