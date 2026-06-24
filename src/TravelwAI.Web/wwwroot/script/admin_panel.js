let travelwaiAccounts = [];
let travelwaiSchedules = [];
let travelwaiPlanStatuses = [];
let travelwaiProvinceTags = [];
let travelwaiAllowedTags = [];
let travelwaiTravelTags = [];
let accountSearchQuery = "";
let scheduleSearchQuery = "";
let planStatusSearchQuery = "";
let provinceTagSearchQuery = "";
let travelwaiPosts = [];
let postSearchQuery = "";
let selectedAdminPostImageFiles = [];
let selectedAiAvatarAssistant = "travelwinne";
let selectedSiteBackgroundTheme = "light";

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

function cleanAccountDisplayName(value) {
  return String(value ?? "")
    .replace(/^\s*Tài\s*khoản\s+/i, "")
    .trim();
}

async function loadAdminPage() {
  await loadAccounts();
  await Promise.all([loadAdminDashboard(), loadTours(), loadSchedules(), loadPlanStatusOptions(), loadProvinceTags(), loadPosts()]);
}

async function loadAdminDashboard() {
  try {
    const response = await authenticatedFetch("/api/admin/dashboard");
    const result = await readJson(response);
    const data = result.data || {};
    setText("adminStatAccounts", data.accounts || 0);
    setText("adminStatLocked", data.lockedAccounts || 0);
    setText("adminStatSales", data.tourSalesAccounts || 0);
    setText("adminStatTours", data.tours || 0);
    setText("adminStatSchedules", data.schedules || 0);
    setText("adminStatPlanStatuses", data.planStatuses || 0);
    setText("adminStatProvinces", data.provinces || 0);
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
    const result = await readJson(response);
    travelwaiAccounts = Array.isArray(result.data) ? result.data : [];
    if (typeof setAvailableTourSalesAccounts === "function") setAvailableTourSalesAccounts(travelwaiAccounts);
    renderAccounts();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function getAdminSearchValue(value) {
  if (typeof normalizeSearchText === "function") return normalizeSearchText(value);
  return String(value ?? "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/\s+/g, " ")
    .trim();
}

function getAccountOfferPercent(account) {
  return Math.max(0, Math.min(25, Number(account?.offer_discount_percent ?? account?.offerDiscountPercent ?? 0)));
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
    account?.role || "User",
    locked,
    `Ưu đãi ${discount}%`,
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
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${query ? "Không tìm thấy tài khoản phù hợp." : "Chưa có tài khoản."}</td></tr>`;
    return;
  }

  body.innerHTML = visibleAccounts.map((account) => {
    const role = account.role || "User";
    const protectedAdmin = account.is_protected || account.isProtected;
    const locked = account.is_locked || account.isLocked;
    return `
      <tr>
        <td><strong>${escapeHtml(account.username || "Người dùng")}</strong><br><small>${escapeHtml(account.email || "")}</small></td>
        <td>${roleBadge(role)}</td>
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
    const result = await readJson(response);
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
    body.innerHTML = `<tr><td colspan="5" class="empty-line">${query ? "Không tìm thấy lịch trình phù hợp." : "Chưa có lịch trình."}</td></tr>`;
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

function roleBadge(role) {
  if (role === "Admin") return `<span class="badge badge-admin">Admin</span>`;
  if (role === "Tour Sales") return `<span class="badge badge-sales">Tour Sales</span>`;
  return `<span class="badge badge-user">User</span>`;
}

function openAccountModal(id) {
  const account = travelwaiAccounts.find(a => String(a.id) === String(id));
  if (!account) return showToast("Không tìm thấy tài khoản");
  document.getElementById("accountId").value = account.id || "";
  document.getElementById("accountEmail").value = account.email || "";
  document.getElementById("accountUsername").value = account.username || "";
  const offerInput = document.getElementById("accountOfferDiscount");
  if (offerInput) offerInput.value = `${getAccountOfferPercent(account)}%`;
  document.getElementById("accountRole").value = account.role || "User";
  document.getElementById("accountLocked").checked = !!(account.is_locked || account.isLocked);

  const protectedAdmin = account.is_protected || account.isProtected;
  document.getElementById("accountRole").disabled = !!protectedAdmin;
  document.getElementById("accountLocked").disabled = !!protectedAdmin;
  document.getElementById("accountModal")?.classList.add("open");
}

function closeAccountModal() {
  document.getElementById("accountModal")?.classList.remove("open");
}

async function submitAccountForm(event) {
  event.preventDefault();
  const id = document.getElementById("accountId").value;
  const payload = {
    username: document.getElementById("accountUsername").value.trim(),
    role: document.getElementById("accountRole").value,
    isLocked: document.getElementById("accountLocked").checked
  };
  try {
    const response = await authenticatedFetch(`/api/admin/accounts/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(payload)
    });
    const result = await readJson(response);
    showToast(result.message || "Đã cập nhật tài khoản");
    closeAccountModal();
    await Promise.all([loadAdminDashboard(), loadAccounts(), loadPosts()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteAccount(id) {
  if (!confirm("Xóa tài khoản này? Tour và bài viết của tài khoản sẽ tự động chuyển sang Admin.")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/accounts/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xóa tài khoản");
    await Promise.all([loadAdminDashboard(), loadAccounts(), loadPosts()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deleteSchedule(id) {
  if (!confirm("Xóa lịch trình này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/schedules/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
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
    const result = await readJson(response);
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
    body.innerHTML = `<tr><td colspan="5" class="empty-line">${query ? "Không tìm thấy trạng thái phù hợp." : "Chưa có trạng thái."}</td></tr>`;
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

async function loadProvinceTags() {
  const body = document.getElementById("provinceTagTableBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="4" class="empty-line">Đang tải tỉnh thành...</td></tr>`;
  try {
    const response = await authenticatedFetch("/api/admin/province-tags");
    const result = await readJson(response);
    travelwaiProvinceTags = Array.isArray(result.data) ? result.data : [];
    applyAdminTravelTags(result);
    renderProvinceTags();
  } catch (error) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
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
    body.innerHTML = `<tr><td colspan="4" class="empty-line">${query ? "Không tìm thấy tỉnh thành phù hợp." : "Chưa có tỉnh thành."}</td></tr>`;
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
    const result = await readJson(response);
    showToast(result.message || "Đã lưu trạng thái");
    closePlanStatusOptionModal();
    await Promise.all([loadAdminDashboard(), loadPlanStatusOptions()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function disablePlanStatusOption(key) {
  if (!confirm("Ẩn trạng thái này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/plan-status-options/${encodeURIComponent(key)}`, { method: "DELETE" });
    const result = await readJson(response);
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
    const result = await readJson(response);
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
  if (!confirm(`Xoá tag "${tagName}"?`)) return;

  try {
    const response = await authenticatedFetch(`/api/admin/travel-tags/${encodeURIComponent(tagName)}`, { method: "DELETE" });
    const result = await readJson(response);
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
    const result = await readJson(response);
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
    const result = await readJson(response);
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

function parsePostImageUrls(post) {
  const raw = getValue(post, "image_urls", "imageUrls", "images", "photos", "photo_urls", "photoUrls", "image", "thumbnail");
  if (!raw) return [];
  if (Array.isArray(raw)) return raw.map(String).filter(Boolean);
  if (typeof raw === "string") {
    const text = raw.trim();
    if (!text) return [];
    if (text.startsWith("[") && text.endsWith("]")) {
      try {
        const parsed = JSON.parse(text);
        if (Array.isArray(parsed)) return parsed.map(String).filter(Boolean);
      } catch (_) {}
    }
    return text.split(/\n|,|\|/).map(x => x.trim()).filter(Boolean);
  }
  return [];
}

function postImageUrlsFromTextarea() {
  const value = document.getElementById("postImageUrls")?.value || "";
  return value.split(/\n|,|\|/).map(x => x.trim()).filter(Boolean);
}

function renderAdminPostImagePreview() {
  const box = document.getElementById("postImagePreview");
  if (!box) return;
  const existing = postImageUrlsFromTextarea();
  const files = selectedAdminPostImageFiles || [];
  const items = [
    ...existing.map(url => `<span>${escapeHtml(url.split('/').pop() || url)}</span>`),
    ...files.map(file => `<span>${escapeHtml(file.name)}</span>`)
  ];
  box.innerHTML = items.join("");
}

function validatePostImageFile(file) {
  if (!file) return;
  if (!file.type || !file.type.startsWith("image/")) throw new Error("Vui lòng chọn đúng tệp ảnh.");
  if (file.size > 10 * 1024 * 1024) throw new Error("Mỗi ảnh bài viết phải nhỏ hơn 10MB.");
}

async function uploadAdminPostImages(files) {
  const list = Array.from(files || []);
  if (!list.length) return [];
  list.forEach(validatePostImageFile);
  const optimizedList = window.TravelwAIImageOptimizer
    ? await window.TravelwAIImageOptimizer.optimizeImageFiles(list)
    : list;
  const formData = new FormData();
  optimizedList.forEach(file => formData.append("images", file, file.name));
  const response = await authenticatedFetch("/api/posts/images", { method: "POST", body: formData });
  const result = await readJson(response);
  return Array.isArray(result.urls) ? result.urls : (Array.isArray(result.images) ? result.images : []);
}

function getAccountId(account) {
  return String(getValue(account, "id", "uid", "user_id", "userId") || "");
}

function getAccountDisplayName(account) {
  return cleanAccountDisplayName(getValue(account, "username", "displayName", "display_name", "name", "email")) || "Tài khoản";
}

function getManagedAccountDisplayNameById(id) {
  if (!id) return "";
  const account = (Array.isArray(travelwaiAccounts) ? travelwaiAccounts : [])
    .find(item => getAccountId(item) === String(id));
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
    body.innerHTML = `<tr><td colspan="6" class="empty-line">${query ? "Không tìm thấy bài viết phù hợp." : "Chưa có bài viết."}</td></tr>`;
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
  const roleOrder = { admin: 0, "tour sales": 1, user: 2 };
  const source = Array.isArray(travelwaiAccounts) ? travelwaiAccounts : [];
  return source
    .filter(account => getAccountId(account))
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
    const id = getAccountId(account);
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
  const result = await readJson(response);
  const post = result.data || result.post || result;
  const index = travelwaiPosts.findIndex(item => String(getValue(item, "id")) === String(id));
  if (index >= 0 && post) travelwaiPosts[index] = { ...travelwaiPosts[index], ...post };
  return post;
}

async function generateAdminPostContentFromFestival() {
  const titleInput = document.getElementById("postTitle");
  const festivalInput = document.getElementById("postFestival");
  const festival = festivalInput?.value.trim() || "";
  if (!festival) {
    festivalInput?.focus();
    showToast("Vui lòng nhập Lễ hội/ngày lễ trước khi dùng AI.");
    return;
  }

  const button = document.getElementById("postAiButton");
  const originalText = button?.textContent || "AI";
  try {
    if (button) {
      button.disabled = true;
      button.textContent = "...";
    }
    const response = await authenticatedFetch("/api/posts/ai-content", {
      method: "POST",
      body: JSON.stringify({
        title: titleInput?.value.trim() || "",
        festival,
        province: document.getElementById("postProvince")?.value.trim() || "",
        month: Number(document.getElementById("postMonth")?.value || new Date().getMonth() + 1)
      })
    });
    const result = await readJson(response);
    const data = result.data || result;
    const content = stripPostSourceLines(data.content || "");
    const summary = stripPostSourceLines(data.summary || "");
    if (!content) throw new Error("Không tìm thấy nội dung thật phù hợp.");

    if (titleInput && data.title) titleInput.value = data.title;

    document.getElementById("postContent").value = content;

    const summaryInput = document.getElementById("postSummary");
    if (summaryInput && summary) summaryInput.value = summary;

    const festivalInput = document.getElementById("postFestival");
    if (festivalInput && data.festival) festivalInput.value = data.festival;

    const provinceInput = document.getElementById("postProvince");
    if (provinceInput && data.province) provinceInput.value = data.province;

    const monthSelect = document.getElementById("postMonth");
    const month = Number(data.month || 0);
    if (monthSelect && month >= 1 && month <= 12) monthSelect.value = String(month);

    showToast(result.message || "Đã điền nội dung khám phá văn hoá lịch sử.");
  } catch (error) {
    showToast(error.message || "Không tạo được nội dung từ Lễ hội/ngày lễ này.");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
  }
}

async function openPostModal(id = "") {
  let post = id ? travelwaiPosts.find(item => String(getValue(item, "id")) === String(id)) : null;
  if (id) {
    try {
      post = await fetchFullAdminPost(id);
    } catch (error) {
      showToast(error.message || "Không tải được bài viết.");
      if (!post) return;
    }
  }
  document.getElementById("postModalTitle").textContent = post ? "Sửa bài viết" : "Thêm bài viết";
  document.getElementById("postId").value = post?.id || "";
  document.getElementById("postTitle").value = post?.title || "";
  document.getElementById("postMonth").value = String(post?.month || new Date().getMonth() + 1);
  document.getElementById("postStatus").value = post?.status || "Hiển thị";
  document.getElementById("postFestival").value = post?.festival || post?.holiday_type || "";
  document.getElementById("postProvince").value = post?.province || "";
  document.getElementById("postTourKeywords").value = post?.tour_keywords || post?.tourKeywords || "";
  document.getElementById("postSummary").value = stripPostSourceLines(post?.summary || "");
  document.getElementById("postContent").value = stripPostSourceLines(post?.content || "");
  document.getElementById("postImageUrls").value = parsePostImageUrls(post || {}).join("\n");
  selectedAdminPostImageFiles = [];
  const postImageFiles = document.getElementById("postImageFiles");
  if (postImageFiles) postImageFiles.value = "";
  renderAdminPostImagePreview();
  fillPostAuthorSelect(post?.author_id || post?.authorId || "", getPostAuthorName(post || {}));
  document.getElementById("postModal")?.classList.add("open");
}

function closePostModal() {
  document.getElementById("postModal")?.classList.remove("open");
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
    const uploadedImageUrls = await uploadAdminPostImages(selectedAdminPostImageFiles);
    const payload = {
      title: document.getElementById("postTitle").value.trim(),
      month: Number(document.getElementById("postMonth").value || new Date().getMonth() + 1),
      status: document.getElementById("postStatus").value,
      festival: document.getElementById("postFestival").value.trim(),
      province: document.getElementById("postProvince").value.trim(),
      tourKeywords: document.getElementById("postTourKeywords").value.trim(),
      summary: stripPostSourceLines(document.getElementById("postSummary").value).trim(),
      content: stripPostSourceLines(document.getElementById("postContent").value).trim(),
      imageUrls: [...postImageUrlsFromTextarea(), ...uploadedImageUrls],
      authorId,
      authorName
    };
    const response = await authenticatedFetch(id ? `/api/admin/posts/${encodeURIComponent(id)}` : "/api/admin/posts", {
      method: id ? "PUT" : "POST",
      body: JSON.stringify(payload)
    });
    const result = await readJson(response);
    showToast(result.message || "Đã lưu bài viết");
    closePostModal();
    await Promise.all([loadPosts(), loadAdminDashboard()]);
  } catch (error) {
    showToast(error.message);
  }
}

async function deletePost(id) {
  if (!confirm("Xóa bài viết này?")) return;
  try {
    const response = await authenticatedFetch(`/api/admin/posts/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
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
      if (activePanel) activePanel.style.display = "block";
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

  setupAdminSearchBox("scheduleSearchInput", "clearScheduleSearch", (value) => {
    scheduleSearchQuery = value;
  }, renderSchedules);

  setupAdminSearchBox("planStatusSearchInput", "clearPlanStatusSearch", (value) => {
    planStatusSearchQuery = value;
  }, renderPlanStatusOptions);

  setupAdminSearchBox("provinceTagSearchInput", "clearProvinceTagSearch", (value) => {
    provinceTagSearchQuery = value;
  }, renderProvinceTags);

  setupAdminSearchBox("postSearchInput", "clearAdminPostSearch", (value) => {
    postSearchQuery = value;
  }, renderPosts);
}

function openAiAvatarModal() {
  document.getElementById("aiAvatarModal")?.classList.add("open");
}

function closeAiAvatarModal() {
  document.getElementById("aiAvatarModal")?.classList.remove("open");
}

function chooseAiAvatar(assistant) {
  selectedAiAvatarAssistant = assistant === "travelwai" ? "travelwai" : "travelwinne";
  document.getElementById("aiAvatarFile")?.click();
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

async function uploadAiAvatar(file) {
  if (!file) return;
  const button = document.getElementById("changeAiAvatarButton");
  const originalText = button?.textContent || "Thay đổi avatar";
  const formData = await buildOptimizedImageFormData(file, "avatar");

  try {
    if (button) {
      button.disabled = true;
      button.textContent = "Đang tải...";
    }
    const response = await authenticatedFetch(`/api/admin/ai-avatar/${encodeURIComponent(selectedAiAvatarAssistant)}`, {
      method: "POST",
      body: formData
    });
    const result = await readJson(response);
    const avatarVersion = String(Date.now());
    localStorage.setItem("travelwaiAiAvatarVersion", avatarVersion);
    window.dispatchEvent(new StorageEvent("storage", {
      key: "travelwaiAiAvatarVersion",
      newValue: avatarVersion
    }));
    closeAiAvatarModal();
    showToast(result.message || "Đã cập nhật avatar AI");
  } catch (error) {
    showToast(error.message || "Không thể cập nhật avatar AI");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
    const input = document.getElementById("aiAvatarFile");
    if (input) input.value = "";
  }
}

function chooseSiteBackground(theme) {
  selectedSiteBackgroundTheme = theme === "dark" ? "dark" : "light";
  document.getElementById("siteBackgroundFile")?.click();
}

async function uploadSiteBackground(file) {
  if (!file) return;
  const button = document.getElementById("changeAiAvatarButton");
  const originalText = button?.textContent || "Thay đổi avatar";
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
    const result = await readJson(response);
    const version = String(Date.now());
    localStorage.setItem("travelwaiBackgroundVersion", version);
    closeAiAvatarModal();
    showToast(result.message || "Đã cập nhật ảnh nền");
    setTimeout(() => window.location.reload(), 450);
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

function setupAiAvatarUpload() {
  const button = document.getElementById("changeAiAvatarButton");
  const input = document.getElementById("aiAvatarFile");
  const backgroundInput = document.getElementById("siteBackgroundFile");
  button?.addEventListener("click", openAiAvatarModal);
  input?.addEventListener("change", () => uploadAiAvatar(input.files?.[0]));
  backgroundInput?.addEventListener("change", () => uploadSiteBackground(backgroundInput.files?.[0]));
}

document.addEventListener("DOMContentLoaded", () => {
  if (document.body.dataset.page !== "admin") return;
  setupAdminTabs();
  setupAdminSearch();
  setupAiAvatarUpload();
  document.getElementById("accountForm")?.addEventListener("submit", submitAccountForm);
  document.getElementById("planStatusOptionForm")?.addEventListener("submit", submitPlanStatusOptionForm);
  document.getElementById("provinceTagForm")?.addEventListener("submit", submitProvinceTagForm);
  document.getElementById("travelTagForm")?.addEventListener("submit", submitTravelTagForm);
  document.getElementById("postForm")?.addEventListener("submit", submitPostForm);
  document.getElementById("postAiButton")?.addEventListener("click", generateAdminPostContentFromFestival);
  document.getElementById("postImageUrls")?.addEventListener("input", renderAdminPostImagePreview);
  document.getElementById("postImageUploadButton")?.addEventListener("click", () => document.getElementById("postImageFiles")?.click());
  document.getElementById("postImageFiles")?.addEventListener("change", (event) => {
    selectedAdminPostImageFiles = Array.from(event.target.files || []);
    renderAdminPostImagePreview();
  });
  loadAdminPage();
});

window.loadAdminPage = loadAdminPage;
window.loadAdminDashboard = loadAdminDashboard;
window.openAccountModal = openAccountModal;
window.closeAccountModal = closeAccountModal;
window.openAiAvatarModal = openAiAvatarModal;
window.closeAiAvatarModal = closeAiAvatarModal;
window.chooseAiAvatar = chooseAiAvatar;
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

window.loadPosts = loadPosts;
window.renderPosts = renderPosts;
window.openPostModal = openPostModal;
window.closePostModal = closePostModal;
window.deletePost = deletePost;
