let manageAccounts = [];
let manageOrders = [];
let manageApplications = [];
let manageAccountSearch = "";
let manageOrderSearch = "";
let manageCountdownTimer = null;
let manageExpiryRefreshPending = false;
let managePendingOrderRefreshTimer = null;

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
  let hasExpiredPlan = false;
  document.querySelectorAll("[data-plan-countdown]").forEach(el => {
    const expiresAt = el.dataset.planCountdown || "";
    el.textContent = formatLongCountdown(expiresAt);
    const date = new Date(expiresAt);
    if (!Number.isNaN(date.getTime()) && date.getTime() <= Date.now()) hasExpiredPlan = true;
  });
  if (hasExpiredPlan && !manageExpiryRefreshPending) {
    manageExpiryRefreshPending = true;
    setTimeout(() => loadManage().finally(() => {
      setTimeout(() => { manageExpiryRefreshPending = false; }, 30000);
    }), 600);
  }
}

function startManageCountdowns() {
  clearInterval(manageCountdownTimer);
  updateManageCountdowns();
  manageCountdownTimer = setInterval(updateManageCountdowns, 1000);
}

function schedulePendingOrderRefresh() {
  clearTimeout(managePendingOrderRefreshTimer);
  managePendingOrderRefreshTimer = null;

  const hasPendingOrders = manageOrders.some(order =>
    String(getValue(order, "status") || "").toLowerCase() === "khách đặt");

  if (!hasPendingOrders || document.hidden) return;

  managePendingOrderRefreshTimer = window.setTimeout(() => {
    loadManage().catch(() => {});
  }, 5000);
}

function showToast(message, type = "info") {
  return window.TravelwAIToast(message, type);
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
    schedulePendingOrderRefresh();
  } catch (error) {
    showToast(error.message);
    const accountBody = document.getElementById("manageAccountTableBody");
    const orderBody = document.getElementById("manageOrderTableBody");
    if (accountBody) accountBody.innerHTML = `<tr><td colspan="7" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
    if (orderBody) orderBody.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function planExpireHtml(account) {
  const role = String(getValue(account, "plan_role", "planRole", "role") || "Free").toLowerCase();
  const expires = getValue(account, "plan_expires_at", "planExpiresAt");
  const isPermanent = String(getValue(account, "plan_is_permanent", "planIsPermanent") || "").toLowerCase() === "true";
  const nextRole = getValue(account, "next_plan_role", "nextPlanRole");
  const nextStart = getValue(account, "next_plan_started_at", "nextPlanStartedAt");
  let html = "";
  if (isPermanent && role !== "free" && role !== "admin") {
    html = '<span class="badge tour-status-badge status-selling">Không giới hạn</span>';
  } else if (!expires || role === "free" || role === "admin") {
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
    body.innerHTML = '<tr><td colspan="7" class="empty-line">Không tìm thấy tài khoản.</td></tr>';
    return;
  }
  body.innerHTML = rows.map(account => {
    const locked = String(getValue(account, "is_locked", "isLocked")) === "true";
    const accountId = escapeHtml(getValue(account, "id"));
    const role = String(getValue(account, "role", "plan_role", "planRole") || "Free").toLowerCase();
    const hasExpiry = Boolean(getValue(account, "plan_expires_at", "planExpiresAt"));
    const isPermanent = String(getValue(account, "plan_is_permanent", "planIsPermanent") || "").toLowerCase() === "true";
    const isAdmin = role === "admin";
    const canDeleteExpiry = !isAdmin && role !== "free" && hasExpiry && !isPermanent;
    const editButton = isAdmin
      ? `<button class="btn-soft manage-settings-icon" type="button" disabled title="Không thể sửa gói Admin" aria-label="Không thể sửa gói Admin">${settingsIconSvg()}</button>`
      : `<button class="btn-soft manage-settings-icon" type="button" onclick="openManagePlanSettings('${accountId}')" title="Sửa gói và hạn gói" aria-label="Sửa gói và hạn gói">${settingsIconSvg()}</button>`;
    const deleteExpiryButton = `<button class="btn-soft manage-settings-icon manage-delete-expiry-icon" type="button" ${canDeleteExpiry ? `onclick="deleteManagePlanExpiry('${accountId}', this)"` : "disabled"} title="${canDeleteExpiry ? "Xóa hạn gói, giữ gói không giới hạn" : "Tài khoản không có hạn gói để xóa"}" aria-label="${canDeleteExpiry ? "Xóa hạn gói" : "Không có hạn gói để xóa"}">${deleteExpiryIconSvg()}</button>`;
    return `<tr>
      <td><strong>${escapeHtml(getValue(account, "username") || "Tài khoản")}</strong></td>
      <td>${escapeHtml(getValue(account, "email"))}</td>
      <td><span class="badge tour-status-badge status-selling">${escapeHtml(getValue(account, "plan_role", "planRole", "role") || "Free")}</span></td>
      <td>${planExpireHtml(account)}</td>
      <td>${locked ? '<span class="badge tour-status-badge status-canceled">Bị khóa</span>' : '<span class="badge tour-status-badge status-selling">Hoạt động</span>'}</td>
      <td>${formatDate(getValue(account, "created_at", "createdAt"))}</td>
      <td class="manage-action-cell"><div class="manage-account-actions">${editButton}${deleteExpiryButton}</div></td>
    </tr>`;
  }).join("");
  startManageCountdowns();
}

function settingsIconSvg() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z"></path><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.83 2.83-.06-.06A1.7 1.7 0 0 0 15 19.4a1.7 1.7 0 0 0-1 .6 1.7 1.7 0 0 0-.4 1.1V21h-4v-.1A1.7 1.7 0 0 0 8.6 19.4a1.7 1.7 0 0 0-1.88.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-.6-1 1.7 1.7 0 0 0-1.1-.4H3v-4h.1A1.7 1.7 0 0 0 4.6 8.6a1.7 1.7 0 0 0-.34-1.88l-.06-.06 2.83-2.83.06.06A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-.6 1.7 1.7 0 0 0 .4-1.1V3h4v.1A1.7 1.7 0 0 0 15.4 4a1.7 1.7 0 0 0 1.88-.34l.06-.06 2.83 2.83-.06.06A1.7 1.7 0 0 0 19.4 9c.13.37.35.7.65.96.3.27.68.42 1.08.44H21v4h-.1a1.7 1.7 0 0 0-1.5.6Z"></path></svg>`;
}

