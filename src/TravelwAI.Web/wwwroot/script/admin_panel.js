
async function readAdminJson(response) {
  const contentType = String(response?.headers?.get?.("content-type") || "").toLowerCase();
  let result = {};
  try {
    if (contentType.includes("application/json")) {
      result = await response.json();
    } else {
      const text = await response.text();
      result = text ? { message: text } : {};
    }
  } catch (error) {
    result = {};
  }

  if (!response?.ok || result?.success === false) {
    const message = result?.message || result?.error || `Không tải được dữ liệu (${response?.status || "lỗi mạng"}).`;
    throw new Error(message);
  }
  return result || {};
}


function runAdminSetupSafely(callback, label) {
  try {
    callback();
  } catch (error) {
    console.error(`Lỗi khởi tạo ${label}:`, error);
  }
}

let travelwaiAccounts = [];
let adminStorageUsers = [];
let adminStorageOverview = null;
let adminStorageDetails = null;
let adminStorageSelectedUserId = "";
let adminStorageSearchQuery = "";
let adminStorageLoaded = false;
let adminStorageLoading = false;
let adminStorageLimitSaving = false;
let travelwaiSchedules = [];
let travelwaiPlanStatuses = [];
let travelwaiProvinceTags = [];
let travelwaiAllowedTags = [];
let travelwaiTravelTags = [];
let accountSearchQuery = "";
let scheduleSearchQuery = "";
let planStatusSearchQuery = "";
let provinceTagSearchQuery = "";
let travelwaiAccountRevenue = [];
let revenueSearchQuery = "";
let adminRevenueLoaded = false;
let adminRevenueLoading = false;
let travelwaiPosts = [];
let postSearchQuery = "";
let selectedAdminPostMediaFiles = [];
let adminPostPreviewObjectUrls = [];
let adminPostAiGenerationSessionId = "";
let adminPostAiGenerationId = "";
let adminPostAiAbortController = null;
let adminPostAiStreamReader = null;
let selectedSiteBackgroundTheme = "light";
let adminSiteLogoUploading = false;
let chatbotStyleLoaded = false;
let chatbotStyleSaving = false;
let chatbotStylesDraft = [];
let chatbotDefaultStyleId = "";
let chatbotActiveStyleId = "";
let chatbotStyleSearchQuery = "";
let salesLevelSettings = [
  { level: 1, commissionPercent: 8, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 2, commissionPercent: 12, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 3, commissionPercent: 15, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 4, commissionPercent: 18, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 5, commissionPercent: 20, offerDiscountPercent: 0, servicePercent: 0 }
];
let accountPlanSettings = [
  { role: "Free", name: "Free", price: "0Đ", subtitle: "Dùng thử cơ bản", note: "Miễn phí", cta: "Bắt đầu miễn phí", requiresPayment: false, benefits: ["AI tạo bài viết 2 lần / 10 phút", "Chatbot 3 câu / 10 phút", "Không dùng AI lập lịch trình", "Không đổi phong cách chatbot", "Không dùng ưu đãi bài viết"] },
  { role: "VIP", name: "VIP", price: "59.000Đ", subtitle: "Có lịch trình", note: "Theo tháng", cta: "Nâng cấp VIP", requiresPayment: true, benefits: ["AI tạo bài viết 5 lần / 10 phút", "Chatbot 7 câu / 10 phút", "Đổi phong cách miễn phí hoặc đã mua", "Không dùng AI lập lịch trình", "Không dùng ưu đãi bài viết"] },
  { role: "Premium", name: "Premium", price: "129.000Đ", subtitle: "Đầy đủ tính năng", note: "Đầy đủ", cta: "Nâng cấp Premium", requiresPayment: true, benefits: ["Đầy đủ tính năng của VIP", "Ưu đãi bài viết", "Không giới hạn lập lịch trình"] },
  { role: "Sales", name: "Sales", price: "Đăng ký", subtitle: "Bán tour và nhận hoa hồng", note: "Thu phí đăng ký", cta: "Đăng ký Sales", requiresPayment: true, benefits: ["Tài khoản kinh doanh Sales", "Quản lý tour đã tạo", "Xem đơn bán tour", "Nhận hoa hồng theo cấp"] },
  { role: "Company", name: "Company", price: "Đăng ký", subtitle: "Đối tác tour và dịch vụ", note: "Thu phí đăng ký", cta: "Đăng ký Company", requiresPayment: true, benefits: ["Tài khoản kinh doanh Company", "Quản lý tour của doanh nghiệp", "Xem doanh thu Company", "Tính phí dịch vụ theo cấp"] }
];

const adminPlanStatusColors = {
  binh_thuong: "#e5e7eb",
  di_bien: "#0ea5e9",
  len_nui: "#22c55e",
  di_tich_lich_su: "#f97316",
  nghi_duong: "#a855f7",
  tuan_trang_mat: "#ec4899",
  team_building: "#14b8a6",
  giai_tri: "#eab308",
};

const adminPlanTagColors = {
  bien: "#0ea5e9",
  nui: "#22c55e",
  di_tich_lich_su: "#f97316",
  tho_mong: "#ec4899",
  khu_vui_choi: "#eab308",
};

function applyAdminTravelTags(result) {
  const tags = Array.isArray(result?.travel_tags) ? result.travel_tags : [];
  if (tags.length) {
    travelwaiTravelTags = tags;
    travelwaiAllowedTags = tags
      .map(tag => tag?.name || tag?.label || "")
      .filter(Boolean);
    tags.forEach(tag => {
      const name = tag?.name || tag?.label || "";
      const color = String(tag?.color || "").trim();
      if (name && /^#[0-9a-f]{6}$/i.test(color)) {
        adminPlanTagColors[normalizeAdminColorKey(name)] = color;
      }
    });
    renderTravelTagExistingList();
    return;
  }
  if (Array.isArray(result?.allowed_tags)) {
    travelwaiAllowedTags = result.allowed_tags;
    renderTravelTagExistingList();
  }
}

function adminActionIcon(type) {
  if (type === "edit") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>`;
  }
  if (type === "hide") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M17.9 17.9A10.9 10.9 0 0 1 12 20C5 20 2 12 2 12a18.6 18.6 0 0 1 4.1-5.9"/><path d="M9.9 4.2A10.4 10.4 0 0 1 12 4c7 0 10 8 10 8a18.3 18.3 0 0 1-2.2 3.3"/><path d="M14.1 14.1a3 3 0 0 1-4.2-4.2"/><path d="M3 3l18 18"/></svg>`;
  }
  if (type === "sell") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 1v22"/><path d="M17 5H9.5a3.5 3.5 0 0 0 0 7H14.5a3.5 3.5 0 0 1 0 7H6"/></svg>`;
  }
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v5"/><path d="M14 11v5"/></svg>`;
}

function adminIconButton(className, iconType, label, onClick) {
  return `<button class="${className} admin-table-icon-button" type="button" onclick="${onClick}" title="${escapeHtml(label)}" aria-label="${escapeHtml(label)}">${adminActionIcon(iconType)}</button>`;
}

function escapeAttr(value) {
  return escapeHtml(value).replace(/`/g, "&#96;");
}

function stripAccountRolePrefix(value) {
  let text = String(value ?? "").trim();
  let changed = true;
  while (changed) {
    changed = false;
    for (const prefix of ["Admin-", "Sales-", "Company-", "Business-"]) {
      if (text.toLowerCase().startsWith(prefix.toLowerCase())) {
        text = text.slice(prefix.length).trim();
        changed = true;
      }
    }
  }
  return text;
}

function cleanAccountDisplayName(value) {
  return stripAccountRolePrefix(String(value ?? "")
    .replace(/^\s*Tài\s*khoản\s+/i, "")
    .trim());
}

function updateAdminPageRoleLinks() {
  if (typeof updateAdminRolePageLinks === "function") {
    updateAdminRolePageLinks();
    return;
  }
  const isAdmin = String(localStorage.getItem("userRole") || "").trim().toLowerCase() === "admin";
  document.querySelectorAll(".admin-role-page-link").forEach(link => {
    link.style.display = isAdmin ? "inline-flex" : "none";
  });
}

async function runAdminLoader(loader, label) {
  try {
    await loader();
  } catch (error) {
    console.error(`Không tải được ${label}:`, error);
  }
}

function clearStuckAdminLoadingStates(message = "Không tải được dữ liệu. Vui lòng thử tải lại trang.") {
  document.querySelectorAll(".empty-line").forEach(element => {
    const text = element.textContent || "";
    const normalized = getAdminSearchValue(text);
    if (normalized.includes("dang tai") || /loading/i.test(text)) element.textContent = message;
  });
}

function deferAdminWork(callback) {
  if (typeof window.requestIdleCallback === "function") {
    window.requestIdleCallback(callback, { timeout: 1000 });
    return;
  }
  window.setTimeout(callback, 80);
}

function adminTimeoutSignal(timeoutMs) {
  if (typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function") {
    return AbortSignal.timeout(timeoutMs);
  }
  const controller = new AbortController();
  window.setTimeout(() => controller.abort(), timeoutMs);
  return controller.signal;
}

async function loadAdminPage() {
  updateAdminPageRoleLinks();

  const criticalLoaders = [
    [() => loadSalesLevelSettings(true), "cấu hình cấp"],
    [loadAccounts, "tài khoản"],
    [loadAdminDashboard, "tổng quan"]
  ];
  const backgroundLoaders = [
    [() => loadAccountPlanSettings(true), "bảng giá"],
    [loadTours, "tour"],
    [loadSchedules, "lịch trình"],
    [loadPlanStatusOptions, "trạng thái"],
    [loadProvinceTags, "tỉnh thành"],
    [loadPosts, "bài viết"]
  ];

  await Promise.allSettled(criticalLoaders.map(([loader, label]) => runAdminLoader(loader, label)));
  deferAdminWork(() => {
    Promise.allSettled(backgroundLoaders.map(([loader, label]) => runAdminLoader(loader, label)))
      .then(() => clearStuckAdminLoadingStates());
  });
}

async function loadAdminDashboard() {
  try {
    const response = await authenticatedFetch("/api/admin/dashboard");
    const result = await readAdminJson(response);
    const data = result.data || {};
    setText("adminStatAccounts", data.accounts || 0);
    setText("adminStatLocked", data.lockedAccounts || 0);
    setText("adminStatTours", data.tours || 0);
    setText("adminStatSchedules", data.schedules || 0);
    setText("adminStatPosts", data.posts || travelwaiPosts.length || 0);
  } catch (error) {
    console.error(error);
    showToast(error.message);
  }
}

async function loadAccounts() {
  const body = document.getElementById("accountTableBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="6" class="empty-line">Đang tải tài khoản...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/accounts");
    const result = await readAdminJson(response);
    travelwaiAccounts = Array.isArray(result.data) ? result.data : [];
    if (typeof setAvailableTourSalesAccounts === "function") setAvailableTourSalesAccounts(travelwaiAccounts);
    renderAccounts();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}


function formatAdminStorageBytes(value) {
  const bytes = Math.max(0, Number(value) || 0);
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes >= 100 * 1024 ? 0 : 1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(bytes >= 100 * 1024 * 1024 ? 0 : 1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function getAdminStorageCategoryLabel(category) {
  const labels = {
    profiles: "Ảnh đại diện",
    chat: "Tin nhắn",
    "ai-chat": "Chatbot",
    posts: "Bài viết",
    memories: "Nhật ký",
    tours: "Tour",
    feedback: "Phản hồi"
  };
  return labels[String(category || "").toLowerCase()] || "Khác";
}

function getAdminStoragePercent(usage) {
  const used = Math.max(0, Number(usage?.usedBytes) || 0);
  const limit = Math.max(0, Number(usage?.limitBytes) || 0);
  return Math.min(100, Math.max(0, Number(usage?.usedPercent) || (limit > 0 ? used / limit * 100 : 0)));
}

function renderAdminStorageProgress(track, bar, usage) {
  const percent = getAdminStoragePercent(usage);
  if (bar) bar.style.width = `${percent}%`;
  if (track) {
    track.setAttribute("aria-valuenow", String(Math.round(percent)));
    track.classList.toggle("is-warning", percent >= 80 && percent < 100);
    track.classList.toggle("is-full", percent >= 100);
  }
}

function formatAdminStorageLimitGigabytes(bytes) {
  const value = Math.max(0, Number(bytes) || 0) / (1024 ** 3);
  if (!Number.isFinite(value)) return "1";
  return String(Number(value.toFixed(3)) || 0);
}

function syncAdminStorageLimitEditor() {
  const input = document.getElementById("adminStorageLimitInput");
  if (!input || document.activeElement === input) return;
  input.value = formatAdminStorageLimitGigabytes(adminStorageOverview?.limitBytes || 1024 ** 3);
}

function renderAdminStorageOverview() {
  const data = adminStorageOverview || {};
  setText("adminStorageTotalText", `${formatAdminStorageBytes(data.usedBytes)} / ${formatAdminStorageBytes(data.limitBytes)}`);
  syncAdminStorageLimitEditor();
  setText("adminStorageAccountCount", `${Math.max(0, Number(data.accountCount) || 0)} tài khoản`);
  setText("adminStorageImageCount", `${Math.max(0, Number(data.imageCount) || 0)} ảnh`);
  renderAdminStorageProgress(
    document.getElementById("adminStorageTotalTrack"),
    document.getElementById("adminStorageTotalBar"),
    data
  );
}

function renderAdminStorageUsers() {
  const body = document.getElementById("adminStorageUserBody");
  if (!body) return;
  const query = getAdminSearchValue(adminStorageSearchQuery);
  const users = adminStorageUsers.filter(user => {
    if (!query) return true;
    return getAdminSearchValue(`${user.username || ""} ${user.email || ""} ${user.role || ""}`).includes(query);
  });

  if (!users.length) {
    body.innerHTML = '<tr><td colspan="4" class="empty-line">Không có tài khoản.</td></tr>';
    return;
  }

  body.innerHTML = users.map(user => {
    const usage = user.usage || {};
    const percent = getAdminStoragePercent(usage);
    const isSelected = String(user.id) === String(adminStorageSelectedUserId);
    return `
      <tr class="${isSelected ? "is-selected" : ""}">
        <td>
          <div class="admin-storage-user-name" data-no-translate>${escapeHtml(user.username || user.email || "Tài khoản")}</div>
          <small data-no-translate>${escapeHtml(user.email || "")}</small>
        </td>
        <td>
          <div class="admin-storage-user-usage">${formatAdminStorageBytes(usage.usedBytes)} / ${formatAdminStorageBytes(usage.limitBytes)}</div>
          <div class="admin-storage-mini-progress"><span style="width:${percent}%"></span></div>
        </td>
        <td>${Math.max(0, Number(usage.imageCount) || 0)}</td>
        <td>
          <button type="button" class="admin-storage-view-button" data-storage-user="${escapeAttr(user.id)}" title="Xem" aria-label="Xem">
            <span data-interface-icon="eye"></span>
          </button>
        </td>
      </tr>`;
  }).join("");
  window.TravelwAIInterfaceIcons?.refresh?.(body);
  window.TravelwAIIcons?.render?.(body);
}

function renderAdminStorageDetail() {
  const panel = document.getElementById("adminStorageDetail");
  if (!panel) return;
  const data = adminStorageDetails;
  if (!data?.user) {
    panel.innerHTML = '<div class="admin-storage-detail-empty">Chọn tài khoản</div>';
    return;
  }

  const user = data.user;
  const usage = data.usage || {};
  const items = Array.isArray(data.items) ? data.items : [];
  const grouped = new Map();
  items.forEach(item => {
    const category = String(item.category || "other");
    if (!grouped.has(category)) grouped.set(category, []);
    grouped.get(category).push(item);
  });

  const categoryOrder = ["chat", "ai-chat", "feedback", "profiles", "posts", "memories", "tours", "other"];
  const groupsHtml = [...grouped.entries()]
    .sort((a, b) => {
      const ai = categoryOrder.indexOf(a[0]);
      const bi = categoryOrder.indexOf(b[0]);
      return (ai < 0 ? 999 : ai) - (bi < 0 ? 999 : bi);
    })
    .map(([category, categoryItems]) => {
      const total = categoryItems.reduce((sum, item) => sum + Math.max(0, Number(item.fileSize) || 0), 0);
      const itemsHtml = categoryItems.map(item => {
        const createdAt = item.createdAt ? new Date(item.createdAt) : null;
        const dateText = createdAt && !Number.isNaN(createdAt.getTime())
          ? createdAt.toLocaleDateString("vi-VN")
          : "";
        return `
          <article class="admin-storage-file-card">
            <a href="${escapeAttr(item.publicUrl || "#")}" target="_blank" rel="noopener" class="admin-storage-file-preview">
              <img src="${escapeAttr(item.publicUrl || "")}" alt="" loading="lazy" />
            </a>
            <div class="admin-storage-file-info">
              <strong>${formatAdminStorageBytes(item.fileSize)}</strong>
              <small>${escapeHtml(dateText)}</small>
            </div>
            <button type="button" class="admin-storage-delete-item" data-storage-delete-item="${escapeAttr(item.id)}" data-storage-user-id="${escapeAttr(user.id)}" title="Xóa" aria-label="Xóa">
              <span data-interface-icon="trash-2"></span>
            </button>
          </article>`;
      }).join("");
      return `
        <section class="admin-storage-category">
          <div class="admin-storage-category-heading">
            <strong>${escapeHtml(getAdminStorageCategoryLabel(category))}</strong>
            <span>${categoryItems.length} · ${formatAdminStorageBytes(total)}</span>
          </div>
          <div class="admin-storage-file-grid">${itemsHtml}</div>
        </section>`;
    }).join("");

  panel.innerHTML = `
    <div class="admin-storage-detail-header">
      <div>
        <h3 data-no-translate>${escapeHtml(user.username || user.email || "Tài khoản")}</h3>
        <p data-no-translate>${escapeHtml(user.email || "")}</p>
      </div>
      <button type="button" class="admin-storage-delete-all" data-storage-delete-user="${escapeAttr(user.id)}" ${items.length ? "" : "disabled"}>
        <span data-interface-icon="trash-2"></span><span>Xóa tất cả</span>
      </button>
    </div>
    <div class="admin-storage-detail-usage">
      <strong>${formatAdminStorageBytes(usage.usedBytes)} / ${formatAdminStorageBytes(usage.limitBytes)}</strong>
      <div class="admin-storage-progress" data-storage-detail-track role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><span data-storage-detail-bar></span></div>
    </div>
    <div class="admin-storage-detail-content">
      ${groupsHtml || '<div class="admin-storage-detail-empty">Không có ảnh.</div>'}
    </div>`;
  renderAdminStorageProgress(
    panel.querySelector("[data-storage-detail-track]"),
    panel.querySelector("[data-storage-detail-bar]"),
    usage
  );
  window.TravelwAIInterfaceIcons?.refresh?.(panel);
  window.TravelwAIIcons?.render?.(panel);
}

async function saveAdminStorageLimit() {
  if (adminStorageLimitSaving) return;
  const input = document.getElementById("adminStorageLimitInput");
  const button = document.getElementById("saveAdminStorageLimit");
  const gigabytes = Number(input?.value);
  if (!Number.isFinite(gigabytes) || gigabytes <= 0) {
    showToast("Tổng hạn mức không hợp lệ.");
    input?.focus();
    return;
  }

  const limitBytes = Math.round(gigabytes * (1024 ** 3));
  adminStorageLimitSaving = true;
  if (button) button.disabled = true;
  try {
    const response = await authenticatedFetch("/api/admin/storage/limit", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ limitBytes })
    });
    const result = await readAdminJson(response);
    const usage = result.data || {};
    adminStorageOverview = {
      ...(adminStorageOverview || {}),
      ...usage
    };
    renderAdminStorageOverview();
    showToast(result.message || "Đã lưu tổng hạn mức.");
  } catch (error) {
    showToast(error.message);
  } finally {
    adminStorageLimitSaving = false;
    if (button) button.disabled = false;
  }
}

async function loadAdminStorage(force = false) {
  if (adminStorageLoading || (adminStorageLoaded && !force)) return;
  const body = document.getElementById("adminStorageUserBody");
  adminStorageLoading = true;
  if (body) body.innerHTML = '<tr><td colspan="4" class="empty-line">Đang tải...</td></tr>';
  try {
    const response = await authenticatedFetch("/api/admin/storage");
    const result = await readAdminJson(response);
    adminStorageOverview = result.data || {};
    adminStorageUsers = Array.isArray(result.data?.users) ? result.data.users : [];
    adminStorageLoaded = true;
    renderAdminStorageOverview();
    renderAdminStorageUsers();
  } catch (error) {
    if (body) body.innerHTML = `<tr><td colspan="4" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
    showToast(error.message);
  } finally {
    adminStorageLoading = false;
  }
}

