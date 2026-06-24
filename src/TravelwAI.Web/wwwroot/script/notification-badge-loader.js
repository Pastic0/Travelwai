const TRAVELWAI_NOTIFICATION_CACHE_KEY = "travelwai:notifications:cache:v2";
const TRAVELWAI_NOTIFICATION_READ_KEY = "travelwai:notifications:read:v1";
const TRAVELWAI_NOTIFICATION_DELETED_KEY = "travelwai:notifications:deleted:v1";
function getTravelwAINotificationOwner() {
  return (localStorage.getItem("userEmail") || "guest").toLowerCase();
}
function getTravelwAINotificationCacheKey() {
  return `${TRAVELWAI_NOTIFICATION_CACHE_KEY}:${getTravelwAINotificationOwner()}`;
}
function getTravelwAINotificationReadKey() {
  return `${TRAVELWAI_NOTIFICATION_READ_KEY}:${getTravelwAINotificationOwner()}`;
}
function getTravelwAINotificationDeletedKey() {
  return `${TRAVELWAI_NOTIFICATION_DELETED_KEY}:${getTravelwAINotificationOwner()}`;
}
const TRAVELWAI_NOTIFICATION_CACHE_TTL_MS = 30 * 1000;
const TRAVELWAI_NOTIFICATION_POLL_MS = 30 * 1000;
let travelwaiNotificationBadgeRequest = null;

function getNotificationBadgeToken() {
  return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token");
}

function readTravelwAINotificationReadIds() {
  try {
    const raw = localStorage.getItem(getTravelwAINotificationReadKey());
    const ids = raw ? JSON.parse(raw) : [];
    return Array.isArray(ids) ? new Set(ids.filter(Boolean).map(String)) : new Set();
  } catch {
    return new Set();
  }
}

function readTravelwAINotificationDeletedIds() {
  try {
    const raw = localStorage.getItem(getTravelwAINotificationDeletedKey());
    const ids = raw ? JSON.parse(raw) : [];
    return Array.isArray(ids) ? new Set(ids.filter(Boolean).map(String)) : new Set();
  } catch {
    return new Set();
  }
}

function filterTravelwAIDeletedNotifications(result) {
  if (!result || !result.data) return result;
  const deletedIds = readTravelwAINotificationDeletedIds();
  ["schedules", "friends", "messages", "systems", "all"].forEach(name => {
    if (!Array.isArray(result.data[name])) return;
    result.data[name] = result.data[name].filter(item => {
      const id = item && item.id ? String(item.id) : "";
      return !(id && deletedIds.has(id));
    });
  });
  return result;
}

function collectTravelwAINotificationItems(result) {
  const data = result && result.data ? result.data : {};
  const groups = [data.schedules, data.friends, data.messages, data.systems, data.all];
  const items = [];
  groups.forEach(group => {
    if (Array.isArray(group)) group.forEach(item => item && items.push(item));
  });
  return items;
}

function applyTravelwAILocalNotificationReadState(result) {
  result = filterTravelwAIDeletedNotifications(result);
  if (!result || !result.data) return result;
  const readIds = readTravelwAINotificationReadIds();
  const seen = new Set();
  let unread = 0;
  collectTravelwAINotificationItems(result).forEach(item => {
    const id = item && item.id ? String(item.id) : "";
    item.is_read = Boolean(id && readIds.has(id));
    const key = id || `${item.type || ""}|${item.title || ""}|${item.content || ""}|${item.created_at || ""}`;
    if (!key || seen.has(key)) return;
    seen.add(key);
    if (!item.is_read) unread += 1;
  });
  result.unread_count = unread;
  result.count = seen.size;
  return result;
}

function readNotificationCache() {
  try {
    const raw = localStorage.getItem(getTravelwAINotificationCacheKey());
    if (!raw) return null;
    const cached = JSON.parse(raw);
    if (!cached || !cached.expiresAt || Date.now() >= cached.expiresAt) {
      localStorage.removeItem(getTravelwAINotificationCacheKey());
      return null;
    }
    return applyTravelwAILocalNotificationReadState(cached.value || null);
  } catch {
    return null;
  }
}

function saveNotificationCache(value) {
  try {
    localStorage.setItem(getTravelwAINotificationCacheKey(), JSON.stringify({
      value: applyTravelwAILocalNotificationReadState(value),
      expiresAt: Date.now() + TRAVELWAI_NOTIFICATION_CACHE_TTL_MS
    }));
  } catch { }
}

function renderNotificationBadge(total) {
  const badge = document.getElementById("notificationBadge");
  if (!badge) return;
  total = Number(total || 0);
  badge.textContent = total > 99 ? "99+" : String(total);
  badge.style.display = total > 0 ? "flex" : "none";
}

async function loadNotificationBadge(forceRefresh = false) {
  const badge = document.getElementById("notificationBadge");
  if (!badge) return;

  const token = getNotificationBadgeToken();
  if (!token) {
    badge.style.display = "none";
    return;
  }

  if (!forceRefresh) {
    const cached = readNotificationCache();
    if (cached) {
      renderNotificationBadge(Number(cached.unread_count || 0));
      return;
    }
  }

  if (travelwaiNotificationBadgeRequest) {
    const result = await travelwaiNotificationBadgeRequest.catch(() => null);
    if (result) renderNotificationBadge(Number(applyTravelwAILocalNotificationReadState(result).unread_count || 0));
    return;
  }

  try {
    travelwaiNotificationBadgeRequest = fetch("/api/notifications", {
      headers: { Authorization: `Bearer ${token}` }
    }).then(async response => {
      if (!response.ok) throw new Error("Không tải được thông báo");
      return applyTravelwAILocalNotificationReadState(await response.json());
    });

    const result = await travelwaiNotificationBadgeRequest;
    saveNotificationCache(result);
    renderNotificationBadge(Number(result.unread_count || 0));
  } catch {
    badge.style.display = "none";
  } finally {
    travelwaiNotificationBadgeRequest = null;
  }
}

window.invalidateTravelwAINotificationCache = function () {
  try { localStorage.removeItem(getTravelwAINotificationCacheKey()); } catch { }
};

window.refreshTravelwAINotificationBadge = function (forceRefresh = false) {
  return loadNotificationBadge(forceRefresh);
};

window.addEventListener("travelwai:notifications-cleared", function () {
  travelwaiNotificationBadgeRequest = null;
  try { localStorage.removeItem(getTravelwAINotificationCacheKey()); } catch { }
  renderNotificationBadge(0);
});

document.addEventListener("DOMContentLoaded", () => {
  loadNotificationBadge();
  setInterval(() => {
    if (!document.hidden) loadNotificationBadge();
  }, TRAVELWAI_NOTIFICATION_POLL_MS);
});

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) loadNotificationBadge();
});
