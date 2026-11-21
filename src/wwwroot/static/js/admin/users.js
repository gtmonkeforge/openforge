import { api, auth, avatar, copy, date, esc, icon, modIcon, pagination, toast, userBadges } from '../core.js';
import { compose } from './tickets.js';
import { boot, confirmDialog, dialog, drawer, fullWhen, head, menu, site, when } from './ui.js';

const filters = [['all', 'Everyone'], ['admins', 'Admins'], ['verified', 'Verified'], ['sellers', 'Stripe sellers'], ['banned', 'Banned'], ['email', 'Email accounts'], ['discord', 'Discord accounts'], ['deleted', 'Deleted']];

export async function render({ app, params, alive }) {
    app.innerHTML = `${head('Users', 'Everyone with an account. Click a row for details.')}
        <div class="toolbar">
            <div class="toolbar-search">${icon('search')}<input id="user-search" type="search" placeholder="Search nickname, username, email, user ID or Stripe ID" aria-label="Search users" value="${esc(params.get('q') || '')}"></div>
            <select id="user-filter" aria-label="Filter">${filters.map(([k, l]) => `<option value="${k}">${l}</option>`).join('')}</select>
            <select id="user-sort" aria-label="Sort"><option value="name">By name</option><option value="newest">Newest first</option><option value="mods">Most mods</option></select>
            <span class="count" id="user-count"></span>
        </div>
        <div class="table-wrap surface"><table class="data users"><thead><tr><th>Nickname</th><th>Username</th><th>Mods</th><th>User ID</th><th>Stripe account</th><th>Status</th><th></th></tr></thead><tbody id="user-rows"></tbody></table></div>
        <div id="user-pages"></div>`;

    const search = app.querySelector('#user-search');
    const filter = app.querySelector('#user-filter');
    const sort = app.querySelector('#user-sort');
    filter.value = filters.some(f => f[0] === params.get('filter')) ? params.get('filter') : 'all';
    let page = 1;
    let timer;
    let opened = false;

    const load = async () => {
        const data = await api('/api/v1/admin/people?' + new URLSearchParams({ q: search.value.trim(), filter: filter.value, sort: sort.value, page }));

        if (!alive())
            return;

        app.querySelector('#user-count').textContent = `${data.total} ${data.total === 1 ? 'user' : 'users'}`;
        app.querySelector('#user-rows').innerHTML = data.items.map((u, i) => `<tr data-row="${i}">
            <td><div class="user-cell">${avatar({ ...u, display_name: u.nickname }, 'sm')}<div><strong>${esc(u.nickname)}</strong>${u.email ? `<small>${esc(u.email)}${u.email_verified ? '' : ' (unconfirmed)'}</small>` : ''}</div></div></td>
            <td class="mono">@${esc(u.username)}${u.provider === 'discord' ? ' <span class="pill">Discord</span>' : u.provider === 'group' ? ' <span class="pill">Group</span>' : ''}</td>
            <td class="mono">${u.mod_count}</td>
            <td class="id-cell"><button data-copy-id="${esc(u.id)}" title="Copy user ID">${esc(u.id)}</button></td>
            <td class="id-cell">${u.stripe_account_id ? `<button data-copy-id="${esc(u.stripe_account_id)}" title="Copy Stripe account ID">${esc(u.stripe_account_id)}</button>${u.stripe_charges_enabled ? '' : ' <span class="pill warn">limited</span>'}` : '<span class="faint">—</span>'}</td>
            <td>${u.deleted_at ? '<span class="pill">Deleted</span>' : u.is_root ? '<span class="pill accent">Root</span>' : u.is_banned ? '<span class="pill bad">Banned</span>' : '<span class="pill good">Active</span>'} ${userBadges(u)}</td>
            <td><div class="kebab"><button class="icon-button" data-more="${i}" aria-label="Actions for ${esc(u.nickname)}">${icon('more')}</button></div></td>
        </tr>`).join('') || '<tr><td colspan="7" class="hint">No users match.</td></tr>';

        app.querySelectorAll('[data-copy-id]').forEach(b => b.onclick = event => {
            event.stopPropagation();
            copy(b.dataset.copyId, 'Copied.');
        });
        app.querySelectorAll('[data-row]').forEach(row => row.onclick = event => {
            if (!event.target.closest('.kebab, [data-copy-id]'))
                details(data.items[Number(row.dataset.row)].id, load);
        });
        app.querySelectorAll('[data-more]').forEach(b => b.onclick = event => {
            event.stopPropagation();
            menu(b, actions(data.items[Number(b.dataset.more)], load));
        });

        // Coming from a review or ticket with ?q=<id>: open that person straight away.
        if (!opened && data.items.length === 1 && params.get('q') === data.items[0].id) {
            opened = true;
            details(data.items[0].id, load);
        }

        pagination(app.querySelector('#user-pages'), data.total, data.limit, page, p => { page = p; load(); });
    };

    filter.onchange = sort.onchange = () => { page = 1; load(); };
    search.oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { page = 1; load(); }, 250);
    };

    await load();
}

