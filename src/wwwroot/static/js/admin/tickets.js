import { ago, api, auth, avatar, brand, copy, esc, icon, onOutside, size, toast } from '../core.js';
import { navigate } from './main.js';
import { boot, confirmDialog, counters, dialog, fullWhen, insertAtCursor, menu, when } from './ui.js';

const folders = [
    ['open', 'Open', 'mail'],
    ['pending', 'Waiting on customer', 'clock'],
    ['mine', 'Assigned to me', 'user'],
    ['unassigned', 'Unassigned', 'tray'],
    ['closed', 'Closed', 'check'],
    ['all', 'All tickets', 'layers'],
];

let staff = null;

export async function render({ app, rest, params, alive }) {
    const folder = folders.some(f => f[0] === params.get('folder')) ? params.get('folder') : 'open';
    const state = { folder, q: params.get('q') || '', selected: rest ? Number(rest) : null, list: null };

    staff ??= await api('/api/v1/admin/staff').catch(() => ({ items: [], me: auth.user.id }));

    app.innerHTML = `<div class="mail ${state.selected ? 'reading' : ''}">
        <nav class="mail-folders" aria-label="Folders">
            <button class="button" id="compose">${icon('edit')}New message</button>
            ${folders.map(([key, label, glyph]) => `<a href="/admin/tickets?folder=${key}" data-folder="${key}" ${key === folder ? 'aria-current="page"' : ''}>${icon(glyph)}${esc(label)}<span class="n" data-count="${key}"></span></a>`).join('')}
            <div class="sep"></div>
            <a href="#" id="check-mail">${icon('refresh')}Check mail</a>
            <a href="#" id="manage-macros">${icon('note')}Saved replies</a>
            <p class="inbox-status">${boot.support_configured ? `Receiving at <span class="mono">${esc(boot.support_email)}</span>` : 'Support mailbox not configured: new email won’t become tickets.'}</p>
        </nav>
        <section class="mail-list" aria-label="Tickets">
            <div class="mail-list-head"><input type="search" id="ticket-search" placeholder="Search tickets, #number or MF-…" aria-label="Search tickets" value="${esc(state.q)}"></div>
            <div class="mail-items" id="mail-items"><div class="page-loading"><span class="spinner"></span></div></div>
        </section>
        <section class="mail-read" id="mail-read" aria-live="polite"></section>
    </div>`;

    const mail = app.querySelector('.mail');
    const items = app.querySelector('#mail-items');
    const read = app.querySelector('#mail-read');
    let timer;

    const url = () => `/admin/tickets${state.selected ? '/' + state.selected : ''}?${new URLSearchParams({ folder: state.folder, ...(state.q ? { q: state.q } : {}) })}`;

    const loadList = async () => {
        const data = await api('/api/v1/admin/tickets?' + new URLSearchParams({ folder: state.folder, q: state.q }));

        if (!alive())
            return;

        state.list = data;

        for (const [key] of folders) {
            const n = app.querySelector(`[data-count="${key}"]`);
            const value = data.counts[key];
            n.textContent = key === 'closed' || key === 'all' ? '' : value || '';
            n.classList.toggle('hot', key === 'open' && data.counts.unread > 0);
        }

        items.innerHTML = data.items.map(t => `<div class="mail-item ${t.unread ? 'unread' : ''}" role="button" tabindex="0" data-id="${t.id}" ${t.id === state.selected ? 'aria-current="true"' : ''}>
            <span class="dot"></span>
            <span class="who">${esc(t.user?.name || t.requester.name || t.requester.email)}</span>
            <time title="${esc(fullWhen(t.updated_at))}">${esc(when(t.updated_at))}</time>
            <span class="subj">${esc(t.subject)}</span>
            <span class="snip">${t.last_direction === 'out' ? 'You: ' : t.last_direction === 'note' ? 'Note: ' : ''}${esc((t.snippet || '').replace(/\s+/g, ' '))}</span>
            <span class="tags"><span class="pill mono">#${t.id}</span>${t.status !== 'open' && state.folder !== t.status ? `<span class="pill">${esc(t.status)}</span>` : ''}${t.assignee ? `<span class="pill">${icon('user')}${esc(t.assignee.name)}</span>` : ''}${t.attachment_count ? `<span class="pill">${icon('paperclip')}${t.attachment_count}</span>` : ''}${t.message_count > 1 ? `<span class="pill">${t.message_count}</span>` : ''}</span>
        </div>`).join('') || `<div class="mail-empty"><div>${icon('tray')}<p>${state.q ? 'No tickets match.' : 'Nothing here.'}</p></div></div>`;

        items.querySelectorAll('[data-id]').forEach(row => {
            const open = () => select(Number(row.dataset.id));
            row.onclick = open;
            row.onkeydown = event => (event.key === 'Enter' || event.key === ' ') && (event.preventDefault(), open());
        });
    };

    const select = async id => {
        state.selected = id;
        history.pushState(null, '', url());
        mail.classList.toggle('reading', !!id);
        items.querySelectorAll('[data-id]').forEach(row => {
            row.setAttribute('aria-current', String(Number(row.dataset.id) === id));

            if (Number(row.dataset.id) === id)
                row.classList.remove('unread');
        });

        if (id)
            await openTicket(id);
        else
            empty();
    };

    const empty = () => {
        read.innerHTML = `<div class="mail-empty"><div>${icon('mail')}<p>Choose a ticket to read it.</p><p class="hint">Email to ${esc(boot.support_email || 'the support address')} becomes a ticket automatically, with an auto-reply carrying its reference.</p></div></div>`;
    };

    const openTicket = async id => {
        read.innerHTML = '<div class="page-loading"><span class="spinner"></span></div>';
        let t;

        try {
            t = await api('/api/v1/admin/tickets/' + id);
        } catch (error) {
            read.innerHTML = `<div class="mail-empty"><div><p>${esc(error.message)}</p></div></div>`;
            return;
        }

        if (!alive() || state.selected !== id)
            return;

        counters.refresh();
        const visible = t.messages;
        const collapseBefore = visible.length > 3 ? visible.length - 2 : 0;

        read.innerHTML = `
            <header class="mail-read-head">
                <div class="row"><button class="button ghost small" id="back-to-list" aria-label="Back to list">${icon('back')}</button><h2 class="grow">${esc(t.subject)}</h2>
                    <div class="kebab"><button class="icon-button" id="ticket-more" aria-label="More actions">${icon('more')}</button></div></div>
                <div class="controls">
                    <button class="pill mono" data-copy-ref title="Copy reference">${esc(t.reference)}</button>
                    <span class="pill mono">#${t.id}</span>
                    <select id="ticket-status" aria-label="Status">${['open', 'pending', 'closed'].map(s => `<option value="${s}" ${s === t.status ? 'selected' : ''}>${s === 'pending' ? 'Waiting on customer' : s[0].toUpperCase() + s.slice(1)}</option>`).join('')}</select>
                    <select id="ticket-assignee" aria-label="Assignee"><option value="">Unassigned</option>${staff.items.map(s => `<option value="${esc(s.id)}" ${s.id === t.assignee_id ? 'selected' : ''}>${esc(s.id === staff.me ? s.name + ' (you)' : s.name)}</option>`).join('')}</select>
                    <span class="spacer"></span>
                    <span class="hint">Opened ${esc(fullWhen(t.created_at))}</span>
                </div>
                <div class="requester">
                    ${t.user ? avatar(t.user, 'sm') : ''}
                    <span>${esc(t.requester.name || '')} <span class="mono">&lt;${esc(t.requester.email)}&gt;</span></span>
                    ${t.user ? `<a class="pill accent" href="/admin/users?q=${encodeURIComponent(t.user.id)}">${icon('user')}@${esc(t.user.username)} · ${t.user.mod_count} mods${t.user.is_banned ? ' · banned' : ''}</a>` : '<span class="pill">No account</span>'}
                    ${t.other_tickets.length ? `<span class="pill">${t.other_tickets.length} other ticket${t.other_tickets.length === 1 ? '' : 's'}</span>` : ''}
                </div>
            </header>
            <div class="mail-thread" id="thread">${visible.map((m, i) => message(t, m, i < collapseBefore || m.direction === 'auto')).join('')}
                ${t.other_tickets.length ? `<section class="surface panel"><h3>Earlier tickets from this address</h3><div class="mini-list">${t.other_tickets.map(o => `<a href="/admin/tickets/${o.id}?folder=all"><span class="mono">#${o.id}</span>${esc(o.subject)}<span class="spacer"></span><span class="pill">${esc(o.status)}</span></a>`).join('')}</div></section>` : ''}
            </div>
            <form class="composer-box" id="composer">
                <div class="composer-tools">
                    <div class="segmented" role="tablist"><button type="button" role="tab" data-kind="reply" aria-selected="true">${icon('reply')}Reply</button><button type="button" role="tab" data-kind="note" aria-selected="false">${icon('note')}Internal note</button></div>
                    <span class="hint" id="composer-hint">Sent from ${esc(boot.support_email || 'support')} as “Hello ${esc(t.greeting)}, … – ${esc(auth.user.username)}, ${esc(brand.name)} support”</span>
                </div>
                <textarea id="reply-body" required maxlength="20000" placeholder="Write your reply… Markdown works: **bold**, [links](https://…), lists." aria-label="Message"></textarea>
                <div class="composer-tools">
                    <span class="relative"><button type="button" class="button ghost small" id="insert-link">${icon('link')}Insert link</button></span>
                    <span class="relative"><button type="button" class="button ghost small" id="insert-macro">${icon('note')}Saved reply</button></span>
                    <button type="button" class="button ghost small" id="preview-reply">${icon('eye')}Preview email</button>
                    <span class="spacer"></span>
                    <select id="after-send" aria-label="After sending">
                        <option value="pending">then wait on customer</option><option value="open">then keep open</option><option value="closed">then close</option>
                    </select>
                    <button class="button" id="send-reply">${icon('send')}<span>Send</span></button>
                </div>
            </form>`;

        bindMessages(read);
        const thread = read.querySelector('#thread');
        thread.scrollTop = thread.scrollHeight;

        read.querySelector('#back-to-list').onclick = () => select(null);
        read.querySelector('[data-copy-ref]').onclick = () => copy(t.reference, 'Reference copied.');

        const patch = async (json, message) => {
            try {
                await api('/api/v1/admin/tickets/' + t.id, { method: 'PATCH', json });
                toast(message);
                await loadList();
            } catch (error) {
                toast(error.message, 'error');
            }
        };

        read.querySelector('#ticket-status').onchange = event => patch({ status: event.target.value }, 'Status updated.');
        read.querySelector('#ticket-assignee').onchange = event => patch({ assignee_id: event.target.value || null }, event.target.value ? 'Assigned.' : 'Unassigned.');
        read.querySelector('#ticket-more').onclick = event => menu(event.currentTarget, [
            { label: 'Mark as unread', icon: 'mail', action: () => patch({ unread: true }, 'Marked unread.').then(() => select(null)) },
            {
                label: 'Rename ticket', icon: 'edit', action: async () => {
                    const result = await dialog({ title: 'Rename ticket', body: `<div class="field"><label for="rename">Title</label><input id="rename" name="subject" required maxlength="200" value="${esc(t.subject)}"></div>`, confirm: 'Save' });

                    if (result)
                        await patch({ subject: result.subject }, 'Renamed.').then(() => openTicket(t.id));
                },
            },
            ...(t.user ? [{ label: 'Open user', icon: 'users', action: () => navigate('/admin/users?q=' + encodeURIComponent(t.user.id)) }] : []),
            'sep',
            {
                label: 'Delete ticket (spam)', icon: 'trash', danger: true, action: async () => {
                    if (!await confirmDialog('Delete this ticket?', 'The ticket, its messages and attachments are removed for good. Use this for spam.', 'Delete', true))
                        return;

                    try {
                        await api('/api/v1/admin/tickets/' + t.id, { method: 'DELETE' });
                        toast('Ticket deleted.');
                        await select(null);
                        await loadList();
                    } catch (error) {
                        toast(error.message, 'error');
                    }
                },
            },
        ]);

        bindComposer(read, t, async () => {
            await loadList();
            await openTicket(t.id);
        });
    };

    app.querySelectorAll('[data-folder]').forEach(link => link.onclick = event => {
        event.preventDefault();
        state.folder = link.dataset.folder;
        app.querySelectorAll('[data-folder]').forEach(l => l.toggleAttribute('aria-current', l === link) || l.removeAttribute('aria-current'));
        link.setAttribute('aria-current', 'page');
        history.replaceState(null, '', url());
        loadList();
    });

    app.querySelector('#ticket-search').oninput = event => {
        clearTimeout(timer);
        timer = setTimeout(() => {
            state.q = event.target.value.trim();
            history.replaceState(null, '', url());
            loadList();
        }, 250);
    };

    app.querySelector('#check-mail').onclick = async event => {
        event.preventDefault();

        try {
            await api('/api/v1/admin/tickets/check', { method: 'POST' });
            toast('Checking the mailbox…');
            setTimeout(loadList, 4000);
        } catch (error) {
            toast(error.message, 'error');
        }
    };

    app.querySelector('#manage-macros').onclick = event => {
        event.preventDefault();
        manageMacros();
    };

    app.querySelector('#compose').onclick = () => compose({});

    if (params.get('compose') !== null)
        compose({ userId: params.get('user'), to: params.get('to') || '', name: params.get('name') || '' });

    await loadList();

    if (state.selected)
        await openTicket(state.selected);
    else
        empty();

    // New mail shows up without a refresh while the page is open.
    const poll = setInterval(() => {
        if (!alive() || !document.body.contains(items)) {
            clearInterval(poll);
            return;
        }

        if (!document.hidden)
            loadList();
    }, 30000);
}

