import { ago, api, canManageMod, app, auth, avatar, bindCopy, capLabels, capTips, date, esc, icon, loaderLabels, modBadges, modIcon, number, setTitle, signInUrl, size, toast, userBadges } from '../core.js';

export async function render(id) {
    const channelName = new URLSearchParams(location.search).get('channel') || 'stable';
    const mod = await api('/api/v1/mods/' + encodeURIComponent(id) + '?channel=' + encodeURIComponent(channelName));
    const owner = await canManageMod(mod.author);
    const current = mod.releases.find(r => r.version === mod.current_version) || mod.releases[0];
    const base = '/api/v1/mods/' + encodeURIComponent(mod.guid);
    const shieldUrl = `/view/${encodeURIComponent(mod.guid)}/monkeshield?channel=${encodeURIComponent(mod.channel.name)}`;

    setTitle(mod.name);
    document.querySelector('.mod-backdrop')?.remove();

    if (mod.banner || mod.logo)
        document.body.insertAdjacentHTML('afterbegin', `<div class="mod-backdrop" aria-hidden="true"><img src="${esc(mod.banner || mod.logo)}" alt=""></div>`);

    const capabilities = Object.keys(capLabels).filter(key => mod.capabilities[key]);
    const loaders = mod.loaders?.length ? mod.loaders : ['bepinex5'];

    app.innerHTML = `
        <a class="breadcrumb" href="/browse">${icon('back')}All mods</a>
        ${mod.status !== 'approved' ? `<p class="notice">${mod.status === 'pending' ? 'This mod is awaiting review before it becomes publicly available.' : 'This mod is not publicly available.'}</p>` : ''}
        ${mod.banner ? `<div class="mod-banner"><img src="${esc(mod.banner)}" alt=""></div>` : ''}
        <section class="mod-hero surface">
            ${modIcon(mod)}
            <div>
                <h1>${esc(mod.name)}</h1>
                <div class="meta-line">
                    <a class="author-chip" href="${esc(mod.author.profile_url)}">${avatar(mod.author, 'sm')}${esc(mod.author.name)}</a>${userBadges(mod.author, true)}
                    <span class="pill">${esc(mod.category)}</span>
                    ${mod.channel.name !== 'stable' ? `<span class="pill accent mono">${esc(mod.channel.name)}</span>` : ''}
                    ${mod.visibility !== 'public' ? `<span class="pill">${mod.visibility === 'private' ? 'Private' : 'Code-only'}</span>` : ''}
                </div>
                ${mod.badges?.length ? `<div class="badges">${modBadges(mod)}</div>` : ''}
            </div>
            <div class="hero-actions">
                ${current ? `<a class="button large" href="#download" id="hero-download">${icon('download')}Download${loaders.length === 1 ? ' · ' + esc(loaderLabels[loaders[0]]) : ''}</a>` : '<span class="pill">No releases yet</span>'}
                <div class="row">
                    ${owner ? `<a class="button subtle small" href="/upload?guid=${encodeURIComponent(mod.guid)}&channel=${encodeURIComponent(mod.channel.name)}">${icon('edit')}Manage</a>` : ''}
                    <button class="button subtle small" data-copy="${esc(location.origin + location.pathname + location.search)}" data-copy-label="Link copied.">${icon('link')}Share</button>
                    ${mod.source_url ? `<a class="button subtle small" href="${esc(mod.source_url)}" target="_blank" rel="noopener noreferrer">${icon('github')}Source</a>` : ''}
                </div>
            </div>
        </section>

        <div class="stat-strip">
            <div><span>Downloads</span><strong>${number(mod.downloads)}</strong></div>
            <div><span>Rating</span><strong id="stat-rating">—</strong></div>
            <div><span>Version</span><strong>${esc(mod.current_version || '—')}</strong></div>
            <div><span>Updated</span><strong>${current ? esc(ago(current.released_at)) : '—'}</strong></div>
            <div><span>Loaders</span><strong>${loaders.length}</strong></div>
        </div>

        <div class="mod-layout">
            <div>
                <div class="tabs" role="tablist" aria-label="Mod sections">
                    <button role="tab" id="tab-overview" aria-controls="panel-overview" data-tab="overview">Overview</button>
                    <button role="tab" id="tab-versions" aria-controls="panel-versions" data-tab="versions">Versions<span class="count">${mod.releases.length}</span></button>
                    <button role="tab" id="tab-community" aria-controls="panel-community" data-tab="community">Community<span class="count" id="comment-count"></span></button>
                    <button role="tab" id="tab-security" aria-controls="panel-security" data-tab="security">Security</button>
                </div>
                <section class="tab-panel" role="tabpanel" id="panel-overview" aria-labelledby="tab-overview">
                    ${gallery(mod)}
                    ${mod.channel.notes ? `<div class="surface panel"><h2>${esc(mod.channel.name)} channel notes</h2><p class="hint">${esc(mod.channel.notes)}</p></div>` : ''}
                    <article class="surface panel"><div class="markdown">${mod.description_html}</div></article>
                </section>
                <section class="tab-panel" role="tabpanel" id="panel-versions" aria-labelledby="tab-versions" hidden>${versions(mod)}</section>
                <section class="tab-panel" role="tabpanel" id="panel-community" aria-labelledby="tab-community" hidden><div class="surface panel" id="community"><p class="hint">Loading…</p></div></section>
                <section class="tab-panel" role="tabpanel" id="panel-security" aria-labelledby="tab-security" hidden><div class="surface panel" id="security"><p class="hint">Loading analysis…</p></div></section>
            </div>
            <aside class="mod-sidebar" aria-label="Downloads and details">
                <section class="surface download-card" id="download">
                    <div class="version"><div><span class="hint">Latest${mod.channel.name !== 'stable' ? ' · ' + esc(mod.channel.name) : ''}</span><br><strong>${current ? 'v' + esc(current.version) : 'No release'}</strong></div>${current ? `<span class="hint">${esc(date(current.released_at))}</span>` : ''}</div>
                    ${current ? downloads(current) : '<p class="hint">Nothing to download in this channel yet.</p>'}
                    ${mod.channels.length > 1 ? `<div class="field"><label for="channel-switch">Release channel</label><select id="channel-switch">${mod.channels.map(c => `<option value="${esc(c.name)}" ${c.name === mod.channel.name ? 'selected' : ''}>${esc(c.name)}${c.public ? '' : ' · private'}${c.enabled ? '' : ' · disabled'}</option>`).join('')}</select></div>` : ''}
                    ${current?.notes ? `<details class="hint"><summary>What’s new</summary><p class="notes">${esc(current.notes)}</p></details>` : ''}
                </section>
                <section class="surface panel shield-summary" id="shield-mini"><h2>${icon('shield')} MonkeShield</h2><p class="hint">Loading…</p></section>
                <section class="surface panel">
                    <h2>Details</h2>
                    <dl class="details-list">
                        <div><dt>GUID</dt><dd><button class="copy-inline" data-copy="${esc(mod.guid)}" data-copy-label="GUID copied.">${esc(mod.guid)}${icon('copy')}</button></dd></div>
                        ${mod.id !== mod.guid ? `<div><dt>Custom ID</dt><dd class="mono">${esc(mod.id)}</dd></div>` : ''}
                        <div><dt>Loaders</dt><dd>${loaders.map(l => esc(loaderLabels[l])).join(', ')}</dd></div>
                        <div><dt>Discord</dt><dd class="mono">@${esc(mod.author.discord_username)}</dd></div>
                        <div><dt>Published</dt><dd>${esc(date(mod.created_at))}</dd></div>
                        ${capabilities.length ? `<div><dt>Capabilities</dt><dd class="capabilities">${capabilities.map(key => `<span class="pill accent" title="${esc(capTips[key])}">${capLabels[key]}</span>`).join('')}</dd></div>` : ''}
                    </dl>
                </section>
                ${mod.links?.length || mod.source_url ? `<section class="surface panel"><h2>Links</h2><div class="link-list">
                    ${mod.source_url ? `<a href="${esc(mod.source_url)}" target="_blank" rel="noopener noreferrer">${icon('code')}Source code${icon('external')}</a>` : ''}
                    ${(mod.links || []).map(l => `<a href="${esc(l.url)}" target="_blank" rel="noopener noreferrer">${icon('link')}${esc(l.label)}${icon('external')}</a>`).join('')}
                </div></section>` : ''}
            </aside>
        </div>`;

    app.querySelectorAll('.markdown a').forEach(a => {
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
    });
    app.querySelector('#channel-switch')?.addEventListener('change', event => {
        const url = new URL(location.href);
        url.searchParams.set('channel', event.target.value);
        location.assign(url.pathname + url.search);
    });
    app.querySelector('#hero-download')?.addEventListener('click', event => {
        event.preventDefault();
        const card = app.querySelector('#download');
        card.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });

    bindCopy(app);
    bindTabs();
    bindGallery(mod);
    loadShield(mod, shieldUrl);
    community(mod, base);
}

