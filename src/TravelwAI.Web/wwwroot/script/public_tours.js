let publicTours = [];
let publicTourSearchQuery = "";
let tourOfferStatus = { progress: 0, target: 5, discount_percent: 0, invites: [] };
let tourOfferRefreshTimer = null;

function money(value) {
  return Number(value || 0).toLocaleString("vi-VN") + "đ";
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function showToast(message) {
  const toast = document.getElementById("tourToast");
  if (!toast) return alert(message);
  toast.textContent = message;
  toast.classList.add("show");
  setTimeout(() => toast.classList.remove("show"), 2600);
}

async function readJson(response) {
  if (!response) throw new Error("Không có phản hồi từ máy chủ");
  const data = await response.json().catch(() => ({}));
  if (!response.ok || data.success === false) throw new Error(data.message || data.detail || "Thao tác thất bại");
  return data;
}

function getValue(item, ...keys) {
  for (const key of keys) {
    if (item && item[key] !== undefined && item[key] !== null && item[key] !== "") return item[key];
  }
  return "";
}

function normalizeDateValue(value) {
  if (!value) return "";
  const text = String(value);
  if (/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
  const d = new Date(text);
  if (Number.isNaN(d.getTime())) return text.split("T")[0] || "";
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function formatDateOnly(value) {
  const normalized = normalizeDateValue(value);
  if (!normalized) return "";
  const parts = normalized.split("-");
  if (parts.length === 3) return `${parts[2]}/${parts[1]}/${parts[0]}`;
  return normalized;
}

function tourDateRange(tour) {
  const start = formatDateOnly(getValue(tour, "start_date", "startDate"));
  const end = formatDateOnly(getValue(tour, "end_date", "endDate"));
  if (start && end) return `${start} → ${end}`;
  return start || end || "Đang cập nhật ngày đi";
}

function normalizeSearchText(value) {
  return String(value ?? "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/\s+/g, " ")
    .trim();
}

function getPublicTourSalesName(tour) {
  return getValue(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName", "seller_name", "sellerName") || "Tour Sales TravelwAI";
}

function getPublicTourSalesLine(tour) {
  return getPublicTourSalesName(tour);
}

function getPublicTourLine(tour) {
  return [getValue(tour, "destination") || "Việt Nam", getValue(tour, "duration")].filter(Boolean).join(" · ");
}

function tourSearchText(tour) {
  return normalizeSearchText([
    tour?.name,
    tour?.destination,
    tour?.description,
    getPublicTourSalesName(tour),
    getValue(tour, "duration"),
    tourDateRange(tour),
    tour?.price
  ].join(" "));
}

function filteredPublicTours() {
  const query = normalizeSearchText(publicTourSearchQuery);
  if (!query) return publicTours;
  return publicTours.filter(tour => tourSearchText(tour).includes(query));
}

function currentTourDiscountPercent() {
  return Math.max(0, Math.min(25, Number(tourOfferStatus?.discount_percent || 0)));
}

function discountedTourPrice(price) {
  const discount = currentTourDiscountPercent();
  const value = Number(price || 0);
  if (!discount || value <= 0) return value;
  return Math.max(0, Math.round(value * (100 - discount) / 100));
}

function renderTourPrice(price) {
  const discount = currentTourDiscountPercent();
  const original = Number(price || 0);
  const discounted = discountedTourPrice(original);
  if (!discount || discounted >= original) return `<span class="tour-price">${money(original)}</span>`;
  return `
    <span class="tour-price tour-price-discounted">
      <small>${money(original)}</small>
      <strong>${money(discounted)}</strong>
      <em>-${discount}%</em>
    </span>`;
}

function renderPublicTours() {
  const grid = document.getElementById("publicToursGrid");
  if (!grid) return;

  const tours = filteredPublicTours();
  if (!publicTours.length) {
    grid.innerHTML = '<div class="management-panel">Chưa có tour đang bán.</div>';
    return;
  }

  if (!tours.length) {
    grid.innerHTML = '<div class="management-panel">Không tìm thấy tour phù hợp.</div>';
    return;
  }

  grid.innerHTML = tours.map(t => {
    const id = t.id || "";
    const image = t.image || "";
    const sold = Number(t.sold || 0);
    const slots = Number(t.slots || 0);
    const available = Number(t.available ?? Math.max(0, slots - sold));
    const disabled = slots > 0 && available <= 0;
    return `
      <article class="public-tour-card">
        <div class="public-tour-image">${image ? `<img loading="lazy" decoding="async" src="${escapeHtml(image)}" alt="${escapeHtml(t.name || 'Tour')}" />` : '✈️'}</div>
        <div class="public-tour-body">
          <h3>${escapeHtml(t.name || 'Tour du lịch')}</h3>
          <div class="public-tour-sales-line">${escapeHtml(getPublicTourSalesLine(t))}</div>
          <div class="public-tour-subtour-line">${escapeHtml(getPublicTourLine(t))}</div>
          <div class="public-tour-info-row public-tour-date-only">
            <span>${escapeHtml(tourDateRange(t))}</span>
          </div>
          <div class="public-tour-book-row">
            <span class="tour-seat-line">Còn ${available}/${slots} chỗ</span>
            ${renderTourPrice(t.price)}
            <button class="btn-primary" type="button" ${disabled ? 'disabled' : ''} onclick="openPublicBookModal('${escapeHtml(id)}')">${disabled ? 'Hết chỗ' : 'Đặt tour'}</button>
          </div>
        </div>
      </article>`;
  }).join("");
}

async function loadPublicTours() {
  const grid = document.getElementById("publicToursGrid");
  if (grid) grid.innerHTML = '<div class="management-panel">Đang tải tour...</div>';
  try {
    const response = await authenticatedFetch("/api/tours");
    const result = await readJson(response);
    publicTours = Array.isArray(result.data) ? result.data : [];
    renderPublicTours();
  } catch (error) {
    if (grid) grid.innerHTML = `<div class="management-panel">${escapeHtml(error.message)}</div>`;
  }
}

function setupPublicTourSearch() {
  const input = document.getElementById("publicTourSearch");
  const clearButton = document.getElementById("clearPublicTourSearch");
  if (!input) return;
  input.addEventListener("input", () => {
    publicTourSearchQuery = input.value || "";
    renderPublicTours();
  });
  clearButton?.addEventListener("click", () => {
    input.value = "";
    publicTourSearchQuery = "";
    input.focus();
    renderPublicTours();
  });
}

function renderPublicBookTourDetails(tour) {
  const box = document.getElementById("publicBookTourDetails");
  if (!box) return;
  if (!tour) {
    box.innerHTML = '<div class="public-book-tour-content"><h3>Không tìm thấy thông tin tour</h3></div>';
    return;
  }

  const image = getValue(tour, "image", "image_url", "imageUrl");
  const destination = getValue(tour, "destination") || "Đang cập nhật điểm đến";
  const duration = getValue(tour, "duration") || "Đang cập nhật thời lượng";
  const sold = Number(getValue(tour, "sold") || 0);
  const slots = Number(getValue(tour, "slots") || 0);
  const available = Number(tour?.available ?? Math.max(0, slots - sold));
  const discount = currentTourDiscountPercent();

  box.innerHTML = `
    <div class="public-book-tour-thumb">
      ${image ? `<img loading="lazy" decoding="async" src="${escapeHtml(image)}" alt="${escapeHtml(tour?.name || 'Tour du lịch')}" />` : '✈️'}
    </div>
    <div class="public-book-tour-content">
      <h3>${escapeHtml(tour?.name || 'Tour du lịch')}</h3>
      <div class="public-book-sales-name">${escapeHtml(getPublicTourSalesName(tour))}</div>
      <div class="public-book-tour-meta">
        <span title="Điểm đến">${escapeHtml(destination)}</span>
        <span title="Thời lượng">${escapeHtml(duration)}</span>
        <span title="Thời gian">${escapeHtml(tourDateRange(tour))}</span>
        <span class="public-book-tour-price" title="Giá tour">${discount ? `${money(discountedTourPrice(tour?.price))} · giảm ${discount}%` : money(tour?.price)}</span>
        <span title="Số chỗ còn lại">Còn ${available}/${slots} chỗ</span>
      </div>
    </div>
  `;
}

function openPublicBookModal(id) {
  const tour = publicTours.find((item) => String(item?.id || "") === String(id));
  document.getElementById("bookTourId").value = id;
  document.getElementById("bookName").value = localStorage.getItem("username") || "";
  document.getElementById("bookEmail").value = localStorage.getItem("userEmail") || "";
  document.getElementById("bookQuantity").value = 1;
  renderPublicBookTourDetails(tour);
  document.getElementById("publicBookModal")?.classList.add("open");
}

function closePublicBookModal() {
  document.getElementById("publicBookModal")?.classList.remove("open");
}

async function submitPublicBooking(event) {
  event.preventDefault();
  const id = document.getElementById("bookTourId").value;
  const payload = {
    customerName: document.getElementById("bookName").value.trim(),
    customerEmail: document.getElementById("bookEmail").value.trim(),
    quantity: Number(document.getElementById("bookQuantity").value || 1)
  };
  try {
    const response = await authenticatedFetch(`/api/tours/${encodeURIComponent(id)}/book`, {
      method: "POST",
      body: JSON.stringify(payload)
    });
    const result = await readJson(response);
    showToast(result.message || "Đặt tour thành công");
    closePublicBookModal();
    await loadTourOfferStatus(true);
    await loadPublicTours();
  } catch (error) {
    showToast(error.message);
  }
}

async function loadTourOfferStatus(silent = false) {
  try {
    const response = await authenticatedFetch("/api/tour-offers/status");
    const result = await readJson(response);
    tourOfferStatus = {
      progress: Number(result.progress || 0),
      target: Number(result.target || 5),
      discount_percent: Number(result.discount_percent || 0),
      invites: Array.isArray(result.invites) ? result.invites : []
    };
    renderTourOfferStatus();
    renderPublicTours();
  } catch (error) {
    if (!silent) showToast(error.message);
  }
}

function renderTourOfferStatus() {
  const progress = Math.max(0, Math.min(Number(tourOfferStatus.progress || 0), Number(tourOfferStatus.target || 5)));
  const target = Math.max(1, Number(tourOfferStatus.target || 5));
  const discount = currentTourDiscountPercent();
  const percent = Math.min(100, Math.round(progress * 100 / target));

  const discountText = document.getElementById("tourOfferDiscountText");
  const progressText = document.getElementById("tourOfferProgressText");
  const fill = document.getElementById("tourOfferProgressFill");
  const list = document.getElementById("tourOfferInviteList");

  if (discountText) discountText.textContent = `Giảm ${discount}%`;
  if (progressText) progressText.textContent = `${progress}/${target} người`;
  if (fill) fill.style.width = `${percent}%`;

  if (!list) return;
  const invites = Array.isArray(tourOfferStatus.invites) ? tourOfferStatus.invites : [];
  if (!invites.length) {
    list.innerHTML = '<div class="tour-offer-empty">Chưa có Gmail được mời.</div>';
    return;
  }

  list.innerHTML = invites.map((item) => {
    const accepted = String(item.status || "").toLowerCase().includes("đã đăng ký");
    const code = item.invite_code || item.inviteCode || "";
    return `
      <div class="tour-offer-invite-item ${accepted ? 'accepted' : ''}">
        <span class="tour-offer-invite-main">
          <b>${escapeHtml(item.invited_email || '')}</b>
          ${code ? `<small>Mã mời: ${escapeHtml(code)}</small>` : ''}
        </span>
        <strong>${accepted ? '+4%' : 'Đã mời'}</strong>
      </div>`;
  }).join("");
}

async function openTourOfferModal() {
  document.getElementById("tourOfferModal")?.classList.add("open");
  await loadTourOfferStatus(false);
  clearInterval(tourOfferRefreshTimer);
  tourOfferRefreshTimer = setInterval(() => {
    if (document.getElementById("tourOfferModal")?.classList.contains("open")) {
      loadTourOfferStatus(true);
    }
  }, 10000);
}

function closeTourOfferModal() {
  document.getElementById("tourOfferModal")?.classList.remove("open");
  clearInterval(tourOfferRefreshTimer);
  tourOfferRefreshTimer = null;
}

async function submitTourOfferInvite(event) {
  event.preventDefault();
  const input = document.getElementById("tourOfferInviteEmail");
  const email = input?.value.trim() || "";
  if (!email) return;

  try {
    const response = await authenticatedFetch("/api/tour-offers/invite", {
      method: "POST",
      body: JSON.stringify({ email })
    });
    const result = await readJson(response);
    input.value = "";
    showToast(result.message || "Đã gửi lời mời");
    await loadTourOfferStatus(false);
  } catch (error) {
    showToast(error.message);
  }
}

function setupTourOfferUi() {
  document.getElementById("tourOfferBtn")?.addEventListener("click", openTourOfferModal);
  document.getElementById("tourOfferInviteForm")?.addEventListener("submit", submitTourOfferInvite);
  document.getElementById("tourOfferModal")?.addEventListener("click", (event) => {
    if (event.target?.id === "tourOfferModal") closeTourOfferModal();
  });
}

document.addEventListener("DOMContentLoaded", () => {
  document.getElementById("publicBookForm")?.addEventListener("submit", submitPublicBooking);
  setupPublicTourSearch();
  setupTourOfferUi();
  loadTourOfferStatus(true).finally(loadPublicTours);
});

window.openPublicBookModal = openPublicBookModal;
window.closePublicBookModal = closePublicBookModal;
window.openTourOfferModal = openTourOfferModal;
window.closeTourOfferModal = closeTourOfferModal;