// Everything after "On … wrote:" or a run of "> " lines is the customer's mail client quoting us back; it's folded away.
function splitQuoted(body) {
    const lines = body.split('\n');
    const index = lines.findIndex((line, i) => /^On .{4,200}wrote:\s*$/.test(line.trim()) || /^-{2,}\s*Original Message/i.test(line.trim())
        || (line.startsWith('>') && lines.slice(i).every(l => l.startsWith('>') || !l.trim())));

    return index <= 0 ? [body, ''] : [lines.slice(0, index).join('\n').trimEnd(), lines.slice(index).join('\n')];
}

function message(t, m, collapsed) {
    const [main, quoted] = m.direction === 'in' ? splitQuoted(m.body) : [m.body, ''];
    const who = m.direction === 'in' ? (t.requester.name || t.requester.email)
        : m.direction === 'auto' ? 'Auto-reply' : m.author?.name || 'Staff';
    const sub = m.direction === 'in' ? esc(m.from) + (m.to ? ` → ${esc(m.to)}` : '')
        : m.direction === 'note' ? 'Internal note · only staff see this'
            : `${esc(brand.name)} support → ${esc(m.to)}`;
    const delivery = m.delivery ? `<span class="pill delivery ${m.delivery.status === 'sent' ? 'good' : m.delivery.status === 'failed' ? 'bad' : 'warn'}" ${m.delivery.error ? `title="${esc(m.delivery.error)}"` : ''}>${esc(m.delivery.status)}</span>` : '';
    const face = m.direction === 'in' ? avatar({ name: who }) : m.direction === 'auto' ? `<span class="avatar">${icon('bolt')}</span>` : avatar({ name: who });

    return `<article class="mail-msg ${esc(m.direction)} ${collapsed ? 'collapsed' : ''}">
        <header data-toggle>${face}<div><strong>${esc(who)}</strong>${delivery}<small>${sub}</small></div><time title="${esc(fullWhen(m.created_at))}">${esc(fullWhen(m.created_at))} · ${esc(ago(m.created_at))}</time></header>
        <div class="mail-msg-body">${esc(main)}${quoted ? `\n<button type="button" class="show-quoted">•••</button><div class="quoted" hidden>${esc(quoted)}</div>` : ''}</div>
        ${m.attachments.length ? `<div class="mail-attachments">${m.attachments.map(a => `<a href="/api/v1/admin/tickets/attachments/${encodeURIComponent(a.id)}" download>${icon('paperclip')}${esc(a.filename)} <span class="hint">${size(a.size)}</span></a>`).join('')}</div>` : ''}
    </article>`;
}

