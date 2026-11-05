// Ortak davranışlar: onay soruları, sekme bağlantıları, fotoğraf büyütme, çift gönderim koruması.
(function () {
    'use strict';

    // Geri alınması zor eylemlerde onay iste (data-confirm="Soru").
    document.addEventListener('submit', e => {
        const form = e.target;
        const msg = form.getAttribute('data-confirm');
        if (msg && !confirm(msg)) { e.preventDefault(); return; }
        // Aynı formun iki kez gönderilmesini engelle (sunucu da yinelenen işlemi uygulamaz).
        if (form.method.toLowerCase() === 'post' && !e.defaultPrevented) {
            setTimeout(() => form.querySelectorAll('button[type=submit]').forEach(b => b.disabled = true), 0);
        }
    });
    document.addEventListener('click', e => {
        const a = e.target.closest('a[data-confirm]');
        if (a && !confirm(a.getAttribute('data-confirm'))) e.preventDefault();
    });

    // Sekmeler: adres çubuğundaki #malzemeler gibi bağlantılar ilgili sekmeyi açar.
    function openFromHash() {
        const h = location.hash.replace('#', '');
        if (!h) return;
        const modalMap = { reddet: 'rejectModal', tamamla: 'submitModal' };
        if (modalMap[h] && document.getElementById(modalMap[h]) && window.bootstrap) {
            bootstrap.Modal.getOrCreateInstance(document.getElementById(modalMap[h])).show();
            return;
        }
        const btn = document.querySelector('[data-bs-target="#' + CSS.escape(h) + '"][data-bs-toggle=tab]');
        if (btn && window.bootstrap) bootstrap.Tab.getOrCreateInstance(btn).show();
    }
    document.querySelectorAll('[data-bs-toggle=tab]').forEach(b => b.addEventListener('shown.bs.tab', () => {
        history.replaceState(null, '', b.getAttribute('data-bs-target'));
    }));
    window.addEventListener('load', openFromHash);

    // Fotoğrafı büyüt
    document.addEventListener('click', e => {
        const a = e.target.closest('a[data-lightbox]');
        if (!a || !window.bootstrap) return;
        e.preventDefault();
        let m = document.getElementById('lightbox');
        if (!m) {
            m = document.createElement('div');
            m.className = 'modal fade lightbox';
            m.id = 'lightbox';
            m.tabIndex = -1;
            m.innerHTML = '<div class="modal-dialog modal-xl modal-dialog-centered"><div class="modal-content"><div class="modal-header py-2"><span class="small cap"></span><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Kapat"></button></div><div class="modal-body p-2"><img alt=""></div><div class="modal-footer py-1"><a class="btn btn-sm btn-outline-secondary dl" target="_blank" rel="noopener">Tam boyut</a></div></div></div>';
            document.body.appendChild(m);
        }
        m.querySelector('img').src = a.href;
        m.querySelector('img').alt = a.dataset.caption || 'Fotoğraf';
        m.querySelector('.cap').textContent = a.dataset.caption || '';
        m.querySelector('.dl').href = a.href;
        bootstrap.Modal.getOrCreateInstance(m).show();
    });

    // İşaret kutusuna / seçime bağlı açılan alanlar:
    //   <input type=checkbox data-toggle-target="#alan">  ·  <select data-show-when="değer" data-show-target="#alan">
    document.querySelectorAll('[data-toggle-target]').forEach(cb => cb.addEventListener('change', () => {
        const t = document.querySelector(cb.dataset.toggleTarget);
        if (t) t.hidden = !cb.checked;
    }));
    document.querySelectorAll('[data-show-target]').forEach(sel => sel.addEventListener('change', () => {
        const t = document.querySelector(sel.dataset.showTarget);
        if (t) t.hidden = sel.value !== sel.dataset.showWhen;
    }));

    // Listede arama (data-filter-list) ve seçili sayısı (data-count-for)
    document.querySelectorAll('[data-filter-list]').forEach(inp => inp.addEventListener('input', () => {
        const q = inp.value.trim().toLocaleLowerCase('tr-TR');
        document.querySelectorAll(inp.dataset.filterList + ' [data-text]').forEach(row => {
            row.hidden = q.length > 0 && !row.dataset.text.includes(q);
        });
    }));
    document.querySelectorAll('[data-count-for]').forEach(label => {
        const list = document.querySelector(label.dataset.countFor);
        if (!list) return;
        const update = () => { label.textContent = list.querySelectorAll('input[type=checkbox]:checked').length + ' seçili'; };
        list.addEventListener('change', update);
        update();
    });

    // Seçilen fotoğrafların küçük önizlemesi (data-preview="#kutu")
    document.querySelectorAll('input[type=file][data-preview]').forEach(inp => inp.addEventListener('change', () => {
        const box = document.querySelector(inp.dataset.preview);
        if (!box) return;
        box.textContent = '';
        [...inp.files].forEach(f => {
            if (f.type.startsWith('image/')) {
                const img = document.createElement('img');
                img.src = URL.createObjectURL(f); img.alt = f.name; img.onload = () => URL.revokeObjectURL(img.src);
                box.appendChild(img);
            } else {
                const s = document.createElement('span'); s.className = 'badge text-bg-light border'; s.textContent = f.name; box.appendChild(s);
            }
        });
    }));

    // Yükleme öncesi istemci tarafı boyut uyarısı (asıl kontrol sunucuda).
    document.querySelectorAll('input[type=file]').forEach(inp => inp.addEventListener('change', () => {
        const big = [...inp.files].filter(f => f.size > 10 * 1024 * 1024);
        if (big.length) { alert(big.map(f => f.name).join(', ') + ': dosya 10 MB sınırını aşıyor.'); inp.value = ''; }
    }));
})();
