const TRAVELWAI_NOTIFICATION_CACHE_KEY = "travelwai:notifications:cache:v9";
const TRAVELWAI_NOTIFICATION_CACHE_TTL_MS = 60 * 1000;
const TRAVELWAI_NOTIFICATION_POLL_MS = 60 * 1000;
let travelwaiNotificationBadgeRequest = null;

function getTravelwAINotificationOwner() {
  return (sessionStorage.getItem("userEmail") || localStorage.getItem("userEmail") || "guest").toLowerCase();
}

function getTravelwAINotificationCacheKey() {
  return `${TRAVELWAI_NOTIFICATION_CACHE_KEY}:${getTravelwAINotificationOwner()}`;
}

function getNotificationBadgeToken() {
  const cookie = document.cookie.split(";").map(value => value.trim()).find(value => value.startsWith("TravelwAIAuth="));
  return (cookie ? decodeURIComponent(cookie.slice("TravelwAIAuth=".length)) : "") || localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || "";
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
    return cached.value || null;
  } catch {
    return null;
  }
}

function saveNotificationCache(value) {
  try {
    localStorage.setItem(getTravelwAINotificationCacheKey(), JSON.stringify({
      value,
      expiresAt: Date.now() + TRAVELWAI_NOTIFICATION_CACHE_TTL_MS
    }));
  } catch { }
}

function renderNotificationBadge(total) {
  document.querySelectorAll("#notificationBadge, .notification-badge").forEach(badge => {
    const count = Number(total || 0);
    badge.textContent = count > 99 ? "99+" : String(count);
    badge.style.display = count > 0 ? "flex" : "none";
  });
}

async function loadNotificationBadge(forceRefresh = false) {
  const badge = document.getElementById("notificationBadge");
  if (!badge) return;
  if (!forceRefresh) {
    const cached = readNotificationCache();
    if (cached) {
      renderNotificationBadge(Number(cached.unread_count || 0));
      return;
    }
  }
  if (travelwaiNotificationBadgeRequest) {
    const result = await travelwaiNotificationBadgeRequest.catch(() => null);
    if (result) renderNotificationBadge(Number(result.unread_count || 0));
    return;
  }

  try {
    const token = getNotificationBadgeToken();
    const headers = token ? { Authorization: `Bearer ${token}` } : {};
    travelwaiNotificationBadgeRequest = fetch("/api/notifications", {
      credentials: "same-origin",
      headers
    }).then(async response => {
      if (!response.ok) throw new Error("Không tải được thông báo");
      return response.json();
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

["travelwai:notification-created", "travelwai:notifications-read"].forEach(eventName => {
  window.addEventListener(eventName, function () {
    travelwaiNotificationBadgeRequest = null;
    window.invalidateTravelwAINotificationCache();
    loadNotificationBadge(true);
  });
});

window.addEventListener("travelwai:notifications-cleared", function () {
  travelwaiNotificationBadgeRequest = null;
  window.invalidateTravelwAINotificationCache();
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
