let manageAccounts = [];
let manageOrders = [];
let manageApplications = [];
let manageAccountSearch = "";
let manageOrderSearch = "";
let manageCountdownTimer = null;

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>\"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;" }[char]));
}

function money(value) {
  return Number(value || 0).toLocaleString("vi-VN") + "đ";
}

function getValue(item, ...keys) {
  for (const key of keys) {
    if (item && item[key] !== undefined && item[key] !== null && item[key] !== "") return item[key];
  }
  return "";
}

function normalizeSearchText(value) {
  return String(value ?? "").toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "").replace(/đ/g, "d").replace(/\s+/g, " ").trim();
}

function formatDate(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value).split("T")[0] || "";
  return date.toLocaleDateString("vi-VN");
}

function formatDateTime(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value).split("T")[0] || "";
  return date.toLocaleString("vi-VN", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

function formatLongCountdown(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "0 ngày 00:00:00";
  const ms = Math.max(0, date.getTime() - Date.now());
  const totalSeconds = Math.floor(ms / 1000);
  const days = Math.floor(totalSeconds / 86400);
  const hours = String(Math.floor((totalSeconds % 86400) / 3600)).padStart(2, "0");
  const minutes = String(Math.floor((totalSeconds % 3600) / 60)).padStart(2, "0");
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${days} ngày ${hours}:${minutes}:${seconds}`;
}

function updateManageCountdowns() {
  document.querySelectorAll("[data-plan-countdown]").forEach(el => {
    el.textContent = formatLongCountdown(el.dataset.planCountdown || "");
  });
}

function startManageCountdowns() {
  clearInterval(manageCountdownTimer);
  updateManageCountdowns();
  manageCountdownTimer = setInterval(updateManageCountdowns, 1000);
}

function showToast(message) {
  const toast = document.getElementById("tourToast");
  if (!toast) return window.TravelwAIToast(message);
  toast.textContent = message;
  toast.classList.add("show");
  setTimeout(() => toast.classList.remove("show"), 2600);
}

async function readJson(response) {
  if (!response) throw new Error("Không có phản hồi từ máy chủ");
  const data = await response.json().catch(() => ({}));
  if (!response.ok || data.success === false) throw new Error(data.message || data.detail || "Không thực hiện được");
  return data;
}

function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value;
}

async function loadManage() {
  try {
    const response = await authenticatedFetch("/api/manage/dashboard");
    const result = await readJson(response);
    const data = result.data || {};
    manageAccounts = Array.isArray(data.accounts) ? data.accounts : [];
    manageOrders = Array.isArray(data.orders) ? data.orders : [];
    manageApplications = Array.isArray(data.applications) ? data.applications : [];
    setText("manageStatAccounts", manageAccounts.length);
    setText("manageStatOrders", manageOrders.length);
    setText("manageStatPending", manageOrders.filter(o => String(getValue(o, "status")).toLowerCase() === "khách đặt").length);
    setText("manageStatApplications", manageApplications.length);
    renderManageAccounts();
    renderManageOrders();
    renderManageApplications();
  } catch (error) {
    showToast(error.message);
    const accountBody = document.getElementById("manageAccountTableBody");
    const orderBody = document.getElementById("manageOrderTableBody");
    if (accountBody) accountBody.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
    if (orderBody) orderBody.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function planExpireHtml(account) {
  const role = String(getValue(account, "plan_role", "planRole", "role") || "Free").toLowerCase();
  const expires = getValue(account, "plan_expires_at", "planExpiresAt");
  const nextRole = getValue(account, "next_plan_role", "nextPlanRole");
  const nextStart = getValue(account, "next_plan_started_at", "nextPlanStartedAt");
  let html = "";
  if (!expires || role === "free" || role === "admin") {
    html = "Không có";
  } else {
    const date = new Date(expires);
    const expired = !Number.isNaN(date.getTime()) && date.getTime() < Date.now();
    html = `<span class="badge tour-status-badge ${expired ? 'status-canceled' : 'status-selling'}">${expired ? 'Hết hạn' : 'Còn hạn'}</span><br><small>${formatDateTime(expires)}</small><br><small>Còn <span data-plan-countdown="${escapeHtml(expires)}">${formatLongCountdown(expires)}</span></small>`;
  }
  if (nextRole && nextStart) {
    html += `<br><small>Tiếp theo: ${escapeHtml(nextRole)} · ${formatDateTime(nextStart)}</small>`;
  }
  return html;
}

function renderManageAccounts() {
  const body = document.getElementById("manageAccountTableBody");
  if (!body) return;
  const q = normalizeSearchText(manageAccountSearch);
  const rows = q ? manageAccounts.filter(a => normalizeSearchText([getValue(a, "username"), getValue(a, "email"), getValue(a, "role"), getValue(a, "plan_expires_at", "planExpiresAt")].join(" ")).includes(q)) : manageAccounts;
  if (!rows.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Không tìm thấy tài khoản.</td></tr>';
    return;
  }
  body.innerHTML = rows.map(account => {
    const locked = String(getValue(account, "is_locked", "isLocked")) === "true";
    return `<tr>
      <td><strong>${escapeHtml(getValue(account, "username") || "Tài khoản")}</strong></td>
      <td>${escapeHtml(getValue(account, "email"))}</td>
      <td><span class="badge tour-status-badge status-selling">${escapeHtml(getValue(account, "plan_role", "planRole", "role") || "Free")}</span></td>
      <td>${planExpireHtml(account)}</td>
      <td>${locked ? '<span class="badge tour-status-badge status-canceled">Bị khóa</span>' : '<span class="badge tour-status-badge status-selling">Hoạt động</span>'}</td>
      <td>${formatDate(getValue(account, "created_at", "createdAt"))}</td>
    </tr>`;
  }).join("");
  startManageCountdowns();
}

function orderPriceHtml(order) {
  const finalPrice = Number(getValue(order, "price_amount", "priceAmount") || 0);
  const original = Number(getValue(order, "original_price_amount", "originalPriceAmount") || finalPrice);
  const discountPercent = Number(getValue(order, "discount_percent", "discountPercent") || 0);
  const discountAmount = Number(getValue(order, "discount_amount", "discountAmount") || 0);
  if (discountPercent > 0) return `<strong>${money(finalPrice)}</strong><br><small>Giảm ${discountPercent}%</small>`;
  return `<strong>${money(finalPrice)}</strong>`;
}

function renderManageOrders() {
  const body = document.getElementById("manageOrderTableBody");
  if (!body) return;
  const q = normalizeSearchText(manageOrderSearch);
  const rows = q ? manageOrders.filter(o => normalizeSearchText([getValue(o, "buyer_name", "buyerName"), getValue(o, "buyer_email", "buyerEmail"), getValue(o, "plan_role", "planRole"), getValue(o, "status")].join(" ")).includes(q)) : manageOrders;
  if (!rows.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Chưa có đơn gói.</td></tr>';
    return;
  }
  body.innerHTML = rows.map(order => {
    const id = getValue(order, "id", "Id");
    const status = getValue(order, "status") || "Khách đặt";
    const pending = String(status).toLowerCase() === "khách đặt";
    const months = Number(getValue(order, "duration_months", "durationMonths") || 1);
    const soldStart = getValue(order, "plan_started_at", "planStartedAt");
    const soldExpire = getValue(order, "plan_expires_at", "planExpiresAt");
    const currentExpire = getValue(order, "current_plan_expires_at", "currentPlanExpiresAt");
    return `<tr>
      <td><strong>${escapeHtml(getValue(order, "buyer_name", "buyerName") || "Người mua")}</strong><br><small>${escapeHtml(getValue(order, "buyer_email", "buyerEmail"))}</small></td>
      <td><strong>${escapeHtml(getValue(order, "plan_role", "planRole") || "Gói")}</strong><br><small>${months} tháng</small></td>
      <td>${orderPriceHtml(order)}</td>
      <td>${soldExpire ? `<strong>${formatDateTime(soldExpire)}</strong>${soldStart ? `<br><small>${formatDateTime(soldStart)}</small>` : ""}` : `<strong>${months} tháng</strong>${currentExpire ? `<br><small>${formatDateTime(currentExpire)}</small>` : ""}`}</td>
      <td><span class="badge tour-status-badge ${pending ? 'status-booked' : 'status-selling'}">${escapeHtml(status)}</span><br><small>${formatDate(getValue(order, "created_at", "createdAt"))}</small></td>
      <td class="manage-action-cell"><div class="inline-actions manage-inline-actions">${pending ? `<button class="btn-primary" type="button" onclick="sellPlanOrder('${escapeHtml(id)}')">Bán</button>` : ""}<button class="btn-danger" type="button" onclick="deletePlanOrder('${escapeHtml(id)}')">Xóa</button></div></td>
    </tr>`;
  }).join("");
}

function renderManageApplications() {
  const body = document.getElementById("manageApplicationTableBody");
  if (!body) return;
  if (!manageApplications.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Chưa có biểu mẫu Sales / Business.</td></tr>';
    return;
  }
  body.innerHTML = manageApplications.map(app => {
    const id = getValue(app, "id", "Id");
    const status = getValue(app, "status") || "Chờ xử lý";
    const pending = String(status).toLowerCase() === "chờ xử lý";
    return `<tr>
      <td><strong>${escapeHtml(getValue(app, "account_name", "contact_name", "contactName") || "Người gửi")}</strong><br><small>${escapeHtml(getValue(app, "account_email", "user_email", "userEmail"))}</small></td>
      <td><strong>${escapeHtml(getValue(app, "plan_role", "planRole"))}</strong></td>
      <td><strong>${escapeHtml(getValue(app, "company_name", "companyName"))}</strong><br><small>${escapeHtml(getValue(app, "business_type", "businessType"))} · ${escapeHtml(getValue(app, "province"))}</small></td>
      <td>${escapeHtml(getValue(app, "contact_name", "contactName"))}<br><small>${escapeHtml(getValue(app, "phone"))} · ${escapeHtml(getValue(app, "email"))}</small></td>
      <td><span class="badge tour-status-badge ${pending ? 'status-booked' : 'status-selling'}">${escapeHtml(status)}</span></td>
      <td class="manage-action-cell"><div class="inline-actions manage-inline-actions">${pending ? `<button class="btn-primary" type="button" onclick="approveBusinessApplication('${escapeHtml(id)}')">Duyệt</button>` : ""}<button class="btn-danger" type="button" onclick="deleteBusinessApplication('${escapeHtml(id)}')">Xóa</button></div></td>
    </tr>`;
  }).join("");
}

async function sellPlanOrder(id) {
  try {
    const response = await authenticatedFetch(`/api/manage/plan-orders/${encodeURIComponent(id)}/sell`, { method: "POST" });
    const result = await readJson(response);
    showToast(result.message || "Đã bán gói");
    await loadManage();
  } catch (error) { showToast(error.message); }
}

async function deletePlanOrder(id) {
  try {
    const response = await authenticatedFetch(`/api/manage/plan-orders/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xoá đơn");
    await loadManage();
  } catch (error) { showToast(error.message); }
}

