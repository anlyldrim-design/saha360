// Personel havuzu ve ekip taslağı.
// Sürükle-bırak, "İşe ekle" ve "+ Ekip alanı ekle" yalnızca ekrandaki TASLAĞI değiştirir. Kalıcı kayıt "kaydet" düğmesiyle,
// sunucuda tek seferde yapılır; uygunluk, çakışma ve unvan kontrolü orada tekrarlanır. Sayfa yenilenirse kaydedilmemiş
// taslak kaybolur (çıkarken uyarı verilir).
//
// Unvan kuralı: personel yalnız kendi unvanının alanına (ör. Tekniker → Tekniker) veya
// "kısıtlamasız" işaretli alanlara (ör. Şoför) atanabilir.
(function () {
    'use strict';
    const dataEl = document.getElementById('poolData');
    if (!dataEl) return;
    const data = JSON.parse(dataEl.textContent);
    const poolList = document.getElementById('poolList');
    const groupsEl = document.getElementById('groups');
    const planMode = !!data.workId;

    const stateText = { Available: 'Müsait', Busy: 'Atanmış', OnLeave: 'İzinli', Inactive: 'Pasif' };
    const stateCss = { Available: 'text-bg-success', Busy: 'text-bg-secondary', OnLeave: 'text-bg-warning', Inactive: 'text-bg-dark' };
    const roleById = new Map(data.groups.map(g => [g.id, g]));

    // Taslak: employeeId -> { id, name, roleId, titleId, status, locked, note }
    const draft = new Map();
    (data.team || []).forEach(t => draft.set(t.id, Object.assign({}, t)));
    // Bu işte açık ekip alanları (sıralı)
    let shown = (data.shownGroups || []).filter(id => roleById.has(id));
    const snapshot = () => JSON.stringify([[...draft.values()].map(t => [t.id, t.roleId]).sort(), shown]);
    const original = snapshot();
    let dirty = false;

    const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
    const icon = name => { const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); s.setAttribute('class', 'ic'); s.setAttribute('aria-hidden', 'true');
        const u = document.createElementNS('http://www.w3.org/2000/svg', 'use'); u.setAttribute('href', '#i-' + name); s.appendChild(u); return s; };
    const titleName = id => (roleById.get(id) || {}).name || 'unvanı yok';

    /** Personel bu alana atanabilir mi? (Sunucudaki AssignmentRules.CanTakeRole ile aynı kural.) */
    const canTake = (titleId, roleId) => { const r = roleById.get(roleId); return !!r && (r.open || titleId === roleId); };
    const whyNot = (name, titleId, roleId) =>
        `${name} (${titleName(titleId)}) "${titleName(roleId)}" alanına atanamaz. Personel yalnız kendi unvanının alanına veya "herkes atanabilir" alanlara (ör. Şoför) atanabilir.`;

    function markDirty() {
        dirty = snapshot() !== original;
        const flag = document.getElementById('dirtyFlag');
        if (flag) flag.hidden = !dirty;
    }

    function toast(msg, kind) {
        const box = document.getElementById('planWarnings') || poolList;
        const a = el('div', 'alert alert-' + (kind || 'warning') + ' py-2 px-3 mb-2 toast-msg', msg);
        a.setAttribute('role', 'alert');
        box.prepend(a);
        setTimeout(() => a.remove(), 7000);
    }

    function ensureGroup(roleId) {
        if (!shown.includes(roleId)) shown.push(roleId);
    }

    /** "İşe ekle" ile hedef alan: önce kendi unvanının alanı (yoksa açılır), unvanı yoksa ekrandaki kısıtlamasız alan. */
    function targetFor(p) {
        if (p.roleId && roleById.has(p.roleId) && roleById.get(p.roleId).active) return p.roleId;
        const open = shown.find(id => roleById.get(id).open);
        return open || null;
    }

    function add(empId, roleId) {
        const p = data.pool.find(x => x.id === empId);
        if (!p) return;
        if (draft.has(empId)) { toast(p.name + ' zaten bu işte.'); return; }
        if (p.state !== 'Available') { toast(p.name + ' atanamaz: ' + (p.reason || stateText[p.state])); return; }
        const target = roleId || targetFor(p);
        if (!target) { toast(p.name + ' için unvan tanımlı değil; önce "herkes atanabilir" bir alan (ör. Şoför) ekleyin veya personel kartından unvan seçin.'); return; }
        if (!canTake(p.roleId, target)) { toast(whyNot(p.name, p.roleId, target), 'danger'); return; }
        if (!shown.includes(target)) toast(`"${titleName(target)}" alanı bu işe eklendi.`, 'info');
        ensureGroup(target);
        draft.set(empId, { id: empId, name: p.name, roleId: target, titleId: p.roleId, status: 'Yeni (kaydedilmedi)', locked: false, isNew: true });
        markDirty(); render();
    }

    function remove(empId) {
        const t = draft.get(empId);
        if (!t) return;
        if (t.locked) { toast(t.name + ' çalışmaya başladı; ekipten çıkarılamaz.'); return; }
        draft.delete(empId); markDirty(); render();
    }

    function move(empId, roleId) {
        const t = draft.get(empId);
        if (!t || t.roleId === roleId) return;
        if (!canTake(t.titleId, roleId)) { toast(whyNot(t.name, t.titleId, roleId), 'danger'); render(); return; }
        t.roleId = roleId; markDirty(); render();
    }

    function removeGroup(roleId) {
        if ([...draft.values()].some(t => t.roleId === roleId)) { toast('Önce bu alandaki personeli çıkarın veya başka alana taşıyın.'); return; }
        shown = shown.filter(id => id !== roleId);
        markDirty(); render();
    }

    // ───────────── Havuz listesi ─────────────
    function renderPool() {
        poolList.textContent = '';
        if (data.pool.length === 0) { poolList.appendChild(el('div', 'empty', 'Bu süzgeçlere uyan personel yok.')); return; }
        let available = 0;
        data.pool.forEach(p => {
            const inDraft = draft.has(p.id);
            if (p.state === 'Available' && !inDraft) available++;
            const card = el('div', 'emp-card' + (p.state !== 'Available' ? ' unavailable' : '') + (inDraft ? ' in-draft' : ''));
            card.dataset.id = p.id;
            const canDrag = planMode && p.state === 'Available' && !inDraft;
            card.draggable = canDrag;
            if (canDrag) { const h = icon('drag'); h.setAttribute('class', 'ic drag-handle'); card.appendChild(h); }
            const body = el('div', 'emp-body');
            body.appendChild(el('div', 'emp-name', p.name));
            body.appendChild(el('div', 'emp-meta', [p.role || 'Unvan yok', p.team, p.no].filter(Boolean).join(' · ')));
            if (p.reason && !inDraft) body.appendChild(el('div', 'emp-reason', '⚠ ' + p.reason));
            if (!p.account) body.appendChild(el('div', 'emp-meta', 'Uygulama hesabı yok'));
            card.appendChild(body);
            card.appendChild(el('span', 'badge ' + (inDraft ? 'text-bg-primary' : stateCss[p.state]), inDraft ? 'Bu işte' : stateText[p.state]));
            if (canDrag) {
                const btn = el('button', 'btn btn-sm btn-outline-primary add-btn');
                btn.type = 'button';
                btn.appendChild(icon('plus'));
                btn.appendChild(document.createTextNode(' İşe ekle'));
                btn.setAttribute('aria-label', p.name + ' işe ekle');
                btn.addEventListener('click', () => add(p.id));
                card.appendChild(btn);
                card.addEventListener('dragstart', e => {
                    e.dataTransfer.setData('text/plain', 'pool:' + p.id); e.dataTransfer.effectAllowed = 'copyMove';
                    card.classList.add('dragging'); highlight(p.roleId);
                });
                card.addEventListener('dragend', () => { card.classList.remove('dragging'); highlight(null); });
            }
            poolList.appendChild(card);
        });
        const c = document.getElementById('poolCount');
        if (c) c.textContent = available + ' müsait / ' + data.pool.length + ' kişi';
    }

    /** Sürüklerken uygun alanları yeşil, uygun olmayanları soluk göster. */
    function highlight(titleId) {
        document.querySelectorAll('.group-zone').forEach(z => {
            const id = parseInt(z.dataset.role, 10);
            z.classList.toggle('can-drop', titleId !== null && canTake(titleId, id));
            z.classList.toggle('no-drop', titleId !== null && !canTake(titleId, id));
        });
    }

    // ───────────── Ekip alanları (bu işe özel) ─────────────
    function renderGroups() {
        if (!groupsEl) return;
        groupsEl.textContent = '';
        shown.forEach(roleId => {
            const g = roleById.get(roleId);
            const members = [...draft.values()].filter(t => t.roleId === g.id);
            const zone = el('div', 'group-zone');
            zone.dataset.role = g.id;
            const head = el('div', 'group-head');
            const title = el('span', 'fw-semibold', g.name);
            if (g.lead) title.appendChild(el('span', 'small text-secondary fw-normal', ' · ekip lideri'));
            head.appendChild(title);
            const right = el('span', 'd-flex align-items-center gap-1');
            right.appendChild(el('span', 'badge ' + (g.open ? 'text-bg-success' : 'text-bg-light border'), g.open ? 'Herkes atanabilir' : 'Yalnız ' + g.name));
            right.appendChild(el('span', 'badge text-bg-light border', String(members.length)));
            const rm = el('button', 'btn btn-sm btn-link text-secondary p-0 ms-1');
            rm.type = 'button'; rm.title = 'Bu alanı işten kaldır';
            rm.setAttribute('aria-label', g.name + ' alanını kaldır');
            rm.appendChild(icon('x'));
            rm.addEventListener('click', () => removeGroup(g.id));
            right.appendChild(rm);
            head.appendChild(right);
            zone.appendChild(head);
            if (members.length === 0)
                zone.appendChild(el('div', 'drop-hint', g.open ? 'Herhangi bir personeli buraya bırakın' : `Unvanı "${g.name}" olan personeli buraya bırakın`));
            members.forEach(t => {
                const card = el('div', 'team-card' + (t.isNew ? ' is-new' : ''));
                card.draggable = !t.locked;
                const body = el('div', 'emp-body');
                body.append(el('div', 'emp-name', t.name), el('div', 'emp-meta', `${titleName(t.titleId)} · ${t.status}`));
                card.appendChild(body);
                // Yalnız bu kişinin atanabileceği alanlar listelenir.
                const sel = el('select', 'form-select form-select-sm role-select');
                sel.setAttribute('aria-label', t.name + ' görev alanı');
                shown.filter(id => canTake(t.titleId, id) || id === t.roleId).forEach(id => {
                    const o = el('option', null, titleName(id)); o.value = id; if (id === t.roleId) o.selected = true; sel.appendChild(o);
                });
                sel.disabled = sel.options.length < 2;
                sel.addEventListener('change', () => move(t.id, parseInt(sel.value, 10)));
                card.appendChild(sel);
                const del = el('button', 'btn btn-sm btn-outline-danger');
                del.type = 'button';
                del.title = t.locked ? 'Çalışmaya başladı; çıkarılamaz' : 'Ekipten çıkar';
                del.disabled = !!t.locked;
                del.setAttribute('aria-label', t.name + ' ekipten çıkar');
                del.appendChild(icon('x'));
                del.addEventListener('click', () => remove(t.id));
                card.appendChild(del);
                card.addEventListener('dragstart', e => { e.dataTransfer.setData('text/plain', 'team:' + t.id); card.classList.add('dragging'); highlight(t.titleId); });
                card.addEventListener('dragend', () => { card.classList.remove('dragging'); highlight(null); });
                zone.appendChild(card);
            });
            zone.addEventListener('dragover', e => { e.preventDefault(); zone.classList.add('over'); });
            zone.addEventListener('dragleave', () => zone.classList.remove('over'));
            zone.addEventListener('drop', e => {
                e.preventDefault(); zone.classList.remove('over'); highlight(null);
                const [kind, id] = (e.dataTransfer.getData('text/plain') || '').split(':');
                const empId = parseInt(id, 10);
                if (kind === 'pool') add(empId, g.id);
                else if (kind === 'team') move(empId, g.id);
            });
            groupsEl.appendChild(zone);
        });

        // "+ Ekip alanı ekle": tüm unvanlardan bu işte henüz olmayanlar
        const addBox = el('div', 'group-zone add-group');
        const candidates = data.groups.filter(g => g.active && !shown.includes(g.id));
        addBox.appendChild(el('div', 'fw-semibold mb-2', '+ Ekip alanı ekle'));
        if (candidates.length === 0) {
            addBox.appendChild(el('div', 'small text-secondary', 'Tüm unvanlar bu işte. Yeni unvan Tanımlar › Görev rolleri ekranından eklenir.'));
        } else {
            const row = el('div', 'd-flex gap-2');
            const sel = el('select', 'form-select form-select-sm');
            sel.setAttribute('aria-label', 'Eklenecek ekip alanı');
            candidates.forEach(g => { const o = el('option', null, g.name + (g.open ? ' (herkes atanabilir)' : '')); o.value = g.id; sel.appendChild(o); });
            const btn = el('button', 'btn btn-sm btn-primary text-nowrap');
            btn.type = 'button';
            btn.appendChild(icon('plus'));
            btn.appendChild(document.createTextNode(' Ekle'));
            btn.addEventListener('click', () => { ensureGroup(parseInt(sel.value, 10)); markDirty(); render(); });
            row.append(sel, btn);
            addBox.appendChild(row);
            addBox.appendChild(el('div', 'small text-secondary mt-2', 'Örn. bu işte bir de mühendis çalışacaksa "Mühendis" alanını ekleyin.'));
        }
        groupsEl.appendChild(addBox);

        const tc = document.getElementById('teamCount');
        if (tc) tc.textContent = draft.size;
        const warn = document.getElementById('planWarnings');
        warn.querySelectorAll('.static').forEach(x => x.remove());
        if (!data.hasSupervisor)
            warn.appendChild(el('div', 'alert alert-warning py-2 px-3 mb-2 static', 'İşin sorumlu şefi seçilmemiş. Personele göndermeden önce işi düzenleyip şef seçin.'));
        if (draft.size > 0 && !shown.some(id => roleById.get(id).lead && [...draft.values()].some(t => t.roleId === id)))
            warn.appendChild(el('div', 'alert alert-light border py-2 px-3 mb-2 static', 'Bilgi: ekipte lider rolünde (ör. Şef) kimse yok.'));
    }

    if (poolList && planMode) {
        poolList.addEventListener('dragover', e => e.preventDefault());
        poolList.addEventListener('drop', e => {
            e.preventDefault(); highlight(null);
            const [kind, id] = (e.dataTransfer.getData('text/plain') || '').split(':');
            if (kind === 'team') remove(parseInt(id, 10));
        });
    }

    function render() { renderPool(); renderGroups(); }

    // ───────────── Kaydet ─────────────
    document.querySelectorAll('[data-save]').forEach(btn => btn.addEventListener('click', async () => {
        const send = btn.dataset.save === 'true';
        if (send && draft.size === 0) { toast('Göndermeden önce en az bir personel atayın.'); return; }
        if (send && !confirm('Atamalar kaydedilip iş personele gönderilecek ve bildirim gidecek. Devam edilsin mi?')) return;
        const buttons = document.querySelectorAll('[data-save]');
        buttons.forEach(b => b.disabled = true);
        try {
            const res = await fetch('/havuz/' + data.workId + '/kaydet', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json', 'Accept': 'application/json',
                    'RequestVerificationToken': document.querySelector('meta[name=csrf]').content
                },
                body: JSON.stringify({
                    send: send, version: data.version, groups: shown,
                    assignments: [...draft.values()].map(t => ({ employeeId: t.id, jobRoleId: t.roleId, taskNote: t.note || null }))
                })
            });
            const body = await res.json().catch(() => ({}));
            if (!res.ok) { toast(body.error || 'Kaydedilemedi. Sayfayı yenileyip tekrar deneyin.', 'danger'); window.scrollTo({ top: 0, behavior: 'smooth' }); return; }
            dirty = false;
            location.href = body.redirect;
        } catch (e) {
            toast('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.', 'danger');
        } finally {
            buttons.forEach(b => b.disabled = false);
        }
    }));

    window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

    render();
})();
