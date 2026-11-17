import { esc, icon, onOutside } from '../core.js';

export const boot = JSON.parse(document.querySelector('#boot')?.textContent || '{}');
export const publicUrl = boot.public_url || location.origin;
export const site = path => publicUrl + path;

// Timestamps arrive as ISO strings or epoch seconds.
export function when(value) {
    if (value == null || value === '')
        return '';

    const d = typeof value === 'number' ? new Date(value * 1000) : new Date(value);
    const today = new Date();
    const sameDay = d.toDateString() === today.toDateString();
    return sameDay ? d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' }) : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' });
}

export function fullWhen(value) {
    if (value == null || value === '')
        return '';

    const d = typeof value === 'number' ? new Date(value * 1000) : new Date(value);
    return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function head(title, subtitle = '', actions = '') {
    return `<header class="admin-head"><button class="icon-button admin-menu-toggle" data-menu aria-label="Open navigation">${icon('menu')}</button>
        <div><h1>${esc(title)}</h1>${subtitle ? `<p>${subtitle}</p>` : ''}</div><span class="spacer"></span>${actions}</header>`;
}

export function loading() {
    return '<div class="page-loading"><span class="spinner"></span>Loading…</div>';
}

// A modal that resolves with the form's values, or null when dismissed.
export function dialog({ title, text = '', body = '', confirm = 'Confirm', danger = false, wide = false, onOpen }) {
    return new Promise(resolve => {
        const wrap = document.createElement('div');
        wrap.className = 'modal';
        wrap.innerHTML = `<form class="modal-card ${wide ? 'wide' : ''}" role="dialog" aria-modal="true" aria-labelledby="modal-title">
            <h2 id="modal-title">${esc(title)}</h2>${text ? `<p>${text}</p>` : ''}${body}
            <div class="modal-actions"><button type="button" class="button ghost" data-cancel>Cancel</button><button class="button ${danger ? 'danger' : ''}" data-ok>${esc(confirm)}</button></div></form>`;
        document.body.append(wrap);

        const form = wrap.querySelector('form');
        const close = value => {
            wrap.remove();
            document.removeEventListener('keydown', escape);
            resolve(value);
        };
        const escape = event => event.key === 'Escape' && close(null);

        document.addEventListener('keydown', escape);
        wrap.addEventListener('pointerdown', event => event.target === wrap && close(null));
        wrap.querySelector('[data-cancel]').onclick = () => close(null);
        form.onsubmit = event => {
            event.preventDefault();
            close(Object.fromEntries(new FormData(form)));
        };

        onOpen?.(form);
        (form.querySelector('input, textarea, select') || form.querySelector('[data-ok]')).focus();
    });
}

export function confirmDialog(title, text, confirm = 'Confirm', danger = false) {
    return dialog({ title, text: esc(text), confirm, danger }).then(result => result !== null);
}

// A small menu anchored to a button. Items: { label, icon, action, danger, disabled, href } or 'sep'.
export function menu(anchor, items) {
    document.querySelectorAll('.kebab-menu').forEach(m => m.remove());
    const box = document.createElement('div');
    box.className = 'kebab-menu';
    box.setAttribute('role', 'menu');
    box.innerHTML = items.map((item, i) => item === 'sep' ? '<hr>' : item.href
        ? `<a role="menuitem" href="${esc(item.href)}" target="_blank" rel="noopener">${icon(item.icon || 'external')}${esc(item.label)}</a>`
        : `<button type="button" role="menuitem" data-i="${i}" class="${item.danger ? 'danger' : ''}" ${item.disabled ? `disabled title="${esc(item.disabled === true ? '' : item.disabled)}"` : ''}>${icon(item.icon || 'next')}${esc(item.label)}</button>`).join('');
    anchor.parentElement.append(box);
    const stop = onOutside(box, () => box.remove());

    box.querySelectorAll('[data-i]').forEach(button => button.onclick = event => {
        event.stopPropagation();
        box.remove();
        stop();
        items[Number(button.dataset.i)].action();
    });
    box.querySelector('button:not(:disabled), a')?.focus();
}

export function drawer(html, wide = true) {
    document.querySelector('.drawer')?.remove();
    const wrap = document.createElement('div');
    wrap.className = 'drawer';
    wrap.innerHTML = `<aside class="drawer-panel ${wide ? 'wide' : ''}" role="dialog" aria-modal="true">${html}</aside>`;
    document.body.append(wrap);

    const close = () => {
        wrap.remove();
        document.removeEventListener('keydown', escape);
    };
    const escape = event => event.key === 'Escape' && !document.querySelector('.modal') && close();

    document.addEventListener('keydown', escape);
    wrap.addEventListener('pointerdown', event => event.target === wrap && close());
    return { panel: wrap.querySelector('.drawer-panel'), close };
}

// Inserts text at the cursor of a textarea and keeps it focused.
export function insertAtCursor(textarea, text) {
    const start = textarea.selectionStart ?? textarea.value.length;
    const end = textarea.selectionEnd ?? start;
    const before = textarea.value.slice(0, start);
    const pad = before && !/\s$/.test(before) ? ' ' : '';
    textarea.setRangeText(pad + text, start, end, 'end');
    textarea.dispatchEvent(new Event('input'));
    textarea.focus();
}

// Pending reviews and open tickets in the sidebar, refreshed when something changes them.
export const counters = { refresh: () => {} };
