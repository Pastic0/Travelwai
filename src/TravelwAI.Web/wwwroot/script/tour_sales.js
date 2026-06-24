let travelwaiTours = [];
let travelwaiOrders = [];
let tourSearchQuery = "";
let orderSearchQuery = "";
let currentTourUserId = "";
let currentTourUserName = localStorage.getItem("username") || localStorage.getItem("userEmail") || "";
let availableTourSalesAccounts = [];

function money(value) {
  const n = Number(value || 0);
  return n.toLocaleString("vi-VN") + "đ";
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function tourActionIcon(type) {
  if (type === "edit") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>`;
  }
  if (type === "sell") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 1v22"/><path d="M17 5H9.5a3.5 3.5 0 0 0 0 7H14.5a3.5 3.5 0 0 1 0 7H6"/></svg>`;
  }
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v5"/><path d="M14 11v5"/></svg>`;
}

function tourIconButton(className, iconType, label, onClick) {
  return `<button class="${className} tour-table-icon-button" type="button" onclick="${onClick}" title="${escapeHtml(label)}" aria-label="${escapeHtml(label)}">${tourActionIcon(iconType)}</button>`;
}

function roleIsAdmin() {
  return (localStorage.getItem("userRole") || "").toLowerCase() === "admin";
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

function numberValue(item, ...keys) {
  const value = getValue(item, ...keys);
  const number = Number(value || 0);
  return Number.isFinite(number) ? number : 0;
}

function getTourSalesName(tour) {
  return getValue(tour, "tour_sales_name", "tourSalesName", "sales_name", "salesName", "seller_name", "sellerName") || "Tour Sales";
}

function getTourOwnerId(tour) {
  return String(getValue(tour, "created_by", "createdBy", "tour_sales_id", "tourSalesId", "seller_id", "sellerId") || "");
}

function getOrderTourId(order) {
  return String(getValue(order, "tour_id", "tourId") || "");
}

function getOrderTourOwnerId(order) {
  const storedOwnerId = String(getValue(order, "tour_sales_id", "tourSalesId") || "");
  if (storedOwnerId) return storedOwnerId;
  const tourId = getOrderTourId(order);
  const tour = travelwaiTours.find(item => String(item?.id || item?.Id || "") === tourId);
  return tour ? getTourOwnerId(tour) : "";
}

function getOrderTourSalesName(order) {
  const storedName = getValue(order, "tour_sales_name", "tourSalesName", "sales_name", "salesName", "seller_name", "sellerName");
  if (storedName) return storedName;
  const tourId = getOrderTourId(order);
  const tour = travelwaiTours.find(item => String(item?.id || item?.Id || "") === tourId);
  return tour ? getTourSalesName(tour) : "Tour Sales";
}

function canSellOrder(order) {
  if (roleIsAdmin()) return true;
  if (order?.can_sell === true || order?.canSell === true) return true;
  const ownerId = getOrderTourOwnerId(order);
  return !!ownerId && !!currentTourUserId && ownerId === currentTourUserId;
}

function canEditTour(tour) {
  if (roleIsAdmin()) return true;
  if (tour?.can_edit === true || tour?.canEdit === true) return true;
  const ownerId = getTourOwnerId(tour);
  return !!ownerId && !!currentTourUserId && ownerId === currentTourUserId;
}

function getAccountId(account) {
  return String(getValue(account, "id", "uid", "user_id", "userId") || "");
}

function getAccountName(account) {
  return getValue(account, "username", "displayName", "display_name", "name", "email") || "Tài khoản";
}

function setAvailableTourSalesAccounts(accounts) {
  availableTourSalesAccounts = (Array.isArray(accounts) ? accounts : [])
    .filter(account => getAccountId(account));
}

async function getAvailableTourSalesAccounts() {
  if (!roleIsAdmin()) return [];
  if (availableTourSalesAccounts.length) return availableTourSalesAccounts;

  try {
    if (typeof travelwaiAccounts !== "undefined" && Array.isArray(travelwaiAccounts) && travelwaiAccounts.length) {
      setAvailableTourSalesAccounts(travelwaiAccounts);
    }
  } catch (_) { }

  if (availableTourSalesAccounts.length) return availableTourSalesAccounts;

  try {
    const response = await authenticatedFetch("/api/admin/accounts");
    const result = await readJson(response);
    setAvailableTourSalesAccounts(result.data || []);
  } catch (error) {
    console.warn("Không tải được danh sách tài khoản", error);
  }

  return availableTourSalesAccounts;
}

async function setupTourSalesField(tour) {
  const field = document.getElementById("tourSalesName");
  if (!field) return;

  const isAdmin = roleIsAdmin();
  const currentName = tour ? getTourSalesName(tour) : (currentTourUserName || localStorage.getItem("username") || localStorage.getItem("userEmail") || "");
  const currentOwnerId = tour ? getTourOwnerId(tour) : currentTourUserId;

  if (field.tagName === "SELECT") {
    field.innerHTML = `<option value="">Chọn tài khoản</option>`;
    field.disabled = !isAdmin;
    field.classList.toggle("tour-sales-admin-editable", isAdmin);

    if (!isAdmin) {
      const option = document.createElement("option");
      option.value = currentOwnerId || currentTourUserId || "";
      option.textContent = currentName || "Tài khoản";
      option.dataset.name = currentName || "Tài khoản";
      option.selected = true;
      field.appendChild(option);
      return;
    }

    const accounts = await getAvailableTourSalesAccounts();
    const selectedOwnerId = currentOwnerId || "";
    let hasSelected = false;

    accounts.forEach(account => {
      const id = getAccountId(account);
      const name = getAccountName(account);
      const option = document.createElement("option");
      option.value = id;
      option.textContent = name;
      option.dataset.name = name;
      if (id && selectedOwnerId && id === selectedOwnerId) {
        option.selected = true;
        hasSelected = true;
      }
      field.appendChild(option);
    });

    if (!tour && !field.value && accounts.length) {
      field.value = getAccountId(accounts[0]);
    }

    if (tour && selectedOwnerId && !hasSelected) {
      field.value = "";
    }

    return;
  }

  field.value = currentName;
  field.readOnly = !isAdmin;
  field.disabled = false;
  field.classList.toggle("tour-sales-admin-editable", isAdmin);
}

function renderOrderTotal(order) {
  const total = numberValue(order, "total_price", "totalPrice");
  const original = numberValue(order, "original_total_price", "originalTotalPrice") || total;
  const discountPercent = Math.max(0, Math.min(25, numberValue(order, "discount_percent", "discountPercent")));
  const discountAmount = numberValue(order, "discount_amount", "discountAmount") || Math.max(0, original - total);

  if (!discountPercent || discountAmount <= 0 || original <= total) {
    return money(total);
  }

  return `
    <div class="order-total-discount">
      <small>${money(original)}</small>
      <strong>${money(total)}</strong>
      <em>Đã trừ ${discountPercent}% · ${money(discountAmount)}</em>
    </div>`;
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

function getTourDisplayStatus(tour) {
  const sold = Number(tour?.sold || 0);
  const slots = Number(tour?.slots || 0);
  const rawStatus = String(tour?.status || "Đang bán").trim();
  if (slots > 0 && sold >= slots) return "Đã bán";
  if (rawStatus.toLowerCase() === "hết chỗ") return "Đã bán";
  return rawStatus || "Đang bán";
}

function tourIsSoldOut(tour) {
  const sold = Number(tour?.sold || 0);
  const slots = Number(tour?.slots || 0);
  const status = getTourDisplayStatus(tour).toLowerCase();
  return (slots > 0 && sold >= slots) || status === "đã bán" || status === "hết chỗ";
}

function tourIsCanceled(tour) {
  const status = normalizeSearchText(getTourDisplayStatus(tour));
  return status.startsWith("da huy") || status === "huy" || status === "cancelled" || status === "canceled";
}

function getStatusBadgeClass(status) {
  const text = String(status || "").trim().toLowerCase();
  if (text === "đang bán") return "badge tour-status-badge status-selling";
  if (text === "đã bán") return "badge tour-status-badge status-sold";
  if (text === "khách đặt") return "badge tour-status-badge status-booked";
  if (text === "đã hủy" || text === "da huy") return "badge tour-status-badge status-canceled";
  if (text === "tạm dừng") return "badge tour-status-badge status-paused";
  return "badge tour-status-badge status-closed";
}

function tourTimeText(tour) {
  const duration = getValue(tour, "duration", "tour_duration");
  const start = formatDateOnly(getValue(tour, "start_date", "startDate", "tour_start_date"));
  const end = formatDateOnly(getValue(tour, "end_date", "endDate", "tour_end_date"));
  const dateRange = start && end ? `${start} → ${end}` : (start || end);
  const safeDuration = escapeHtml(duration);
  const safeDateRange = escapeHtml(dateRange);
  if (duration && dateRange) return `${safeDuration}<br><small>${safeDateRange}</small>`;
  return safeDuration || safeDateRange || "";
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

function getTourPlainTimeText(tour) {
  const duration = getValue(tour, "duration", "tour_duration");
  const start = formatDateOnly(getValue(tour, "start_date", "startDate", "tour_start_date"));
  const end = formatDateOnly(getValue(tour, "end_date", "endDate", "tour_end_date"));
  const dateRange = start && end ? `${start} → ${end}` : (start || end);
  return [duration, dateRange].filter(Boolean).join(" ");
}

function getTourSearchText(tour) {
  return normalizeSearchText([
    tour?.name,
    tour?.destination,
    tour?.description,
    getTourPlainTimeText(tour),
    tour?.price,
    tour?.status,
    getTourDisplayStatus(tour),
    getTourSalesName(tour)
  ].join(" "));
}

function getOrderSearchText(order) {
  return normalizeSearchText([
    order?.customer_name,
    order?.customerName,
    order?.customer_email,
    order?.customerEmail,
    order?.tour_name,
    order?.tourName,
    order?.tour_id,
    order?.tourId,
    getOrderTourSalesName(order),
    order?.quantity,
    order?.total_price,
    order?.totalPrice,
    order?.original_total_price,
    order?.originalTotalPrice,
    order?.discount_percent,
    order?.discountPercent,
    order?.status,
    tourTimeText(order),
    order?.created_at,
    formatDate(order?.created_at)
  ].join(" "));
}

function filterToursForCurrentPage(tours) {
  const isTourSalesPage = document.body?.dataset?.page === "tour-sales";
  let visibleTours = tours.filter(tour => !tourIsCanceled(tour));
  if (isTourSalesPage) visibleTours = visibleTours.filter(tour => !tourIsSoldOut(tour));
  const query = normalizeSearchText(tourSearchQuery);
  if (query) visibleTours = visibleTours.filter(tour => getTourSearchText(tour).includes(query));
  return visibleTours;
}

function setupTourSearch() {
  const input = document.getElementById("tourSearchInput");
  const clearButton = document.getElementById("clearTourSearch");
  if (!input) return;
  input.addEventListener("input", () => {
    tourSearchQuery = input.value || "";
    renderTours();
  });
  clearButton?.addEventListener("click", () => {
    input.value = "";
    tourSearchQuery = "";
    input.focus();
    renderTours();
  });
}

function setupOrderSearch() {
  const input = document.getElementById("orderSearchInput");
  const clearButton = document.getElementById("clearOrderSearch");
  if (!input) return;
  input.addEventListener("input", () => {
    orderSearchQuery = input.value || "";
    renderOrders();
  });
  clearButton?.addEventListener("click", () => {
    input.value = "";
    orderSearchQuery = "";
    input.focus();
    renderOrders();
  });
}

function validateTourImageFile(file) {
  if (!file) return;
  if (!file.type || !file.type.startsWith("image/")) {
    throw new Error("Vui lòng chọn đúng tệp ảnh.");
  }
  if (file.size > 10 * 1024 * 1024) {
    throw new Error("Dung lượng ảnh tour phải nhỏ hơn 10MB.");
  }
}

async function uploadTourImageFile(file) {
  validateTourImageFile(file);
  const uploadFile = window.TravelwAIImageOptimizer
    ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
    : file;
  const formData = new FormData();
  formData.append("image", uploadFile, uploadFile.name || file.name);
  const response = await authenticatedFetch("/api/tour-sales/tour-image", {
    method: "POST",
    body: formData
  });
  const result = await readJson(response);
  return result.url || result.image || result.data?.url || "";
}

async function loadTourSalesPage() {
  const adminLink = document.getElementById("adminPanelLink");
  if (adminLink && roleIsAdmin()) adminLink.style.display = "inline-flex";
  await Promise.all([loadTourDashboard(), loadTours(), loadOrders()]);
}

async function loadTourDashboard() {
  try {
    const response = await authenticatedFetch("/api/tour-sales/dashboard");
    const result = await readJson(response);
    const data = result.data || {};
    setText("statTours", data.tours || 0);
    setText("statActiveTours", data.activeTours || 0);
    setText("statSold", data.sold || 0);
    setText("statRevenue", money(data.revenue || 0));
  } catch (error) {
    console.error(error);
    showToast(error.message);
  }
}

async function loadTours() {
  const body = document.getElementById("tourTableBody");
  if (body) body.innerHTML = `<tr><td colspan="7" class="empty-line">Đang tải tour...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/tour-sales/tours");
    const result = await readJson(response);
    currentTourUserId = result.current_user_id || result.currentUserId || currentTourUserId;
    currentTourUserName = result.current_user_name || result.currentUserName || currentTourUserName || localStorage.getItem("username") || localStorage.getItem("userEmail") || "";
    travelwaiTours = Array.isArray(result.data) ? result.data : [];
    renderTours();
  } catch (error) {
    console.error(error);
    if (body) body.innerHTML = `<tr><td colspan="7" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function renderTours() {
  const body = document.getElementById("tourTableBody");
  if (!body) return;

  const visibleTours = filterToursForCurrentPage(travelwaiTours);
  const hasSearch = normalizeSearchText(tourSearchQuery).length > 0;

  if (!visibleTours.length) {
    body.innerHTML = `<tr><td colspan="7" class="empty-line">${hasSearch ? "Không tìm thấy tour phù hợp." : "Chưa có tour đang bán."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleTours.map((tour) => {
    const id = tour.id || tour.Id || "";
    const sold = Number(tour.sold || 0);
    const slots = Number(tour.slots || 0);
    const status = getTourDisplayStatus(tour);
    const statusClass = getStatusBadgeClass(status);
    const salesName = getTourSalesName(tour);
    const editable = canEditTour(tour);
    return `
      <tr>
        <td>
          <div class="tour-table-name">
            <strong>${escapeHtml(tour.name || "Tour")}</strong>
            <small>${escapeHtml(salesName)}</small>
          </div>
        </td>
        <td class="nowrap-cell">${escapeHtml(tour.destination || "")}</td>
        <td class="nowrap-cell">${tourTimeText(tour)}</td>
        <td class="nowrap-cell">${money(tour.price)}</td>
        <td class="nowrap-cell">${sold}/${slots}</td>
        <td class="nowrap-cell"><span class="${statusClass}">${escapeHtml(status)}</span></td>
        <td class="nowrap-cell">
          <div class="inline-actions">
            ${editable ? `
              ${tourIconButton("btn-primary", "edit", "Sửa tour", `editTour('${escapeHtml(id)}')`)}
              ${tourIconButton("btn-danger", "delete", "Xóa tour", `deleteTour('${escapeHtml(id)}')`)}
            ` : `<span class="locked-action">Không chỉnh được</span>`}
          </div>
        </td>
      </tr>`;
  }).join("");
}

async function loadOrders() {
  const body = document.getElementById("orderTableBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="8" class="empty-line">Đang tải đơn...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/tour-sales/orders");
    const result = await readJson(response);
    travelwaiOrders = Array.isArray(result.data) ? result.data : [];
    renderOrders();
  } catch (error) {
    console.error(error);
    body.innerHTML = `<tr><td colspan="8" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function renderOrders() {
  const body = document.getElementById("orderTableBody");
  if (!body) return;

  const query = normalizeSearchText(orderSearchQuery);
  const sourceRows = query
    ? travelwaiOrders.filter(order => getOrderSearchText(order).includes(query))
    : travelwaiOrders;

  if (!sourceRows.length) {
    body.innerHTML = `<tr><td colspan="8" class="empty-line">${query ? "Không tìm thấy đơn bán tour phù hợp." : "Chưa có khách đặt tour."}</td></tr>`;
    return;
  }

  const isAdmin = roleIsAdmin();
  const orderedRows = [...sourceRows].sort((a, b) => {
    const aSold = String(a.status || "").toLowerCase() === "đã bán" ? 1 : 0;
    const bSold = String(b.status || "").toLowerCase() === "đã bán" ? 1 : 0;
    return aSold - bSold;
  });

  body.innerHTML = orderedRows.map((order) => {
    const id = order.id || order.Id || "";
    const status = order.status || "Khách đặt";
    const statusLower = status.toLowerCase();
    const isPendingOrder = statusLower === "khách đặt";
    const canSell = isPendingOrder && canSellOrder(order);
    const statusClass = getStatusBadgeClass(status);
    const orderTime = tourTimeText(order);
    return `
      <tr>
        <td><strong>${escapeHtml(order.customer_name || "Khách hàng")}</strong><br><small>${escapeHtml(order.customer_email || "")}</small></td>
        <td><strong>${escapeHtml(order.tour_name || order.tour_id || "")}</strong>${orderTime ? `<br><span class="order-time-small">${orderTime}</span>` : ""}</td>
        <td><strong>${escapeHtml(getOrderTourSalesName(order))}</strong></td>
        <td>${escapeHtml(order.quantity || 1)}</td>
        <td>${renderOrderTotal(order)}</td>
        <td><span class="${statusClass}">${escapeHtml(status)}</span></td>
        <td>${formatDate(order.created_at)}</td>
        <td>
          <div class="inline-actions">
            ${isPendingOrder ? (canSell ? tourIconButton("btn-primary", "sell", "Bán tour", `sellBookedOrder('${escapeHtml(id)}')`) : `<span class="locked-action">Không bán được</span>`) : ""}
            ${isAdmin ? tourIconButton("btn-danger", "delete", "Xóa đơn bán tour", `deleteTourOrder('${escapeHtml(id)}')`) : ""}
          </div>
        </td>
      </tr>`;
  }).join("");
}

async function openTourModal(tour = null) {
  const modal = document.getElementById("tourModal");
  if (!modal) return;
  document.getElementById("tourModalTitle").textContent = tour ? "Sửa tour" : "Thêm tour";
  document.getElementById("tourId").value = tour?.id || "";
  await setupTourSalesField(tour);
  document.getElementById("tourName").value = tour?.name || "";
  document.getElementById("tourDestination").value = tour?.destination || "";
  document.getElementById("tourDuration").value = tour?.duration || "";
  document.getElementById("tourStartDate").value = normalizeDateValue(getValue(tour, "start_date", "startDate"));
  document.getElementById("tourEndDate").value = normalizeDateValue(getValue(tour, "end_date", "endDate"));
  document.getElementById("tourPrice").value = tour?.price || 0;
  document.getElementById("tourSlots").value = tour?.slots || 0;
  document.getElementById("tourSold").value = tour?.sold || 0;
  document.getElementById("tourStatus").value = getTourDisplayStatus(tour);
  document.getElementById("tourImage").value = tour?.image || "";
  const imageFileInput = document.getElementById("tourImageFile");
  if (imageFileInput) imageFileInput.value = "";
  const imageFileName = document.getElementById("tourImageFileName");
  if (imageFileName) imageFileName.textContent = "";
  document.getElementById("tourDescription").value = tour?.description || "";
  modal.classList.add("open");
}

function closeTourModal() {
  document.getElementById("tourModal")?.classList.remove("open");
}

function editTour(id) {
  const tour = travelwaiTours.find(t => String(t.id) === String(id));
  if (!tour) return showToast("Không tìm thấy tour");
  if (!canEditTour(tour)) return showToast("Tour Sales chỉ được sửa tour của họ.");
  openTourModal(tour);
}

async function submitTourForm(event) {
  event.preventDefault();
  const id = document.getElementById("tourId").value;
  const startDate = document.getElementById("tourStartDate").value;
  const endDate = document.getElementById("tourEndDate").value;
  if (startDate && endDate && endDate < startDate) {
    showToast("Ngày kết thúc phải sau hoặc bằng ngày bắt đầu.");
    return;
  }

  const submitButton = event.submitter || document.querySelector("#tourForm button[type='submit']");
  const originalButtonText = submitButton?.textContent || "Lưu tour";
  const imageFileInput = document.getElementById("tourImageFile");
  const imageFile = imageFileInput?.files?.[0] || null;

  try {
    if (submitButton) {
      submitButton.textContent = imageFile ? "Đang tải ảnh..." : "Đang lưu...";
      submitButton.disabled = true;
    }

    let imageValue = document.getElementById("tourImage").value.trim();
    if (imageFile) {
      imageValue = await uploadTourImageFile(imageFile);
      document.getElementById("tourImage").value = imageValue;
    }

    const tourSalesField = document.getElementById("tourSalesName");
    const selectedTourSalesOption = tourSalesField?.tagName === "SELECT"
      ? tourSalesField.selectedOptions?.[0]
      : null;
    const selectedTourSalesId = tourSalesField?.tagName === "SELECT" ? (tourSalesField.value || "") : "";
    const selectedTourSalesName = selectedTourSalesOption?.dataset?.name || selectedTourSalesOption?.textContent || tourSalesField?.value || currentTourUserName || "";

    const payload = {
      name: document.getElementById("tourName").value.trim(),
      tourSalesId: selectedTourSalesId,
      tourSalesName: selectedTourSalesName.trim(),
      destination: document.getElementById("tourDestination").value.trim(),
      duration: document.getElementById("tourDuration").value.trim(),
      startDate,
      endDate,
      price: Number(document.getElementById("tourPrice").value || 0),
      slots: Number(document.getElementById("tourSlots").value || 0),
      sold: Number(document.getElementById("tourSold").value || 0),
      status: document.getElementById("tourStatus").value,
      image: imageValue,
      description: document.getElementById("tourDescription").value.trim()
    };

    if (submitButton) submitButton.textContent = "Đang lưu...";

    const response = await authenticatedFetch(id ? `/api/tour-sales/tours/${encodeURIComponent(id)}` : "/api/tour-sales/tours", {
      method: id ? "PUT" : "POST",
      body: JSON.stringify(payload)
    });
    const result = await readJson(response);
    showToast(result.message || "Đã lưu tour");
    closeTourModal();
    await Promise.all([loadTourDashboard(), loadTours()]);
    if (typeof loadAdminDashboard === "function") loadAdminDashboard();
  } catch (error) {
    showToast(error.message);
  } finally {
    if (submitButton) {
      submitButton.textContent = originalButtonText;
      submitButton.disabled = false;
    }
  }
}

async function deleteTour(id) {
  const tour = travelwaiTours.find(t => String(t.id) === String(id));
  if (tour && !canEditTour(tour)) return showToast("Tour Sales chỉ được xóa tour của họ.");
  if (!confirm("Xóa tour này?")) return;
  try {
    const response = await authenticatedFetch(`/api/tour-sales/tours/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xóa tour");
    await Promise.all([loadTourDashboard(), loadTours()]);
    if (typeof loadAdminDashboard === "function") loadAdminDashboard();
  } catch (error) {
    showToast(error.message);
  }
}

async function sellBookedOrder(id) {
  const order = travelwaiOrders.find(item => String(item.id || item.Id || "") === String(id));
  if (order && !canSellOrder(order)) return showToast("Tour này thuộc Tour Sales khác, bạn không thể bán.");
  if (!confirm("Xác nhận bán đơn này? Sau khi bán thành công, hệ thống mới tạo lịch trình cho khách.")) return;
  try {
    const response = await authenticatedFetch(`/api/tour-sales/orders/${encodeURIComponent(id)}/sell`, { method: "POST" });
    const result = await readJson(response);
    showToast(result.message || "Đã bán tour");
    await loadTourSalesPage();
    if (typeof loadAdminDashboard === "function") loadAdminDashboard();
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteTourOrder(id) {
  if (!confirm("Xóa đơn bán tour này?")) return;
  try {
    const response = await authenticatedFetch(`/api/tour-sales/orders/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xóa đơn bán tour");
    await loadTourSalesPage();
    if (typeof loadAdminDashboard === "function") loadAdminDashboard();
  } catch (error) {
    showToast(error.message);
  }
}

function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value;
}

function formatDate(value) {
  if (!value) return "";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return escapeHtml(value);
  return d.toLocaleString("vi-VN");
}

document.addEventListener("DOMContentLoaded", () => {
  document.getElementById("tourForm")?.addEventListener("submit", submitTourForm);
  document.getElementById("tourImageUploadButton")?.addEventListener("click", () => document.getElementById("tourImageFile")?.click());
  document.getElementById("tourImageFile")?.addEventListener("change", (event) => {
    const file = event.target?.files?.[0];
    const helper = document.getElementById("tourImageFileName");
    if (helper) helper.textContent = file ? file.name : "";
  });
  setupTourSearch();
  setupOrderSearch();
  if (document.body.dataset.page === "tour-sales") {
    loadTourSalesPage();
    setInterval(() => {
      if (document.getElementById("tourModal")?.classList.contains("open")) return;
      loadTourSalesPage();
    }, 30000);
  }
});

window.loadTourSalesPage = loadTourSalesPage;
window.loadTourDashboard = loadTourDashboard;
window.loadTours = loadTours;
window.openTourModal = openTourModal;
window.setAvailableTourSalesAccounts = setAvailableTourSalesAccounts;
window.closeTourModal = closeTourModal;
window.editTour = editTour;
window.deleteTour = deleteTour;
window.sellBookedOrder = sellBookedOrder;
window.deleteTourOrder = deleteTourOrder;

window.renderOrders = renderOrders;
