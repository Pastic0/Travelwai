(function () {
  const API_BASE_URL = "/api";
  const AI_STORAGE_PREFIX = "travelwai-ai-chat-history";
  const AI_PENDING_PROMPT_KEY = "travelwai-ai-pending-prompt";
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
    welcome: "Xin chào, mình là Quản lý TravelwAI. Bạn có thể nhắn mình để mở trang, lập lịch trình, tìm tour, nhắn tin với người khác, đổi mật khẩu hoặc đăng xuất."
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
      { url: "/schedule", reply: "Mình chuyển bạn đến trang Lịch trình.", patterns: [/lap\s*lich\s*trinh/, /tao\s*lich\s*trinh/, /lich\s*trinh/] },
      { url: "/plans", reply: "Mình chuyển bạn đến trang Kế hoạch.", patterns: [/lap\s*ke\s*hoach/, /tao\s*ke\s*hoach/, /ke\s*hoach/] },
      { url: "/provinces", reply: "Mình chuyển bạn đến Bản đồ Việt Nam.", patterns: [/ban\s*do/, /tinh\s*thanh/, /34\s*tinh/, /viet\s*nam/] },
      { url: "/posts", reply: "Mình chuyển bạn đến trang Bài viết.", patterns: [/bai\s*viet/, /tin\s*du\s*lich/, /kham\s*pha\s*bai/] },
      { url: "/tours", reply: "Mình chuyển bạn đến trang Tour du lịch.", patterns: [/tour\s*du\s*lich/, /dat\s*tour/, /xem\s*tour/] },
      { url: "/tour-sales", reply: "Mình chuyển bạn đến trang Tài khoản.", patterns: [/tour\s*sales/, /ban\s*tour/, /don\s*ban\s*tour/, /sales/] },
      { url: "/admin", reply: "Mình chuyển bạn đến trang Admin.", patterns: [/admin/, /quan\s*tri/, /quan\s*ly\s*he\s*thong/] },
      { url: "/messaging", reply: "Mình đang mở trang Nhắn tin.", patterns: [/tin\s*nhan/, /nhan\s*tin/, /messaging/, /chat/] },
      { url: "/profile", reply: "Mình chuyển bạn đến trang Hồ sơ.", patterns: [/ho\s*so/, /thong\s*tin\s*ca\s*nhan/, /tai\s*khoan/, /doi\s*ten/] },
      { url: "/notifications", reply: "Mình chuyển bạn đến trang Thông báo.", patterns: [/thong\s*bao/, /notification/] },
      { url: "/contact", reply: "Bạn đang ở chatbot Admin rồi. Cứ nhắn nội dung cần hỗ trợ tại đây nhé.", patterns: [/phan\s*hoi/, /lien\s*he/, /gop\s*y/, /ho\s*tro/] },
      { url: "/home", reply: "Mình chuyển bạn về trang chủ.", patterns: [/trang\s*chu/, /home/] },
      { url: "/landing", reply: "Mình chuyển bạn về trang giới thiệu TravelwAI.", patterns: [/landing/, /gioi\s*thieu/, /trang\s*gioi\s*thieu/] }
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
      return { type: "logout", reply: "Mình sẽ đăng xuất tài khoản cho bạn." };
    }

    if (/doi\s*mat\s*khau|doi\s*password|change\s*password/.test(normalized)) {
      return { type: "navigate", url: "/profile", password: true, reply: "Mình chuyển bạn đến Hồ sơ để đổi mật khẩu." };
    }

    if (/(co|có)?\s*trang\s*nao|danh\s*sach\s*trang|menu|chuc\s*nang|huong\s*dan\s*(web|website)?/.test(normalized)) {
      return {
        type: "info",
        reply: "TravelwAI có các trang: Đăng nhập, Đăng ký, Trang chủ, Lịch trình, Kế hoạch, Bản đồ Việt Nam, Nhắn tin, Bài viết, Tour du lịch, Hồ sơ, Thông báo, Tài khoản Sales và Admin. Bạn nhắn tên trang, mình sẽ mở ngay."
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
        if (confirmedTarget.url && confirmedTarget.url !== "/contact") {
          setTimeout(() => { window.location.href = confirmedTarget.url; }, 650);
        }
        return true;
      }
      appendLocalManagerReply("Bạn nhắn tên trang hoặc chức năng muốn mở, ví dụ: đăng nhập, bản đồ, lịch trình, tour du lịch, nhắn tin, đổi mật khẩu.");
      return true;
    }

    if (needsFullMessagingCommand(text)) {
      appendLocalManagerReply("Mình mở trang Nhắn tin để thực hiện đúng chức năng trò chuyện hoặc kết bạn cho bạn.");
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

    if (target.url && target.url !== "/contact") {
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
      button.title = isSending ? "AI đang trả lời" : "Gửi câu hỏi cho AI";
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

  async function sendAiMessage(event) {
    if (event) event.preventDefault();
    if (isSending) return;

    const input = document.getElementById("supportAdminAiInput");
    const text = (input?.value || "").trim();
    if (!text) {
      setSupportStatus("Nhập câu hỏi trước khi gửi cho AI.", "error");
      input?.focus();
      return;
    }

    const token = getToken();
    if (token) {
      await loadCurrentUser();
    }

    const storedBeforeSend = loadStoredMessages();
    const userMessage = {
      id: `contact-ai-user-${Date.now()}`,
      sender_id: getCurrentUserId() || "current-user",
      sender_info: currentUser || { username: getUserDisplayName(currentUser), email: localStorage.getItem("userEmail") || "" },
      content: text,
      time_sent: new Date().toISOString()
    };

    const afterUser = [...storedBeforeSend, userMessage];
    saveStoredMessages(afterUser);
    appendMessage(userMessage);
    if (input) input.value = "";
    setSupportStatus("", "");

    if (await handleManagerCommand(text)) return;

    if (!token) {
      appendLocalManagerReply("Bạn có thể nhắn tên trang để mình mở nhanh. Để dùng đầy đủ AI, lịch trình, tour và tin nhắn, bạn hãy đăng nhập trước.");
      return;
    }

    try {
      setSendingState(true);
      const response = await fetch(`${API_BASE_URL}/ai/chat`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${token}`
        },
        body: JSON.stringify({
          message: text,
          history: buildHistoryForRequest(storedBeforeSend),
          assistant: managerConfig.mode,
          context: ""
        })
      });

      const result = await response.json().catch(() => ({}));
      if (!response.ok || result.success === false) {
        throw new Error(result.detail || result.message || "Không thể gọi AI.");
      }

      let replyText = String(result.data?.reply || "").trim();
      if (!replyText || /openrouter|kh[oô]ng\s*tr[aả]\s*v[eề]\s*n[oộ]i\s*dung|kh[oô]ng\s*c[oó]\s*ph[aả]n\s*h[oồ]i/i.test(replyText)) {
        throw new Error("AI không có phản hồi rõ ràng.");
      }
      if (replyText.length > CONTACT_AI_REPLY_LIMIT + 30) {
        replyText = replyText.slice(0, CONTACT_AI_REPLY_LIMIT + 30).replace(/\s+\S*$/, "").trim();
      }

      const latestMessages = loadStoredMessages();
      const reply = buildManagerMessage(replyText);
      saveStoredMessages([...latestMessages, reply]);
      appendMessage(reply);
    } catch (error) {
      console.warn("Chatbot Admin không dùng được OpenRouter, chuyển sang trả lời nội bộ:", error);
      setSupportStatus("", "");
      appendLocalManagerReply("Mình chưa nhận diện được lệnh này. Bạn có thể nhắn: đăng nhập, đăng ký, bản đồ, lịch trình, kế hoạch, tour du lịch, bài viết, nhắn tin, đổi mật khẩu hoặc đăng xuất.");
    } finally {
      setSendingState(false);
      input?.focus();
    }
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
    const panel = getPanel();
    if (!panel) {
      window.location.href = "/messaging?ai=travelwai";
      return;
    }
    panel.classList.add("open");
    panel.setAttribute("aria-hidden", "false");
    await initializePanelContent();
    setTimeout(() => document.getElementById("supportAdminAiInput")?.focus(), 120);
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
