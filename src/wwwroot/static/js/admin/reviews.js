import { ago, api, avatar, capLabels, date, esc, icon, loaderLabels, modIcon, money, number, size, toast, userBadges } from '../core.js';
import { shieldRing, shieldState } from '../pages/mod.js';
import { navigate } from './main.js';
import { confirmDialog, counters, head, site, when } from './ui.js';

const quickReasons = [
    ['Description', 'The description doesn’t explain what the mod does or how to use it. Add a clear summary and any setup steps.'],
    ['Screenshots', 'The screenshots don’t show the mod, or include content that isn’t allowed. Replace them with images of the mod in use.'],
    ['Scan failed', 'MonkeShield flagged code in this release that needs explaining or removing. See the failing checks, then publish a fixed release.'],
    ['Source', 'Add a link to the source code so staff and users can see what the mod does.'],
    ['Duplicate', 'This looks like a copy of an existing mod. If it’s yours, update the original listing instead; if it isn’t, you need the author’s permission.'],
    ['GUID', 'The GUID doesn’t identify this mod clearly. Use one that’s unique to you and the mod, usually com.yourname.modname, and matches the GUID in your plugin. A mod’s GUID can’t be changed, so publish again under the corrected one.'],
    ['Wrong category', 'Choose the category that matches what this mod does.'],
    ['Rules', 'This mod breaks the rules for what can be listed. Check the Terms of Service before resubmitting.'],
];

// GUIDs are free-form now, so the conventional reverse-domain shape is only a hint for reviewers.
const conventional = /^[a-z][a-z0-9_-]*(\.[a-z][a-z0-9_-]*){2,}$/i;

function guidPill(guid) {
    return conventional.test(guid) ? '' : '<span class="pill warn" title="Not in the usual com.author.mod form. Check it identifies this mod and matches its plugin.">Unusual GUID</span>';
}

export async function render(ctx) {
    if (ctx.rest)
        return review(ctx, ctx.rest);

    return queue(ctx);
}

function shieldPill(shield) {
    const s = shieldState(shield);
    const tone = s.state === 'pass' ? 'good' : s.state === 'fail' ? 'bad' : '';
    const detail = s.measured ? ` · ${shield.failed_checks}/${shield.total_checks}` : '';
    return `<span class="pill ${tone}">${icon('shield')}${esc(s.label)}${detail}</span>`;
}

async function queue({ app, params, alive }) {
    const status = params.get('status') === 'unapproved' ? 'unapproved' : 'pending';
    const data = await api('/api/v1/admin/reviews?status=' + status);

    if (!alive())
        return;

    app.innerHTML = `${head('Reviews', 'Approve or deny submissions. Denials go back to the author with your reason so they can fix and resubmit.',
        data.items.length && status === 'pending' ? `<a class="button" href="/admin/reviews/${encodeURIComponent(data.items[0].guid)}">${icon('eye')}Start reviewing</a>` : '')}
        <div class="toolbar"><div class="segmented" role="tablist">
            <a href="/admin/reviews" ${status === 'pending' ? 'aria-current="page"' : ''}>Waiting <span class="mono">${number(data.counts.pending)}</span></a>
            <a href="/admin/reviews?status=unapproved" ${status === 'unapproved' ? 'aria-current="page"' : ''}>Denied <span class="mono">${number(data.counts.unapproved)}</span></a>
        </div><span class="spacer"></span><span class="hint">${status === 'pending' ? 'Oldest first.' : 'Waiting on their authors to resubmit.'}</span></div>
        <div class="queue">${data.items.map(m => `<a class="queue-item" href="/admin/reviews/${encodeURIComponent(m.guid)}">
            ${modIcon(m)}
            <div><h3>${esc(m.name)}</h3><div class="meta"><span>by ${esc(m.author.name)}</span>${m.author.is_verified ? '<span class="pill good">Verified author</span>' : ''}<span class="mono">${esc(m.guid)}</span>${guidPill(m.guid)}<span class="mono">v${esc(m.version)}</span><span>${esc(m.category)}</span>${m.paid ? '<span class="pill accent">Paid</span>' : ''}${m.denials ? `<span class="pill warn">${m.denials} earlier denial${m.denials === 1 ? '' : 's'}</span>` : ''}</div></div>
            ${shieldPill(m.shield)}
            <span class="age" title="${esc(m.submitted_at)}">${esc(ago(m.submitted_at))}</span>
        </a>`).join('') || `<div class="empty"><h2>${status === 'pending' ? 'Queue is clear' : 'Nothing denied'}</h2><p>${status === 'pending' ? 'New submissions and edited mods show up here.' : 'Mods you deny appear here until their authors resubmit.'}</p></div>`}</div>`;
}

