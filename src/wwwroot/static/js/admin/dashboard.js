import { ago, api, auth, esc, icon, number } from '../core.js';
import { boot, head, when } from './ui.js';

export async function render({ app, alive }) {
    const data = await api('/api/v1/admin/dashboard');

    if (!alive())
        return;

    const kpi = (href, glyph, label, value, note = '', attention = false) => `<a class="kpi ${attention ? 'attention' : ''}" href="${href}">
        <span>${icon(glyph)}${esc(label)}</span><strong>${number(value)}</strong>${note ? `<small>${note}</small>` : ''}</a>`;
    const support = data.support;
    const inboxState = !support.configured ? ['bad', 'Not configured'] : support.last_error ? ['bad', 'Can’t reach the mailbox'] : support.last_check ? ['good', 'Connected'] : ['warn', 'Starting…'];

    app.innerHTML = `
        ${head(`Welcome back, ${auth.user.display_name}`, 'What needs attention across the site right now.')}
        <div class="kpis">
            ${kpi('/admin/reviews', 'shield', 'Mods awaiting review', data.reviews.pending, data.reviews.oldest ? `Oldest waiting ${esc(ago(data.reviews.oldest))}` : 'Queue is clear', data.reviews.pending > 0)}
            ${kpi('/admin/tickets', 'mail', 'Open tickets', data.tickets.open, `${number(data.tickets.unread)} unread · ${number(data.tickets.unassigned)} unassigned`, data.tickets.unread > 0)}
            ${kpi('/admin/tickets?folder=mine', 'user', 'Assigned to you', data.tickets.mine, `${number(data.tickets.pending)} waiting on customers`)}
            ${kpi('/admin/disputes', 'flag', 'Open disputes', data.disputes_open, 'Buyer refund requests', data.disputes_open > 0)}
            ${kpi('/admin/users', 'users', 'Users', data.users.total, `+${number(data.users.new_week)} this week · ${number(data.users.banned)} banned`)}
            ${kpi('/admin/analytics', 'download', 'Downloads (7 days)', data.downloads_week)}
            ${kpi('/admin/mail', 'send', 'Mail queue', data.mail.queued, `${number(data.mail.failed_week)} failed this week`, data.mail.failed_week > 0)}
            ${kpi('/admin/errors', 'terminal', 'Server errors (24h)', data.errors_day, '', data.errors_day > 0)}
        </div>
        <div class="dash-grid">
            <section class="surface panel">
                <div class="row"><h2>${icon('history')} Recent staff activity</h2><span class="spacer"></span><a class="button ghost small" href="/admin/audit">Full audit log</a></div>
                <div class="audit-list">${data.audit.map(a => `<div class="audit-item"><div><strong>${esc(a.actor.name)}</strong> <code>${esc(a.action)}</code> <span class="mono hint">${esc(a.target)}</span></div><time title="${esc(a.created_at)}">${esc(ago(a.created_at))}</time></div>`).join('') || '<p class="hint">Nothing yet.</p>'}</div>
            </section>
            <section class="stack">
                <div class="surface panel">
                    <h2>${icon('mail')} Support mailbox</h2>
                    <dl class="details-list">
                        <div><dt>Address</dt><dd class="mono">${esc(support.address || 'Not set')}</dd></div>
                        <div><dt>Status</dt><dd><span class="status-dot ${inboxState[0]}"></span> ${esc(inboxState[1])}</dd></div>
                        <div><dt>Last check</dt><dd>${support.last_check ? esc(when(support.last_check)) + ' · ' + esc(ago(support.last_check)) : '—'}</dd></div>
                        ${support.last_error ? `<div><dt>Error</dt><dd class="error-text">${esc(support.last_error)}</dd></div>` : ''}
                    </dl>
                    ${support.configured ? '' : '<p class="hint">Set SUPPORT_EMAIL, SUPPORT_IMAP_HOST and SUPPORT_MAIL_PASSWORD in the server’s .env (Purelymail: imap.purelymail.com / smtp.purelymail.com), then restart.</p>'}
                </div>
                <div class="surface panel">
                    <h2>${icon('server')} This panel</h2>
                    <dl class="details-list">
                        <div><dt>Admin URL</dt><dd class="mono">${esc(boot.admin_url || location.origin)}</dd></div>
                        <div><dt>Public site</dt><dd class="mono">${esc(boot.public_url || '')}</dd></div>
                        <div><dt>Outgoing mail</dt><dd>${boot.mail_configured ? '<span class="status-dot good"></span> Configured' : '<span class="status-dot bad"></span> Not configured'}</dd></div>
                        <div><dt>Payments</dt><dd>${boot.stripe_configured ? '<span class="status-dot good"></span> Stripe connected' : '<span class="status-dot"></span> Off'}</dd></div>
                        <div><dt>Reviews</dt><dd>${number(data.reviews.decided_week)} decisions this week</dd></div>
                    </dl>
                </div>
            </section>
        </div>`;
}
