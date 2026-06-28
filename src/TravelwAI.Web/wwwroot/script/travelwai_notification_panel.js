(function () {
  var API_URL = "/api/notifications";
  var CACHE_KEY = "travelwai:notifications:cache:v2";
  var READ_KEY = "travelwai:notifications:read:v1";
  var DELETED_KEY = "travelwai:notifications:deleted:v1";
  function getOwner() {
    return (localStorage.getItem("userEmail") || "guest").toLowerCase();
  }
  function getCacheKey() {
    return CACHE_KEY + ":" + getOwner();
  }
  function getReadKey() {
    return READ_KEY + ":" + getOwner();
  }
  function getDeletedKey() {
    return DELETED_KEY + ":" + getOwner();
  }
  var CACHE_TTL_MS = 30 * 1000;
  var POLL_MS = 30 * 1000;
  var currentItems = [];
  var currentUnread = 0;
  var notificationRequest = null;

  function getToken() {
    return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || "";
  }

  function authHeaders(json) {
    var headers = { "Authorization": "Bearer " + getToken() };
    if (json) headers["Content-Type"] = "application/json";
    return headers;
  }

  function readCache() {
    try {
      var raw = localStorage.getItem(getCacheKey());
      if (!raw) return null;
      var cached = JSON.parse(raw);
      if (!cached || !cached.expiresAt || Date.now() >= cached.expiresAt) {
        localStorage.removeItem(getCacheKey());
        return null;
      }
      return applyLocalReadState(cached.value || null);
    } catch (error) {
      return null;
    }
  }

  function saveCache(value) {
    try {
      localStorage.setItem(getCacheKey(), JSON.stringify({
        value: applyLocalReadState(value),
        expiresAt: Date.now() + CACHE_TTL_MS
      }));
    } catch (error) { }
  }

  function invalidateCache() {
    try { localStorage.removeItem(getCacheKey()); } catch (error) { }
  }

  function readLocalReadIds() {
    try {
      var raw = localStorage.getItem(getReadKey());
      var list = raw ? JSON.parse(raw) : [];
      if (Object.prototype.toString.call(list) !== "[object Array]") return {};
      var ids = {};
      for (var i = 0; i < list.length; i++) if (list[i]) ids[String(list[i])] = true;
      return ids;
    } catch (error) {
      return {};
    }
  }

  function saveLocalReadIds(ids) {
    try {
      var list = Object.keys(ids || {}).filter(Boolean).slice(-500);
      localStorage.setItem(getReadKey(), JSON.stringify(list));
    } catch (error) { }
  }

  function rememberLocalReadIds(list) {
    var ids = readLocalReadIds();
    for (var i = 0; i < (list || []).length; i++) {
      if (list[i]) ids[String(list[i])] = true;
    }
    saveLocalReadIds(ids);
  }

  function readLocalDeletedIds() {
    try {
      var raw = localStorage.getItem(getDeletedKey());
      var list = raw ? JSON.parse(raw) : [];
      if (Object.prototype.toString.call(list) !== "[object Array]") return {};
      var ids = {};
      for (var i = 0; i < list.length; i++) if (list[i]) ids[String(list[i])] = true;
      return ids;
    } catch (error) {
      return {};
    }
  }

  function saveLocalDeletedIds(ids) {
    try {
      var list = Object.keys(ids || {}).filter(Boolean).slice(-1200);
      localStorage.setItem(getDeletedKey(), JSON.stringify(list));
    } catch (error) { }
  }

  function rememberLocalDeletedIds(list) {
    var ids = readLocalDeletedIds();
    for (var i = 0; i < (list || []).length; i++) {
      if (list[i]) ids[String(list[i])] = true;
    }
    saveLocalDeletedIds(ids);
  }

  function filterDeletedNotifications(result) {
    if (!result || !result.data) return result;
    var deletedIds = readLocalDeletedIds();
    var data = result.data;
    var names = ["schedules", "friends", "messages", "systems", "all"];
    for (var n = 0; n < names.length; n++) {
      var group = data[names[n]];
      if (Object.prototype.toString.call(group) !== "[object Array]") continue;
      data[names[n]] = group.filter(function (item) {
        var id = item && item.id ? String(item.id) : "";
        return !(id && deletedIds[id]);
      });
    }
    result.count = flatten(data).length;
    return result;
  }

  function collectNotificationIds(result) {
    result = filterDeletedNotifications(result || { data: { all: currentItems || [] } });
    var items = flatten(result && result.data ? result.data : {});
    var seen = {};
    var ids = [];
    for (var i = 0; i < items.length; i++) {
      var id = items[i] && items[i].id ? String(items[i].id) : "";
      if (!id || seen[id]) continue;
      seen[id] = true;
      ids.push(id);
    }
    return ids;
  }

  function clearLocalNotifications(result) {
    var ids = result && result.data ? collectNotificationIds(result) : [];
    if (!ids.length) {
      var nodes = document.querySelectorAll(".notification-panel-item[data-notification-id]");
      for (var i = 0; i < nodes.length; i++) {
        var id = nodes[i].getAttribute("data-notification-id");
        if (id) ids.push(id);
      }
    }
    rememberLocalDeletedIds(ids);
    try { localStorage.removeItem(getReadKey()); } catch (error) { }
    invalidateCache();
    currentItems = [];
    updateBadges(0);
    var summary = document.getElementById("notification-panel-summary");
    if (summary) {
      summary.innerHTML = '<span>📅 0</span><span>👥 0</span><span>💬 0</span><span>⚙️ 0</span>';
    }
    var list = document.getElementById("notification-panel-list");
    if (list) list.innerHTML = '<div class="notification-panel-state">Đã xoá thông báo cũ.</div>';
    return ids.length;
  }

  function applyLocalReadState(result) {
    result = filterDeletedNotifications(result);
    if (!result || !result.data) return result;
    var readIds = readLocalReadIds();
    var items = flatten(result.data);
    for (var i = 0; i < items.length; i++) {
      var id = items[i] && items[i].id ? String(items[i].id) : "";
      items[i].is_read = Boolean(id && readIds[id]);
    }
    result.unread_count = countUnread(items);
    result.count = items.length;
    return result;
  }

  window.invalidateTravelwAINotificationCache = invalidateCache;
  window.clearTravelwAINotificationsLocal = clearLocalNotifications;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/\"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function getPanel() {
    return document.getElementById("notification-panel");
  }

  function updateBadges(total) {
    total = Number(total || 0);
    currentUnread = total;
    var badges = document.querySelectorAll("#notificationBadge, .notification-badge");
    for (var i = 0; i < badges.length; i++) {
      badges[i].textContent = total > 99 ? "99+" : String(total);
      badges[i].style.display = total > 0 ? "flex" : "none";
    }
    var unreadText = document.getElementById("notification-unread-count");
    if (unreadText) unreadText.textContent = String(total);
  }

  function getArray(data, name) {
    return data && Object.prototype.toString.call(data[name]) === "[object Array]" ? data[name] : [];
  }

  function flatten(data) {
    data = data || {};
    var all = getArray(data, "all");
    var items = all.length ? all : getArray(data, "schedules").concat(getArray(data, "friends"), getArray(data, "messages"), getArray(data, "systems"));
    items.sort(function (a, b) {
      return String((b && b.created_at) || "").localeCompare(String((a && a.created_at) || ""));
    });
    return items;
  }


  function iconFor(type) {
    if (type === "schedule") return "📅";
    if (type === "friend") return "👥";
    if (type === "message") return "💬";
    return "⚙️";
  }

  function typeName(type) {
    if (type === "schedule") return "Lịch trình";
    if (type === "friend") return "Kết bạn";
    if (type === "message") return "Tin nhắn";
    return "Hệ thống";
  }

  function inlineIcon(name) {
    if (name === "check") {
      return '<svg class="notification-action-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6 9 17l-5-5"></path></svg>';
    }
    if (name === "x") {
      return '<svg class="notification-action-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18"></path><path d="m6 6 12 12"></path></svg>';
    }
    return "";
  }

  function ensureToolbar() {
    var body = document.querySelector("#notification-panel .panel-body");
    if (!body || document.getElementById("notification-panel-toolbar")) return;

    var toolbar = document.createElement("div");
    toolbar.id = "notification-panel-toolbar";
    toolbar.className = "notification-panel-toolbar";
    toolbar.innerHTML = '<div class="notification-panel-unread">Chưa đọc: <strong id="notification-unread-count">0</strong></div>' +
      '<button type="button" id="notification-read-all-btn" class="notification-read-all-btn">Đã đọc tất cả</button>';

    var summary = document.getElementById("notification-panel-summary");
    if (summary && summary.parentNode === body) {
      body.insertBefore(toolbar, summary.nextSibling);
    } else {
      body.insertBefore(toolbar, body.firstChild);
    }

    var btn = document.getElementById("notification-read-all-btn");
    if (btn) btn.onclick = markAllRead;
  }

  function renderLoading() {
    var list = document.getElementById("notification-panel-list");
    if (list) list.innerHTML = '<div class="notification-panel-state">Đang tải thông báo...</div>';
  }

  function renderError(message) {
    var list = document.getElementById("notification-panel-list");
    if (list) list.innerHTML = '<div class="notification-panel-state error">' + message + '</div>';
  }

  function renderNotifications(result) {
    result = applyLocalReadState(result);
    ensureToolbar();
    var list = document.getElementById("notification-panel-list");
    if (!list) return;

    var data = result && result.data ? result.data : {};
    var items = flatten(data);
    currentItems = items;
    var unread = result && typeof result.unread_count !== "undefined" ? Number(result.unread_count) : countUnread(items);
    updateBadges(unread);

    var scheduleCount = getArray(data, "schedules").length;
    var friendCount = getArray(data, "friends").length;
    var messageCount = getArray(data, "messages").length;
    var systemCount = getArray(data, "systems").length;

    var summary = document.getElementById("notification-panel-summary");
    if (summary) {
      summary.innerHTML = '<span>📅 ' + scheduleCount + '</span>' +
        '<span>👥 ' + friendCount + '</span>' +
        '<span>💬 ' + messageCount + '</span>' +
        '<span>⚙️ ' + systemCount + '</span>';
    }

    if (!items.length) {
      list.innerHTML = '<div class="notification-panel-state">Chưa có thông báo mới.</div>';
      return;
    }

    var html = "";
    for (var i = 0; i < items.length; i++) {
      var item = items[i] || {};
      var url = item.url || "/notifications";
      var id = item.id || "";
      var readClass = item.is_read ? " is-read" : " is-unread";
      var isFriendRequest = item.type === "friend" && item.request_email;
      var tag = isFriendRequest ? "div" : "a";
      var href = isFriendRequest ? "" : ' href="' + escapeHtml(url) + '"';
      var actions = isFriendRequest
        ? '<div class="notification-friend-actions">' +
          '<button type="button" class="notification-friend-action-btn accept" data-request-email="' + escapeHtml(item.request_email) + '" data-action="accepted" aria-label="Đồng ý" title="Đồng ý">' + inlineIcon("check") + '</button>' +
          '<button type="button" class="notification-friend-action-btn decline" data-request-email="' + escapeHtml(item.request_email) + '" data-action="declined" aria-label="Từ chối" title="Từ chối">' + inlineIcon("x") + '</button>' +
          '</div>'
        : "";

      html += '<' + tag + ' class="notification-panel-item' + readClass + '" data-notification-id="' + escapeHtml(id) + '"' + href + '>' +
        '<div class="notification-panel-icon">' + iconFor(item.type) + '</div>' +
        '<div class="notification-panel-content">' +
        '<div class="notification-panel-type">' + typeName(item.type) + (item.is_read ? '' : '<span class="notification-unread-dot">Mới</span>') + '</div>' +
        '<h4>' + escapeHtml(item.title || "Thông báo") + '</h4>' +
        '<p>' + escapeHtml(item.content || "Có thông báo mới.") + '</p>' +
        actions +
        '</div></' + tag + '>';
    }
    list.innerHTML = html;
  }

  function fetchNotifications(forceRefresh) {
    if (!forceRefresh) {
      var cached = readCache();
      if (cached) return Promise.resolve(cached);
    }

    if (notificationRequest) return notificationRequest;

    notificationRequest = fetch(API_URL, { headers: authHeaders(false) }).then(function (response) {
      if (response.status === 401) {
        updateBadges(0);
        return null;
      }
      if (!response.ok) throw new Error("Không tải được thông báo (" + response.status + ")");
      return response.json();
    }).then(function (result) {
      if (result) {
        result = applyLocalReadState(result);
        saveCache(result);
      }
      return result;
    }).finally(function () {
      notificationRequest = null;
    });

    return notificationRequest;
  }

  function handleNotificationFriendAction(event, button) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    var requestEmail = button.getAttribute("data-request-email");
    var action = button.getAttribute("data-action");
    if (!requestEmail || !action || !getToken()) return false;

    var item = button.closest(".notification-panel-item");
    var buttons = item ? item.querySelectorAll(".notification-friend-action-btn") : [];
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = true;

    var formData = new FormData();
    formData.append("request_email", requestEmail);
    formData.append("action", action);

    fetch("/api/friend_requests", {
      method: "POST",
      headers: authHeaders(false),
      body: formData
    }).then(function (response) {
      if (!response.ok) throw new Error("Không thể xử lý lời mời kết bạn.");
      return response.json();
    }).then(function () {
      invalidateCache();
      if (item && item.parentNode) item.parentNode.removeChild(item);
      if (window.refreshFriendsAndRequests) window.refreshFriendsAndRequests(false, true);
      loadNotifications(true, true);
    }).catch(function (error) {
      for (var i = 0; i < buttons.length; i++) buttons[i].disabled = false;
      renderError(escapeHtml(error.message || "Không thể xử lý lời mời kết bạn."));
    });

    return false;
  }

  function countUnread(items) {
    var count = 0;
    for (var i = 0; i < items.length; i++) if (!items[i].is_read) count++;
    return count;
  }

  function loadNotifications(silent, forceRefresh) {
    var token = getToken();
    if (!token) {
      updateBadges(0);
      if (!silent) renderError('Bạn cần đăng nhập để xem thông báo. <a href="/login">Đăng nhập</a>');
      return;
    }

    if (!forceRefresh) {
      var cached = readCache();
      if (cached) {
        renderNotifications(cached);
        return;
      }
    }

    if (!silent) renderLoading();

    fetchNotifications(Boolean(forceRefresh)).then(function (result) {
      if (result) renderNotifications(result);
      else if (!silent) renderError('Phiên đăng nhập đã hết hạn. <a href="/login">Đăng nhập lại</a>');
    }).catch(function (error) {
      if (!silent) renderError(escapeHtml(error.message || "Không tải được thông báo."));
    });
  }

  function markOneRead(id, element) {
    if (!id || !getToken()) return;

    rememberLocalReadIds([id]);

    if (element && !element.classList.contains("is-read")) {
      element.classList.remove("is-unread");
      element.classList.add("is-read");
      var dot = element.querySelector(".notification-unread-dot");
      if (dot && dot.parentNode) dot.parentNode.removeChild(dot);
      updateBadges(Math.max(0, currentUnread - 1));
    }

    invalidateCache();
  }

  function markAllRead(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    if (!getToken()) return false;

    var btn = document.getElementById("notification-read-all-btn");
    if (btn) {
      btn.disabled = true;
      btn.textContent = "Đang xử lý...";
    }

    var ids = [];
    var items = document.querySelectorAll(".notification-panel-item");
    for (var i = 0; i < items.length; i++) {
      var id = items[i].getAttribute("data-notification-id");
      if (id) ids.push(id);
      items[i].classList.remove("is-unread");
      items[i].classList.add("is-read");
      var dot = items[i].querySelector(".notification-unread-dot");
      if (dot && dot.parentNode) dot.parentNode.removeChild(dot);
    }
    rememberLocalReadIds(ids);
    invalidateCache();
    updateBadges(0);

    if (btn) {
      btn.disabled = false;
      btn.textContent = "Đã đọc tất cả";
    }
    return false;
  }

  function openNotificationPanel(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    var panel = getPanel();
    if (!panel) {
      window.location.href = "/notifications";
      return false;
    }
    ensureToolbar();
    panel.classList.add("open");
    panel.setAttribute("aria-hidden", "false");
    document.body.classList.add("notification-panel-open");
    loadNotifications(false, true);
    return false;
  }

  function closeNotificationPanel() {
    var panel = getPanel();
    if (!panel) return;
    panel.classList.remove("open");
    panel.setAttribute("aria-hidden", "true");
    document.body.classList.remove("notification-panel-open");
  }

  window.addEventListener("travelwai:notifications-cleared", function () {
    notificationRequest = null;
    invalidateCache();
    currentItems = [];
    updateBadges(0);
    var summary = document.getElementById("notification-panel-summary");
    if (summary) summary.innerHTML = '<span>📅 0</span><span>👥 0</span><span>💬 0</span><span>⚙️ 0</span>';
    var list = document.getElementById("notification-panel-list");
    if (list) list.innerHTML = '<div class="notification-panel-state">Đã xoá thông báo cũ.</div>';
  });

  window.openNotificationPanel = openNotificationPanel;
  window.closeNotificationPanel = closeNotificationPanel;
  window.loadTravelwainotifications = loadNotifications;
  window.markAllNotificationsRead = markAllRead;

  function bindNotificationTriggers() {
    ensureToolbar();
    var triggers = document.querySelectorAll('a[href="/notifications"], .notification-icon-container, [data-notification-panel-trigger], #notificationIconContainer');
    for (var i = 0; i < triggers.length; i++) {
      triggers[i].setAttribute("data-notification-panel-trigger", "");
      triggers[i].onclick = openNotificationPanel;
    }

    var closes = document.querySelectorAll("[data-close-notification-panel]");
    for (var j = 0; j < closes.length; j++) {
      closes[j].onclick = function (event) {
        if (event) event.preventDefault();
        closeNotificationPanel();
        return false;
      };
    }

    var btn = document.getElementById("notification-read-all-btn");
    if (btn) btn.onclick = markAllRead;
  }

  document.addEventListener("click", function (event) {
    var target = event.target;
    var friendAction = target.closest ? target.closest(".notification-friend-action-btn") : null;
    if (friendAction) {
      handleNotificationFriendAction(event, friendAction);
      return false;
    }

    var notificationItem = target.closest ? target.closest(".notification-panel-item") : null;
    if (notificationItem) {
      var id = notificationItem.getAttribute("data-notification-id");
      markOneRead(id, notificationItem);
      return true;
    }

    var readAllBtn = target.closest ? target.closest("#notification-read-all-btn") : null;
    if (readAllBtn) {
      markAllRead(event);
      return false;
    }

    var trigger = target.closest ? target.closest('a[href="/notifications"], .notification-icon-container, [data-notification-panel-trigger], #notificationIconContainer') : null;
    if (trigger) {
      openNotificationPanel(event);
      return false;
    }

    var panel = getPanel();
    if (!panel || !panel.classList.contains("open")) return;
    if (!panel.contains(target)) closeNotificationPanel();
  }, true);

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" || event.keyCode === 27) closeNotificationPanel();
  });

  document.addEventListener("visibilitychange", function () {
    if (!document.hidden) loadNotifications(true, false);
  });

  function start() {
    bindNotificationTriggers();
    loadNotifications(true, false);
    window.setInterval(function () {
      if (!document.hidden) loadNotifications(true, false);
    }, POLL_MS);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start);
  } else {
    start();
  }
})();
