(function () {
  const storageKey = "travelwaiTheme";
  const CACHE_CLEAR_ICON = "🧹";
  const CACHE_DONE_ICON = "✅";
  const OLD_PROVINCE_INFO_KEY = "travelwai_static_province_info_34_v7";
  const NOTIFICATION_CACHE_PREFIX = "travelwai:notifications:cache:";
  const NOTIFICATION_READ_PREFIX = "travelwai:notifications:read:";
  const NOTIFICATION_DELETED_KEY = "travelwai:notifications:deleted:v1";

  const MINI_CHAT_CONFIGS = {
    travelwai: {
      key: "travelwai",
      mode: "travelwai",
      buttonIcon: "👩‍💼",
      title: "Quản lý TravelwAI",
      subtitle: "Hỏi nhanh cách dùng website",
      avatar: "/logo/travelwai-manager-avatar.webp",
      welcome: "Xin chào, mình là Quản lý TravelwAI. Bạn cần hỗ trợ gì trên website?"
    },
    guide: {
      key: "guide",
      mode: "guide",
      buttonIcon: "🧭",
      title: "Hướng dẫn viên Travelwinne",
      subtitle: "Hỏi về văn hoá, lịch sử, lễ hội",
      avatar: "/logo/travelwinne-guide-avatar.webp",
      welcome: "Xin chào, mình là Hướng dẫn viên Travelwinne. Bạn muốn khám phá địa danh, lễ hội hay câu chuyện lịch sử nào?"
    }
  };

  const miniChatHistories = { travelwai: [], guide: [] };
  let activeMiniChatKey = "travelwai";

  function readTheme() {
    try {
      const saved = localStorage.getItem(storageKey);
      return saved === "dark" ? "dark" : "light";
    } catch {
      return "light";
    }
  }

  function saveTheme(theme) {
    try { localStorage.setItem(storageKey, theme); } catch { }
  }

  function setButtonState(button, theme) {
    if (!button) return;
    const isDark = theme === "dark";
    button.textContent = isDark ? "☀️" : "🌙";
    button.setAttribute("aria-label", isDark ? "Chuyển sang nền sáng" : "Chuyển sang nền tối");
    button.setAttribute("title", isDark ? "Chuyển sang nền sáng" : "Chuyển sang nền tối");
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-travelwai-theme", theme);
    if (document.body) {
      document.body.classList.toggle("travelwai-theme-dark", theme === "dark");
      document.body.classList.toggle("travelwai-theme-light", theme !== "dark");
    }
    saveTheme(theme);
    setButtonState(document.getElementById("travelwaiThemeToggle"), theme);
  }

  function isOldProvinceCacheKey(key) {
    return /^travelwai_static_province_info_34(?:_v\d+)?$/i.test(key) && key !== OLD_PROVINCE_INFO_KEY;
  }

  function isSafeCacheKeyToRemove(key) {
    if (!key) return false;
    const normalizedKey = String(key).toLowerCase();
    return normalizedKey === "twai_cache_conversations"
      || normalizedKey.startsWith("travelwai:notifications:cache:")
      || normalizedKey.startsWith("travelwai:notifications:read:")
      || normalizedKey.startsWith("travelwai:notifications:deleted:")
      || /^travelwai:[^:]*cache[^:]*:/i.test(key)
      || isOldProvinceCacheKey(key);
  }

  function clearRecommendedLocalCache() {
    let removed = 0;
    try {
      for (let index = localStorage.length - 1; index >= 0; index -= 1) {
        const key = localStorage.key(index);
        if (isSafeCacheKeyToRemove(key)) {
          localStorage.removeItem(key);
          removed += 1;
        }
      }
    } catch { }
    return removed;
  }

  function readCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) return decodeURIComponent(parts.pop().split(";").shift() || "");
    return "";
  }

  function getNotificationOwner() {
    try { return (localStorage.getItem("userEmail") || "guest").toLowerCase(); } catch { return "guest"; }
  }

  function getNotificationDeletedKey() {
    return NOTIFICATION_DELETED_KEY + ":" + getNotificationOwner();
  }

  function getAuthToken() {
    try {
      return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || readCookie("TravelwAIAuth") || "";
    } catch {
      return "";
    }
  }

  function readIdList(key) {
    try {
      const raw = localStorage.getItem(key);
      const list = raw ? JSON.parse(raw) : [];
      return Array.isArray(list) ? list.filter(Boolean).map(String) : [];
    } catch { return []; }
  }

  function saveIdList(key, list) {
    try {
      const seen = new Set();
      const ids = [];
      (list || []).forEach(function (id) {
        id = String(id || "");
        if (!id || seen.has(id)) return;
        seen.add(id);
        ids.push(id);
      });
      localStorage.setItem(key, JSON.stringify(ids.slice(-1200)));
    } catch { }
  }

  function collectNotificationIdsFromResult(result) {
    const data = result && result.data ? result.data : {};
    const groups = [data.schedules, data.friends, data.messages, data.systems, data.all];
    const seen = new Set();
    const ids = [];
    groups.forEach(function (group) {
      if (!Array.isArray(group)) return;
      group.forEach(function (item) {
        const id = item && item.id ? String(item.id) : "";
        if (!id || seen.has(id)) return;
        seen.add(id);
        ids.push(id);
      });
    });
    return ids;
  }

  function collectNotificationIdsFromLocalCache() {
    const owner = getNotificationOwner();
    const ids = [];
    try {
      for (let index = 0; index < localStorage.length; index += 1) {
        const key = localStorage.key(index) || "";
        const normalizedKey = key.toLowerCase();
        if (!normalizedKey.startsWith(NOTIFICATION_CACHE_PREFIX)) continue;
        if (owner && !normalizedKey.endsWith(":" + owner)) continue;
        const raw = localStorage.getItem(key);
        const cached = raw ? JSON.parse(raw) : null;
        ids.push.apply(ids, collectNotificationIdsFromResult((cached && cached.value) || cached));
      }
    } catch { }
    return ids;
  }

  function collectNotificationIdsFromDom() {
    const ids = [];
    try {
      const nodes = document.querySelectorAll(".notification-item[data-notification-id], .notification-panel-item[data-notification-id]");
      nodes.forEach(function (node) {
        const id = node.getAttribute("data-notification-id");
        if (id) ids.push(String(id));
      });
    } catch { }
    return ids;
  }

  function clearNotificationViews() {
    try { if (window.clearTravelwAINotificationsPage) window.clearTravelwAINotificationsPage(); } catch { }
    try { if (window.clearTravelwAINotificationsLocal) window.clearTravelwAINotificationsLocal(); } catch { }
    resetNotificationUiAfterClear();
    try { window.dispatchEvent(new CustomEvent("travelwai:notifications-cleared")); } catch { }
  }

  function rememberDeletedNotificationIds(ids) {
    if (!ids || !ids.length) return 0;
    const key = getNotificationDeletedKey();
    const oldIds = readIdList(key);
    const all = oldIds.concat(ids);
    saveIdList(key, all);
    return new Set(all.filter(Boolean).map(String)).size;
  }

  function removeNotificationCacheAndReadKeys() {
    let removed = 0;
    try {
      for (let index = localStorage.length - 1; index >= 0; index -= 1) {
        const key = localStorage.key(index) || "";
        const normalizedKey = key.toLowerCase();
        if (normalizedKey.startsWith(NOTIFICATION_CACHE_PREFIX) || normalizedKey.startsWith(NOTIFICATION_READ_PREFIX)) {
          localStorage.removeItem(key);
          removed += 1;
        }
      }
    } catch { }
    return removed;
  }

  function fetchCurrentNotificationIds() {
    const token = getAuthToken();
    if (!token) return Promise.resolve([]);
    return fetch("/api/notifications", {
      cache: "no-store",
      headers: { "Authorization": "Bearer " + token }
    }).then(function (response) {
      if (!response.ok) return [];
      return response.json();
    }).then(function (result) {
      return collectNotificationIdsFromResult(result);
    }).catch(function () { return []; });
  }

  function clearNotificationsInDatabase(ids) {
    const token = getAuthToken();
    if (!token) return Promise.resolve({ success: false, deleted_count: 0 });
    return fetch("/api/notifications/clear", {
      method: "POST",
      cache: "no-store",
      headers: {
        "Authorization": "Bearer " + token,
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ ids: Array.from(new Set((ids || []).filter(Boolean).map(String))) })
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (result) {
        if (!response.ok || result.success === false) throw new Error(result.detail || result.message || "Không xoá được thông báo trong database.");
        return result;
      });
    });
  }

  function resetNotificationUiAfterClear() {
    const badges = document.querySelectorAll("#notificationBadge, .notification-badge");
    badges.forEach(function (badge) {
      badge.textContent = "0";
      badge.style.display = "none";
    });

    const unreadText = document.getElementById("notification-unread-count");
    if (unreadText) unreadText.textContent = "0";

    const panelSummary = document.getElementById("notification-panel-summary");
    if (panelSummary) panelSummary.innerHTML = '<span>📅 0</span><span>👥 0</span><span>💬 0</span><span>⚙️ 0</span>';

    const panelList = document.getElementById("notification-panel-list");
    if (panelList) panelList.innerHTML = '<div class="notification-panel-state">Đã xoá thông báo cũ.</div>';

    const pageCounters = ["scheduleCount", "friendCount", "messageCount", "systemCount", "notificationPageUnreadCount"];
    pageCounters.forEach(function (id) {
      const element = document.getElementById(id);
      if (element) element.textContent = "0";
    });

    const pageLists = document.querySelectorAll("#scheduleNotificationList, #friendNotificationList, #messageNotificationList, #systemNotificationList");
    pageLists.forEach(function (list) {
      list.innerHTML = '<div class="notification-empty">Đã xoá thông báo cũ.</div>';
    });
  }

  function clearRecommendedBrowserCache() {
    if (!window.caches || typeof window.caches.keys !== "function") return Promise.resolve(0);
    return window.caches.keys().then(function (keys) {
      const removable = (keys || []).filter(function (key) {
        const normalizedKey = String(key || "").toLowerCase();
        return normalizedKey.includes("travelwai") || normalizedKey.includes("twai") || normalizedKey.includes("css") || normalizedKey.includes("static");
      });
      return Promise.all(removable.map(function (key) {
        return window.caches.delete(key).then(function (deleted) { return deleted ? 1 : 0; }).catch(function () { return 0; });
      })).then(function (items) {
        return items.reduce(function (total, value) { return total + value; }, 0);
      });
    }).catch(function () { return 0; });
  }

  function setCacheButtonState(button, done) {
    if (!button) return;
    button.textContent = done ? CACHE_DONE_ICON : CACHE_CLEAR_ICON;
    button.setAttribute("aria-label", done ? "Đã dọn dẹp" : "Dọn dẹp");
    button.setAttribute("title", done ? "Đã dọn dẹp" : "Dọn dẹp");
  }

  function ensureFloatingToolsHost() {
    let host = document.getElementById("travelwaiFloatingTools");
    if (!host) {
      host = document.createElement("div");
      host.id = "travelwaiFloatingTools";
      host.className = "twai-floating-tools-zone";
      host.setAttribute("aria-label", "Công cụ nhanh TravelwAI");
      document.body.appendChild(host);
    }
    return host;
  }

  function ensureCacheButton(host) {
    let button = document.getElementById("travelwaiCacheClearButton");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiCacheClearButton";
      button.className = "twai-theme-toggle twai-tool-action twai-cache-clear-toggle";
      button.type = "button";
      host.appendChild(button);
    } else if (button.parentElement !== host) {
      host.appendChild(button);
    }

    setCacheButtonState(button, false);
    button.addEventListener("click", function () {
      button.disabled = true;
      const localIds = collectNotificationIdsFromLocalCache().concat(collectNotificationIdsFromDom());
      fetchCurrentNotificationIds().then(function (serverIds) {
        const ids = Array.from(new Set(localIds.concat(serverIds).filter(Boolean).map(String)));
        rememberDeletedNotificationIds(ids);
        return clearNotificationsInDatabase(ids).catch(function () { return { success: false }; });
      }).finally(function () {
        removeNotificationCacheAndReadKeys();
        clearRecommendedLocalCache();
        if (window.invalidateTravelwAINotificationCache) window.invalidateTravelwAINotificationCache();
        clearNotificationViews();
        clearRecommendedBrowserCache().finally(function () {
          setCacheButtonState(button, true);
          window.setTimeout(function () {
            setCacheButtonState(button, false);
            button.disabled = false;
          }, 1200);
        });
      });
    });
    return button;
  }

  function ensureThemeButton(host) {
    let button = document.getElementById("travelwaiThemeToggle");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiThemeToggle";
      button.className = "twai-theme-toggle twai-tool-action";
      button.type = "button";
    }

    if (button.parentElement !== host) host.appendChild(button);

    setButtonState(button, readTheme());
    button.addEventListener("click", function () {
      const current = document.documentElement.getAttribute("data-travelwai-theme") === "dark" ? "dark" : "light";
      applyTheme(current === "dark" ? "light" : "dark");
    });
    return button;
  }

  function ensureMiniChatButton(host, key) {
    const config = MINI_CHAT_CONFIGS[key];
    let button = document.getElementById("travelwaiMiniChatButton-" + key);
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiMiniChatButton-" + key;
      button.className = "twai-theme-toggle twai-tool-action twai-mini-chat-toggle twai-mini-chat-toggle-" + key;
      button.type = "button";
      button.textContent = "";
      button.setAttribute("aria-label", "Nhắn với " + config.title);
      button.setAttribute("title", "Nhắn với " + config.title);
      button.addEventListener("click", function () { openMiniChat(key); });
      host.appendChild(button);
    } else if (button.parentElement !== host) {
      host.appendChild(button);
    }
    button.textContent = "";
    return button;
  }

  function ensureMiniChatPanel() {
    let panel = document.getElementById("travelwaiMiniChatPanel");
    if (panel) return panel;

    panel = document.createElement("aside");
    panel.id = "travelwaiMiniChatPanel";
    panel.className = "twai-mini-chat-panel";
    panel.setAttribute("aria-hidden", "true");
    panel.innerHTML = `
      <div class="twai-mini-chat-header">
        <div class="twai-mini-chat-user">
          <img loading="lazy" decoding="async" id="twaiMiniChatAvatar" src="/logo/travelwai-manager-avatar.webp" alt="" />
          <div>
            <strong id="twaiMiniChatTitle">Quản lý TravelwAI</strong>
            <span id="twaiMiniChatSubtitle">Hỏi nhanh cách dùng website</span>
          </div>
        </div>
        <button type="button" class="twai-mini-chat-close" id="twaiMiniChatClose" aria-label="Đóng">×</button>
      </div>
      <div class="twai-mini-chat-messages" id="twaiMiniChatMessages"></div>
      <form class="twai-mini-chat-form" id="twaiMiniChatForm">
        <input id="twaiMiniChatInput" maxlength="800" autocomplete="off" placeholder="Nhập tin nhắn..." />
        <button type="submit" aria-label="Gửi">➤</button>
      </form>`;
    document.body.appendChild(panel);

    panel.querySelector("#twaiMiniChatClose")?.addEventListener("click", closeMiniChat);
    panel.querySelector("#twaiMiniChatForm")?.addEventListener("submit", sendMiniChatMessage);
    return panel;
  }

  function openMiniChat(key) {
    activeMiniChatKey = MINI_CHAT_CONFIGS[key] ? key : "travelwai";
    const config = MINI_CHAT_CONFIGS[activeMiniChatKey];
    const panel = ensureMiniChatPanel();
    const avatar = document.getElementById("twaiMiniChatAvatar");
    const title = document.getElementById("twaiMiniChatTitle");
    const subtitle = document.getElementById("twaiMiniChatSubtitle");
    if (avatar) avatar.src = config.avatar;
    if (title) title.textContent = config.title;
    if (subtitle) subtitle.textContent = config.subtitle;
    panel.classList.add("open");
    panel.setAttribute("aria-hidden", "false");
    renderMiniChatMessages();
    window.setTimeout(function () { document.getElementById("twaiMiniChatInput")?.focus(); }, 80);
  }

  function closeMiniChat() {
    const panel = document.getElementById("travelwaiMiniChatPanel");
    if (!panel) return;
    panel.classList.remove("open");
    panel.setAttribute("aria-hidden", "true");
  }

  function renderMiniChatMessages() {
    const list = document.getElementById("twaiMiniChatMessages");
    const config = MINI_CHAT_CONFIGS[activeMiniChatKey];
    if (!list || !config) return;
    const history = miniChatHistories[activeMiniChatKey] || [];
    const rows = [{ role: "assistant", content: config.welcome }].concat(history);
    list.innerHTML = rows.map(function (message) {
      const role = message.role === "user" ? "user" : "assistant";
      return `<div class="twai-mini-chat-message ${role}">${escapeHtml(message.content || "")}</div>`;
    }).join("");
    list.scrollTop = list.scrollHeight;
  }

  function escapeHtml(value) {
    return String(value || "").replace(/[&<>"']/g, function (char) {
      return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char] || char;
    });
  }

  function buildMiniChatHistoryForApi(key) {
    return (miniChatHistories[key] || []).slice(-10).map(function (item) {
      return { role: item.role === "assistant" ? "assistant" : "user", content: item.content || "" };
    });
  }

  function normalizeMiniChatText(value) {
    return String(value || "")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/đ/g, "d")
      .replace(/Đ/g, "D")
      .toLowerCase()
      .replace(/[^a-z0-9@._+\-\s]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function isMiniChatManagerConfirmText(text) {
    return /^(ok|oke|okay|duoc|dong y|xac nhan|chap nhan|uh|u|co|yes|y|di|mo di|chuyen di|lam di|tiep tuc)$/.test(normalizeMiniChatText(text));
  }

  function getLastMiniChatManagerReplyText() {
    const history = miniChatHistories.travelwai || [];
    for (let index = history.length - 1; index >= 0; index -= 1) {
      const message = history[index];
      if (message && message.role === "assistant" && message.content) {
        return String(message.content || "");
      }
    }
    return "";
  }

  function getMiniChatNavigationTargetFromText(text) {
    const normalized = normalizeMiniChatText(text);
    if (!normalized) return null;

    if (/dang\s*nhap|login/.test(normalized)) {
      return { type: "navigate", url: "/login", reply: "Mình chuyển bạn qua trang Đăng nhập." };
    }

    if (/dang\s*ky|tao\s*tai\s*khoan|register|sign\s*up|signup/.test(normalized)) {
      return { type: "navigate", url: "/signup", reply: "Mình chuyển bạn qua trang Đăng ký." };
    }

    if (/quen\s*mat\s*khau|khoi\s*phuc\s*mat\s*khau|lay\s*lai\s*mat\s*khau|forgot\s*password|reset\s*password/.test(normalized)) {
      return { type: "navigate", url: "/forgot-password", reply: "Mình chuyển bạn qua trang Quên mật khẩu." };
    }

    const rules = [
      { url: "/tour-sales", reply: "Mình chuyển bạn đến trang Sales.", patterns: [/tour\s*sales/, /trang\s*sales/, /qua\s*sales/, /sales/, /ban\s*tour/, /don\s*ban\s*tour/] },
      { url: "/admin", reply: "Mình chuyển bạn đến trang Admin.", patterns: [/admin/, /quan\s*tri/, /quan\s*ly\s*he\s*thong/] },
      { url: "/schedule", reply: "Mình chuyển bạn đến trang Lịch trình.", patterns: [/lap\s*lich\s*trinh/, /tao\s*lich\s*trinh/, /lich\s*trinh/] },
      { url: "/plans", reply: "Mình chuyển bạn đến trang Kế hoạch.", patterns: [/lap\s*ke\s*hoach/, /tao\s*ke\s*hoach/, /ke\s*hoach/] },
      { url: "/provinces", reply: "Mình chuyển bạn đến Bản đồ Việt Nam.", patterns: [/ban\s*do/, /tinh\s*thanh/, /34\s*tinh/, /viet\s*nam/] },
      { url: "/posts", reply: "Mình chuyển bạn đến trang Bài viết.", patterns: [/bai\s*viet/, /tin\s*du\s*lich/, /kham\s*pha\s*bai/] },
      { url: "/tours", reply: "Mình chuyển bạn đến trang Tour du lịch.", patterns: [/tour\s*du\s*lich/, /dat\s*tour/, /xem\s*tour/, /qua\s*tour/, /trang\s*tour/, /^tour$/, /\btour\b/] },
      { url: "/messaging", reply: "Mình đang mở trang Nhắn tin.", patterns: [/tin\s*nhan/, /nhan\s*tin/, /messaging/, /chat/] },
      { url: "/profile", reply: "Mình chuyển bạn đến trang Hồ sơ.", patterns: [/ho\s*so/, /thong\s*tin\s*ca\s*nhan/, /tai\s*khoan/, /doi\s*ten/] },
      { url: "/notifications", reply: "Mình chuyển bạn đến trang Thông báo.", patterns: [/thong\s*bao/, /notification/] },
      { url: "/contact", reply: "Mình chuyển bạn đến trang Phản hồi.", patterns: [/phan\s*hoi/, /lien\s*he/, /gop\s*y/, /ho\s*tro/] },
      { url: "/home", reply: "Mình chuyển bạn về trang chủ.", patterns: [/trang\s*chu/, /home/] },
      { url: "/landing", reply: "Mình chuyển bạn về trang giới thiệu TravelwAI.", patterns: [/landing/, /gioi\s*thieu/, /trang\s*gioi\s*thieu/] }
    ];

    return rules.find(function (rule) {
      return rule.patterns.some(function (pattern) { return pattern.test(normalized); });
    }) || null;
  }

  function getMiniChatManagerTarget(text) {
    const normalized = normalizeMiniChatText(text);

    if (/dang\s*xuat|thoat\s*tai\s*khoan|log\s*out/.test(normalized)) {
      return { type: "logout", reply: "Mình sẽ đăng xuất tài khoản cho bạn." };
    }

    if (/doi\s*mat\s*khau|doi\s*password|change\s*password/.test(normalized)) {
      return { type: "navigate", url: "/profile", password: true, reply: "Mình chuyển bạn đến Hồ sơ để đổi mật khẩu." };
    }

    if (/(co\s*)?trang\s*nao|danh\s*sach\s*trang|menu|chuc\s*nang|huong\s*dan\s*(web|website)?/.test(normalized)) {
      return {
        type: "info",
        reply: "TravelwAI có các trang: Đăng nhập, Đăng ký, Trang chủ, Lịch trình, Kế hoạch, Bản đồ Việt Nam, Nhắn tin, Bài viết, Tour du lịch, Hồ sơ, Thông báo, Sales và Admin. Bạn nhắn tên trang, mình sẽ mở ngay."
      };
    }

    return getMiniChatNavigationTargetFromText(text);
  }

  function runMiniChatManagerAction(target) {
    if (!target) return false;

    if (target.password) {
      try { sessionStorage.setItem("travelwaiOpenProfilePassword", "1"); } catch { }
    }

    if (target.type === "logout") {
      window.setTimeout(function () {
        try { localStorage.clear(); } catch { }
        try { sessionStorage.clear(); } catch { }
        window.location.href = "/";
      }, 550);
      return true;
    }

    if (target.url) {
      window.setTimeout(function () {
        window.location.href = target.url;
      }, 650);
      return true;
    }

    return target.type === "info";
  }

  function tryHandleMiniChatManagerCommand(text) {
    if (activeMiniChatKey !== "travelwai") return false;

    if (isMiniChatManagerConfirmText(text)) {
      const lastReply = getLastMiniChatManagerReplyText();
      const confirmedTarget = getMiniChatNavigationTargetFromText(lastReply);
      if (confirmedTarget) {
        pushMiniChat("assistant", confirmedTarget.reply);
        runMiniChatManagerAction(confirmedTarget);
        return true;
      }

      pushMiniChat("assistant", "Bạn nhắn tên trang hoặc chức năng muốn mở, ví dụ: đăng nhập, bản đồ, lịch trình, tour du lịch, sales, admin, nhắn tin, đổi mật khẩu.");
      return true;
    }

    const target = getMiniChatManagerTarget(text);
    if (!target) return false;

    pushMiniChat("assistant", target.reply);
    runMiniChatManagerAction(target);
    return true;
  }

  function pushMiniChat(role, content) {
    if (!miniChatHistories[activeMiniChatKey]) miniChatHistories[activeMiniChatKey] = [];
    miniChatHistories[activeMiniChatKey].push({ role: role === "user" ? "user" : "assistant", content: content || "" });
  }

  function sendMiniChatMessage(event) {
    event.preventDefault();
    const input = document.getElementById("twaiMiniChatInput");
    const form = document.getElementById("twaiMiniChatForm");
    const text = (input?.value || "").trim();
    if (!text) return;

    const token = getAuthToken();
    const config = MINI_CHAT_CONFIGS[activeMiniChatKey] || MINI_CHAT_CONFIGS.travelwai;

    pushMiniChat("user", text);
    if (input) input.value = "";
    renderMiniChatMessages();

    if (tryHandleMiniChatManagerCommand(text)) {
      renderMiniChatMessages();
      return;
    }

    if (!token && activeMiniChatKey === "travelwai") {
      pushMiniChat("assistant", "Bạn vui lòng đăng ký hoặc đăng nhập để Quản lý TravelwAI hỗ trợ đầy đủ các chức năng tài khoản, lịch trình, tour và tin nhắn.");
      renderMiniChatMessages();
      return;
    }

    if (form) form.classList.add("loading");

    const headers = { "Content-Type": "application/json" };
    if (token) headers.Authorization = "Bearer " + token;

    fetch("/api/ai/chat", {
      method: "POST",
      cache: "no-store",
      headers,
      body: JSON.stringify({
        message: text,
        assistant: config.mode,
        history: buildMiniChatHistoryForApi(activeMiniChatKey)
      })
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (result) {
        if (!response.ok || result.success === false) throw new Error(result.detail || result.message || "Trợ lý chưa trả lời được.");
        return result;
      });
    }).then(function (result) {
      const reply = result?.data?.reply || result?.reply || "Mình đã nhận được tin nhắn.";
      pushMiniChat("assistant", reply);
      if (activeMiniChatKey === "travelwai") {
        const target = getMiniChatManagerTarget(text) || getMiniChatNavigationTargetFromText(reply);
        if (target && target.type !== "info") runMiniChatManagerAction(target);
      }
    }).catch(function (error) {
      pushMiniChat("assistant", error.message || "Không gửi được tin nhắn.");
    }).finally(function () {
      if (form) form.classList.remove("loading");
      renderMiniChatMessages();
    });
  }

  applyTheme(readTheme());

  document.addEventListener("DOMContentLoaded", function () {
    const host = ensureFloatingToolsHost();
    ensureMiniChatButton(host, "travelwai");
    ensureMiniChatButton(host, "guide");
    ensureCacheButton(host);
    ensureThemeButton(host);
    ensureMiniChatPanel();
  });
})();