function deleteExpiryIconSvg() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3v3M17 3v3M4 9h16"></path><rect x="4" y="5" width="16" height="16" rx="2"></rect><path d="m9 13 6 6M15 13l-6 6"></path></svg>`;
}

function toDateTimeLocal(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function setManagePlanExpiryState() {
  const role = document.getElementById("managePlanRole")?.value || "Free";
  const input = document.getElementById("managePlanExpiresAt");
  if (!input) return;
  const isFree = role === "Free";
  input.disabled = isFree;
  input.required = !isFree;
  if (isFree) {
    input.value = "";
  } else if (!input.value) {
    const defaultExpiry = new Date();
    defaultExpiry.setMonth(defaultExpiry.getMonth() + 1);
    input.value = toDateTimeLocal(defaultExpiry.toISOString());
  }
}

function openManagePlanSettings(accountId) {
  const account = manageAccounts.find(item => String(getValue(item, "id")) === String(accountId));
  if (!account) return showToast("Không tìm thấy tài khoản");
  const rawRole = String(getValue(account, "plan_role", "planRole", "role") || "Free");
  const role = rawRole.toLowerCase() === "business" ? "Company" : rawRole;
  if (role.toLowerCase() === "admin") return showToast("Không thể sửa gói Admin");
  const modal = document.getElementById("managePlanModal");
  document.getElementById("managePlanAccountId").value = accountId;
  document.getElementById("managePlanAccountLabel").textContent = `${getValue(account, "username") || "Tài khoản"} · ${getValue(account, "email") || ""}`;
  document.getElementById("managePlanRole").value = ["Free", "VIP", "Premium", "Sales", "Company"].includes(role) ? role : "Free";
  document.getElementById("managePlanExpiresAt").value = toDateTimeLocal(getValue(account, "plan_expires_at", "planExpiresAt"));
  document.getElementById("managePlanExpiresAt").min = toDateTimeLocal(new Date(Date.now() + 60000).toISOString());
  setManagePlanExpiryState();
  modal?.classList.add("open");
  modal?.setAttribute("aria-hidden", "false");
}

function closeManagePlanSettings() {
  const modal = document.getElementById("managePlanModal");
  modal?.classList.remove("open");
  modal?.setAttribute("aria-hidden", "true");
}

async function deleteManagePlanExpiry(accountId, button) {
  const account = manageAccounts.find(item => String(getValue(item, "id")) === String(accountId));
  if (!account) return showToast("Không tìm thấy tài khoản");
  const name = getValue(account, "username") || getValue(account, "email") || "tài khoản này";
  const expiresAt = getValue(account, "plan_expires_at", "planExpiresAt");
  if (!expiresAt) return showToast("Tài khoản này không có hạn gói để xóa");
  const accepted = window.confirm(`Xóa hạn gói của ${name}? Gói hiện tại sẽ được giữ nguyên và chuyển thành không giới hạn thời gian.`);
  if (!accepted) return;
  if (button) button.disabled = true;
  try {
    const response = await authenticatedFetch(`/api/manage/accounts/${encodeURIComponent(accountId)}/plan-expiry`, {
      method: "DELETE"
    });
    const result = await readJson(response);
    showToast(result.message || "Đã xóa hạn gói");
    await loadManage();
  } catch (error) {
    showToast(error.message);
    if (button) button.disabled = false;
  }
}

async function saveManagePlanSettings(event) {
  event.preventDefault();
  const accountId = document.getElementById("managePlanAccountId")?.value || "";
  const role = document.getElementById("managePlanRole")?.value || "Free";
  const expiresInput = document.getElementById("managePlanExpiresAt")?.value || "";
  if (!accountId) return showToast("Thiếu tài khoản cần cập nhật");
  let expiresAt = null;
  if (role !== "Free") {
    const expiresDate = new Date(expiresInput);
    if (!expiresInput || Number.isNaN(expiresDate.getTime()) || expiresDate.getTime() <= Date.now()) {
      return showToast("Hạn gói phải lớn hơn thời điểm hiện tại");
    }
    expiresAt = expiresDate.toISOString();
  }
  const button = document.getElementById("saveManagePlanButton");
  if (button) button.disabled = true;
  try {
    const response = await authenticatedFetch(`/api/manage/accounts/${encodeURIComponent(accountId)}/plan`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ role, expiresAt })
    });
    const result = await readJson(response);
    closeManagePlanSettings();
    showToast(result.message || "Đã cập nhật gói tài khoản");
    await loadManage();
  } catch (error) {
    showToast(error.message);
  } finally {
    if (button) button.disabled = false;
  }
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
  const rows = q ? manageOrders.filter(o => normalizeSearchText([getValue(o, "buyer_name", "buyerName"), getValue(o, "buyer_email", "buyerEmail"), getValue(o, "plan_role", "planRole"), getValue(o, "style_name", "styleName"), getValue(o, "status")].join(" ")).includes(q)) : manageOrders;
  if (!rows.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Chưa có đơn gói.</td></tr>';
    return;
  }
  body.innerHTML = rows.map(order => {
    const id = getValue(order, "id", "Id");
    const status = getValue(order, "status") || "Khách đặt";
    const pending = String(status).toLowerCase() === "khách đặt";
    const isStyleOrder = String(getValue(order, "order_type", "orderType") || "").toLowerCase() === "chatbot_style";
    const months = Number(getValue(order, "duration_months", "durationMonths") || 1);
    const soldStart = getValue(order, "plan_started_at", "planStartedAt");
    const soldExpire = getValue(order, "plan_expires_at", "planExpiresAt");
    const currentExpire = getValue(order, "current_plan_expires_at", "currentPlanExpiresAt");
    const productName = isStyleOrder ? `Phong cách: ${getValue(order, "style_name", "styleName") || "Chatbot"}` : (getValue(order, "plan_role", "planRole") || "Gói");
    return `<tr>
      <td><strong>${escapeHtml(getValue(order, "buyer_name", "buyerName") || "Người mua")}</strong><br><small>${escapeHtml(getValue(order, "buyer_email", "buyerEmail"))}</small></td>
      <td><strong>${escapeHtml(productName)}</strong><br><small>${isStyleOrder ? "Vĩnh viễn" : `${months} tháng`}</small></td>
      <td>${orderPriceHtml(order)}</td>
      <td>${isStyleOrder ? "<strong>Vĩnh viễn</strong>" : (soldExpire ? `<strong>${formatDateTime(soldExpire)}</strong>${soldStart ? `<br><small>${formatDateTime(soldStart)}</small>` : ""}` : `<strong>${months} tháng</strong>${currentExpire ? `<br><small>${formatDateTime(currentExpire)}</small>` : ""}`)}</td>
      <td><span class="badge tour-status-badge ${pending ? 'status-booked' : 'status-selling'}">${escapeHtml(status)}</span><br><small>${formatDate(getValue(order, "created_at", "createdAt"))}</small></td>
      <td class="manage-action-cell"><div class="inline-actions manage-inline-actions">${pending ? `<button class="btn-primary" type="button" onclick="sellPlanOrder('${escapeHtml(id)}')">Bán</button>` : ""}<button class="btn-danger" type="button" onclick="deletePlanOrder('${escapeHtml(id)}')">Xóa</button></div></td>
    </tr>`;
  }).join("");
}


