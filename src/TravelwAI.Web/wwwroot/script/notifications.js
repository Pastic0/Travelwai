const NOTIFICATION_API = "/api/notifications";
const NOTIFICATION_CACHE_KEY = "travelwai:notifications:cache:v2";
const NOTIFICATION_READ_KEY = "travelwai:notifications:read:v1";
const NOTIFICATION_DELETED_KEY = "travelwai:notifications:deleted:v1";
function getNotificationOwner() {
  return (localStorage.getItem("userEmail") || "guest").toLowerCase();
}
function getNotificationCacheKey() {
  return `${NOTIFICATION_CACHE_KEY}:${getNotificationOwner()}`;
}
function getNotificationReadKey() {
  return `${NOTIFICATION_READ_KEY}:${getNotificationOwner()}`;
}
function getNotificationDeletedKey() {
  return `${NOTIFICATION_DELETED_KEY}:${getNotificationOwner()}`;
}
const NOTIFICATION_CACHE_TTL_MS = 30 * 1000;
const NOTIFICATION_POLL_MS = 30 * 1000;
let notificationPageRequest = null;

function getNotificationToken() {
  return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token");
}

function readLocalNotificationReads() {
  try {
    const raw = localStorage.getItem(getNotificationReadKey());
    const ids = raw ? JSON.parse(raw) : [];
    return Array.isArray(ids) ? new Set(ids.filter(Boolean).map(String)) : new Set();
  } catch {
    return new Set();
  }
}

function saveLocalNotificationReads(ids) {
  try {
    const list = Array.from(ids).filter(Boolean).slice(-500);
    localStorage.setItem(getNotificationReadKey(), JSON.stringify(list));
  } catch { }
}

function rememberLocalNotificationReads(ids) {
  const saved = readLocalNotificationReads();
  (ids || []).forEach(id => {
    if (id) saved.add(String(id));
  });
  saveLocalNotificationReads(saved);
}

function readLocalNotificationDeletedIds() {
  try {
    const raw = localStorage.getItem(getNotificationDeletedKey());
    const ids = raw ? JSON.parse(raw) : [];
    return Array.isArray(ids) ? new Set(ids.filter(Boolean).map(String)) : new Set();
  } catch {
    return new Set();
  }
}

function saveLocalNotificationDeletedIds(ids) {
  try {
    const list = Array.from(ids).filter(Boolean).slice(-1200);
    localStorage.setItem(getNotificationDeletedKey(), JSON.stringify(list));
  } catch { }
}

function rememberLocalNotificationDeletedIds(ids) {
  const saved = readLocalNotificationDeletedIds();
  (ids || []).forEach(id => {
    if (id) saved.add(String(id));
  });
  saveLocalNotificationDeletedIds(saved);
}

function filterDeletedNotifications(result) {
  if (!result || !result.data) return result;
  const deletedIds = readLocalNotificationDeletedIds();
  ["schedules", "friends", "messages", "systems", "all"].forEach(name => {
    if (!Array.isArray(result.data[name])) return;
    result.data[name] = result.data[name].filter(item => {
      const id = item && item.id ? String(item.id) : "";
      return !(id && deletedIds.has(id));
    });
  });
  result.count = getUniqueNotificationItems(result).length;
  return result;
}

function clearOldNotificationsFromPage(result) {
  let ids = [];
  if (result && result.data) {
    result = filterDeletedNotifications(result);
    ids = getUniqueNotificationItems(result).map(item => item && item.id ? String(item.id) : "").filter(Boolean);
  } else {
    document.querySelectorAll(".notification-item[data-notification-id]").forEach(item => {
      const id = item.getAttribute("data-notification-id");
      if (id) ids.push(id);
    });
  }
  rememberLocalNotificationDeletedIds(ids);
  try { localStorage.removeItem(getNotificationReadKey()); } catch { }
  invalidateNotificationCache();
  updateNotificationBadge(0);
  setText("notificationPageUnreadCount", 0);
  setText("scheduleCount", 0);
  setText("friendCount", 0);
  setText("messageCount", 0);
  setText("systemCount", 0);
  renderGroup("scheduleNotificationList", [], "Đã xoá thông báo cũ.", "📅");
  renderGroup("friendNotificationList", [], "Đã xoá thông báo cũ.", "👥");
  renderGroup("messageNotificationList", [], "Đã xoá thông báo cũ.", "💬");
  renderGroup("systemNotificationList", [], "Đã xoá thông báo cũ.", "⚙️");
  return ids.length;
}

