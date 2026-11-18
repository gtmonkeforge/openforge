import { api, auth, esc, icon, number, pagination, toast } from '../core.js';
import { confirmDialog, dialog, head } from './ui.js';

const historyKey = 'monkeforge.admin.sql-history';
const snippets = [
    ['Tables and row counts', "SELECT name, (SELECT COUNT(*) FROM pragma_table_info(name)) AS columns FROM sqlite_master WHERE type='table' ORDER BY name;"],
    ['Newest users', 'SELECT id, username, display_name, email, is_admin, is_banned, created_at FROM users ORDER BY created_at DESC LIMIT 25;'],
    ['Mods by status', 'SELECT status, COUNT(*) FROM mods GROUP BY status;'],
    ['Failed emails', "SELECT id, to_address, subject, error FROM email_outbox WHERE status='failed' ORDER BY id DESC LIMIT 50;"],
    ['Integrity check', 'PRAGMA integrity_check;'],
];

export async function render({ app, params, alive }) {
    if (!auth.user.is_root) {
        app.innerHTML = '<div class="empty"><h2>Root only</h2><p>Only the root administrator can open the database.</p></div>';
        return;
    }

    const { items: tables } = await api('/api/v1/admin/db/tables');

    if (!alive())
        return;

    const mode = params.get('view') === 'sql' ? 'sql' : 'tables';
    app.innerHTML = `${head('Database', 'Raw rows of the registry database. Changes skip every check the site normally makes, and are recorded in the audit log.')}
        <div class="toolbar"><div class="segmented" role="tablist">
            <a href="/admin/database" ${mode === 'tables' ? 'aria-current="page"' : ''}>${icon('database')} Tables</a>
            <a href="/admin/database?view=sql" ${mode === 'sql' ? 'aria-current="page"' : ''}>${icon('terminal')} SQL console</a>
        </div></div>
        <div id="db-body"></div>`;

    const body = app.querySelector('#db-body');

    if (mode === 'sql')
        sqlConsole(body, tables);
    else
        browser(body, tables, params.get('table') || tables.find(t => t.name === 'users')?.name || tables[0]?.name);
}

// How a raw value looks in a cell: NULL, numbers, text and blobs are told apart at a glance.
function cell(value) {
    if (value === null)
        return '<span class="db-null">NULL</span>';

    if (typeof value === 'object' && value.$blob !== undefined)
        return `<span class="db-blob">BLOB ${number(value.size)} B</span>`;

    if (typeof value === 'number')
        return `<span class="db-num">${esc(value)}</span>`;

    const text = String(value);
    return esc(text.length > 160 ? text.slice(0, 160) + '…' : text);
}

function typed(raw, kind) {
    if (kind === 'null')
        return null;

    if (kind === 'integer') {
        if (!/^-?\d+$/.test(raw.trim()))
            throw new Error('That isn’t a whole number.');

        return Number(raw.trim());
    }

    if (kind === 'real') {
        const n = Number(raw.trim());

        if (raw.trim() === '' || Number.isNaN(n))
            throw new Error('That isn’t a number.');

        // Keeps 1.0 a REAL rather than letting JSON turn it into an integer.
        return Number.isInteger(n) ? { $real: n } : n;
    }

    if (kind === 'blob') {
        if (!/^([\da-f]{2})*$/i.test(raw.trim()))
            throw new Error('Blobs are written as hexadecimal.');

        return { $blob: raw.trim() };
    }

    return raw;
}

function kindOf(value, declared) {
    if (value === null)
        return 'null';

    if (typeof value === 'object' && value.$blob !== undefined)
        return 'blob';

    if (typeof value === 'number')
        return Number.isInteger(value) && !/REAL|FLOA|DOUB/i.test(declared) ? 'integer' : 'real';

    return 'text';
}

async function editValue(column, value) {
    const kind = kindOf(value, column.type);
    const raw = value === null ? '' : typeof value === 'object' ? value.$blob : String(value);
    const result = await dialog({
        title: `Edit ${column.name}`,
        text: `${esc(column.type || 'no declared type')}${column.not_null ? ' · NOT NULL' : ''}${column.pk ? ' · primary key' : ''}`,
        wide: true,
        confirm: 'Save',
        body: `<div class="field"><label for="db-kind">Store as</label><select id="db-kind" name="kind">${['text', 'integer', 'real', 'blob', 'null'].map(k => `<option value="${k}" ${k === kind ? 'selected' : ''}>${k.toUpperCase()}</option>`).join('')}</select></div>
            <div class="field"><label for="db-value">Value</label><textarea id="db-value" name="value" rows="${raw.length > 200 || raw.includes('\n') ? 10 : 3}" class="mono">${esc(raw)}</textarea></div>
            ${typeof value === 'object' && value?.truncated ? '<p class="error-text">This blob is shown cut short; saving would replace it with the shortened copy.</p>' : ''}`,
        onOpen: form => {
            const sync = () => form.querySelector('#db-value').disabled = form.querySelector('#db-kind').value === 'null';
            form.querySelector('#db-kind').onchange = sync;
            sync();
        },
    });

    if (!result)
        return undefined;

    return typed(result.value ?? '', result.kind);
}