function bindMessages(root) {
    root.querySelectorAll('[data-toggle]').forEach(header => header.onclick = () => header.parentElement.classList.toggle('collapsed'));
    root.querySelectorAll('.show-quoted').forEach(button => button.onclick = () => {
        const quoted = button.nextElementSibling;
        quoted.hidden = !quoted.hidden;
    });
}

// Search docs, mods, profiles and site pages, and drop a Markdown link in at the cursor.
function linkPicker(anchor, textarea) {
    document.querySelectorAll('.popover').forEach(p => p.remove());
    const box = document.createElement('div');
    box.className = 'popover';
    box.style.bottom = 'calc(100% + 6px)';
    box.style.left = '0';
    box.innerHTML = `<input type="search" placeholder="Search docs, mods, users, pages…" aria-label="Search links"><div class="popover-items"></div>`;
    anchor.parentElement.append(box);
    const input = box.querySelector('input');
    const list = box.querySelector('.popover-items');
    const glyphs = { doc: 'note', page: 'globe', mod: 'box', user: 'user' };
    let timer;
    let sequence = 0;
    const stop = onOutside(box, () => box.remove());

    const load = async () => {
        const ticket = ++sequence;
        const { items } = await api('/api/v1/admin/links?q=' + encodeURIComponent(input.value.trim()));

        if (ticket !== sequence)
            return;

        list.innerHTML = items.map((item, i) => `<button type="button" data-i="${i}">${icon(glyphs[item.type] || 'link')}<strong>${esc(item.title)}</strong><small>${esc(item.hint)} · ${esc(item.url.replace(/^https?:\/\//, ''))}</small></button>`).join('') || '<p class="hint">Nothing found.</p>';
        list.querySelectorAll('[data-i]').forEach(button => button.onclick = () => {
            const item = items[Number(button.dataset.i)];
            insertAtCursor(textarea, `[${item.title}](${item.url})`);
            box.remove();
            stop();
        });
    };

    input.oninput = () => {
        clearTimeout(timer);
        timer = setTimeout(load, 180);
    };
    input.onkeydown = event => {
        if (event.key === 'Escape') {
            box.remove();
            stop();
            textarea.focus();
        } else if (event.key === 'ArrowDown') {
            event.preventDefault();
            list.querySelector('button')?.focus();
        }
    };

    input.focus();
    load();
}

