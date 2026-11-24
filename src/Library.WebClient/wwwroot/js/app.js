const API_BASE = '/api';

const STATUS_LABELS = {
  Pending: 'Pending · Chờ duyệt',
  Reserved: 'Reserved · Đã giữ sách',
  Borrowed: 'Borrowed · Đang mượn',
  Returned: 'Returned · Đã trả',
  Rejected: 'Rejected · Từ chối'
};

const PAPER_COVERS = [
  { bg: '#eee8dd', fg: '#3d3122' },
  { bg: '#e2ece9', fg: '#1b3b32' },
  { bg: '#e4eaf3', fg: '#1d3557' },
  { bg: '#f2e7e3', fg: '#592820' },
  { bg: '#eae7f1', fg: '#332b52' }
];

const state = {
  bookAvailOnly: false,
  userLoanMode: 'all',
  manageStatus: '',
  auditService: '',
  staffToken: localStorage.getItem('lib_staff_token') || '',
  staffName: localStorage.getItem('lib_staff_name') || '',
  members: [],
  books: [],
  userLoans: [],
  bookTimer: null,
  manageTimer: null,
  auditTimer: null,
  pollTimer: null
};

function esc(val) {
  if (val === null || val === undefined) return '';
  return String(val)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleString('vi-VN', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric'
  });
}

function initials(fullName) {
  if (!fullName) return 'BD';
  const words = fullName.trim().split(/\s+/);
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[words.length - 1][0]).toUpperCase();
}

function shortCoverTitle(title) {
  if (!title) return 'Sách';
  const words = title.trim().split(/\s+/).slice(0, 3).join(' ');
  return words.length > 20 ? words.slice(0, 18) + '…' : words;
}

function statusText(status) {
  return STATUS_LABELS[status] || status;
}

function authHeaders(extra = {}) {
  const h = { ...extra };
  if (state.staffToken) {
    h['Authorization'] = `Bearer ${state.staffToken}`;
  }
  return h;
}

function notify(html, isError = false) {
  const box = document.getElementById('toastBox');
  const el = document.createElement('div');
  el.className = 'toast' + (isError ? ' error' : '');
  el.innerHTML = html;
  box.appendChild(el);
  setTimeout(() => {
    el.style.opacity = '0';
    el.style.transition = 'opacity 0.2s';
    setTimeout(() => el.remove(), 220);
  }, 4300);
}

function copyId(value, label = 'Mã') {
  if (!value) return;
  navigator.clipboard?.writeText(value);
  notify(`Đã sao chép ${label}: <code>${esc(value)}</code>`);
}

