
const API_BASE = "/api";
let currentSchedules = [];
let editingScheduleId = null;
let searchTimeout = null;
let currentSearchResults = [];
let dailyPlans = {};
let user_friendList = [];
let sharedEmails = [];
let visibleSchedules = [];
let aiScheduleMessages = [];

document.addEventListener("DOMContentLoaded", async function () {

  if (typeof isAuthenticated !== "function") {
    console.error("Chưa tải được hàm xác thực. Chuyển về trang đăng nhập.");
    window.location.href = "/login";
    return;
  }

  if (!isAuthenticated()) {
    window.location.href = "/login";
    return;
  }

  initializeSchedulePage();
  get_user_friendList()
    .then((friends) => {
      user_friendList = normalizeFriendListResponse(friends);
    })
    .catch((error) => {
      console.warn("Không tải được danh sách bạn bè, vẫn cho phép dùng lịch trình:", error);
      user_friendList = { data: [] };
    });
});

function normalizeFriendListResponse(result) {
  if (Array.isArray(result)) return { data: result };
  if (result && Array.isArray(result.data)) return result;
  if (result && Array.isArray(result.friends)) return { ...result, data: result.friends };
  return { data: [] };
}

async function get_user_friendList() {
  const token = localStorage.getItem("idToken");
  const response = await fetch(`${API_BASE}/friend_requests`, {
    method: "GET",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
  });

  if (!response.ok) {
    throw new Error(`Không tải được danh sách bạn bè (${response.status})`);
  }

  return response.json();
}

let scheduleSearchDebounceTimeout;

async function searchUsersForSharingInPanel() {
  const emailInput = document.getElementById("emailInput");
  const query = emailInput.value.trim().toLowerCase();

  const resultsContainer = document.getElementById("userSearchResults");
  if (!resultsContainer) {
    console.error(
      "Không tìm thấy khung hiển thị kết quả tìm kiếm userSearchResults trong trang lịch trình."
    );
    return;
  }

  clearTimeout(scheduleSearchDebounceTimeout);

  if (query.length < 1) {

    hideSearchResults();
    return;
  }

  scheduleSearchDebounceTimeout = setTimeout(() => {

    if (
      typeof user_friendList === "undefined" ||
      typeof user_friendList.data === "undefined" ||
      !Array.isArray(user_friendList.data)
    ) {
      console.warn(
        "[Lịch trình] Danh sách bạn bè chưa sẵn sàng hoặc không đúng định dạng."
      );
      showNoResults(
        "Danh sách bạn bè đang tải hoặc chưa sẵn sàng. Vui lòng thử lại sau."
      );
      return;
    }

    if (user_friendList.data.length === 0) {
      showNoResults(
        "Bạn chưa có bạn bè để chia sẻ. Hãy thêm bạn trong mục Tin nhắn."
      );
      return;
    }

    const filteredFriends = user_friendList.data.filter((friend) => {

      return (
        friend.email &&
        typeof friend.email === "string" &&
        friend.email.toLowerCase().includes(query)
      );
    });

    if (filteredFriends.length > 0) {
      displaySearchResults(filteredFriends);
    } else {
      showNoResults("Không tìm thấy bạn bè phù hợp.");
    }
  }, 300);
}

function initializeSchedulePage() {
  setupEventListeners();
  loadUserSchedules();
}

function setupEventListeners() {

  const createScheduleBtn = document.getElementById("createScheduleBtn");
  if (createScheduleBtn) createScheduleBtn.onclick = openCreateModal;

  const aiCreateScheduleBtn = document.getElementById("aiCreateScheduleBtn");
  if (aiCreateScheduleBtn) aiCreateScheduleBtn.onclick = openAiCreateModal;

  const askScheduleAiBtn = document.getElementById("askScheduleAiBtn");
  if (askScheduleAiBtn) askScheduleAiBtn.addEventListener("click", sendScheduleAiPrompt);

  const scheduleAiPrompt = document.getElementById("scheduleAiPrompt");
  if (scheduleAiPrompt) {
    scheduleAiPrompt.addEventListener("keydown", function (event) {
      if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        sendScheduleAiPrompt();
      }
    });
  }

  const scheduleSearchInput = document.getElementById("scheduleSearchInput");
  const scheduleFilterSelect = document.getElementById("scheduleFilterSelect");
  if (scheduleSearchInput) scheduleSearchInput.addEventListener("input", applyScheduleFilters);
  if (scheduleFilterSelect) scheduleFilterSelect.addEventListener("change", applyScheduleFilters);

  document.querySelectorAll(".close").forEach((closeBtn) => {
    closeBtn.addEventListener("click", (e) => {
      if (e.target.closest("#scheduleModal")) {
        closeModal();
      } else if (e.target.closest("#detailModal")) {
        closeDetailModal();
      } else if (e.target.closest("#dayPlanModal")) {
        closeDayPlanModal();
      }
    });
  });

  const scheduleForm = document.getElementById("scheduleForm");
  if (scheduleForm) scheduleForm.addEventListener("submit", handleScheduleSubmit);

  const scheduleModal = document.getElementById("scheduleModal");
  if (scheduleModal) {
    scheduleModal.addEventListener("click", (event) => {
      if (event.target === scheduleModal) {
        closeModal();
      }
    });
  }

  const detailModal = document.getElementById("detailModal");
  if (detailModal) {
    detailModal.addEventListener("click", (event) => {
      if (event.target === detailModal) {
        closeDetailModal();
      }
    });
  }

  const dayPlanModal = document.getElementById("dayPlanModal");
  if (dayPlanModal) {
    dayPlanModal.addEventListener("click", (event) => {
      if (event.target === dayPlanModal) {
        closeDayPlanModal();
      }
    });
  }

  document.querySelectorAll('input[name="visibility"]').forEach((radio) => {
    radio.addEventListener("change", handleVisibilityChange);
  });

  const addEmailBtn = document.getElementById("addEmailBtn");
  if (addEmailBtn) addEmailBtn.addEventListener("click", addEmail);

  const emailInput = document.getElementById("emailInput");
  if (emailInput) {
    emailInput.addEventListener("keypress", function (e) {
      if (e.key === "Enter") {
        e.preventDefault();
        addEmail();
      }
    });

    emailInput.addEventListener("input", handleEmailSearch);
    emailInput.addEventListener("blur", function () {

      setTimeout(() => {
        hideSearchResults();
      }, 200);
    });
  }

  const startDateInput = document.getElementById("startDate");
  const endDateInput = document.getElementById("endDate");

  if (startDateInput && endDateInput) {
    startDateInput.addEventListener("change", function () {
      endDateInput.min = this.value;
      if (endDateInput.value && endDateInput.value < this.value) {
        endDateInput.value = this.value;
      }
      generateDailyPlansInterface();
    });

    endDateInput.addEventListener("change", function () {
      startDateInput.max = this.value;
      generateDailyPlansInterface();
    });

    const today = new Date().toISOString().split("T")[0];
    startDateInput.min = today;
    endDateInput.min = today;
  }

  document.addEventListener("click", function (e) {
    if (!e.target.closest(".email-search-wrapper")) {
      hideSearchResults();
    }
  });
}

async function loadUserSchedules() {
  try {
    const ownSchedulesResponse = await authenticatedFetch(`${API_BASE}/get_schedules`);
    let allSchedules = [];

    if (ownSchedulesResponse && ownSchedulesResponse.ok) {
      const ownResult = await ownSchedulesResponse.json();
      if (ownResult.success) {
        const ownSchedules = (ownResult.owned_data || []).map((schedule) => ({
          ...schedule,
          isOwner: true,
          shareType: "owned",
        }));
        const sharedSchedules = (ownResult.shared_data || []).map((schedule) => ({
          ...schedule,
          isOwner: false,
          shareType: "shared",
        }));
        allSchedules = [...ownSchedules, ...sharedSchedules];
      }
    }

    allSchedules.sort((a, b) => new Date(b.created_at || b.start_date || 0) - new Date(a.created_at || a.start_date || 0));
    currentSchedules = allSchedules;
    updateScheduleOverview(currentSchedules);
    applyScheduleFilters();
  } catch (error) {
    console.error("Lỗi tải lịch trình:", error);
    showError("Không tải được danh sách lịch trình. Vui lòng thử lại.");
    showEmptyState();
  }
}

