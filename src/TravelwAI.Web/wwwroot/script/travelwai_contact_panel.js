(function () {
  const API_BASE_URL = "/api";
  const SUPPORT_STORAGE_PREFIX = "travelwai-admin-support-history";
  const SUPPORT_ADMIN_EMAIL = "2324802010387@student.tdmu.edu.vn";
  const ADMIN_PENDING_MESSAGE_KEY = "travelwai-admin-pending-message";
  const CHAT_POSITION_KEY = "travelwai-ai-chat-position";
  const AI_JOB_STORAGE_PREFIX = "travelwai-ai-active-job";
  const AI_JOB_POLL_MS = 1200;
  const CONTACT_HISTORY_LIMIT = 100;
  const CHAT_MESSAGE_PAYLOAD_TYPE = "travelwai-chat-message";
  const DEFAULT_WAIGO_AVATAR_URL = "";

  function getWaigoAvatarUrl() {
    return window.TravelwAISiteBranding?.getLogoUrl?.()
      || window.TravelwAISiteLogoUrl
      || DEFAULT_WAIGO_AVATAR_URL;
  }

  let currentUser = null;
  let isSending = false;
  let initialized = false;
  let selectedAiMediaFiles = [];
  let aiMediaPreviewObjectUrls = [];
  let activeAiJobId = "";
  let aiJobPollTimer = null;
  let aiJobPollController = null;
  let aiStartRequestController = null;
  let isCancellingAiJob = false;
  let aiCancelRequested = false;
  const MAX_AI_MEDIA_SIZE = 10 * 1024 * 1024;
  const MAX_AI_MEDIA_COUNT = 2;
  const MAX_AI_IMAGE_SIDE = 1280;
  const MAX_AI_IMAGE_BYTES = 1.5 * 1024 * 1024;
  const AI_IMAGE_QUALITY_STEPS = [0.82, 0.74, 0.66, 0.58, 0.52, 0.48];
  const aiOptimizedImageCache = new WeakMap();

  const managerConfig = {
    key: "travelwai",
    mode: "travelwai",
    id: "travelwai-ai",
    displayName: "WaiGo",
    avatar: getWaigoAvatarUrl()
  };

  function getPanel() {
    return document.getElementById("contact-panel");
  }

  function applyChatbotBranding(detail) {
    const nextAvatar = String(detail?.logoUrl || getWaigoAvatarUrl()).trim();
    if (managerConfig.avatar === nextAvatar) return;
    managerConfig.avatar = nextAvatar;

    document.querySelectorAll(".waigo-brand-avatar").forEach(image => {
      image.setAttribute("data-site-logo", "true");
      if (image.getAttribute("src") !== nextAvatar) image.setAttribute("src", nextAvatar);
    });

    if (initialized || getPanel()?.classList.contains("open")) renderMessages();
  }

  function applyChatbotSettings(settings) {
    const name = String(settings?.chatbotName || "WaiGo").trim() || "WaiGo";
    managerConfig.displayName = name;
    const launcher = document.getElementById("travelwaiChatbotButton");
    if (launcher) {
      launcher.title = name;
      launcher.setAttribute("aria-label", `Mở ${name}`);
    }
    const panel = getPanel();
    if (panel) panel.setAttribute("aria-label", `Hội thoại ${name}`);
    if (initialized || panel?.classList.contains("open")) renderMessages();
  }

  function bindFloatingStylePicker() {
    const button = document.getElementById("floatingWaigoStyleButton");
    const menu = document.getElementById("floatingWaigoStyleMenu");
    window.TravelwAIChatbotSettings?.bindPicker?.(button, menu, (settings, message) => {
      applyChatbotSettings(settings);
      if (typeof window.TravelwAIToast === "function") window.TravelwAIToast(message || "Đã đổi phong cách.", "success");
    });
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
    const text = String(message || "").trim();
    const isProgress = text && !type;

    if (status) {
      status.textContent = isProgress ? text : "";
      status.hidden = !isProgress;
      status.className = "support-admin-status";
    }

    if (text && !isProgress && typeof window.TravelwAIToast === "function") {
      window.TravelwAIToast(text, type || "info");
    }
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function decodeUnicodeEscapes(value) {
    return String(value || "").replace(/\\u([0-9a-fA-F]{4})/g, function (_, hex) {
      return String.fromCharCode(Number.parseInt(hex, 16));
    });
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

  function buildManagerAvatarUrl() {
    return managerConfig.avatar;
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
    return `${SUPPORT_STORAGE_PREFIX}:${getStorageOwnerKey()}`;
  }

  function getAiJobStorageKey() {
    return `${AI_JOB_STORAGE_PREFIX}:${getStorageOwnerKey()}`;
  }

  function readActiveAiJobId() {
    try {
      const value = JSON.parse(localStorage.getItem(getAiJobStorageKey()) || "null");
      return String(value?.jobId || "").trim();
    } catch (_) {
      return "";
    }
  }

  function writeActiveAiJobId(jobId, notify = true) {
    const cleanJobId = String(jobId || "").trim();
    if (!cleanJobId) return;
    activeAiJobId = cleanJobId;
    try {
      const current = readActiveAiJobId();
      if (current !== cleanJobId) {
        localStorage.setItem(getAiJobStorageKey(), JSON.stringify({ jobId: cleanJobId, updatedAt: new Date().toISOString() }));
      }
      if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-job-updated", { detail: { source: "floating", jobId: cleanJobId } }));
    } catch (_) {}
  }

  function clearActiveAiJobId(jobId, notify = true) {
    const current = readActiveAiJobId();
    if (jobId && current && current !== jobId) return;
    activeAiJobId = "";
    try {
      localStorage.removeItem(getAiJobStorageKey());
      if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-job-updated", { detail: { source: "floating", jobId: "" } }));
    } catch (_) {}
  }

  function loadStoredMessages() {
    try {
      const raw = localStorage.getItem(getAiStorageKey());
      const parsed = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed) ? parsed.filter((item) => !item?.is_system_welcome) : [];
    } catch {
      return [];
    }
  }

  function saveStoredMessages(messages, notify = true, changeKind = "updated") {
    try {
      const clean = (messages || []).filter((item) => !item.is_system_welcome).slice(-CONTACT_HISTORY_LIMIT);
      localStorage.setItem(getAiStorageKey(), JSON.stringify(clean));
      if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-history-updated", { detail: { source: "floating", kind: changeKind } }));
    } catch (_) {}
  }

  function normalizeStoredAttachment(item) {
    if (!item) return null;
    const url = String(item.url || item.src || "").trim();
    if (!url) return null;
    const contentType = String(item.contentType || item.content_type || "application/octet-stream");
    return { url, name: String(item.name || item.fileName || "Tệp đính kèm"), contentType, size: Number(item.size || 0), type: contentType.startsWith("video/") ? "video" : "image" };
  }

  function buildStoredMessageContent(text, attachments) {
    const list = (Array.isArray(attachments) ? attachments : []).map(normalizeStoredAttachment).filter(Boolean);
    if (!list.length) return String(text || "");
    return JSON.stringify({ type: CHAT_MESSAGE_PAYLOAD_TYPE, version: 2, text: String(text || ""), attachments: list, attachment: list[0] || null });
  }

  function getVisibleMessages() {
    return loadStoredMessages();
  }

  function formatTime(value) {
    const date = value ? new Date(value) : new Date();
    if (Number.isNaN(date.getTime())) return "";
    return date.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" });
  }


  function cleanLegacyAiImageText(value) {
    const text = String(value || "").trim();
    return /^đã gửi.*ảnh.*ai.*xem[.!]?$/i.test(text) ? "" : text;
  }

  function parseStoredAiContent(message) {
    const raw = String(message?.content || "");
    try {
      const payload = JSON.parse(raw);
      if (payload?.type === "travelwai-chat-message") {
        const attachments = (Array.isArray(payload.attachments) ? payload.attachments : (payload.attachment ? [payload.attachment] : []))
          .map(normalizeStoredAttachment).filter(Boolean);
        return { text: cleanLegacyAiImageText(payload.text), attachments };
      }
    } catch (_) {}
    const legacyAttachments = message?.imageDataUrl ? [{ url: String(message.imageDataUrl), name: String(message.imageName || "Ảnh đính kèm"), contentType: "image/jpeg", type: "image", size: 0 }] : [];
    return { text: cleanLegacyAiImageText(raw), attachments: legacyAttachments };
  }

  function isAiSender(message) {
    return [managerConfig.id, "travelwai-support"].includes(String(message?.sender_id || ""));
  }

  function createMessageElement(message) {
    const isUser = !isAiSender(message);
    const parsedMessage = parseStoredAiContent(message);
    const row = document.createElement("div");
    row.className = `admin-support-message-row ${isUser ? "sent" : "received"}`;

    const avatar = document.createElement("div");
    avatar.className = "admin-support-message-avatar";
    if (isUser) {
      avatar.textContent = (getUserDisplayName(currentUser) || "B").charAt(0).toUpperCase();
    } else {
      // The message list is rebuilt on every streaming chunk. A CSS background
      // reuses the already loaded uploaded logo and avoids a white image flash.
      avatar.classList.add("waigo-avatar-shell", "waigo-logo-background");
      avatar.setAttribute("role", "img");
      avatar.setAttribute("aria-label", managerConfig.displayName);
    }

    const bubble = document.createElement("div");
    const translationIdentity = String(message?.id || message?.jobId || message?.time_sent || message?.timestamp || Date.now());
    bubble.className = "admin-support-message-bubble";
    bubble.innerHTML = `
      <div class="admin-support-message-sender">${escapeHtml(isUser ? "Bạn" : managerConfig.displayName)}</div>
      <div class="admin-support-message-text" data-chat-message-text data-no-translate data-ai-translation-target="interface" data-ai-translation-key="waigo:${escapeHtml(translationIdentity)}">${escapeHtml(decodeUnicodeEscapes(parsedMessage.text || ""))}</div>
      <div class="admin-support-message-time">${escapeHtml(formatTime(message.time_sent || message.timestamp))}</div>`;

    if (parsedMessage.attachments?.length) {
      const mediaList = document.createElement("div");
      mediaList.className = "admin-support-message-media-list";
      parsedMessage.attachments.forEach((attachment) => {
        if (attachment.contentType.startsWith("video/")) {
          const video = document.createElement("video");
          video.className = "admin-support-message-image";
          video.src = attachment.url;
          video.controls = true;
          video.playsInline = true;
          mediaList.appendChild(video);
        } else {
          const image = document.createElement("img");
          image.className = "admin-support-message-image";
          image.src = attachment.url;
          image.alt = attachment.name || "Ảnh đính kèm";
          mediaList.appendChild(image);
        }
      });
      const textElement = bubble.querySelector(".admin-support-message-text");
      textElement?.insertAdjacentElement("afterend", mediaList);
    }

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
    const list = document.getElementById("supportAdminMessages");
    if (!list) return;
    list.innerHTML = "";
    getVisibleMessages().forEach((message) => list.appendChild(createMessageElement(message)));
    list.scrollTop = list.scrollHeight;
    window.TravelwAITranslation?.refreshConversationControl?.(
      document.getElementById("supportTranslateConversationButton"),
      list
    );
  }

  function appendMessage(message) {
    const list = document.getElementById("supportAdminMessages");
    if (!list) return;
    const messageElement = createMessageElement(message);
    list.appendChild(messageElement);
    list.scrollTop = list.scrollHeight;
    window.TravelwAITranslation?.refreshConversationControl?.(
      document.getElementById("supportTranslateConversationButton"),
      messageElement
    );
  }

  function buildHistoryForRequest(messages) {
    return (messages || [])
      .filter((message) => !message.is_system_welcome && message.content)
      .slice(-12)
      .map((message) => ({
        role: isAiSender(message) ? "assistant" : "user",
        content: parseStoredAiContent(message).text || ""
      }));
  }

  function buildManagerMessage(text) {
    return {
      id: `contact-support-reply-${Date.now()}`,
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

  function upsertAiJobReply(jobId, text, isError, isFinal = false) {
    const cleanText = sanitizeAiText(text);
    if (!cleanText || !jobId) return false;
    const messageId = `${isError ? "ai-job-error" : "ai-job-reply"}-${jobId}`;
    const messages = loadStoredMessages();
    const index = messages.findIndex((message) => String(message?.id || "") === messageId);
    const reply = buildManagerMessage(cleanText);
    reply.id = messageId;
    if (index >= 0) reply.time_sent = messages[index]?.time_sent || messages[index]?.timestamp || reply.time_sent;
    const next = index >= 0
      ? messages.map((message, itemIndex) => itemIndex === index ? { ...message, ...reply } : message)
      : [...messages, reply];
    saveStoredMessages(next, isFinal, isFinal ? "received" : "streaming");
    if (getPanel()?.classList.contains("open")) renderMessages();
    return true;
  }

  function appendAiJobReplyOnce(jobId, text, isError) {
    return upsertAiJobReply(jobId, text, isError, true);
  }

  function scheduleAiJobPoll(jobId, delay = AI_JOB_POLL_MS) {
    clearTimeout(aiJobPollTimer);
    aiJobPollTimer = window.setTimeout(() => pollAiJob(jobId), delay);
  }

  async function pollAiJob(jobId) {
    const cleanJobId = String(jobId || "").trim();
    if (!cleanJobId) {
      setSendingState(false);
      return;
    }

    activeAiJobId = cleanJobId;
    setSendingState(true);

    aiJobPollController?.abort();
    const controller = new AbortController();
    aiJobPollController = controller;

    try {
      const response = await fetch(`${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}`, {
        headers: { Authorization: `Bearer ${getToken()}`, "Content-Type": "application/json" },
        cache: "no-store",
        signal: controller.signal
      });
      const result = await response.json().catch(() => ({}));

      if ([401, 403, 404].includes(response.status)) {
        clearActiveAiJobId(cleanJobId);
        setSendingState(false);
        return;
      }
      if (!response.ok) throw new Error(result.message || "Không thể kiểm tra tiến trình AI.");

      const terminal = applyAiJobSnapshot(cleanJobId, result);
      if (!terminal) scheduleAiJobPoll(cleanJobId);
    } catch (error) {
      if (error?.name === "AbortError" || isCancellingAiJob || cleanJobId !== activeAiJobId) return;
      scheduleAiJobPoll(cleanJobId, 2500);
    } finally {
      if (aiJobPollController === controller) aiJobPollController = null;
    }
  }

  function applyAiJobSnapshot(jobId, result) {
    const status = String(result?.status || "").toLowerCase();
    if (status === "queued" || status === "running") {
      if (result?.reply) upsertAiJobReply(jobId, result.reply, false, false);
      writeActiveAiJobId(jobId, false);
      return false;
    }

    if (status === "completed") {
      upsertAiJobReply(jobId, result?.reply || "", false, true);
    } else if (status !== "cancelled") {
      upsertAiJobReply(jobId, result?.message || "Xin lỗi, tôi chưa thể kết nối với máy chủ AI. Vui lòng thử lại sau.", true, true);
    }

    clearActiveAiJobId(jobId);
    setSendingState(false);
    renderMessages();
    document.getElementById("supportAdminInput")?.focus();
    return true;
  }

  async function streamAiJob(jobId) {
    const cleanJobId = String(jobId || "").trim();
    if (!cleanJobId) {
      setSendingState(false);
      return;
    }

    activeAiJobId = cleanJobId;
    setSendingState(true);
    clearTimeout(aiJobPollTimer);
    aiJobPollTimer = null;
    aiJobPollController?.abort();
    const controller = new AbortController();
    aiJobPollController = controller;

    try {
      const response = await fetch(`${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}/stream`, {
        headers: { Authorization: `Bearer ${getToken()}`, "Content-Type": "application/json" },
        cache: "no-store",
        signal: controller.signal
      });
      if (!response.ok || !response.body) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.message || "Không thể mở luồng trả lời AI.");
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      let terminal = false;
      while (!terminal) {
        const { value, done } = await reader.read();
        buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
        let newlineIndex;
        while ((newlineIndex = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, newlineIndex).trim();
          buffer = buffer.slice(newlineIndex + 1);
          if (!line) continue;
          const result = JSON.parse(line);
          terminal = applyAiJobSnapshot(cleanJobId, result);
          if (terminal) break;
        }
        if (done) break;
      }

      if (!terminal && cleanJobId === activeAiJobId) scheduleAiJobPoll(cleanJobId, 400);
    } catch (error) {
      if (error?.name === "AbortError" || isCancellingAiJob || cleanJobId !== activeAiJobId) return;
      scheduleAiJobPoll(cleanJobId, 500);
    } finally {
      if (aiJobPollController === controller) aiJobPollController = null;
    }
  }

  async function cancelActiveAiJob(event) {
    event?.preventDefault?.();
    event?.stopPropagation?.();
    if (!isSending || isCancellingAiJob) return;

    isCancellingAiJob = true;
    aiCancelRequested = true;
    clearTimeout(aiJobPollTimer);
    aiJobPollTimer = null;
    aiJobPollController?.abort();
    aiStartRequestController?.abort();
    setSendingState(true);

    const cleanJobId = String(activeAiJobId || readActiveAiJobId() || "").trim();
    const endpoint = cleanJobId
      ? `${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}`
      : `${API_BASE_URL}/ai/chat/jobs/active`;

    try {
      const response = await fetch(endpoint, {
        method: "DELETE",
        headers: { Authorization: `Bearer ${getToken()}`, "Content-Type": "application/json" },
        cache: "no-store"
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok && response.status !== 404) {
        throw new Error(result.message || "Không thể dừng AI.");
      }

      clearActiveAiJobId(cleanJobId || undefined);
      isCancellingAiJob = false;
      setSendingState(false);
      setSupportStatus("Đã dừng AI.", "success");
      document.getElementById("supportAdminInput")?.focus();
    } catch (error) {
      isCancellingAiJob = false;
      aiCancelRequested = false;
      setSendingState(true);
      if (cleanJobId) scheduleAiJobPoll(cleanJobId, 800);
      else window.setTimeout(resumeActiveAiJob, 800);
      setSupportStatus(error?.message || "Không thể dừng AI.", "error");
    }
  }

  async function resumeActiveAiJob() {
    const storedJobId = readActiveAiJobId();
    if (storedJobId) {
      streamAiJob(storedJobId);
      return;
    }

    const token = getToken();
    if (!token) {
      setSendingState(false);
      return;
    }

    try {
      const response = await fetch(`${API_BASE_URL}/ai/chat/jobs/active`, {
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        cache: "no-store"
      });
      const result = await response.json().catch(() => ({}));
      if (response.ok && result.active && result.jobId) {
        writeActiveAiJobId(result.jobId, false);
        streamAiJob(result.jobId);
      } else {
        setSendingState(false);
      }
    } catch (_) {

    }
  }

  function sanitizeAiText(value) {
    return decodeUnicodeEscapes(value)
      .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, "")

      .replace(/```[\s\S]*?```/g, function (block) { return block.replace(/```[^\n]*\n?/g, "").replace(/```/g, ""); })
      .replace(/`([^`]+)`/g, "$1")
      .replace(/^\s{0,3}#{1,6}\s+/gm, "")
      .replace(/^\s*>\s?/gm, "")
      .replace(/^\s*[-+*]\s+/gm, "• ")
      .replace(/\*{1,3}([^*\n]+)\*{1,3}/g, "$1")
      .replace(/_{1,3}([^_\n]+)_{1,3}/g, "$1")
      .replace(/[*_]/g, "")
      .replace(/(^|\s)[^\p{L}\p{N}\s]{4,}(?=\s|$)/gu, " ")
      .replace(/([!@#$%^&_=+~|\\/<>])\1{2,}/g, "$1")
      .replace(/[ \t]{2,}/g, " ")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  function detectChatReplyLanguage(value) {
    const text = String(value || "").trim();
    if (!text) return "vi";
    if (/[À-ỹĐđ]/.test(text)) return "vi";

    const normalized = text.toLowerCase().replace(/[^a-z\s']/g, " ").replace(/\s+/g, " ").trim();
    if (/\b(?:pricing|cart|checkout|home|business|admin|manage|contact|schedule|plans|posts|tours|notifications|messaging|signup|register|login|profile|logout|password)\b/.test(normalized)) return "en";
    const englishWords = normalized.match(/\b(?:the|and|is|are|am|i|you|your|my|we|they|what|where|when|why|how|please|help|show|open|go|to|can|could|would|want|need|tell|about|travel|tour|price|login|register|profile|message|chat|yes|no|thanks|thank)\b/g) || [];
    const vietnameseWords = normalized.match(/\b(?:toi|minh|ban|la|va|cua|cho|voi|muon|can|giup|mo|toi|trang|du|lich|tour|gia|dang|nhap|dang|ky|ho|so|tin|nhan|co|khong|cam|on)\b/g) || [];
    return englishWords.length > vietnameseWords.length ? "en" : "vi";
  }

  const MANAGER_PAGE_NAMES_EN = Object.freeze({
    "Đăng nhập": "Login",
    "Đăng ký": "Sign up",
    "Quên mật khẩu": "Forgot password",
    "Đặt lại mật khẩu": "Reset password",
    "Trang chủ": "Home",
    "Giới thiệu": "TravelwAI introduction",
    "Bản đồ Việt Nam": "Vietnam map",
    "Chi tiết tỉnh": "Province details",
    "Lịch trình": "Itinerary",
    "Kế hoạch": "Plans",
    "Giỏ hàng": "Cart",
    "Thanh toán": "Checkout",
    "Hồ sơ": "Profile",
    "Nhắn tin": "Messaging",
    "Hỗ trợ Admin": "Admin support",
    "Bài viết": "Posts",
    "Tour du lịch": "Tours",
    "Business": "Business",
    "Admin": "Admin",
    "Manage": "Manage"
  });

  function localizeManagerReply(value, language) {
    const reply = String(value || "").trim();
    if (language !== "en" || !reply) return reply;

    const exactReplies = {
      "Đang đăng xuất tài khoản.": "Signing out.",
      "Đang mở Hồ sơ để đổi mật khẩu.": "Opening Profile so you can change your password.",
      "Đang mở trang Nhắn tin.": "Opening Messaging.",
      "Đang mở hội thoại với Admin.": "Opening the Admin conversation.",
      "Đang mở trang chủ.": "Opening Home.",
      "Đang mở giới thiệu TravelwAI.": "Opening the TravelwAI introduction page.",
      "Dùng cú pháp: tới trang [tên trang], qua trang [tên trang] hoặc chi tiết trang [tên trang].": "Use: open [page name], go to [page name], or describe [page name].",
      "Các trang TravelwAI: Đăng nhập, Đăng ký, Quên mật khẩu, Đặt lại mật khẩu, Trang chủ, Giới thiệu, Bản đồ Việt Nam, Chi tiết tỉnh, Lịch trình, Kế hoạch, Giỏ hàng, Thanh toán, Hồ sơ, Nhắn tin, Hỗ trợ Admin, Bài viết, Tour du lịch, Business, Admin, Manage. Nhắn: mở [tên trang], tới trang [tên trang] hoặc chi tiết trang [tên trang].": "TravelwAI pages: Login, Sign up, Forgot password, Reset password, Home, Introduction, Vietnam map, Province details, Itinerary, Plans, Cart, Checkout, Profile, Messaging, Admin support, Posts, Tours, Business, Admin, and Manage. Type: open [page name], go to [page name], or describe [page name]."
    };
    if (exactReplies[reply]) return exactReplies[reply];

    const adminEmailMatch = reply.match(/^Đang mở Tin nhắn với Admin (.+)\.$/);
    if (adminEmailMatch) return `Opening Messaging with Admin ${adminEmailMatch[1]}.`;

    const openingMatch = reply.match(/^Đang mở(?: trang)? (.+)\.$/);
    if (openingMatch) {
      const originalName = openingMatch[1].trim();
      return `Opening ${MANAGER_PAGE_NAMES_EN[originalName] || originalName}.`;
    }

    if (reply.startsWith("Các trang TravelwAI:")) {
      return exactReplies["Các trang TravelwAI: Đăng nhập, Đăng ký, Quên mật khẩu, Đặt lại mật khẩu, Trang chủ, Giới thiệu, Bản đồ Việt Nam, Chi tiết tỉnh, Lịch trình, Kế hoạch, Giỏ hàng, Thanh toán, Hồ sơ, Nhắn tin, Hỗ trợ Admin, Bài viết, Tour du lịch, Business, Admin, Manage. Nhắn: mở [tên trang], tới trang [tên trang] hoặc chi tiết trang [tên trang]."];
    }

    return reply;
  }

  function appendLocalManagerReply(text, language = "vi") {
    const cleanText = sanitizeAiText(localizeManagerReply(text, language));
    if (!cleanText) return null;
    const messages = loadStoredMessages();
    const reply = buildManagerMessage(cleanText);
    const next = [...messages, reply];
    saveStoredMessages(next, true, "received");
    appendMessage(reply);
    return reply;
  }

  function getLastManagerReplyText() {
    const messages = loadStoredMessages();
    for (let index = messages.length - 1; index >= 0; index -= 1) {
      const message = messages[index];
      if (isAiSender(message) && message.content && !message.is_system_welcome) {
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
      { url: "/cart", reply: "Đang mở Giỏ hàng.", patterns: [/gio\s*hang/, /cart/] },
      { url: "/checkout", reply: "Đang mở Thanh toán.", patterns: [/thanh\s*toan/, /checkout/, /xac\s*nhan\s*thanh\s*toan/, /qr\s*thanh\s*toan/] },
      { url: "/manage", reply: "Đang mở Manage.", patterns: [/manage/, /quan\s*ly\s*goi/, /quan\s*ly\s*don\s*goi/, /don\s*goi/] },
      { url: "/business", reply: "Đang mở Business.", patterns: [/business/, /company/, /trang\s*business/, /trang\s*company/, /doanh\s*nghiep/, /kinh\s*doanh/] },
      { url: "/schedule", reply: "Đang mở trang Lịch trình.", patterns: [/lap\s*lich\s*trinh/, /tao\s*lich\s*trinh/, /lich\s*trinh/] },
      { url: "/plans", reply: "Đang mở trang Kế hoạch.", patterns: [/lap\s*ke\s*hoach/, /tao\s*ke\s*hoach/, /ke\s*hoach/] },
      { url: "/provinces", reply: "Đang mở Bản đồ Việt Nam.", patterns: [/ban\s*do/, /tinh\s*thanh/, /34\s*tinh/, /viet\s*nam/] },
      { url: "/posts", reply: "Đang mở trang Bài viết.", patterns: [/bai\s*viet/, /tin\s*du\s*lich/, /kham\s*pha\s*bai/] },
      { url: "/tours", reply: "Đang mở trang Tour du lịch.", patterns: [/tour\s*du\s*lich/, /dat\s*tour/, /xem\s*tour/] },
      { url: "/business", reply: "Đang mở Business.", patterns: [/sales/, /ban\s*tour/, /don\s*ban\s*tour/] },
      { url: "/admin", reply: "Đang mở trang Admin.", patterns: [/admin/, /quan\s*tri/, /quan\s*ly\s*he\s*thong/] },
      { url: "/messaging?admin=1", reply: `Đang mở Tin nhắn với Admin ${SUPPORT_ADMIN_EMAIL}.`, patterns: [/tin\s*nhan/, /nhan\s*tin/, /messaging/, /chat/] },
      { url: "/profile", reply: "Đang mở trang Hồ sơ.", patterns: [/ho\s*so/, /thong\s*tin\s*ca\s*nhan/, /tai\s*khoan/, /doi\s*ten/] },
      { url: "/messaging?admin=1", reply: "Đang mở hội thoại với Admin.", patterns: [/lien\s*he\s*admin/, /ho\s*tro\s*admin/] },
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
        reply: "Các trang TravelwAI: Đăng nhập, Đăng ký, Quên mật khẩu, Đặt lại mật khẩu, Trang chủ, Giới thiệu, Bản đồ Việt Nam, Chi tiết tỉnh, Lịch trình, Kế hoạch, Giỏ hàng, Thanh toán, Hồ sơ, Nhắn tin, Hỗ trợ Admin, Bài viết, Tour du lịch, Business, Admin, Manage. Nhắn: mở [tên trang], tới trang [tên trang] hoặc chi tiết trang [tên trang]."
      };
    }

    return getNavigationTargetFromText(text);
  }

  function needsFullMessagingCommand(text) {
    const normalized = normalizeForSearch(text);
    return /(?:nhan\s*tin|chat|tro\s*chuyen|cuoc\s*tro\s*chuyen)\s*(?:voi|cung)/.test(normalized)
      || /(?:ket\s*ban|them\s*ban|loi\s*moi\s*ket\s*ban|yeu\s*cau\s*ket\s*ban)/.test(normalized);
  }

  function setSendingState(value) {
    isSending = Boolean(value);
    const button = document.querySelector("#supportAdminForm .admin-support-send-btn");
    const input = document.getElementById("supportAdminInput");
    if (button) {
      button.disabled = isCancellingAiJob;
      button.classList.toggle("is-loading", isSending);
      button.classList.toggle("is-ai-stop", isSending && !isCancellingAiJob);
      button.classList.toggle("is-stopping", isCancellingAiJob);
      button.setAttribute("aria-busy", isSending ? "true" : "false");
      button.setAttribute("aria-label", isCancellingAiJob
        ? "Đang dừng AI"
        : isSending
          ? "Dừng AI"
          : `Gửi câu hỏi cho ${managerConfig.displayName}`);
      button.title = isCancellingAiJob
        ? "Đang dừng AI..."
        : isSending
          ? "Bấm để dừng AI"
          : `Gửi câu hỏi cho ${managerConfig.displayName}`;
    }
    if (input) input.disabled = isSending;
    document.querySelectorAll("#supportAdminForm .twai-chatbot-suggestion").forEach((suggestion) => {
      suggestion.disabled = isSending;
    });
  }

  function fillFloatingAiSuggestion(event) {
    const suggestion = String(event.currentTarget?.dataset?.aiSuggestion || "").trim();
    const input = document.getElementById("supportAdminInput");
    if (!suggestion || !input || input.disabled) return;

    input.value = suggestion;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();
    input.setSelectionRange?.(suggestion.length, suggestion.length);
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

  function readFileAsDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(new Error("Không đọc được tệp đính kèm."));
      reader.readAsDataURL(file);
    });
  }

  function extractVideoFrameAsDataUrl(file) {
    return new Promise((resolve, reject) => {
      const objectUrl = URL.createObjectURL(file);
      const video = document.createElement("video");
      video.muted = true;
      video.playsInline = true;
      video.preload = "metadata";
      const cleanup = () => URL.revokeObjectURL(objectUrl);
      video.onerror = () => { cleanup(); reject(new Error(`Không đọc được video ${file.name || ""}.`)); };
      video.onloadeddata = () => {
        try {
          const canvas = document.createElement("canvas");
          const maxSide = 1280;
          const scale = Math.min(1, maxSide / Math.max(video.videoWidth || 1, video.videoHeight || 1));
          canvas.width = Math.max(1, Math.round((video.videoWidth || 1) * scale));
          canvas.height = Math.max(1, Math.round((video.videoHeight || 1) * scale));
          canvas.getContext("2d")?.drawImage(video, 0, 0, canvas.width, canvas.height);
          const dataUrl = canvas.toDataURL("image/jpeg", 0.82);
          cleanup();
          resolve(dataUrl);
        } catch (error) {
          cleanup();
          reject(error);
        }
      };
      video.src = objectUrl;
    });
  }

  function toOllamaImage(dataUrl) {
    return String(dataUrl || "").replace(/^data:image\/[^;]+;base64,/, "");
  }

  function canvasToBlob(canvas, type, quality) {
    return new Promise((resolve) => canvas.toBlob(resolve, type, quality));
  }

  function blobToDataUrl(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(new Error("Không đọc được ảnh đã tối ưu."));
      reader.readAsDataURL(blob);
    });
  }

  function formatMediaBytes(value) {
    const bytes = Math.max(0, Number(value || 0));
    if (bytes < 1024) return `${Math.round(bytes)} B`;
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  function replaceFileExtension(fileName, extension) {
    const base = String(fileName || "anh").replace(/\.[^/.]+$/, "") || "anh";
    return `${base}.${extension}`;
  }

  async function loadImageForAi(file) {
    if (typeof createImageBitmap === "function") {
      try {
        return await createImageBitmap(file, { imageOrientation: "from-image" });
      } catch (_) {
        return await createImageBitmap(file);
      }
    }

    return new Promise((resolve, reject) => {
      const objectUrl = URL.createObjectURL(file);
      const image = new Image();
      image.onload = () => { URL.revokeObjectURL(objectUrl); resolve(image); };
      image.onerror = () => { URL.revokeObjectURL(objectUrl); reject(new Error(`Không đọc được ảnh ${file?.name || ""}.`)); };
      image.src = objectUrl;
    });
  }

  async function encodeAiCanvas(canvas, quality) {
    let blob = await canvasToBlob(canvas, "image/webp", quality);
    if (blob && blob.size > 0 && blob.type === "image/webp") return blob;

    const jpegCanvas = document.createElement("canvas");
    jpegCanvas.width = canvas.width;
    jpegCanvas.height = canvas.height;
    const jpegContext = jpegCanvas.getContext("2d", { alpha: false });
    if (!jpegContext) return null;
    jpegContext.fillStyle = "#ffffff";
    jpegContext.fillRect(0, 0, jpegCanvas.width, jpegCanvas.height);
    jpegContext.drawImage(canvas, 0, 0);
    blob = await canvasToBlob(jpegCanvas, "image/jpeg", quality);
    return blob && blob.size > 0 ? blob : null;
  }

  async function optimizeImageForAi(file) {
    if (aiOptimizedImageCache.has(file)) return aiOptimizedImageCache.get(file);

    const task = (async () => {
      const bitmap = await loadImageForAi(file);
      const sourceWidth = Number(bitmap.width || bitmap.naturalWidth || 1);
      const sourceHeight = Number(bitmap.height || bitmap.naturalHeight || 1);
      let bestBlob = null;
      let bestWidth = sourceWidth;
      let bestHeight = sourceHeight;

      try {
        const sideSteps = [MAX_AI_IMAGE_SIDE, 1120, 960, 800, 640, 512];
        for (let index = 0; index < sideSteps.length; index += 1) {
          const maxSide = sideSteps[index];
          const scale = Math.min(1, maxSide / Math.max(sourceWidth, sourceHeight));
          const width = Math.max(1, Math.round(sourceWidth * scale));
          const height = Math.max(1, Math.round(sourceHeight * scale));
          const canvas = document.createElement("canvas");
          canvas.width = width;
          canvas.height = height;
          const context = canvas.getContext("2d", { alpha: true });
          if (!context) throw new Error("Trình duyệt không hỗ trợ xử lý ảnh.");
          context.imageSmoothingEnabled = true;
          context.imageSmoothingQuality = "high";
          context.drawImage(bitmap, 0, 0, width, height);

          const quality = AI_IMAGE_QUALITY_STEPS[Math.min(index, AI_IMAGE_QUALITY_STEPS.length - 1)];
          const blob = await encodeAiCanvas(canvas, quality);
          if (!blob) continue;
          if (!bestBlob || blob.size < bestBlob.size) {
            bestBlob = blob;
            bestWidth = width;
            bestHeight = height;
          }
          if (blob.size <= MAX_AI_IMAGE_BYTES) break;
        }
      } finally {
        if (typeof bitmap.close === "function") bitmap.close();
      }

      if (!bestBlob) {
        if (Number(file?.size || 0) > MAX_AI_IMAGE_BYTES) {
          throw new Error("Trình duyệt không thể tối ưu ảnh này. Hãy chọn ảnh nhỏ hơn.");
        }
        const originalDataUrl = await readFileAsDataUrl(file);
        return {
          dataUrl: originalDataUrl,
          uploadFile: file,
          width: sourceWidth,
          height: sourceHeight,
          originalSize: Number(file.size || 0),
          optimizedSize: Number(file.size || 0),
          contentType: String(file.type || "image/jpeg"),
          optimized: false
        };
      }

      if (bestBlob.size > 2.4 * 1024 * 1024) {
        throw new Error("Ảnh sau khi tối ưu vẫn quá lớn. Hãy chọn ảnh khác hoặc cắt bớt ảnh.");
      }
      const extension = bestBlob.type === "image/webp" ? "webp" : "jpg";
      const optimizedFile = new File([bestBlob], replaceFileExtension(file.name, extension), {
        type: bestBlob.type || "image/jpeg",
        lastModified: Date.now()
      });
      return {
        dataUrl: await blobToDataUrl(bestBlob),
        uploadFile: optimizedFile,
        width: bestWidth,
        height: bestHeight,
        originalSize: Number(file.size || 0),
        optimizedSize: Number(bestBlob.size || 0),
        contentType: bestBlob.type || "image/jpeg",
        optimized: true
      };
    })();

    aiOptimizedImageCache.set(file, task);
    return task;
  }

  function clearAiPreviewObjectUrls() {
    aiMediaPreviewObjectUrls.splice(0).forEach(url => URL.revokeObjectURL(url));
  }

  function aiPreviewItem(file, index) {
    const objectUrl = URL.createObjectURL(file);
    aiMediaPreviewObjectUrls.push(objectUrl);
    const media = String(file.type || "").startsWith("video/")
      ? `<video src="${escapeHtml(objectUrl)}" preload="metadata" muted playsinline></video>`
      : `<img src="${escapeHtml(objectUrl)}" alt="${escapeHtml(file.name || "Ảnh đính kèm")}" />`;
    return `<div class="twai-chatbot-media-preview-item">${media}<strong>${escapeHtml(file.name || "Tệp")}</strong><button type="button" data-ai-media-remove="${index}" aria-label="Xóa tệp" title="Xóa tệp"><span data-interface-icon="trash-2"></span></button></div>`;
  }

  function renderSelectedAiMedia() {
    const preview = document.getElementById("supportAiImagePreview");
    if (!preview) return;
    clearAiPreviewObjectUrls();
    preview.innerHTML = selectedAiMediaFiles.map(aiPreviewItem).join("");
    preview.hidden = selectedAiMediaFiles.length === 0;
    preview.querySelectorAll("[data-ai-media-remove]").forEach(button => {
      button.addEventListener("click", () => {
        selectedAiMediaFiles.splice(Number(button.dataset.aiMediaRemove), 1);
        renderSelectedAiMedia();
      });
    });
  }

  function clearSelectedAiMedia() {
    selectedAiMediaFiles = [];
    const input = document.getElementById("supportAiImageInput");
    if (input) input.value = "";
    renderSelectedAiMedia();
  }

  function getClipboardImageFile(event) {
    const items = Array.from(event?.clipboardData?.items || []);
    const imageItem = items.find((item) => String(item.type || "").startsWith("image/"));
    const file = imageItem?.getAsFile?.();
    if (!file) return null;
    if (file.name) return file;
    const extension = String(file.type || "image/png").split("/")[1] || "png";
    return new File([file], `anh-dan-${Date.now()}.${extension}`, { type: file.type || "image/png" });
  }

  async function handleAiImagePaste(event) {
    const file = getClipboardImageFile(event);
    if (!file) return;
    event.preventDefault();
    addSelectedAiMedia([file]);
  }

  function addSelectedAiMedia(files) {
    for (const file of Array.from(files || [])) {
      const type = String(file.type || "");
      if (!type.startsWith("image/") && !type.startsWith("video/")) {
        window.TravelwAIToast("AI chỉ hỗ trợ ảnh hoặc video.", "warning");
        continue;
      }
      if (file.size > MAX_AI_MEDIA_SIZE) {
        window.TravelwAIToast(`${file.name || "Tệp"} vượt quá 10MB.`, "warning");
        continue;
      }
      selectedAiMediaFiles.push(file);
    }
    if (selectedAiMediaFiles.length > MAX_AI_MEDIA_COUNT) {
      window.TravelwAIToast(`AI chỉ nhận tối đa ${MAX_AI_MEDIA_COUNT} ảnh hoặc video.`, "warning");
    }
    selectedAiMediaFiles = selectedAiMediaFiles.slice(0, MAX_AI_MEDIA_COUNT);
    renderSelectedAiMedia();
  }

  async function uploadAiMedia(files) {
    const list = Array.from(files || []).slice(0, MAX_AI_MEDIA_COUNT);
    if (!list.length) return [];

    const formData = new FormData();
    for (const file of list) {
      const prepared = file.type?.startsWith("image/") && window.TravelwAIImageOptimizer
        ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
        : file;
      formData.append("files", prepared, prepared.name || file.name);
    }

    const response = await fetch(`${API_BASE_URL}/ai/attachments`, {
      method: "POST",
      headers: { Authorization: `Bearer ${getToken()}` },
      body: formData
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.message || result.detail || "Không thể lưu ảnh đính kèm.");
    }
    return (Array.isArray(result.media) ? result.media : []).map(normalizeStoredAttachment).filter(Boolean);
  }

  function getAiMediaPlaceholder(count) {
    return count > 1 ? `Đã gửi ${count} ảnh cho AI.` : "Đã gửi một ảnh cho AI.";
  }

  function replaceStoredAiMessage(messageId, text, attachments) {
    const current = loadStoredMessages();
    const updated = current.map((message) => String(message?.id || "") === String(messageId)
      ? { ...message, content: buildStoredMessageContent(text, attachments) }
      : message);
    saveStoredMessages(updated, true, "updated");
  }

  function buildDirectAiMediaPayload(files) {
    const list = Array.from(files || []);
    return Promise.all(list.map(async (file) => {
      const isVideo = String(file?.type || "").startsWith("video/");
      let prepared;
      if (isVideo) {
        const dataUrl = await extractVideoFrameAsDataUrl(file);
        const response = await fetch(dataUrl);
        const frameBlob = await response.blob();
        prepared = {
          dataUrl,
          uploadFile: file,
          analysisFileName: replaceFileExtension(file.name || "khung-hinh-video", "jpg"),
          width: 0,
          height: 0,
          originalSize: Number(file?.size || 0),
          optimizedSize: Number(frameBlob.size || 0),
          contentType: "image/jpeg",
          optimized: true
        };
      } else {
        prepared = await optimizeImageForAi(file);
      }

      const imageData = toOllamaImage(prepared.dataUrl);
      if (!imageData) return null;
      const dimensionText = prepared.width > 0 && prepared.height > 0
        ? `${prepared.width}x${prepared.height}px`
        : "khung hình đại diện";
      return {
        imageData,
        uploadFile: prepared.uploadFile || file,
        attachment: {
          url: prepared.dataUrl,
          name: String(isVideo ? (prepared.analysisFileName || "Khung hình video.jpg") : (prepared.uploadFile?.name || file?.name || "Ảnh đính kèm")),
          contentType: prepared.contentType || "image/jpeg",
          size: Number(prepared.optimizedSize || 0),
          type: "image"
        },
        contextLabel: `${String(file?.name || "Tệp")} (${isVideo ? "video lấy khung hình" : "ảnh"}; ${dimensionText}; ` +
          `${formatMediaBytes(prepared.originalSize)} → ${formatMediaBytes(prepared.optimizedSize)} trước khi gửi AI)`
      };
    }));
  }

  async function sendSupportMessage(event) {
    if (event) event.preventDefault();
    if (isSending) return;

    const input = document.getElementById("supportAdminInput");
    const text = (input?.value || "").trim();
    const pendingFiles = selectedAiMediaFiles.slice(0, MAX_AI_MEDIA_COUNT);
    if (!text && !pendingFiles.length) {
      window.TravelwAIToast("Nhập nội dung hoặc đính kèm tệp trước khi gửi.", "warning");
      input?.focus();
      return;
    }

    const token = getToken();
    if (!token) {
      setSupportStatus("Bạn cần đăng nhập.", "error");
      try { localStorage.setItem(ADMIN_PENDING_MESSAGE_KEY, text); } catch (_) {}
      setTimeout(() => { window.location.href = "/login"; }, 800);
      return;
    }

    await loadCurrentUser();
    const storedBeforeSend = loadStoredMessages();
    aiCancelRequested = false;
    aiStartRequestController?.abort();
    const startController = new AbortController();
    aiStartRequestController = startController;
    setSendingState(true);
    setSupportStatus("", "");

    try {
      if (pendingFiles.length) setSupportStatus("Đang tối ưu ảnh trước khi gửi AI...", "");
      const mediaPayload = (await buildDirectAiMediaPayload(pendingFiles)).filter(Boolean);
      if (aiCancelRequested) throw new DOMException("Đã dừng AI", "AbortError");

      // Upload chính bản đã tối ưu để không tranh băng thông với request phân tích ảnh.
      const optimizedUploadFiles = mediaPayload.map(item => item.uploadFile).filter(Boolean);
      const uploadPromise = optimizedUploadFiles.length
        ? uploadAiMedia(optimizedUploadFiles).catch((error) => {
            console.warn("Không thể lưu ảnh AI lên storage:", error);
            return [];
          })
        : Promise.resolve([]);

      setSupportStatus("", "");
      const images = mediaPayload.map(item => item.imageData).filter(Boolean);
      const localAttachments = mediaPayload.map(item => item.attachment).filter(Boolean);
      const referenceContext = mediaPayload.length
        ? `Tệp người dùng đính kèm: ${mediaPayload.map(item => item.contextLabel).join(", ")}.`
        : "";

      const response = await fetch(`${API_BASE_URL}/ai/chat/jobs`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify({
          Message: text || "Hãy phân tích các ảnh hoặc khung hình video đã đính kèm.",
          History: buildHistoryForRequest(storedBeforeSend),
          ReferenceContext: referenceContext,
          Images: images,
          Language: "auto"
        }),
        signal: startController.signal
      });

      const result = await response.json().catch(() => ({}));
      if (aiCancelRequested) {
        const createdJobId = String(result.jobId || "").trim();
        if (createdJobId) {
          void fetch(`${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(createdJobId)}`, {
            method: "DELETE",
            headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
            cache: "no-store"
          }).catch(() => {});
        }
        throw new DOMException("Đã dừng AI", "AbortError");
      }

      if (response.status === 409 && result.jobId) {
        writeActiveAiJobId(result.jobId);
        streamAiJob(result.jobId);
        setSupportStatus("AI đang trả lời câu trước.", "info");
        return;
      }
      if (!response.ok || !result.jobId) throw new Error(result.message || result.detail || "AI chưa thể trả lời lúc này.");

      const jobId = String(result.jobId);
      const messageId = `ai-user-${jobId}`;
      const persistedText = text || (localAttachments.length ? getAiMediaPlaceholder(localAttachments.length) : "");
      const storedUserMessage = {
        id: messageId,
        sender_id: getCurrentUserId() || "current-user",
        sender_info: currentUser || { username: getUserDisplayName(currentUser), email: localStorage.getItem("userEmail") || "" },
        content: persistedText,
        time_sent: new Date().toISOString()
      };
      const visibleUserMessage = {
        ...storedUserMessage,
        content: buildStoredMessageContent(text, localAttachments)
      };

      saveStoredMessages([...storedBeforeSend, storedUserMessage], true, "sent");
      appendMessage(visibleUserMessage);
      if (input) input.value = "";
      clearSelectedAiMedia();
      writeActiveAiJobId(jobId);
      streamAiJob(jobId);

      // Job AI đã chạy; khi upload xong mới thay URL local bằng URL storage trong lịch sử.
      void uploadPromise.then((storedAttachments) => {
        if (storedAttachments.length) replaceStoredAiMessage(messageId, text, storedAttachments);
      });
    } catch (error) {
      if (error?.name !== "AbortError") {
        setSendingState(false);
        setSupportStatus(error?.message || "Không kết nối được AI.", "error");
        input?.focus();
      }
    } finally {
      if (aiStartRequestController === startController) aiStartRequestController = null;
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
    if (!panel) return;

    // Hiện chatbot ngay; không bắt người dùng chờ tải tài khoản/lịch sử.
    panel.classList.remove("minimized");
    panel.classList.add("open");
    restorePanelPosition(panel);
    panel.setAttribute("aria-hidden", "false");
    document.getElementById("supportAdminInput")?.focus();

    await initializePanelContent();
    renderMessages();
    document.getElementById("supportAdminInput")?.focus();
  }


  function getInterfaceLanguage() {
    const language = window.TravelwAILanguage?.get?.()
      || document.documentElement.getAttribute("data-travelwai-language")
      || document.documentElement.lang
      || "vi";
    return String(language).toLowerCase().startsWith("en") ? "en" : "vi";
  }

  function normalizeLocalizedAiQuestion(question) {
    if (question && typeof question === "object") {
      const language = getInterfaceLanguage();
      return String(question[language] || question.vi || question.en || question.question || "");
    }
    return String(question || "");
  }


  async function resolveAiQuestionForCurrentLanguage(question) {
    const prompt = normalizeLocalizedAiQuestion(question).replace(/\s+/g, " ").trim();
    if (!prompt || getInterfaceLanguage() !== "en" || !/[ĂÂĐÊÔƠƯÀ-ỹ]/i.test(prompt)) return prompt;

    const languageApi = window.TravelwAILanguage;
    if (typeof languageApi?.translateText !== "function") return prompt;

    return await new Promise((resolve) => {
      let finished = false;
      const complete = (value) => {
        if (finished) return;
        finished = true;
        resolve(String(value || prompt).replace(/\s+/g, " ").trim());
      };

      languageApi.translateText(prompt, complete);

      window.setTimeout(() => complete(prompt), 8000);
    });
  }


  async function askAiFromPage(question) {
    // Mở hội thoại nổi trước, rồi mới xử lý/dịch câu hỏi.
    const opening = openPanel();
    const prompt = await resolveAiQuestionForCurrentLanguage(question);
    await opening;
    if (!prompt) return;

    const input = document.getElementById("supportAdminInput");
    if (!input) return;

    input.value = prompt;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();


    if (isSending) {
      setSupportStatus("AI đang trả lời.", "info");
      return;
    }

    await sendSupportMessage();
  }

  window.TravelwAIOpenChatbot = openPanel;
  window.TravelwAIAskAI = askAiFromPage;


  document.addEventListener("click", function (event) {
    const askButton = event.target?.closest?.('[data-travelwai-ask-ai="true"]');
    if (!askButton) return;
    openPanel();
  }, true);

  window.addEventListener("travelwai:open-chatbot", function () {
    openPanel();
  });

  window.addEventListener("travelwai:ask-ai", function (event) {
    askAiFromPage(event?.detail?.question || "");
  });

  function closePanel() {
    const panel = getPanel();
    if (!panel) return;
    panel.classList.remove("open");
    panel.setAttribute("aria-hidden", "true");
  }


  function keepPanelInsideViewport(panel, left, top) {
    const margin = 10;
    const maxLeft = Math.max(margin, window.innerWidth - panel.offsetWidth - margin);
    const maxTop = Math.max(margin, window.innerHeight - panel.offsetHeight - margin);
    return {
      left: Math.min(Math.max(margin, left), maxLeft),
      top: Math.min(Math.max(margin, top), maxTop)
    };
  }

  function savePanelPosition(panel) {
    if (!panel || !panel.style.left) return;
    try {
      localStorage.setItem(CHAT_POSITION_KEY, JSON.stringify({
        left: parseFloat(panel.style.left) || 0,
        top: parseFloat(panel.style.top) || 0
      }));
    } catch (_) {}
  }

  function restorePanelPosition(panel) {
    if (!panel) return;
    try {
      const saved = JSON.parse(localStorage.getItem(CHAT_POSITION_KEY) || "null");
      if (!saved || !Number.isFinite(saved.left) || !Number.isFinite(saved.top)) return;
      const position = keepPanelInsideViewport(panel, saved.left, saved.top);
      panel.style.left = `${position.left}px`;
      panel.style.top = `${position.top}px`;
      panel.style.right = "auto";
      panel.style.bottom = "auto";
    } catch (_) {}
  }

  function bindPanelDragging() {
    const panel = getPanel();
    const handle = document.querySelector("[data-chat-drag-handle]");
    if (!panel || !handle) return;

    let drag = null;

    function movePanel(event) {
      if (!drag || event.pointerId !== drag.pointerId) return;
      const position = keepPanelInsideViewport(
        panel,
        event.clientX - drag.offsetX,
        event.clientY - drag.offsetY
      );
      panel.style.setProperty("left", `${position.left}px`);
      panel.style.setProperty("top", `${position.top}px`);
      panel.style.setProperty("right", "auto");
      panel.style.setProperty("bottom", "auto");
      event.preventDefault();
    }

    function stopDragging(event) {
      if (!drag || event.pointerId !== drag.pointerId) return;
      drag = null;
      panel.classList.remove("is-dragging");
      document.removeEventListener("pointermove", movePanel);
      document.removeEventListener("pointerup", stopDragging);
      document.removeEventListener("pointercancel", stopDragging);
      savePanelPosition(panel);
    }

    handle.addEventListener("pointerdown", function (event) {
      if (event.pointerType === "mouse" && event.button !== 0) return;
      if (event.target.closest("button, a, input, textarea, summary")) return;

      const rect = panel.getBoundingClientRect();
      drag = {
        pointerId: event.pointerId,
        offsetX: event.clientX - rect.left,
        offsetY: event.clientY - rect.top
      };

      panel.classList.add("is-dragging");
      panel.style.setProperty("left", `${rect.left}px`);
      panel.style.setProperty("top", `${rect.top}px`);
      panel.style.setProperty("right", "auto");
      panel.style.setProperty("bottom", "auto");

      document.addEventListener("pointermove", movePanel, { passive: false });
      document.addEventListener("pointerup", stopDragging);
      document.addEventListener("pointercancel", stopDragging);
      event.preventDefault();
    });

    handle.addEventListener("dblclick", function (event) {
      if (event.target.closest("button, a, input, textarea, summary")) return;
      panel.style.removeProperty("left");
      panel.style.removeProperty("top");
      panel.style.removeProperty("right");
      panel.style.removeProperty("bottom");
      try { localStorage.removeItem(CHAT_POSITION_KEY); } catch (_) {}
    });

    window.addEventListener("resize", function () {
      if (!panel.style.left) return;
      const rect = panel.getBoundingClientRect();
      const position = keepPanelInsideViewport(panel, rect.left, rect.top);
      panel.style.setProperty("left", `${position.left}px`);
      panel.style.setProperty("top", `${position.top}px`);
      savePanelPosition(panel);
    });
  }

  function bindEvents() {
    bindPanelDragging();

    document.querySelectorAll('[data-contact-panel-trigger]').forEach((trigger) => {
      trigger.addEventListener("click", openPanel);
    });

    document.querySelectorAll("[data-close-contact-panel]").forEach((btn) => {
      btn.addEventListener("click", function (event) {
        event.preventDefault();
        closePanel();
      });
    });

    document.querySelectorAll("#supportAdminForm").forEach((form) => {
      form.addEventListener("submit", sendSupportMessage);
    });

    document.querySelector("#supportAdminForm .admin-support-send-btn")?.addEventListener("click", function (event) {
      if (!isSending) return;
      cancelActiveAiJob(event);
    });

    document.querySelectorAll("#supportAdminForm .twai-chatbot-suggestion").forEach((button) => {
      button.addEventListener("click", fillFloatingAiSuggestion);
    });

    document.getElementById("supportAiImageButton")?.addEventListener("click", function () {
      document.getElementById("supportAiImageInput")?.click();
    });
    document.getElementById("supportAiImageInput")?.addEventListener("change", function (event) {
      addSelectedAiMedia(event.target.files);
      event.target.value = "";
    });
        document.getElementById("supportAdminInput")?.addEventListener("paste", handleAiImagePaste);
    document.getElementById("supportAdminForm")?.addEventListener("paste", function (event) {
      if (event.target?.id === "supportAdminInput") return;
      handleAiImagePaste(event);
    });

    document.getElementById("clearSupportChatHistory")?.addEventListener("click", function () {
      try { localStorage.removeItem(getAiStorageKey()); } catch (_) {}
      renderMessages();
      const input = document.getElementById("supportAdminInput");
      if (input) input.value = "";
      clearSelectedAiMedia();
      window.dispatchEvent(new CustomEvent("travelwai:ai-history-cleared"));

      if (typeof window.TravelwAIToast === "function") {
        window.TravelwAIToast("Đã xóa lịch sử trò chuyện với AI.");
      } else {
        setSupportStatus("Đã xóa lịch sử trò chuyện với AI.", "success");
      }
      input?.focus();
    });

    document.addEventListener("click", function (event) {
      const panel = getPanel();
      if (!panel || !panel.classList.contains("open")) return;

      const clickedTrigger = event.target.closest('[data-contact-panel-trigger], [data-travelwai-ask-ai="true"]');
      if (!panel.contains(event.target) && !clickedTrigger) {
        closePanel();
      }
    });

    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") closePanel();
    });
  }

  window.addEventListener("travelwai:ai-history-updated", function (event) {
    if (event?.detail?.source === "floating") return;
    if (getPanel()?.classList.contains("open")) renderMessages();
  });

  window.addEventListener("travelwai:ai-job-updated", function (event) {
    if (event?.detail?.source === "floating") return;
    const jobId = String(event?.detail?.jobId || readActiveAiJobId() || "").trim();
    if (jobId) streamAiJob(jobId);
    else resumeActiveAiJob();
  });

  window.addEventListener("storage", function (event) {
    if (event.key === getAiStorageKey()) {
      if (getPanel()?.classList.contains("open")) renderMessages();
    }
    if (event.key === getAiJobStorageKey()) resumeActiveAiJob();
  });

  window.addEventListener("travelwai:chatbot-settings-changed", event => {
    applyChatbotSettings(event?.detail || {});
  });

  window.addEventListener("travelwai:brandingchange", event => {
    applyChatbotBranding(event?.detail || {});
  });

  document.addEventListener("DOMContentLoaded", function () {
    bindEvents();
    bindFloatingStylePicker();
    const settingsPromise = window.TravelwAIChatbotSettings?.load?.() || Promise.resolve({ chatbotName: "WaiGo" });
    Promise.resolve(settingsPromise)
      .then(applyChatbotSettings)
      .finally(() => loadCurrentUser().finally(resumeActiveAiJob));
  });
})();
