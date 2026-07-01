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
let selectedAiAvatarAssistant = "travelwai";
let selectedSiteBackgroundTheme = "light";
let salesLevelSettings = [
  { level: 1, commissionPercent: 8, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 2, commissionPercent: 12, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 3, commissionPercent: 15, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 4, commissionPercent: 18, offerDiscountPercent: 0, servicePercent: 0 },
  { level: 5, commissionPercent: 20, offerDiscountPercent: 0, servicePercent: 0 }
];
let adminAnalyticsData = null;
let adminAnalyticsYearMonths = [];
let accountPlanSettings = [
  { role: "Free", name: "Free", price: "0đ", subtitle: "Dùng thử cơ bản", note: "Miễn phí", cta: "Bắt đầu miễn phí", requiresPayment: false, benefits: ["Xem bản đồ Việt Nam, bài viết và tour du lịch", "Nhắn tin thường và xem thông báo", "Không dùng AI tạo bài viết", "Không lập lịch trình", "Không dùng ưu đãi bài viết", "Chatbot AI 3 câu hỏi trong 5 phút"] },
  { role: "VIP", name: "VIP", price: "59.000đ", subtitle: "Có AI và lịch trình", note: "Theo tháng", cta: "Nâng cấp VIP", requiresPayment: true, benefits: ["AI tạo bài viết", "Lập lịch trình", "Không dùng ưu đãi bài viết", "Chatbot AI 10 câu hỏi trong 5 phút"] },
  { role: "Premium", name: "Premium", price: "129.000đ", subtitle: "Không giới hạn", note: "Đầy đủ", cta: "Nâng cấp Premium", requiresPayment: true, benefits: ["Đầy đủ tính năng", "Ưu đãi bài viết", "Chatbot AI không giới hạn"] },
  { role: "Sales", name: "Sales", price: "Đăng ký", subtitle: "Bán tour và nhận hoa hồng", note: "Thu phí đăng ký", cta: "Đăng ký Sales", requiresPayment: true, benefits: ["Quản lý tour đã tạo", "Xem đơn bán tour", "Nhận hoa hồng theo cấp"] },
  { role: "Business", name: "Business", price: "Đăng ký", subtitle: "Đối tác tour và dịch vụ", note: "Thu phí đăng ký", cta: "Đăng ký Business", requiresPayment: true, benefits: ["Quản lý tour Business", "Xem doanh thu Business", "Tính phí dịch vụ theo cấp"] }
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
    for (const prefix of ["Admin-", "Business-"]) {
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

async function loadAdminPage() {
  updateAdminPageRoleLinks();
  await Promise.all([loadSalesLevelSettings(true), loadAccountPlanSettings(true)]);
  await loadAccounts();
  await Promise.all([loadAdminDashboard(), loadAdminAnalytics(true), loadTours(), loadSchedules(), loadPlanStatusOptions(), loadProvinceTags(), loadPosts()]);
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

async function loadAdminAnalytics(silent = false) {
  try {
    const response = await authenticatedFetch("/api/admin/analytics");
    const result = await readJson(response);
    adminAnalyticsData = result.data || null;
    renderAdminAnalytics();
  } catch (error) {
    console.error(error);
    if (!silent) showToast(error.message || "Không tải được thống kê");
    renderEmptyAdminAnalytics();
  }
}

function renderAdminAnalytics() {
  const data = adminAnalyticsData || {};
  renderAdminAnalyticsSnapshot("Month", data.month || {});
  renderAdminAnalyticsYearMonths(data.year_months || data.yearMonths || []);
  const summary = document.getElementById("adminAnalyticsSummary");
  if (summary) summary.textContent = "Bấm AI thống kê để tạo nhận xét.";
}

function renderEmptyAdminAnalytics() {
  renderAdminAnalyticsSnapshot("Month", {});
  renderAdminAnalyticsYearMonths([]);
  const summary = document.getElementById("adminAnalyticsSummary");
  if (summary) summary.textContent = "Chưa có dữ liệu thống kê.";
}

function renderAdminAnalyticsSnapshot(prefix, stats) {
  setText(`analytics${prefix}TopProvince`, formatAnalyticsMetric(stats.top_province || stats.topProvince));
  setText(`analytics${prefix}BudgetRange`, formatAnalyticsMetric(stats.budget_range || stats.budgetRange));
  setText(`analytics${prefix}TopTour`, formatAnalyticsMetric(stats.top_tour || stats.topTour));
  setText(`analytics${prefix}GroupSize`, formatAnalyticsMetric(stats.group_size || stats.groupSize));
  setText(`analytics${prefix}TopPost`, formatAnalyticsMetric(stats.top_post || stats.topPost));
}

function renderAdminAnalyticsYearMonths(months) {
  const body = document.getElementById("analyticsYearMonthBody");
  if (!body) return;
  const list = Array.isArray(months) ? months : [];
  adminAnalyticsYearMonths = list;
  if (!list.length) {
    body.innerHTML = `<tr><td colspan="6" class="empty-line">Chưa có dữ liệu thống kê.</td></tr>`;
    return;
  }
  body.innerHTML = list.map((item, index) => {
    const label = escapeHtml(item.label || `Tháng ${item.month || ""}`.trim());
    const cell = (kind, metric) => `<td class="admin-analytics-year-cell" data-analytics-detail="${kind}" data-analytics-month-index="${index}" role="button" tabindex="0">${escapeHtml(formatAnalyticsMetric(metric))}</td>`;
    return `<tr>
      <td><strong>${label}</strong></td>
      ${cell("province", item.top_province || item.topProvince)}
      ${cell("budget", item.budget_range || item.budgetRange)}
      ${cell("tour", item.top_tour || item.topTour)}
      ${cell("group", item.group_size || item.groupSize)}
      ${cell("post", item.top_post || item.topPost)}
    </tr>`;
  }).join("");
}

function formatAnalyticsMetric(metric) {
  const label = String(metric?.label || "Chưa có dữ liệu").trim() || "Chưa có dữ liệu";
  const count = Number(metric?.count || 0);
  if (!count || label === "Chưa có dữ liệu") return label;
  return `${label} (${count} lượt)`;
}

function getAdminAnalyticsDetails(kind, stats) {
  const source = stats || adminAnalyticsData?.month || {};
  const details = source.details || source.detail || {};
  if (kind === "province") {
    return {
      title: "5 tỉnh được tìm kiếm nhiều nhất",
      rows: details.top_provinces || details.topProvinces || []
    };
  }
  if (kind === "budget") {
    return {
      title: "Ngân sách phổ biến",
      rows: details.budget_ranges || details.budgetRanges || []
    };
  }
  if (kind === "tour") {
    return {
      title: "5 tour được mua nhiều nhất",
      rows: details.top_tours || details.topTours || []
    };
  }
  if (kind === "post") {
    return {
      title: "5 bài viết được xem nhiều nhất",
      rows: details.top_posts || details.topPosts || []
    };
  }
  return {
    title: "Du lịch theo nhóm",
    rows: details.group_sizes || details.groupSizes || []
  };
}

function openAdminAnalyticsDetailModal(kind, stats) {
  const modal = document.getElementById("adminAnalyticsDetailModal");
  const title = document.getElementById("adminAnalyticsDetailTitle");
  const body = document.getElementById("adminAnalyticsDetailBody");
  if (!modal || !body) return;
  const detail = getAdminAnalyticsDetails(kind, stats);
  if (title) title.textContent = detail.title;
  const rows = Array.isArray(detail.rows) ? detail.rows : [];
  if (!rows.length) {
    body.innerHTML = `<div class="empty-line">Chưa có dữ liệu.</div>`;
  } else {
    body.innerHTML = rows.map((item) => {
      const label = String(item?.label || "Chưa có dữ liệu").trim() || "Chưa có dữ liệu";
      const count = Number(item?.count || 0);
      return `<div class="admin-analytics-detail-item">${escapeHtml(label)} <strong>(${count} lượt)</strong></div>`;
    }).join("");
  }
  modal.classList.add("open");
}

function closeAdminAnalyticsDetailModal() {
  document.getElementById("adminAnalyticsDetailModal")?.classList.remove("open");
}

function setupAdminAnalyticsCards() {
  document.querySelectorAll(".admin-analytics-card[data-analytics-detail]").forEach((card) => {
    const open = () => openAdminAnalyticsDetailModal(card.dataset.analyticsDetail || "province");
    card.addEventListener("click", open);
    card.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        open();
      }
    });
  });

  const yearBody = document.getElementById("analyticsYearMonthBody");
  if (yearBody && yearBody.dataset.analyticsClickBound !== "1") {
    yearBody.dataset.analyticsClickBound = "1";
    const openYearDetail = (target) => {
      const cell = target?.closest?.(".admin-analytics-year-cell[data-analytics-detail][data-analytics-month-index]");
      if (!cell) return;
      const index = Number(cell.dataset.analyticsMonthIndex || -1);
      const stats = adminAnalyticsYearMonths[index] || null;
      openAdminAnalyticsDetailModal(cell.dataset.analyticsDetail || "province", stats);
    };
    yearBody.addEventListener("click", (event) => openYearDetail(event.target));
    yearBody.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ") return;
      const cell = event.target?.closest?.(".admin-analytics-year-cell[data-analytics-detail][data-analytics-month-index]");
      if (!cell) return;
      event.preventDefault();
      openYearDetail(cell);
    });
  }
}