function viewIconSvg() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"></path><circle cx="12" cy="12" r="2.75"></circle></svg>`;
}

function applicationDetailHtml(app, label, keys, full = false) {
  const raw = getValue(app, ...keys);
  const value = String(raw ?? "").trim();
  const content = value
    ? escapeHtml(value).replace(/\r?\n/g, "<br>")
    : '<span class="manage-application-empty">Chưa cung cấp</span>';
  return `<div class="manage-application-detail-item${full ? " full" : ""}">
    <span class="manage-application-detail-label">${escapeHtml(label)}</span>
    <div class="manage-application-detail-value">${content}</div>
  </div>`;
}

function openBusinessApplicationView(id) {
  const app = manageApplications.find(item => String(getValue(item, "id", "Id")) === String(id));
  if (!app) return showToast("Không tìm thấy biểu mẫu");

  const modal = document.getElementById("manageApplicationViewModal");
  const content = document.getElementById("manageApplicationViewContent");
  if (!modal || !content) return showToast("Không mở được chi tiết biểu mẫu");

  const planRole = getValue(app, "plan_role", "planRole") || "Sales / Company";
  const status = getValue(app, "status") || "Chờ xử lý";
  const senderName = getValue(app, "account_name", "contact_name", "contactName") || "Người gửi";
  const senderEmail = getValue(app, "account_email", "user_email", "userEmail");
  const pending = String(status).toLowerCase() === "chờ xử lý";

  document.getElementById("manageApplicationViewTitle").textContent = `Biểu mẫu đăng ký ${planRole}`;
  document.getElementById("manageApplicationViewSubtitle").textContent = senderEmail
    ? `${senderName} · ${senderEmail}`
    : senderName;

  content.innerHTML = `
    <div class="manage-application-detail-item">
      <span class="manage-application-detail-label">Gói đăng ký</span>
      <div class="manage-application-detail-value"><strong>${escapeHtml(planRole)}</strong></div>
    </div>
    <div class="manage-application-detail-item">
      <span class="manage-application-detail-label">Trạng thái</span>
      <div class="manage-application-detail-value"><span class="badge tour-status-badge ${pending ? "status-booked" : "status-selling"}">${escapeHtml(status)}</span></div>
    </div>
    ${applicationDetailHtml(app, "Tài khoản gửi", ["account_name", "contact_name", "contactName"])}
    ${applicationDetailHtml(app, "Email tài khoản", ["account_email", "user_email", "userEmail"])}
    ${applicationDetailHtml(app, "Tên công ty / cá nhân kinh doanh", ["company_name", "companyName"], true)}
    ${applicationDetailHtml(app, "Loại hình", ["business_type", "businessType"])}
    ${applicationDetailHtml(app, "Mã số thuế / CMND", ["tax_code", "taxCode"])}
    ${applicationDetailHtml(app, "Địa chỉ văn phòng", ["office_address", "officeAddress"], true)}
    ${applicationDetailHtml(app, "Tỉnh / Thành phố", ["province"])}
    ${applicationDetailHtml(app, "Website / Fanpage", ["website"])}
    ${applicationDetailHtml(app, "Họ và tên người phụ trách", ["contact_name", "contactName"])}
    ${applicationDetailHtml(app, "Chức vụ", ["position"])}
    ${applicationDetailHtml(app, "Số điện thoại", ["phone"])}
    ${applicationDetailHtml(app, "Email liên hệ", ["email"])}
    ${applicationDetailHtml(app, "Ngày gửi", ["created_at", "createdAt"])}
    ${applicationDetailHtml(app, "Ngày duyệt", ["approved_at", "approvedAt"])}
  `;


  const detailItems = content.querySelectorAll(".manage-application-detail-item");
  if (detailItems.length >= 2) {
    const createdValue = getValue(app, "created_at", "createdAt");
    const approvedValue = getValue(app, "approved_at", "approvedAt");
    const createdItem = detailItems[detailItems.length - 2]?.querySelector(".manage-application-detail-value");
    const approvedItem = detailItems[detailItems.length - 1]?.querySelector(".manage-application-detail-value");
    if (createdItem) createdItem.textContent = createdValue ? formatDateTime(createdValue) : "Chưa cung cấp";
    if (approvedItem) approvedItem.textContent = approvedValue ? formatDateTime(approvedValue) : "Chưa duyệt";
  }

  modal.classList.add("open");
  modal.setAttribute("aria-hidden", "false");
  document.getElementById("closeManageApplicationViewModal")?.focus();
}

