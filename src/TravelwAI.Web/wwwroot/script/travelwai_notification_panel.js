(function () {
  var API_URL = "/api/notifications";
  var CACHE_KEY = "travelwai:notifications:cache:v9";
  var CACHE_TTL_MS = 60 * 1000;
  var POLL_MS = 60 * 1000;
  var currentItems = [];
  var currentUnread = 0;
  var notificationRequest = null;

  function getOwner() {
    return (sessionStorage.getItem("userEmail") || localStorage.getItem("userEmail") || "guest").toLowerCase();
  }

  function getCacheKey() {
    return CACHE_KEY + ":" + getOwner();
  }

  function readCookie(name) {
    var prefix = name + "=";
    var item = document.cookie.split(";").map(function (part) { return part.trim(); }).find(function (part) { return part.indexOf(prefix) === 0; });
    return item ? decodeURIComponent(item.slice(prefix.length)) : "";
  }

  function getToken() {
    return readCookie("TravelwAIAuth") || localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || "";
  }

  function authHeaders(json) {
    var headers = {};
    var token = getToken();
    if (token) headers["Authorization"] = "Bearer " + token;
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
      return cached.value || null;
    } catch (error) {
      return null;
    }
  }

  function saveCache(value) {
    try {
      localStorage.setItem(getCacheKey(), JSON.stringify({
        value: value,
        expiresAt: Date.now() + CACHE_TTL_MS
      }));
    } catch (error) { }
  }

  function invalidateCache() {
    try { localStorage.removeItem(getCacheKey()); } catch (error) { }
  }

  function notificationMutation(path, body, keepalive) {
    return fetch(API_URL + path, {
      method: "POST",
      credentials: "same-origin",
      headers: authHeaders(true),
      body: JSON.stringify(body || {}),
      keepalive: Boolean(keepalive)
    }).then(function (response) {
      return response.json().catch(function () { return null; }).then(function (result) {
        if (response.status === 401) throw new Error("Phiên đăng nhập đã hết hạn.");
        if (!response.ok || (result && result.success === false)) {
          throw new Error((result && result.message) || "Thao tác thông báo thất bại (" + response.status + ").");
        }
        return result || { success: true };
      });
    });
  }

  function clearPanelView() {
    currentItems = [];
    updateBadges(0);
    var list = document.getElementById("notification-panel-list");
    if (list) list.innerHTML = '<div class="notification-panel-state">Đã dọn thông báo.</div>';
  }

  function clearNotificationsInDatabase(ids) {
    var cleanIds = Object.prototype.toString.call(ids) === "[object Array]"
      ? ids.filter(Boolean).map(String).filter(function (id, index, list) { return list.indexOf(id) === index; })
      : [];
    return notificationMutation("/clear", { ids: cleanIds }, false).then(function (result) {
      notificationRequest = null;
      invalidateCache();
      clearPanelView();
      window.dispatchEvent(new CustomEvent("travelwai:notifications-cleared", { detail: result }));
      return result;
    });
  }

  window.invalidateTravelwAINotificationCache = invalidateCache;
  window.clearTravelwAINotifications = clearNotificationsInDatabase;
  window.clearTravelwAINotificationsLocal = clearNotificationsInDatabase;

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
    var items = all.length ? all : getArray(data, "friends")
      .concat(getArray(data, "messages"), getArray(data, "tours"), getArray(data, "schedules"), getArray(data, "payments"));
    items = items.filter(function (item) {
      var category = String((item && item.category) || "").toLowerCase();
      return category === "friend-request" || category === "message-new" || category === "tour-booked" || category === "schedule-created" || category === "payment-success";
    });
    items.sort(function (a, b) {
      return String((b && b.created_at) || "").localeCompare(String((a && a.created_at) || ""));
    });
    return items;
  }


  function typeName(type) {
    if (type === "friend") return "Kết bạn";
    if (type === "message") return "Tin nhắn";
    if (type === "payment") return "Thanh toán";
    if (type === "schedule") return "Lịch trình";
    if (type === "tour") return "Tour";
    return "Hệ thống";
  }

  function imagePreviewFor(item) {
    var raw = String(item && item.image_preview_url ? item.image_preview_url : "").trim();
    if (!raw || !/^(https?:\/\/|\/)/i.test(raw) || raw.indexOf("//") === 0) return "";
    var alt = String(item && item.image_preview_alt ? item.image_preview_alt : "Ảnh trong tin nhắn");
    return '<img class="notification-panel-message-preview" src="' + escapeHtml(raw) + '" alt="' + escapeHtml(alt) + '" loading="lazy" decoding="async" />';
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

    body.insertBefore(toolbar, body.firstChild);

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
    ensureToolbar();
    var list = document.getElementById("notification-panel-list");
    if (!list) return;

    var data = result && result.data ? result.data : {};
    var items = flatten(data);
    currentItems = items;
    var unread = result && typeof result.unread_count !== "undefined" ? Number(result.unread_count) : countUnread(items);
    updateBadges(unread);

    if (!items.length) {
      list.innerHTML = '<div class="notification-panel-state">Chưa có thông báo mới.</div>';
      return;
    }

    var html = "";
    for (var i = 0; i < items.length; i++) {
      var item = items[i] || {};
      var url = item.url && item.url !== "/notifications" ? item.url : "";
      var id = item.id || "";
      var readClass = item.is_read ? " is-read" : " is-unread";
      var isFriendRequest = item.type === "friend" && item.request_email;
      var tag = isFriendRequest || !url ? "div" : "a";
      var href = tag === "a" ? ' href="' + escapeHtml(url) + '"' : "";
      var actions = isFriendRequest
        ? '<div class="notification-friend-actions">' +
          '<button type="button" class="notification-friend-action-btn accept" data-request-email="' + escapeHtml(item.request_email) + '" data-action="accepted" aria-label="Đồng ý" title="Đồng ý">' + inlineIcon("check") + '</button>' +
          '<button type="button" class="notification-friend-action-btn decline" data-request-email="' + escapeHtml(item.request_email) + '" data-action="declined" aria-label="Từ chối" title="Từ chối">' + inlineIcon("x") + '</button>' +
          '</div>'
        : "";

      html += '<' + tag + ' class="notification-panel-item' + readClass + '" data-notification-id="' + escapeHtml(id) + '"' + href + '>' +
        '<div class="notification-panel-content">' +
        '<div class="notification-panel-type">' + typeName(item.type) + (item.is_read ? '' : '<span class="notification-unread-dot">Mới</span>') + '</div>' +
        '<h4>' + escapeHtml(item.title || "Thông báo") + '</h4>' +
        '<p>' + escapeHtml(item.content || "Có thông báo mới.") + '</p>' +
        imagePreviewFor(item) +
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

    notificationRequest = fetch(API_URL, { credentials: "same-origin", headers: authHeaders(false) }).then(function (response) {
      if (response.status === 401) {
        updateBadges(0);
        return null;
      }
      if (!response.ok) throw new Error("Không tải được thông báo (" + response.status + ")");
      return response.json();
    }).then(function (result) {
      if (result) saveCache(result);
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
    if (!requestEmail || !action) return false;

    var item = button.closest(".notification-panel-item");
    var buttons = item ? item.querySelectorAll(".notification-friend-action-btn") : [];
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = true;

    var formData = new FormData();
    formData.append("request_email", requestEmail);
    formData.append("action", action);

    fetch("/api/friend_requests", {
      method: "POST",
      credentials: "same-origin",
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

  function persistReadState(ids, keepalive) {
    ids = (ids || []).filter(Boolean).map(String).filter(function (id, index, list) { return list.indexOf(id) === index; });
    if (!ids.length) return Promise.resolve({ success: true, read_count: 0 });
    return notificationMutation("/read", { ids: ids }, keepalive);
  }

  function applyReadToElement(element) {
    if (!element || element.classList.contains("is-read")) return;
    element.classList.remove("is-unread");
    element.classList.add("is-read");
    var dot = element.querySelector(".notification-unread-dot");
    if (dot && dot.parentNode) dot.parentNode.removeChild(dot);
  }

  function markOneRead(id, element) {
    if (!id || !element || element.classList.contains("is-read")) return Promise.resolve();
    return persistReadState([id], true).then(function () {
      applyReadToElement(element);
      invalidateCache();
      updateBadges(Math.max(0, currentUnread - 1));
      window.dispatchEvent(new CustomEvent("travelwai:notifications-read", { detail: { ids: [id] } }));
    }).catch(function (error) {
      window.TravelwAINotify?.error?.(error.message || "Không lưu được trạng thái đã đọc.", { persist: false });
    });
  }

  function markAllRead(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    var btn = document.getElementById("notification-read-all-btn");
    if (btn) {
      btn.disabled = true;
      btn.textContent = "Đang xử lý...";
    }

    var ids = [];
    var items = document.querySelectorAll(".notification-panel-item.is-unread");
    for (var i = 0; i < items.length; i++) {
      var id = items[i].getAttribute("data-notification-id");
      if (id) ids.push(id);
    }

    return persistReadState(ids, false).then(function () {
      for (var index = 0; index < items.length; index++) applyReadToElement(items[index]);
      invalidateCache();
      updateBadges(0);
      window.dispatchEvent(new CustomEvent("travelwai:notifications-read", { detail: { ids: ids } }));
    }).catch(function (error) {
      window.TravelwAINotify?.error?.(error.message || "Không đánh dấu được thông báo.", { persist: false });
    }).finally(function () {
      if (btn) {
        btn.disabled = false;
        btn.textContent = "Đã đọc tất cả";
      }
    });
  }

  function openNotificationPanel(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    var panel = getPanel();
    if (!panel) return false;
    window.closeFeedbackPanel?.();
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

  window.addEventListener("travelwai:notification-created", function () {
    notificationRequest = null;
    invalidateCache();
    loadNotifications(true, Boolean(getPanel()?.classList.contains("open")));
  });

  window.addEventListener("travelwai:notifications-cleared", function () {
    notificationRequest = null;
    invalidateCache();
    clearPanelView();
  });

  window.openNotificationPanel = openNotificationPanel;
  window.closeNotificationPanel = closeNotificationPanel;
  window.loadTravelwainotifications = loadNotifications;
  window.markAllNotificationsRead = markAllRead;

  function bindNotificationTriggers() {
    ensureToolbar();
    var triggers = document.querySelectorAll('.notification-icon-container, [data-notification-panel-trigger], #notificationIconContainer');
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

    var trigger = target.closest ? target.closest('.notification-icon-container, [data-notification-panel-trigger], #notificationIconContainer') : null;
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
