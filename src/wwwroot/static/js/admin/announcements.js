import { api, applyWidths, auth, brand, esc, icon, number, toast } from '../core.js';
import { confirmDialog, dialog, head, insertAtCursor, when } from './ui.js';

let lastTest = '';

export async function render({ app, alive }) {
    const [data, drafts] = await Promise.all([api('/api/v1/admin/email'), api('/api/v1/admin/email/drafts')]);

    if (!alive())
        return;

    const progress = b => {
        const done = b.sent + b.failed;
        return `<div class="bar"><i data-width="${b.recipients ? (done / b.recipients * 100).toFixed(1) : 100}"></i></div>
            <div class="hint">${number(b.sent)} sent${b.queued ? ` · ${number(b.queued)} queued` : ''}${b.failed ? ` · <span class="error-text">${number(b.failed)} failed</span>` : ''} of ${number(b.recipients)}</div>`;
    };

    app.innerHTML = `${head('Announcements', `Draft an email in the ${esc(brand.name)} style and send it to everyone or to one address.`)}
        ${data.configured ? '' : '<p class="notice">Email isn’t configured on this server. Set SMTP_HOST, SMTP_USERNAME, SMTP_PASSWORD and MAIL_FROM in the server’s .env, then restart.</p>'}
        <div class="drafter">
            <section class="surface panel">
                <div class="row"><h2>${icon('edit')} Draft</h2><span class="spacer"></span><span class="hint" id="draft-state"></span><button type="button" class="button ghost small" id="new-draft">${icon('plus')}New</button></div>
                <form class="stack" id="drafter">
                    <div class="audience" role="radiogroup" aria-label="Audience">
                        <label><input type="radio" name="audience" value="subscribed" checked><strong>All users</strong><small>Everyone with a confirmed email who gets announcements. Each email has an unsubscribe link.</small><span class="n">${number(data.audience.subscribed)}</span></label>
                        ${auth.user.is_root ? `<label><input type="radio" name="audience" value="everyone"><strong>Everyone, including unsubscribed</strong><small>Legal or security notices only.</small><span class="n">${number(data.audience.everyone)}</span></label>` : ''}
                        <label><input type="radio" name="audience" value="address"><strong>A specific email address</strong><small>One person, with or without an account.</small><span class="n">1</span></label>
                    </div>
                    <div class="field" id="to-field" hidden><label for="d-to">Send to</label><input id="d-to" type="email" maxlength="254" placeholder="person@example.com">
                        ${data.support ? '<label class="check"><input type="checkbox" id="d-support">Send from the support address so replies become a ticket</label>' : ''}</div>
                    <div class="field"><label for="d-subject">Subject</label><input id="d-subject" required maxlength="150" placeholder="What’s new on ${esc(brand.name)}"></div>
                    <div class="field"><label for="d-body">Message<span>Markdown: **bold**, [links](https://…), ## headings, lists</span></label>
                        <div class="editor-toolbar row">
                            <button type="button" class="button ghost small" data-md="**|**" title="Bold"><strong>B</strong></button>
                            <button type="button" class="button ghost small" data-md="_|_" title="Italic"><em>I</em></button>
                            <button type="button" class="button ghost small" data-md="## |" title="Heading">H</button>
                            <button type="button" class="button ghost small" data-md="- |" title="List">${icon('menu')}</button>
                            <button type="button" class="button ghost small" data-md="[|](https://)" title="Link">${icon('link')}</button>
                        </div>
                        <textarea id="d-body" required maxlength="20000" rows="14"></textarea></div>
                    <div class="row">
                        <button type="button" class="button ghost" id="d-save">Save draft</button>
                        <button type="button" class="button subtle" id="d-test" ${data.configured ? '' : 'disabled'}>Send me a test</button>
                        <span class="spacer"></span>
                        <button type="submit" class="button" id="d-send" ${data.configured ? '' : 'disabled'}>${icon('send')}<span id="d-count"></span></button>
                    </div>
                </form>
                ${drafts.items.length ? `<div class="drawer-section"><h3>Drafts</h3><div class="draft-list">${drafts.items.map((d, i) => `<button type="button" data-draft="${i}"><strong>${esc(d.subject || '(no subject)')}</strong><small>${esc(d.audience === 'address' ? 'To ' + (d.to || '?') : 'All users')} · ${esc(d.author.name || '')} · ${esc(when(d.updated_at))}</small></button>`).join('')}</div></div>` : ''}
            </section>
            <section class="surface panel">
                <div class="row"><h2>${icon('eye')} Preview</h2><span class="spacer"></span><span class="hint">${esc(data.from || '')}</span></div>
                <iframe class="email-frame" id="d-frame" sandbox title="Email preview"></iframe>
            </section>
        </div>
        <section class="surface panel drawer-section">
            <div class="row"><h2>Sent</h2></div>
            ${data.broadcasts.length ? data.broadcasts.map(b => `<details class="broadcast">
                <summary><strong>${esc(b.subject)}</strong><div class="hint">${esc(when(b.created_at))} · by ${esc(b.author.name)}${b.target ? ` · to <span class="mono">${esc(b.target)}</span>` : ' · all users'}${b.include_opted_out ? ' · <span class="pill warn">ignored opt-outs</span>' : ''}</div>${progress(b)}</summary>
                <pre class="broadcast-body">${esc(b.body)}</pre>
            </details>`).join('') : '<p class="hint">Nothing sent yet.</p>'}
        </section>`;

    applyWidths(app);
    const form = app.querySelector('#drafter');
    const subject = form.querySelector('#d-subject');
    const body = form.querySelector('#d-body');
    const to = form.querySelector('#d-to');
    const fromSupport = form.querySelector('#d-support');
    const state = app.querySelector('#draft-state');
    let draftId = null;
    let timer;
    let previewTimer;
    let dirty = false;

    const audience = () => form.querySelector('[name=audience]:checked').value;
    const count = () => audience() === 'address' ? 1 : audience() === 'everyone' ? data.audience.everyone : data.audience.subscribed;

    const sync = () => {
        app.querySelector('#to-field').hidden = audience() !== 'address';
        to.required = audience() === 'address';
        app.querySelector('#d-count').textContent = audience() === 'address' ? 'Send email' : `Send to ${number(count())}`;
    };

    const preview = async () => {
        try {
            const { url } = await api('/api/v1/admin/email/preview', { method: 'POST', json: { subject: subject.value, body: body.value, ...(audience() === 'address' ? { to: to.value } : {}) } });
            app.querySelector('#d-frame').src = url;
        } catch {
        }
    };

    const save = async (quiet = true) => {
        if (!subject.value.trim() && !body.value.trim())
            return;

        try {
            const json = { subject: subject.value, body: body.value, audience: audience(), to: to.value };
            const result = await api('/api/v1/admin/email/drafts' + (draftId ? '/' + draftId : ''), { method: draftId ? 'PUT' : 'POST', json });
            draftId = result.id;
            dirty = false;
            state.textContent = 'Draft saved ' + when(result.updated_at);

            if (!quiet)
                toast('Draft saved.');
        } catch (error) {
            state.textContent = 'Couldn’t save draft';

            if (!quiet)
                toast(error.message, 'error');
        }
    };

    const changed = () => {
        dirty = true;
        state.textContent = 'Unsaved changes';
        clearTimeout(timer);
        timer = setTimeout(save, 2500);
        clearTimeout(previewTimer);
        previewTimer = setTimeout(preview, 500);
    };

    const loadDraft = d => {
        draftId = d?.id || null;
        subject.value = d?.subject || '';
        body.value = d?.body || '';
        to.value = d?.to || '';
        const radio = form.querySelector(`[name=audience][value="${d?.audience || 'subscribed'}"]`) || form.querySelector('[name=audience]');
        radio.checked = true;
        state.textContent = d ? 'Editing draft' : '';
        dirty = false;
        sync();
        preview();
    };

    form.querySelectorAll('[name=audience]').forEach(r => r.onchange = () => { sync(); changed(); });
    [subject, body, to].forEach(el => el.oninput = changed);
    form.querySelectorAll('[data-md]').forEach(b => b.onclick = () => {
        const [before, after] = b.dataset.md.split('|');
        const selected = body.value.slice(body.selectionStart, body.selectionEnd);
        insertAtCursor(body, before + selected + after);
    });
    app.querySelectorAll('[data-draft]').forEach(b => b.onclick = () => loadDraft(drafts.items[Number(b.dataset.draft)]));
    app.querySelector('#new-draft').onclick = async () => {
        if (dirty)
            await save();

        loadDraft(null);
    };
    app.querySelector('#d-save').onclick = () => save(false);

    app.querySelector('#d-test').onclick = async () => {
        if (!subject.value.trim() || !body.value.trim()) {
            toast('Write a subject and message first.', 'error');
            return;
        }

        const target = await dialog({ title: 'Send a test', text: 'The test is marked [Test] and its unsubscribe link is a placeholder.', confirm: 'Send test', body: `<div class="field"><label for="test-to">Send to</label><input id="test-to" name="to" type="email" required maxlength="254" value="${esc(lastTest)}"></div>` });

        if (!target)
            return;

        lastTest = target.to.trim();

        try {
            const result = await api('/api/v1/admin/email/test', { method: 'POST', json: { subject: subject.value, body: body.value, to: lastTest } });
            toast(`Test sent to ${result.to}.`);
        } catch (error) {
            toast(error.message, 'error');
        }
    };

    form.onsubmit = async event => {
        event.preventDefault();
        const single = audience() === 'address';

        if (!await confirmDialog(single ? `Email ${to.value.trim()}?` : `Email ${number(count())} people?`, `“${subject.value.trim()}” goes out now. This can’t be undone.`, single ? 'Send' : `Send to ${number(count())}`, !single))
            return;

        const button = app.querySelector('#d-send');
        button.disabled = true;

        try {
            const result = single
                ? await api('/api/v1/admin/email/direct', { method: 'POST', json: { to: to.value, subject: subject.value, body: body.value, from_support: !!fromSupport?.checked } })
                : await api('/api/v1/admin/email/broadcasts', { method: 'POST', json: { subject: subject.value, body: body.value, include_opted_out: audience() === 'everyone' } });
            toast(single ? `Sent to ${result.to}.` : `Queued for ${number(result.recipients)} people.`);

            if (draftId)
                await api('/api/v1/admin/email/drafts/' + draftId, { method: 'DELETE' }).catch(() => {});

            await render({ app, alive });
        } catch (error) {
            toast(error.message, 'error');
            button.disabled = false;
        }
    };

    sync();
    preview();
}