async function macroPicker(anchor, textarea) {
    document.querySelectorAll('.popover').forEach(p => p.remove());
    const { items } = await api('/api/v1/admin/macros');
    const box = document.createElement('div');
    box.className = 'popover';
    box.style.bottom = 'calc(100% + 6px)';
    box.style.left = '0';
    box.innerHTML = `<div class="popover-items">${items.map((m, i) => `<button type="button" data-i="${i}">${icon('note')}<strong>${esc(m.title)}</strong><small>${esc(m.body.slice(0, 90))}</small></button>`).join('') || '<p class="hint">No saved replies yet.</p>'}
        <button type="button" data-save>${icon('plus')}<strong>Save this message as a saved reply</strong><small>Reuse it on other tickets</small></button></div>`;
    anchor.parentElement.append(box);
    const stop = onOutside(box, () => box.remove());

    box.querySelectorAll('[data-i]').forEach(button => button.onclick = () => {
        insertAtCursor(textarea, items[Number(button.dataset.i)].body);
        box.remove();
        stop();
    });

    box.querySelector('[data-save]').onclick = async () => {
        box.remove();
        stop();

        if (!textarea.value.trim()) {
            toast('Write the message first, then save it.', 'error');
            return;
        }

        const result = await dialog({ title: 'Save reply', body: '<div class="field"><label for="macro-title">Title</label><input id="macro-title" name="title" required maxlength="80" placeholder="e.g. How to reset a password"></div>', confirm: 'Save' });

        if (!result)
            return;

        try {
            await api('/api/v1/admin/macros', { method: 'POST', json: { title: result.title, body: textarea.value } });
            toast('Saved reply added.');
        } catch (error) {
            toast(error.message, 'error');
        }
    };
}