function closeBusinessApplicationView() {
  const modal = document.getElementById("manageApplicationViewModal");
  if (!modal) return;
  modal.classList.remove("open");
  modal.setAttribute("aria-hidden", "true");
}

function renderManageApplications() {
  const body = document.getElementById("manageApplicationTableBody");
  if (!body) return;
  if (!manageApplications.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Chưa có biểu mẫu Sales / Company.</td></tr>';
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
      <td class="manage-action-cell"><div class="inline-actions manage-inline-actions manage-application-actions">${pending ? `<button class="btn-primary" type="button" onclick="approveBusinessApplication('${escapeHtml(id)}')">Duyệt</button>` : ""}<button class="btn-soft manage-view-application-btn" type="button" onclick="openBusinessApplicationView('${escapeHtml(id)}')" title="Xem biểu mẫu đã điền" aria-label="Xem biểu mẫu đã điền">${viewIconSvg()}<span>Xem</span></button><button class="btn-danger" type="button" onclick="deleteBusinessApplication('${escapeHtml(id)}')">Xóa</button></div></td>
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

document.addEventListener("DOMContentLoaded", () => {
  setupSearch();
  document.getElementById("managePlanRole")?.addEventListener("change", setManagePlanExpiryState);
  document.getElementById("managePlanForm")?.addEventListener("submit", saveManagePlanSettings);
  document.getElementById("closeManagePlanModal")?.addEventListener("click", closeManagePlanSettings);
  document.getElementById("cancelManagePlanModal")?.addEventListener("click", closeManagePlanSettings);
  document.getElementById("managePlanModal")?.addEventListener("click", event => { if (event.target?.id === "managePlanModal") closeManagePlanSettings(); });
  document.getElementById("closeManageApplicationViewModal")?.addEventListener("click", closeBusinessApplicationView);
  document.getElementById("dismissManageApplicationViewModal")?.addEventListener("click", closeBusinessApplicationView);
  document.getElementById("manageApplicationViewModal")?.addEventListener("click", event => { if (event.target?.id === "manageApplicationViewModal") closeBusinessApplicationView(); });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    closeManagePlanSettings();
    closeBusinessApplicationView();
  });
  loadManage();
});
window.openManagePlanSettings = openManagePlanSettings;
window.sellPlanOrder = sellPlanOrder;
window.deletePlanOrder = deletePlanOrder;
window.approveBusinessApplication = approveBusinessApplication;
window.openBusinessApplicationView = openBusinessApplicationView;
window.deleteBusinessApplication = deleteBusinessApplication;


document.addEventListener("visibilitychange", function () {
  if (document.hidden) {
    clearTimeout(managePendingOrderRefreshTimer);
    managePendingOrderRefreshTimer = null;
    return;
  }

  schedulePendingOrderRefresh();
});