function modPage(mod) {
    const caps = Object.keys(capLabels).filter(key => mod.capabilities?.[key]);
    const releases = (mod.releases || []).slice(0, 6);

    return `
        ${mod.banner ? `<div class="mod-banner"><img src="${esc(mod.banner)}" alt=""></div>` : ''}
        <section class="mod-hero surface">
            ${modIcon(mod)}
            <div>
                <h1>${esc(mod.name)}</h1>
                <div class="meta-line">
                    <span class="pill">${esc(mod.category)}</span>
                    <span class="pill mono">v${esc(mod.current_version || '—')}</span>
                    ${mod.visibility !== 'public' ? `<span class="pill">${esc(mod.visibility)}</span>` : ''}
                    ${mod.gated ? `<span class="pill accent">${mod.price_cents > 0 ? esc(money(mod.price_cents, mod.currency)) : 'Membership'}</span>` : ''}
                    ${mod.uses_injector ? `<span class="pill">${icon('lock')}Injector</span>` : ''}
                    ${caps.map(c => `<span class="pill">${esc(capLabels[c])}</span>`).join('')}
                </div>
                <p class="hint"><span class="mono">GUID ${esc(mod.guid)}</span> ${guidPill(mod.guid)} · created ${esc(date(mod.created_at))} · updated ${esc(ago(mod.updated_at))}</p>
            </div>
            <div class="hero-actions"><a class="button subtle small" href="${esc(site('/view/' + encodeURIComponent(mod.guid)))}" target="_blank" rel="noopener">${icon('external')}Open on site</a>
                ${mod.source_url ? `<a class="button subtle small" href="${esc(mod.source_url)}" target="_blank" rel="noopener noreferrer">${icon('github')}Source</a>` : '<span class="pill warn">No source link</span>'}</div>
        </section>
        ${mod.screenshots?.length ? `<div class="review-shots">${mod.screenshots.map(s => `<a href="${esc(s)}" target="_blank" rel="noopener"><img src="${esc(s)}" alt="" loading="lazy"></a>`).join('')}</div>` : '<p class="notice">No screenshots.</p>'}
        <article class="surface panel"><div class="review-label">${icon('note')}Description</div><div class="markdown">${mod.description_html || '<p class="hint">No description.</p>'}</div></article>
        ${mod.links?.length ? `<section class="surface panel"><div class="review-label">${icon('link')}Links</div><div class="link-list">${mod.links.map(l => `<a href="${esc(l.url)}" target="_blank" rel="noopener noreferrer">${icon('link')}${esc(l.label)} <span class="hint mono">${esc(l.url)}</span>${icon('external')}</a>`).join('')}</div></section>` : ''}
        <section class="surface panel"><div class="review-label">${icon('layers')}Releases on ${esc(mod.channel?.name || 'stable')} · ${(mod.releases || []).length}</div>
            ${releases.map(r => `<div class="review-release"><div class="row"><strong class="mono">v${esc(r.version)}</strong>${r.version === mod.current_version ? '<span class="pill accent">Current</span>' : ''}<span class="spacer"></span><span class="hint">${esc(date(r.released_at))}</span></div>
                ${r.notes ? `<p class="hint">${esc(r.notes)}</p>` : ''}
                <div class="files">${r.downloads.map(f => f.url ? `<a class="pill mono" href="${esc(f.url)}" rel="nofollow">${icon('download')}${esc(f.filename)} · ${size(f.size)} · ${esc(loaderLabels[f.loader] || f.loader)}</a>` : `<span class="pill mono">${icon('lock')}${esc(f.filename)} · sealed</span>`).join('')}</div>
                ${r.downloads.map(f => `<div class="hint mono">sha256 ${esc(f.sha256)}</div>`).join('')}</div>`).join('') || '<p class="hint">No releases.</p>'}
            ${(mod.channels || []).length > 1 ? `<p class="hint">Other channels: ${mod.channels.filter(c => c.name !== mod.channel?.name).map(c => `<span class="pill mono">${esc(c.name)}</span>`).join(' ')}</p>` : ''}
        </section>
        ${(mod.tiers || []).length ? `<section class="surface panel"><div class="review-label">${icon('tag')}Membership tiers</div>${mod.tiers.map(t => `<div class="row"><strong>${esc(t.name)}</strong><span class="spacer"></span><span class="mono">${esc(money(t.price_cents, t.currency || mod.currency))}/mo</span></div>`).join('')}</section>` : ''}`;
}