// JSON can't tell 1.0 from 1, so REAL integers travel as text the server stores with REAL affinity, or the number.
function wire(value) {
    return value && typeof value === 'object' && value.$real !== undefined ? value.$real : value;
}

function browser(body, tables, selected) {
    let state = { table: selected, page: 1, sort: '', dir: 'asc', column: '', filter: '' };
    let timer;

    body.innerHTML = `<div class="db-layout">
        <nav class="db-tables surface" aria-label="Tables">${tables.map(t => `<a href="/admin/database?table=${encodeURIComponent(t.name)}" data-table="${esc(t.name)}">${icon(t.type === 'view' ? 'eye' : 'database')}<span class="grow">${esc(t.name)}</span><span class="n">${number(t.rows)}</span></a>`).join('')}</nav>
        <section class="db-main" id="db-main"></section>
    </div>`;

    const main = body.querySelector('#db-main');

    const load = async () => {
        body.querySelectorAll('[data-table]').forEach(a => a.toggleAttribute('aria-current', a.dataset.table === state.table));
        history.replaceState(null, '', '/admin/database?table=' + encodeURIComponent(state.table));
        const info = tables.find(t => t.name === state.table);
        const data = await api(`/api/v1/admin/db/tables/${encodeURIComponent(state.table)}/rows?` + new URLSearchParams({ page: state.page, sort: state.sort, dir: state.dir, column: state.column, filter: state.filter }));

        main.innerHTML = `<div class="toolbar">
                <h2 class="mono">${esc(data.table)}</h2>${info.type === 'view' ? '<span class="pill">view</span>' : ''}${data.editable ? '' : '<span class="pill warn">read-only here</span>'}
                <span class="count">${number(data.total)} rows</span><span class="spacer"></span>
                <select id="db-column" aria-label="Filter column"><option value="">Any column</option>${data.columns.map(c => `<option ${c.name === state.column ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}</select>
                <input id="db-filter" type="search" placeholder="Contains…" value="${esc(state.filter)}" aria-label="Filter rows">
                ${data.editable ? `<button class="button small" id="db-insert">${icon('plus')}Insert row</button>` : ''}
                <button class="button ghost small" id="db-schema">${icon('code')}Schema</button>
            </div>
            <div class="table-wrap surface db-grid"><table class="data"><thead><tr>${data.editable ? `<th data-sort="rowid" class="mono">rowid${state.sort === 'rowid' ? (state.dir === 'asc' ? ' ↑' : ' ↓') : ''}</th>` : ''}${data.columns.map(c => `<th data-sort="${esc(c.name)}" title="${esc(c.type)}${c.pk ? ' · primary key' : ''}${c.not_null ? ' · NOT NULL' : ''}">${c.pk ? icon('key') : ''}${esc(c.name)}<small>${esc(c.type)}</small>${state.sort === c.name ? (state.dir === 'asc' ? ' ↑' : ' ↓') : ''}</th>`).join('')}${data.editable ? '<th></th>' : ''}</tr></thead>
            <tbody>${data.rows.map((r, i) => `<tr>${data.editable ? `<td class="mono faint">${r.rowid}</td>` : ''}${r.values.map((v, j) => `<td ${data.editable ? `data-cell="${i}:${j}" tabindex="0" title="Double-click to edit"` : ''}>${cell(v)}</td>`).join('')}${data.editable ? `<td><button class="icon-button" data-delete="${i}" aria-label="Delete row ${r.rowid}">${icon('trash')}</button></td>` : ''}</tr>`).join('') || `<tr><td colspan="${data.columns.length + 2}" class="hint">No rows.</td></tr>`}</tbody></table></div>
            <div id="db-pages"></div>`;

        main.querySelectorAll('[data-sort]').forEach(th => th.onclick = () => {
            state.dir = state.sort === th.dataset.sort && state.dir === 'asc' ? 'desc' : 'asc';
            state.sort = th.dataset.sort;
            load();
        });
        main.querySelector('#db-column').onchange = event => { state.column = event.target.value; state.page = 1; load(); };
        main.querySelector('#db-filter').oninput = event => {
            clearTimeout(timer);
            timer = setTimeout(() => { state.filter = event.target.value; state.page = 1; load(); }, 300);
        };
        main.querySelector('#db-schema').onclick = () => dialog({ title: info.name, wide: true, confirm: 'Close', body: `<pre class="terminal">${esc(info.sql)}</pre>` });

        const edit = async target => {
            const [i, j] = target.dataset.cell.split(':').map(Number);
            const row = data.rows[i];
            const column = data.columns[j];
            const value = await editValue(column, row.values[j]);

            if (value === undefined)
                return;

            try {
                await api(`/api/v1/admin/db/tables/${encodeURIComponent(state.table)}/rows/${row.rowid}`, { method: 'PATCH', json: { values: { [column.name]: wire(value) } } });
                toast(`${column.name} updated.`);
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        };

        main.querySelectorAll('[data-cell]').forEach(td => {
            td.ondblclick = () => edit(td);
            td.onkeydown = event => event.key === 'Enter' && edit(td);
        });

        main.querySelectorAll('[data-delete]').forEach(b => b.onclick = async () => {
            const row = data.rows[Number(b.dataset.delete)];

            if (!await confirmDialog(`Delete row ${row.rowid}?`, `It’s removed from ${state.table} straight away. Rows in other tables that point at it may block this, or be deleted with it.`, 'Delete', true))
                return;

            try {
                await api(`/api/v1/admin/db/tables/${encodeURIComponent(state.table)}/rows/${row.rowid}`, { method: 'DELETE' });
                toast('Row deleted.');
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });

        main.querySelector('#db-insert')?.addEventListener('click', async () => {
            const result = await dialog({
                title: `Insert into ${state.table}`,
                text: 'Leave a column empty to use its default. Type NULL for an explicit null. Numbers are stored as numbers.',
                wide: true,
                confirm: 'Insert',
                body: `<div class="db-insert">${data.columns.map(c => `<div class="field"><label for="ins-${esc(c.name)}">${esc(c.name)} <span>${esc(c.type)}${c.not_null ? ' · required' : ''}${c.default != null ? ` · default ${esc(c.default)}` : ''}</span></label><input id="ins-${esc(c.name)}" name="${esc(c.name)}" class="mono"></div>`).join('')}</div>`,
            });

            if (!result)
                return;

            const values = {};

            for (const c of data.columns) {
                const raw = result[c.name];

                if (raw === undefined || raw === '')
                    continue;

                values[c.name] = raw === 'NULL' ? null : /^-?\d+$/.test(raw) && /INT/i.test(c.type) ? Number(raw) : /^-?\d+(\.\d+)?$/.test(raw) && /REAL|FLOA|DOUB/i.test(c.type) ? Number(raw) : raw;
            }

            try {
                const inserted = await api(`/api/v1/admin/db/tables/${encodeURIComponent(state.table)}/rows`, { method: 'POST', json: { values } });
                toast(`Inserted row ${inserted.rowid}.`);
                await load();
            } catch (error) {
                toast(error.message, 'error');
            }
        });

        pagination(main.querySelector('#db-pages'), data.total, data.limit, state.page, p => { state.page = p; load(); });
    };

    body.querySelectorAll('[data-table]').forEach(a => a.onclick = event => {
        event.preventDefault();
        state = { table: a.dataset.table, page: 1, sort: '', dir: 'asc', column: '', filter: '' };
        load().catch(error => toast(error.message, 'error'));
    });

    if (state.table)
        load().catch(error => main.innerHTML = `<div class="error">${esc(error.message)}</div>`);
}

function readHistory() {
    try {
        return JSON.parse(localStorage.getItem(historyKey) || '[]');
    } catch {
        return [];
    }
}

function sqlConsole(body, tables) {
    body.innerHTML = `<div class="sql-layout">
        <section class="surface panel stack">
            <textarea id="sql" class="sql-editor" spellcheck="false" autocapitalize="off" placeholder="SELECT * FROM users LIMIT 10;" aria-label="SQL"></textarea>
            <div class="row">
                <label class="check"><input type="checkbox" id="sql-writes">Allow writes</label>
                <label class="check"><input type="checkbox" id="sql-rollback" disabled>Dry run (roll back at the end)</label>
                <span class="spacer"></span><span class="kbd-hint"><kbd>⌘/Ctrl</kbd>+<kbd>Enter</kbd> to run</span>
                <button class="button" id="sql-run">${icon('bolt')}Run</button>
            </div>
            <p class="hint">Statements are parsed by SQLite one at a time and checked before they run. Without “Allow writes” the connection is read-only. With it, everything runs in one transaction that rolls back if any statement fails. ATTACH, DETACH, VACUUM and transaction statements aren’t allowed. Each query stops after 15 seconds; at most 500 rows are shown.</p>
            <div id="sql-results"></div>
        </section>
        <aside class="stack">
            <section class="surface panel"><h3>Snippets</h3><div class="draft-list">${snippets.map(([label], i) => `<button type="button" data-snippet="${i}"><strong>${esc(label)}</strong></button>`).join('')}</div></section>
            <section class="surface panel"><h3>Tables</h3><div class="sql-tables">${tables.map(t => `<button type="button" class="chip" data-insert="${esc(t.name)}" title="${esc(t.columns.map(c => c.name).join(', '))}">${esc(t.name)}</button>`).join('')}</div></section>
            <section class="surface panel"><h3>History</h3><div class="draft-list" id="sql-history"></div></section>
        </aside>
    </div>`;

    const editor = body.querySelector('#sql');
    const writes = body.querySelector('#sql-writes');
    const rollback = body.querySelector('#sql-rollback');
    const results = body.querySelector('#sql-results');

    const paintHistory = () => {
        const items = readHistory();
        body.querySelector('#sql-history').innerHTML = items.map((h, i) => `<button type="button" data-history="${i}"><strong class="mono">${esc(h.length > 90 ? h.slice(0, 90) + '…' : h)}</strong></button>`).join('') || '<p class="hint">Queries you run appear here.</p>';
        body.querySelectorAll('[data-history]').forEach(b => b.onclick = () => { editor.value = items[Number(b.dataset.history)]; editor.focus(); });
    };

    writes.onchange = () => {
        rollback.disabled = !writes.checked;
        rollback.checked = rollback.checked && writes.checked;
        editor.classList.toggle('writes', writes.checked);
    };

    body.querySelectorAll('[data-snippet]').forEach(b => b.onclick = () => { editor.value = snippets[Number(b.dataset.snippet)][1]; editor.focus(); });
    body.querySelectorAll('[data-insert]').forEach(b => b.onclick = () => { editor.setRangeText(b.dataset.insert, editor.selectionStart, editor.selectionEnd, 'end'); editor.focus(); });

    const run = async () => {
        const sql = editor.value.trim();

        if (!sql)
            return;

        if (writes.checked && !rollback.checked && !await confirmDialog('Run with writes?', 'Changes are committed straight to the live database if every statement succeeds. There’s no undo.', 'Run and commit', true))
            return;

        const button = body.querySelector('#sql-run');
        button.disabled = true;
        results.innerHTML = '<div class="page-loading"><span class="spinner"></span>Running…</div>';

        try {
            const data = await api('/api/v1/admin/db/sql', { method: 'POST', json: { sql, allow_writes: writes.checked, rollback: rollback.checked } });
            const items = [sql, ...readHistory().filter(h => h !== sql)].slice(0, 30);

            try {
                localStorage.setItem(historyKey, JSON.stringify(items));
            } catch {
            }

            paintHistory();
            const wrote = data.statements.some(s => !s.read_only && !s.error);
            results.innerHTML = `${wrote ? `<p class="notice">${data.committed ? 'Changes committed.' : data.error ? 'Nothing was saved: an error rolled every change back.' : 'Dry run: every change was rolled back.'}</p>` : data.error ? '<p class="notice">Stopped at the error below. Statements before it ran.</p>' : ''}
                ${data.statements.map((s, i) => `<section class="sql-result ${s.error ? 'failed' : ''}">
                    <header class="row"><span class="pill mono">${i + 1}</span><span class="pill ${s.read_only ? '' : 'warn'}">${s.read_only ? 'read' : 'write'}</span><code class="grow">${esc(s.sql.length > 200 ? s.sql.slice(0, 200) + '…' : s.sql)}</code><span class="hint mono">${s.ms} ms${s.read_only ? '' : ` · ${number(s.changes)} changed`}</span></header>
                    ${s.error ? `<p class="error">${esc(s.error)}</p>` : ''}
                    ${s.columns.length ? `<div class="table-wrap db-grid"><table class="data"><thead><tr>${s.columns.map(c => `<th>${esc(c)}</th>`).join('')}</tr></thead><tbody>${s.rows.map(r => `<tr>${r.map(v => `<td>${cell(v)}</td>`).join('')}</tr>`).join('') || `<tr><td colspan="${s.columns.length}" class="hint">No rows.</td></tr>`}</tbody></table></div>
                        <p class="hint">${number(s.rows.length)} row${s.rows.length === 1 ? '' : 's'}${s.truncated ? ' (more not shown)' : ''}</p>` : ''}
                </section>`).join('')}`;
        } catch (error) {
            results.innerHTML = `<div class="error">${esc(error.message)}</div>`;
        }

        button.disabled = false;
    };

    body.querySelector('#sql-run').onclick = run;
    editor.onkeydown = event => {
        if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
            event.preventDefault();
            run();
        } else if (event.key === 'Tab') {
            event.preventDefault();
            editor.setRangeText('    ', editor.selectionStart, editor.selectionEnd, 'end');
        }
    };

    paintHistory();
    editor.focus();
}