function updateScheduleOverview(schedules) {
  const total = schedules.length;
  const owned = schedules.filter((s) => s.shareType === "owned").length;
  const shared = schedules.filter((s) => s.shareType === "shared").length;
  const setText = (id, value) => {
    const el = document.getElementById(id);
    if (el) el.textContent = value;
  };
  setText("totalSchedulesCount", total);
  setText("ownedSchedulesCount", owned);
  setText("sharedSchedulesCount", shared);
}

function applyScheduleFilters() {
  const query = (document.getElementById("scheduleSearchInput")?.value || "").toLowerCase().trim();
  const filter = document.getElementById("scheduleFilterSelect")?.value || "all";

  visibleSchedules = currentSchedules.filter((schedule) => {
    const matchesFilter = filter === "all" || schedule.shareType === filter;
    const haystack = [schedule.title, schedule.description, ...(schedule.tags || [])]
      .join(" ")
      .toLowerCase();
    const matchesQuery = !query || haystack.includes(query);
    return matchesFilter && matchesQuery;
  });

  displaySchedules(visibleSchedules);
}

function displaySchedules(schedules) {
  const grid = document.getElementById("schedulesGrid");
  const emptyState = document.getElementById("emptyState");

  if (schedules.length === 0) {
    grid.innerHTML = "";
    emptyState.style.display = "block";
    return;
  }

  emptyState.style.display = "none";

  grid.innerHTML = schedules
    .map((schedule) => {
      const dayCount = schedule.days?.length || calculateDayCount(schedule.start_date, schedule.end_date);
      const activityCount = (schedule.days || []).reduce((sum, day) => sum + (day.destinations?.length || 0), 0);
      const badge = schedule.shareType === "shared"
        ? '<span class="shared-badge">Được chia sẻ</span>'
        : '<span class="private-badge">Của tôi</span>';
      return `
        <div class="schedule-card ${schedule.shareType === "shared" ? "shared-schedule" : ""}" onclick="openScheduleDetail('${schedule.id}')">
          <div class="schedule-card-top">
            <h3>${escapeHtml(schedule.title || "Chưa đặt tên")}</h3>
            ${badge}
          </div>
          <div class="schedule-meta-grid">
            <span>Bắt đầu: ${formatDate(schedule.start_date)}</span>
            <span>Kết thúc: ${formatDate(schedule.end_date)}</span>
            <span>Số ngày: ${dayCount}</span>
            <span>Hoạt động: ${activityCount}</span>
          </div>
          <div class="budget">${schedule.budget ? formatCurrency(schedule.budget, schedule.currency) : "Chưa đặt ngân sách"}</div>
          <div class="tags">
            ${(schedule.tags || []).map((tag) => `<span class="tag">${escapeHtml(tag)}</span>`).join("")}
          </div>
        </div>
      `;
    })
    .join("");
}

function calculateDayCount(startDate, endDate) {
  if (!startDate || !endDate) return 0;
  const start = new Date(startDate);
  const end = new Date(endDate);
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) return 0;
  return Math.max(1, Math.ceil(Math.abs(end - start) / (1000 * 60 * 60 * 24)) + 1);
}

function showEmptyState() {
  document.getElementById("schedulesGrid").innerHTML = "";
  document.getElementById("emptyState").style.display = "block";
}

function openCreateModal() {
  editingScheduleId = null;
  document.getElementById("modalTitle").textContent = "Tạo lịch trình mới";
  document.getElementById("saveScheduleBtn").textContent = "Tạo lịch trình";
  clearForm();
  const modal = document.getElementById("scheduleModal");
  modal.style.display = "block";
  modal.setAttribute("aria-hidden", "false");
}

function openAiCreateModal() {
  openCreateModal();
  setTimeout(() => {
    const input = document.getElementById("scheduleAiPrompt");
    if (input) {
      input.focus();
      input.scrollIntoView({ behavior: "smooth", block: "center" });
    }
  }, 120);
}

function addScheduleAiMessage(role, content) {
  const container = document.getElementById("scheduleAiMessages");
  if (!container || !content) return;

  aiScheduleMessages.push({ role, content });
  const div = document.createElement("div");
  div.className = `ai-schedule-message ${role === "user" ? "user" : "assistant"}`;
  div.textContent = content;
  container.appendChild(div);
  container.scrollTop = container.scrollHeight;
}

function getScheduleAiHistoryForRequest() {
  return aiScheduleMessages.slice(-10).map((message) => ({
    role: message.role === "assistant" ? "assistant" : "user",
    content: message.content,
  }));
}

function collectCurrentScheduleContext() {
  return {
    title: document.getElementById("scheduleTitle")?.value?.trim() || "",
    description: document.getElementById("scheduleDescription")?.value?.trim() || "",
    start_date: document.getElementById("startDate")?.value || "",
    end_date: document.getElementById("endDate")?.value || "",
    budget: document.getElementById("budget")?.value || "",
    currency: document.getElementById("currency")?.value || "VND",
    tags: (document.getElementById("tags")?.value || "")
      .split(",")
      .map((tag) => tag.trim())
      .filter(Boolean),
    days: dailyPlans,
  };
}

async function sendScheduleAiPrompt() {
  const input = document.getElementById("scheduleAiPrompt");
  const button = document.getElementById("askScheduleAiBtn");
  const message = input?.value?.trim() || "";

  if (!message) {
    showError("Nhập yêu cầu để AI lập lịch trình trước.");
    return;
  }

  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
  if (!token) {
    showError("Không tìm thấy token đăng nhập.");
    window.location.href = "/login";
    return;
  }

  const historyBeforeSend = getScheduleAiHistoryForRequest();
  addScheduleAiMessage("user", message);
  input.value = "";

  try {
    if (button) {
      button.disabled = true;
      button.textContent = "Đang lập...";
    }

    const response = await fetch(`${API_BASE}/ai/schedule-plan`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({
        message,
        history: historyBeforeSend,
        currentSchedule: collectCurrentScheduleContext(),
      }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không gọi được AI lập lịch trình.");
    }

    const data = result.data || {};
    const status = String(data.status || "").toLowerCase();

    if (status === "needs_more_info") {
      const question = data.question || data.reply || "Bạn bổ sung thêm điểm đến, ngày đi và ngân sách giúp mình nhé.";
      addScheduleAiMessage("assistant", question);
      return;
    }

    if (data.patch) {
      applyScheduleAiPatch(data.patch);
      addScheduleAiMessage("assistant", data.reply || "Mình đã chỉnh đúng phần bạn yêu cầu. Bạn kiểm tra lại rồi bấm Tạo lịch trình để lưu.");
      showSuccess("AI đã chỉnh lịch trình theo yêu cầu.");
      return;
    }

    if (data.schedule) {
      applyAiScheduleDraft(data.schedule);
      addScheduleAiMessage("assistant", data.reply || "Mình đã tạo bản nháp lịch trình và điền vào form. Bạn kiểm tra lại rồi bấm Tạo lịch trình để lưu.");
      showSuccess("AI đã điền lịch trình vào form.");
      return;
    }

    addScheduleAiMessage("assistant", data.reply || data.question || "AI chưa tạo được lịch trình. Hãy mô tả rõ hơn điểm đến, ngày đi và sở thích.");
  } catch (error) {
    console.error("Lỗi AI lập lịch trình:", error);
    const messageText = error.message || "Không gọi được AI lập lịch trình.";
    addScheduleAiMessage("assistant", messageText);
    showError(messageText);
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = "Gửi AI";
    }
  }
}