function shieldPanel(entry) {
    const report = entry.report;
    const s = shieldState(report);
    const checks = (report.artifacts || []).flatMap(a => (a.report.checks || []).map(c => ({ ...c, artifact: a.name })));
    const failing = checks.filter(c => c.status === 'Fail');
    const unevaluated = checks.filter(c => !['Pass', 'Fail', 'Inconclusive'].includes(c.status));

    return `<section class="surface panel">
        <div class="review-label">${icon('shield')}MonkeShield · <span class="mono">${esc(entry.channel)} v${esc(entry.version)}</span></div>
        <div class="shield-box">${shieldRing(report)}<div>
            <h3>${esc(s.label)}</h3>
            <p class="hint mono">${s.measured ? `${report.failed_checks} failed · ${report.total_checks - report.failed_checks} passed` : esc(report.status || 'not scanned')}</p>
            <p class="hint">${(report.artifacts || []).length} assemblies${unevaluated.length ? ` · ${unevaluated.length} not evaluated` : ''}</p>
            <a class="button ghost small" href="${esc(site(`/view/${encodeURIComponent(entry.guid)}/monkeshield?channel=${encodeURIComponent(entry.channel)}`))}" target="_blank" rel="noopener">Full report${icon('external')}</a>
        </div></div>
        ${(report.diagnostics || []).map(d => `<p class="notice">${esc(d)}</p>`).join('')}
        ${failing.length ? `<div class="shield-checks">${failing.map(c => `<div class="shield-check fail"><strong>${esc(c.title)}</strong><small>${esc(c.ruleId)} · ${esc(c.category)} · ${esc(c.artifact)}</small><p>${esc(c.explanation)}</p>${(c.evidence || []).slice(0, 4).map(e => `<p><code>${esc(e.location)}</code> ${esc(e.detail)}</p>`).join('')}</div>`).join('')}</div>` : s.complete ? '<p class="hint">No failing checks.</p>' : ''}
    </section>`;
}

