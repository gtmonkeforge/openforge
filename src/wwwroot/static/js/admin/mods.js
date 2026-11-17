import { api, categories, esc, icon, modIcon, pagination, toast, userBadges } from '../core.js';
import { navigate } from './main.js';
import { confirmDialog, counters, head, menu, site } from './ui.js';

export async function render({ app, params, alive }) {
    app.innerHTML = `${head('All mods', 'Every listing on the site, whatever its status.')}
        <div class="toolbar"><div class="toolbar-search">${icon('search')}<input id="mod-search" type="search" placeholder="Search name or GUID" aria-label="Search mods" value="${esc(params.get('q') || '')}"></div>
            <select id="mod-status" aria-label="Status"><option value="all">All statuses</option><option value="pending">Pending review</option><option value="approved">Approved</option><option value="unapproved">Denied</option><option value="deleted">Deleted</option></select>
            <span class="count" id="mod-counts"></span></div>
        <div class="table-wrap surface"><table class="data users"><thead><tr><th>Mod</th><th>Author</th><th>Status</th><th>Version</th><th></th></tr></thead><tbody id="mod-rows"></tbody></table></div>
        <div id="mod-pages"></div>`;

    let page = 1;
    let timer;
    const search = app.querySelector('#mod-search');
    const status = app.querySelector('#mod-status');
    status.value = params.get('status') || 'all';

    const act = async (m, next) => {
        if (next === 'deleted' && !await confirmDialog('Remove this mod?', 'It disappears from listings and downloads but stays in the deleted list. Its monthly tiers end now and subscribers stop being billed.', 'Remove', true))
            return;

        try {
            const result = await api('/api/v1/admin/mods/' + encodeURIComponent(m.guid), { method: 'PATCH', json: { status: next } });
            toast(result.subscriptions_failed ? `Updated, but ${result.subscriptions_failed} subscription(s) couldn’t be cancelled. Cancel them in Stripe.` : 'Mod updated.', result.subscriptions_failed ? 'error' : '');
            counters.refresh();
            await load();
        } catch (error) {
            toast(error.message, 'error');
        }
    };

    const load = async () => {
        const data = await api('/api/v1/admin/mods?' + new URLSearchParams({ q: search.value, status: status.value, page }));

        if (!alive())
            return;

        const c = data.counts;
        app.querySelector('#mod-counts').textContent = `${c.pending || 0} pending · ${c.approved || 0} approved · ${c.unapproved || 0} denied · ${c.deleted || 0} deleted`;
        app.querySelector('#mod-rows').innerHTML = data.items.map((m, i) => `<tr data-row="${i}">
            <td><div class="user-cell">${modIcon(m)}<div><strong>${esc(m.name)}</strong><small class="mono">${esc(m.guid)}</small></div></div></td>
            <td>${esc(m.author.name)} ${userBadges(m.author)}</td>
            <td><span class="pill ${m.status === 'approved' ? 'good' : m.status === 'pending' ? 'warn' : 'bad'}">${esc(m.status === 'unapproved' ? 'denied' : m.status)}</span>${m.choice_category ? ` <span class="pill accent">Choice · ${esc(m.choice_category)}</span>` : ''}</td>
            <td class="mono">v${esc(m.current_version)}</td>
            <td><div class="kebab"><button class="icon-button" data-more="${i}" aria-label="Actions">${icon('more')}</button></div></td>
        </tr>`).join('') || '<tr><td colspan="5" class="hint">No mods match.</td></tr>';

        app.querySelectorAll('[data-row]').forEach(row => row.onclick = event => {
            if (event.target.closest('.kebab'))
                return;

            const m = data.items[Number(row.dataset.row)];

            if (m.status !== 'deleted')
                navigate('/admin/reviews/' + encodeURIComponent(m.guid));
        });

        app.querySelectorAll('[data-more]').forEach(button => button.onclick = event => {
            event.stopPropagation();
            const m = data.items[Number(button.dataset.more)];
            const choice = cat => async () => {
                try {
                    await api('/api/v1/admin/mods/' + encodeURIComponent(m.guid) + '/choice', { method: 'PUT', json: { category: cat } });
                    toast(cat ? `Modder’s Choice for ${cat}.` : 'Badge removed.');
                    await load();
                } catch (error) {
                    toast(error.message, 'error');
                }
            };

            menu(button, [
                ...(m.status !== 'deleted' ? [{ label: 'Open review', icon: 'eye', action: () => navigate('/admin/reviews/' + encodeURIComponent(m.guid)) }, { label: 'View on site', icon: 'external', href: site('/view/' + encodeURIComponent(m.guid)) }] : []),
                'sep',
                ...(m.status !== 'approved' ? [{ label: m.status === 'deleted' ? 'Restore and approve' : 'Approve', icon: 'check', action: () => act(m, 'approved') }] : []),
                ...(m.status === 'approved' || m.status === 'pending' ? [{ label: 'Unapprove', icon: 'x', action: () => act(m, 'unapproved') }] : []),
                ...(m.status !== 'deleted' ? [{ label: 'Remove from site', icon: 'trash', danger: true, action: () => act(m, 'deleted') }] : []),
                'sep',
                ...categories.map(cat => ({ label: `Modder’s Choice · ${cat}`, icon: 'star', action: choice(cat) })),
                ...(m.choice_category ? [{ label: 'Remove Modder’s Choice', icon: 'x', action: choice(null) }] : []),
                'sep',
                {
                    label: 'Purge permanently', icon: 'trash', danger: true, action: async () => {
                        if (!await confirmDialog('Purge permanently?', 'Deletes the mod and all of its releases, files and history. This can’t be undone.', 'Purge', true))
                            return;

                        try {
                            await api('/api/v1/admin/mods/' + encodeURIComponent(m.guid), { method: 'DELETE' });
                            toast('Mod purged.');
                            await load();
                        } catch (error) {
                            toast(error.message, 'error');
                        }
                    },
                },
            ]);
        });

        pagination(app.querySelector('#mod-pages'), data.total, data.limit, page, p => { page = p; load(); });
    };

    status.onchange = () => { page = 1; load(); };
    search.oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { page = 1; load(); }, 250);
    };

    await load();
}