const ADMIN_ANALYTICS_BUDGET_ORDER = ["1.000.000 - 3.000.000", "3.000.000 - 5.000.000", "5.000.000 - 10.000.000"];
const ADMIN_ANALYTICS_GROUP_ORDER = ["1 đến 2 người", "3 đến 5 người", "5 đến 10 người"];

function getAdminAnalyticsRows(stats, snakeKey, camelKey) {
  const details = stats?.details || stats?.detail || {};
  const rows = details?.[snakeKey] || details?.[camelKey] || [];
  return Array.isArray(rows) ? rows : [];
}

function aggregateAdminAnalyticsRows(months, snakeKey, camelKey, take = 3, fixedOrder = null) {
  const counts = new Map();
  if (Array.isArray(fixedOrder)) fixedOrder.forEach(label => counts.set(label, 0));
  (Array.isArray(months) ? months : []).forEach(month => {
    getAdminAnalyticsRows(month, snakeKey, camelKey).forEach(item => {
      const label = String(item?.label || "").trim();
      if (!label || label === "Chưa có dữ liệu") return;
      const count = Math.max(0, Number(item?.count || 0));
      counts.set(label, (counts.get(label) || 0) + count);
    });
  });

  let rows = Array.from(counts.entries()).map(([label, count]) => ({ label, count }));
  if (Array.isArray(fixedOrder)) {
    rows = rows.sort((a, b) => {
      const diff = Number(b.count || 0) - Number(a.count || 0);
      if (diff) return diff;
      return fixedOrder.indexOf(a.label) - fixedOrder.indexOf(b.label);
    });
  } else {
    rows = rows
      .filter(item => Number(item.count || 0) > 0)
      .sort((a, b) => Number(b.count || 0) - Number(a.count || 0) || a.label.localeCompare(b.label, "vi"));
  }
  return rows.slice(0, take);
}

