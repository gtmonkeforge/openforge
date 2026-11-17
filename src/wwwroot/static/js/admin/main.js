import { api, applyAccent, auth, avatar, brand, esc, icon, readAccent, toast } from '../core.js';
import { boot, counters, publicUrl, site } from './ui.js';

const sections = [
    { label: 'Overview' },
    { path: '/admin', title: 'Dashboard', icon: 'dashboard', load: () => import('./dashboard.js') },
    { label: 'Moderation' },
    { path: '/admin/reviews', title: 'Reviews', icon: 'shield', badge: 'reviews', load: () => import('./reviews.js') },
    { path: '/admin/mods', title: 'All mods', icon: 'grid', load: () => import('./mods.js') },
    { path: '/admin/disputes', title: 'Disputes', icon: 'flag', badge: 'disputes', load: () => import('./ops.js'), view: 'disputes' },
    { label: 'Support' },
    { path: '/admin/tickets', title: 'Tickets', icon: 'mail', badge: 'tickets', load: () => import('./tickets.js') },
    { path: '/admin/users', title: 'Users', icon: 'users', load: () => import('./users.js') },
    { path: '/admin/announcements', title: 'Announcements', icon: 'megaphone', load: () => import('./announcements.js') },
    { label: 'Operations' },
    { path: '/admin/analytics', title: 'Analytics', icon: 'bolt', load: () => import('./ops.js'), view: 'analytics' },
    { path: '/admin/mail', title: 'Mail queue', icon: 'send', load: () => import('./ops.js'), view: 'mail' },
    { path: '/admin/audit', title: 'Audit log', icon: 'history', load: () => import('./ops.js'), view: 'audit' },
    { path: '/admin/errors', title: 'Server errors', icon: 'terminal', load: () => import('./ops.js'), view: 'errors' },
    { path: '/admin/admins', title: 'Administrators', icon: 'admin', load: () => import('./ops.js'), view: 'admins', root: true },
];

const side = document.querySelector('#admin-side');
const shell = document.querySelector('#admin-shell');
const app = document.querySelector('#app');

function signInScreen(reason) {
    shell.classList.add('fullscreen');
    const message = reason === 'expired' ? 'That sign-in link expired. Open the panel from the site again.'
        : reason === 'denied' ? 'That account can’t open the admin panel.'
            : auth.user ? 'This account doesn’t have administrator access.' : `Sign in on ${esc(brand.name)} and open the admin panel from your account menu. You’ll be signed in here automatically.`;

    app.innerHTML = `<section class="signin-card surface"><span class="brand-mark" aria-hidden="true"></span><h1>${esc(brand.name)} admin</h1><p>${message}</p>
        <div class="signin-actions"><a class="button large" href="${esc(site('/admin'))}">${icon('user')}Continue from ${esc(brand.name)}</a></div></section>`;
}

function renderSide(path) {
    const visible = sections.filter(s => !s.root || auth.user.is_root);
    side.innerHTML = `<a class="admin-brand" href="/admin"><span class="brand-mark" aria-hidden="true"></span>${esc(brand.name)}<small>Admin</small></a>
        <nav class="admin-nav">${visible.map(s => s.label ? `<div class="label">${esc(s.label)}</div>`
            : `<a href="${s.path}" ${current(path)?.path === s.path ? 'aria-current="page"' : ''}>${icon(s.icon)}${esc(s.title)}${s.badge ? `<span class="badge" data-badge="${s.badge}" hidden></span>` : ''}</a>`).join('')}</nav>
        <div class="admin-side-foot">
            <div class="admin-me">${avatar(auth.user, 'sm')}<div><strong>${esc(auth.user.display_name)}</strong><small>${auth.user.is_root ? 'root' : 'admin'} · @${esc(auth.user.username)}</small></div></div>
            <a href="${esc(publicUrl)}" target="_blank" rel="noopener">${icon('external')}Open ${esc(brand.name)}</a>
            <button type="button" id="admin-logout">${icon('logout')}Sign out</button>
        </div>`;

    side.querySelector('#admin-logout').onclick = async () => {
        await api('/api/v1/logout', { method: 'POST' }).catch(() => {});
        location.href = '/admin';
    };
}

function current(path) {
    return sections.filter(s => s.path).sort((a, b) => b.path.length - a.path.length).find(s => path === s.path || path.startsWith(s.path + '/'));
}

async function refreshCounters() {
    try {
        const data = await api('/api/v1/admin/dashboard');
        const set = (key, value, hot) => side.querySelectorAll(`[data-badge="${key}"]`).forEach(b => {
            b.hidden = !value;
            b.textContent = value > 99 ? '99+' : value;
            b.classList.toggle('hot', !!hot);
        });

        set('reviews', data.reviews.pending, data.reviews.pending > 0);
        set('tickets', data.tickets.open, data.tickets.unread > 0);
        set('disputes', data.disputes_open, data.disputes_open > 0);
        document.title = `${data.tickets.unread ? `(${data.tickets.unread}) ` : ''}Admin · ${brand.name}`;
    } catch {
    }
}

let navigating = 0;

export async function navigate(path, push = true) {
    const ticket = ++navigating;

    if (push && path !== location.pathname + location.search)
        history.pushState(null, '', path);

    const url = new URL(path, location.origin);
    const section = current(url.pathname) || sections[1];

    if (section.root && !auth.user.is_root) {
        app.innerHTML = '<div class="empty"><h2>Root only</h2><p>Only the root administrator can manage administrators.</p></div>';
        return;
    }

    shell.classList.remove('fullscreen');
    side.classList.remove('open');
    renderSide(url.pathname);
    refreshCounters();
    document.querySelectorAll('.drawer, .modal, .popover, .kebab-menu').forEach(el => el.remove());

    try {
        const page = await section.load();

        if (ticket !== navigating)
            return;

        const rest = url.pathname.slice(section.path.length).replace(/^\//, '');
        await page.render({ app, shell, view: section.view, rest: rest ? decodeURIComponent(rest) : '', params: url.searchParams, alive: () => ticket === navigating });
        app.focus({ preventScroll: true });
    } catch (error) {
        if (ticket === navigating)
            app.innerHTML = `<div class="error">${esc(error.message)}</div>`;
    }
}

// Links inside the panel stay in the panel without a full reload; anything else (the public site, downloads) opens normally.
document.addEventListener('click', event => {
    const menu = event.target.closest('[data-menu]');

    if (menu) {
        side.classList.toggle('open');
        return;
    }

    const link = event.target.closest('a[href]');

    if (!link || link.target || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
        return;

    const url = new URL(link.href, location.origin);

    if (url.origin !== location.origin || !url.pathname.startsWith('/admin'))
        return;

    event.preventDefault();
    navigate(url.pathname + url.search);
});

addEventListener('popstate', () => navigate(location.pathname + location.search, false));
counters.refresh = refreshCounters;

async function start() {
    applyAccent(readAccent());

    try {
        Object.assign(auth, await api('/api/v1/session'));
    } catch {
    }

    if (auth.user?.accent_color)
        applyAccent(auth.user.accent_color);

    const reason = new URLSearchParams(location.search).get('signin');

    if (!auth.user?.is_admin) {
        signInScreen(reason);
        return;
    }

    if (reason)
        history.replaceState(null, '', '/admin');

    if (!boot.support_configured && location.pathname.startsWith('/admin/tickets'))
        toast('The support mailbox isn’t configured, so tickets won’t arrive by email yet.', 'error');

    await navigate(location.pathname + location.search, false);
    setInterval(refreshCounters, 60000);
}

start();
