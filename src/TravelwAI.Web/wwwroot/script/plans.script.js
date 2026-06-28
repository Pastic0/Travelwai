const PLAN_API_BASE = "/api";
let allPlans = [];
let visiblePlans = [];
let allUsersForPlan = [];
let selectedInviteUsers = [];
let planUserSearchTimer = null;
let planStatusOptions = [];
let provinceTravelTags = [];
let activeStatusKey = "";
let currentPlanProfile = null;
let currentUserActivePlan = null;

const PLAN_STATUS_COLORS = {
  binh_thuong: "#e5e7eb",
  di_bien: "#0ea5e9",
  len_nui: "#22c55e",
  di_tich_lich_su: "#f97316",
  nghi_duong: "#a855f7",
  tuan_trang_mat: "#ec4899",
  team_building: "#14b8a6",
  giai_tri: "#eab308",
};

const PLAN_TAG_COLORS = {
  bien: "#0ea5e9",
  nui: "#22c55e",
  di_tich_lich_su: "#f97316",
  tho_mong: "#ec4899",
  khu_vui_choi: "#eab308",
};

function getPlanToken() {
  return localStorage.getItem("idToken") || "";
}

function authHeaders(extra = {}) {
  return {
    Authorization: `Bearer ${getPlanToken()}`,
    ...extra,
  };
}

document.addEventListener("DOMContentLoaded", async function () {
  if (typeof isAuthenticated !== "function" || !isAuthenticated()) {
    window.location.href = "/login";
    return;
  }

  bindPlanEvents();
  setPlanDateLimits();
  await Promise.all([loadPlanUsers(), loadCurrentPlanProfile(), loadPlans()]);
});

function bindPlanEvents() {
  document.getElementById("openPlanModalBtn")?.addEventListener("click", () => openPlanModal());
  document.getElementById("planForm")?.addEventListener("submit", handlePlanSubmit);
  document.getElementById("planFilterSelect")?.addEventListener("change", applyPlanFilter);
  document.getElementById("planStatusFilterSelect")?.addEventListener("change", applyPlanFilter);

  const avatarButton = document.getElementById("planStatusAvatarBtn");
  if (avatarButton) {
    avatarButton.addEventListener("click", (event) => {
      event.stopPropagation();
      togglePlanStatusDropdown();
    });
  }

  document.addEventListener("click", (event) => {
    const wrap = document.querySelector(".plan-status-avatar-wrap");
    if (wrap && !wrap.contains(event.target)) closePlanStatusDropdown();
  });

  const destinationStatus = document.getElementById("planDestinationStatus");
  if (destinationStatus) destinationStatus.addEventListener("change", () => updatePlanProvinceOptions(destinationStatus.value));

  const planUserSearchInput = document.getElementById("planUserSearchInput");
  if (planUserSearchInput) {
    planUserSearchInput.addEventListener("input", handlePlanUserSearch);
    planUserSearchInput.addEventListener("blur", () => setTimeout(hidePlanUserSearchResults, 180));
  }

  const planModal = document.getElementById("planModal");
  if (planModal) {
    planModal.addEventListener("click", (event) => {
      if (event.target === planModal) closePlanModal();
    });
  }

  document.querySelectorAll("#planModal .close").forEach((button) => button.addEventListener("click", closePlanModal));
}

function setPlanDateLimits() {
  const start = document.getElementById("planStartDate");
  const end = document.getElementById("planEndDate");
  if (!start || !end) return;

  const today = new Date().toISOString().split("T")[0];
  start.min = today;
  end.min = today;

  start.addEventListener("change", () => {
    end.min = start.value || today;
    if (end.value && start.value && end.value < start.value) end.value = start.value;
  });

  end.addEventListener("change", () => {
    if (start.value && end.value && end.value < start.value) start.value = end.value;
  });
}

