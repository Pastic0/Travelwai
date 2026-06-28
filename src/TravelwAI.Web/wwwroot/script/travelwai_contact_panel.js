(function () {
  const API_BASE_URL = "/api";
  const AI_STORAGE_PREFIX = "travelwai-ai-chat-history";
  const AI_PENDING_PROMPT_KEY = "travelwai-ai-pending-prompt";
  const SUPPORT_ADMIN_EMAIL = "2324802010387@student.tdmu.edu.vn";
  const ADMIN_PENDING_MESSAGE_KEY = "travelwai-admin-pending-message";
  const AI_AVATAR_VERSION_KEY = "travelwaiAiAvatarVersion";
  const CONTACT_AI_HISTORY_LIMIT = 100;
  const CONTACT_AI_REPLY_LIMIT = 150;

  let currentUser = null;
  let isSending = false;
  let initialized = false;

  const managerConfig = {
    key: "travelwai",
    mode: "travelwai",
    id: "travelwai-ai",
    displayName: "Quản lý TravelwAI",
    avatar: "travelwai-manager-avatar.webp",
    welcome: "Xin chào, hệ thống sẽ mở hội thoại nhắn tin với Admin chính TravelwAI."
  };

  function getPanel() {
    return document.getElementById("contact-panel");
  }

  function getToken() {
    return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || readCookie("TravelwAIAuth");
  }

  function readCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) return decodeURIComponent(parts.pop().split(";").shift() || "");
    return "";
  }

  function setSupportStatus(message, type) {
    const status = document.getElementById("supportAdminStatus");
    if (!status) return;
    status.textContent = message || "";
    status.hidden = !message;
    status.className = `support-admin-status ${type || ""}`.trim();
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function normalizeForSearch(value) {
    return String(value || "")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/đ/g, "d")
      .replace(/Đ/g, "D")
      .toLowerCase()
      .replace(/[^a-z0-9@._\s-]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function getAiAvatarVersion() {
    try {
      return localStorage.getItem(AI_AVATAR_VERSION_KEY) || "";
    } catch {
      return "";
    }
  }

  function buildManagerAvatarUrl() {
    const version = getAiAvatarVersion();
    return `/logo/${managerConfig.avatar}${version ? `?v=${encodeURIComponent(version)}` : ""}`;
  }

  function getCurrentUserId() {
    return currentUser?.id || currentUser?.localId || currentUser?.uid || currentUser?.user_id || localStorage.getItem("userId") || "";
  }

  function getUserDisplayName(user) {
    return user?.displayName || user?.username || user?.name || localStorage.getItem("username") || localStorage.getItem("userEmail") || "Bạn";
  }

  function getStorageOwnerKey() {
    return getCurrentUserId() || (currentUser?.email || localStorage.getItem("userEmail") || "guest").toLowerCase();
  }

  function getAiStorageKey() {
    return `${AI_STORAGE_PREFIX}:travelwai:${getStorageOwnerKey()}`;
  }

  function loadStoredMessages() {
    try {
      const raw = localStorage.getItem(getAiStorageKey());
      const parsed = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  function saveStoredMessages(messages) {
    try {
      const clean = (messages || []).filter((item) => !item.is_system_welcome).slice(-CONTACT_AI_HISTORY_LIMIT);
      localStorage.setItem(getAiStorageKey(), JSON.stringify(clean));
    } catch (_) {}
  }

  function buildWelcomeMessage() {
    return {
      id: "travelwai-contact-welcome",
      sender_id: managerConfig.id,
      sender_info: {
        id: managerConfig.id,
        username: managerConfig.displayName,
        displayName: managerConfig.displayName,
        profilePic: buildManagerAvatarUrl()
      },
      content: managerConfig.welcome,
      time_sent: new Date().toISOString(),
      is_system_welcome: true
    };
  }

  function getVisibleMessages() {
    const stored = loadStoredMessages();
    return stored.length ? stored : [buildWelcomeMessage()];
  }

  function formatTime(value) {
    const date = value ? new Date(value) : new Date();
    if (Number.isNaN(date.getTime())) return "";
    return date.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" });
  }

  function createMessageElement(message) {
    const isUser = message.sender_id !== managerConfig.id;
    const row = document.createElement("div");
    row.className = `admin-ai-message-row ${isUser ? "sent" : "received"}`;

    const avatar = document.createElement("div");
    avatar.className = "admin-ai-message-avatar";
    if (isUser) {
      avatar.textContent = (getUserDisplayName(currentUser) || "B").charAt(0).toUpperCase();
    } else {
      const img = document.createElement("img");
      img.src = buildManagerAvatarUrl();
      img.alt = managerConfig.displayName;
      avatar.appendChild(img);
    }

    const bubble = document.createElement("div");
    bubble.className = "admin-ai-message-bubble";
    bubble.innerHTML = `
      <div class="admin-ai-message-sender">${escapeHtml(isUser ? "Bạn" : managerConfig.displayName)}</div>
      <div class="admin-ai-message-text">${escapeHtml(message.content || "")}</div>
      <div class="admin-ai-message-time">${escapeHtml(formatTime(message.time_sent || message.timestamp))}</div>`;

    if (isUser) {
      row.appendChild(bubble);
      row.appendChild(avatar);
    } else {
      row.appendChild(avatar);
      row.appendChild(bubble);
    }
    return row;
  }

  function renderMessages() {
    const list = document.getElementById("supportAdminAiMessages");
    if (!list) return;
    list.innerHTML = "";
    getVisibleMessages().forEach((message) => list.appendChild(createMessageElement(message)));
    list.scrollTop = list.scrollHeight;
  }

  function appendMessage(message) {
    const list = document.getElementById("supportAdminAiMessages");
    if (!list) return;
    list.appendChild(createMessageElement(message));
    list.scrollTop = list.scrollHeight;
  }

  function buildHistoryForRequest(messages) {
    return (messages || [])
      .filter((message) => !message.is_system_welcome && message.content)
      .slice(-12)
      .map((message) => ({
        role: message.sender_id === managerConfig.id ? "assistant" : "user",
        content: message.content
      }));
  }

  function buildManagerMessage(text) {
    return {
      id: `contact-ai-reply-${Date.now()}`,
      sender_id: managerConfig.id,
      sender_info: {
        id: managerConfig.id,
        username: managerConfig.displayName,
        displayName: managerConfig.displayName,
        profilePic: buildManagerAvatarUrl()
      },
      content: text,
      time_sent: new Date().toISOString()
    };
  }

  function appendLocalManagerReply(text) {
    const messages = loadStoredMessages();
    const reply = buildManagerMessage(text);
    const next = [...messages, reply];
    saveStoredMessages(next);
    appendMessage(reply);
    return reply;
  }

  function getLastManagerReplyText() {
    const messages = loadStoredMessages();
    for (let index = messages.length - 1; index >= 0; index -= 1) {
      const message = messages[index];
      if (message?.sender_id === managerConfig.id && message.content && !message.is_system_welcome) {
        return String(message.content || "");
      }
    }
    return "";
  }

  function isConfirmText(text) {
    const normalized = normalizeForSearch(text);
    return /^(ok|oke|okay|duoc|dong y|xac nhan|chap nhan|uh|u|co|yes|y|di|mo di|chuyen di|lam di|tiep tuc)$/.test(normalized);
  }

  function getNavigationTargetFromText(text) {
    const normalized = normalizeForSearch(text);
    if (!normalized) return null;

  if (window.TravelwAIPageCommands && typeof window.TravelwAIPageCommands.parseManagerCommand === "function") {
    const command = window.TravelwAIPageCommands.parseManagerCommand(text);
    if (command && command.type === "navigate") return command;
    if (command) return null;
  }

    if (/dang\s*nhap|login/.test(normalized)) {
      return { type: "navigate", url: "/login", reply: "Đang mở trang Đăng nhập." };
    }

    if (/dang\s*ky|tao\s*tai\s*khoan|register|sign\s*up|signup/.test(normalized)) {
      return { type: "navigate", url: "/signup", reply: "Đang mở trang Đăng ký." };
    }

    if (/quen\s*mat\s*khau|khoi\s*phuc\s*mat\s*khau|lay\s*lai\s*mat\s*khau|forgot\s*password|reset\s*password/.test(normalized)) {
      return { type: "navigate", url: "/forgot-password", reply: "Đang mở trang Quên mật khẩu." };
    }

    const rules = [
      { url: "/pricing", reply: "Đang mở Bảng giá.", patterns: [/bang\s*gia/, /pricing/, /gia\s*goi/, /goi\s*tai\s*khoan/, /mua\s*goi/] },
      { url: "/cart", reply: "Đang mở Giỏ hàng.", patterns: [/gio\s*hang/, /cart/] },
      { url: "/checkout", reply: "Đang mở Thanh toán.", patterns: [/thanh\s*toan/, /checkout/, /xac\s*nhan\s*thanh\s*toan/, /qr\s*thanh\s*toan/] },
      { url: "/manage", reply: "Đang mở Manage.", patterns: [/manage/, /quan\s*ly\s*goi/, /quan\s*ly\s*don\s*goi/, /don\s*goi/] },
      { url: "/business", reply: "Đang mở Business.", patterns: [/business/, /trang\s*business/, /doanh\s*nghiep/, /kinh\s*doanh/] },
      { url: "/contact", reply: "Đang mở Liên hệ.", patterns: [/trang\s*lien\s*he/, /contact\s*page/, /lien\s*he\s*travelwai/] },
      { url: "/schedule", reply: "Đang mở trang Lịch trình.", patterns: [/lap\s*lich\s*trinh/, /tao\s*lich\s*trinh/, /lich\s*trinh/] },
      { url: "/plans", reply: "Đang mở trang Kế hoạch.", patterns: [/lap\s*ke\s*hoach/, /tao\s*ke\s*hoach/, /ke\s*hoach/] },
      { url: "/provinces", reply: "Đang mở Bản đồ Việt Nam.", patterns: [/ban\s*do/, /tinh\s*thanh/, /34\s*tinh/, /viet\s*nam/] },
      { url: "/posts", reply: "Đang mở trang Bài viết.", patterns: [/bai\s*viet/, /tin\s*du\s*lich/, /kham\s*pha\s*bai/] },
      { url: "/tours", reply: "Đang mở trang Tour du lịch.", patterns: [/tour\s*du\s*lich/, /dat\s*tour/, /xem\s*tour/] },
      { url: "/tour-sales", reply: "Đang mở trang Sales.", patterns: [/sales/, /ban\s*tour/, /don\s*ban\s*tour/, /sales/] },
      { url: "/admin", reply: "Đang mở trang Admin.", patterns: [/admin/, /quan\s*tri/, /quan\s*ly\s*he\s*thong/] },
      { url: "/messaging?admin=1", reply: `Đang mở Tin nhắn với Admin ${SUPPORT_ADMIN_EMAIL}.`, patterns: [/tin\s*nhan/, /nhan\s*tin/, /messaging/, /chat/] },
      { url: "/profile", reply: "Đang mở trang Hồ sơ.", patterns: [/ho\s*so/, /thong\s*tin\s*ca\s*nhan/, /tai\s*khoan/, /doi\s*ten/] },
      { url: "/notifications", reply: "Đang mở trang Thông báo.", patterns: [/thong\s*bao/, /notification/] },
      { url: "/messaging?admin=1", reply: "Đang mở hội thoại với Admin.", patterns: [/phan\s*hoi/, /lien\s*he/, /gop\s*y/, /ho\s*tro/] },
      { url: "/home", reply: "Đang mở trang chủ.", patterns: [/trang\s*chu/, /home/] },
      { url: "/landing", reply: "Đang mở giới thiệu TravelwAI.", patterns: [/landing/, /gioi\s*thieu/, /trang\s*gioi\s*thieu/] }
    ];

    return rules.find((rule) => rule.patterns.some((pattern) => pattern.test(normalized))) || null;
  }

  function getConfirmedNavigationTargetFromLastReply() {
    const lastReply = getLastManagerReplyText();
    if (!lastReply) return null;
    const normalized = normalizeForSearch(lastReply);
    if (!/(xac\s*nhan|dong\s*y|ban\s*muon|minh\s*chuyen|minh\s*mo|mo\s*trang|chuyen\s*ban)/.test(normalized)) return null;
    return getNavigationTargetFromText(normalized);
  }

  function getManagerNavigationTarget(text) {
    const normalized = normalizeForSearch(text);

    if (/dang\s*xuat|thoat\s*tai\s*khoan|log\s*out/.test(normalized)) {
      return { type: "logout", reply: "Đang đăng xuất tài khoản." };
    }

    if (/doi\s*mat\s*khau|doi\s*password|change\s*password/.test(normalized)) {
      return { type: "navigate", url: "/profile", password: true, reply: "Đang mở Hồ sơ để đổi mật khẩu." };
    }

    if (window.TravelwAIPageCommands && typeof window.TravelwAIPageCommands.parseManagerCommand === "function") {
      const command = window.TravelwAIPageCommands.parseManagerCommand(text);
      if (command) return command;
    }

    if (/(co|có)?\s*trang\s*nao|danh\s*sach\s*trang|menu|chuc\s*nang|huong\s*dan\s*(web|website)?/.test(normalized)) {
      return {
        type: "info",
        reply: "Các trang TravelwAI: Đăng nhập, Đăng ký, Quên mật khẩu, Đặt lại mật khẩu, Trang chủ, Giới thiệu, Bản đồ Việt Nam, Chi tiết tỉnh, Lịch trình, Kế hoạch, Bảng giá, Giỏ hàng, Thanh toán, Hồ sơ, Nhắn tin, Hỗ trợ Admin, Liên hệ, Thông báo, Bài viết, Tour du lịch, Sales, Business, Admin, Manage. Nhắn: mở [tên trang], tới trang [tên trang] hoặc chi tiết trang [tên trang]."
      };
    }

    return getNavigationTargetFromText(text);
  }

  function needsFullMessagingCommand(text) {
    const normalized = normalizeForSearch(text);
    return /(?:nhan\s*tin|chat|tro\s*chuyen|cuoc\s*tro\s*chuyen)\s*(?:voi|cung)/.test(normalized)
      || /(?:ket\s*ban|them\s*ban|loi\s*moi\s*ket\s*ban|yeu\s*cau\s*ket\s*ban)/.test(normalized);
  }

  async function handleManagerCommand(text) {
    if (isConfirmText(text)) {
      const confirmedTarget = getConfirmedNavigationTargetFromLastReply();
      if (confirmedTarget) {
        appendLocalManagerReply(confirmedTarget.reply);
        if (confirmedTarget.password) {
          sessionStorage.setItem("travelwaiOpenProfilePassword", "1");
        }
        if (confirmedTarget.url) {
          setTimeout(() => { window.location.href = confirmedTarget.url; }, 650);
        }
        return true;
      }
      appendLocalManagerReply("Dùng cú pháp: tới trang [tên trang], qua trang [tên trang] hoặc chi tiết trang [tên trang].");
      return true;
    }

    if (needsFullMessagingCommand(text)) {
      appendLocalManagerReply("Đang mở trang Nhắn tin.");
      try {
        localStorage.setItem(AI_PENDING_PROMPT_KEY, JSON.stringify({ assistant: "travelwai", prompt: text }));
      } catch (_) {}
      setTimeout(() => { window.location.href = "/messaging?ai=travelwai"; }, 650);
      return true;
    }

    const target = getManagerNavigationTarget(text);
    if (!target) return false;

    appendLocalManagerReply(target.reply);

    if (target.type === "logout") {
      setTimeout(() => {
        if (typeof logout === "function") logout();
        else {
          localStorage.clear();
          sessionStorage.clear();
          window.location.href = "/";
        }
      }, 550);
      return true;
    }

    if (target.password) {
      sessionStorage.setItem("travelwaiOpenProfilePassword", "1");
    }

    if (target.url) {
      setTimeout(() => { window.location.href = target.url; }, 650);
    }
    return true;
  }

  function setSendingState(value) {
    isSending = Boolean(value);
    const button = document.querySelector("#supportAdminAiForm .admin-ai-send-btn");
    const input = document.getElementById("supportAdminAiInput");
    if (button) {
      button.disabled = isSending;
      button.innerHTML = isSending ? '<span class="admin-ai-send-spinner" aria-hidden="true"></span>' : "Gửi";
      button.title = isSending ? "Đang mở Tin nhắn" : "Gửi tin nhắn cho Admin";
    }
    if (input) input.disabled = isSending;
  }

  async function loadCurrentUser() {
    if (currentUser) return currentUser;
    const token = getToken();
    if (!token) return null;
    try {
      const response = await fetch(`${API_BASE_URL}/profile`, {
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }
      });
      const result = await response.json().catch(() => ({}));
      if (response.ok && result.user) {
        currentUser = result.user;
        if (currentUser.email) localStorage.setItem("userEmail", currentUser.email);
        if (currentUser.username || currentUser.displayName) localStorage.setItem("username", currentUser.username || currentUser.displayName);
      }
    } catch (_) {}
    return currentUser;
  }

  function openAdminMessaging(message) {
    const text = String(message || "").trim();
    try {
      if (text) localStorage.setItem(ADMIN_PENDING_MESSAGE_KEY, text);
    } catch (_) {}
    window.location.href = "/messaging?admin=1";
  }

  async function sendAiMessage(event) {
    if (event) event.preventDefault();
    if (isSending) return;

    const input = document.getElementById("supportAdminAiInput");
    const text = (input?.value || "").trim();
    if (!text) {
      setSupportStatus("Nhập nội dung cần nhắn cho Admin chính trước.", "error");
      input?.focus();
      return;
    }

    const token = getToken();
    if (!token) {
      setSupportStatus("Bạn cần đăng nhập để nhắn tin với Admin chính.", "error");
      try { localStorage.setItem(ADMIN_PENDING_MESSAGE_KEY, text); } catch (_) {}
      setTimeout(() => { window.location.href = "/login"; }, 800);
      return;
    }

    await loadCurrentUser();
    const storedBeforeSend = loadStoredMessages();
    const userMessage = {
      id: `contact-admin-user-${Date.now()}`,
      sender_id: getCurrentUserId() || "current-user",
      sender_info: currentUser || { username: getUserDisplayName(currentUser), email: localStorage.getItem("userEmail") || "" },
      content: text,
      time_sent: new Date().toISOString()
    };
    saveStoredMessages([...storedBeforeSend, userMessage]);
    appendMessage(userMessage);
    if (input) input.value = "";
    setSupportStatus(`Đang mở Tin nhắn với Admin chính ${SUPPORT_ADMIN_EMAIL}...`, "success");
    openAdminMessaging(text);
  }

  async function initializePanelContent() {
    if (initialized) return;
    initialized = true;
    await loadCurrentUser();
    renderMessages();
  }

  async function openPanel(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    openAdminMessaging("");
  }

  function closePanel() {
    const panel = getPanel();
    if (!panel) return;
    panel.classList.remove("open");
    panel.setAttribute("aria-hidden", "true");
  }

  function bindEvents() {
    document.querySelectorAll('a[href="/contact"], [data-contact-panel-trigger]').forEach((trigger) => {
      trigger.addEventListener("click", openPanel);
    });

    document.querySelectorAll("[data-close-contact-panel]").forEach((btn) => {
      btn.addEventListener("click", function (event) {
        event.preventDefault();
        closePanel();
      });
    });

    document.querySelectorAll("#supportAdminAiForm").forEach((form) => {
      form.addEventListener("submit", sendAiMessage);
    });

    document.querySelectorAll("[data-admin-ai-suggestion]").forEach((button) => {
      button.addEventListener("click", function () {
        const input = document.getElementById("supportAdminAiInput");
        if (input) {
          input.value = button.getAttribute("data-admin-ai-suggestion") || button.textContent || "";
          input.focus();
        }
      });
    });

    document.addEventListener("click", function (event) {
      const panel = getPanel();
      if (!panel || !panel.classList.contains("open")) return;
      const clickedTrigger = event.target.closest('a[href="/contact"], [data-contact-panel-trigger]');
      if (!panel.contains(event.target) && !clickedTrigger) {
        closePanel();
      }
    });

    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") closePanel();
    });
  }

  document.addEventListener("DOMContentLoaded", bindEvents);

  window.addEventListener("storage", (event) => {
    if (event.key === AI_AVATAR_VERSION_KEY && getPanel()?.classList.contains("open")) {
      const avatar = document.querySelector(".admin-ai-header-avatar");
      if (avatar) avatar.src = buildManagerAvatarUrl();
      renderMessages();
    }
  });
})();