window.clearTravelwAINotificationsPage = clearOldNotificationsFromPage;

function collectNotificationItems(result) {
  const data = result && result.data ? result.data : {};
  const groups = [data.schedules, data.friends, data.messages, data.systems, data.all];
  const items = [];
  groups.forEach(group => {
    if (Array.isArray(group)) group.forEach(item => item && items.push(item));
  });
  return items;
}

function getUniqueNotificationItems(result) {
  const seen = new Set();
  const items = [];
  collectNotificationItems(result).forEach(item => {
    const id = item && item.id ? String(item.id) : "";
    const key = id || `${item.type || ""}|${item.title || ""}|${item.content || ""}|${item.created_at || ""}`;
    if (!key || seen.has(key)) return;
    seen.add(key);
    items.push(item);
  });
  return items;
}

function applyLocalNotificationReadState(result) {
  result = filterDeletedNotifications(result);
  if (!result || !result.data) return result;
  const readIds = readLocalNotificationReads();
  collectNotificationItems(result).forEach(item => {
    const id = item && item.id ? String(item.id) : "";
    item.is_read = Boolean(id && readIds.has(id));
  });
  const unread = getUniqueNotificationItems(result).filter(item => !item.is_read).length;
  result.unread_count = unread;
  result.count = getUniqueNotificationItems(result).length;
  return result;
}

function readNotificationCache() {
  try {
    const raw = localStorage.getItem(getNotificationCacheKey());
    if (!raw) return null;
    const cached = JSON.parse(raw);
    if (!cached || !cached.expiresAt || Date.now() >= cached.expiresAt) {
      localStorage.removeItem(getNotificationCacheKey());
      return null;
    }
    return applyLocalNotificationReadState(cached.value || null);
  } catch {
    return null;
  }
}

function saveNotificationCache(value) {
  try {
    localStorage.setItem(getNotificationCacheKey(), JSON.stringify({
      value: applyLocalNotificationReadState(value),
      expiresAt: Date.now() + NOTIFICATION_CACHE_TTL_MS
    }));
  } catch { }
}

function invalidateNotificationCache() {
  try { localStorage.removeItem(getNotificationCacheKey()); } catch { }
  if (window.invalidateTravelwAINotificationCache) window.invalidateTravelwAINotificationCache();
}

async function fetchNotifications(forceRefresh = false) {
  const token = getNotificationToken();
  if (!token) {
    window.location.href = "/login";
    return null;
  }

  if (!forceRefresh) {
    const cached = readNotificationCache();
    if (cached) return cached;
  }

  if (notificationPageRequest) return notificationPageRequest;

  notificationPageRequest = fetch(NOTIFICATION_API, {
    headers: { Authorization: `Bearer ${token}` }
  }).then(async response => {
    if (response.status === 401) {
      window.location.href = "/login";
      return null;
    }

    if (!response.ok) {
      throw new Error(`Không tải được thông báo (${response.status})`);
    }

    const result = applyLocalNotificationReadState(await response.json());
    saveNotificationCache(result);
    return result;
  }).finally(() => {
    notificationPageRequest = null;
  });

  return notificationPageRequest;
}

function setText(id, text) {
  const element = document.getElementById(id);
  if (element) element.textContent = text;
}

function updateNotificationBadge(total) {
  document.querySelectorAll("#notificationBadge, .notification-badge").forEach(badge => {
    badge.textContent = total > 99 ? "99+" : String(total || 0);
    badge.style.display = total > 0 ? "flex" : "none";
  });
}