function normalizeAiDate(value) {
  if (!value) return "";
  const text = String(value).trim();
  const match = text.match(/\d{4}-\d{2}-\d{2}/);
  if (match) return match[0];

  const parsed = new Date(text);
  if (!Number.isNaN(parsed.getTime())) {
    return parsed.toISOString().split("T")[0];
  }

  return "";
}

function addDaysToDate(dateText, daysToAdd) {
  const date = new Date(dateText);
  if (Number.isNaN(date.getTime())) return "";
  date.setDate(date.getDate() + daysToAdd);
  return date.toISOString().split("T")[0];
}

function applyAiScheduleDraft(schedule) {
  if (!schedule || typeof schedule !== "object") return;

  const title = schedule.title || "Lịch trình AI gợi ý";
  const description = schedule.description || "";
  const startDate = normalizeAiDate(schedule.start_date) || document.getElementById("startDate")?.value || "";
  let endDate = normalizeAiDate(schedule.end_date) || document.getElementById("endDate")?.value || "";

  const days = Array.isArray(schedule.days) ? schedule.days : [];
  if (!endDate && startDate && days.length > 0) {
    endDate = addDaysToDate(startDate, days.length - 1);
  }

  document.getElementById("scheduleTitle").value = title;
  document.getElementById("scheduleDescription").value = description;
  if (startDate) document.getElementById("startDate").value = startDate;
  if (endDate) document.getElementById("endDate").value = endDate;
  if (schedule.budget !== null && typeof schedule.budget !== "undefined") {
    document.getElementById("budget").value = Number(schedule.budget) || "";
  }
  document.getElementById("currency").value = schedule.currency || "VND";
  document.getElementById("tags").value = Array.isArray(schedule.tags) ? schedule.tags.join(", ") : "AI, du lịch";

  dailyPlans = buildDailyPlansFromAiSchedule(schedule, startDate);
  generateDailyPlansInterface();
}

function applyScheduleAiPatch(patch) {
  if (!patch || typeof patch !== "object") return;

  const startInput = document.getElementById("startDate");
  const endInput = document.getElementById("endDate");
  const oldStartDate = startInput?.value || "";
  let shouldRefreshDays = false;

  if (typeof patch.title === "string") {
    document.getElementById("scheduleTitle").value = patch.title;
  }

  if (typeof patch.description === "string") {
    document.getElementById("scheduleDescription").value = patch.description;
  }

  if (Array.isArray(patch.tags)) {
    document.getElementById("tags").value = patch.tags.join(", ");
  }

  const newStartDate = normalizeAiDate(patch.start_date);
  if (newStartDate && startInput) {
    if (oldStartDate && oldStartDate !== newStartDate) {
      const offset = getDateOffsetInDays(oldStartDate, newStartDate);
      if (offset !== 0) {
        dailyPlans = shiftDailyPlanDates(dailyPlans, offset);
        shouldRefreshDays = true;
      }
    }
    startInput.value = newStartDate;
    if (endInput) endInput.min = newStartDate;
  }

  const durationDays = Number(patch.duration_days || patch.durationDays || 0);
  const patchEndDate = normalizeAiDate(patch.end_date);
  if (durationDays > 0 && startInput?.value && endInput) {
    endInput.value = addDaysToDate(startInput.value, durationDays - 1);
    shouldRefreshDays = true;
  } else if (patchEndDate && endInput) {
    endInput.value = patchEndDate;
    shouldRefreshDays = true;
  }

  if (typeof patch.budget !== "undefined" && patch.budget !== null) {
    const budgetValue = Number(patch.budget);
    document.getElementById("budget").value = Number.isFinite(budgetValue) ? budgetValue : "";
  }

  if (typeof patch.currency === "string" && patch.currency.trim()) {
    const currencySelect = document.getElementById("currency");
    const currency = patch.currency.trim().toUpperCase();
    if (currencySelect && [...currencySelect.options].some((option) => option.value === currency)) {
      currencySelect.value = currency;
    }
  }

  if (shouldRefreshDays) {
    generateDailyPlansInterface();
  }
}

function getDateOffsetInDays(fromDate, toDate) {
  const from = new Date(fromDate);
  const to = new Date(toDate);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) return 0;
  return Math.round((to.getTime() - from.getTime()) / 86400000);
}

function shiftDailyPlanDates(plan, offsetDays) {
  if (!plan || typeof plan !== "object" || !offsetDays) return plan || {};
  const shifted = {};
  Object.keys(plan).forEach((dateKey) => {
    const newDateKey = addDaysToDate(dateKey, offsetDays);
    if (newDateKey) shifted[newDateKey] = plan[dateKey];
  });
  return shifted;
}

function buildDailyPlansFromAiSchedule(schedule, startDate) {
  const plan = {};
  const days = Array.isArray(schedule.days) ? schedule.days : [];

  days.forEach((day, dayIndex) => {
    const date = normalizeAiDate(day.date) || (startDate ? addDaysToDate(startDate, dayIndex) : "");
    if (!date) return;

    const phaseMap = new Map();
    const destinations = Array.isArray(day.destinations) ? day.destinations : [];

    destinations.forEach((destination) => {
      const phaseName = destination.time_phase || destination.timePhase || "Hoạt động trong ngày";
      const timeRange = destination.time_range || destination.timeRange || "";
      const phaseKey = `${phaseName}|${timeRange}`;

      if (!phaseMap.has(phaseKey)) {
        phaseMap.set(phaseKey, {
          name: phaseName,
          timeRange,
          activities: [],
        });
      }

      const durationText = destination.estimated_duration ? `\nThời lượng dự kiến: ${destination.estimated_duration}` : "";
      phaseMap.get(phaseKey).activities.push({
        name: destination.name || "Hoạt động du lịch",
        notes: `${destination.description || ""}${durationText}`.trim(),
      });
    });

    plan[date] = { timePhases: Array.from(phaseMap.values()) };
  });

  return plan;
}

function openEditModal(schedule) {

  editingScheduleId = schedule.id;
  document.getElementById("modalTitle").textContent = "Sửa lịch trình";
  document.getElementById("saveScheduleBtn").textContent = "Cập nhật lịch trình";

  document.getElementById("scheduleTitle").value = schedule.title;
  document.getElementById("scheduleDescription").value =
    schedule.description || "";
  document.getElementById("startDate").value =
    schedule.start_date.split("T")[0];
  document.getElementById("endDate").value = schedule.end_date.split("T")[0];
  document.getElementById("budget").value = schedule.budget || "";
  document.getElementById("currency").value = schedule.currency || "VND";
  document.getElementById("tags").value = (schedule.tags || []).join(", ");

  sharedEmails = schedule.shared_emails || schedule.shared_with_emails || [];
  updateEmailList();

  if (sharedEmails.length > 0) {
    document.getElementById("visibilityShared").checked = true;
  } else {
    document.getElementById("visibilityPrivate").checked = true;
  }

  dailyPlans = {};
  if (schedule.days && schedule.days.length > 0) {
    schedule.days.forEach((day) => {
      const dateKey = day.date.split("T")[0];
      dailyPlans[dateKey] = {
        timePhases: [],
      };

      if (day.destinations && day.destinations.length > 0) {

        const phaseGroups = {};
        day.destinations.forEach((dest) => {
          const phaseName = dest.time_phase || "Hoạt động";
          if (!phaseGroups[phaseName]) {
            phaseGroups[phaseName] = {
              name: phaseName,
              timeRange: dest.time_range || "",
              activities: [],
            };
          }
          phaseGroups[phaseName].activities.push({
            name: dest.name || "",
            notes: dest.description || "",
          });
        });

        Object.values(phaseGroups).forEach((phase) => {
          dailyPlans[dateKey].timePhases.push(phase);
        });
      }
    });
  }

  handleVisibilityChange();

  const scheduleModalElement = document.getElementById("scheduleModal");
  scheduleModalElement.style.display = "block";
  scheduleModalElement.setAttribute("aria-hidden", "false");

  setTimeout(() => {
    generateDailyPlansInterface();
  }, 100);
}

