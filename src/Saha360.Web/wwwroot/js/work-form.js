// Yeni iş formu: açılır/kapanır malzeme paneli. Aynı malzeme tekrar eklenirse miktar birleştirilir.
(function () {
    'use strict';
    const sel = document.getElementById('matSelect');
    if (!sel) return;
    const qty = document.getElementById('matQty');
    const unitEl = document.getElementById('matUnit');
    const table = document.getElementById('matTable');
    const tbody = table.querySelector('tbody');
    const err = document.getElementById('matError');
    const count = document.getElementById('matCount');
    const options = [...document.querySelectorAll('#matList option')];
    const lines = new Map(); // id -> { name, unit, qty }

    const find = () => options.find(o => o.value === sel.value);
    const parse = s => { s = (s || '').trim().replace(/\s/g, ''); if (s.includes(',') && !s.includes('.')) s = s.replace(',', '.'); return /^\d+(\.\d{1,3})?$/.test(s) ? parseFloat(s) : NaN; };
    const fmt = n => n.toLocaleString('tr-TR', { maximumFractionDigits: 3 });

    sel.addEventListener('input', () => { const o = find(); unitEl.textContent = o ? '(' + o.dataset.unit + ')' : ''; });

    function render() {
        tbody.textContent = '';
        lines.forEach((l, id) => {
            const tr = document.createElement('tr');
            const td = t => { const c = document.createElement('td'); c.textContent = t; return c; };
            tr.append(td(l.name));
            const q = td(fmt(l.qty)); q.className = 'text-end'; tr.append(q);
            tr.append(td(l.unit));
            const act = document.createElement('td');
            const rm = document.createElement('button');
            rm.type = 'button'; rm.className = 'btn btn-sm btn-outline-danger'; rm.textContent = 'Çıkar';
            rm.addEventListener('click', () => { lines.delete(id); render(); });
            const h1 = document.createElement('input'); h1.type = 'hidden'; h1.name = 'materialId'; h1.value = id;
            const h2 = document.createElement('input'); h2.type = 'hidden'; h2.name = 'materialQty'; h2.value = String(l.qty);
            act.append(rm, h1, h2); act.className = 'text-end';
            tr.append(act);
            tbody.append(tr);
        });
        table.hidden = lines.size === 0;
        count.textContent = lines.size;
    }

    document.getElementById('matAdd').addEventListener('click', () => {
        err.textContent = '';
        const o = find();
        if (!o) { err.textContent = 'Listeden bir malzeme seçin.'; return; }
        const n = parse(qty.value);
        if (!(n > 0)) { err.textContent = 'Miktar sıfırdan büyük bir sayı olmalı (en fazla 3 ondalık).'; return; }
        const id = o.dataset.id;
        const cur = lines.get(id);
        if (cur) cur.qty = Math.round((cur.qty + n) * 1000) / 1000;
        else lines.set(id, { name: o.value.split(' · ').slice(1).join(' · ') || o.value, unit: o.dataset.unit, qty: n });
        sel.value = ''; qty.value = ''; unitEl.textContent = '';
        render();
        sel.focus();
    });
    qty.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); document.getElementById('matAdd').click(); } });
})();
