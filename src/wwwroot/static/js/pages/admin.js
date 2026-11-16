import { api, app, applyWidths, auth, categories, date, esc, icon, modIcon, money, number, pagination, setTitle, showError, toast, userBadges } from '../core.js';

export async function render() {
    if (!auth.user?.is_admin) {
        showError('Administrators only', 'Administrator access is required.');
        return;
    }

    setTitle('Admin');
    const tabs = ['Mods', 'Disputes', 'Users', 'Analytics', 'Errors', ...(auth.user.is_root ? ['Admins'] : [])];
    let active = new URLSearchParams(location.search).get('tab') || 'Mods';

    if (!tabs.includes(active))
        active = 'Mods';

    app.innerHTML = `
        <div class="page-head"><div><h1>Admin panel</h1></div></div>
        <div class="tabs" role="tablist">${tabs.map(t => `<button role="tab" data-tab="${t}">${t}</button>`).join('')}</div>
        <section id="admin-body"></section>`;

    let generation = 0;

    const select = async tab => {
        const ticket = ++generation;
        const alive = () => ticket === generation;
        history.replaceState(null, '', '/admin?tab=' + tab);
        app.querySelectorAll('[data-tab]').forEach(b => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
        const body = app.querySelector('#admin-body');
        body.innerHTML = '<div class="page-loading"><span class="spinner"></span>Loading…</div>';

        try {
            if (tab === 'Mods')
                await mods(body, alive);
            else if (tab === 'Disputes')
                await disputes(body, alive);
            else if (tab === 'Analytics')
                await analytics(body, alive);
            else if (tab === 'Errors')
                await errors(body);
            else
                await users(body, tab === 'Admins', alive);
        } catch (error) {
            if (alive())
                body.innerHTML = `<div class="error">${esc(error.message)}</div>`;
        }
    };

    app.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => select(b.dataset.tab));
    await select(active);
}