async function manageMacros() {
    const { items } = await api('/api/v1/admin/macros');
    const result = await dialog({
        title: 'Saved replies',
        text: 'Reusable text for common answers. Insert one from the composer.',
        wide: true,
        confirm: 'Done',
        body: `<div class="mini-list">${items.map(m => `<div><strong>${esc(m.title)}</strong><span class="hint grow">${esc(m.body.slice(0, 80))}</span><button type="button" class="button ghost small" data-edit="${esc(m.id)}">Edit</button><button type="button" class="button danger small" data-delete="${esc(m.id)}">Delete</button></div>`).join('') || '<p class="hint">None yet. Write a reply, then choose Saved reply → Save this message.</p>'}</div>`,
        onOpen: form => {
            form.querySelectorAll('[data-delete]').forEach(button => button.onclick = async () => {
                await api('/api/v1/admin/macros/' + encodeURIComponent(button.dataset.delete), { method: 'DELETE' });
                button.parentElement.remove();
            });
            form.querySelectorAll('[data-edit]').forEach(button => button.onclick = async () => {
                const macro = items.find(m => m.id === button.dataset.edit);
                form.closest('.modal').remove();
                const edited = await dialog({
                    title: 'Edit saved reply', wide: true, confirm: 'Save',
                    body: `<div class="field"><label for="m-title">Title</label><input id="m-title" name="title" required maxlength="80" value="${esc(macro.title)}"></div><div class="field"><label for="m-body">Message</label><textarea id="m-body" name="body" required rows="10" maxlength="20000">${esc(macro.body)}</textarea></div>`,
                });

                if (edited) {
                    await api('/api/v1/admin/macros/' + encodeURIComponent(macro.id), { method: 'PUT', json: edited }).catch(error => toast(error.message, 'error'));
                    manageMacros();
                }
            });
        },
    });

    return result;
}