function formatAdminAnalyticsExample(item) {
  if (!item || !item.label || !Number(item.count || 0)) return "";
  return `${item.label} (${Number(item.count || 0)} lượt)`;
}

function getAdminAnalyticsExampleList(items) {
  return (Array.isArray(items) ? items : [])
    .filter(item => Number(item?.count || 0) > 0)
    .slice(0, 3)
    .map(formatAdminAnalyticsExample)
    .filter(Boolean);
}

function renderAdminAnalyticsInsight(title, text, items) {
  const examples = getAdminAnalyticsExampleList(items);
  if (!examples.length) return "";
  return `<p><strong>${escapeHtml(title)}:</strong> ${escapeHtml(text)} <span>${examples.map(escapeHtml).join("; ")}.</span></p>`;
}

function showAdminAnalyticsAiSummary() {
  const summary = document.getElementById("adminAnalyticsSummary");
  if (!summary) return;
  const months = adminAnalyticsData?.year_months || adminAnalyticsData?.yearMonths || [];
  const year = new Date().getFullYear();
  const rows = [
    {
      title: "Tỉnh được tìm nhiều nhất",
      text: "dựa trên lượt mở chi tiết tỉnh, bấm Hỏi AI trong tỉnh và câu hỏi AI có nhắc đến tỉnh/thành. Ba mục dưới đây là các tỉnh có dữ liệu cao nhất trong năm.",
      items: aggregateAdminAnalyticsRows(months, "top_provinces", "topProvinces", 3)
    },
    {
      title: "Ngân sách phổ biến",
      text: "dựa trên ngân sách trong kế hoạch và đơn tour đã ghi nhận. Hệ thống chỉ hiển thị các khoảng có phát sinh lượt chọn.",
      items: aggregateAdminAnalyticsRows(months, "budget_ranges", "budgetRanges", 3, ADMIN_ANALYTICS_BUDGET_ORDER)
    },
    {
      title: "Loại tour đặt nhiều nhất",
      text: "dựa trên đơn tour có trong hệ thống. Nếu tour chưa có đơn thì mục này sẽ không tự tạo ví dụ.",
      items: aggregateAdminAnalyticsRows(months, "top_tours", "topTours", 3)
    },
    {
      title: "Du lịch theo nhóm",
      text: "dựa trên số người trong kế hoạch và đơn tour. Dữ liệu cho biết người dùng thường đi theo nhóm nhỏ, nhóm vừa hay nhóm đông.",
      items: aggregateAdminAnalyticsRows(months, "group_sizes", "groupSizes", 3, ADMIN_ANALYTICS_GROUP_ORDER)
    },
    {
      title: "Bài viết được xem nhiều nhất",
      text: "dựa trên lượt bấm Xem bài viết. Ba mục dưới đây là các bài viết được xem nhiều nhất nếu đã có lượt xem.",
      items: aggregateAdminAnalyticsRows(months, "top_posts", "topPosts", 3)
    }
  ];

  const hasData = rows.some(row => getAdminAnalyticsExampleList(row.items).length > 0);
  if (!hasData) {
    summary.textContent = `Năm ${year} chưa có dữ liệu thống kê đủ để tạo nhận xét.`;
    return;
  }

  const insightHtml = rows
    .map(row => renderAdminAnalyticsInsight(row.title, row.text, row.items))
    .filter(Boolean)
    .join("");

  summary.innerHTML = `<div class="admin-analytics-ai-title">AI thống kê theo năm ${year}</div>
    <div class="admin-analytics-ai-text-list">
      ${insightHtml}
    </div>`;
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
    const result = await readJson(response);
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
    const result = await readJson(response);
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
  if (role === "company") return "Business";
  if (role === "sales" || role === "tour sales" || role === "toursales") return "Sales";
  if (role === "business") return "Business";
  if (role === "vip") return "VIP";
  if (role === "premium") return "Premium";
  return "Free";
}