async function openAdminStorageUser(userId) {
  const id = String(userId || "").trim();
  if (!id) return;
  adminStorageSelectedUserId = id;
  renderAdminStorageUsers();
  const panel = document.getElementById("adminStorageDetail");
  if (panel) panel.innerHTML = '<div class="admin-storage-detail-empty">Đang tải...</div>';
  try {
    const response = await authenticatedFetch(`/api/admin/storage/${encodeURIComponent(id)}`);
    const result = await readAdminJson(response);
    adminStorageDetails = result.data || null;
    renderAdminStorageDetail();
  } catch (error) {
    if (panel) panel.innerHTML = `<div class="admin-storage-detail-empty">${escapeHtml(error.message)}</div>`;
  }
}

async function deleteAdminStorageItem(userId, uploadId) {
  const confirmed = window.TravelwAIConfirm
    ? await window.TravelwAIConfirm("Xóa ảnh này?")
    : window.confirm("Xóa ảnh này?");
  if (!confirmed) return;
  try {
    const response = await authenticatedFetch(
      `/api/admin/storage/${encodeURIComponent(userId)}/items/${encodeURIComponent(uploadId)}`,
      { method: "DELETE" }
    );
    const result = await readAdminJson(response);
    adminStorageDetails = result.data || null;
    renderAdminStorageDetail();
    adminStorageLoaded = false;
    await loadAdminStorage(true);
    renderAdminStorageUsers();
    showToast(result.message || "Đã xóa ảnh.");
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteAllAdminStorage(userId) {
  const confirmed = window.TravelwAIConfirm
    ? await window.TravelwAIConfirm("Xóa toàn bộ ảnh của tài khoản này?")
    : window.confirm("Xóa toàn bộ ảnh của tài khoản này?");
  if (!confirmed) return;
  try {
    const response = await authenticatedFetch(`/api/admin/storage/${encodeURIComponent(userId)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    adminStorageDetails = result.data || null;
    renderAdminStorageDetail();
    adminStorageLoaded = false;
    await loadAdminStorage(true);
    renderAdminStorageUsers();
    showToast(result.message || "Đã xóa toàn bộ ảnh.");
  } catch (error) {
    showToast(error.message);
  }
}

function setupAdminStorage() {
  const body = document.getElementById("adminStorageUserBody");
  const detail = document.getElementById("adminStorageDetail");
  const limitInput = document.getElementById("adminStorageLimitInput");
  const saveLimitButton = document.getElementById("saveAdminStorageLimit");
  saveLimitButton?.addEventListener("click", saveAdminStorageLimit);
  limitInput?.addEventListener("keydown", event => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    saveAdminStorageLimit();
  });
  body?.addEventListener("click", event => {
    const button = event.target.closest("[data-storage-user]");
    if (button) openAdminStorageUser(button.dataset.storageUser);
  });
  detail?.addEventListener("click", event => {
    const itemButton = event.target.closest("[data-storage-delete-item]");
    if (itemButton) {
      deleteAdminStorageItem(itemButton.dataset.storageUserId, itemButton.dataset.storageDeleteItem);
      return;
    }
    const allButton = event.target.closest("[data-storage-delete-user]");
    if (allButton) deleteAllAdminStorage(allButton.dataset.storageDeleteUser);
  });
}

function getAdminSearchValue(value) {
  if (typeof normalizeSearchText === "function") return normalizeSearchText(value);
  return String(value ?? "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/[đĐ]/g, "d")
    .replace(/\s+/g, " ")
    .trim();
}

function clampPercent(value, fallback = 0) {
  const number = Number(value);
  if (!Number.isFinite(number)) return fallback;
  return Math.max(0, Math.min(100, number));
}

function normalizeSalesLevel(value) {
  const level = Number(value);
  if (!Number.isFinite(level)) return 1;
  return Math.max(1, Math.min(5, Math.round(level)));
}

function getSalesLevelSetting(level) {
  const safeLevel = normalizeSalesLevel(level);
  return salesLevelSettings.find(item => Number(item.level) === safeLevel) || salesLevelSettings[0];
}

function normalizeSalesLevelSettings(rows) {
  const source = Array.isArray(rows) ? rows : [];
  return [1, 2, 3, 4, 5].map(level => {
    const current = source.find(item => Number(item?.level) === level) || {};
    const fallback = salesLevelSettings.find(item => Number(item.level) === level) || {};
    return {
      level,
      commissionPercent: clampPercent(current.commission_percent ?? current.commissionPercent ?? fallback.commissionPercent ?? 0),
      offerDiscountPercent: clampPercent(current.offer_discount_percent ?? current.offerDiscountPercent ?? fallback.offerDiscountPercent ?? 0),
      servicePercent: clampPercent(current.service_percent ?? current.servicePercent ?? current.service_fee_percent ?? current.serviceFeePercent ?? fallback.servicePercent ?? 0)
    };
  });
}

async function loadSalesLevelSettings(silent = false) {
  try {
    const response = await authenticatedFetch('/api/admin/sales-level-settings');
    const result = await readAdminJson(response);
    salesLevelSettings = normalizeSalesLevelSettings(result.data || result.levels || []);
    renderSalesLevelSettingsForm();
  } catch (error) {
    if (!silent) showToast(error.message || 'Không tải được cấu hình từng cấp');
    renderSalesLevelSettingsForm();
  }
}

function renderSalesLevelSettingsForm() {
  const grid = document.getElementById('salesLevelSettingsGrid');
  if (!grid) return;
  grid.innerHTML = salesLevelSettings.map(item => `
    <tr>
      <td><strong>Cấp ${item.level}</strong></td>
      <td><input id="salesLevelOffer${item.level}" type="number" min="0" max="100" step="1" value="${item.offerDiscountPercent}" /></td>
      <td><input id="salesLevelCommission${item.level}" type="number" min="0" max="100" step="1" value="${item.commissionPercent}" /></td>
      <td><input id="salesLevelService${item.level}" type="number" min="0" max="100" step="1" value="${item.servicePercent}" /></td>
    </tr>
  `).join('');
}

async function submitSalesLevelSettingsForm(event) {
  event.preventDefault();
  const levels = [1, 2, 3, 4, 5].map(level => ({
    level,
    offerDiscountPercent: clampPercent(document.getElementById(`salesLevelOffer${level}`)?.value || 0),
    commissionPercent: clampPercent(document.getElementById(`salesLevelCommission${level}`)?.value || getSalesLevelSetting(level).commissionPercent),
    servicePercent: clampPercent(document.getElementById(`salesLevelService${level}`)?.value || getSalesLevelSetting(level).servicePercent || 0)
  }));
  try {
    const response = await authenticatedFetch('/api/admin/sales-level-settings', {
      method: 'PUT',
      body: JSON.stringify({ levels })
    });
    const result = await readAdminJson(response);
    salesLevelSettings = normalizeSalesLevelSettings(result.data || levels);
    renderSalesLevelSettingsForm();
    syncAccountLevelFields();
    showToast(result.message || 'Đã lưu cấu hình từng cấp');
  } catch (error) {
    showToast(error.message || 'Không lưu được cấu hình từng cấp');
  }
}

function normalizeAccountPlanRole(value) {
  const role = String(value || "Free").trim().toLowerCase();
  if (role === "user") return "Free";
  if (role === "business") return "Company";
  if (role === "sales" || role === "tour sales" || role === "toursales") return "Sales";
  if (role === "company") return "Company";
  if (role === "vip") return "VIP";
  if (role === "premium") return "Premium";
  return "Free";
}

function parseAccountPlanAmount(value) {
  const digits = String(value ?? "").replace(/[^0-9]/g, "");
  if (!digits) return 0;
  const amount = Number(digits);
  return Number.isFinite(amount) ? Math.max(0, Math.trunc(amount)) : 0;
}

function formatAccountPlanAmount(value) {
  return `${parseAccountPlanAmount(value).toLocaleString("vi-VN")}Đ`;
}

function isAccountPlanMoneyRole(role) {
  return ["Free", "VIP", "Premium"].includes(normalizeAccountPlanRole(role));
}

function normalizeAccountPlanSettings(rows) {
  const source = Array.isArray(rows) ? rows : [];
  return accountPlanSettings.map(fallback => {
    const current = source.find(item => normalizeAccountPlanRole(item?.role) === fallback.role) || {};
    const benefits = Array.isArray(current.benefits) ? current.benefits : fallback.benefits;
    const rawAmount = current.monthlyPriceAmount ?? current.monthly_price_amount;
    const fallbackAmount = parseAccountPlanAmount(fallback.price);
    const monthlyPriceAmount = rawAmount !== undefined && rawAmount !== null && rawAmount !== ""
      ? Math.max(0, Math.trunc(Number(rawAmount) || 0))
      : (parseAccountPlanAmount(current.price || fallback.price) || fallbackAmount);
    const price = isAccountPlanMoneyRole(fallback.role)
      ? formatAccountPlanAmount(monthlyPriceAmount)
      : (current.price || fallback.price);
    return {
      role: fallback.role,
      name: current.name || fallback.name,
      price,
      monthlyPriceAmount,
      subtitle: current.subtitle ?? fallback.subtitle,
      note: current.note || fallback.note,
      cta: current.cta || fallback.cta,
      requiresPayment: Boolean(current.requiresPayment ?? current.requires_payment ?? fallback.requiresPayment),
      benefits: benefits.map(item => String(item || "").trim()).filter(Boolean)
    };
  });
}

async function loadAccountPlanSettings(silent = false) {
  try {
    const response = await fetch('/api/account-plans', { cache: 'no-store', signal: adminTimeoutSignal(15000) });
    const result = await readAdminJson(response);
    accountPlanSettings = normalizeAccountPlanSettings(result.data || result.plans || []);
  } catch (error) {
    if (!silent) showToast(error.message || 'Không tải được bảng giá');
  }
  renderAccountPlanSettingsForm();
}

function renderAccountPlanSettingsForm() {
  const body = document.getElementById('accountPlanSettingsBody');
  if (!body) return;
  body.innerHTML = accountPlanSettings.map(plan => `
    <tr>
      <td><strong>${escapeHtml(plan.name)}</strong><br><small>${escapeHtml(plan.role)}</small><input id="accountPlanName${plan.role}" type="hidden" value="${escapeAttr(plan.name)}" /><input id="accountPlanNote${plan.role}" type="hidden" value="${escapeAttr(plan.note)}" /><input id="accountPlanCta${plan.role}" type="hidden" value="${escapeAttr(plan.cta)}" /></td>
      <td><input id="accountPlanPrice${plan.role}" value="${escapeAttr(plan.price)}" ${isAccountPlanMoneyRole(plan.role) ? 'inputmode="numeric" autocomplete="off" data-account-plan-money="true"' : ''} /></td>
      <td><input id="accountPlanSubtitle${plan.role}" value="${escapeAttr(plan.subtitle)}" /></td>
      <td><textarea id="accountPlanBenefits${plan.role}" rows="4">${escapeHtml(plan.benefits.join('\n'))}</textarea></td>
      <td><select id="accountPlanPayment${plan.role}"><option value="true" ${plan.requiresPayment ? 'selected' : ''}>Có</option><option value="false" ${!plan.requiresPayment ? 'selected' : ''}>Không</option></select></td>
    </tr>
  `).join('');

  body.querySelectorAll('[data-account-plan-money="true"]').forEach(input => {
    const format = () => {
      input.value = formatAccountPlanAmount(input.value);
      const caret = Math.max(0, input.value.length - 1);
      try { input.setSelectionRange(caret, caret); } catch (_) {}
    };
    input.addEventListener('input', format);
    input.addEventListener('blur', format);
    input.addEventListener('focus', () => {
      const caret = Math.max(0, input.value.length - 1);
      try { input.setSelectionRange(caret, caret); } catch (_) {}
    });
  });
}

async function submitAccountPlanSettingsForm(event) {
  event.preventDefault();
  const plans = accountPlanSettings.map(plan => {
    const priceInput = document.getElementById(`accountPlanPrice${plan.role}`)?.value.trim() || plan.price;
    const hasNumericPrice = /[0-9]/.test(priceInput);
    const monthlyPriceAmount = isAccountPlanMoneyRole(plan.role) || hasNumericPrice
      ? parseAccountPlanAmount(priceInput)
      : null;
    return {
      role: plan.role,
      name: document.getElementById(`accountPlanName${plan.role}`)?.value || plan.name,
      price: isAccountPlanMoneyRole(plan.role) ? formatAccountPlanAmount(monthlyPriceAmount) : priceInput,
      monthlyPriceAmount,
      subtitle: document.getElementById(`accountPlanSubtitle${plan.role}`)?.value.trim() ?? plan.subtitle,
      note: document.getElementById(`accountPlanNote${plan.role}`)?.value || plan.note,
      cta: document.getElementById(`accountPlanCta${plan.role}`)?.value || plan.cta,
      requiresPayment: document.getElementById(`accountPlanPayment${plan.role}`)?.value === 'true',
      benefits: (document.getElementById(`accountPlanBenefits${plan.role}`)?.value || '')
        .split(/\n+/)
        .map(item => item.trim())
        .filter(Boolean)
    };
  });
  try {
    const response = await authenticatedFetch('/api/admin/account-plans', {
      method: 'PUT',
      body: JSON.stringify({ plans })
    });
    const result = await readAdminJson(response);
    accountPlanSettings = normalizeAccountPlanSettings(result.data || plans);
    renderAccountPlanSettingsForm();
    const accountPlansUpdatedAt = String(Date.now());
    try { localStorage.setItem('travelwai-account-plans-updated-at', accountPlansUpdatedAt); } catch (_) {}
    window.dispatchEvent(new CustomEvent('travelwai:account-plans-updated', { detail: { plans: accountPlanSettings, updatedAt: accountPlansUpdatedAt } }));
    if (window.TravelwAIPricingPopup?.reload) await window.TravelwAIPricingPopup.reload();
    showToast(result.message || 'Đã lưu bảng giá và cập nhật giá thanh toán thực tế');
  } catch (error) {
    showToast(error.message || 'Không lưu được bảng giá');
  }
}

function getAccountOfferLevel(account) {
  return normalizeSalesLevel(account?.offer_level ?? account?.offerLevel ?? account?.sales_level ?? account?.salesLevel ?? 1);
}

function getAccountCommissionLevel(account) {
  return normalizeSalesLevel(account?.commission_level ?? account?.commissionLevel ?? account?.sales_level ?? account?.salesLevel ?? 1);
}

function getAccountServiceLevel(account) {
  return normalizeSalesLevel(account?.service_level ?? account?.serviceLevel ?? 1);
}

function getAccountOfferPercent(account) {
  const level = getAccountOfferLevel(account);
  const fallback = getSalesLevelSetting(level)?.offerDiscountPercent ?? 0;
  return clampPercent(account?.offer_discount_percent ?? account?.offerDiscountPercent ?? account?.admin_offer_discount_percent ?? account?.adminOfferDiscountPercent ?? fallback, fallback);
}

function getAccountCommissionPercent(account) {
  const level = getAccountCommissionLevel(account);
  const fallback = getSalesLevelSetting(level)?.commissionPercent ?? 8;
  return clampPercent(account?.commission_percent ?? account?.commissionPercent ?? fallback, fallback);
}

function getAccountServicePercent(account) {
  const level = getAccountServiceLevel(account);
  const fallback = getSalesLevelSetting(level)?.servicePercent ?? 0;
  return clampPercent(account?.service_fee_percent ?? account?.serviceFeePercent ?? account?.service_percent ?? account?.servicePercent ?? fallback, fallback);
}

function renderAccountOffer(account) {
  const discount = getAccountOfferPercent(account);
  return `<span class="badge badge-offer">${discount}%</span>`;
}

function getAccountSearchText(account) {
  const locked = account?.is_locked || account?.isLocked ? "Đã khóa" : "Hoạt động";
  const discount = getAccountOfferPercent(account);
  return getAdminSearchValue([
    account?.username,
    account?.email,
    account?.role || "Free",
    locked,
    account?.is_online || account?.isOnline ? "Đang online" : "Đang offline",
    account?.last_seen_at,
    `Ưu đãi ${discount}%`,
    `${getAccountCommissionPercent(account)}% hoa hồng`,
    `${getAccountServicePercent(account)}% dịch vụ`,
    `Cấp hoa hồng ${getAccountCommissionLevel(account)}`,
    `Cấp ưu đãi ${getAccountOfferLevel(account)}`,
    `Cấp dịch vụ ${getAccountServiceLevel(account)}`,
    account?.created_at,
    formatDate(account?.created_at)
  ].join(" "));
}

function renderAccounts() {
  const body = document.getElementById("accountTableBody");
  if (!body) return;

  const query = getAdminSearchValue(accountSearchQuery);
  const visibleAccounts = query
    ? travelwaiAccounts.filter(account => getAccountSearchText(account).includes(query))
    : travelwaiAccounts;

  if (!visibleAccounts.length) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${query ? "Không tìm thấy tài khoản." : "Chưa có tài khoản."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleAccounts.map((account) => {
    const role = account.role || "Free";
    const protectedAdmin = account.is_protected || account.isProtected;
    const locked = account.is_locked || account.isLocked;
    return `
      <tr>
        <td><strong>${escapeHtml(account.username || "Người dùng")}</strong><br><small>${escapeHtml(account.email || "")}</small><br>${account.is_online || account.isOnline ? `<span class="badge badge-online">Đang online</span>` : `<span class="badge badge-offline">Đang offline</span>`}</td>
        <td>${renderAccountRole(account)}</td>
        <td>${locked ? `<span class="badge badge-lock">Đã khóa</span>` : `<span class="badge badge-open">Hoạt động</span>`}</td>
        <td>${renderAccountOffer(account)}</td>
        <td>${formatDate(account.created_at)}</td>
        <td>
          <div class="inline-actions">
            ${adminIconButton("btn-primary", "edit", "Sửa tài khoản", `openAccountModal('${escapeHtml(account.id)}')`)}
            ${protectedAdmin ? "" : adminIconButton("btn-danger", "delete", "Xóa tài khoản", `deleteAccount('${escapeHtml(account.id)}')`)}
          </div>
        </td>
      </tr>`;
  }).join("");
}

async function loadSchedules() {
  const body = document.getElementById("scheduleTableBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="5" class="empty-line">Đang tải lịch trình...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/schedules");
    const result = await readAdminJson(response);
    travelwaiSchedules = Array.isArray(result.data) ? result.data : [];
    renderSchedules();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function getScheduleCreatorName(schedule) {
  const creatorId = schedule?.creator_id || schedule?.creatorId || schedule?.user_id || schedule?.created_by_user_id || schedule?.created_by || "";
  const creatorName = schedule?.creator_name || schedule?.creatorName || schedule?.owner_name || schedule?.ownerName || "";
  const creatorEmail = schedule?.creator_email || schedule?.creatorEmail || schedule?.owner_email || schedule?.ownerEmail || "";

  if (creatorName) return creatorName;
  if (creatorEmail) return creatorEmail;

  const account = travelwaiAccounts.find(item => String(item?.id || "") === String(creatorId || ""));
  if (account) return account.username || account.email || creatorId;

  return creatorId || "Không rõ";
}

function getScheduleSearchText(schedule) {
  const title = schedule?.title || schedule?.name || schedule?.schedule_name || "Lịch trình";
  const userId = schedule?.user_id || schedule?.created_by_user_id || schedule?.created_by || "";
  const creatorName = getScheduleCreatorName(schedule);
  const creatorEmail = schedule?.creator_email || schedule?.creatorEmail || schedule?.owner_email || schedule?.ownerEmail || "";
  const start = schedule?.start_date || schedule?.startDate || "";
  const end = schedule?.end_date || schedule?.endDate || "";
  return getAdminSearchValue([
    title,
    schedule?.description,
    creatorName,
    creatorEmail,
    userId,
    start,
    end,
    schedule?.status || "Đang lưu",
    schedule?.created_at,
    formatDate(schedule?.created_at)
  ].join(" "));
}

function renderSchedules() {
  const body = document.getElementById("scheduleTableBody");
  if (!body) return;

  const query = getAdminSearchValue(scheduleSearchQuery);
  const visibleSchedules = query
    ? travelwaiSchedules.filter(schedule => getScheduleSearchText(schedule).includes(query))
    : travelwaiSchedules;

  if (!visibleSchedules.length) {
    body.innerHTML = `<tr><td colspan="5" class="empty-line">${query ? "Không tìm thấy lịch trình." : "Chưa có lịch trình."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleSchedules.map((schedule) => {
    const id = schedule.id || "";
    const title = schedule.title || schedule.name || schedule.schedule_name || "Lịch trình";
    const creatorName = getScheduleCreatorName(schedule);
    const start = schedule.start_date || schedule.startDate || "";
    const end = schedule.end_date || schedule.endDate || "";
    return `
      <tr>
        <td><strong>${escapeHtml(title)}</strong></td>
        <td>${escapeHtml(creatorName)}</td>
        <td>${escapeHtml(start)} ${end ? "/ " + escapeHtml(end) : ""}</td>
        <td><span class="badge badge-open">${escapeHtml(schedule.status || "Đang lưu")}</span></td>
        <td>${adminIconButton("btn-danger", "delete", "Xóa lịch trình", `deleteSchedule('${escapeHtml(id)}')`)}</td>
      </tr>`;
  }).join("");
}

function normalizeAccountRole(role) {
  const value = String(role || "Free").trim().toLowerCase();
  if (value === "tour sales") return "Sales";
  if (value === "sales") return "Sales";
  if (value === "company" || value === "business") return "Company";
  if (value === "admin") return "Admin";
  if (value === "vip") return "VIP";
  if (value === "premium") return "Premium";
  return "Free";
}

function roleBadge(role) {
  const normalized = normalizeAccountRole(role);
  if (normalized === "Admin") return `<span class="badge badge-admin">Admin</span>`;
  if (normalized === "Sales") return `<span class="badge badge-sales">Sales</span>`;
  if (normalized === "Company") return `<span class="badge badge-sales">Company</span>`;
  if (normalized === "VIP") return `<span class="badge badge-user">VIP</span>`;
  if (normalized === "Premium") return `<span class="badge badge-user">Premium</span>`;
  if (normalized === "Free") return `<span class="badge badge-user">Free</span>`;
  return `<span class="badge badge-user">Free</span>`;
}

function renderAccountRole(account) {
  const role = normalizeAccountRole(account?.role || "Free");
  if (role === "Sales") return `${roleBadge(role)}<br><small>Cấp hoa hồng ${getAccountCommissionLevel(account)} - Hoa hồng ${getAccountCommissionPercent(account)}%</small>`;
  if (role === "Company") return `${roleBadge(role)}<br><small>Cấp dịch vụ ${getAccountServiceLevel(account)} - Dịch vụ ${getAccountServicePercent(account)}%</small>`;
  return roleBadge(role);
}


function toAdminDateTimeLocal(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function adminDateTimeLocalToIso(value) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toISOString();
}

function roleRequiresPlanExpiry(role) {
  const normalized = normalizeAccountRole(role);
  return normalized === "VIP" || normalized === "Premium" || normalized === "Sales" || normalized === "Company";
}

function syncAccountPlanExpiryField() {
  const roleInput = document.getElementById("accountRole");
  const expiryInput = document.getElementById("accountPlanExpiresAt");
  if (!roleInput || !expiryInput) return;
  const requiresExpiry = roleRequiresPlanExpiry(roleInput.value);
  const protectedAdmin = !!roleInput.disabled;
  expiryInput.disabled = protectedAdmin || !requiresExpiry;
  expiryInput.required = requiresExpiry && !protectedAdmin;
  expiryInput.min = toAdminDateTimeLocal(new Date(Date.now() + 60000).toISOString());
  if (!requiresExpiry) expiryInput.value = "";
}

function openAccountModal(id) {
  const account = travelwaiAccounts.find(a => String(a.id) === String(id));
  if (!account) return showToast("Không tìm thấy tài khoản");
  document.getElementById("accountId").value = account.id || "";
  document.getElementById("accountEmail").value = account.email || "";
  document.getElementById("accountUsername").value = stripAccountRolePrefix(account.username || "");
  const commissionInput = document.getElementById("accountCommissionPercent");
  const commissionLevel = document.getElementById("accountSalesLevel");
  const offerInput = document.getElementById("accountOfferDiscount");
  const offerLevel = document.getElementById("accountOfferLevel");
  const serviceInput = document.getElementById("accountServicePercent");
  const serviceLevel = document.getElementById("accountServiceLevel");
  if (commissionInput) commissionInput.value = getAccountCommissionPercent(account);
  if (commissionLevel) {
    commissionLevel.value = String(getAccountCommissionLevel(account));
    commissionLevel.dataset.originalValue = String(getAccountCommissionLevel(account));
  }
  if (offerInput) offerInput.value = getAccountOfferPercent(account);
  if (offerLevel) {
    offerLevel.value = String(getAccountOfferLevel(account));
    offerLevel.dataset.originalValue = String(getAccountOfferLevel(account));
  }
  if (serviceInput) serviceInput.value = getAccountServicePercent(account);
  if (serviceLevel) {
    serviceLevel.value = String(getAccountServiceLevel(account));
    serviceLevel.dataset.originalValue = String(getAccountServiceLevel(account));
  }
  const accountRoleInput = document.getElementById("accountRole");
  const accountExpiryInput = document.getElementById("accountPlanExpiresAt");
  const normalizedRole = normalizeAccountRole(account.role || "Free");
  accountRoleInput.value = normalizedRole;
  accountRoleInput.dataset.originalValue = normalizedRole;
  if (accountExpiryInput) {
    const expiresAt = account.plan_expires_at || account.planExpiresAt || "";
    accountExpiryInput.value = toAdminDateTimeLocal(expiresAt);
    accountExpiryInput.dataset.originalValue = accountExpiryInput.value;
  }
  document.getElementById("accountLocked").checked = !!(account.is_locked || account.isLocked);

  const protectedAdmin = account.is_protected || account.isProtected;
  accountRoleInput.disabled = !!protectedAdmin;
  [commissionInput, commissionLevel, offerInput, offerLevel, serviceInput, serviceLevel].forEach(field => {
    if (field) field.disabled = !!protectedAdmin;
  });
  document.getElementById("accountLocked").disabled = !!protectedAdmin;
  syncAccountLevelFields(false);
  syncAccountPlanExpiryField();
  document.getElementById("accountModal")?.classList.add("open");
}

function closeAccountModal() {
  document.getElementById("accountModal")?.classList.remove("open");
}

function syncAccountLevelFields(applySelected = false) {
  const role = normalizeAccountRole(document.getElementById("accountRole")?.value || "Free");
  const roleDisabled = !!document.getElementById("accountRole")?.disabled;
  const commissionInput = document.getElementById("accountCommissionPercent");
  const commissionLevel = document.getElementById("accountSalesLevel");
  const offerInput = document.getElementById("accountOfferDiscount");
  const offerLevel = document.getElementById("accountOfferLevel");
  const serviceInput = document.getElementById("accountServicePercent");
  const serviceLevel = document.getElementById("accountServiceLevel");
  const disableSalesFields = role !== "Sales" || roleDisabled;
  const disableCompanyFields = role !== "Company" || roleDisabled;
  if (commissionInput) commissionInput.disabled = disableSalesFields;
  if (commissionLevel) commissionLevel.disabled = disableSalesFields;
  if (offerInput) offerInput.disabled = roleDisabled;
  if (offerLevel) offerLevel.disabled = roleDisabled;
  if (serviceInput) serviceInput.disabled = disableCompanyFields;
  if (serviceLevel) serviceLevel.disabled = disableCompanyFields;
  if (applySelected && role === "Sales") {
    const commissionSetting = getSalesLevelSetting(commissionLevel?.value || 1);
    if (commissionInput && commissionSetting) commissionInput.value = commissionSetting.commissionPercent;
  }
  if (applySelected) {
    const offerSetting = getSalesLevelSetting(offerLevel?.value || 1);
    if (offerInput && offerSetting) offerInput.value = offerSetting.offerDiscountPercent;
  }
  if (applySelected && role === "Company") {
    const serviceSetting = getSalesLevelSetting(serviceLevel?.value || 1);
    if (serviceInput && serviceSetting) serviceInput.value = serviceSetting.servicePercent;
  }
}

async function submitAccountForm(event) {
  event.preventDefault();
  const id = document.getElementById("accountId").value;
  const commissionLevel = normalizeSalesLevel(document.getElementById("accountSalesLevel")?.value || 1);
  const offerLevel = normalizeSalesLevel(document.getElementById("accountOfferLevel")?.value || 1);
  const serviceLevel = normalizeSalesLevel(document.getElementById("accountServiceLevel")?.value || 1);
  const roleInput = document.getElementById("accountRole");
  const expiryInput = document.getElementById("accountPlanExpiresAt");
  const selectedRole = normalizeAccountRole(roleInput?.value || "Free");
  const originalRole = normalizeAccountRole(roleInput?.dataset?.originalValue || "Free");
  const roleChanged = selectedRole !== originalRole;
  const expiryValue = String(expiryInput?.value || "").trim();
  if (roleChanged && roleRequiresPlanExpiry(selectedRole) && !expiryValue) {
    showToast("Vui lòng chọn hạn gói khi đổi vai trò.");
    expiryInput?.focus();
    return;
  }
  if (roleRequiresPlanExpiry(selectedRole) && expiryValue) {
    const expiryDate = new Date(expiryValue);
    if (Number.isNaN(expiryDate.getTime()) || expiryDate.getTime() <= Date.now()) {
      showToast("Hạn gói phải lớn hơn thời điểm hiện tại.");
      expiryInput?.focus();
      return;
    }
  }
  const payload = {
    username: document.getElementById("accountUsername").value.trim(),
    role: selectedRole,
    planExpiresAt: roleRequiresPlanExpiry(selectedRole) ? adminDateTimeLocalToIso(expiryValue) : null,
    offerDiscountPercent: clampPercent(document.getElementById("accountOfferDiscount")?.value || 0),
    offerLevel,
    salesLevel: commissionLevel,
    commissionLevel,
    commissionPercent: clampPercent(document.getElementById("accountCommissionPercent")?.value || 0),
    commissionManualOverride: true,
    servicePercent: clampPercent(document.getElementById("accountServicePercent")?.value || 0),
    serviceLevel,
    isLocked: document.getElementById("accountLocked").checked
  };
  try {
    const response = await authenticatedFetch(`/api/admin/accounts/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã cập nhật tài khoản");
    closeAccountModal();
    await Promise.all([loadAdminDashboard(), loadAccounts(), loadPosts()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteAccount(id) {
  if (!await window.TravelwAIConfirm("Xóa tài khoản này? Tour và bài viết của tài khoản sẽ tự động chuyển sang Admin.")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/accounts/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã xóa tài khoản");
    await Promise.all([loadAdminDashboard(), loadAccounts(), loadPosts()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteSchedule(id) {
  if (!await window.TravelwAIConfirm("Xóa lịch trình này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/schedules/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã xóa lịch trình");
    await Promise.all([loadAdminDashboard(), loadSchedules()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function loadPlanStatusOptions() {
  const body = document.getElementById("planStatusTableBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="5" class="empty-line">Đang tải trạng thái...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/plan-status-options");
    const result = await readAdminJson(response);
    travelwaiPlanStatuses = Array.isArray(result.data) ? result.data : [];
    applyAdminTravelTags(result);
    renderPlanStatusOptions();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function getPlanStatusSearchText(status) {
  return getAdminSearchValue([
    status?.label,
    status?.description,
    status?.key,
    status?.id,
    Array.isArray(status?.tags) ? status.tags.join(" ") : "",
    status?.match_all ? "Khớp tất cả" : "Khớp một trong các tag",
    status?.enabled === false ? "Ẩn" : "Hiện"
  ].join(" "));
}

function renderPlanStatusOptions() {
  const body = document.getElementById("planStatusTableBody");
  if (!body) return;

  const query = getAdminSearchValue(planStatusSearchQuery);
  const visibleStatuses = query
    ? travelwaiPlanStatuses.filter(status => getPlanStatusSearchText(status).includes(query))
    : travelwaiPlanStatuses;

  if (!visibleStatuses.length) {
    body.innerHTML = `<tr><td colspan="5" class="empty-line">${query ? "Không tìm thấy trạng thái." : "Chưa có trạng thái."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleStatuses.map(status => {
    const key = status.key || status.id || "";
    const enabled = status.enabled !== false;
    const tags = Array.isArray(status.tags) ? status.tags : [];
    return `
      <tr>
        <td><strong class="admin-status-name" style="${adminAccentStyle(getAdminStatusColor(status))}"><span></span>${escapeHtml(status.label || key)}</strong></td>
        <td>${renderTagChips(tags)}</td>
        <td>${status.match_all ? "Khớp tất cả" : "Khớp một trong các tag"}</td>
        <td>${enabled ? `<span class="badge badge-open">Hiện</span>` : `<span class="badge badge-lock">Ẩn</span>`}</td>
        <td><div class="inline-actions">
          ${adminIconButton("btn-primary", "edit", "Sửa trạng thái", `openPlanStatusOptionModal('${escapeHtml(key)}')`)}
          ${adminIconButton("btn-danger", "hide", "Ẩn trạng thái", `disablePlanStatusOption('${escapeHtml(key)}')`)}
        </div></td>
      </tr>`;
  }).join("");
}

function formatAdminRevenueMoney(value) {
  const amount = Math.max(0, Number(value) || 0);
  return `${new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 0 }).format(amount)}đ`;
}

function getAdminRevenueSearchText(item) {
  return getAdminSearchValue([
    item?.username,
    item?.displayName,
    item?.email,
    item?.role,
    item?.accountId
  ].join(" "));
}

function formatAdminRevenueRelevantMoney(value, applicable) {
  if (!applicable) return '<span class="admin-revenue-not-applicable" title="Tài khoản này không nhận khoản tiền này">—</span>';
  return formatAdminRevenueMoney(value);
}

function renderAdminRevenue() {
  const body = document.getElementById("adminRevenueTableBody");
  if (!body) return;

  const query = getAdminSearchValue(revenueSearchQuery);
  const rows = query
    ? travelwaiAccountRevenue.filter(item => getAdminRevenueSearchText(item).includes(query))
    : travelwaiAccountRevenue;

  if (!rows.length) {
    body.innerHTML = `<tr><td colspan="8" class="empty-line">${query ? "Không tìm thấy tài khoản." : "Chưa có dữ liệu doanh thu."}</td></tr>`;
    return;
  }

  body.innerHTML = rows.map(item => {
    const name = cleanAccountDisplayName(item.username || item.displayName || item.email || "Tài khoản");
    const email = item.email || "";
    const discountRelevant = item.discountRelevant === true;
    const commissionRelevant = item.commissionRelevant === true;
    const serviceFeeRelevant = item.serviceFeeRelevant === true;
    return `
      <tr>
        <td><strong data-no-translate>${escapeHtml(name)}</strong><br><small data-no-translate>${escapeHtml(email)}</small></td>
        <td><span class="badge badge-user">${escapeHtml(item.role || "Free")}</span></td>
        <td>${Math.max(0, Number(item.orderCount) || 0)}</td>
        <td><strong>${formatAdminRevenueMoney(item.grossRevenue)}</strong></td>
        <td>${formatAdminRevenueRelevantMoney(item.discountDeducted, discountRelevant)}</td>
        <td>${formatAdminRevenueRelevantMoney(item.commission, commissionRelevant)}</td>
        <td>${formatAdminRevenueRelevantMoney(item.serviceFee, serviceFeeRelevant)}</td>
        <td><strong class="admin-revenue-total">${formatAdminRevenueMoney(item.revenue)}</strong></td>
      </tr>`;
  }).join("");
}

async function loadAdminRevenue(force = false) {
  const body = document.getElementById("adminRevenueTableBody");
  if (!body || adminRevenueLoading || (adminRevenueLoaded && !force)) return;
  adminRevenueLoading = true;
  body.innerHTML = `<tr><td colspan="8" class="empty-line">Đang tải doanh thu...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/revenue-by-account");
    const result = await readAdminJson(response);
    travelwaiAccountRevenue = Array.isArray(result.data) ? result.data : [];
    adminRevenueLoaded = true;
    renderAdminRevenue();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="8" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  } finally {
    adminRevenueLoading = false;
  }
}

async function loadProvinceTags() {
  const body = document.getElementById("provinceTagTableBody");
  if (body) body.innerHTML = `<tr><td colspan="4" class="empty-line">Đang tải tỉnh thành...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/province-tags");
    const result = await readAdminJson(response);
    travelwaiProvinceTags = Array.isArray(result.data) ? result.data : [];
    applyAdminTravelTags(result);
    renderProvinceTags();
  } catch (error) {
    if (body) body.innerHTML = `<tr><td colspan="4" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function getProvinceTagSearchText(province) {
  return getAdminSearchValue([
    province?.name,
    province?.province_name,
    province?.province_id,
    province?.id,
    province?.area,
    province?.region,
    province?.description,
    Array.isArray(province?.tags) ? province.tags.join(" ") : ""
  ].join(" "));
}

function renderProvinceTags() {
  const body = document.getElementById("provinceTagTableBody");
  if (!body) return;

  const query = getAdminSearchValue(provinceTagSearchQuery);
  const visibleProvinces = query
    ? travelwaiProvinceTags.filter(province => getProvinceTagSearchText(province).includes(query))
    : travelwaiProvinceTags;

  if (!visibleProvinces.length) {
    body.innerHTML = `<tr><td colspan="4" class="empty-line">${query ? "Không tìm thấy tỉnh thành." : "Chưa có tỉnh thành."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleProvinces.map(province => {
    const id = province.id || province.province_id || province.name || "";
    const tags = Array.isArray(province.tags) ? province.tags : [];
    return `
      <tr>
        <td><strong>${escapeHtml(province.name || province.province_name || "Tỉnh thành")}</strong><br><small>#${escapeHtml(province.province_id || id)}</small></td>
        <td>${escapeHtml(province.area || "")}<br><small>${escapeHtml(province.region || "")}</small></td>
        <td>${renderTagChips(tags)}</td>
        <td>${adminIconButton("btn-primary", "edit", "Sửa tỉnh thành", `openProvinceTagModal('${escapeHtml(id)}')`)}</td>
      </tr>`;
  }).join("");
}

function renderTagChips(tags) {
  if (!tags || !tags.length) return `<span class="badge badge-user">Chưa gắn tag</span>`;
  return `<div class="admin-tag-chips">${tags.map(tag => `<span style="${adminAccentStyle(getAdminTagColor(tag))}">${escapeHtml(tag)}</span>`).join("")}</div>`;
}

function renderTravelTagExistingList() {
  const container = document.getElementById("travelTagExistingList");
  if (!container) return;
  const tagNames = travelwaiTravelTags.length
    ? travelwaiTravelTags.map(tag => tag?.name || tag?.label || "").filter(Boolean)
    : travelwaiAllowedTags;
  const uniqueTags = Array.from(new Set(tagNames.map(tag => String(tag).trim()).filter(Boolean)));
  container.innerHTML = uniqueTags.length
    ? `<div class="admin-tag-chips admin-tag-delete-list">${uniqueTags.map(tag => `<span class="admin-deletable-tag" style="${adminAccentStyle(getAdminTagColor(tag))}">${escapeHtml(tag)}<button class="admin-delete-tag-btn" type="button" data-tag="${escapeHtml(tag)}" title="Xoá tag" aria-label="Xoá tag ${escapeHtml(tag)}">×</button></span>`).join("")}</div>`
    : `<span class="badge badge-user">Chưa có tag du lịch</span>`;
  container.querySelectorAll(".admin-delete-tag-btn").forEach(button => {
    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      deleteTravelTag(button.dataset.tag || "");
    });
  });
}

function getAdminStatusColor(status) {
  const key = String(status?.key || status?.id || "");
  const color = String(status?.color || "").trim();
  if (/^#[0-9a-f]{6}$/i.test(color)) {
    if (key === "binh_thuong" && color.toLowerCase() === "#ffffff") return adminPlanStatusColors.binh_thuong;
    return color;
  }
  if (adminPlanStatusColors[key]) return adminPlanStatusColors[key];
  const tags = Array.isArray(status?.tags) ? status.tags : [];
  return tags.length ? getAdminTagColor(tags[0]) : "#6366f1";
}

function getAdminTagColor(tag) {
  const key = normalizeAdminColorKey(tag);
  return adminPlanTagColors[key] || "#6366f1";
}

function adminAccentStyle(color) {
  return `--admin-accent:${color}`;
}

function normalizeAdminColorKey(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/_+/g, "_");
}

function renderTagCheckboxes(containerId, selectedTags) {
  const container = document.getElementById(containerId);
  if (!container) return;
  const selected = new Set((selectedTags || []).map(tag => String(tag).toLowerCase()));
  container.innerHTML = travelwaiAllowedTags.map(tag => `
    <label class="tag-checkbox-item">
      <input type="checkbox" value="${escapeHtml(tag)}" ${selected.has(String(tag).toLowerCase()) ? "checked" : ""} />
      <span style="${adminAccentStyle(getAdminTagColor(tag))}">${escapeHtml(tag)}</span>
    </label>`).join("");
}

function getCheckedTags(containerId) {
  return Array.from(document.querySelectorAll(`#${containerId} input[type="checkbox"]:checked`)).map(input => input.value);
}

function openPlanStatusOptionModal(key = "") {
  const status = travelwaiPlanStatuses.find(item => String(item.key || item.id || "") === String(key));
  document.getElementById("planStatusOptionOriginalKey").value = key || "";
  document.getElementById("planStatusOptionKey").value = status?.key || status?.id || "";
  document.getElementById("planStatusOptionLabel").value = status?.label || "";
  document.getElementById("planStatusOptionDescription").value = status?.description || "";
  document.getElementById("planStatusOptionOrder").value = status?.order || 999;
  document.getElementById("planStatusOptionColor").value = getAdminStatusColor(status || { key });
  document.getElementById("planStatusOptionEnabled").value = status?.enabled === false ? "false" : "true";
  document.getElementById("planStatusOptionMatchAll").checked = !!status?.match_all;
  renderTagCheckboxes("planStatusTagCheckboxes", status?.tags || []);
  document.getElementById("planStatusOptionModal")?.classList.add("open");
}

function closePlanStatusOptionModal() {
  document.getElementById("planStatusOptionModal")?.classList.remove("open");
}

async function submitPlanStatusOptionForm(event) {
  event.preventDefault();
  const originalKey = document.getElementById("planStatusOptionOriginalKey").value.trim();
  const label = document.getElementById("planStatusOptionLabel").value.trim();
  const key = document.getElementById("planStatusOptionKey").value.trim() || originalKey || normalizeAdminColorKey(label);
  if (!label) return showToast("Bạn chưa nhập tên trạng thái");
  if (!key) return showToast("Tên trạng thái không hợp lệ");

  const payload = {
    key,
    label,
    description: document.getElementById("planStatusOptionDescription").value.trim(),
    tags: getCheckedTags("planStatusTagCheckboxes"),
    matchAll: document.getElementById("planStatusOptionMatchAll").checked,
    enabled: document.getElementById("planStatusOptionEnabled").value === "true",
    order: parseInt(document.getElementById("planStatusOptionOrder").value, 10) || 999,
    color: document.getElementById("planStatusOptionColor").value || "#6366f1"
  };

  try {
    const response = await authenticatedFetch(`/api/admin/plan-status-options/${encodeURIComponent(originalKey || key)}`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã lưu trạng thái");
    closePlanStatusOptionModal();
    await Promise.all([loadAdminDashboard(), loadPlanStatusOptions()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function disablePlanStatusOption(key) {
  if (!await window.TravelwAIConfirm("Ẩn trạng thái này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/plan-status-options/${encodeURIComponent(key)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã ẩn trạng thái");
    await Promise.all([loadAdminDashboard(), loadPlanStatusOptions()]);
  } catch (error) {
    showToast(error.message);
  }
}

function openTravelTagModal() {
  const nameInput = document.getElementById("travelTagName");
  const colorInput = document.getElementById("travelTagColor");
  if (nameInput) nameInput.value = "";
  if (colorInput) colorInput.value = "#6366f1";
  renderTravelTagExistingList();
  document.getElementById("travelTagModal")?.classList.add("open");
  setTimeout(() => nameInput?.focus(), 50);
}

function closeTravelTagModal() {
  document.getElementById("travelTagModal")?.classList.remove("open");
}

async function submitTravelTagForm(event) {
  event.preventDefault();
  const name = document.getElementById("travelTagName")?.value.trim() || "";
  const color = document.getElementById("travelTagColor")?.value || "#6366f1";
  if (!name) return showToast("Bạn chưa nhập tên tag");

  try {
    const response = await authenticatedFetch("/api/admin/travel-tags", {
      method: "POST",
      body: JSON.stringify({ name, color })
    });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã thêm tag");
    await Promise.all([loadPlanStatusOptions(), loadProvinceTags()]);
    renderTravelTagExistingList();
    closeTravelTagModal();
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteTravelTag(name) {
  const tagName = String(name || "").trim();
  if (!tagName) return;
  if (!await window.TravelwAIConfirm(`Xoá tag "${tagName}"?`)) return;

  try {
    const response = await authenticatedFetch(`/api/admin/travel-tags/${encodeURIComponent(tagName)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã xoá tag");
    await Promise.all([loadPlanStatusOptions(), loadProvinceTags()]);
    renderTravelTagExistingList();
  } catch (error) {
    showToast(error.message);
  }
}

function openProvinceTagModal(id) {
  const province = travelwaiProvinceTags.find(item => String(item.id || item.province_id || item.name || "") === String(id));
  if (!province) return showToast("Không tìm thấy tỉnh thành");
  document.getElementById("provinceTagId").value = province.id || province.province_id || id;
  document.getElementById("provinceTagProvinceId").value = province.province_id || province.id || "";
  document.getElementById("provinceTagName").value = province.name || province.province_name || "";
  document.getElementById("provinceTagArea").value = province.area || "";
  document.getElementById("provinceTagRegion").value = province.region || "";
  document.getElementById("provinceTagDescription").value = province.description || "";
  renderTagCheckboxes("provinceTagCheckboxes", province.tags || []);
  document.getElementById("provinceTagModal")?.classList.add("open");
}

function closeProvinceTagModal() {
  document.getElementById("provinceTagModal")?.classList.remove("open");
}

async function submitProvinceTagForm(event) {
  event.preventDefault();
  const id = document.getElementById("provinceTagId").value.trim();
  const payload = {
    id,
    provinceId: parseInt(document.getElementById("provinceTagProvinceId").value, 10) || null,
    name: document.getElementById("provinceTagName").value.trim(),
    area: document.getElementById("provinceTagArea").value.trim(),
    region: document.getElementById("provinceTagRegion").value.trim(),
    description: document.getElementById("provinceTagDescription").value.trim(),
    tags: getCheckedTags("provinceTagCheckboxes")
  };

  try {
    const response = await authenticatedFetch(`/api/admin/province-tags/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã lưu tỉnh thành");
    closeProvinceTagModal();
    await Promise.all([loadAdminDashboard(), loadProvinceTags()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function loadPosts() {
  const body = document.getElementById("postTableBody");
  if (body) body.innerHTML = `<tr><td colspan="6" class="empty-line">Đang tải bài viết...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/posts");
    const result = await readAdminJson(response);
    travelwaiPosts = Array.isArray(result.data) ? result.data : [];
    renderPosts();
    setText("adminStatPosts", travelwaiPosts.length);
  } catch (error) {
    if (body) body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function normalizeWikiLine(value) {
  return String(value ?? "")
    .trim()
    .replace(/^=+|=+$/g, "")
    .trim()
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9 ]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function stripPostSourceLines(value) {
  const blockedHeadings = new Set([
    "xem them",
    "tham khao",
    "lien ket ngoai",
    "chu thich",
    "ghi chu",
    "nguon tham khao",
    "thu muc"
  ]);
  const kept = [];
  for (const line of String(value ?? "").split(/\r?\n/)) {
    const normalized = normalizeWikiLine(line);
    if (blockedHeadings.has(normalized)) break;
    if (/^\s*Nguồn\s+dữ\s+liệu/i.test(line) || /Wikipedia tiếng Việt|vi\.wikipedia\.org/i.test(line)) continue;
    kept.push(line);
  }
  return kept.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

function adminIsVideoMediaUrl(url) {
  return /\.(mp4|webm|mov)(?:$|[?#])/i.test(String(url || ""));
}

function normalizeAdminPostMediaItem(item) {
  if (!item) return null;
  if (typeof item === "string") {
    const url = item.trim();
    if (!url) return null;
    const video = adminIsVideoMediaUrl(url);
    return { url, name: url.split(/[/?#]/).pop() || "Tệp", contentType: video ? "video/mp4" : "image/jpeg", size: 0, type: video ? "video" : "image" };
  }
  const url = String(item.url || item.src || item.path || "").trim();
  if (!url) return null;
  const contentType = String(item.contentType || item.content_type || item.mimeType || "").trim().toLowerCase();
  const video = String(item.type || "").toLowerCase() === "video" || contentType.startsWith("video/") || adminIsVideoMediaUrl(url);
  return { url, name: String(item.name || item.fileName || url.split(/[/?#]/).pop() || "Tệp"), contentType: contentType || (video ? "video/mp4" : "image/jpeg"), size: Number(item.size || 0), type: video ? "video" : "image" };
}

function parseAdminPostMedia(post) {
  const rawMedia = getValue(post, "media", "media_items", "mediaItems");
  let media = [];
  if (Array.isArray(rawMedia)) media = rawMedia.map(normalizeAdminPostMediaItem).filter(Boolean);
  else if (typeof rawMedia === "string" && rawMedia.trim()) {
    try { const parsed = JSON.parse(rawMedia); if (Array.isArray(parsed)) media = parsed.map(normalizeAdminPostMediaItem).filter(Boolean); } catch (_) {}
  }
  const raw = getValue(post, "media_urls", "mediaUrls", "image_urls", "imageUrls", "images", "video_urls", "videoUrls", "image", "thumbnail");
  let legacy = [];
  if (Array.isArray(raw)) legacy = raw;
  else if (typeof raw === "string") {
    try { const parsed = JSON.parse(raw); legacy = Array.isArray(parsed) ? parsed : raw.split(/\n|,|\|/); } catch (_) { legacy = raw.split(/\n|,|\|/); }
  }
  legacy.map(normalizeAdminPostMediaItem).filter(Boolean).forEach(item => { if (!media.some(current => current.url === item.url)) media.push(item); });
  return media.slice(0, 12);
}

function adminPostMediaFromInput() {
  const value = String(document.getElementById("postImageUrls")?.value || "").trim();
  if (!value) return [];
  try { const parsed = JSON.parse(value); if (Array.isArray(parsed)) return parsed.map(normalizeAdminPostMediaItem).filter(Boolean); } catch (_) {}
  return value.split(/\n|,|\|/).map(normalizeAdminPostMediaItem).filter(Boolean);
}

function revokeAdminPostPreviewObjectUrls() {
  adminPostPreviewObjectUrls.forEach(url => URL.revokeObjectURL(url));
  adminPostPreviewObjectUrls = [];
}

function setAdminPostExistingMedia(items) {
  const input = document.getElementById("postImageUrls");
  if (input) input.value = JSON.stringify((items || []).map(normalizeAdminPostMediaItem).filter(Boolean));
}

function adminPostPreviewMarkup(item, index, source) {
  const preview = item.type === "video"
    ? `<video preload="metadata" muted playsinline src="${escapeHtml(item.url)}"></video>`
    : `<img src="${escapeHtml(item.url)}" alt="${escapeHtml(item.name || `Tệp ${index + 1}`)}" />`;
  return `<div class="image-attachment-preview-item">${preview}<button class="image-attachment-remove" type="button" data-media-source="${source}" data-media-index="${index}" title="Xóa tệp" aria-label="Xóa tệp"><span data-interface-icon="trash-2"></span></button></div>`;
}

function renderAdminPostImagePreview() {
  const box = document.getElementById("postImagePreview");
  if (!box) return;
  revokeAdminPostPreviewObjectUrls();
  const existing = adminPostMediaFromInput();
  const selected = selectedAdminPostMediaFiles.map(file => {
    const url = URL.createObjectURL(file);
    adminPostPreviewObjectUrls.push(url);
    return normalizeAdminPostMediaItem({ url, name: file.name, contentType: file.type, size: file.size });
  });
  box.innerHTML = existing.map((item, index) => adminPostPreviewMarkup(item, index, "existing"))
    .concat(selected.map((item, index) => adminPostPreviewMarkup(item, index, "selected"))).join("");
  box.querySelectorAll(".image-attachment-remove").forEach(button => {
    button.addEventListener("click", () => {
      const index = Number(button.dataset.mediaIndex);
      if (button.dataset.mediaSource === "existing") {
        const next = adminPostMediaFromInput();
        next.splice(index, 1);
        setAdminPostExistingMedia(next);
      } else selectedAdminPostMediaFiles.splice(index, 1);
      renderAdminPostImagePreview();
    });
  });
}

function validateAdminPostMediaFile(file) {
  if (!file) return;
  const type = String(file.type || "");
  if (!type.startsWith("image/") && !type.startsWith("video/")) throw new Error("Chỉ hỗ trợ ảnh hoặc video.");
  if (file.size > 10 * 1024 * 1024) throw new Error("Mỗi tệp phải nhỏ hơn 10MB.");
}

async function uploadAdminPostMedia(files) {
  const list = Array.from(files || []);
  if (!list.length) return [];
  list.forEach(validateAdminPostMediaFile);
  const prepared = [];
  for (const file of list) prepared.push(file.type?.startsWith("image/") && window.TravelwAIImageOptimizer ? await window.TravelwAIImageOptimizer.optimizeImageFile(file) : file);
  const formData = new FormData();
  prepared.forEach(file => formData.append("files", file, file.name));
  const response = await authenticatedFetch("/api/posts/images", { method: "POST", body: formData });
  const result = await readAdminJson(response);
  if (Array.isArray(result.media)) return result.media.map(normalizeAdminPostMediaItem).filter(Boolean);
  return (Array.isArray(result.urls) ? result.urls : []).map(normalizeAdminPostMediaItem).filter(Boolean);
}

function getAdminAccountId(account) {
  return String(getValue(account, "id", "uid", "user_id", "userId") || "");
}

function getAccountDisplayName(account) {
  return cleanAccountDisplayName(getValue(account, "username", "displayName", "display_name", "name", "email")) || "Tài khoản";
}

function getManagedAccountDisplayNameById(id) {
  if (!id) return "";
  const account = (Array.isArray(travelwaiAccounts) ? travelwaiAccounts : [])
    .find(item => getAdminAccountId(item) === String(id));
  return account ? getAccountDisplayName(account) : "";
}

function getPostAuthorName(post) {
  const authorId = getValue(post, "author_id", "authorId");
  const managedName = getManagedAccountDisplayNameById(authorId);
  if (managedName) return managedName;
  const name = cleanAccountDisplayName(getValue(post, "author_name", "authorName"));
  return name || "TravelwAI";
}

function getPostSearchText(post) {
  return getAdminSearchValue([
    post?.title, post?.summary, post?.content, post?.festival, post?.province,
    post?.holiday_type, post?.holidayType, post?.tour_keywords, post?.tourKeywords,
    getPostAuthorName(post), post?.status, `tháng ${post?.month || ""}`
  ].join(" "));
}

function renderPosts() {
  const body = document.getElementById("postTableBody");
  if (!body) return;
  const query = getAdminSearchValue(postSearchQuery);
  const visible = query ? travelwaiPosts.filter(post => getPostSearchText(post).includes(query)) : travelwaiPosts;
  if (!visible.length) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${query ? "Không tìm thấy bài viết." : "Chưa có bài viết."}</td></tr>`;
    return;
  }
  body.innerHTML = visible.map(post => {
    const id = getValue(post, "id") || "";
    return `
      <tr>
        <td><strong>${escapeHtml(post.title || "Bài viết")}</strong><br><small>${escapeHtml(stripPostSourceLines(post.summary || ""))}</small></td>
        <td class="nowrap-cell">Tháng ${escapeHtml(post.month || "")}</td>
        <td>${escapeHtml(post.festival || post.holiday_type || "")}</td>
        <td><strong>${escapeHtml(getPostAuthorName(post))}</strong></td>
        <td class="nowrap-cell"><span class="badge ${String(post.status || "Hiển thị") === "Ẩn" ? "status-paused" : "status-selling"}">${escapeHtml(post.status || "Hiển thị")}</span></td>
        <td class="nowrap-cell"><div class="inline-actions">${adminIconButton("btn-primary", "edit", "Sửa bài viết", `openPostModal('${escapeHtml(id)}')`)}${adminIconButton("btn-danger", "delete", "Xóa bài viết", `deletePost('${escapeHtml(id)}')`)}</div></td>
      </tr>`;
  }).join("");
}

function getPostAuthorAccounts() {
  const roleOrder = { admin: 0, sales: 1, "tour sales": 1, business: 2, company: 2, premium: 3, vip: 4, free: 5, user: 5 };
  const source = Array.isArray(travelwaiAccounts) ? travelwaiAccounts : [];
  return source
    .filter(account => getAdminAccountId(account))
    .sort((a, b) => {
      const ar = roleOrder[String(a?.role || "").trim().toLowerCase()] ?? 9;
      const br = roleOrder[String(b?.role || "").trim().toLowerCase()] ?? 9;
      if (ar !== br) return ar - br;
      return getAccountDisplayName(a).localeCompare(getAccountDisplayName(b), "vi");
    });
}

function fillPostAuthorSelect(selectedId = "", selectedName = "") {
  const select = document.getElementById("postAuthor");
  if (!select) return;
  const accounts = getPostAuthorAccounts();
  select.innerHTML = `<option value="">Chọn tài khoản</option>`;
  accounts.forEach(account => {
    const id = getAdminAccountId(account);
    const name = getAccountDisplayName(account);
    const option = document.createElement("option");
    option.value = id;
    option.textContent = name;
    option.dataset.name = name;
    if (selectedId && id === String(selectedId)) option.selected = true;
    select.appendChild(option);
  });

  if (selectedId && !select.value && selectedName) {
    const option = document.createElement("option");
    option.value = selectedId;
    option.textContent = cleanAccountDisplayName(selectedName) || selectedName;
    option.dataset.name = option.textContent;
    option.selected = true;
    select.appendChild(option);
  }
}

async function fetchFullAdminPost(id) {
  const response = await authenticatedFetch(`/api/admin/posts/${encodeURIComponent(id)}`);
  const result = await readAdminJson(response);
  const post = result.data || result.post || result;
  const index = travelwaiPosts.findIndex(item => String(getValue(item, "id")) === String(id));
  if (index >= 0 && post) travelwaiPosts[index] = { ...travelwaiPosts[index], ...post };
  return post;
}


function setPostFormValue(id, value) {
  const input = document.getElementById(id);
  if (input) input.value = value ?? "";
}

function resetPostImageFileInput() {
  selectedAdminPostMediaFiles = [];
  revokeAdminPostPreviewObjectUrls();
  const fileInput = document.getElementById("postImageFiles");
  if (fileInput) fileInput.value = "";
}

function fillPostForm(post = {}) {
  const authorId = String(getValue(post, "author_id", "authorId") || "");
  const authorName = cleanAccountDisplayName(getValue(post, "author_name", "authorName")) || getManagedAccountDisplayNameById(authorId);
  fillPostAuthorSelect(authorId, authorName);

  setPostFormValue("postId", getValue(post, "id") || "");
  setPostFormValue("postMonth", Math.min(12, Math.max(1, Number(getValue(post, "month") || new Date().getMonth() + 1))) || new Date().getMonth() + 1);
  setPostFormValue("postFestival", getValue(post, "festival", "holiday_type", "holidayType") || "");
  setPostFormValue("postTitle", post.title || "");
  setPostFormValue("postStatus", post.status || "Hiển thị");
  setPostFormValue("postProvince", post.province || "");
  setPostFormValue("postTourKeywords", getValue(post, "tour_keywords", "tourKeywords") || "");
  setPostFormValue("postSummary", stripPostSourceLines(post.summary || ""));
  setPostFormValue("postContent", stripPostSourceLines(post.content || ""));
  setPostFormValue("postImageUrls", JSON.stringify(parseAdminPostMedia(post)));
  renderAdminPostImagePreview();
}

function createAdminPostAiSessionId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`;
}

async function openPostModal(id = "") {
  const modal = document.getElementById("postModal");
  if (!modal) return showToast("Không tìm thấy form bài viết");

  const postId = String(id || "").trim();
  const title = document.getElementById("postModalTitle");
  const form = document.getElementById("postForm");
  form?.reset();
  resetPostImageFileInput();
  setAdminPostAiLoading(false, "");
  adminPostAiGenerationSessionId = createAdminPostAiSessionId();
  adminPostAiGenerationId = "";

  if (title) title.textContent = postId ? "Sửa bài viết" : "Thêm bài viết";

  const localPost = postId
    ? travelwaiPosts.find(item => String(getValue(item, "id")) === postId)
    : null;

  fillPostForm(localPost || { month: new Date().getMonth() + 1, status: "Hiển thị" });
  setPostFormValue("postId", postId);
  modal.classList.add("open");

  if (postId) {
    try {
      const fullPost = await fetchFullAdminPost(postId);
      if (fullPost) {
        fillPostForm({ ...(localPost || {}), ...fullPost, id: getValue(fullPost, "id") || postId });
      }
    } catch (error) {
      console.error(error);
      showToast(error.message || "Không tải được chi tiết bài viết");
    }
  }

  setTimeout(() => document.getElementById("postTitle")?.focus(), 50);
}

function setAdminPostAiLoading(isLoading, message = "") {
  const button = document.getElementById("postAiGenerateButton");
  const status = document.getElementById("postAiGenerateStatus");
  if (button) {
    button.disabled = false;
    button.classList.toggle("is-loading", Boolean(isLoading));
    button.classList.toggle("is-cancellable", Boolean(isLoading));
    button.setAttribute("aria-busy", isLoading ? "true" : "false");
    button.setAttribute("aria-label", isLoading ? "Dừng AI tạo bài viết" : "Tự tạo nội dung bài viết bằng AI");
    button.title = isLoading ? "Bấm để dừng AI" : "Tự tạo nội dung bằng AI";
  }
  if (status) status.textContent = message;
}

function decodePartialJsonString(rawValue) {
  let value = String(rawValue || "");
  if (!value) return "";
  if ((value.match(/\\$/) || []).length) value = value.slice(0, -1);
  try {
    return JSON.parse(`"${value}"`);
  } catch (_) {
    return value
      .replace(/\\n/g, "\n")
      .replace(/\\r/g, "")
      .replace(/\\t/g, "\t")
      .replace(/\\"/g, '"')
      .replace(/\\\\/g, "\\");
  }
}

function extractPartialPostAiField(rawJson, fieldName) {
  const source = String(rawJson || "");
  const marker = `"${fieldName}"`;
  const markerIndex = source.indexOf(marker);
  if (markerIndex < 0) return "";
  const colonIndex = source.indexOf(":", markerIndex + marker.length);
  if (colonIndex < 0) return "";
  const quoteIndex = source.indexOf('"', colonIndex + 1);
  if (quoteIndex < 0) return "";

  let escaped = false;
  let encoded = "";
  for (let index = quoteIndex + 1; index < source.length; index += 1) {
    const character = source[index];
    if (escaped) {
      encoded += `\\${character}`;
      escaped = false;
      continue;
    }
    if (character === "\\") {
      escaped = true;
      continue;
    }
    if (character === '"') return decodePartialJsonString(encoded);
    encoded += character;
  }
  if (escaped) encoded += "\\";
  return decodePartialJsonString(encoded);
}

function updateAdminPostAiStreamingFields(rawJson) {
  const fields = [
    ["title", "postTitle"],
    ["province", "postProvince"],
    ["tourKeywords", "postTourKeywords"],
    ["summary", "postSummary"],
    ["content", "postContent"]
  ];
  fields.forEach(([jsonKey, inputId]) => {
    const value = extractPartialPostAiField(rawJson, jsonKey);
    if (value) setPostFormValue(inputId, value);
  });
}

function clearAdminPostAiGeneratedFields() {
  ["postTitle", "postProvince", "postTourKeywords", "postSummary", "postContent"].forEach(id => setPostFormValue(id, ""));
}

async function generateAdminPostContentFromFestival() {
  const festivalInput = document.getElementById("postFestival");
  const keyword = festivalInput?.value?.trim() || "";
  if (!keyword) {
    showToast("Vui lòng nhập lễ hội hoặc ngày lễ trước khi tạo nội dung.");
    festivalInput?.focus();
    return;
  }

  if (adminPostAiAbortController) {
    setAdminPostAiLoading(true, "Đang dừng AI...");
    adminPostAiAbortController.abort("user-cancelled");
    try { await adminPostAiStreamReader?.cancel?.("user-cancelled"); } catch (_) {}
    return;
  }

  const abortController = new AbortController();
  adminPostAiAbortController = abortController;
  setAdminPostAiLoading(true, "Đang chuẩn bị dữ liệu cho AI...");
  let rawAiOutput = "";
  let completedPayload = null;

  try {
    const response = await authenticatedFetch("/api/admin/post-content-ai/generate-stream", {
      method: "POST",
      headers: { Accept: "application/x-ndjson" },
      body: JSON.stringify({
        keyword,
        language: window.TravelwAILanguage?.get?.() || "vi",
        sessionId: adminPostAiGenerationSessionId || (adminPostAiGenerationSessionId = createAdminPostAiSessionId())
      }),
      timeoutMs: 240000,
      streamResponse: true,
      signal: abortController.signal
    });

    if (!response.ok || !response.body) {
      const result = await readAdminJson(response);
      throw new Error(result.message || "Không thể tạo nội dung bằng AI.");
    }

    const reader = response.body.getReader();
    adminPostAiStreamReader = reader;
    const decoder = new TextDecoder();
    let streamBuffer = "";
    let streamDone = false;

    while (!streamDone) {
      const { value, done } = await reader.read();
      streamBuffer += decoder.decode(value || new Uint8Array(), { stream: !done });
      let newlineIndex;
      while ((newlineIndex = streamBuffer.indexOf("\n")) >= 0) {
        const line = streamBuffer.slice(0, newlineIndex).trim();
        streamBuffer = streamBuffer.slice(newlineIndex + 1);
        if (!line) continue;

        const event = JSON.parse(line);
        const type = String(event.type || "").toLowerCase();
        if (type === "status") {
          setAdminPostAiLoading(true, event.message || "AI đang chuẩn bị nội dung...");
        } else if (type === "delta") {
          rawAiOutput += String(event.delta || "");
          updateAdminPostAiStreamingFields(rawAiOutput);
          const currentContent = document.getElementById("postContent")?.value || "";
          setAdminPostAiLoading(true, currentContent
            ? `AI đang viết nội dung... ${currentContent.length.toLocaleString("vi-VN")} ký tự`
            : "AI đang sinh tiêu đề và nội dung...");
        } else if (type === "reset") {
          rawAiOutput = "";
          clearAdminPostAiGeneratedFields();
          setAdminPostAiLoading(true, event.message || "AI đang tạo lại nội dung...");
        } else if (type === "completed") {
          completedPayload = event;
          streamDone = true;
          break;
        } else if (type === "error") {
          throw new Error(event.message || "Không thể tạo nội dung bằng AI.");
        }
      }
      if (done) break;
    }

    if (!completedPayload) throw new Error("Luồng AI kết thúc nhưng chưa trả về nội dung hoàn chỉnh.");
    const data = completedPayload.data || completedPayload;
    adminPostAiGenerationSessionId = data.aiGenerationSessionId || data.ai_generation_session_id || adminPostAiGenerationSessionId;
    adminPostAiGenerationId = data.aiGenerationId || data.ai_generation_id || "";

    setPostFormValue("postTitle", data.title || "");
    setPostFormValue("postProvince", data.province || "");
    setPostFormValue("postTourKeywords", data.tourKeywords || data.tour_keywords || "");
    setPostFormValue("postSummary", data.summary || "");
    setPostFormValue("postContent", data.content || "");

    setAdminPostAiLoading(false, "Đã tạo nội dung.");
    showToast(completedPayload.message || "Đã tạo nội dung.");
    document.getElementById("postTitle")?.focus();
  } catch (error) {
    if (abortController.signal.aborted) {
      setAdminPostAiLoading(false, "Đã dừng tạo nội dung.");
      showToast("Đã dừng AI tạo bài viết.");
    } else {
      console.error("Không thể tạo nội dung bài viết bằng AI:", error);
      setAdminPostAiLoading(false, "");
      showToast(error?.message || "Không thể tạo nội dung bằng AI.");
    }
  } finally {
    if (adminPostAiStreamReader) {
      try { adminPostAiStreamReader.releaseLock?.(); } catch (_) {}
      adminPostAiStreamReader = null;
    }
    if (adminPostAiAbortController === abortController) adminPostAiAbortController = null;
    const button = document.getElementById("postAiGenerateButton");
    button?.classList.remove("is-cancellable");
    if (button?.classList.contains("is-loading")) {
      setAdminPostAiLoading(false, document.getElementById("postAiGenerateStatus")?.textContent || "");
    }
  }
}

function closePostModal() {
  adminPostAiAbortController?.abort("modal-closed");
  try { void adminPostAiStreamReader?.cancel?.("modal-closed"); } catch (_) {}
  adminPostAiStreamReader = null;
  adminPostAiAbortController = null;
  setAdminPostAiLoading(false, "");
  document.getElementById("postModal")?.classList.remove("open");
  revokeAdminPostPreviewObjectUrls();
  adminPostAiGenerationSessionId = "";
  adminPostAiGenerationId = "";
}

async function submitPostForm(event) {
  event.preventDefault();
  const id = document.getElementById("postId").value.trim();
  const authorSelect = document.getElementById("postAuthor");
  const selectedAuthorOption = authorSelect?.selectedOptions?.[0];
  let authorId = authorSelect?.value || "";
  let authorName = cleanAccountDisplayName(selectedAuthorOption?.dataset?.name || selectedAuthorOption?.textContent || "");
  if (!authorId) {
    showToast("Vui lòng chọn tài khoản trong Quản lý tài khoản trước khi lưu bài viết.");
    authorSelect?.focus();
    return;
  }
  if (!authorName || authorName === "Chọn tài khoản") authorName = getManagedAccountDisplayNameById(authorId) || "TravelwAI";
  try {
    const uploadedMedia = await uploadAdminPostMedia(selectedAdminPostMediaFiles);
    const payload = {
      title: document.getElementById("postTitle").value.trim(),
      month: Number(document.getElementById("postMonth").value || new Date().getMonth() + 1),
      status: document.getElementById("postStatus").value,
      festival: document.getElementById("postFestival").value.trim(),
      province: document.getElementById("postProvince").value.trim(),
      tourKeywords: document.getElementById("postTourKeywords").value.trim(),
      summary: stripPostSourceLines(document.getElementById("postSummary").value).trim(),
      content: stripPostSourceLines(document.getElementById("postContent").value).trim(),
      media: [...adminPostMediaFromInput(), ...uploadedMedia],
      imageUrls: [...adminPostMediaFromInput(), ...uploadedMedia].filter(item => item.type === "image").map(item => item.url),
      authorId,
      authorName,
      aiGenerationSessionId: adminPostAiGenerationSessionId,
      aiGenerationId: adminPostAiGenerationId
    };
    const response = await authenticatedFetch(id ? `/api/admin/posts/${encodeURIComponent(id)}` : "/api/admin/posts", {
      method: id ? "PUT" : "POST",
      body: JSON.stringify(payload)
    });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã lưu bài viết");
    closePostModal();
    await Promise.all([loadPosts(), loadAdminDashboard()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deletePost(id) {
  if (!await window.TravelwAIConfirm("Xóa bài viết này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/posts/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readAdminJson(response);
    showToast(result.message || "Đã xóa bài viết");
    await Promise.all([loadPosts(), loadAdminDashboard()]);
  } catch (error) {
    showToast(error.message);
  }
}

function setupAdminTabs() {
  document.querySelectorAll(".tab-btn").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll(".tab-btn").forEach(b => b.classList.remove("active"));
      button.classList.add("active");
      const target = button.dataset.tab;
      document.querySelectorAll(".admin-tab-panel").forEach(panel => panel.style.display = "none");
      const activePanel = document.getElementById(`tab-${target}`);
      if (activePanel) activePanel.style.display = target === "storage" ? "grid" : "block";
      if (target === "storage") loadAdminStorage();
      if (target === "revenue") loadAdminRevenue();
    });
  });
}

function setupAdminSearchBox(inputId, clearButtonId, onQueryChange, renderFunction) {
  const input = document.getElementById(inputId);
  const clearButton = document.getElementById(clearButtonId);
  if (!input) return;

  input.addEventListener("input", () => {
    onQueryChange(input.value || "");
    renderFunction();
  });

  clearButton?.addEventListener("click", () => {
    input.value = "";
    onQueryChange("");
    input.focus();
    renderFunction();
  });
}

function setupAdminSearch() {
  setupAdminSearchBox("accountSearchInput", "clearAccountSearch", (value) => {
    accountSearchQuery = value;
  }, renderAccounts);

  setupAdminSearchBox("storageSearchInput", "clearStorageSearch", (value) => {
    adminStorageSearchQuery = value;
  }, renderAdminStorageUsers);

  setupAdminSearchBox("scheduleSearchInput", "clearScheduleSearch", (value) => {
    scheduleSearchQuery = value;
  }, renderSchedules);

  setupAdminSearchBox("planStatusSearchInput", "clearPlanStatusSearch", (value) => {
    planStatusSearchQuery = value;
  }, renderPlanStatusOptions);

  setupAdminSearchBox("revenueSearchInput", "clearRevenueSearch", (value) => {
    revenueSearchQuery = value;
  }, renderAdminRevenue);

  setupAdminSearchBox("postSearchInput", "clearAdminPostSearch", (value) => {
    postSearchQuery = value;
  }, renderPosts);
}

function createChatbotStyleDraftId() {
  if (window.crypto?.randomUUID) return `style-${window.crypto.randomUUID().slice(0, 8)}`;
  return `style-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

function isFreeChatbotStyleId(id) {
  return ["default", "gentle", "formal"].includes(String(id || "").trim().toLowerCase());
}

function normalizeChatbotStyleDraft(item, index) {
  const id = String(item?.id || createChatbotStyleDraftId()).trim();
  const isFree = isFreeChatbotStyleId(id) || item?.isFree === true || item?.is_free === true;
  const rawPrice = Number(item?.price ?? item?.priceAmount ?? item?.price_amount ?? 10000);
  return {
    id,
    name: String(item?.name || `Phong cách ${index + 1}`).trim(),
    prompt: String(item?.prompt || ""),
    price: isFree ? 0 : Math.max(1000, Number.isFinite(rawPrice) ? rawPrice : 10000),
    maxResponseWords: Math.max(50, Math.min(2000, Number(item?.maxResponseWords ?? item?.max_response_words ?? 500) || 500)),
    isFree
  };
}

function getActiveChatbotStyleIndex() {
  let index = chatbotStylesDraft.findIndex(style => style.id === chatbotActiveStyleId);
  if (index >= 0) return index;
  index = chatbotStylesDraft.findIndex(style => style.id === chatbotDefaultStyleId);
  if (index < 0) index = 0;
  chatbotActiveStyleId = chatbotStylesDraft[index]?.id || "";
  return index;
}

function syncActiveChatbotStyleInputs() {
  const index = getActiveChatbotStyleIndex();
  const style = chatbotStylesDraft[index];
  const editor = document.getElementById("chatbotStyleEditor");
  const nameInput = document.getElementById("chatbotStyleName");
  const promptInput = document.getElementById("chatbotStylePrompt");
  const priceInput = document.getElementById("chatbotStylePrice");
  const wordLimitInput = document.getElementById("chatbotStyleMaxResponseWords");
  const defaultButton = document.getElementById("chatbotDefaultStyleButton");
  const removeButton = document.getElementById("removeChatbotStyleButton");
  const hasStyle = Boolean(style);

  if (editor) editor.classList.toggle("is-empty", !hasStyle);
  if (nameInput) {
    nameInput.disabled = !hasStyle;
    nameInput.value = style?.name || "";
  }
  if (promptInput) {
    promptInput.disabled = !hasStyle;
    promptInput.value = style?.prompt || "";
  }
  if (priceInput) {
    priceInput.disabled = !hasStyle || Boolean(style?.isFree);
    priceInput.value = style?.isFree ? "0" : String(style?.price || 10000);
  }
  if (wordLimitInput) {
    wordLimitInput.disabled = !hasStyle;
    wordLimitInput.value = String(style?.maxResponseWords || 500);
  }
  if (defaultButton) {
    const isDefault = hasStyle && style.id === chatbotDefaultStyleId;
    defaultButton.disabled = !hasStyle;
    defaultButton.classList.toggle("is-default", isDefault);
    defaultButton.textContent = isDefault ? "Mặc định" : "Đặt mặc định";
  }
  if (removeButton) removeButton.disabled = !hasStyle || chatbotStylesDraft.length <= 3 || Boolean(style?.isFree);
}

function renderChatbotStyleList() {
  const list = document.getElementById("chatbotStyleList");
  if (!list) return;
  const query = String(chatbotStyleSearchQuery || "").trim().toLocaleLowerCase("vi");
  const filtered = chatbotStylesDraft.filter(style => !query || String(style.name || "").toLocaleLowerCase("vi").includes(query));

  list.innerHTML = filtered.length
    ? filtered.map(style => `
      <button class="chatbot-style-choice${style.id === chatbotActiveStyleId ? " is-active" : ""}" type="button" role="option" aria-selected="${style.id === chatbotActiveStyleId ? "true" : "false"}" data-chatbot-style-id="${escapeAttr(style.id)}">
        <span data-no-translate>${escapeHtml(style.name || "Chưa đặt tên")}</span>
        <span class="chatbot-style-price-mark">${style.isFree ? "Miễn phí" : money(style.price || 0)}</span>
        ${style.id === chatbotDefaultStyleId ? '<span class="chatbot-style-default-mark">Mặc định</span>' : ""}
      </button>`).join("")
    : '<div class="chatbot-style-empty">Không tìm thấy</div>';

  list.querySelectorAll("[data-chatbot-style-id]").forEach(button => {
    button.addEventListener("click", () => {
      chatbotActiveStyleId = String(button.dataset.chatbotStyleId || "");
      renderChatbotStyleList();
      syncActiveChatbotStyleInputs();
      document.getElementById("chatbotStyleName")?.focus();
    });
  });
  window.TravelwAIIcons?.render?.(list);
  syncActiveChatbotStyleInputs();
}

function commitActiveChatbotStyleInputs() {
  const index = getActiveChatbotStyleIndex();
  const style = chatbotStylesDraft[index];
  if (!style) return;
  const nameInput = document.getElementById("chatbotStyleName");
  const promptInput = document.getElementById("chatbotStylePrompt");
  const priceInput = document.getElementById("chatbotStylePrice");
  const wordLimitInput = document.getElementById("chatbotStyleMaxResponseWords");
  style.name = String(nameInput?.value ?? style.name ?? "");
  style.prompt = String(promptInput?.value ?? style.prompt ?? "");
  if (!style.isFree) {
    const price = Number(priceInput?.value ?? style.price ?? 10000);
    style.price = Number.isFinite(price) ? Math.max(1000, Math.min(100000000, price)) : 10000;
  }
  const maxResponseWords = Number(wordLimitInput?.value ?? style.maxResponseWords ?? 500);
  style.maxResponseWords = Number.isFinite(maxResponseWords) ? Math.max(50, Math.min(2000, Math.round(maxResponseWords))) : 500;
}

function updateActiveChatbotStyleName(value) {
  const index = getActiveChatbotStyleIndex();
  if (!chatbotStylesDraft[index]) return;
  chatbotStylesDraft[index].name = value;
  renderChatbotStyleList();
}

function updateActiveChatbotStylePrompt(value) {
  const index = getActiveChatbotStyleIndex();
  if (!chatbotStylesDraft[index]) return;
  chatbotStylesDraft[index].prompt = value;
}

function updateActiveChatbotStylePrice(value) {
  const index = getActiveChatbotStyleIndex();
  const style = chatbotStylesDraft[index];
  if (!style || style.isFree) return;
  const price = Number(value);
  style.price = Number.isFinite(price) ? Math.max(1000, Math.min(100000000, price)) : 10000;
  renderChatbotStyleList();
}


function updateActiveChatbotStyleMaxResponseWords(value) {
  const index = getActiveChatbotStyleIndex();
  const style = chatbotStylesDraft[index];
  if (!style) return;
  const words = Number(value);
  style.maxResponseWords = Number.isFinite(words) ? Math.max(50, Math.min(2000, Math.round(words))) : 500;
}

function setActiveChatbotStyleAsDefault() {
  const index = getActiveChatbotStyleIndex();
  const style = chatbotStylesDraft[index];
  if (!style) return;
  chatbotDefaultStyleId = style.id;
  renderChatbotStyleList();
}

function removeActiveChatbotStyle() {
  if (chatbotStylesDraft.length <= 1) return;
  const index = getActiveChatbotStyleIndex();
  const removed = chatbotStylesDraft.splice(index, 1)[0];
  if (removed?.id === chatbotDefaultStyleId) chatbotDefaultStyleId = chatbotStylesDraft[Math.max(0, index - 1)]?.id || chatbotStylesDraft[0]?.id || "";
  chatbotActiveStyleId = chatbotStylesDraft[Math.min(index, chatbotStylesDraft.length - 1)]?.id || "";
  renderChatbotStyleList();
}

async function loadChatbotStyleSetting(force = false) {
  if (chatbotStyleLoaded && !force) return;
  const nameInput = document.getElementById("chatbotDisplayName");
  if (!nameInput) return;

  nameInput.disabled = true;
  try {
    const response = await authenticatedFetch("/api/admin/chatbot-style");
    const result = await readAdminJson(response);
    const data = result?.data || {};
    nameInput.value = String(data.chatbotName || "WaiGo");
    chatbotStylesDraft = (Array.isArray(data.styles) ? data.styles : []).map(normalizeChatbotStyleDraft);
    if (!chatbotStylesDraft.length) chatbotStylesDraft = [normalizeChatbotStyleDraft({ name: "Mặc định", prompt: "" }, 0)];
    chatbotDefaultStyleId = String(data.defaultStyleId || chatbotStylesDraft[0].id);
    chatbotActiveStyleId = chatbotStylesDraft.some(style => style.id === chatbotActiveStyleId) ? chatbotActiveStyleId : chatbotDefaultStyleId;
    chatbotStyleSearchQuery = "";
    const searchInput = document.getElementById("chatbotStyleSearchInput");
    if (searchInput) searchInput.value = "";
    renderChatbotStyleList();
    chatbotStyleLoaded = true;
  } catch (error) {
    showToast(error.message || "Không tải được cấu hình nói chuyện của chatbot.");
  } finally {
    nameInput.disabled = false;
  }
}

function openChatbotStyleModal() {
  document.getElementById("chatbotStyleModal")?.classList.add("open");
  loadChatbotStyleSetting().then(() => document.getElementById("chatbotStyleSearchInput")?.focus());
}

function closeChatbotStyleModal() {
  document.getElementById("chatbotStyleModal")?.classList.remove("open");
}

function addChatbotStyle() {
  if (chatbotStylesDraft.length >= 20) return showToast("Chỉ được tạo tối đa 20 phong cách.");
  const style = normalizeChatbotStyleDraft({ name: `Phong cách ${chatbotStylesDraft.length + 1}`, prompt: "", price: 10000, maxResponseWords: 500 }, chatbotStylesDraft.length);
  chatbotStylesDraft.push(style);
  chatbotActiveStyleId = style.id;
  chatbotStyleSearchQuery = "";
  const searchInput = document.getElementById("chatbotStyleSearchInput");
  if (searchInput) searchInput.value = "";
  renderChatbotStyleList();
  document.getElementById("chatbotStyleName")?.focus();
}

async function submitChatbotStyleForm(event) {
  event?.preventDefault?.();
  if (chatbotStyleSaving) return;

  commitActiveChatbotStyleInputs();
  const nameInput = document.getElementById("chatbotDisplayName");
  const saveButton = document.getElementById("saveChatbotStyleButton");
  const chatbotName = String(nameInput?.value || "").trim();
  if (!chatbotName) return showToast("Vui lòng nhập tên chatbot.");
  if (chatbotName.length > 40) return showToast("Tên chatbot tối đa 40 ký tự.");

  const styles = chatbotStylesDraft.map(style => ({
    id: style.id,
    name: String(style.name || "").trim(),
    prompt: String(style.prompt || "").trim(),
    price: style.isFree ? 0 : Number(style.price || 10000),
    maxResponseWords: Math.max(50, Math.min(2000, Number(style.maxResponseWords || 500)))
  }));
  if (styles.some(style => !style.name)) return showToast("Mỗi phong cách phải có tên.");
  if (styles.some(style => style.name.length > 60)) return showToast("Tên phong cách tối đa 60 ký tự.");
  if (styles.some(style => style.prompt.length > 4000)) return showToast("Nội dung mỗi phong cách tối đa 4000 ký tự.");
  if (styles.some(style => style.maxResponseWords < 50 || style.maxResponseWords > 2000)) return showToast("Giới hạn trả lời phải từ 50 đến 2000 từ.");

  chatbotStyleSaving = true;
  if (saveButton) {
    saveButton.disabled = true;
    saveButton.textContent = "Đang lưu...";
  }

  try {
    const response = await authenticatedFetch("/api/admin/chatbot-style", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        chatbotName,
        defaultStyleId: chatbotDefaultStyleId,
        styles
      })
    });
    const result = await readAdminJson(response);
    const data = result?.data || {};
    if (nameInput) nameInput.value = String(data.chatbotName || chatbotName);
    chatbotStylesDraft = (Array.isArray(data.styles) ? data.styles : styles).map(normalizeChatbotStyleDraft);
    chatbotDefaultStyleId = String(data.defaultStyleId || chatbotStylesDraft[0]?.id || "");
    chatbotActiveStyleId = chatbotStylesDraft.some(style => style.id === chatbotActiveStyleId) ? chatbotActiveStyleId : chatbotDefaultStyleId;
    renderChatbotStyleList();
    chatbotStyleLoaded = true;
    closeChatbotStyleModal();
    showToast(result.message || "Đã lưu cấu hình chatbot.");
    const chatbotSettingsUpdatedAt = String(Date.now());
    try { localStorage.setItem("travelwai-chatbot-settings-updated-at", chatbotSettingsUpdatedAt); } catch (_) {}
    window.dispatchEvent(new CustomEvent("travelwai:chatbot-admin-settings-updated", { detail: { updatedAt: chatbotSettingsUpdatedAt } }));
    window.TravelwAIChatbotSettings?.load?.(true).catch?.(() => {});
  } catch (error) {
    showToast(error.message || "Không lưu được cấu hình chatbot.");
  } finally {
    chatbotStyleSaving = false;
    if (saveButton) {
      saveButton.disabled = false;
      saveButton.textContent = "Lưu cấu hình";
    }
  }
}

function setupChatbotStyleControl() {
  document.getElementById("changeChatbotStyleButton")?.addEventListener("click", openChatbotStyleModal);
  document.getElementById("chatbotStyleForm")?.addEventListener("submit", submitChatbotStyleForm);
  document.getElementById("addChatbotStyleButton")?.addEventListener("click", addChatbotStyle);
  document.getElementById("chatbotStyleSearchInput")?.addEventListener("input", event => {
    chatbotStyleSearchQuery = event.target.value;
    renderChatbotStyleList();
  });
  document.getElementById("chatbotStyleName")?.addEventListener("input", event => updateActiveChatbotStyleName(event.target.value));
  document.getElementById("chatbotStylePrompt")?.addEventListener("input", event => updateActiveChatbotStylePrompt(event.target.value));
  document.getElementById("chatbotStylePrice")?.addEventListener("input", event => updateActiveChatbotStylePrice(event.target.value));
  document.getElementById("chatbotStyleMaxResponseWords")?.addEventListener("input", event => updateActiveChatbotStyleMaxResponseWords(event.target.value));
  document.getElementById("chatbotDefaultStyleButton")?.addEventListener("click", setActiveChatbotStyleAsDefault);
  document.getElementById("removeChatbotStyleButton")?.addEventListener("click", removeActiveChatbotStyle);
}

function openSystemSettingsModal() {
  document.getElementById("systemSettingsModal")?.classList.add("open");
}

function closeSystemSettingsModal() {
  document.getElementById("systemSettingsModal")?.classList.remove("open");
}

async function buildOptimizedImageFormData(file, mainFieldName) {
  const formData = new FormData();
  if (window.TravelwAIImageOptimizer?.optimizeImageFileVariants) {
    const variants = await window.TravelwAIImageOptimizer.optimizeImageFileVariants(file);
    const primary = variants.webp || variants.primary || file;
    formData.append(mainFieldName, primary, primary.name || file.name.replace(/\.[^/.]+$/, ".webp"));
  } else {
    const optimized = window.TravelwAIImageOptimizer
      ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
      : file;
    formData.append(mainFieldName, optimized, optimized.name || file.name);
  }
  return formData;
}

function chooseSiteBackground(theme) {
  selectedSiteBackgroundTheme = theme === "dark" ? "dark" : "light";
  document.getElementById("siteBackgroundFile")?.click();
}

async function uploadSiteBackground(file) {
  if (!file) return;
  const button = document.getElementById("changeSystemSettingsButton");
  const originalText = button?.textContent || "Cấu hình";
  const formData = await buildOptimizedImageFormData(file, "image");

  try {
    if (button) {
      button.disabled = true;
      button.textContent = "Đang tải nền...";
    }
    const response = await authenticatedFetch(`/api/admin/background/${encodeURIComponent(selectedSiteBackgroundTheme)}`, {
      method: "POST",
      body: formData
    });
    const result = await readAdminJson(response);
    const data = result.data || {};
    window.TravelwAISiteBranding?.applyBackground?.(
      data.theme || selectedSiteBackgroundTheme,
      data.backgroundUrl || data.background_url,
      data.version || Date.now()
    );
    closeSystemSettingsModal();
    showToast(result.message || "Đã cập nhật ảnh nền");
  } catch (error) {
    showToast(error.message || "Không thể cập nhật ảnh nền");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
    const input = document.getElementById("siteBackgroundFile");
    if (input) input.value = "";
  }
}

function chooseAdminSiteLogo() {
  if (adminSiteLogoUploading) return;
  const input = document.getElementById("adminSiteLogoInput");
  if (!input) return showToast("Không mở được trình chọn logo.");
  input.value = "";
  input.click();
}

async function uploadAdminSiteLogo(file) {
  if (!file || adminSiteLogoUploading) return;
  if (!/^image\/(jpeg|png|gif|webp)$/i.test(file.type || "") && !/\.(jpe?g|png|gif|webp)$/i.test(file.name || "")) {
    return showToast("Logo phải là ảnh JPG, PNG, GIF hoặc WEBP.");
  }
  if (file.size > 10 * 1024 * 1024) return showToast("Logo tối đa 10MB.");

  const button = document.getElementById("changeSiteLogoButton");
  adminSiteLogoUploading = true;
  if (button) {
    button.disabled = true;
    button.classList.add("is-uploading");
  }

  try {
    const form = new FormData();
    form.append("logo", file);
    const response = await authenticatedFetch("/api/manage/site-logo", { method: "POST", body: form });
    const result = await readAdminJson(response);
    const data = result.data || {};
    window.TravelwAISiteBranding?.applyLogo?.(data.logoUrl || data.logo_url, data.version || data.logoVersion || Date.now());
    showToast(result.message || "Đã cập nhật logo TravelwAI.");
  } catch (error) {
    showToast(error.message || "Không thể cập nhật logo.");
  } finally {
    adminSiteLogoUploading = false;
    if (button) {
      button.disabled = false;
      button.classList.remove("is-uploading");
    }
    const input = document.getElementById("adminSiteLogoInput");
    if (input) input.value = "";
  }
}

function setupAdminSiteLogoControl() {
  document.getElementById("changeSiteLogoButton")?.addEventListener("click", chooseAdminSiteLogo);
  document.getElementById("adminSiteLogoInput")?.addEventListener("change", event => {
    const file = event.target?.files?.[0];
    if (file) uploadAdminSiteLogo(file);
  });
}

function setupSystemSettingsModal() {
  const button = document.getElementById("changeSystemSettingsButton");
  const backgroundInput = document.getElementById("siteBackgroundFile");
  button?.addEventListener("click", openSystemSettingsModal);
  backgroundInput?.addEventListener("change", () => uploadSiteBackground(backgroundInput.files?.[0]));
}

let travelwaiAdminStarted = false;

function startAdminPanel() {
  if (travelwaiAdminStarted || document.body?.dataset?.page !== "admin") return;
  travelwaiAdminStarted = true;


  runAdminSetupSafely(setupAdminTabs, "tab Admin");
  runAdminSetupSafely(setupAdminSearch, "tìm kiếm Admin");
  document.getElementById("refreshAdminRevenue")?.addEventListener("click", () => loadAdminRevenue(true));
  runAdminSetupSafely(setupAdminStorage, "quản lý dung lượng");
  runAdminSetupSafely(setupChatbotStyleControl, "phong cách nói chuyện WaiGo");
  runAdminSetupSafely(setupAdminSiteLogoControl, "đổi logo website");
  runAdminSetupSafely(setupSystemSettingsModal, "cấu hình hệ thống");
  document.getElementById("accountForm")?.addEventListener("submit", submitAccountForm);
  document.getElementById("accountRole")?.addEventListener("change", () => {
    syncAccountLevelFields(false);
    syncAccountPlanExpiryField();
  });
  document.getElementById("accountSalesLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("accountOfferLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("accountServiceLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("salesLevelSettingsForm")?.addEventListener("submit", submitSalesLevelSettingsForm);
  document.getElementById("accountPlanSettingsForm")?.addEventListener("submit", submitAccountPlanSettingsForm);
  document.getElementById("planStatusOptionForm")?.addEventListener("submit", submitPlanStatusOptionForm);
  document.getElementById("travelTagForm")?.addEventListener("submit", submitTravelTagForm);
  document.getElementById("postForm")?.addEventListener("submit", submitPostForm);
  document.getElementById("postAiGenerateButton")?.addEventListener("click", generateAdminPostContentFromFestival);
  document.getElementById("postImageUploadButton")?.addEventListener("click", () => document.getElementById("postImageFiles")?.click());
  document.getElementById("postImageFiles")?.addEventListener("change", (event) => {
    const addedFiles = Array.from(event.target.files || []);
    try {
      addedFiles.forEach(validateAdminPostMediaFile);
      const remaining = Math.max(0, 12 - adminPostMediaFromInput().length - selectedAdminPostMediaFiles.length);
      selectedAdminPostMediaFiles = selectedAdminPostMediaFiles.concat(addedFiles.slice(0, remaining));
      if (addedFiles.length > remaining) showToast("Mỗi bài viết tối đa 12 ảnh hoặc video.");
      event.target.value = "";
      renderAdminPostImagePreview();
    } catch (error) {
      event.target.value = "";
      showToast(error.message || "Tệp không hợp lệ.");
    }
  });
  runAdminSetupSafely(updateAdminPageRoleLinks, "liên kết vai trò");
  document.documentElement.dataset.adminScriptStarted = "true";
  const adminLoadingWatchdog = window.setTimeout(() => {
    clearStuckAdminLoadingStates("Máy chủ phản hồi quá lâu. Vui lòng tải lại trang.");
  }, 22000);
  Promise.resolve()
    .then(() => loadAdminPage())
    .catch((error) => {
      console.error("Không thể khởi tạo trang Admin:", error);
      clearStuckAdminLoadingStates(error?.message || "Không thể tải dữ liệu quản trị.");
    })
    .finally(() => window.clearTimeout(adminLoadingWatchdog));
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", startAdminPanel, { once: true });
} else {
  startAdminPanel();
}
window.addEventListener("load", startAdminPanel, { once: true });

window.loadAdminPage = loadAdminPage;
window.loadAdminDashboard = loadAdminDashboard;
window.openAccountModal = openAccountModal;
window.closeAccountModal = closeAccountModal;
window.openSystemSettingsModal = openSystemSettingsModal;
window.closeSystemSettingsModal = closeSystemSettingsModal;
window.chooseSiteBackground = chooseSiteBackground;
window.deleteAccount = deleteAccount;
window.deleteSchedule = deleteSchedule;
window.renderAccounts = renderAccounts;
window.renderSchedules = renderSchedules;
window.openPlanStatusOptionModal = openPlanStatusOptionModal;
window.closePlanStatusOptionModal = closePlanStatusOptionModal;
window.disablePlanStatusOption = disablePlanStatusOption;
window.openTravelTagModal = openTravelTagModal;
window.closeTravelTagModal = closeTravelTagModal;
window.openProvinceTagModal = openProvinceTagModal;
window.closeProvinceTagModal = closeProvinceTagModal;
window.loadPlanStatusOptions = loadPlanStatusOptions;
window.loadProvinceTags = loadProvinceTags;
window.loadAdminRevenue = loadAdminRevenue;
window.renderAdminRevenue = renderAdminRevenue;

window.loadPosts = loadPosts;
window.renderPosts = renderPosts;
window.openPostModal = openPostModal;
window.closePostModal = closePostModal;
window.generateAdminPostContentFromFestival = generateAdminPostContentFromFestival;
window.deletePost = deletePost;