const app = {
  switchTab(tab) {
    document.querySelectorAll('.view-pane').forEach(p => p.classList.remove('active'));
    document.querySelectorAll('.nav-tab').forEach(b => b.classList.remove('active'));
    document.getElementById(`pane-${tab}`)?.classList.add('active');
    document.getElementById(`tab-${tab}`)?.classList.add('active');

    if (tab === 'manage') this.loadAllOrders();
    if (tab === 'queues') this.loadQueuesAndLogs();
    if (tab === 'members') this.renderMembers();
  },

  openSheet(id) {
    document.getElementById('overlay').classList.add('open');
    document.getElementById(id)?.classList.add('open');
  },

  closeSheets() {
    document.getElementById('overlay').classList.remove('open');
    document.querySelectorAll('.sheet').forEach(s => s.classList.remove('open'));
  },

  renderStaffPill() {
    const btn = document.getElementById('staffBtn');
    if (state.staffToken) {
      btn.classList.add('logged-in');
      btn.textContent = `Thủ thư: ${state.staffName || 'Đã xác thực'} (Thoát)`;
    } else {
      btn.classList.remove('logged-in');
      btn.textContent = 'Thủ thư: Đăng nhập';
    }
  },

  handleStaffClick() {
    if (state.staffToken) {
      state.staffToken = '';
      state.staffName = '';
      localStorage.removeItem('lib_staff_token');
      localStorage.removeItem('lib_staff_name');
      this.renderStaffPill();
      notify('Đã đăng xuất phiên Thủ thư.');
    } else {
      this.openSheet('staffSheet');
    }
  },

  async submitStaffLogin(ev) {
    ev.preventDefault();
    const username = document.getElementById('fStaffUser').value.trim();
    const password = document.getElementById('fStaffPass').value;

    const res = await fetch(`${API_BASE}/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Đăng nhập thất bại'), true);
      return;
    }

    state.staffToken = data.token;
    state.staffName = data.username;
    localStorage.setItem('lib_staff_token', data.token);
    localStorage.setItem('lib_staff_name', data.username);
    this.renderStaffPill();
    this.closeSheets();
    notify(`Đã xác thực quyền <strong>${esc(data.displayName)}</strong> (Bearer Token).`);
  },

  async loadSummaryAndHealth() {
    try {
      const [healthRes, statsRes, qRes] = await Promise.all([
        fetch(`${API_BASE}/health`),
        fetch(`${API_BASE}/stats`),
        fetch(`${API_BASE}/queues`)
      ]);

      if (healthRes.ok) {
        const h = await healthRes.json();
        const dbUp = h.database === 'Connected';
        const mqUp = h.rabbitMq === 'Connected';
        document.getElementById('statusDb').innerHTML =
          `<span class="conn-dot ${dbUp ? 'ok' : 'err'}"></span>PostgreSQL: ${dbUp ? 'Ổn định' : 'Mất kết nối'}`;
        document.getElementById('statusMq').innerHTML =
          `<span class="conn-dot ${mqUp ? 'ok' : 'err'}"></span>RabbitMQ: ${mqUp ? 'Ổn định' : 'Mất kết nối'}`;
      }

      if (statsRes.ok) {
        const s = await statsRes.json();
        document.getElementById('sumBooks').textContent = s.totalBooks;
        document.getElementById('sumTotalCopies').textContent = s.totalCopies;
        document.getElementById('sumAvail').textContent = s.availableCopies;
        document.getElementById('sumActive').textContent = s.reservedReservations + s.borrowedReservations;

        const qWrap = document.getElementById('sumQueueWrap');
        if (s.pendingReservations > 0) {
          qWrap.innerHTML = `<span class="conn-dot busy"></span>Chờ duyệt: <strong>${s.pendingReservations}</strong>`;
        } else {
          qWrap.innerHTML = `Chờ duyệt: <strong>0</strong>`;
        }

        document.getElementById('badgeOrders').textContent = s.totalReservations;
        document.getElementById('badgeMembers').textContent = s.totalUsers;
      }

      if (qRes.ok) {
        const q = await qRes.json();
        document.getElementById('badgeDlq').textContent = q.deadLetterMessages ?? 0;
        document.getElementById('qMainCount').textContent = `${q.mainQueueMessages ?? 0} bản tin chờ`;
        document.getElementById('qConsumerCount').textContent =
          `Số tiến trình Worker đang lắng nghe: ${q.mainQueueConsumers ?? 0}`;
        document.getElementById('qDlqCount').textContent = `${q.deadLetterMessages ?? 0} bản tin lỗi`;
      }
    } catch (err) {
      console.error(err);
    }
  },

  async loadMembers(preferredId = null) {
    try {
      const res = await fetch(`${API_BASE}/users`);
      if (!res.ok) throw new Error();
      state.members = await res.json();

      const sel = document.getElementById('readerSelect');
      const prev = preferredId || sel.value;

      if (state.members.length === 0) {
        sel.innerHTML = '<option value="">Chưa có bạn đọc</option>';
        return;
      }

      sel.innerHTML = state.members.map(m =>
        `<option value="${esc(m.id)}">${esc(m.fullName)}</option>`
      ).join('');

      if (prev && state.members.some(m => m.id === prev)) {
        sel.value = prev;
      }

      this.syncActiveReaderInfo();
      this.renderMembers();
    } catch {
      notify('Không thể tải danh sách bạn đọc từ máy chủ.', true);
    }
  },

  syncActiveReaderInfo() {
    const id = document.getElementById('readerSelect').value;
    const member = state.members.find(m => m.id === id);
    document.getElementById('currentAvatar').textContent = initials(member?.fullName);
    document.getElementById('readerSubLabel').textContent = member
      ? `${member.fullName} · ${member.email || member.phoneNumber || 'Thẻ thư viện'}`
      : 'Vui lòng chọn bạn đọc';
    this.renderMembers();
  },

  onReaderChange() {
    this.syncActiveReaderInfo();
    this.loadUserLoans();
  },

  chooseMember(id) {
    document.getElementById('readerSelect').value = id;
    this.onReaderChange();
    this.switchTab('catalog');
    const m = state.members.find(x => x.id === id);
    if (m) notify(`Đang thao tác với thẻ của <strong>${esc(m.fullName)}</strong>.`);
  },

  renderMembers() {
    const grid = document.getElementById('memberGrid');
    if (!grid) return;
    const activeId = document.getElementById('readerSelect').value;

    if (state.members.length === 0) {
      grid.innerHTML = '<div class="empty-box">Chưa có bạn đọc nào được đăng ký.</div>';
      return;
    }

    grid.innerHTML = state.members.map(m => {
      const isCurrent = m.id === activeId;
      return `
        <div class="member-card ${isCurrent ? 'active' : ''}">
          <div style="display:flex;align-items:center;gap:12px;">
            <div class="reader-avatar" style="width:36px;height:36px;font-size:12px;">${esc(initials(m.fullName))}</div>
            <div>
              <div style="font-weight:600;font-size:14px;">${esc(m.fullName)}</div>
              <div style="font-size:12.5px;color:var(--text-muted);">${esc(m.email || 'Chưa cập nhật email')}</div>
            </div>
          </div>

          <div style="font-size:12px;color:var(--text-secondary);background:#fafaf8;padding:8px 10px;border-radius:6px;border:1px solid var(--border);">
            <div>Điện thoại: <strong>${esc(m.phoneNumber || '—')}</strong></div>
            <div class="mono" style="color:var(--text-muted);margin-top:2px;">Mã thẻ: ${esc(m.id)}</div>
          </div>

          <div style="display:flex;justify-content:space-between;align-items:center;gap:8px;">
            <button class="btn btn-sm ${isCurrent ? 'btn-outline' : 'btn-dark'}" onclick="app.chooseMember('${esc(m.id)}')">
              ${isCurrent ? 'Đang chọn thẻ này' : 'Chọn thẻ để đặt sách'}
            </button>
            <button class="text-action" onclick="copyId('${esc(m.id)}', 'Mã bạn đọc')">Sao chép mã</button>
          </div>
        </div>
      `;
    }).join('');
  },

  setBookAvailFilter(availOnly) {
    state.bookAvailOnly = availOnly;
    document.getElementById('filterBookAll').classList.toggle('active', !availOnly);
    document.getElementById('filterBookAvail').classList.toggle('active', availOnly);
    this.loadBooks();
  },

  debounceBooks() {
    clearTimeout(state.bookTimer);
    state.bookTimer = setTimeout(() => this.loadBooks(), 220);
  },

  async loadBooks() {
    try {
      const kw = document.getElementById('bookSearch').value.trim();
      const params = new URLSearchParams();
      if (kw) params.set('search', kw);
      if (state.bookAvailOnly) params.set('availableOnly', 'true');

      const res = await fetch(`${API_BASE}/books?${params.toString()}`);
      if (!res.ok) throw new Error();
      state.books = await res.json();
      document.getElementById('badgeBooks').textContent = state.books.length;

      const wrap = document.getElementById('bookCatalog');
      if (state.books.length === 0) {
        wrap.innerHTML = '<div class="empty-box">Không tìm thấy đầu sách phù hợp với từ khóa hiện tại.</div>';
        return;
      }

      wrap.innerHTML = state.books.map((b, i) => {
        const out = b.availableCopies <= 0;
        const low = b.availableCopies === 1;
        const cover = PAPER_COVERS[i % PAPER_COVERS.length];
        const stockCls = out ? 'empty' : (low ? 'low' : '');
        const stockLabel = out
          ? `Hết bản khả dụng (0/${b.totalCopies})`
          : `Khả dụng: ${b.availableCopies}/${b.totalCopies} quyển`;

        return `
          <article class="book-card">
            <div class="book-info-wrap">
              <div class="paper-cover" style="background:${cover.bg};color:${cover.fg};" onclick="app.openBookDetail('${esc(b.id)}')">
                <div class="paper-cover-bar"></div>
                <div class="paper-cover-label">${esc(shortCoverTitle(b.title))}</div>
              </div>

              <div style="min-width:0;flex:1;">
                <h3 class="book-heading" onclick="app.openBookDetail('${esc(b.id)}')">${esc(b.title)}</h3>
                <div class="book-byline">
                  ${esc(b.author)} &nbsp;·&nbsp; <span class="mono">ISBN ${esc(b.isbn || '—')}</span>
                </div>
                <p class="book-summary">${esc(b.description || '')}</p>
                <div class="book-footer-meta">
                  <span class="stock-text num ${stockCls}">${stockLabel}</span>
                  <button class="text-action" onclick="app.openBookDetail('${esc(b.id)}')">Chi tiết sách</button>
                  <button class="text-action" title="Gửi đồng thời 3 yêu cầu đặt sách để kiểm tra xử lý hàng đợi" onclick="app.reserveBurst('${esc(b.id)}', 3)">Gửi nhanh 3 phiếu</button>
                </div>
              </div>
            </div>

            <div class="book-cta">
              <button class="btn ${out ? 'btn-outline' : 'btn-primary'}" onclick="app.reserveSingle('${esc(b.id)}')">
                ${out ? 'Đặt thử (Hết sách)' : 'Đặt mượn sách'}
              </button>
            </div>
          </article>
        `;
      }).join('');
    } catch {
      document.getElementById('bookCatalog').innerHTML =
        '<div class="empty-box">Không tải được danh mục sách từ API.</div>';
    }
  },

  async openBookDetail(bookId) {
    try {
      const res = await fetch(`${API_BASE}/books/${bookId}`);
      if (!res.ok) throw new Error();
      const b = await res.json();

      const holdersHtml = (b.activeHolders && b.activeHolders.length > 0)
        ? b.activeHolders.map(h => `
            <div style="display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px solid var(--border);font-size:12.5px;">
              <span>${esc(h.userName)} (<button class="text-action mono" onclick="app.closeSheets();app.inspectOrder('${esc(h.id)}')">#${esc(h.id.slice(0, 8))}</button>)</span>
              <span class="badge badge-${esc(h.status)}">${esc(h.status)}</span>
            </div>
          `).join('')
        : '<div style="font-size:12.5px;color:var(--text-muted);">Hiện chưa có bạn đọc nào đang giữ hoặc mượn cuốn này.</div>';

      document.getElementById('detailSheetBody').innerHTML = `
        <div style="margin-bottom:16px;">
          <h4 style="margin:0 0 4px;font-size:16px;font-weight:700;">${esc(b.title)}</h4>
          <div style="font-size:13px;color:var(--text-muted);">
            Tác giả: <strong>${esc(b.author)}</strong> · <span class="mono">ISBN ${esc(b.isbn || '—')}</span>
          </div>
        </div>

        <p style="font-size:13.5px;color:var(--text-secondary);line-height:1.6;padding-bottom:16px;border-bottom:1px solid var(--border);">
          ${esc(b.description || 'Chưa có mô tả cho cuốn sách này.')}
        </p>

        <div class="num" style="display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin:16px 0;">
          <div style="border:1px solid var(--border);border-radius:8px;padding:12px;background:#fafaf8;">
            <div style="font-size:11.5px;color:var(--text-muted);">Khả dụng</div>
            <div style="font-size:19px;font-weight:700;color:var(--green-text);margin-top:2px;">${b.availableCopies}/${b.totalCopies}</div>
          </div>
          <div style="border:1px solid var(--border);border-radius:8px;padding:12px;background:#fafaf8;">
            <div style="font-size:11.5px;color:var(--text-muted);">Đang giữ chỗ</div>
            <div style="font-size:19px;font-weight:700;margin-top:2px;">${b.reservedCount ?? 0}</div>
          </div>
          <div style="border:1px solid var(--border);border-radius:8px;padding:12px;background:#fafaf8;">
            <div style="font-size:11.5px;color:var(--text-muted);">Đang mượn</div>
            <div style="font-size:19px;font-weight:700;margin-top:2px;">${b.borrowedCount ?? 0}</div>
          </div>
        </div>

        <div style="margin:16px 0;">
          <div style="font-size:12.5px;font-weight:600;margin-bottom:6px;">Phiếu đang giữ / mượn cuốn sách này</div>
          ${holdersHtml}
        </div>

        <div style="border:1px solid var(--border);border-radius:8px;padding:14px;margin-top:18px;">
          <label for="editCopiesInput" style="display:block;font-size:12.5px;font-weight:600;margin-bottom:8px;">
            Điều chỉnh tổng số bản của đầu sách
          </label>
          <div style="display:flex;gap:8px;">
            <input type="number" id="editCopiesInput" class="text-input plain num" value="${b.totalCopies}" min="1" max="500">
            <button type="button" class="btn btn-dark" onclick="app.saveBookCopies('${esc(b.id)}')">Lưu thay đổi</button>
          </div>
          <div style="display:flex;justify-content:space-between;align-items:center;margin-top:10px;font-size:12px;color:var(--text-muted);">
            <span class="mono">${esc(b.id)}</span>
            <button class="text-action" onclick="copyId('${esc(b.id)}', 'Mã sách')">Sao chép</button>
          </div>
        </div>
      `;
      this.openSheet('detailSheet');
    } catch {
      notify('Không tải được chi tiết đầu sách.', true);
    }
  },

  async saveBookCopies(bookId) {
    const totalCopies = parseInt(document.getElementById('editCopiesInput').value, 10);
    if (isNaN(totalCopies) || totalCopies < 1) return;

    const res = await fetch(`${API_BASE}/books/${bookId}`, {
      method: 'PUT',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({ totalCopies })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể cập nhật số lượng bản'), true);
      return;
    }
    this.closeSheets();
    notify(`Đã cập nhật tổng số lượng thành <strong>${totalCopies} quyển</strong>.`);
    await this.refreshAll();
  },

  async reserveSingle(bookId) {
    const userId = document.getElementById('readerSelect').value;
    if (!userId) {
      notify('Vui lòng chọn bạn đọc ở góc trên bên phải trước khi đặt sách.', true);
      return;
    }

    try {
      const res = await fetch(`${API_BASE}/reservations`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bookId, userId })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        notify(esc(data.message || 'Không gửi được yêu cầu đặt sách'), true);
        return;
      }

      document.getElementById('lookupInput').value = data.id;
      notify(`Đã xếp hàng <code>202 Accepted</code> · Phiếu <code>#${esc(data.id.slice(0, 8))}</code> đang được Worker kiểm tra...`);

      await Promise.all([this.loadUserLoans(), this.loadSummaryAndHealth()]);
      this.startPolling();
    } catch {
      notify('Lỗi kết nối tới máy chủ API.', true);
    }
  },

  async reserveBurst(bookId, count = 3) {
    const userId = document.getElementById('readerSelect').value;
    if (!userId) {
      notify('Vui lòng chọn bạn đọc trước khi gửi yêu cầu.', true);
      return;
    }

    try {
      const jobs = Array.from({ length: count }, () =>
        fetch(`${API_BASE}/reservations`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ bookId, userId })
        }).then(r => r.json())
      );

      const list = await Promise.all(jobs);
      notify(`Đã đẩy đồng thời <strong>${list.length} yêu cầu đặt mượn</strong> vào RabbitMQ.`);

      await Promise.all([this.loadUserLoans(), this.loadSummaryAndHealth()]);
      this.startPolling();
    } catch {
      notify('Không thể gửi yêu cầu đồng thời.', true);
    }
  },

  startPolling() {
    clearTimeout(state.pollTimer);
    let count = 0;

    const step = async () => {
      count++;
      await Promise.all([this.loadBooks(), this.loadSummaryAndHealth()]);
      const hasPending = await this.loadUserLoans();
      if (document.getElementById('pane-queues').classList.contains('active')) {
        await this.loadQueuesAndLogs();
      }
      if (hasPending && count < 12) {
        state.pollTimer = setTimeout(step, 1000);
      }
    };
    state.pollTimer = setTimeout(step, 850);
  },

  setUserLoanMode(btn) {
    document.querySelectorAll('#userLoanSwitch .pill-option').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.userLoanMode = btn.getAttribute('data-mode') || 'all';
    this.loadUserLoans();
  },

  async loadUserLoans() {
    const userId = document.getElementById('readerSelect').value;
    if (!userId) return false;

    try {
      const params = new URLSearchParams();
      if (state.userLoanMode === 'active') params.set('activeOnly', 'true');
      else if (state.userLoanMode !== 'all') params.set('status', state.userLoanMode);

      const qs = params.toString() ? `?${params.toString()}` : '';
      const res = await fetch(`${API_BASE}/users/${userId}/reservations${qs}`);
      if (!res.ok) throw new Error();
      const items = await res.json();
      state.userLoans = items;
      const box = document.getElementById('userLoanList');

      if (items.length === 0) {
        box.innerHTML = '<div class="empty-box">Chưa có phiếu đặt hoặc sách đang mượn trong bộ lọc này.</div>';
        return false;
      }

      box.innerHTML = items.map(r => `
        <div class="loan-item">
          <div class="loan-header">
            <span class="loan-book-title">${esc(r.bookTitle || 'Sách thư viện')}</span>
            <span class="badge badge-${esc(r.status)}">${esc(statusText(r.status))}</span>
          </div>
          <div class="loan-note">${esc(r.reasonMessage || 'Đang chờ Worker kiểm tra số bản khả dụng...')}</div>
          <div class="loan-meta">
            <span>
              Mã phiếu:
              <button class="text-action mono" onclick="app.inspectOrder('${esc(r.id)}')">#${esc(r.id.slice(0, 8))}</button>
            </span>
            <span class="num">${formatTime(r.createdAt)}</span>
          </div>
          ${this.renderLoanButtons(r)}
        </div>
      `).join('');

      return items.some(x => x.status === 'Pending');
    } catch {
      return false;
    }
  },

  renderLoanButtons(r) {
    const btns = [];
    if (r.status === 'Reserved') {
      btns.push(`<button class="btn btn-primary btn-sm" onclick="app.changeStatus('${esc(r.id)}', 'Borrowed')">Nhận sách (Borrowed)</button>`);
    }
    if (r.status === 'Borrowed') {
      btns.push(`<button class="btn btn-outline btn-sm" onclick="app.changeStatus('${esc(r.id)}', 'Returned')">Trả sách (Returned)</button>`);
    }
    if (r.status === 'Pending' || r.status === 'Reserved') {
      btns.push(`<button class="btn btn-danger-quiet btn-sm" onclick="app.cancelOrder('${esc(r.id)}')">Hủy đặt</button>`);
    }
    return btns.length ? `<div class="loan-actions">${btns.join('')}</div>` : '';
  },

  async changeStatus(id, newStatus) {
    const res = await fetch(`${API_BASE}/reservations/${id}/status`, {
      method: 'PUT',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({ newStatus })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể cập nhật trạng thái phiếu'), true);
      return;
    }
    notify(`Đã cập nhật trạng thái sang <strong>${esc(statusText(data.status))}</strong>.`);
    await this.refreshAll();
  },

  async cancelOrder(id) {
    const res = await fetch(`${API_BASE}/reservations/${id}/cancel`, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify({ reason: 'Hủy phiếu từ giao diện Web.' })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể hủy phiếu'), true);
      return;
    }
    notify('Đã hủy phiếu đặt sách và hoàn trả lại số bản khả dụng.');
    await this.refreshAll();
  },

  setManageStatus(btn) {
    document.querySelectorAll('#manageStatusSwitch .pill-option').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.manageStatus = btn.getAttribute('data-status') || '';
    this.loadAllOrders();
  },

  debounceManage() {
    clearTimeout(state.manageTimer);
    state.manageTimer = setTimeout(() => this.loadAllOrders(), 220);
  },

  async loadAllOrders() {
    try {
      const kw = document.getElementById('manageSearch').value.trim();
      const params = new URLSearchParams();
      if (state.manageStatus) params.set('status', state.manageStatus);
      if (kw) params.set('search', kw);

      const res = await fetch(`${API_BASE}/reservations?${params.toString()}`);
      if (!res.ok) throw new Error();
      const rows = await res.json();
      const tbody = document.getElementById('manageTableBody');

      if (rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" class="empty-box">Không có phiếu đặt mượn nào phù hợp.</td></tr>';
        return;
      }

      tbody.innerHTML = rows.map(r => `
        <tr>
          <td>
            <button class="text-action mono" onclick="app.inspectOrder('${esc(r.id)}')">#${esc(r.id.slice(0, 8))}</button>
            <div class="mono" style="font-size:11px;color:var(--text-muted);cursor:pointer;" onclick="copyId('${esc(r.correlationId || '')}', 'Correlation ID')">${esc((r.correlationId || '—').slice(0, 14))}</div>
          </td>
          <td>
            <div style="font-weight:500;">${esc(r.userName)}</div>
            <div style="font-size:12px;color:var(--text-muted);">${esc(r.userPhone || '')}</div>
          </td>
          <td style="font-weight:500;">${esc(r.bookTitle)}</td>
          <td><span class="badge badge-${esc(r.status)}">${esc(r.status)}</span></td>
          <td style="color:var(--text-secondary);max-width:260px;">${esc(r.reasonMessage || '')}</td>
          <td class="num" style="color:var(--text-muted);white-space:nowrap;">${formatTime(r.createdAt)}</td>
          <td style="text-align:right;white-space:nowrap;">
            ${r.status === 'Reserved' ? `<button class="btn btn-primary btn-sm" onclick="app.changeStatus('${esc(r.id)}', 'Borrowed')">Giao sách</button>` : ''}
            ${r.status === 'Borrowed' ? `<button class="btn btn-outline btn-sm" onclick="app.changeStatus('${esc(r.id)}', 'Returned')">Nhận trả</button>` : ''}
            ${(r.status === 'Pending' || r.status === 'Reserved') ? `<button class="btn btn-danger-quiet btn-sm" onclick="app.cancelOrder('${esc(r.id)}')">Hủy</button>` : ''}
          </td>
        </tr>
      `).join('');
    } catch (err) {
      console.error(err);
    }
  },

  // Tab 3: Queue Telemetry, Distributed Labs & Audit Logs
  setAuditService(btn) {
    document.querySelectorAll('#auditServiceSwitch .pill-option').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.auditService = btn.getAttribute('data-svc') || '';
    this.loadQueuesAndLogs();
  },

  debounceAudit() {
    clearTimeout(state.auditTimer);
    state.auditTimer = setTimeout(() => this.loadQueuesAndLogs(), 220);
  },

  async loadQueuesAndLogs() {
    await this.loadSummaryAndHealth();
    try {
      const kw = document.getElementById('auditSearch').value.trim();
      const params = new URLSearchParams({ limit: '50' });
      if (state.auditService) params.set('service', state.auditService);
      if (kw) params.set('search', kw);

      const res = await fetch(`${API_BASE}/audit-logs?${params.toString()}`);
      if (!res.ok) throw new Error();
      const logs = await res.json();
      const tbody = document.getElementById('auditTableBody');

      if (logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" class="empty-box">Chưa có bản ghi nhật ký nào. Hãy đặt sách hoặc bấm một thực nghiệm ở trên.</td></tr>';
        return;
      }

      tbody.innerHTML = logs.map(l => `
        <tr>
          <td class="num" style="white-space:nowrap;color:var(--text-muted);">${formatTime(l.createdAt)}</td>
          <td><span class="service-tag ${l.serviceName === 'Library.Worker' ? 'worker' : ''}">${esc(l.serviceName)}</span></td>
          <td class="mono" style="font-weight:600;">${esc(l.eventType)}</td>
          <td class="mono">
            ${l.correlationId
              ? `<button class="text-action mono" onclick="copyId('${esc(l.correlationId)}', 'Correlation ID')">${esc(l.correlationId.slice(0, 14))}</button>`
              : '—'}
          </td>
          <td>
            ${esc(l.message)}
            ${l.reservationId ? `<button class="text-action mono" style="margin-left:6px;" onclick="app.inspectOrder('${esc(l.reservationId)}')">Xem phiếu</button>` : ''}
          </td>
        </tr>
      `).join('');
    } catch (err) {
      console.error(err);
    }
  },

  async runLabBurst() {
    const targetBook = state.books.find(b => b.availableCopies === 1) || state.books[0];
    if (!targetBook) {
      notify('Chưa có đầu sách nào để chạy thực nghiệm.', true);
      return;
    }
    await this.reserveBurst(targetBook.id, 5);
    setTimeout(() => this.loadQueuesAndLogs(), 1400);
  },

  async runLabIdempotent() {
    const resList = await fetch(`${API_BASE}/reservations`).then(r => r.json()).catch(() => []);
    const processed = resList.find(r => r.status !== 'Pending');
    if (!processed) {
      notify('Hãy đặt ít nhất 1 cuốn sách trước để có phiếu đã duyệt dùng kiểm thử Idempotency.', true);
      return;
    }

    const res = await fetch(`${API_BASE}/diagnostics/republish/${processed.id}`, { method: 'POST' });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể phát lặp lại bản tin'), true);
      return;
    }

    notify(`Đã phát lặp lại bản tin của phiếu <code>#${esc(processed.id.slice(0, 8))}</code>. Kiểm tra sự kiện <code>IdempotentSkipped</code> bên dưới!`);
    setTimeout(() => this.loadQueuesAndLogs(), 1100);
  },

  async runLabPoison() {
    const res = await fetch(`${API_BASE}/diagnostics/poison-message`, { method: 'POST' });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không gửi được gói tin lỗi'), true);
      return;
    }

    notify(`Đã gửi gói tin hỏng định dạng (Trace <code>${esc(data.correlationId)}</code>). Bộ đếm Dead Letter Queue sẽ tăng sau ~1 giây!`);
    setTimeout(() => this.loadQueuesAndLogs(), 1100);
  },

  inspectOrder(id) {
    document.getElementById('lookupInput').value = id;
    this.switchTab('lookup');
    this.lookupOrder();
  },

  async lookupOrder() {
    const id = document.getElementById('lookupInput').value.trim();
    const out = document.getElementById('lookupResult');
    if (!id) {
      notify('Vui lòng nhập mã GUID của phiếu cần tra cứu.', true);
      return;
    }

    try {
      const res = await fetch(`${API_BASE}/reservations/${encodeURIComponent(id)}`);
      if (!res.ok) {
        out.innerHTML = '<div class="empty-box" style="color:var(--red-text);background:#fff5f5;border:1px solid #fecaca;border-radius:10px;">Không tìm thấy phiếu đặt mượn với mã GUID đã nhập.</div>';
        return;
      }

      const d = await res.json();
      const rejected = d.status === 'Rejected';

      const trailHtml = (d.auditTrail && d.auditTrail.length > 0)
        ? `
          <div style="margin-top:18px;">
            <div style="font-size:13px;font-weight:600;margin-bottom:8px;">Nhật ký xử lý qua các thành phần (Audit Trail)</div>
            <div style="border:1px solid var(--border);border-radius:8px;overflow:hidden;">
              ${d.auditTrail.map(a => `
                <div style="padding:9px 14px;border-bottom:1px solid var(--border);display:flex;flex-wrap:wrap;justify-content:space-between;gap:8px;font-size:12.5px;">
                  <div>
                    <span class="service-tag ${a.serviceName === 'Library.Worker' ? 'worker' : ''}">${esc(a.serviceName)}</span>
                    <strong class="mono" style="margin:0 6px;">${esc(a.eventType)}</strong>
                    <span>${esc(a.message)}</span>
                  </div>
                  <span class="num" style="color:var(--text-muted);font-size:11.5px;">${formatTime(a.createdAt)}</span>
                </div>
              `).join('')}
            </div>
          </div>
        `
        : '';

      out.innerHTML = `
        <div style="border:1px solid var(--border);border-radius:12px;padding:20px;background:var(--bg-card);">
          <div style="display:flex;flex-wrap:wrap;justify-content:space-between;align-items:flex-start;gap:12px;padding-bottom:14px;border-bottom:1px solid var(--border);">
            <div>
              <h3 style="margin:0;font-size:16px;font-weight:700;">${esc(d.bookTitle)}</h3>
              <div style="font-size:13px;color:var(--text-muted);margin-top:3px;">
                Bạn đọc: <strong>${esc(d.userName)}</strong> (${esc(d.userEmail || 'Chưa có email')})
              </div>
            </div>
            <span class="badge badge-${esc(d.status)}">${esc(statusText(d.status))}</span>
          </div>

          <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:14px;margin-top:14px;font-size:12.5px;">
            <div>
              <span style="color:var(--text-muted);">Mã định danh phiếu (Reservation ID):</span>
              <div class="mono" style="margin-top:3px;">
                ${esc(d.id)}
                <button class="text-action" style="margin-left:6px;" onclick="copyId('${esc(d.id)}', 'Mã phiếu')">Sao chép</button>
              </div>
            </div>
            <div>
              <span style="color:var(--text-muted);">Mã truy vết (Correlation ID):</span>
              <div class="mono" style="margin-top:3px;">
                ${esc(d.correlationId || '—')}
                ${d.correlationId ? `<button class="text-action" style="margin-left:6px;" onclick="copyId('${esc(d.correlationId)}', 'Correlation ID')">Sao chép</button>` : ''}
              </div>
            </div>
            <div>
              <span style="color:var(--text-muted);">Kết quả xử lý:</span>
              <div style="margin-top:3px;font-weight:500;">${esc(d.reasonMessage || 'Đang chờ xử lý...')}</div>
            </div>
          </div>

          <div class="steps-row num">
            <div class="step-box completed">
              <div class="step-label">Bước 1 · Library.API</div>
              <div class="step-name">Tiếp nhận (Pending)</div>
              <div class="step-ts">${formatTime(d.createdAt)}</div>
            </div>

            <div class="step-box ${rejected ? 'failed' : (d.processedAt ? 'completed' : '')}">
              <div class="step-label">Bước 2 · Library.Worker</div>
              <div class="step-name">${rejected ? 'Từ chối / Đã hủy' : 'Giữ sách (Reserved)'}</div>
              <div class="step-ts">${formatTime(d.processedAt)}</div>
            </div>

            <div class="step-box ${d.borrowedAt ? 'completed' : ''}">
              <div class="step-label">Bước 3 · Quầy thư viện</div>
              <div class="step-name">Nhận sách (Borrowed)</div>
              <div class="step-ts">${formatTime(d.borrowedAt)}</div>
            </div>

            <div class="step-box ${d.returnedAt ? 'completed' : ''}">
              <div class="step-label">Bước 4 · Hoàn tất</div>
              <div class="step-name">Trả sách (Returned)</div>
              <div class="step-ts">${formatTime(d.returnedAt)}</div>
            </div>
          </div>

          ${trailHtml}

          <details style="margin-top:16px;">
            <summary style="cursor:pointer;font-size:12.5px;color:var(--text-secondary);font-weight:500;">Xem dữ liệu JSON gốc từ máy chủ</summary>
            <pre class="json-box mono">${esc(JSON.stringify(d, null, 2))}</pre>
          </details>
        </div>
      `;
    } catch {
      notify('Mã GUID không hợp lệ hoặc mất kết nối.', true);
    }
  },

  async submitBook(ev) {
    ev.preventDefault();
    const payload = {
      title: document.getElementById('fBookTitle').value.trim(),
      author: document.getElementById('fBookAuthor').value.trim(),
      isbn: document.getElementById('fBookIsbn').value.trim(),
      totalCopies: parseInt(document.getElementById('fBookCopies').value, 10) || 1,
      description: document.getElementById('fBookDesc').value.trim()
    };

    const res = await fetch(`${API_BASE}/books`, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(payload)
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể lưu đầu sách mới'), true);
      return;
    }

    document.getElementById('bookForm').reset();
    document.getElementById('fBookCopies').value = '2';
    this.closeSheets();
    notify(`Đã thêm đầu sách <strong>${esc(data.title)}</strong> vào kho.`);
    await this.refreshAll();
  },

  async submitMember(ev) {
    ev.preventDefault();
    const payload = {
      fullName: document.getElementById('fMemberName').value.trim(),
      email: document.getElementById('fMemberEmail').value.trim(),
      phoneNumber: document.getElementById('fMemberPhone').value.trim()
    };

    const res = await fetch(`${API_BASE}/users`, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: JSON.stringify(payload)
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      notify(esc(data.message || 'Không thể tạo thẻ bạn đọc'), true);
      return;
    }

    document.getElementById('memberForm').reset();
    this.closeSheets();
    notify(`Đã tạo thẻ bạn đọc cho <strong>${esc(data.fullName)}</strong>.`);
    await this.loadMembers(data.id);
    await Promise.all([this.loadUserLoans(), this.loadSummaryAndHealth()]);
  },

  async refreshAll() {
    await Promise.all([this.loadSummaryAndHealth(), this.loadBooks(), this.loadUserLoans()]);
    if (document.getElementById('pane-manage').classList.contains('active')) {
      await this.loadAllOrders();
    }
    if (document.getElementById('pane-queues').classList.contains('active')) {
      await this.loadQueuesAndLogs();
    }
  },

  async init() {
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') this.closeSheets();
    });
    this.renderStaffPill();
    await this.loadMembers();
    await Promise.all([this.loadSummaryAndHealth(), this.loadBooks(), this.loadUserLoans()]);
  }
};

window.app = app;
window.addEventListener('DOMContentLoaded', () => app.init());