function normalizeAccountPlanSettings(rows) {
  const source = Array.isArray(rows) ? rows : [];
  return accountPlanSettings.map(fallback => {
    const current = source.find(item => normalizeAccountPlanRole(item?.role) === fallback.role) || {};
    const benefits = Array.isArray(current.benefits) ? current.benefits : fallback.benefits;
    return {
      role: fallback.role,
      name: current.name || fallback.name,
      price: current.price || fallback.price,
      subtitle: current.subtitle || fallback.subtitle,
      note: current.note || fallback.note,
      cta: current.cta || fallback.cta,
      requiresPayment: Boolean(current.requiresPayment ?? current.requires_payment ?? fallback.requiresPayment),
      benefits: benefits.map(item => String(item || "").trim()).filter(Boolean)
    };
  });
}

async function loadAccountPlanSettings(silent = false) {
  try {
    const response = await fetch('/api/account-plans', { cache: 'no-store' });
    const result = await readJson(response);
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
      <td><input id="accountPlanPrice${plan.role}" value="${escapeAttr(plan.price)}" /></td>
      <td><input id="accountPlanSubtitle${plan.role}" value="${escapeAttr(plan.subtitle)}" /></td>
      <td><textarea id="accountPlanBenefits${plan.role}" rows="4">${escapeHtml(plan.benefits.join('\n'))}</textarea></td>
      <td><select id="accountPlanPayment${plan.role}"><option value="true" ${plan.requiresPayment ? 'selected' : ''}>Có</option><option value="false" ${!plan.requiresPayment ? 'selected' : ''}>Không</option></select></td>
    </tr>
  `).join('');
}

async function submitAccountPlanSettingsForm(event) {
  event.preventDefault();
  const plans = accountPlanSettings.map(plan => ({
    role: plan.role,
    name: document.getElementById(`accountPlanName${plan.role}`)?.value || plan.name,
    price: document.getElementById(`accountPlanPrice${plan.role}`)?.value.trim() || plan.price,
    subtitle: document.getElementById(`accountPlanSubtitle${plan.role}`)?.value.trim() || plan.subtitle,
    note: document.getElementById(`accountPlanNote${plan.role}`)?.value || plan.note,
    cta: document.getElementById(`accountPlanCta${plan.role}`)?.value || plan.cta,
    requiresPayment: document.getElementById(`accountPlanPayment${plan.role}`)?.value === 'true',
    benefits: (document.getElementById(`accountPlanBenefits${plan.role}`)?.value || '')
      .split(/\n+/)
      .map(item => item.trim())
      .filter(Boolean)
  }));
  try {
    const response = await authenticatedFetch('/api/admin/account-plans', {
      method: 'PUT',
      body: JSON.stringify({ plans })
    });
    const result = await readJson(response);
    accountPlanSettings = normalizeAccountPlanSettings(result.data || plans);
    renderAccountPlanSettingsForm();
    if (window.TravelwAIPricingPopup?.reload) window.TravelwAIPricingPopup.reload();
    showToast(result.message || 'Đã lưu bảng giá');
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

function getAccountSalesLevel(account) {
  return getAccountCommissionLevel(account);
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
        <td><strong>${escapeHtml(account.username || "Người dùng")}</strong><br><small>${escapeHtml(account.email || "")}</small></td>
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
  if (value === "company" || value === "business") return "Business";
  if (value === "admin") return "Admin";
  if (value === "vip") return "VIP";
  if (value === "premium") return "Premium";
  return "Free";
}

function roleBadge(role) {
  const normalized = normalizeAccountRole(role);
  if (normalized === "Admin") return `<span class="badge badge-admin">Admin</span>`;
  if (normalized === "Sales") return `<span class="badge badge-sales">Sales</span>`;
  if (normalized === "Business") return `<span class="badge badge-sales">Business</span>`;
  if (normalized === "VIP") return `<span class="badge badge-user">VIP</span>`;
  if (normalized === "Premium") return `<span class="badge badge-user">Premium</span>`;
  if (normalized === "Free") return `<span class="badge badge-user">Free</span>`;
  return `<span class="badge badge-user">Free</span>`;
}

function renderAccountRole(account) {
  const role = normalizeAccountRole(account?.role || "Free");
  if (role === "Sales") return `${roleBadge(role)}<br><small>Cấp hoa hồng ${getAccountCommissionLevel(account)} - Hoa hồng ${getAccountCommissionPercent(account)}%</small>`;
  if (role === "Business") return `${roleBadge(role)}<br><small>Cấp dịch vụ ${getAccountServiceLevel(account)} - Dịch vụ ${getAccountServicePercent(account)}%</small>`;
  return roleBadge(role);
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
  document.getElementById("accountRole").value = normalizeAccountRole(account.role || "Free");
  document.getElementById("accountLocked").checked = !!(account.is_locked || account.isLocked);

  const protectedAdmin = account.is_protected || account.isProtected;
  document.getElementById("accountRole").disabled = !!protectedAdmin;
  [commissionInput, commissionLevel, offerInput, offerLevel, serviceInput, serviceLevel].forEach(field => {
    if (field) field.disabled = !!protectedAdmin;
  });
  document.getElementById("accountLocked").disabled = !!protectedAdmin;
  syncAccountLevelFields(false);
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
  const disableCompanyFields = role !== "Business" || roleDisabled;
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
  if (applySelected && role === "Business") {
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
  const payload = {
    username: document.getElementById("accountUsername").value.trim(),
    role: document.getElementById("accountRole").value,
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
    const result = await readJson(response);
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
    const result = await readJson(response);
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
    const result = await readJson(response);
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
  if (!await window.TravelwAIConfirm(`Xoá tag "${tagName}"?`)) return;

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
    if (!content) throw new Error("Không tìm thấy nội dung phù hợp.");

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

    showToast(result.message || "Đã điền nội dung bài viết.");
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
  if (!await window.TravelwAIConfirm("Xóa bài viết này?")) return;
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
  selectedAiAvatarAssistant = "travelwai";
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
  setupAdminAnalyticsCards();
  setupAiAvatarUpload();
  document.getElementById("accountForm")?.addEventListener("submit", submitAccountForm);
  document.getElementById("accountRole")?.addEventListener("change", () => syncAccountLevelFields(false));
  document.getElementById("accountSalesLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("accountOfferLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("accountServiceLevel")?.addEventListener("change", () => syncAccountLevelFields(true));
  document.getElementById("salesLevelSettingsForm")?.addEventListener("submit", submitSalesLevelSettingsForm);
  document.getElementById("accountPlanSettingsForm")?.addEventListener("submit", submitAccountPlanSettingsForm);
  document.getElementById("planStatusOptionForm")?.addEventListener("submit", submitPlanStatusOptionForm);
  document.getElementById("provinceTagForm")?.addEventListener("submit", submitProvinceTagForm);
  document.getElementById("travelTagForm")?.addEventListener("submit", submitTravelTagForm);
  document.getElementById("postForm")?.addEventListener("submit", submitPostForm);
  document.getElementById("postAiButton")?.addEventListener("click", generateAdminPostContentFromFestival);
  document.getElementById("adminAnalyticsAiButton")?.addEventListener("click", showAdminAnalyticsAiSummary);
  document.getElementById("adminAnalyticsRefreshButton")?.addEventListener("click", () => loadAdminAnalytics(false));
  document.getElementById("postImageUrls")?.addEventListener("input", renderAdminPostImagePreview);
  document.getElementById("postImageUploadButton")?.addEventListener("click", () => document.getElementById("postImageFiles")?.click());
  document.getElementById("postImageFiles")?.addEventListener("change", (event) => {
    selectedAdminPostImageFiles = Array.from(event.target.files || []);
    renderAdminPostImagePreview();
  });
  updateAdminPageRoleLinks();
  loadAdminPage();
});

window.loadAdminPage = loadAdminPage;
window.loadAdminDashboard = loadAdminDashboard;
window.loadAdminAnalytics = loadAdminAnalytics;
window.closeAdminAnalyticsDetailModal = closeAdminAnalyticsDetailModal;
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