function escapeHtml(value) {
  return String(value == null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function renderGroup(containerId, items, emptyText, icon) {
  const container = document.getElementById(containerId);
  if (!container) return;

  if (!items || items.length === 0) {
    container.innerHTML = `<div class="notification-empty">${emptyText}</div>`;
    return;
  }

  container.innerHTML = items.map(item => {
    const readClass = item.is_read ? " is-read" : " is-unread";
    const unread = item.is_read ? "" : `<span class="notification-unread-dot">Mới</span>`;
    return `
      <a class="notification-item${readClass}" data-notification-id="${escapeHtml(item.id || "")}" href="${escapeHtml(item.url || "/notifications")}">
        <div class="notification-icon">${icon}</div>
        <div class="notification-content">
          <h3>${escapeHtml(item.title)} ${unread}</h3>
          <p>${escapeHtml(item.content)}</p>
        </div>
      </a>
    `;
  }).join("");
}

function updateUnreadTextFromDom() {
  const unread = document.querySelectorAll(".notification-item.is-unread").length;
  setText("notificationPageUnreadCount", unread);
  updateNotificationBadge(unread);
}

async function markNotificationRead(id, element) {
  if (!id || !getNotificationToken()) return;

  rememberLocalNotificationReads([id]);

  if (element && !element.classList.contains("is-read")) {
    element.classList.remove("is-unread");
    element.classList.add("is-read");
    const dot = element.querySelector(".notification-unread-dot");
    if (dot) dot.remove();
  }

  invalidateNotificationCache();
  updateUnreadTextFromDom();
}

async function markAllNotificationsRead(event) {
  if (event) event.preventDefault();
  if (!getNotificationToken()) return;

  const btn = document.getElementById("markAllNotificationsReadBtn");
  if (btn) {
    btn.disabled = true;
    btn.textContent = "Đang xử lý...";
  }

  const ids = [];
  document.querySelectorAll(".notification-item").forEach(item => {
    const id = item.getAttribute("data-notification-id");
    if (id) ids.push(id);
    item.classList.remove("is-unread");
    item.classList.add("is-read");
    const dot = item.querySelector(".notification-unread-dot");
    if (dot) dot.remove();
  });
  rememberLocalNotificationReads(ids);
  invalidateNotificationCache();
  updateNotificationBadge(0);
  setText("notificationPageUnreadCount", 0);

  if (btn) {
    btn.disabled = false;
    btn.textContent = "Đã đọc tất cả";
  }
}

async function loadNotificationPage(forceRefresh = false) {
  try {
    const result = applyLocalNotificationReadState(await fetchNotifications(forceRefresh));
    if (!result) return;

    const data = result.data || {};
    const schedules = data.schedules || [];
    const friends = data.friends || [];
    const messages = data.messages || [];
    const systems = data.systems || [];

    setText("scheduleCount", schedules.length);
    setText("friendCount", friends.length);
    setText("messageCount", messages.length);
    setText("systemCount", systems.length);
    setText("notificationPageUnreadCount", Number(result.unread_count || 0));
    updateNotificationBadge(Number(result.unread_count || 0));

    renderGroup("scheduleNotificationList", schedules, "Chưa có thông báo lịch trình.", "📅");
    renderGroup("friendNotificationList", friends, "Chưa có lời mời kết bạn mới.", "👥");
    renderGroup("messageNotificationList", messages, "Chưa có tin nhắn mới.", "💬");
    renderGroup("systemNotificationList", systems, "Chưa có thông báo hệ thống.", "⚙️");
  } catch (error) {
    document.querySelectorAll(".notification-list").forEach(x => {
      x.innerHTML = `<div class="notification-error">${escapeHtml(error.message)}</div>`;
    });
  }
}

window.addEventListener("travelwai:notifications-cleared", () => {
  notificationPageRequest = null;
  try { localStorage.removeItem(getNotificationCacheKey()); } catch { }
  updateNotificationBadge(0);
  setText("notificationPageUnreadCount", 0);
  setText("scheduleCount", 0);
  setText("friendCount", 0);
  setText("messageCount", 0);
  setText("systemCount", 0);
  renderGroup("scheduleNotificationList", [], "Đã xoá thông báo cũ.", "📅");
  renderGroup("friendNotificationList", [], "Đã xoá thông báo cũ.", "👥");
  renderGroup("messageNotificationList", [], "Đã xoá thông báo cũ.", "💬");
  renderGroup("systemNotificationList", [], "Đã xoá thông báo cũ.", "⚙️");
});

document.addEventListener("click", event => {
  const readAll = event.target.closest && event.target.closest("#markAllNotificationsReadBtn");
  if (readAll) {
    markAllNotificationsRead(event);
    return;
  }

  const item = event.target.closest && event.target.closest(".notification-item");
  if (item) {
    markNotificationRead(item.getAttribute("data-notification-id"), item);
  }
});

document.addEventListener("DOMContentLoaded", () => {
  loadNotificationPage();
  setInterval(() => {
    if (!document.hidden) loadNotificationPage();
  }, NOTIFICATION_POLL_MS);
});

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) loadNotificationPage();
});