async function openScheduleDetail(scheduleId) {
  try {
    const response = await authenticatedFetch(`${API_BASE}/schedules/${scheduleId}`);

    if (response && response.ok) {
      const result = await response.json();
      if (result.success) {
        const schedule = result.data;

        const scheduleWithMeta = currentSchedules.find(
          (s) => s.id === scheduleId
        );
        const isOwner = scheduleWithMeta ? scheduleWithMeta.isOwner : true;

        displayScheduleDetail(schedule, isOwner);

        const editBtn = document.getElementById("editScheduleBtn");
        const deleteBtn = document.getElementById("deleteScheduleBtn");

        if (isOwner) {
          editBtn.style.display = "inline-block";
          deleteBtn.style.display = "inline-block";

          editBtn.onclick = () => {
            closeDetailModal();
            openEditModal(schedule);
          };

          deleteBtn.onclick = () => {
            deleteSchedule(schedule.id, schedule.title);
          };
        } else {
          editBtn.style.display = "none";
          deleteBtn.style.display = "none";
        }

        const detailModalElement = document.getElementById("detailModal");
        detailModalElement.style.display = "block";
        detailModalElement.setAttribute("aria-hidden", "false");
      } else {
        throw new Error(result.message || "Không tải được chi tiết lịch trình");
      }
    } else {
      throw new Error("Không lấy được chi tiết lịch trình");
    }
  } catch (error) {
    console.error("Lỗi tải chi tiết lịch trình:", error);
    showError("Không tải được chi tiết lịch trình. Vui lòng thử lại.");
  }
}

function displayScheduleDetail(schedule, isOwner = true) {
  document.getElementById("detailTitle").textContent = schedule.title;

  const detailsContainer = document.getElementById("scheduleDetails");
  const scheduleSharedEmails = schedule.shared_emails || schedule.shared_with_emails || [];

  let totalDaysText = "Không rõ";
  if (schedule.start_date && schedule.end_date) {
    const start = new Date(schedule.start_date);
    const end = new Date(schedule.end_date);
    const diffTime = Math.abs(end - start);
    const diffDays = Math.ceil(diffTime / (1000 * 60 * 60 * 24)) + 1;
    totalDaysText = `${diffDays} ngày`;
  }

  detailsContainer.innerHTML = `
    <div class="schedule-detail-content-enhanced">
      ${
        !isOwner
          ? `
        <div class="shared-schedule-notice">
          <span>Lịch trình này được chia sẻ với bạn bởi ${escapeHtml(
            schedule.owner_email || "chủ lịch trình"
          )}.</span>
        </div>
      `
          : ""
      }

      <div class="detail-header-section">
        <h2>${escapeHtml(schedule.title)}</h2>
      </div>

      <div class="detail-section detail-main-info">
        <h3>Thông tin chính</h3>
        <div class="detail-grid-enhanced">
          <div class="detail-item-enhanced">
            <label>Ngày bắt đầu:</label>
            <span>${formatDate(schedule.start_date)}</span>
          </div>
          <div class="detail-item-enhanced">
            <label>Ngày kết thúc:</label>
            <span>${formatDate(schedule.end_date)}</span>
          </div>
          <div class="detail-item-enhanced">
            <label>Thời lượng:</label>
            <span>${totalDaysText}</span>
          </div>
          <div class="detail-item-enhanced">
            <label>Ngân sách:</label>
            <span>${
              schedule.budget && schedule.budget > 0
                ? formatCurrency(schedule.budget, schedule.currency)
                : "Chưa đặt"
            }</span>
          </div>
          <div class="detail-item-enhanced">
            <label>Quyền xem:</label>
            <span>${
              scheduleSharedEmails.length > 0
                ? '<span class="visibility-badge shared">Đã chia sẻ</span>'
                : '<span class="visibility-badge private">Riêng tư</span>'
            }</span>
          </div>
          ${
            scheduleSharedEmails.length > 0
              ? `<div class="detail-item-enhanced full-width">
                  <label>Chia sẻ với:</label>
                  <span class="shared-emails-list">${scheduleSharedEmails
                    .map(
                      (email) =>
                        `<span class="email-badge">${escapeHtml(email)}</span>`
                    )
                    .join(" ")}</span>
                </div>`
              : ""
          }
          ${
            schedule.tags && schedule.tags.length > 0
              ? `<div class="detail-item-enhanced full-width">
                <label>Thẻ:</label>
                <span class="tags-list">${(schedule.tags || [])
                  .map(
                    (tag) => `<span class="tag-badge">${escapeHtml(tag)}</span>`
                  )
                  .join(" ")}</span>
              </div>`
              : ""
          }
        </div>
      </div>

      <div class="detail-section detail-days-container-enhanced">
        <h3>Lịch trình từng ngày</h3>
        ${
          schedule.days && schedule.days.length > 0
            ? schedule.days
                .sort((a, b) => a.day_number - b.day_number)
                .map(
                  (day, index) => `
            <div class="detail-day-card">
              <div class="day-card-header">
                <h4>Ngày ${day.day_number}</h4>
                <span class="day-card-date">${formatDate(day.date)}</span>
              </div>
              <div class="day-card-body">
                ${
                  day.notes
                    ? `<div class="day-card-meta notes"><p>${escapeHtml(
                        day.notes
                      )}</p></div>`
                    : ""
                }
                ${
                  day.accommodation
                    ? `<div class="day-card-meta accommodation"><label>Nơi ở:</label> <p>${escapeHtml(
                        day.accommodation
                      )}</p></div>`
                    : ""
                }
                ${
                  day.transportation
                    ? `<div class="day-card-meta transportation"><label>Di chuyển:</label> <p>${escapeHtml(
                        day.transportation
                      )}</p></div>`
                    : ""
                }
                ${
                  day.destinations && day.destinations.length > 0
                    ? `<div class="day-activities-detailed">
                        ${generateDayPlanDetailedHTML(day.destinations)}
                      </div>`
                    : '<div class="day-card-meta no-activities-day"><p>Chưa có hoạt động cụ thể cho ngày này.</p></div>'
                }
              </div>
            </div>
          `
                )
                .join("")
            : '<p class="no-days-detail">Chưa có kế hoạch từng ngày. Hãy sửa lịch trình để thêm chi tiết.</p>'
        }
      </div>
    </div>
  `;
}