async function review({ app, shell, alive }, guid) {
    shell.classList.add('fullscreen');
    const data = await api('/api/v1/admin/reviews/' + encodeURIComponent(guid));

    if (!alive())
        return;

    const { mod, author, queue: q } = data;
    const stable = data.shield.find(r => r.channel === (mod.channel?.name || 'stable')) || data.shield[0];
    const decided = mod.status !== 'pending';
    const after = q.next && q.next !== guid ? q.next : null;

    app.innerHTML = `<div class="review">
        <div class="review-bar">
            <a class="button ghost small" href="/admin/reviews">${icon('back')}Queue</a>
            ${modIcon(mod)}
            <h1>${esc(mod.name)}</h1>
            <span class="pill ${mod.status === 'approved' ? 'good' : mod.status === 'pending' ? 'warn' : 'bad'}">${esc(mod.status === 'unapproved' ? 'denied' : mod.status)}</span>
            <span class="spacer"></span>
            ${q.position ? `<span class="position">${q.position} of ${q.total}</span>` : `<span class="position">${q.total} waiting</span>`}
            <a class="button ghost small" ${q.previous ? `href="/admin/reviews/${encodeURIComponent(q.previous)}"` : 'aria-disabled="true"'} title="Previous (K)">${icon('back')}</a>
            <a class="button ghost small" ${after ? `href="/admin/reviews/${encodeURIComponent(after)}"` : 'aria-disabled="true"'} title="Next (J)">${icon('next')}</a>
        </div>
        <div class="review-body">
            <div class="review-page">${decided ? `<p class="notice">This mod is <strong>${esc(mod.status === 'unapproved' ? 'denied' : mod.status)}</strong>. You can still change the decision below.</p>` : ''}${modPage(mod)}</div>
            <aside class="review-side">
                <section class="surface panel">
                    <div class="review-label">${icon('user')}Submitted by</div>
                    <div class="author-card">${avatar(author)}<div>
                        <h2>${esc(author.name)} ${userBadges(author)}</h2>
                        <div class="handle">@${esc(author.username)}${author.discord_username ? ` · Discord ${esc(author.discord_username)}` : ''}</div>
                    </div></div>
                    <dl class="details-list">
                        <div><dt>User ID</dt><dd class="mono">${esc(author.id)}</dd></div>
                        <div><dt>Account</dt><dd>${esc(author.is_group ? 'Publishing group' : author.provider === 'email' ? 'Email' : 'Discord')}${author.created_at ? ` · joined ${esc(date(author.created_at))}` : ''}</dd></div>
                        ${author.members ? `<div><dt>Members</dt><dd>${author.members.map(m => esc(m.name)).join(', ') || '—'}</dd></div>` : ''}
                        <div><dt>History</dt><dd>${author.mods.length} other mod${author.mods.length === 1 ? '' : 's'} · ${author.denials} denial${author.denials === 1 ? '' : 's'}</dd></div>
                        ${author.stripe_account_id ? `<div><dt>Stripe</dt><dd class="mono">${esc(author.stripe_account_id)}</dd></div>` : ''}
                        ${author.is_banned ? '<div><dt>Status</dt><dd><span class="pill bad">Banned</span></dd></div>' : ''}
                    </dl>
                    <div class="row"><a class="button ghost small" href="/admin/users?q=${encodeURIComponent(author.id)}">${icon('users')}Manage user</a><a class="button ghost small" href="${esc(site(author.profile_url))}" target="_blank" rel="noopener">${icon('external')}Profile</a></div>
                    ${author.mods.length ? `<div class="mini-list drawer-section">${author.mods.slice(0, 6).map(m => `<a href="/admin/reviews/${encodeURIComponent(m.guid)}">${modIcon(m)}${esc(m.name)}<span class="spacer"></span><span class="pill">${esc(m.status)}</span></a>`).join('')}</div>` : ''}
                </section>
                ${stable ? shieldPanel({ ...stable, guid: mod.guid }) : `<section class="surface panel"><div class="review-label">${icon('shield')}MonkeShield</div><p class="hint">No scan report yet.</p></section>`}
                ${data.shield.filter(r => r !== stable).map(r => shieldPanel({ ...r, guid: mod.guid })).join('')}
                <section class="surface panel">
                    <div class="review-label">${icon('history')}Review history</div>
                    <div class="timeline">${data.history.map(h => `<div class="timeline-item ${esc(h.decision)}"><strong>${esc(h.decision === 'resubmitted' ? 'Resubmitted by author' : h.decision === 'approved' ? 'Approved' : 'Denied')}</strong> <span class="hint">by ${esc(h.reviewer.name)} · v${esc(h.version)}</span><br><time>${esc(when(h.created_at))}</time>${h.reason ? `<p>${esc(h.reason)}</p>` : ''}</div>`).join('') || '<p class="hint">First review.</p>'}</div>
                </section>
                <section class="surface panel">
                    <div class="review-label">${icon('note')}Staff notes</div>
                    <div class="timeline">${data.notes.map(n => `<div class="timeline-item"><strong>${esc(n.name)}</strong> <time>${esc(when(n.created_at))}</time><p>${esc(n.body)}</p></div>`).join('') || '<p class="hint">Only staff see these.</p>'}</div>
                    <form class="stack drawer-section" id="note-form"><textarea name="body" rows="2" required maxlength="5000" placeholder="Add a note for other admins…" aria-label="Staff note"></textarea><div class="row"><span class="spacer"></span><button class="button subtle small">Add note</button></div></form>
                </section>
            </aside>
        </div>
        <form class="decision-bar" id="decision">
            <div>
                <div class="chips">${quickReasons.map(([label], i) => `<button type="button" class="chip" data-reason="${i}">${esc(label)}</button>`).join('')}</div>
                <textarea id="reason" maxlength="2000" rows="2" placeholder="Reason for the author. Required to deny; optional note when approving." aria-label="Reason"></textarea>
            </div>
            <div class="stack">
                <div class="decision-actions">
                    <button type="button" class="button danger large" data-decide="deny">${icon('x')}Deny</button>
                    <button type="button" class="button approve large" data-decide="approve">${icon('check')}Approve</button>
                </div>
                <span class="kbd-hint"><kbd>A</kbd> approve · <kbd>D</kbd> deny · <kbd>J</kbd>/<kbd>K</kbd> next/prev</span>
            </div>
        </form>
    </div>`;

    const reason = app.querySelector('#reason');

    app.querySelectorAll('[data-reason]').forEach(chip => chip.onclick = () => {
        const text = quickReasons[Number(chip.dataset.reason)][1];
        reason.value = reason.value.trim() ? reason.value.trim() + '\n\n' + text : text;
        reason.focus();
    });

    app.querySelector('#note-form').onsubmit = async event => {
        event.preventDefault();

        try {
            await api('/api/v1/admin/mods/' + encodeURIComponent(mod.guid) + '/comments', { method: 'POST', json: { body: event.target.body.value } });
            navigate(location.pathname, false);
        } catch (error) {
            toast(error.message, 'error');
        }
    };

    const decide = async decision => {
        const text = reason.value.trim();

        if (decision === 'deny' && text.length < 5) {
            toast('Write a reason so the author knows what to fix.', 'error');
            reason.focus();
            return;
        }

        if (decision === 'deny' && data.live_subscriptions && !await confirmDialog('Deny this mod?', `It has ${data.live_subscriptions} live subscription(s). Denying ends them now and subscribers stop being billed.`, 'Deny and cancel subscriptions', true))
            return;

        app.querySelectorAll('[data-decide]').forEach(b => b.disabled = true);

        try {
            const result = await api('/api/v1/admin/reviews/' + encodeURIComponent(mod.guid), { method: 'POST', json: { decision, reason: text } });
            toast(decision === 'approve' ? `${mod.name} approved.` : `${mod.name} denied. The author has been notified.`);

            if (result.subscriptions_failed)
                toast(`${result.subscriptions_failed} subscription(s) couldn’t be cancelled. Cancel them in Stripe.`, 'error');

            counters.refresh();
            navigate(after ? '/admin/reviews/' + encodeURIComponent(after) : '/admin/reviews');
        } catch (error) {
            toast(error.message, 'error');
            app.querySelectorAll('[data-decide]').forEach(b => b.disabled = false);
        }
    };

    app.querySelectorAll('[data-decide]').forEach(b => b.onclick = () => decide(b.dataset.decide));

    const keys = event => {
        if (!app.querySelector('.review')) {
            document.removeEventListener('keydown', keys);
            return;
        }

        if (event.target.closest('input, textarea, select') || event.metaKey || event.ctrlKey || event.altKey || document.querySelector('.modal'))
            return;

        if (event.key === 'a')
            confirmDialog(`Approve ${mod.name}?`, 'It goes live straight away and the author is notified.', 'Approve').then(ok => ok && decide('approve'));
        else if (event.key === 'd') {
            event.preventDefault();
            reason.focus();
        } else if (event.key === 'j' && after)
            navigate('/admin/reviews/' + encodeURIComponent(after));
        else if (event.key === 'k' && q.previous)
            navigate('/admin/reviews/' + encodeURIComponent(q.previous));
    };

    document.addEventListener('keydown', keys);
}