async function loadCurrentPlanProfile() {
  try {
    const response = await fetch(`${PLAN_API_BASE}/profile`, {
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    currentPlanProfile = result.user || result.data || null;
  } catch (error) {
    console.warn("Không tải được ảnh đại diện:", error);
    currentPlanProfile = null;
  }
  updatePlanAvatar();
}

async function loadPlans() {
  const grid = document.getElementById("plansGrid");
  if (grid) grid.innerHTML = '<div class="loading-message">Đang tải kế hoạch...</div>';

  try {
    const response = await fetch(`${PLAN_API_BASE}/plans`, {
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không tải được kế hoạch");

    allPlans = Array.isArray(result.data?.plans) ? result.data.plans : [];
    setPlanCatalog(result.data?.status_options, result.data?.province_tags);

    const currentStatus = result.data?.current_status || {};
    activeStatusKey = currentStatus.status_key || currentStatus.destination_key || "";

    updatePlanStatusBubble();
    updateCurrentUserActivePlan();
    applyPlanFilter();
    updatePlanCounters();
  } catch (error) {
    console.error(error);
    if (grid) grid.innerHTML = `<div class="empty-state"><h3>Lỗi tải kế hoạch</h3><p>${escapeHtml(error.message)}</p></div>`;
  }
}

function setPlanCatalog(statusOptions, provinceTags) {
  if (Array.isArray(statusOptions) && statusOptions.length) planStatusOptions = statusOptions;
  if (Array.isArray(provinceTags) && provinceTags.length) provinceTravelTags = provinceTags;

  renderStatusSelect("planDestinationStatus", "Chọn trạng thái");
  renderStatusFilterSelect();
  renderPlanStatusChoices();
  updatePlanStatusBubble();
}

function renderStatusSelect(id, placeholder) {
  const select = document.getElementById(id);
  if (!select) return;
  const current = select.value;
  select.innerHTML = `<option value="">${escapeHtml(placeholder)}</option>` + planStatusOptions
    .map((status) => `<option value="${escapeAttr(status.key || status.id || "")}">${escapeHtml(status.label || status.key || "Trạng thái")}</option>`)
    .join("");
  if (current) select.value = current;
}

function renderStatusFilterSelect() {
  const select = document.getElementById("planStatusFilterSelect");
  if (!select) return;
  const current = select.value || "all";
  select.innerHTML = '<option value="all">Tất cả trạng thái</option>' + planStatusOptions
    .map((status) => `<option value="${escapeAttr(status.key || status.id || "")}">${escapeHtml(status.label || status.key || "Trạng thái")}</option>`)
    .join("");
  select.value = Array.from(select.options).some(option => option.value === current) ? current : "all";
}

function renderPlanStatusChoices() {
  const container = document.getElementById("planStatusChoiceList");
  if (!container) return;

  if (!planStatusOptions.length) {
    container.innerHTML = '<div class="loading-message">Chưa có trạng thái</div>';
    return;
  }

  container.innerHTML = planStatusOptions
    .map((status) => {
      const key = String(status.key || status.id || "");
      const label = escapeHtml(status.label || key || "Trạng thái");
      const active = key === activeStatusKey ? " active" : "";
      const accent = getStatusAccent(status);
      return `
        <button class="plan-status-choice${active}" type="button" role="option" aria-label="${label}" title="${label}" data-key="${escapeAttr(key)}" data-label="${label}" aria-selected="${active ? "true" : "false"}" style="--plan-accent:${accent}" onclick="savePlanStatus('${escapeAttr(key)}')">
          <span class="plan-status-choice-dot" aria-hidden="true"></span>
          <span class="plan-status-choice-text"><strong>${label}</strong></span>
        </button>`;
    })
    .join("");
}
function togglePlanStatusDropdown() {
  const dropdown = document.getElementById("planStatusDropdown");
  const button = document.getElementById("planStatusAvatarBtn");
  if (!dropdown) return;
  const willOpen = dropdown.hasAttribute("hidden");
  dropdown.toggleAttribute("hidden", !willOpen);
  button?.setAttribute("aria-expanded", willOpen ? "true" : "false");
}

function closePlanStatusDropdown() {
  const dropdown = document.getElementById("planStatusDropdown");
  const button = document.getElementById("planStatusAvatarBtn");
  if (!dropdown) return;
  dropdown.setAttribute("hidden", "");
  button?.setAttribute("aria-expanded", "false");
}

function updatePlanAvatar() {
  const img = document.getElementById("planStatusAvatarImg");
  if (!img) return;
  const src = resolvePlanAvatarUrl(currentPlanProfile?.profilePic || currentPlanProfile?.photoURL || currentPlanProfile?.avatar || "");
  img.src = src || "/logo/profile-icon-white.webp";
}

function resolvePlanAvatarUrl(value) {
  const src = String(value || "").trim();
  if (!src) return "";
  if (src.startsWith("http") || src.startsWith("data:")) return src;
  if (src.startsWith("/")) return src;
  return `/${src.replace(/^\/+/, "")}`;
}

function updatePlanStatusBubble() {
  const bubble = document.getElementById("activePlanStatusBubble");
  const label = document.getElementById("activePlanStatusLabel");
  const dot = document.getElementById("planStatusAvatarDot");
  const status = findStatusOption(activeStatusKey);
  const accent = status ? getStatusAccent(status) : "#94a3b8";

  if (label) label.textContent = status ? (status.label || activeStatusKey) : "Chọn trạng thái";
  if (bubble) {
    bubble.style.setProperty("--plan-accent", accent);
    bubble.classList.toggle("is-empty", !status);
  }
  if (dot) dot.style.setProperty("--plan-accent", accent);
  renderPlanStatusChoices();
}

function updatePlanProvinceOptions(statusKey) {
  const select = document.getElementById("planProvinceName");
  if (!select) return;

  const status = findStatusOption(statusKey);
  if (!status) {
    select.innerHTML = '<option value="">Chọn trạng thái trước</option>';
    return;
  }

  const matched = provinceTravelTags.filter((province) => provinceMatchesStatus(province, status));

  select.innerHTML = '<option value="">Chọn tỉnh/thành</option>' + matched
    .map((province) => `<option value="${escapeAttr(province.name || province.province_name || "")}">${escapeHtml(province.name || province.province_name || "Tỉnh thành")} - ${escapeHtml((province.tags || []).join(", "))}</option>`)
    .join("");
}

function findStatusOption(statusKey) {
  return planStatusOptions.find((item) => String(item.key || item.id || "") === String(statusKey || ""));
}

function provinceMatchesStatus(province, status) {
  const provinceTags = new Set((province.tags || []).map(normalizeSearchKey));
  const required = (status.tags || []).map(normalizeSearchKey).filter(Boolean);
  if (!required.length) return true;
  if (status.match_all || status.match_all_tags) return required.every((tag) => provinceTags.has(tag));
  return required.some((tag) => provinceTags.has(tag));
}

async function loadPlanUsers() {
  try {
    const response = await fetch(`${PLAN_API_BASE}/users`, {
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    allUsersForPlan = Array.isArray(result.data) ? result.data : [];
  } catch (error) {
    console.warn("Không tải được danh sách người dùng để mời:", error);
    allUsersForPlan = [];
  }
}

async function savePlanStatus(statusKeyFromButton) {
  const statusKey = statusKeyFromButton || "";
  if (!statusKey) {
    showPlanToast("Bạn chưa chọn trạng thái muốn đi.", "error");
    return;
  }

  try {
    const response = await fetch(`${PLAN_API_BASE}/plans/status`, {
      method: "POST",
      headers: authHeaders({ "Content-Type": "application/json" }),
      body: JSON.stringify({ StatusKey: statusKey }),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không lưu được trạng thái");

    activeStatusKey = statusKey;
    const filter = document.getElementById("planStatusFilterSelect");
    if (filter) filter.value = statusKey;
    updatePlanStatusBubble();
    closePlanStatusDropdown();
    showPlanToast("Đã đổi trạng thái muốn đi.", "success");
    await loadPlans();
  } catch (error) {
    showPlanToast(error.message, "error");
  }
}

function applyPlanFilter() {
  const filter = document.getElementById("planFilterSelect")?.value || "all";
  const statusFilter = document.getElementById("planStatusFilterSelect")?.value || "all";
  visiblePlans = allPlans.filter((plan) => {
    if (statusFilter !== "all" && String(plan.plan_status_key || plan.destination_key || "") !== statusFilter) return false;
    if (filter === "owned") return !!plan.is_owner;
    if (filter === "joined") return !!plan.is_member && !plan.is_owner;
    if (filter === "invited") return !!plan.is_invited;
    if (filter === "ready") return plan.status === "ready" || plan.status === "group_created";
    return true;
  });
  renderPlans();
}

function updateCurrentUserActivePlan() {
  currentUserActivePlan = allPlans.find((plan) => {
    const status = String(plan.status || "forming").toLowerCase();
    if (status === "cancelled" || status === "expired") return false;
    return !!plan.is_owner || !!plan.is_member;
  }) || null;
}

function updatePlanCounters() {
  setText("totalPlansCount", allPlans.length);
  setText("ownedPlansCount", allPlans.filter((plan) => plan.is_owner).length);
  setText("readyPlansCount", allPlans.filter((plan) => plan.status === "ready" || plan.status === "group_created").length);
}

function renderPlans() {
  const grid = document.getElementById("plansGrid");
  const empty = document.getElementById("plansEmptyState");
  if (!grid || !empty) return;

  if (!visiblePlans.length) {
    grid.innerHTML = "";
    empty.style.display = "block";
    return;
  }

  empty.style.display = "none";
  grid.innerHTML = visiblePlans.map(renderPlanCard).join("");
}

function renderPlanCard(plan) {
  const title = escapeHtml(plan.title || "Kế hoạch du lịch");
  const destination = escapeHtml(plan.destination_display || plan.province_name || plan.destination_status || "Chưa có điểm đến");
  const status = String(plan.status || "forming");
  const statusLabel = getPlanStatusLabel(status);
  const start = formatDate(plan.start_date);
  const end = formatDate(plan.end_date);
  const memberCount = Number(plan.member_count || 0);
  const target = Number(plan.target_people || 0);
  const progress = Math.max(0, Math.min(100, Number(plan.progress_percent || 0)));
  const ownerName = escapeHtml(plan.owner?.username || plan.owner_name || "Người lập kế hoạch");
  const members = Array.isArray(plan.members) ? plan.members : [];
  const tags = Array.isArray(plan.tags) ? plan.tags.slice(0, 6) : [];
  const travelStatus = findStatusOption(plan.plan_status_key || plan.destination_key || "");

  const memberChips = members.length
    ? members.map((member) => `<span class="member-chip">${escapeHtml(member.username || member.name || "Người dùng")}</span>`).join("")
    : '<span class="member-chip">Chưa có thành viên</span>';
  const tagChips = tags.length ? `<div class="plan-tags">${tags.map(tag => `<span class="plan-tag-chip" style="${getTagAccentStyle(tag)}">${escapeHtml(tag)}</span>`).join("")}</div>` : "";
  const travelStatusChip = travelStatus
    ? `<span class="plan-travel-status-chip" style="--plan-accent:${getStatusAccent(travelStatus)}"><span></span>${escapeHtml(travelStatus.label || travelStatus.key || "Trạng thái")}</span>`
    : "";

  return `
    <article class="schedule-card plan-card">
      <div class="plan-card-header">
        <div>
          <h3>${title}</h3>
          <p>${destination}</p>
        </div>
        <span class="plan-badge ${escapeAttr(status)}">${statusLabel}</span>
      </div>

      <div class="plan-card-status-row">${travelStatusChip}</div>

      <div class="plan-meta">
        <span>👤 Người lập: ${ownerName}</span>
        <span>📅 ${start} - ${end}</span>
        <span>🎯 Cần ${target} người</span>
      </div>

      ${tagChips}

      <div class="plan-progress-wrap" aria-label="Tiến độ đủ người">
        <div class="plan-progress-top">
          <span>Tiến độ lập kế hoạch</span>
          <span>${memberCount}/${target} người</span>
        </div>
        <div class="plan-progress-bar">
          <div class="plan-progress-fill" style="width:${progress}%">${progress}%</div>
        </div>
      </div>

      <div class="plan-members">${memberChips}</div>
      <div class="plan-actions">${renderPlanActions(plan)}</div>
    </article>`;
}

function renderPlanActions(plan) {
  const id = escapeAttr(plan.id || "");
  const actions = [];

  if (plan.can_join) actions.push(`<button class="btn-primary" type="button" onclick="joinPlan('${id}')">Tham gia</button>`);
  if (plan.can_create_group) actions.push(`<button class="btn-primary" type="button" onclick="createPlanGroup('${id}')">Tạo nhóm + lịch trình</button>`);
  if (plan.conversation_id) actions.push(`<button class="btn-secondary" type="button" onclick="window.location.href='/messaging'">Vào nhóm</button>`);
  if (plan.schedule_id && plan.is_owner && !plan.can_create_group) actions.push(`<button class="btn-secondary" type="button" onclick="window.location.href='/schedule'">Mở lịch trình</button>`);

  if (plan.is_owner && plan.status !== "cancelled" && plan.status !== "expired") {
    actions.push(`<button class="btn-danger" type="button" onclick="cancelPlan('${id}')">Hủy</button>`);
  }

  return actions.length ? actions.join("") : '<span class="form-help">Đang chờ thêm người tham gia.</span>';
}
function openPlanModal(prefillUser) {
  const modal = document.getElementById("planModal");
  if (!modal) return;

  updateCurrentUserActivePlan();
  if (currentUserActivePlan) {
    showPlanToast("Bạn đang có hoặc đang tham gia một kế hoạch. Hãy hủy kế hoạch hoặc chờ nhóm giải tán rồi mới lập kế hoạch mới.", "error");
    return;
  }

  resetPlanForm();
  const savedStatus = activeStatusKey || "";
  const destinationStatus = document.getElementById("planDestinationStatus");
  if (destinationStatus && savedStatus) {
    destinationStatus.value = savedStatus;
    updatePlanProvinceOptions(savedStatus);
  }

  if (prefillUser) addInviteUser(prefillUser);

  modal.classList.add("show");
  modal.style.display = "block";
  modal.setAttribute("aria-hidden", "false");
  document.body.classList.add("plan-modal-open");
}

function closePlanModal() {
  const modal = document.getElementById("planModal");
  if (!modal) return;
  modal.classList.remove("show");
  modal.style.display = "none";
  modal.setAttribute("aria-hidden", "true");
  document.body.classList.remove("plan-modal-open");
  hidePlanUserSearchResults();
}

function resetPlanForm() {
  document.getElementById("planForm")?.reset();
  selectedInviteUsers = [];
  renderSelectedInviteList();

  const today = new Date().toISOString().split("T")[0];
  const start = document.getElementById("planStartDate");
  const end = document.getElementById("planEndDate");
  if (start) start.value = today;
  if (end) end.value = today;
  const target = document.getElementById("planTargetPeople");
  if (target) target.value = "2";
  updatePlanProvinceOptions("");
}

async function handlePlanSubmit(event) {
  event.preventDefault();

  const statusKey = getValue("planDestinationStatus");
  const status = findStatusOption(statusKey);
  const payload = {
    Title: getValue("planTitle"),
    DestinationStatus: status?.label || statusKey,
    PlanStatusKey: statusKey,
    ProvinceName: getValue("planProvinceName"),
    Description: getValue("planDescription"),
    StartDate: getValue("planStartDate"),
    EndDate: getValue("planEndDate"),
    TargetPeople: parseInt(getValue("planTargetPeople"), 10) || 2,
    Budget: getValue("planBudget") ? Number(getValue("planBudget")) : null,
    Currency: getValue("planCurrency") || "VND",
    Tags: splitTags(getValue("planTags")),
    InviteUserIds: selectedInviteUsers.map((user) => String(user.id)).filter(Boolean),
  };

  if (!payload.Title || !payload.PlanStatusKey || !payload.ProvinceName || !payload.StartDate || !payload.EndDate) {
    showPlanToast("Bạn cần nhập đủ tên, trạng thái, tỉnh/thành và ngày đi.", "error");
    return;
  }

  try {
    setButtonLoading("savePlanBtn", true, "Đang lập...");
    const response = await fetch(`${PLAN_API_BASE}/plans`, {
      method: "POST",
      headers: authHeaders({ "Content-Type": "application/json" }),
      body: JSON.stringify(payload),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không lập được kế hoạch");

    closePlanModal();
    showPlanToast("Đã lập kế hoạch. Khi đủ người hệ thống sẽ tự tạo lịch trình.", "success");
    await loadPlans();
  } catch (error) {
    showPlanToast(error.message, "error");
  } finally {
    setButtonLoading("savePlanBtn", false);
  }
}

function handlePlanUserSearch() {
  const input = document.getElementById("planUserSearchInput");
  const query = normalizeSearchKey(input?.value || "");
  clearTimeout(planUserSearchTimer);

  if (!query) {
    hidePlanUserSearchResults();
    return;
  }

  planUserSearchTimer = setTimeout(() => {
    const selectedIds = new Set(selectedInviteUsers.map((user) => String(user.id)));
    const results = allUsersForPlan
      .filter((user) => !selectedIds.has(String(user.id)))
      .filter((user) => normalizeSearchKey(`${user.username || ""} ${user.name || ""} ${user.email || ""}`).includes(query))
      .slice(0, 8);

    renderPlanUserSearchResults(results);
  }, 220);
}

function renderPlanUserSearchResults(users) {
  const container = document.getElementById("planUserSearchResults");
  if (!container) return;

  if (!users.length) {
    container.innerHTML = '<div class="plan-search-result"><small>Không tìm thấy người</small></div>';
    container.style.display = "block";
    return;
  }

  container.innerHTML = users
    .map((user) => `
      <div class="plan-search-result" onclick="selectPlanUser('${escapeAttr(user.id || "")}')">
        <div>
          <strong>${escapeHtml(user.username || user.name || "Người dùng")}</strong>
          <small>${escapeHtml(user.email || "")}</small>
        </div>
        <span>Thêm</span>
      </div>`)
    .join("");
  container.style.display = "block";
}

function selectPlanUser(userId) {
  const user = allUsersForPlan.find((item) => String(item.id) === String(userId));
  if (!user) return;
  addInviteUser(user);
  const input = document.getElementById("planUserSearchInput");
  if (input) input.value = "";
  hidePlanUserSearchResults();
}

function addInviteUser(user) {
  if (!user || !user.id) return;
  if (selectedInviteUsers.some((item) => String(item.id) === String(user.id))) {
    showPlanToast("Người này đã nằm trong danh sách mời.", "error");
    return;
  }
  selectedInviteUsers.push(user);
  renderSelectedInviteList();
}

function removeInviteUser(userId) {
  selectedInviteUsers = selectedInviteUsers.filter((user) => String(user.id) !== String(userId));
  renderSelectedInviteList();
}

function renderSelectedInviteList() {
  const list = document.getElementById("selectedInviteList");
  if (!list) return;

  if (!selectedInviteUsers.length) {
    list.innerHTML = '<span class="form-help">Chưa chọn người để mời.</span>';
    return;
  }

  list.innerHTML = selectedInviteUsers
    .map((user) => `
      <span class="invited-chip">
        ${escapeHtml(user.username || user.name || user.email || "Người dùng")}
        <small>${escapeHtml(user.email || "")}</small>
        <button class="remove-invite-btn" type="button" onclick="removeInviteUser('${escapeAttr(user.id)}')">×</button>
      </span>`)
    .join("");
}

function hidePlanUserSearchResults() {
  const container = document.getElementById("planUserSearchResults");
  if (container) {
    container.innerHTML = "";
    container.style.display = "none";
  }
}

async function joinPlan(planId) {
  try {
    const response = await fetch(`${PLAN_API_BASE}/plans/${encodeURIComponent(planId)}/join`, {
      method: "POST",
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không thể tham gia kế hoạch");
    showPlanToast("Đã tham gia kế hoạch.", "success");
    await loadPlans();
  } catch (error) {
    showPlanToast(error.message, "error");
  }
}

async function createPlanGroup(planId) {
  try {
    const response = await fetch(`${PLAN_API_BASE}/plans/${encodeURIComponent(planId)}/create-group`, {
      method: "POST",
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không thể tạo nhóm");
    showPlanToast("Đã tạo nhóm và lịch trình theo kế hoạch.", "success");
    await loadPlans();
  } catch (error) {
    showPlanToast(error.message, "error");
  }
}

async function cancelPlan(planId) {
  if (!await window.TravelwAIConfirm("Hủy kế hoạch này? Sau khi hủy bạn mới có thể lập kế hoạch mới.")) return;

  try {
    const response = await fetch(`${PLAN_API_BASE}/plans/${encodeURIComponent(planId)}`, {
      method: "DELETE",
      headers: authHeaders({ "Content-Type": "application/json" }),
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(result.detail || result.message || "Không thể hủy kế hoạch");
    showPlanToast("Đã hủy kế hoạch.", "success");
    await loadPlans();
  } catch (error) {
    showPlanToast(error.message, "error");
  }
}

function getPlanStatusLabel(status) {
  const map = {
    forming: "Đang tìm người",
    ready: "Đã đủ người",
    group_created: "Đã tạo nhóm",
    cancelled: "Đã hủy",
    expired: "Đã giải tán",
  };
  return map[status] || status;
}

function getStatusAccent(status) {
  if (!status) return "#94a3b8";
  const key = String(status.key || status.id || "");
  const color = String(status.color || "").trim();
  if (/^#[0-9a-f]{6}$/i.test(color)) {
    if (key === "binh_thuong" && color.toLowerCase() === "#ffffff") return PLAN_STATUS_COLORS.binh_thuong;
    return color;
  }
  if (PLAN_STATUS_COLORS[key]) return PLAN_STATUS_COLORS[key];
  const tags = Array.isArray(status.tags) ? status.tags : [];
  if (tags.length) return getTagAccent(tags[0]);
  return "#6366f1";
}

function getTagAccent(tag) {
  const key = normalizeKeyForColor(tag);
  return PLAN_TAG_COLORS[key] || "#6366f1";
}

function getTagAccentStyle(tag) {
  return `--plan-accent:${getTagAccent(tag)}`;
}

function getStatusTagText(status) {
  const tags = Array.isArray(status?.tags) ? status.tags.join(", ") : "";
  return tags ? `Tag: ${tags}` : "Bấm để chọn trạng thái này";
}

function normalizeKeyForColor(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/_+/g, "_");
}

function splitTags(value) {
  return String(value || "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function getValue(id) {
  return document.getElementById(id)?.value.trim() || "";
}

function setText(id, value) {
  const element = document.getElementById(id);
  if (element) element.textContent = String(value);
}

function formatDate(value) {
  if (!value) return "Chưa đặt ngày";
  const parts = String(value).split("-");
  if (parts.length !== 3) return escapeHtml(value);
  return `${parts[2]}/${parts[1]}/${parts[0]}`;
}

function setButtonLoading(id, loading, text) {
  const button = document.getElementById(id);
  if (!button) return;
  if (loading) {
    button.dataset.originalText = button.textContent;
    button.textContent = text || "Đang xử lý...";
    button.disabled = true;
  } else {
    button.textContent = button.dataset.originalText || button.textContent;
    button.disabled = false;
  }
}

function normalizeSearchKey(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

function showPlanToast(message, type = "info") {
  const old = document.querySelector(".plan-toast");
  if (old) old.remove();

  const toast = document.createElement("div");
  toast.className = `plan-toast ${type}`;
  toast.textContent = message;
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), 3200);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function escapeAttr(value) {
  return escapeHtml(value).replace(/`/g, "&#096;");
}