function canManage(u) {
    return !u.deleted_at && !(u.is_root && !auth.user.is_root) && !(u.is_admin && u.id !== auth.user.id && !auth.user.is_root);
}

async function run(promise, success, reload) {
    try {
        const result = await promise;
        toast(typeof success === 'function' ? success(result) : success);
        await reload?.();
        return result;
    } catch (error) {
        toast(error.message, 'error');
        return null;
    }
}

function actions(u, reload) {
    const base = '/api/v1/admin/people/' + encodeURIComponent(u.id);
    const allowed = canManage(u);
    const locked = allowed ? false : 'Only the root administrator can change this account.';

    return [
        { label: 'View details', icon: 'eye', action: () => details(u.id, reload) },
        { label: 'Open profile', icon: 'external', href: site('/profile/' + encodeURIComponent(u.id)) },
        'sep',
        { label: 'Email from support', icon: 'mail', disabled: !u.email || !u.email_verified ? 'No confirmed email address.' : !boot.support_configured ? 'The support mailbox isn’t configured.' : false, action: () => compose({ userId: u.id, to: u.email, name: u.nickname }) },
        {
            label: 'Send password reset', icon: 'key', disabled: locked || (u.provider !== 'email' ? 'Discord accounts have no password.' : !u.email ? 'No email address.' : false), action: async () => {
                if (await confirmDialog('Send a password reset?', `A one-hour reset link goes to ${u.email}. Their current password keeps working until they use it.`, 'Send link'))
                    await run(api(base + '/password-reset', { method: 'POST' }), r => `Reset link sent to ${r.to}.`);
            },
        },
        {
            label: `Remove passkeys${u.passkey_count ? ` (${u.passkey_count})` : ''}`, icon: 'lock', disabled: locked || (!u.passkey_count ? 'This account has no passkeys.' : false), action: async () => {
                if (await confirmDialog('Remove every passkey?', `${u.nickname} won’t be able to sign in with a passkey until they add a new one. They’re emailed about it.`, 'Remove passkeys', true))
                    await run(api(base + '/passkeys', { method: 'DELETE' }), r => `${r.removed} passkey(s) removed.`, reload);
            },
        },
        {
            label: 'Sign out everywhere', icon: 'logout', disabled: locked, action: async () => {
                if (await confirmDialog('Sign out everywhere?', 'Ends every browser and app session for this account.', 'Sign out'))
                    await run(api(base + '/sign-out', { method: 'POST' }), 'Signed out of every device.');
            },
        },
        {
            label: u.is_verified ? 'Remove verification' : 'Verify', icon: 'check', disabled: u.deleted_at ? true : false,
            action: () => run(api('/api/v1/admin/users/' + encodeURIComponent(u.id), { method: 'PATCH', json: { is_verified: !u.is_verified } }), 'User updated.', reload),
        },
        {
            label: u.is_developer ? 'Remove Developer badge' : 'Give Developer badge', icon: 'hammers',
            disabled: !auth.user.is_root ? 'Only the root administrator can give the Developer badge.' : u.deleted_at ? true : false,
            action: () => run(api('/api/v1/admin/developers/' + encodeURIComponent(u.id), { method: 'PUT', json: { is_developer: !u.is_developer } }),
                u.is_developer ? 'Developer badge removed.' : 'Developer badge given.', reload),
        },
        'sep',
        {
            label: u.is_banned ? 'Unban' : 'Ban', icon: 'ban', danger: !u.is_banned, disabled: locked || (u.is_root ? 'The root administrator can’t be banned.' : false), action: async () => {
                if (!u.is_banned && !await confirmDialog(`Ban ${u.nickname}?`, 'They’re signed out and blocked immediately. Their memberships stop billing.', 'Ban', true))
                    return;

                await run(api('/api/v1/admin/users/' + encodeURIComponent(u.id), { method: 'PATCH', json: { is_banned: !u.is_banned } }), u.is_banned ? 'Unbanned.' : 'Banned.', reload);
            },
        },
        {
            label: 'Delete account', icon: 'trash', danger: true, disabled: locked || (u.is_root || u.id === auth.user.id ? 'You can’t delete this account here.' : false), action: () => deleteAccount(u, reload),
        },
    ];
}