async function approveBusinessApplication(id) {
  try {
    const response = await authenticatedFetch(`/api/manage/business-applications/${encodeURIComponent(id)}/approve`, { method: "POST" });
    const result = await readJson(response);
    showToast(result.message || "Đã duyệt biểu mẫu");
    await loadManage();
  } catch (error) { showToast(error.message); }
}

async function deleteBusinessApplication(id) {
  try {
    const response = await authenticatedFetch(`/api/manage/business-applications/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xoá biểu mẫu");
    await loadManage();
  } catch (error) { showToast(error.message); }
}

function setupSearch() {
  document.getElementById("manageAccountSearch")?.addEventListener("input", event => { manageAccountSearch = event.target.value || ""; renderManageAccounts(); });
  document.getElementById("clearManageAccountSearch")?.addEventListener("click", () => { const input = document.getElementById("manageAccountSearch"); if (input) input.value = ""; manageAccountSearch = ""; renderManageAccounts(); });
  document.getElementById("manageOrderSearch")?.addEventListener("input", event => { manageOrderSearch = event.target.value || ""; renderManageOrders(); });
  document.getElementById("clearManageOrderSearch")?.addEventListener("click", () => { const input = document.getElementById("manageOrderSearch"); if (input) input.value = ""; manageOrderSearch = ""; renderManageOrders(); });
}

document.addEventListener("DOMContentLoaded", () => { setupSearch(); loadManage(); });
window.sellPlanOrder = sellPlanOrder;
window.deletePlanOrder = deletePlanOrder;
window.approveBusinessApplication = approveBusinessApplication;
window.deleteBusinessApplication = deleteBusinessApplication;