async function previewFrame(url) {
    await dialog({ title: 'Email preview', text: 'Exactly what the customer receives, including the support greeting and signature.', wide: true, confirm: 'Close', body: `<iframe class="email-frame" sandbox src="${esc(url)}" title="Email preview"></iframe>` });
}

function bindComposer(root, t, done) {
    const form = root.querySelector('#composer');
    const body = form.querySelector('#reply-body');
    const hint = form.querySelector('#composer-hint');
    const send = form.querySelector('#send-reply');
    const after = form.querySelector('#after-send');
    const draftKey = 'monkeforge.admin.reply.' + t.id;
    let kind = 'reply';

    try {
        body.value = localStorage.getItem(draftKey) || '';
    } catch {
    }

    body.oninput = () => {
        try {
            body.value ? localStorage.setItem(draftKey, body.value) : localStorage.removeItem(draftKey);
        } catch {
        }
    };

    form.querySelectorAll('[data-kind]').forEach(tab => tab.onclick = () => {
        kind = tab.dataset.kind;
        form.querySelectorAll('[data-kind]').forEach(b => b.setAttribute('aria-selected', String(b === tab)));
        form.classList.toggle('note', kind === 'note');
        send.querySelector('span').textContent = kind === 'note' ? 'Add note' : 'Send';
        after.hidden = kind === 'note';
        form.querySelector('#preview-reply').hidden = kind === 'note';
        body.placeholder = kind === 'note' ? 'Only staff see notes. Use them to hand over context.' : 'Write your reply…';
        hint.textContent = kind === 'note' ? 'Internal: the customer never sees this.' : `Sent from ${boot.support_email || 'support'} as “Hello ${t.greeting}, … – ${auth.user.username}, ${brand.name} support”`;
        body.focus();
    });

    form.querySelector('#insert-link').onclick = event => linkPicker(event.currentTarget, body);
    form.querySelector('#insert-macro').onclick = event => macroPicker(event.currentTarget, body).catch(error => toast(error.message, 'error'));
    form.querySelector('#preview-reply').onclick = async () => {
        if (!body.value.trim()) {
            toast('Write something to preview.', 'error');
            return;
        }

        try {
            const { url } = await api(`/api/v1/admin/tickets/${t.id}/preview`, { method: 'POST', json: { body: body.value } });
            await previewFrame(url);
        } catch (error) {
            toast(error.message, 'error');
        }
    };

    body.onkeydown = event => {
        if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
            event.preventDefault();
            form.requestSubmit();
        }
    };

    form.onsubmit = async event => {
        event.preventDefault();
        send.disabled = true;

        try {
            await api(`/api/v1/admin/tickets/${t.id}/messages`, { method: 'POST', json: { kind, body: body.value, ...(kind === 'reply' ? { status: after.value } : {}) } });
            toast(kind === 'note' ? 'Note added.' : 'Reply sent.');

            try {
                localStorage.removeItem(draftKey);
            } catch {
            }

            await done();
        } catch (error) {
            toast(error.message, 'error');
            send.disabled = false;
        }
    };
}