async function handleScheduleSubmit(event) {
  event.preventDefault();

  const formattedDays = [];
  const scheduleStartDateValue = document.getElementById("startDate").value;

  if (!scheduleStartDateValue) {
    showError("Thiếu ngày bắt đầu. Vui lòng chọn ngày bắt đầu.");
    console.error("Thiếu ngày bắt đầu khi lưu lịch trình");
    return;
  }

  const sortedDates = Object.keys(dailyPlans).sort(
    (a, b) => new Date(a) - new Date(b)
  );

  sortedDates.forEach((date, index) => {
    const dayPlan = dailyPlans[date];
    const activities = [];

    if (dayPlan.timePhases && dayPlan.timePhases.length > 0) {
      dayPlan.timePhases.forEach((phase) => {
        if (phase.activities && phase.activities.length > 0) {
          phase.activities.forEach((activity) => {
            if (activity.name && activity.name.trim()) {
              activities.push({
                name: activity.name.trim(),
                description: activity.notes || "",
                time_phase: phase.name || "Khung giờ chưa đặt tên",
                time_range: phase.timeRange || "",
                estimated_duration: null,
              });
            }
          });
        }
      });
    }

    if (
      activities.length > 0 ||
      (dayPlan.timePhases && dayPlan.timePhases.length > 0)
    ) {
      const scheduleStartDate = new Date(scheduleStartDateValue);
      const currentDate = new Date(date);

      const utcScheduleStartDate = Date.UTC(
        scheduleStartDate.getFullYear(),
        scheduleStartDate.getMonth(),
        scheduleStartDate.getDate()
      );
      const utcCurrentDate = Date.UTC(
        currentDate.getFullYear(),
        currentDate.getMonth(),
        currentDate.getDate()
      );

      const diffDays = Math.floor(
        (utcCurrentDate - utcScheduleStartDate) / (1000 * 60 * 60 * 24)
      );
      const day_number = diffDays + 1;

      const dayObjectToPush = {
        day_number: day_number,
        date: date,
        destinations: activities,
        notes: "",
        accommodation: "",
        transportation: "",
      };

      formattedDays.push(dayObjectToPush);
    } else {
    }
  });

  const formData = {
    title: document.getElementById("scheduleTitle").value.trim(),
    description: document.getElementById("scheduleDescription").value.trim(),
    start_date: scheduleStartDateValue,
    end_date: document.getElementById("endDate").value,
    budget: parseFloat(document.getElementById("budget").value) || null,
    currency: document.getElementById("currency").value,
    old_schedule_id: editingScheduleId,
    tags: document
      .getElementById("tags")
      .value.split(",")
      .map((tag) => tag.trim())
      .filter((tag) => tag),
    shared_emails: sharedEmails,
    days: formattedDays,
  };

  try {
    const url = `${API_BASE}/schedules`;
    const response = await authenticatedFetch(url, {
      method: "POST",
      body: JSON.stringify(formData),
    });

    if (response && response.ok) {
      const result = await response.json();
      if (result.success) {
        showSuccess(
          editingScheduleId
            ? "Đã cập nhật lịch trình!"
            : "Đã tạo lịch trình!"
        );
        closeModal();
        loadUserSchedules();
      } else {
        console.error("Kết quả lỗi từ API:", result);
        throw new Error(
          result.message || "Không lưu được lịch trình do lỗi API"
        );
      }
    } else {
      const errorBody = response
        ? await response.text()
        : "Lỗi không xác định từ máy chủ";
      console.error(
        "Lỗi gửi yêu cầu. Mã trạng thái:",
        response ? response.status : "Không rõ",
        "Nội dung phản hồi:",
        errorBody
      );
      throw new Error(
        `Không lưu được lịch trình. Máy chủ phản hồi mã ${
          response ? response.status : "Không rõ"
        }. Xem console để biết chi tiết.`
      );
    }
  } catch (error) {
    console.error("Lỗi lưu lịch trình:", error);
    showError(`Không lưu được lịch trình. ${error.message}`);
  }
}

async function deleteSchedule(scheduleId, title) {
  if (
    !confirm(
      `Bạn có chắc muốn xóa "${title}" không? Hành động này không thể hoàn tác.`
    )
  ) {
    return;
  }

  try {
    const response = await authenticatedFetch(
      `${API_BASE}/schedules/${scheduleId}`,
      {
        method: "DELETE",
      }
    );

    if (response && response.ok) {
      const result = await response.json();

      if (result.success) {
        showSuccess("Đã xóa lịch trình!");
        closeDetailModal();
        loadUserSchedules();
      } else {
        throw new Error(result.message || "Không xóa được lịch trình");
      }
    } else {
      throw new Error("Không xóa được lịch trình");
    }
  } catch (error) {
    console.error("Lỗi xóa lịch trình:", error);
    showError("Không xóa được lịch trình. Vui lòng thử lại.");
  }
}

function closeModal() {
  const modal = document.getElementById("scheduleModal");
  modal.style.display = "none";
  modal.setAttribute("aria-hidden", "true");
  clearForm();
}

function closeDetailModal() {
  const modal = document.getElementById("detailModal");
  modal.style.display = "none";
  modal.setAttribute("aria-hidden", "true");
}

function resetScheduleAiPanel() {
  aiScheduleMessages = [];
  const container = document.getElementById("scheduleAiMessages");
  if (container) {
    container.innerHTML = `
      <div class="ai-schedule-message assistant">
        Ví dụ: "Đi Đà Lạt, 2 ngày, đi hôm nay".
      </div>
    `;
  }
  const input = document.getElementById("scheduleAiPrompt");
  if (input) input.value = "";
}

function clearForm() {
  document.getElementById("scheduleForm").reset();
  editingScheduleId = null;
  sharedEmails = [];
  dailyPlans = {};
  resetScheduleAiPanel();
  updateEmailList();

  const container = document.getElementById("dailyPlansContainer");
  container.innerHTML = `
    <div class="daily-plans-placeholder-redesigned">
      <div class="placeholder-content">
        <h3>Sẵn sàng lập kế hoạch?</h3>
      </div>
    </div>
  `;

}