async function deleteAccount(u, reload) {
    const result = await dialog({
        title: `Delete ${u.nickname}?`,
        text: 'This removes their name, email, password, passkeys, API keys, friends and comments, takes their mods off the site and cancels their subscriptions. Payment records are kept for accounting. It can’t be undone.',
        danger: true,
        confirm: 'Delete account',
        body: `<div class="field"><label for="confirm-name">Type <strong class="mono">${esc(u.username)}</strong> to confirm</label><input id="confirm-name" name="confirm" required autocomplete="off"></div>`,
    });

    if (!result)
        return;

    await run(api('/api/v1/admin/people/' + encodeURIComponent(u.id), { method: 'DELETE', json: { confirm: result.confirm } }),
        r => `Account deleted. ${r.mods_removed} mod(s) removed${r.subscriptions_failed ? `, ${r.subscriptions_failed} subscription(s) need cancelling in Stripe` : ''}.`, reload);
    document.querySelector('.drawer')?.remove();
}

async function details(id, reload) {
    const { panel, close } = drawer('<div class="page-loading"><span class="spinner"></span></div>');
    let data;

    try {
        data = await api('/api/v1/admin/people/' + encodeURIComponent(id));
    } catch (error) {
        panel.innerHTML = `<div class="error">${esc(error.message)}</div>`;
        return;
    }

    const u = data.user;
    const refresh = async () => {
        await reload();
        close();
        details(id, reload);
    };

    panel.innerHTML = `
        <div class="drawer-head">${avatar({ ...u, display_name: u.nickname })}<div class="grow"><h2>${esc(u.nickname)} ${userBadges(u)}</h2><div class="hint mono">@${esc(u.username)}${u.discord_username && u.discord_username !== u.username ? ` · Discord ${esc(u.discord_username)}` : ''}</div></div>
            <div class="kebab"><button class="button subtle small" id="drawer-actions">${icon('more')}Actions</button></div><button class="icon-button" id="drawer-close" aria-label="Close">${icon('x')}</button></div>
        <dl class="details-list">
            <div><dt>User ID</dt><dd class="mono">${esc(u.id)}</dd></div>
            <div><dt>Email</dt><dd>${u.email ? `${esc(u.email)} ${u.email_verified ? '<span class="pill good">confirmed</span>' : '<span class="pill warn">unconfirmed</span>'}` : '—'}</dd></div>
            <div><dt>Sign-in</dt><dd>${esc(u.provider === 'email' ? 'Email' : u.provider === 'group' ? 'Publishing group' : 'Discord')}${data.has_password ? ' · password' : ''} · ${u.passkey_count} passkey(s) · ${data.api_keys} API key(s)</dd></div>
            <div><dt>Joined</dt><dd>${u.created_at ? esc(fullWhen(u.created_at)) : 'Unknown'}</dd></div>
            <div><dt>Status</dt><dd>${u.deleted_at ? `Deleted ${esc(date(u.deleted_at))}` : u.is_banned ? '<span class="pill bad">Banned</span>' : '<span class="pill good">Active</span>'}</dd></div>
            <div><dt>Stripe</dt><dd>${data.stripe ? `<span class="mono">${esc(data.stripe.account_id)}</span> · ${data.stripe.charges_enabled ? 'charges on' : 'charges off'} · ${data.stripe.transfers_enabled ? 'payouts on' : 'payouts off'}${data.stripe.profile_issues ? ` · <span class="error-text">${esc(data.stripe.profile_issues)}</span>` : ''}` : '—'}</dd></div>
            <div><dt>Buying</dt><dd>${data.live_subscriptions} live subscription(s)</dd></div>
            ${data.groups.length ? `<div><dt>Groups</dt><dd>${data.groups.map(g => `${esc(g.name)} (${esc(g.role)})`).join(', ')}</dd></div>` : ''}
        </dl>
        ${data.passkeys.length ? `<div class="drawer-section"><h3>Passkeys</h3><div class="mini-list">${data.passkeys.map(p => `<div>${icon('lock')}<span class="grow">${esc(p.name)} <span class="hint">added ${esc(date(p.created_at))}${p.last_used_at ? ` · used ${esc(when(p.last_used_at))}` : ''}</span></span>${canManage(u) ? `<button class="button danger small" data-passkey="${esc(p.id)}">Remove</button>` : ''}</div>`).join('')}</div></div>` : ''}
        <div class="drawer-section"><h3>Mods · ${data.mods.length}</h3><div class="mini-list">${data.mods.map(m => `<a href="/admin/reviews/${encodeURIComponent(m.guid)}">${modIcon(m)}<span class="grow">${esc(m.name)}</span><span class="pill">${esc(m.status)}</span></a>`).join('') || '<p class="hint">No mods.</p>'}</div></div>
        <div class="drawer-section"><h3>Support tickets · ${data.tickets.length}</h3><div class="mini-list">${data.tickets.map(t => `<a href="/admin/tickets/${t.id}?folder=all"><span class="mono">#${t.id}</span><span class="grow">${esc(t.subject)}</span><span class="pill">${esc(t.status)}</span></a>`).join('') || '<p class="hint">No tickets.</p>'}</div></div>
        <div class="drawer-section"><h3>Staff actions</h3><div class="audit-list">${data.audit.map(a => `<div class="audit-item"><div><strong>${esc(a.actor || '?')}</strong> <code>${esc(a.action)}</code></div><time>${esc(when(a.created_at))}</time></div>`).join('') || '<p class="hint">None.</p>'}</div></div>
        <div class="drawer-section row"><a class="button ghost small" href="${esc(data.profile_url)}" target="_blank" rel="noopener">${icon('external')}Public profile</a></div>`;

    panel.querySelector('#drawer-close').onclick = close;
    panel.querySelector('#drawer-actions').onclick = event => menu(event.currentTarget, actions(u, refresh).filter(item => item === 'sep' || item.label !== 'View details'));
    panel.querySelectorAll('a[href^="/admin"]').forEach(link => link.addEventListener('click', () => close()));
    panel.querySelectorAll('[data-passkey]').forEach(button => button.onclick = async () => {
        if (await confirmDialog('Remove this passkey?', 'They’re emailed about it and can add a new one from their security settings.', 'Remove', true))
            await run(api(`/api/v1/admin/people/${encodeURIComponent(u.id)}/passkeys/${encodeURIComponent(button.dataset.passkey)}`, { method: 'DELETE' }), 'Passkey removed.', refresh);
    });
}