// A new conversation started by staff: from the Users page, or to any address.
export async function compose({ userId = null, to = '', name = '' }) {
    if (!boot.support_configured) {
        toast('The support mailbox isn’t configured, so email can’t be sent from it.', 'error');
        return;
    }

    let textarea;
    const result = await dialog({
        title: userId ? `Email ${name || 'user'}` : 'New message',
        text: `Sent from ${esc(boot.support_email)}. Replies come back here as a ticket.`,
        wide: true,
        confirm: 'Send',
        body: `${userId ? `<p class="hint">To: <strong>${esc(name)}</strong> <span class="mono">${esc(to)}</span></p>` : `<div class="field"><label for="c-to">To</label><input id="c-to" name="to" type="email" required maxlength="254" value="${esc(to)}" placeholder="person@example.com"></div>`}
            <div class="field"><label for="c-subject">Subject</label><input id="c-subject" name="subject" required maxlength="200"></div>
            <div class="field"><label for="c-body">Message<span>Wrapped in “Hello …” and your support signature</span></label><textarea id="c-body" name="body" required rows="9" maxlength="20000"></textarea></div>
            <div class="row"><span class="relative"><button type="button" class="button ghost small" id="c-link">${icon('link')}Insert link</button></span><span class="relative"><button type="button" class="button ghost small" id="c-macro">${icon('note')}Saved reply</button></span><button type="button" class="button ghost small" id="c-preview">${icon('eye')}Preview</button></div>`,
        onOpen: form => {
            textarea = form.querySelector('#c-body');
            form.querySelector('#c-link').onclick = event => linkPicker(event.currentTarget, textarea);
            form.querySelector('#c-macro').onclick = event => macroPicker(event.currentTarget, textarea);
            form.querySelector('#c-preview').onclick = async () => {
                try {
                    const { url } = await api('/api/v1/admin/tickets/compose-preview', { method: 'POST', json: { body: textarea.value, subject: form.querySelector('#c-subject').value, to: form.querySelector('#c-to')?.value || to } });
                    window.open(url, '_blank', 'noopener');
                } catch (error) {
                    toast(error.message, 'error');
                }
            };
        },
    });

    if (!result)
        return;

    try {
        const created = await api('/api/v1/admin/tickets', { method: 'POST', json: { ...(userId ? { user_id: userId } : { to: result.to }), subject: result.subject, body: result.body } });
        toast(`Sent. Ticket #${created.id} (${created.reference}) tracks the conversation.`);
        navigate(`/admin/tickets/${created.id}?folder=all`);
    } catch (error) {
        toast(error.message, 'error');
    }
}
