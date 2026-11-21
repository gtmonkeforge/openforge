import { api, choiceBadge, esc, icon, modIcon, pagination, toast, userBadges } from '../core.js';
import { navigate } from './main.js';
import { confirmDialog, counters, dialog, head, menu, site } from './ui.js';

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
            <td><div class="user-cell">${modIcon(m)}<div><strong>${esc(m.name)}${choiceBadge(m)}</strong><small class="mono">${esc(m.guid)}</small></div></div></td>
            <td>${esc(m.author.name)} ${userBadges(m.author)}</td>
            <td><span class="pill ${m.status === 'approved' ? 'good' : m.status === 'pending' ? 'warn' : 'bad'}">${esc(m.status === 'unapproved' ? 'denied' : m.status)}</span></td>
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
            const choiceUrl = '/api/v1/admin/mods/' + encodeURIComponent(m.guid) + '/choice';
            const electChoice = async () => {
                const result = await dialog({
                    title: m.modders_choice ? 'Edit Modder’s Choice note' : `Make ${m.name} Modder’s Choice?`,
                    text: 'The mod gets a trophy next to its name. The note appears on the mod page; leave it empty to show only the trophy.',
                    confirm: m.modders_choice ? 'Save note' : 'Give trophy',
                    body: `<div class="field"><label for="choice-note">Why it’s Modder’s Choice <span>optional</span></label><textarea id="choice-note" name="note" rows="5" maxlength="1000" placeholder="What makes this mod stand out?">${esc(m.choice_note || '')}</textarea></div>`,
                });

                if (!result)
                    return;

                try {
                    await api(choiceUrl, { method: 'PUT', json: { note: result.note.trim() || null } });
                    toast(m.modders_choice ? 'Note saved.' : `${m.name} is now Modder’s Choice.`);
                    await load();
                } catch (error) {
                    toast(error.message, 'error');
                }
            };
            const medalsUrl = '/api/v1/admin/mods/' + encodeURIComponent(m.guid) + '/medals';
            const medalName = (title, confirm, value = '') => dialog({
                title, confirm,
                text: 'The medal shows on the mod and on its creator’s profile. Hovering it shows this name.',
                body: `<div class="field"><label for="medal-name">Medal name</label><input id="medal-name" name="name" required maxlength="80" placeholder="#1 Winter Modding Contest" value="${esc(value)}"></div>`,
            });
            const addMedal = async () => {
                const result = await medalName(`Give ${m.name} a medal`, 'Give medal');

                if (result)
                    await save(api(medalsUrl, { method: 'POST', json: { name: result.name } }), `Medal given to ${m.name}.`);
            };
            const renameMedal = medal => async () => {
                const result = await medalName('Rename medal', 'Save', medal.name);

                if (result)
                    await save(api(medalsUrl + '/' + medal.id, { method: 'PATCH', json: { name: result.name } }), 'Medal renamed.');
            };
            const removeMedal = medal => async () => {
                if (await confirmDialog(`Remove “${medal.name}”?`, `${m.name} and its creator stop showing this medal.`, 'Remove', true))
                    await save(api(medalsUrl + '/' + medal.id, { method: 'DELETE' }), 'Medal removed.');
            };
            const save = async (promise, message) => {
                try {
                    await promise;
                    toast(message);
                    await load();
                } catch (error) {
                    toast(error.message, 'error');
                }
            };
            const removeChoice = async () => {
                if (!await confirmDialog('Remove Modder’s Choice?', `${m.name} loses its trophy and the note is deleted.`, 'Remove', true))
                    return;

                try {
                    await api(choiceUrl, { method: 'DELETE' });
                    toast('Modder’s Choice removed.');
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
                ...(m.status !== 'deleted' ? [{ label: m.modders_choice ? 'Edit Modder’s Choice note' : 'Make Modder’s Choice', icon: 'trophy', action: electChoice }] : []),
                ...(m.modders_choice ? [{ label: 'Remove Modder’s Choice', icon: 'x', action: removeChoice }] : []),
                ...(m.status !== 'deleted' ? [{ label: 'Give medal', icon: 'medal', action: addMedal }] : []),
                ...(m.medals || []).flatMap(medal => [
                    { label: `Rename “${medal.name}”`, icon: 'edit', action: renameMedal(medal) },
                    { label: `Remove “${medal.name}”`, icon: 'x', action: removeMedal(medal) },
                ]),
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