function bindTabs() {
    const tabs = [...app.querySelectorAll('[data-tab]')];

    const select = (name, focus = false) => {
        tabs.forEach(tab => {
            const active = tab.dataset.tab === name;
            tab.setAttribute('aria-selected', String(active));
            tab.tabIndex = active ? 0 : -1;
            app.querySelector('#panel-' + tab.dataset.tab).hidden = !active;

            if (active && focus)
                tab.focus();
        });
        history.replaceState(null, '', name === 'overview' ? location.pathname + location.search : '#' + name);
    };

    tabs.forEach((tab, index) => {
        tab.onclick = () => select(tab.dataset.tab);
        tab.onkeydown = event => {
            if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft')
                return;

            const next = tabs[(index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length];
            select(next.dataset.tab, true);
        };
    });

    const initial = location.hash.slice(1);
    select(tabs.some(t => t.dataset.tab === initial) ? initial : 'overview');
}

function gallery(mod) {
    if (!mod.screenshots.length)
        return '';

    const multiple = mod.screenshots.length > 1;

    return `<section class="surface gallery" aria-label="Screenshots" tabindex="0">
        <div class="gallery-stage">
            <img id="gallery-image" src="${esc(mod.screenshots[0])}" alt="${esc(mod.name)} screenshot 1">
            ${multiple ? `<button class="icon-button nav prev" id="gallery-prev" aria-label="Previous screenshot">${icon('back')}</button><button class="icon-button nav next" id="gallery-next" aria-label="Next screenshot">${icon('next')}</button>` : ''}
        </div>
        ${multiple ? `<div class="thumbs">${mod.screenshots.map((s, i) => `<button data-shot="${i}" aria-label="Screenshot ${i + 1}" ${i === 0 ? 'aria-current="true"' : ''}><img src="${esc(s)}" alt="" loading="lazy"></button>`).join('')}</div>` : ''}
    </section>`;
}

function bindGallery(mod) {
    const image = app.querySelector('#gallery-image');

    if (!image)
        return;

    let index = 0;

    const show = next => {
        index = (next + mod.screenshots.length) % mod.screenshots.length;
        image.animate([{ opacity: .3 }, { opacity: 1 }], { duration: 220 });
        image.src = mod.screenshots[index];
        image.alt = `${mod.name} screenshot ${index + 1}`;
        app.querySelectorAll('[data-shot]').forEach(b => b.setAttribute('aria-current', String(Number(b.dataset.shot) === index)));
    };

    app.querySelector('#gallery-prev')?.addEventListener('click', () => show(index - 1));
    app.querySelector('#gallery-next')?.addEventListener('click', () => show(index + 1));
    app.querySelectorAll('[data-shot]').forEach(b => b.onclick = () => show(Number(b.dataset.shot)));
    app.querySelector('.gallery').addEventListener('keydown', event => {
        if (event.key === 'ArrowLeft')
            show(index - 1);

        if (event.key === 'ArrowRight')
            show(index + 1);
    });

    image.onclick = () => {
        const box = document.createElement('div');
        box.className = 'lightbox';
        box.innerHTML = `<img src="${esc(mod.screenshots[index])}" alt="${esc(image.alt)}">`;
        box.onclick = () => box.remove();
        document.addEventListener('keydown', function close(event) {
            if (event.key === 'Escape') {
                box.remove();
                document.removeEventListener('keydown', close);
            }
        });
        document.body.append(box);
    };
}

function downloads(release) {
    return Object.entries(loaderLabels).map(([loader, label]) => {
        const files = release.downloads.filter(f => (f.loader || 'bepinex5') === loader);

        if (!files.length)
            return '';

        return `<div class="loader-group"><span>${esc(label)}</span>${files.map(f => `<a class="file-link" href="${esc(f.url)}">
            <span class="kind">${esc(f.kind.toUpperCase())}</span>
            <span class="name">${esc(f.filename)}<small>${size(f.size)}</small></span>${icon('download')}</a>`).join('')}</div>`;
    }).join('') + `<details class="hint"><summary>SHA-256 checksums</summary>${release.downloads.map(f => `<p>${esc(f.filename)}<code class="checksum">${esc(f.sha256)}</code></p>`).join('')}</details>`;
}

function versions(mod) {
    if (!mod.releases.length)
        return '<div class="empty"><h2>No releases yet</h2><p>Releases published to this channel will appear here.</p></div>';

    return `<div class="timeline">${mod.releases.map(r => `<article class="surface release">
        <header><strong>v${esc(r.version)}</strong>${r.version === mod.current_version ? '<span class="pill good">Latest</span>' : ''}${(r.loaders || []).map(l => `<span class="pill">${esc(loaderLabels[l])}</span>`).join('')}<time datetime="${esc(r.released_at)}">${esc(date(r.released_at))}</time></header>
        ${r.downloads.map(f => `<a class="file-link" href="${esc(f.url)}"><span class="kind">${esc(f.kind.toUpperCase())}</span><span class="name">${esc(f.filename)}<small>${esc(loaderLabels[f.loader] || f.loader)} · ${size(f.size)}</small></span>${icon('download')}</a>`).join('')}
        ${r.notes ? `<p class="notes">${esc(r.notes)}</p>` : ''}
    </article>`).join('')}</div>`;
}

function ring(percent, state) {
    const circumference = 2 * Math.PI * 56;
    const filled = state === 'unknown' ? circumference : circumference * Math.min(1, percent / 100);

    return `<svg viewBox="0 0 132 132" aria-hidden="true"><circle class="track" cx="66" cy="66" r="56"/><circle class="value" cx="66" cy="66" r="56" stroke-dasharray="${circumference.toFixed(1)}" stroke-dashoffset="${(circumference - filled).toFixed(1)}"/></svg>`;
}

export function shieldState(report) {
    const complete = report.status === 'complete';
    const measured = complete && Number.isInteger(report.total_checks) && report.total_checks > 0;
    const risk = measured ? report.failed_checks / report.total_checks * 100 : 0;
    const label = complete
        ? (report.eligible ? 'Passed' : 'Review required')
        : ({ scanning: 'Scan in progress', not_scanned: 'Not scanned yet', incomplete: 'Scan incomplete', unavailable: 'Scanner unavailable' }[report.status] || 'Unavailable');

    return { complete, measured, risk, label, state: !measured ? 'unknown' : report.eligible ? 'pass' : 'fail' };
}

export function shieldRing(report) {
    const s = shieldState(report);
    return `<div class="ring ${s.state === 'fail' ? 'fail' : s.state === 'unknown' ? 'unknown' : ''}">${ring(s.measured ? 100 - s.risk : 0, s.state)}<div><strong>${s.measured ? s.risk.toFixed(2) + '%' : '—'}</strong><small>risk</small></div></div>`;
}

async function loadShield(mod, shieldUrl) {
    const mini = app.querySelector('#shield-mini');
    const full = app.querySelector('#security');

    try {
        const { report } = await api('/api/v1/mods/' + encodeURIComponent(mod.guid) + '/monkeshield-report?channel=' + encodeURIComponent(mod.channel.name));
        const s = shieldState(report);
        const tone = s.state === 'pass' ? 'good' : s.state === 'fail' ? 'bad' : '';
        const counts = s.measured ? `<p class="hint mono">${report.failed_checks} failed · ${report.total_checks - report.failed_checks} passed</p>` : '';

        mini.innerHTML = `<h2>${icon('shield')} MonkeShield</h2>${shieldRing(report)}<span class="pill ${tone}">${esc(s.label)}</span>${counts}<a class="button ghost small block" href="${shieldUrl}">Full report${icon('next')}</a>`;
        full.innerHTML = `<div class="row">${shieldRing(report)}<div><h2>${esc(s.label)}</h2><p class="hint">Static analysis of every .NET assembly in the latest ${esc(mod.channel.name)} release. Automatic approval requires fewer than 2.5% failed checks in each assembly.</p>${counts}<a class="button subtle" href="${shieldUrl}">${icon('shield')}Open full report</a></div></div>
            ${(report.diagnostics || []).map(d => `<p class="notice">${esc(d)}</p>`).join('')}
            <p class="hint">Static analysis is not a guarantee of safety. Review a mod’s source and permissions before installing it.</p>`;
    } catch {
        mini.innerHTML = `<h2>${icon('shield')} MonkeShield</h2><p class="hint">Analysis unavailable.</p><a class="button ghost small block" href="${shieldUrl}">Open report</a>`;
        full.innerHTML = '<p class="hint">Analysis unavailable.</p>';
    }
}

function stars(value) {
    return `<span class="stars">${[1, 2, 3, 4, 5].map(n => `<span class="${n <= Math.round(value) ? 'on' : ''}">★</span>`).join('')}</span>`;
}

async function community(mod, base) {
    const panel = app.querySelector('#community');
    const open = mod.status === 'approved';
    let page = 1;

    panel.innerHTML = `
        <div class="row" id="rating-summary"></div>
        <div id="rating-controls"></div>
        <div class="section-title"><h2>Comments</h2></div>
        ${auth.user && open ? `<form id="comment-form" class="stack"><label class="sr-only" for="comment-body">Write a comment</label><textarea id="comment-body" required maxlength="3000" rows="3" placeholder="Share feedback, tips or bug reports…"></textarea><div class="row"><span class="spacer"></span><button class="button" type="submit">${icon('chat')}Post comment</button></div></form>`
            : `<p class="hint">${open ? `<a href="${signInUrl()}">Sign in with Discord</a> to rate and comment.` : 'Ratings and comments open after approval.'}</p>`}
        <div id="comments"></div>
        <div id="comment-pages"></div>`;

    async function ratings() {
        const r = await api(base + '/ratings');
        app.querySelector('#stat-rating').textContent = r.count ? `${r.average.toFixed(1)} ★` : '—';
        panel.querySelector('#rating-summary').innerHTML = r.count
            ? `<div class="rating-big"><strong>${r.average.toFixed(1)}</strong><div>${stars(r.average)}<div class="hint mono">${r.count} rating${r.count === 1 ? '' : 's'}</div></div></div>`
            : '<p class="hint">No ratings yet. Be the first.</p>';

        const controls = panel.querySelector('#rating-controls');

        if (!auth.user || !open) {
            controls.innerHTML = '';
            return;
        }

        controls.innerHTML = `<div class="row"><span class="hint">Your rating</span><div class="star-input">${[1, 2, 3, 4, 5].map(n => `<button type="button" data-stars="${n}" class="${n <= (r.mine || 0) ? 'on' : ''}" aria-label="Rate ${n} of 5" aria-pressed="${r.mine === n}">★</button>`).join('')}</div>${r.mine ? '<button class="button ghost small" id="remove-rating">Remove</button>' : ''}</div>`;
        controls.querySelectorAll('[data-stars]').forEach(b => {
            b.onmouseenter = () => controls.querySelectorAll('[data-stars]').forEach(s => s.classList.toggle('on', Number(s.dataset.stars) <= Number(b.dataset.stars)));
            b.onmouseleave = () => controls.querySelectorAll('[data-stars]').forEach(s => s.classList.toggle('on', Number(s.dataset.stars) <= (r.mine || 0)));
            b.onclick = async () => {
                try {
                    await api(base + '/ratings', { method: 'PUT', json: { stars: Number(b.dataset.stars) } });
                    await ratings();
                } catch (error) {
                    toast(error.message, 'error');
                }
            };
        });
        controls.querySelector('#remove-rating')?.addEventListener('click', async () => {
            try {
                await api(base + '/ratings', { method: 'DELETE' });
                await ratings();
            } catch (error) {
                toast(error.message, 'error');
            }
        });
    }

    async function comments() {
        const result = await api(base + '/comments?page=' + page);

        if (!result.items.length && page > 1) {
            page--;
            return comments();
        }

        app.querySelector('#comment-count').textContent = result.total ? String(result.total) : '';
        panel.querySelector('#comments').innerHTML = result.items.length
            ? result.items.map(c => `<article class="comment">${avatar(c.author, 'sm')}<div><header><a href="${esc(c.author.profile_url)}"><strong>${esc(c.author.name)}</strong></a><time datetime="${esc(c.created_at)}" title="${esc(date(c.created_at))}">${esc(ago(c.created_at))}</time>${c.can_delete ? `<span class="spacer"></span><button class="button ghost small" data-delete="${c.id}" aria-label="Delete comment">${icon('trash')}</button>` : ''}</header><p>${esc(c.body)}</p></div></article>`).join('')
            : '<p class="hint">No comments yet.</p>';

        panel.querySelectorAll('[data-delete]').forEach(b => b.onclick = async () => {
            if (!confirm('Delete this comment?'))
                return;

            try {
                await api(base + '/comments/' + b.dataset.delete, { method: 'DELETE' });
                await comments();
            } catch (error) {
                toast(error.message, 'error');
            }
        });

        const pages = panel.querySelector('#comment-pages');
        const total = Math.ceil(result.total / 20);
        pages.innerHTML = total > 1 ? `<div class="pagination"><button class="button subtle small" id="c-prev" ${page === 1 ? 'disabled' : ''}>Newer</button><span>${page} / ${total}</span><button class="button subtle small" id="c-next" ${page >= total ? 'disabled' : ''}>Older</button></div>` : '';
        pages.querySelector('#c-prev')?.addEventListener('click', () => { page--; comments(); });
        pages.querySelector('#c-next')?.addEventListener('click', () => { page++; comments(); });
    }

    const form = panel.querySelector('#comment-form');

    if (form) {
        form.onsubmit = async event => {
            event.preventDefault();
            const button = form.querySelector('button');
            button.disabled = true;

            try {
                await api(base + '/comments', { method: 'POST', json: { body: form.querySelector('textarea').value } });
                form.reset();
                page = 1;
                await comments();
            } catch (error) {
                toast(error.message, 'error');
            } finally {
                button.disabled = false;
            }
        };
    }

    await Promise.all([ratings(), comments()]).catch(error => toast(error.message, 'error'));
}