function formatDate(dateString) {
  try {
    const date = new Date(dateString);
    return date.toLocaleDateString("vi-VN", {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  } catch (error) {
    return dateString;
  }
}

function formatCurrency(amount, currency) {
  try {
    return new Intl.NumberFormat("vi-VN", {
      style: "currency",
      currency: currency,
      minimumFractionDigits: 0,
    }).format(amount);
  } catch (error) {
    return `${amount} ${currency}`;
  }
}

function escapeHtml(unsafe) {
  if (!unsafe) return "";
  return unsafe
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function showSuccess(message) {

  const notification = document.createElement("div");
  notification.className = "notification success";
  notification.textContent = message;
  notification.style.cssText = `
    position: fixed;
    top: 20px;
    right: 20px;
    background: #4CAF50;
    color: white;
    padding: 15px 20px;
    border-radius: 5px;
    z-index: 10000;
    animation: slideIn 0.3s ease;
  `;

  document.body.appendChild(notification);

  setTimeout(() => {
    notification.remove();
  }, 3000);
}

function showError(message) {

  const notification = document.createElement("div");
  notification.className = "notification error";
  notification.textContent = message;
  notification.style.cssText = `
    position: fixed;
    top: 20px;
    right: 20px;
    background: #f44336;
    color: white;
    padding: 15px 20px;
    border-radius: 5px;
    z-index: 10000;
    animation: slideIn 0.3s ease;
  `;

  document.body.appendChild(notification);

  setTimeout(() => {
    notification.remove();
  }, 5000);
}

const style = document.createElement("style");
style.textContent = `
  @keyframes slideIn {
    from { transform: translateX(100%); opacity: 0; }
    to { transform: translateX(0); opacity: 1; }
  }

  .schedule-detail-content {
    line-height: 1.6;
  }

  .detail-section {
    margin-bottom: 30px;
  }

  .detail-section h3 {
    color: #333;
    border-bottom: 2px solid #eee;
    padding-bottom: 10px;
    margin-bottom: 20px;
  }

  .detail-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 15px;
  }

  .detail-item {
    display: flex;
    flex-direction: column;
  }

  .detail-item label {
    font-weight: 600;
    color: #555;
    margin-bottom: 5px;
  }

  .detail-item span {
    color: #333;
  }

  .days-container {
    display: flex;
    flex-direction: column;
    gap: 20px;
  }

  .day-item {
    border: 1px solid #eee;
    border-radius: 8px;
    padding: 15px;
    background: #f9f9f9;
  }

  .day-item h4 {
    margin: 0 0 10px 0;
    color: #333;
  }

  .destinations-list {
    margin: 10px 0;
    padding-left: 20px;
  }

  .destinations-list li {
    margin-bottom: 10px;
  }

  .no-destinations, .no-days {
    color: #666;
    font-style: italic;
  }

  .day-notes, .day-accommodation, .day-transportation {
    margin-top: 10px;
    padding-top: 10px;
    border-top: 1px solid #ddd;
    font-size: 14px;
  }

  @media (max-width: 768px) {
    .detail-grid {
      grid-template-columns: 1fr;
    }

    .form-row {
      grid-template-columns: 1fr !important;
    }
  }
`;
document.head.appendChild(style);

function handleVisibilityChange() {
  const selectedVisibility = document.querySelector(
    'input[name="visibility"]:checked'
  ).value;
  const emailSharingSection = document.getElementById("emailSharingSection");

  if (selectedVisibility === "shared") {
    emailSharingSection.style.display = "block";
  } else {
    emailSharingSection.style.display = "none";
  }
}

function addEmail() {
  const emailInput = document.getElementById("emailInput");
  const email = emailInput.value.trim();

  if (!email) return;

  const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
  if (!emailRegex.test(email)) {
    showError("Vui lòng nhập email hợp lệ");
    return;
  }

  if (sharedEmails.includes(email)) {
    showError("Email này đã được thêm");
    return;
  }

  sharedEmails.push(email);
  emailInput.value = "";
  updateEmailList();
}

function removeEmail(email) {
  sharedEmails = sharedEmails.filter((e) => e !== email);
  updateEmailList();
}

function updateEmailList() {
  const emailList = document.getElementById("emailList");
  emailList.innerHTML = "";

  sharedEmails.forEach((email) => {
    const emailTag = document.createElement("div");
    emailTag.className = "email-tag";
    emailTag.innerHTML = `
      ${escapeHtml(email)}
      <button type="button" class="remove-email" onclick="removeEmail(\'${escapeHtml(
        email
      )}\')" title="Xóa email">
        ×
      </button>
    `;
    emailList.appendChild(emailTag);
  });
}

function handleEmailSearch() {
  const emailInput = document.getElementById("emailInput");
  const query = emailInput.value.trim().toLowerCase();

  if (searchTimeout) {
    clearTimeout(searchTimeout);
  }

  if (query.length < 1) {
    hideSearchResults();
    return;
  }

  searchTimeout = setTimeout(() => {
    if (
      typeof user_friendList === "undefined" ||
      typeof user_friendList.data === "undefined" ||
      !Array.isArray(user_friendList.data)
    ) {
      console.warn(
        "[Lịch trình] Danh sách bạn bè chưa sẵn sàng hoặc không đúng định dạng."
      );
      showNoResults(
        "Danh sách bạn bè đang tải hoặc chưa sẵn sàng. Vui lòng thử lại sau."
      );
      return;
    }

    if (user_friendList.data.length === 0) {
      showNoResults("Bạn chưa có bạn bè để chia sẻ.");
      return;
    }

    const filteredFriends = user_friendList.data.filter((friend) => {
      const nameMatch =
        friend.username && friend.username.toLowerCase().includes(query);
      const emailMatch =
        friend.email && friend.email.toLowerCase().includes(query);
      return nameMatch || emailMatch;
    });

    if (filteredFriends.length > 0) {
      displaySearchResults(filteredFriends);
    } else {
      showNoResults("Không tìm thấy bạn bè phù hợp.");
    }
  }, 300);
}

function displaySearchResults(users) {
  const searchResultsContainer = document.getElementById("userSearchResults");
  searchResultsContainer.innerHTML = "";

  if (!users || users.length === 0) {
    showNoResults("Không tìm thấy bạn bè phù hợp.");
    return;
  }

  users.forEach((user) => {
    const userItem = document.createElement("div");
    userItem.className = "user-search-item";

    const avatar = document.createElement("div");
    avatar.className = "user-avatar";

    if (user.profilePic) {
      const img = document.createElement("img");
      let picUrl = user.profilePic;

      if (!picUrl.startsWith("http") && typeof API_BASE !== "undefined") {
        const base = API_BASE.endsWith("/api")
          ? API_BASE.substring(0, API_BASE.length - 4)
          : API_BASE;
        picUrl = base + (picUrl.startsWith("/") ? picUrl : "/" + picUrl);
      } else if (!picUrl.startsWith("http")) {
        picUrl = "/logo/travelwai-paper-plane.webp";
      }

      img.src = picUrl;
      img.alt = user.username ? user.username.charAt(0).toUpperCase() : "U";
      img.style.width = "100%";
      img.style.height = "100%";
      img.style.objectFit = "cover";
      img.onerror = () => {
        avatar.textContent = user.username
          ? user.username.charAt(0).toUpperCase()
          : user.email
          ? user.email.charAt(0).toUpperCase()
          : "U";
        img.remove();
      };
      avatar.innerHTML = "";
      avatar.appendChild(img);
    } else {
      avatar.textContent = user.username
        ? user.username.charAt(0).toUpperCase()
        : user.email
        ? user.email.charAt(0).toUpperCase()
        : "U";
    }

    const userInfo = document.createElement("div");
    userInfo.className = "user-info";

    const userNameDisplay = document.createElement("div");
    userNameDisplay.className = "user-name";
    userNameDisplay.textContent = user.username || "Người dùng không xác định";

    const userEmailDisplay = document.createElement("div");
    userEmailDisplay.className = "user-email";
    userEmailDisplay.textContent = user.email;

    userInfo.appendChild(userNameDisplay);
    userInfo.appendChild(userEmailDisplay);

    userItem.appendChild(avatar);
    userItem.appendChild(userInfo);

    userItem.addEventListener("click", () => {
      selectUser(user);
    });

    searchResultsContainer.appendChild(userItem);
  });

  searchResultsContainer.style.display = "block";
  searchResultsContainer.classList.add("show");
}

function showNoResults(message) {
  const searchResultsContainer = document.getElementById("userSearchResults");
  searchResultsContainer.innerHTML = `<div class="no-users-found">${message}</div>`;
  searchResultsContainer.style.display = "block";
  searchResultsContainer.classList.add("show");
}

function selectUser(user) {
  const emailInput = document.getElementById("emailInput");
  emailInput.value = user.email;
  hideSearchResults();

  addEmail();
}

function hideSearchResults() {
  const resultsContainer = document.getElementById("userSearchResults");
  if (resultsContainer) {
    resultsContainer.style.display = "none";
  }
}

function generateDailyPlansInterface() {
  const startDate = document.getElementById("startDate").value;
  const endDate = document.getElementById("endDate").value;
  const container = document.getElementById("dailyPlansContainer");

  if (!startDate || !endDate) {
    container.innerHTML = `
      <div class="daily-plans-placeholder-redesigned">
        <div class="placeholder-content">
            <h3>Sẵn sàng lập kế hoạch?</h3>
          </div>
      </div>
    `;
    return;
  }

  const days = getDaysBetweenDates(startDate, endDate);

  if (days.length > 30) {
    container.innerHTML = `
      <div class="daily-plans-placeholder-redesigned">
        <div class="placeholder-content">
          <h3>Lịch trình quá dài</h3>
        </div>
      </div>
    `;
    return;
  }

  container.innerHTML = days
    .map((date, index) => createDayPlanCardRedesigned(date, index + 1))
    .join("");

}

function getDaysBetweenDates(startDate, endDate) {
  const dates = [];
  const start = new Date(startDate);
  const end = new Date(endDate);

  for (
    let date = new Date(start);
    date <= end;
    date.setDate(date.getDate() + 1)
  ) {
    dates.push(new Date(date).toISOString().split("T")[0]);
  }

  return dates;
}

function createDayPlanCardRedesigned(date, dayNumber) {
  const dateObj = new Date(date);
  const dayName = dateObj.toLocaleDateString("vi-VN", { weekday: "long" });
  const formattedDate = dateObj.toLocaleDateString("vi-VN", {
    month: "short",
    day: "numeric",
  });

  const dayPlan = dailyPlans[date] || {
    timePhases: [],
  };

  const totalPhases = dayPlan.timePhases.length;
  const totalActivities = dayPlan.timePhases.reduce(
    (sum, phase) => sum + phase.activities.length,
    0
  );

  return `
    <div class="day-plan-card-redesigned" data-date="${date}">
      <div class="day-plan-header-redesigned">
        <div class="day-info">
          <span class="day-number-badge">Ngày ${dayNumber}</span>
          <span>${dayName}, ${formattedDate}</span>
        </div>
        <div class="day-plan-header-actions">
          ${
            totalPhases > 0
              ? `<button type="button" class="btn-view-day-plan" onclick="openDayPlanModal(\'${date}\', ${dayNumber}, \'${dayName}, ${formattedDate}\')">Xem kế hoạch</button>`
              : ""
          }
          <button type="button" class="day-plan-toggle" onclick="toggleDayPlanRedesigned(\'${date}\')">▼</button>
        </div>
      </div>
      <div class="day-plan-content-redesigned" id="day-content-${date}">
        <div class="day-controls-redesigned">
          <button type="button" class="btn-add-time-phase-redesigned" onclick="addTimePhaseRedesigned(\'${date}\')">
            <span>+</span> Thêm khung giờ
          </button>
          ${
            totalPhases > 0
              ? `
            <div style="margin-top: 12px; text-align: center; color: #6c757d; font-size: 14px;">
              ${totalPhases} khung giờ • ${totalActivities} hoạt động
            </div>
          `
              : ""
          }
        </div>
        <div class="time-phases-redesigned" id="time-phases-${date}">
          ${dayPlan.timePhases
            .map((phase, index) =>
              createCustomTimePhaseRedesigned(date, phase, index)
            )
            .join("")}
          ${
            dayPlan.timePhases.length === 0
              ? '<p class="no-phases-redesigned">Chưa có khung giờ nào. Bấm "Thêm khung giờ" để bắt đầu lập kế hoạch cho ngày này!</p>'
              : ""
          }
        </div>
      </div>
    </div>
  `;
}

function createCustomTimePhaseRedesigned(date, phase, index) {
  return `
    <div class="time-phase-redesigned" data-date="${date}" data-phase-index="${index}">
      <div class="time-phase-header-redesigned">
        <div class="phase-inputs-row">
          <input type="text" class="phase-name-input-redesigned" value="${escapeHtml(
            phase.name || ""
          )}"
                 placeholder="VD: Buổi sáng tham quan, nghỉ trưa, dạo phố buổi tối"
                 onchange="updatePhaseNameRedesigned(\'${date}\', ${index}, this.value)">
          <input type="text" class="phase-time-input-redesigned" value="${escapeHtml(
            phase.timeRange || ""
          )}"
                 placeholder="VD: 09:00 - 12:00"
                 onchange="updatePhaseTimeRedesigned(\'${date}\', ${index}, this.value)">
        </div>
        <div class="phase-actions-redesigned">
          <button type="button" class="btn-add-activity-redesigned" onclick="addActivityRedesigned(\'${date}\', ${index})">+ Thêm hoạt động</button>
          <button type="button" class="btn-remove-phase-redesigned" onclick="removeTimePhaseRedesigned(\'${date}\', ${index})">Xóa khung giờ</button>
        </div>
      </div>
      <div class="time-phase-content-redesigned" id="phase-${date}-${index}">
        ${phase.activities
          .map((activity, actIndex) =>
            createActivityItemRedesigned(date, index, activity, actIndex)
          )
          .join("")}
        ${
          phase.activities.length === 0
            ? '<p class="no-activities-redesigned">Chưa có hoạt động trong khung giờ này. Bấm "Thêm hoạt động" để bắt đầu.</p>'
            : ""
        }
      </div>
    </div>
  `;
}

function createActivityItemRedesigned(
  date,
  phaseIndex,
  activity,
  activityIndex
) {
  return `
    <div class="activity-item-redesigned" data-date="${date}" data-phase-index="${phaseIndex}" data-activity-index="${activityIndex}">
      <button type="button" class="remove-activity-btn-redesigned" onclick="removeActivityRedesigned(\'${date}\', ${phaseIndex}, ${activityIndex})">×</button>
      <input type="text" class="activity-input-redesigned" placeholder="Tên hoạt động"
             value="${escapeHtml(activity.name || "")}"
             onchange="updateActivityRedesigned(\'${date}\', ${phaseIndex}, ${activityIndex}, \'name\', this.value)">
      <textarea class="activity-notes-redesigned" placeholder=""
                onchange="updateActivityRedesigned(\'${date}\', ${phaseIndex}, ${activityIndex}, \'notes\', this.value)">${escapeHtml(
    activity.notes || ""
  )}</textarea>
    </div>
  `;
}

function addTimePhaseRedesigned(date) {
  if (!dailyPlans[date]) {
    dailyPlans[date] = { timePhases: [] };
  }

  dailyPlans[date].timePhases.push({
    name: `Khung giờ ${dailyPlans[date].timePhases.length + 1}`,
    timeRange: "",
    activities: [],
  });

  refreshDayPlanRedesigned(date);
}

function removeTimePhaseRedesigned(date, phaseIndex) {
  if (dailyPlans[date] && dailyPlans[date].timePhases) {
    dailyPlans[date].timePhases.splice(phaseIndex, 1);
    refreshDayPlanRedesigned(date);
  }
}

function updatePhaseTimeRedesigned(date, phaseIndex, value) {
  if (!dailyPlans[date]) {
    dailyPlans[date] = { timePhases: [] };
  }

  if (!dailyPlans[date].timePhases[phaseIndex]) {
    dailyPlans[date].timePhases[phaseIndex] = {
      name: "",
      timeRange: "",
      activities: [],
    };
  }

  dailyPlans[date].timePhases[phaseIndex].timeRange = value;
}

function addActivityRedesigned(date, phaseIndex) {
  if (!dailyPlans[date]) {
    dailyPlans[date] = { timePhases: [] };
  }

  if (!dailyPlans[date].timePhases[phaseIndex]) {
    dailyPlans[date].timePhases[phaseIndex] = {
      name: "",
      timeRange: "",
      activities: [],
    };
  }

  dailyPlans[date].timePhases[phaseIndex].activities.push({
    name: "",
    notes: "",
  });

  refreshTimePhaseRedesigned(date, phaseIndex);
}

function removeActivityRedesigned(date, phaseIndex, activityIndex) {
  if (
    dailyPlans[date] &&
    dailyPlans[date].timePhases[phaseIndex] &&
    dailyPlans[date].timePhases[phaseIndex].activities
  ) {
    dailyPlans[date].timePhases[phaseIndex].activities.splice(activityIndex, 1);
    refreshTimePhaseRedesigned(date, phaseIndex);
  }
}

function updateActivityRedesigned(
  date,
  phaseIndex,
  activityIndex,
  field,
  value
) {
  if (!dailyPlans[date]) {
    dailyPlans[date] = { timePhases: [] };
  }

  if (!dailyPlans[date].timePhases[phaseIndex]) {
    dailyPlans[date].timePhases[phaseIndex] = {
      name: "",
      timeRange: "",
      activities: [],
    };
  }

  if (!dailyPlans[date].timePhases[phaseIndex].activities[activityIndex]) {
    dailyPlans[date].timePhases[phaseIndex].activities[activityIndex] = {
      name: "",
      notes: "",
    };
  }

  dailyPlans[date].timePhases[phaseIndex].activities[activityIndex][field] =
    value;
}

function refreshDayPlanRedesigned(date) {
  const startDate = document.getElementById("startDate").value;

  if (!startDate) {
    console.warn("Không tìm thấy ngày bắt đầu nên không thể làm mới kế hoạch ngày");
    return;
  }

  const startDateObj = new Date(startDate);
  const currentDateObj = new Date(date);
  const dayNumber =
    Math.floor((currentDateObj - startDateObj) / (1000 * 60 * 60 * 24)) + 1;

  const dayCard = document.querySelector(`[data-date="${date}"]`);
  if (dayCard) {
    const content = dayCard.querySelector(".day-plan-content-redesigned");
    const wasExpanded = content && content.classList.contains("expanded");

    dayCard.outerHTML = createDayPlanCardRedesigned(date, dayNumber);

    if (wasExpanded) {
      const newContent = document.getElementById(`day-content-${date}`);
      const newButton =
        newContent.previousElementSibling.querySelector(".day-plan-toggle");
      if (newContent && newButton) {
        newContent.classList.add("expanded");
        newButton.textContent = "▲";
      }
    }
  }
}

function refreshTimePhaseRedesigned(date, phaseIndex) {
  const container = document.getElementById(`phase-${date}-${phaseIndex}`);
  const activities = dailyPlans[date]
    ? dailyPlans[date].timePhases[phaseIndex]?.activities || []
    : [];

  container.innerHTML = activities
    .map((activity, actIndex) =>
      createActivityItemRedesigned(date, phaseIndex, activity, actIndex)
    )
    .join("");

  if (activities.length === 0) {
    container.innerHTML =
      '<p class="no-activities-redesigned">Chưa có hoạt động trong khung giờ này</p>';
  }
}

function generateDayPlanDetailedHTML(activities) {
  const phaseGroups = {};
  if (!activities || activities.length === 0) {
    return '<p class="no-activities-for-day">Chưa có hoạt động cho ngày này.</p>';
  }

  activities.forEach((activity) => {
    const phaseKey = `${activity.time_phase || "Hoạt động chung"}_${
      activity.time_range || "Không rõ"
    }`;
    if (!phaseGroups[phaseKey]) {
      phaseGroups[phaseKey] = {
        name: activity.time_phase || "Hoạt động chung",
        timeRange: activity.time_range || "",
        activities: [],
      };
    }
    phaseGroups[phaseKey].activities.push(activity);
  });

  let html = "";

  if (Object.keys(phaseGroups).length === 0) {
    return '<p class="no-phases-for-day">Chưa có khung giờ nào có hoạt động trong ngày này.</p>';
  }

  Object.values(phaseGroups).forEach((phase) => {
    html += `
      <div class="time-phase-block">
        <div class="phase-block-header">
          <h5>${escapeHtml(phase.name)}</h5>
          ${
            phase.timeRange
              ? `<span class="phase-block-time">${escapeHtml(
                  phase.timeRange
                )}</span>`
              : ""
          }
        </div>
        <ul class="activity-list-detailed">
          ${phase.activities
            .map(
              (activity) => `
            <li class="activity-item-detailed">
              <div class="activity-name-detailed">${escapeHtml(
                activity.name
              )}</div>
              ${
                activity.description
              }
              ${
                activity.estimated_duration
                  ? `<div class="activity-duration-detailed"><small>Thời lượng: ${escapeHtml(
                      activity.estimated_duration
                    )}</small></div>`
                  : ""
              }
            </li>
          `
            )
            .join("")}
        </ul>
      </div>
    `;
  });
  return (
    html ||
    '<p class="no-activities-in-phases">Chưa có hoạt động trong các khung giờ của ngày này.</p>'
  );
}

function toggleDayPlanRedesigned(date) {
  const content = document.getElementById(`day-content-${date}`);
  const button =
    content.previousElementSibling.querySelector(".day-plan-toggle");

  if (content.classList.contains("expanded")) {
    content.classList.remove("expanded");
    button.textContent = "▼";
  } else {
    content.classList.add("expanded");
    button.textContent = "▲";
  }
}

function openDayPlanModal(date, dayNumber, formattedDate) {
  const dayPlan = dailyPlans[date] || { timePhases: [] };

  document.getElementById(
    "dayPlanTitle"
  ).textContent = `Ngày ${dayNumber} - ${formattedDate}`;

  const modalContent = document.getElementById("dayPlanDetails");
  modalContent.innerHTML = generateDayPlanPreview(
    date,
    dayNumber,
    formattedDate,
    dayPlan
  );

  const editBtn = document.getElementById("editDayPlanBtn");
  editBtn.onclick = () => {
    closeDayPlanModal();

    const content = document.getElementById(`day-content-${date}`);
    const button =
      content.previousElementSibling.querySelector(".day-plan-toggle");
    if (!content.classList.contains("expanded")) {
      content.classList.add("expanded");
      button.textContent = "▲";
    }

    document
      .querySelector(`[data-date="${date}"]`)
      .scrollIntoView({ behavior: "smooth" });
  };

  const dayPlanModalElement = document.getElementById("dayPlanModal");
  dayPlanModalElement.style.display = "block";
  dayPlanModalElement.setAttribute("aria-hidden", "false");
}

function closeDayPlanModal() {
  const modal = document.getElementById("dayPlanModal");
  modal.style.display = "none";
  modal.setAttribute("aria-hidden", "true");
}

function generateDayPlanPreview(date, dayNumber, formattedDate, dayPlan) {
  const totalPhases = dayPlan.timePhases.length;
  const totalActivities = dayPlan.timePhases.reduce(
    (sum, phase) => sum + phase.activities.length,
    0
  );
  const phasesWithActivities = dayPlan.timePhases.filter(
    (phase) => phase.activities.length > 0
  ).length;

  if (totalPhases === 0) {
    return `
      <div class="preview-empty-day">
        <h4>Chưa có kế hoạch</h4>
      </div>
    `;
  }

  let html = `
    <div class="day-plan-summary">
      <h3>Tổng quan ngày ${dayNumber}</h3>
      <p>${formattedDate}</p>
    </div>

    <div class="day-plan-stats">
      <div class="stat-item">
        <div class="stat-number">${totalPhases}</div>
        <div class="stat-label">Khung giờ</div>
      </div>
      <div class="stat-item">
        <div class="stat-number">${totalActivities}</div>
        <div class="stat-label">Hoạt động</div>
      </div>
      <div class="stat-item">
        <div class="stat-number">${phasesWithActivities}</div>
        <div class="stat-label">Khung giờ có hoạt động</div>
      </div>
    </div>

    <div class="preview-time-phases">
  `;

  dayPlan.timePhases.forEach((phase, index) => {
    html += `
      <div class="preview-time-phase">
        <div class="preview-phase-header">
          <div class="preview-phase-title">
            <span class="preview-phase-name">${escapeHtml(
              phase.name || `Khung giờ ${index + 1}`
            )}</span>
            ${
              phase.timeRange
                ? `<span class="preview-phase-time">${escapeHtml(
                    phase.timeRange
                  )}</span>`
                : ""
            }
          </div>
        </div>
        <div class="preview-phase-activities">
    `;

    if (phase.activities.length === 0) {
      html += `<div class="preview-no-activities">Chưa có hoạt động trong khung giờ này</div>`;
    } else {
      phase.activities.forEach((activity) => {
        html += `
          <div class="preview-activity">
            <div class="preview-activity-name">${escapeHtml(
              activity.name || "Hoạt động chưa đặt tên"
            )}</div>
            ${
              activity.notes
                ? `<div class="preview-activity-notes">${escapeHtml(
                    activity.notes
                  )}</div>`
                : ""
            }
          </div>
        `;
      });
    }

    html += `
        </div>
      </div>
    `;
  });

  html += `</div>`;
  return html;
}