async function mods(body, alive) {
    body.innerHTML = `<div class="toolbar"><input id="mod-search" type="search" placeholder="Search name or GUID" aria-label="Search mods"><select id="mod-status" aria-label="Status"><option value="pending">Pending review</option><option value="approved">Approved</option><option value="unapproved">Unapproved</option><option value="deleted">Deleted</option><option value="all">All</option></select><span class="count" id="mod-counts"></span></div><div id="mod-list"></div><div id="mod-pages"></div>`;
    let page = 1;
    let timer;

    const load = async () => {
        const query = new URLSearchParams({ q: body.querySelector('#mod-search').value, status: body.querySelector('#mod-status').value, page });
        const data = await api('/api/v1/admin/mods?' + query);

        if (!alive())
            return;

        const c = data.counts;
        body.querySelector('#mod-counts').textContent = `${c.pending || 0} pending · ${c.approved || 0} approved · ${c.unapproved || 0} unapproved · ${c.deleted || 0} deleted`;
        body.querySelector('#mod-list').innerHTML = data.items.length ? data.items.map(m => `<article class="surface admin-mod">
            ${modIcon(m)}
            <div><h3>${esc(m.name)}</h3><div class="hint mono">${esc(m.guid)} · v${esc(m.current_version)}</div><div class="hint">by <a href="/profile/${encodeURIComponent(m.author.id)}">${esc(m.author.name)}</a> ${userBadges(m.author)}</div></div>
            <span class="pill ${m.status === 'approved' ? 'good' : m.status === 'pending' ? 'warn' : 'bad'}">${esc(m.status)}</span>
            <div class="actions">
                ${m.status !== 'deleted' ? `<a class="button ghost small" href="/view/${encodeURIComponent(m.guid)}">${icon('eye')}Review</a><a class="button ghost small" href="/view/${encodeURIComponent(m.guid)}/monkeshield">${icon('shield')}Scan</a>` : ''}
                ${m.status !== 'approved' ? `<button class="button small" data-status="approved" data-guid="${esc(m.guid)}">${m.status === 'deleted' ? 'Restore & approve' : 'Approve'}</button>` : ''}
                ${m.status === 'approved' || m.status === 'pending' ? `<button class="button subtle small" data-status="unapproved" data-guid="${esc(m.guid)}">Unapprove</button>` : ''}
                ${m.status !== 'deleted' ? `<button class="button danger small" data-status="deleted" data-guid="${esc(m.guid)}">Delete</button>` : ''}
                <select class="button subtle small" data-choice="${esc(m.guid)}" aria-label="Modder’s Choice"><option value="">Modder’s Choice…</option>${categories.map(cat => `<option>${cat}</option>`).join('')}<option value="__remove">Remove badge</option></select>
                <button class="button ghost small" data-comments="${esc(m.guid)}">${icon('chat')}Notes</button>
                <span class="spacer"></span>
                <button class="button danger small" data-purge="${esc(m.guid)}">${icon('trash')}Purge</button>
            </div>
            <div class="comments" data-comment-panel="${esc(m.guid)}" hidden></div>
        </article>`).join('') : '<div class="empty"><h2>Queue is empty</h2><p>Choose another status to see other submissions.</p></div>';

        body.querySelectorAll('[data-status]').forEach(b => b.onclick = async () => {
            if (b.dataset.status === 'deleted' && !confirm('Remove this mod from listings and downloads? It stays in the deleted queue. Its monthly tiers end now and subscribers stop being billed.'))
                return;

            try {
                const result = await api('/api/v1/admin/mods/' + encodeURIComponent(b.dataset.guid), { method: 'PATCH', json: { status: b.dataset.status } });
                toast(result.subscriptions_failed ? `Mod status updated, but ${result.subscriptions_failed} subscription(s) couldn’t be cancelled. Cancel them in Stripe.` : result.subscriptions_cancelled ? `Mod status updated. ${result.subscriptions_cancelled} subscription(s) cancelled.` : 'Mod status updated.', result.subscriptions_failed ? 'error' : '');
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
        body.querySelectorAll('[data-choice]').forEach(s => s.onchange = async () => {
            if (!s.value)
                return;

            try {
                await api('/api/v1/admin/mods/' + encodeURIComponent(s.dataset.choice) + '/choice', { method: 'PUT', json: { category: s.value === '__remove' ? null : s.value } });
                toast('Modder’s Choice updated.');
            } catch (error) {
                toast(error.message, 'error');
            }

            s.value = '';
        });
        body.querySelectorAll('[data-purge]').forEach(b => b.onclick = async () => {
            if (!confirm('Permanently delete this mod and all of its releases, files and history? This cannot be undone.'))
                return;

            try {
                await api('/api/v1/admin/mods/' + encodeURIComponent(b.dataset.purge), { method: 'DELETE' });
                toast('Mod permanently deleted.');
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
        body.querySelectorAll('[data-comments]').forEach(b => b.onclick = () => notes(body, b.dataset.comments));
        pagination(body.querySelector('#mod-pages'), data.total, data.limit, page, p => { page = p; load(); });
    };

    body.querySelector('#mod-status').onchange = () => { page = 1; load(); };
    body.querySelector('#mod-search').oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { page = 1; load(); }, 250);
    };

    await load();
}

async function notes(body, guid) {
    const panel = [...body.querySelectorAll('[data-comment-panel]')].find(el => el.dataset.commentPanel === guid);

    if (!panel.hidden) {
        panel.hidden = true;
        return;
    }

    panel.hidden = false;
    const data = await api('/api/v1/admin/mods/' + encodeURIComponent(guid) + '/comments');
    panel.innerHTML = `<div class="stack">${data.items.map(c => `<div class="comment"><span></span><div><header><strong>${esc(c.display_name)}</strong><time>${esc(date(c.created_at))}</time></header><p>${esc(c.body)}</p></div></div>`).join('') || '<p class="hint">No notes yet. Only administrators can see these.</p>'}
        <form class="stack"><textarea required maxlength="5000" rows="2" placeholder="Add a note for other admins…" aria-label="Admin note"></textarea><div class="row"><span class="spacer"></span><button class="button subtle small">Add note</button></div></form></div>`;
    panel.querySelector('form').onsubmit = async event => {
        event.preventDefault();

        try {
            await api('/api/v1/admin/mods/' + encodeURIComponent(guid) + '/comments', { method: 'POST', json: { body: panel.querySelector('textarea').value } });
            panel.hidden = true;
            notes(body, guid);
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

async function users(body, admins, alive) {
    body.innerHTML = `${admins ? '<form class="surface panel row" id="grant"><label for="grant-id">Grant admin to Discord user ID</label><input id="grant-id" class="mono-input" required inputmode="numeric" pattern="[0-9]+" placeholder="User ID"><button class="button">Add admin</button></form><div class="gap"></div>' : ''}
        <div class="toolbar"><input id="user-search" type="search" placeholder="Search username, display name or ID" aria-label="Search users"><span class="count" id="user-count"></span></div>
        <div class="table-wrap surface"><table class="data"><thead><tr><th>User</th><th>Mods</th><th>Status</th><th>Actions</th></tr></thead><tbody id="user-rows"></tbody></table></div><div id="user-pages"></div>`;
    let page = 1;
    let timer;

    const load = async () => {
        const data = await api('/api/v1/admin/' + (admins ? 'admins' : 'users') + '?' + new URLSearchParams({ q: body.querySelector('#user-search').value, page }));

        if (!alive())
            return;

        body.querySelector('#user-count').textContent = `${data.total} ${admins ? 'administrators' : 'users'}`;
        body.querySelector('#user-rows').innerHTML = data.items.map(u => `<tr>
            <td><a href="/profile/${encodeURIComponent(u.id)}"><strong>${esc(u.display_name)}</strong></a> ${userBadges(u, true)}<div class="hint mono">@${esc(u.username)} · ${esc(u.id)}</div></td>
            <td class="mono">${u.mod_count}</td>
            <td>${u.is_root ? '<span class="pill accent">Root</span>' : u.is_banned ? '<span class="pill bad">Banned</span>' : '<span class="pill good">Active</span>'}</td>
            <td><div class="row">${admins
                ? (u.is_root ? '<span class="hint">Configured on the server</span>' : `<button class="button danger small" data-admin="${esc(u.id)}">Remove admin</button>`)
                : `<button class="button subtle small" data-flag="is_verified" data-value="${!u.is_verified}" data-uid="${esc(u.id)}">${u.is_verified ? 'Unverify' : 'Verify'}</button>${!u.is_root && (!u.is_admin || auth.user.is_root) ? `<button class="button ${u.is_banned ? 'subtle' : 'danger'} small" data-flag="is_banned" data-value="${!u.is_banned}" data-uid="${esc(u.id)}">${u.is_banned ? 'Unban' : 'Ban'}</button>` : ''}`}</div></td>
        </tr>`).join('') || '<tr><td colspan="4" class="hint">No users match.</td></tr>';

        body.querySelectorAll('[data-flag]').forEach(b => b.onclick = async () => {
            if (b.dataset.flag === 'is_banned' && b.dataset.value === 'true' && !confirm('Ban this user? They lose access immediately.'))
                return;

            try {
                await api('/api/v1/admin/users/' + encodeURIComponent(b.dataset.uid), { method: 'PATCH', json: { [b.dataset.flag]: b.dataset.value === 'true' } });
                toast('User updated.');
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
        body.querySelectorAll('[data-admin]').forEach(b => b.onclick = async () => {
            if (!confirm('Remove administrator access from this user?'))
                return;

            try {
                await api('/api/v1/admin/admins/' + encodeURIComponent(b.dataset.admin), { method: 'PUT', json: { is_admin: false } });
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
        pagination(body.querySelector('#user-pages'), data.total, data.limit, page, p => { page = p; load(); });
    };

    body.querySelector('#user-search').oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { page = 1; load(); }, 250);
    };
    body.querySelector('#grant')?.addEventListener('submit', async event => {
        event.preventDefault();

        try {
            await api('/api/v1/admin/admins/' + encodeURIComponent(body.querySelector('#grant-id').value.trim()), { method: 'PUT', json: { is_admin: true } });
            body.querySelector('#grant-id').value = '';
            toast('Administrator added.');
            await load();
        } catch (error) {
            toast(error.message, 'error');
        }
    });

    await load();
}

function bars(items) {
    if (!items.length)
        return '<p class="hint">No activity in this period.</p>';

    const max = Math.max(1, ...items.map(i => i.value));
    return `<div class="bars">${items.map(i => `<div class="bar-row"><div><span>${esc(i.name)}</span><strong>${number(i.value)}</strong></div><div class="bar"><i data-width="${(i.value / max * 100).toFixed(1)}"></i></div></div>`).join('')}</div>`;
}

function chart(rows, days) {
    const byDay = Object.fromEntries(rows.map(r => [r.day, r]));
    const data = [];

    for (let i = days - 1; i >= 0; i--) {
        const d = new Date();
        d.setUTCDate(d.getUTCDate() - i);
        const day = d.toISOString().slice(0, 10);
        data.push(byDay[day] || { day, views: 0, downloads: 0 });
    }

    const max = Math.max(1, ...data.flatMap(r => [r.views, r.downloads]));
    const point = (r, i, key) => `${(40 + i / Math.max(1, days - 1) * 560).toFixed(1)},${(190 - r[key] / max * 160).toFixed(1)}`;
    const line = key => data.map((r, i) => (i ? 'L' : 'M') + point(r, i, key)).join(' ');
    const area = key => line(key) + ` L600,190 L40,190 Z`;

    return `<svg class="chart" viewBox="0 0 620 230" role="img" aria-label="Daily downloads and views">
        <defs><linearGradient id="fill-d" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="var(--accent)" stop-opacity=".35"/><stop offset="1" stop-color="var(--accent)" stop-opacity="0"/></linearGradient></defs>
        ${[0, .5, 1].map(r => `<path d="M40 ${190 - r * 160}H600" stroke="rgb(255 255 255 / 8%)"/><text x="32" y="${194 - r * 160}" text-anchor="end">${Math.round(max * r)}</text>`).join('')}
        <path d="${area('downloads')}" fill="url(#fill-d)"/>
        <path d="${line('views')}" fill="none" stroke="#a995dc" stroke-width="1.6" stroke-dasharray="4 3"/>
        <path d="${line('downloads')}" fill="none" stroke="var(--accent)" stroke-width="2.4"/>
        <text x="40" y="218">${data[0].day}</text><text x="600" y="218" text-anchor="end">${data.at(-1).day}</text>
    </svg><div class="row hint"><span class="pill accent">Downloads</span><span class="pill">Views (dashed)</span></div>`;
}

async function analytics(body, alive) {
    body.innerHTML = `<div class="toolbar">
        <select id="a-scope" aria-label="Scope"><option value="site">Entire site</option><option value="category">Category</option><option value="mod">Single mod</option></select>
        <select id="a-category" aria-label="Category" hidden>${categories.map(c => `<option>${c}</option>`).join('')}</select>
        <input id="a-mod" class="mono-input" placeholder="com.author.mod" list="a-mods" aria-label="Mod GUID" hidden><datalist id="a-mods"></datalist>
        <select id="a-days" aria-label="Period"><option value="7">7 days</option><option value="30" selected>30 days</option><option value="90">90 days</option><option value="365">1 year</option></select>
        <button class="button subtle" id="a-apply">Apply</button></div><div id="a-results"></div>`;

    let world;
    let sequence = 0;

    const load = async () => {
        const ticket = ++sequence;
        const scope = body.querySelector('#a-scope').value;
        const key = scope === 'category' ? body.querySelector('#a-category').value : scope === 'mod' ? body.querySelector('#a-mod').value.trim() : '';
        const results = body.querySelector('#a-results');

        if (scope === 'mod' && !key) {
            results.innerHTML = '<p class="hint">Enter a mod GUID.</p>';
            return;
        }

        try {
            const data = await api('/api/v1/admin/analytics?' + new URLSearchParams({ scope, key, days: body.querySelector('#a-days').value }));

            if (!alive() || ticket !== sequence)
                return;

            results.innerHTML = `<div class="stat-cards">
                    <div class="surface stat-card"><span>Downloads</span><strong>${number(data.downloads)}</strong></div>
                    <div class="surface stat-card"><span>Page views</span><strong>${number(data.views)}</strong></div>
                    <div class="surface stat-card"><span>All-time downloads</span><strong>${number(data.lifetime_downloads)}</strong></div>
                    <div class="surface stat-card"><span>Mods in scope</span><strong>${number(data.mod_count)}</strong></div>
                </div>
                <p class="hint">Daily and geographic activity is tracked from ${data.tracked_since ? esc(date(data.tracked_since)) : 'the first visit'}. Views count page loads, not people.</p>
                <div class="analytics-grid">
                    <section class="surface panel"><h2>Activity over time</h2>${chart(data.daily, data.days)}</section>
                    <section class="surface panel"><h2>By category</h2>${bars(data.categories.map(c => ({ name: c.category, value: c.activity })))}</section>
                    <section class="surface panel"><h2>Around the world</h2><div id="world"></div><p class="hint">Unknown location: ${number(data.unknown_activity)} events${data.geo_configured ? '' : ' · country header not trusted'} · Map: Natural Earth</p><div id="countries"></div></section>
                    <section class="surface panel"><h2>Most downloaded</h2>${bars(data.top_mods.map(m => ({ name: m.name || m.guid, value: m.downloads })))}</section>
                </div>`;

            world ??= await fetch('/static/world-map.json').then(r => r.json());

            if (!alive() || ticket !== sequence)
                return;

            const counts = Object.fromEntries(data.countries.map(c => [c.country, c.activity]));
            const max = Math.max(1, ...data.countries.filter(c => c.country !== 'XX').map(c => c.activity));
            results.querySelector('#world').innerHTML = `<svg class="world-map" viewBox="0 0 720 360" role="img" aria-label="Activity by country">${world.map(c => {
                const count = counts[c.code] || 0;
                const opacity = count ? (0.25 + 0.75 * count / max).toFixed(2) : 0.08;
                return `<path d="${esc(c.path)}" fill="var(--accent)" fill-opacity="${opacity}" tabindex="0"><title>${esc(c.name)}: ${count}</title></path>`;
            }).join('')}</svg>`;

            const names = new Intl.DisplayNames(['en'], { type: 'region' });
            results.querySelector('#countries').innerHTML = bars(data.countries.filter(c => c.country !== 'XX').slice(0, 8).map(c => ({ name: names.of(c.country) || c.country, value: c.activity })));
            applyWidths(results);
        } catch (error) {
            if (alive())
                results.innerHTML = `<div class="error">${esc(error.message)}</div>`;
        }
    };

    body.querySelector('#a-scope').onchange = () => {
        const scope = body.querySelector('#a-scope').value;
        body.querySelector('#a-category').hidden = scope !== 'category';
        body.querySelector('#a-mod').hidden = scope !== 'mod';

        if (scope !== 'mod')
            load();
    };
    body.querySelector('#a-category').onchange = load;
    body.querySelector('#a-days').onchange = load;
    body.querySelector('#a-apply').onclick = load;

    const all = await api('/api/v1/admin/mods?status=all&limit=100');
    body.querySelector('#a-mods').innerHTML = all.items.map(m => `<option value="${esc(m.guid)}">${esc(m.name)}</option>`).join('');
    await load();
}

async function errors(body) {
    const data = await api('/api/v1/admin/errors');
    body.innerHTML = data.items.length ? data.items.map(e => `<details class="surface error-log"><summary class="row"><code>${esc(e.id)}</code><strong>${esc(e.exception_type)}</strong><span class="hint mono">${esc(e.method)} ${esc(e.path)}</span><span class="spacer"></span><span class="hint">${esc(date(e.created_at))}</span></summary><p>${esc(e.message)}</p><pre>${esc(e.traceback)}</pre></details>`).join('<div class="gap"></div>')
        : '<div class="empty"><h2>No errors recorded</h2><p>The last 50 unexpected server errors appear here.</p></div>';
}

async function disputes(body, alive) {
    body.innerHTML = `<div class="toolbar"><select id="dispute-status" aria-label="Status"><option value="open">Open</option><option value="accepted">Refunded</option><option value="rejected">Rejected</option><option value="all">All</option></select>
        <span class="hint">Accepting refunds the buyer in full, including the platform fee. For memberships it refunds the latest payment and ends the subscription.</span></div><div id="dispute-list"></div>`;

    const load = async () => {
        const data = await api('/api/v1/admin/disputes?status=' + body.querySelector('#dispute-status').value);

        if (!alive())
            return;

        body.querySelector('#dispute-list').innerHTML = data.items.length ? data.items.map(d => `<article class="surface panel dispute">
            <div class="row"><strong>${esc(d.kind === 'purchase' ? 'Purchase' : 'Membership')}</strong>
                <span>${d.guid ? `<a href="/view/${encodeURIComponent(d.guid)}">${esc(d.mod_name || d.guid)}</a>` : ''}${d.tier_name ? ' · ' + esc(d.tier_name) : ''}</span>
                <span class="spacer"></span>${d.amount_cents != null ? `<span class="mono">${esc(money(d.amount_cents, d.currency))}</span>` : ''}
                <span class="pill ${d.status === 'open' ? 'warn' : d.status === 'accepted' ? 'good' : ''}">${esc(d.status === 'accepted' ? 'refunded' : d.status)}</span></div>
            <p class="hint">Buyer <a href="/profile/${encodeURIComponent(d.user.id)}">${esc(d.user.name)}</a>${d.seller ? ` · Seller <a href="/profile/${encodeURIComponent(d.seller.id)}">${esc(d.seller.name)}</a>` : ''} · opened ${esc(date(d.created_at))}${d.paid_at ? ` · paid ${esc(date(d.paid_at))}` : ''}</p>
            <blockquote class="dispute-reason">${esc(d.reason)}</blockquote>
            ${d.staff_note ? `<p class="hint">Staff note: ${esc(d.staff_note)}</p>` : ''}
            ${d.status === 'open' ? `<div class="row"><input class="grow" data-note="${esc(d.id)}" maxlength="2000" placeholder="Note to the buyer (optional)" aria-label="Note to the buyer"><button class="button subtle small" data-resolve="reject" data-id="${esc(d.id)}">Reject</button><button class="button small" data-resolve="accept" data-id="${esc(d.id)}">Accept & refund</button></div>` : ''}
        </article>`).join('') : '<div class="empty"><h2>No disputes here</h2><p>Disputes opened by buyers show up in this list.</p></div>';

        body.querySelectorAll('[data-resolve]').forEach(button => button.onclick = async () => {
            const accept = button.dataset.resolve === 'accept';

            if (accept && !confirm('Refund this buyer in full? The money comes back out of the seller’s Stripe balance.'))
                return;

            button.disabled = true;

            try {
                const note = body.querySelector(`[data-note="${CSS.escape(button.dataset.id)}"]`).value;
                await api('/api/v1/admin/disputes/' + encodeURIComponent(button.dataset.id) + '/resolve', { method: 'POST', json: { action: button.dataset.resolve, note } });
                toast(accept ? 'Refund issued.' : 'Dispute closed.');
                await load();
            } catch (error) {
                toast(error.message, 'error');
                button.disabled = false;
            }
        });
    };

    body.querySelector('#dispute-status').onchange = load;
    await load();
}
