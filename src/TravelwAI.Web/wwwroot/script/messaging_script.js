
let currentConversation = null;
let currentUser = null;
let conversations = [];
let activeConversationSearchQuery = "";
let activeFriendsSearchQuery = "";
let activeSidebarPanelMode = "conversations";
let currentMessages = [];
let websocket = null;
let friend_requests = [];
let user_friendList = [];
let friendRefreshTimer = null;
let all_users = [];
let selectedChatAttachments = [];
let selectedMemoryShareFiles = [];
let chatAttachmentPreviewObjectUrls = [];
let memorySharePreviewObjectUrls = [];
let selectedGroupUsers = [];
let currentChatModalMode = "chat";
let outgoingFriendRequestKeys = new Set();
const API_BASE_URL = "/api";
const CLIENT_CACHE_VERSION = "2026-07-09-clean-v1";
const USERS_CACHE_TTL_MS = 5 * 60 * 1000;
const FRIEND_CACHE_TTL_MS = 30 * 1000;
const CONVERSATION_CACHE_TTL_MS = 15 * 1000;
const MESSAGE_CACHE_TTL_MS = 30 * 1000;
const FRIEND_REFRESH_MS = 30 * 1000;
const MAX_CHAT_ATTACHMENT_SIZE = 10 * 1024 * 1024;
const MAX_AI_ATTACHMENT_COUNT = 2;
const MAX_CHAT_MESSAGE_LENGTH = 100;
const CHAT_MESSAGE_PAYLOAD_TYPE = "travelwai-chat-message";
const SUPPORT_ADMIN_EMAIL = "2324802010387@student.tdmu.edu.vn";
const ADMIN_PENDING_MESSAGE_KEY = "travelwai-admin-pending-message";
const AI_CONVERSATION_ID = "travelwai-ai";
const AI_JOB_STORAGE_PREFIX = "travelwai-ai-active-job";
const AI_JOB_POLL_MS = 1200;

function decodeUnicodeEscapes(value) {
  return String(value || "").replace(/\\u([0-9a-fA-F]{4})/g, function (_, hex) {
    return String.fromCharCode(Number.parseInt(hex, 16));
  });
}
const AI_HISTORY_PREFIX = "travelwai-admin-support-history";
const DEFAULT_AI_AVATAR_URL = "";
let AI_AVATAR_URL = window.TravelwAISiteBranding?.getLogoUrl?.()
  || window.TravelwAISiteLogoUrl
  || DEFAULT_AI_AVATAR_URL;
let AI_DISPLAY_NAME = "WaiGo";
let isAiSending = false;
let activeAiJobId = "";
let aiJobPollTimer = null;
let aiJobPollController = null;
let aiStartRequestController = null;
let isCancellingAiJob = false;
let aiCancelRequested = false;

function applyMessagingBranding(detail) {
  const nextAvatar = String(
    detail?.logoUrl
    || window.TravelwAISiteBranding?.getLogoUrl?.()
    || window.TravelwAISiteLogoUrl
    || DEFAULT_AI_AVATAR_URL
  ).trim();
  if (AI_AVATAR_URL === nextAvatar) return;
  AI_AVATAR_URL = nextAvatar;

  // site_branding.js owns logo preloading and DOM updates. Do not set src to
  // an empty value or reassign the same image while messages are streaming.
  if (typeof renderMessages === "function") renderMessages();
  if (typeof renderConversations === "function") renderConversations(activeConversationSearchQuery);
  if (typeof updateConversationSelection === "function") updateConversationSelection();
}

function applyMessagingChatbotSettings(settings) {
  AI_DISPLAY_NAME = String(settings?.chatbotName || "WaiGo").trim() || "WaiGo";
  if (isAiConversation()) {
    const nameElement = document.getElementById("conversationUserName");
    if (nameElement) nameElement.textContent = AI_DISPLAY_NAME;
    renderMessages();
  }
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
}

function bindMessagingWaigoStylePicker() {
  const button = document.getElementById("messagingWaigoStyleButton");
  const menu = document.getElementById("messagingWaigoStyleMenu");
  window.TravelwAIChatbotSettings?.bindPicker?.(button, menu, (settings, message) => {
    applyMessagingChatbotSettings(settings);
    if (typeof window.TravelwAIToast === "function") window.TravelwAIToast(message || "Đã đổi phong cách.", "success");
  });
}

function setMessageSendButtonLoading(isLoading, busyLabel = "Đang gửi") {
  const sendButton = document.querySelector(".message-input .send-btn");
  if (!sendButton) return;

  const loading = Boolean(isLoading);
  const canStopAi = loading && isAiConversation();
  sendButton.disabled = loading && !canStopAi || isCancellingAiJob;
  sendButton.classList.toggle("is-loading", loading);
  sendButton.classList.toggle("is-ai-stop", canStopAi && !isCancellingAiJob);
  sendButton.classList.toggle("is-stopping", isCancellingAiJob);
  sendButton.setAttribute("aria-busy", loading ? "true" : "false");
  sendButton.setAttribute("aria-label", isCancellingAiJob ? "Đang dừng AI" : canStopAi ? "Dừng AI" : loading ? busyLabel : "Gửi");
  sendButton.title = isCancellingAiJob ? "Đang dừng AI..." : canStopAi ? "Bấm để dừng AI" : loading ? busyLabel : "Gửi";
  if (isAiConversation()) setAiSuggestionButtonsDisabled(loading);
}

function isAiConversation(conversation = currentConversation) {
  return conversation?.id === AI_CONVERSATION_ID || conversation?.is_ai === true;
}

function isAiMessageSender(message) {
  return [AI_CONVERSATION_ID, "travelwai-support"].includes(String(message?.sender_id || ""));
}

function getAiHistoryStorageKey() {
  const owner = getCurrentUserId() || currentUser?.email || localStorage.getItem("userEmail") || "guest";
  return `${AI_HISTORY_PREFIX}:${String(owner).toLowerCase()}`;
}

function getAiJobStorageKey() {
  const owner = getCurrentUserId() || currentUser?.email || localStorage.getItem("userEmail") || "guest";
  return `${AI_JOB_STORAGE_PREFIX}:${String(owner).toLowerCase()}`;
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
    if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-job-updated", { detail: { source: "messaging", jobId: cleanJobId } }));
  } catch (_) {}
}

function clearActiveAiJobId(jobId, notify = true) {
  const current = readActiveAiJobId();
  if (jobId && current && current !== jobId) return;
  activeAiJobId = "";
  try {
    localStorage.removeItem(getAiJobStorageKey());
    if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-job-updated", { detail: { source: "messaging", jobId: "" } }));
  } catch (_) {}
}

function loadAiMessages() {
  try {
    const parsed = JSON.parse(localStorage.getItem(getAiHistoryStorageKey()) || "[]");
    return Array.isArray(parsed) ? parsed.filter((item) => !item?.is_system_welcome) : [];
  } catch (_) {
    return [];
  }
}

function saveAiMessages(messages, notify = true, changeKind = "updated") {
  try {
    localStorage.setItem(getAiHistoryStorageKey(), JSON.stringify((messages || []).slice(-100)));
    if (notify) window.dispatchEvent(new CustomEvent("travelwai:ai-history-updated", { detail: { source: "messaging", kind: changeKind } }));
  } catch (_) {}
}

function getAiConversation() {
  const history = loadAiMessages();
  const last = history[history.length - 1];
  return {
    id: AI_CONVERSATION_ID,
    is_ai: true,
    participants: [],
    last_message: last?.content || `Trợ lý chuyến đi ${AI_DISPLAY_NAME}`,
    last_message_time: last?.timestamp || last?.time_sent || ""
  };
}

function upsertAiJobReply(jobId, text, isError, isFinal = false) {
  const cleanText = decodeUnicodeEscapes(String(text || "").trim());
  if (!cleanText || !jobId) return false;
  const messageId = `${isError ? "ai-job-error" : "ai-job-reply"}-${jobId}`;
  const messages = loadAiMessages();
  const index = messages.findIndex((message) => String(message?.id || "") === messageId);
  const reply = {
    id: messageId,
    sender_id: AI_CONVERSATION_ID,
    sender_info: { id: AI_CONVERSATION_ID, username: AI_DISPLAY_NAME, displayName: AI_DISPLAY_NAME, profilePic: AI_AVATAR_URL },
    content: cleanText,
    timestamp: index >= 0 ? (messages[index]?.timestamp || new Date().toISOString()) : new Date().toISOString()
  };
  const next = index >= 0
    ? messages.map((message, itemIndex) => itemIndex === index ? { ...message, ...reply } : message)
    : [...messages, reply];
  saveAiMessages(next, isFinal, isFinal ? "received" : "streaming");
  if (isAiConversation()) {
    currentMessages = next;
    renderMessages();
  }
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
  return true;
}

function appendAiJobReplyOnce(jobId, text, isError) {
  return upsertAiJobReply(jobId, text, isError, true);
}

function setAiJobRunning(running) {
  isAiSending = Boolean(running);
  if (!isAiConversation()) return;

  const input = document.getElementById("messageInput");
  if (input) input.disabled = isAiSending;
  setMessageSendButtonLoading(isAiSending, "AI đang trả lời");
  setAiSuggestionButtonsDisabled(isAiSending);
}

function scheduleAiJobPoll(jobId, delay = AI_JOB_POLL_MS) {
  clearTimeout(aiJobPollTimer);
  aiJobPollTimer = window.setTimeout(() => pollAiJob(jobId), delay);
}

async function pollAiJob(jobId) {
  const cleanJobId = String(jobId || "").trim();
  if (!cleanJobId) {
    setAiJobRunning(false);
    return;
  }

  activeAiJobId = cleanJobId;
  setAiJobRunning(true);

  aiJobPollController?.abort();
  const controller = new AbortController();
  aiJobPollController = controller;

  try {
    const response = await fetch(`${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}`, {
      headers: { Authorization: `Bearer ${getAuthToken()}`, "Content-Type": "application/json" },
      cache: "no-store",
      signal: controller.signal
    });
    const result = await response.json().catch(() => ({}));

    if ([401, 403, 404].includes(response.status)) {
      clearActiveAiJobId(cleanJobId);
      setAiJobRunning(false);
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
  setAiJobRunning(false);
  const input = document.getElementById("messageInput");
  if (isAiConversation()) input?.focus();
  return true;
}

async function streamAiJob(jobId) {
  const cleanJobId = String(jobId || "").trim();
  if (!cleanJobId) {
    setAiJobRunning(false);
    return;
  }

  activeAiJobId = cleanJobId;
  setAiJobRunning(true);
  clearTimeout(aiJobPollTimer);
  aiJobPollTimer = null;
  aiJobPollController?.abort();
  const controller = new AbortController();
  aiJobPollController = controller;

  try {
    const response = await fetch(`${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}/stream`, {
      headers: { Authorization: `Bearer ${getAuthToken()}`, "Content-Type": "application/json" },
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

async function cancelActiveAiJob() {
  if (!isAiSending || isCancellingAiJob) return;

  isCancellingAiJob = true;
  aiCancelRequested = true;
  clearTimeout(aiJobPollTimer);
  aiJobPollTimer = null;
  aiJobPollController?.abort();
  aiStartRequestController?.abort();
  setAiJobRunning(true);

  const cleanJobId = String(activeAiJobId || readActiveAiJobId() || "").trim();
  const endpoint = cleanJobId
    ? `${API_BASE_URL}/ai/chat/jobs/${encodeURIComponent(cleanJobId)}`
    : `${API_BASE_URL}/ai/chat/jobs/active`;

  try {
    const response = await fetch(endpoint, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${getAuthToken()}`, "Content-Type": "application/json" },
      cache: "no-store"
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok && response.status !== 404) {
      throw new Error(result.message || "Không thể dừng AI.");
    }

    clearActiveAiJobId(cleanJobId || undefined);
    isCancellingAiJob = false;
    setAiJobRunning(false);
    if (typeof window.TravelwAIToast === "function") window.TravelwAIToast("Đã dừng AI.", "success");
    else showError("Đã dừng AI.");
    document.getElementById("messageInput")?.focus();
  } catch (error) {
    isCancellingAiJob = false;
    aiCancelRequested = false;
    setAiJobRunning(true);
    if (cleanJobId) scheduleAiJobPoll(cleanJobId, 800);
    else window.setTimeout(resumeActiveAiJob, 800);
    showError(error?.message || "Không thể dừng AI.");
  }
}

async function resumeActiveAiJob() {
  const storedJobId = readActiveAiJobId();
  if (storedJobId) {
    streamAiJob(storedJobId);
    return;
  }

  const token = getAuthToken();
  if (!token) {
    setAiJobRunning(false);
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
      setAiJobRunning(false);
    }
  } catch (_) {

  }
}


function readMessagingCookie(name) {
  const prefix = `${encodeURIComponent(name)}=`;
  const cookie = document.cookie
    .split(";")
    .map((item) => item.trim())
    .find((item) => item.startsWith(prefix));

  if (!cookie) return "";

  try {
    return decodeURIComponent(cookie.substring(prefix.length));
  } catch (error) {
    console.warn(`Không thể đọc cookie ${name}:`, error);
    return cookie.substring(prefix.length);
  }
}

function getAuthToken() {
  return (
    sessionStorage.getItem("idToken") ||
    localStorage.getItem("idToken") ||
    readMessagingCookie("TravelwAIAuth") ||
    ""
  );
}

function checkAuth() {
  const token = getAuthToken();
  if (!token) return false;


  const localToken = localStorage.getItem("idToken");
  const expiration = localStorage.getItem("tokenExpiration");
  if (localToken && token === localToken && expiration) {
    const expirationTime = Number(expiration);
    if (Number.isFinite(expirationTime) && Date.now() >= expirationTime) {
      return false;
    }
  }

  return true;
}

window.addEventListener("travelwai:ai-history-cleared", function () {
  if (!isAiConversation()) return;
  currentMessages = [];
  renderMessages();
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
});

window.addEventListener("travelwai:ai-history-updated", function (event) {
  if (event?.detail?.source === "messaging") return;
  if (isAiConversation()) {
    const history = loadAiMessages();
    currentMessages = history;
    renderMessages();
  }
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
});

window.addEventListener("travelwai:ai-job-updated", function (event) {
  if (event?.detail?.source === "messaging") return;
  const jobId = String(event?.detail?.jobId || readActiveAiJobId() || "").trim();
  if (jobId) streamAiJob(jobId);
  else resumeActiveAiJob();
});

window.addEventListener("storage", function (event) {
  if (event.key === getAiJobStorageKey()) resumeActiveAiJob();
});

window.addEventListener("travelwai:brandingchange", event => {
  applyMessagingBranding(event?.detail || {});
});

window.addEventListener("travelwai:chatbot-settings-changed", event => {
  applyMessagingChatbotSettings(event?.detail || {});
});

document.addEventListener("DOMContentLoaded", function () {
  syncMessagingMobileViewport();
  initializeResizableMessagingLayout();
  initializeSidebarPanelMode();
  initializeAiSuggestionButtons();
  bindMessagingWaigoStylePicker();
  document.getElementById("messageInput")?.addEventListener("paste", handleMessagingImagePaste);
  const settingsPromise = window.TravelwAIChatbotSettings?.load?.() || Promise.resolve({ chatbotName: "WaiGo" });
  Promise.resolve(settingsPromise)
    .then(applyMessagingChatbotSettings)
    .finally(() => initializeMessaging().finally(resumeActiveAiJob));
});

function initializeAiSuggestionButtons() {
  document.querySelectorAll("#messageAiSuggestions .message-ai-suggestion").forEach((button) => {
    button.addEventListener("click", function () {
      const suggestion = String(button.dataset.aiSuggestion || "").trim();
      const input = document.getElementById("messageInput");
      if (!suggestion || !input || input.disabled || !isAiConversation()) return;

      input.value = suggestion;
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.focus();
      input.setSelectionRange?.(suggestion.length, suggestion.length);
    });
  });
}

function setAiSuggestionButtonsDisabled(disabled) {
  document.querySelectorAll("#messageAiSuggestions .message-ai-suggestion").forEach((button) => {
    button.disabled = Boolean(disabled);
  });
}

function syncMessagingMobileViewport() {
  const header = document.querySelector("body.messaging-page > header");
  const headerHeight = header ? Math.ceil(header.getBoundingClientRect().height) : 0;
  document.documentElement.style.setProperty("--twai-mobile-header-height", `${headerHeight}px`);
}

function setMobileConversationOpenState(isOpen) {
  syncMessagingMobileViewport();
  if (document.body && document.body.classList.contains("messaging-page")) {
    document.body.classList.toggle("mobile-conversation-open", Boolean(isOpen));
  }
}

window.addEventListener("resize", syncMessagingMobileViewport);
window.addEventListener("orientationchange", function () {
  setTimeout(syncMessagingMobileViewport, 160);
});

async function initializeMessaging() {
  try {
    showLoading(true);

    if (!checkAuth()) {
      window.location.href = "/login";
      return;
    }

    const token =
      getAuthToken();

    if (!token) {
      window.location.href = "/login";
      return;
    }

    currentUser = await getCurrentUser();

    if (!currentUser) {
      window.location.href = "/login";
      return;
    }

    await get_all_users();
    await refreshFriendsAndRequests(false);
    await loadConversations();
    startFriendAutoRefresh();

    setupSearchFunctionality();
    setupFriendSearchAutoHide();

    showLoading(false);
    const pageParams = new URLSearchParams(window.location.search);
    const styleStoreRequested = pageParams.get("styleStore") === "1"
      || /^(1|true|open)$/i.test(pageParams.get("store") || "");
    const aiChatRequested = pageParams.get("ai") === "1"
      || /^ai$/i.test(pageParams.get("chat") || "")
      || styleStoreRequested;

    if (aiChatRequested) {
      await selectConversation(getAiConversation());

      if (styleStoreRequested) {
        await window.TravelwAIChatbotSettings?.openStore?.();
        if (pageParams.get("payment") === "style-success" && typeof window.TravelwAIToast === "function") {
          window.TravelwAIToast("Thanh toán thành công. Phong cách đã mở khóa.", "success");
        }

        const cleanUrl = new URL(window.location.href);
        cleanUrl.searchParams.delete("styleStore");
        cleanUrl.searchParams.delete("store");
        cleanUrl.searchParams.delete("payment");
        window.history.replaceState({}, "", `${cleanUrl.pathname}${cleanUrl.search}${cleanUrl.hash}`);
      }
    } else {
      await handlePendingDirectChat();
    }
  } catch (error) {
    console.error("Lỗi khởi tạo tin nhắn:", error);
    showLoading(false);
    showError("Không thể tải tin nhắn. Vui lòng thử lại sau.");
  }
}

async function get_all_users(forceRefresh = false) {
  if (!forceRefresh) {
    const cachedUsers = readClientCache("users");
    if (Array.isArray(cachedUsers)) {
      all_users = cachedUsers;
      return all_users;
    }
  }

  const token =
    getAuthToken();
  const response = await fetch(`${API_BASE_URL}/users`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const data = await response.json();
  if (data.success) {
    all_users = data.data || [];
    saveClientCache("users", all_users, USERS_CACHE_TTL_MS);
    return all_users;
  }

  return all_users;
}

async function get_user_friendList_and_requests(forceRefresh = false) {
  if (!forceRefresh) {
    const cached = readClientCache("friends-and-requests");
    if (cached) {
      user_friendList = Array.isArray(cached.friends) ? cached.friends : [];
      friend_requests = Array.isArray(cached.pending) ? cached.pending : [];
      return { friends: user_friendList, pending: friend_requests };
    }
  }

  const token = getAuthToken();
  const response = await fetch(`${API_BASE_URL}/friend_requests`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const data = await response.json();
  if (data.success) {
    user_friendList = Array.isArray(data.friends) ? data.friends : (data.data || []);
    friend_requests = Array.isArray(data.pending) ? data.pending : [];

    user_friendList = user_friendList.map((friend) => {
      if (getUserId(friend)) return friend;
      const matchedUser = (all_users || []).find(
        (user) => (user.email || "").toLowerCase() === (friend.email || "").toLowerCase()
      );
      return matchedUser ? { ...matchedUser, ...friend, id: getUserId(matchedUser) } : friend;
    });

    const value = { friends: user_friendList, pending: friend_requests };
    saveClientCache("friends-and-requests", value, FRIEND_CACHE_TTL_MS);
    return value;
  }
  return { friends: user_friendList || [], pending: friend_requests || [] };
}

async function refreshFriendsAndRequests(showSpinner = true, forceRefresh = false) {
  const friendsList = document.getElementById("friendsList");
  const sidebarList = document.getElementById("conversationList");

  if (showSpinner) {
    if (friendsList) {
      friendsList.innerHTML = '<div class="loading-message compact">Đang làm mới danh sách bạn bè...</div>';
    }
    if (activeSidebarPanelMode === "friends" && sidebarList) {
      sidebarList.innerHTML = '<div class="loading-message">Đang làm mới danh sách bạn bè...</div>';
    }
  }

  await get_user_friendList_and_requests(forceRefresh);
  renderFriendsList();
  await makeFriendRequestBlock();
}

function startFriendAutoRefresh() {
  if (friendRefreshTimer) clearInterval(friendRefreshTimer);
  friendRefreshTimer = setInterval(async () => {
    if (document.hidden) return;
    try {
      await refreshFriendsAndRequests(false);
    } catch (error) {
      console.warn("Không thể tự động làm mới danh sách bạn bè:", error);
    }
  }, FRIEND_REFRESH_MS);
}

async function makeFriendRequestBlock() {
  const friendRequestBlock = document.getElementById("friendRequestBlock");
  const friendRequestList = document.getElementById("friendRequestList");
  if (!friendRequestBlock || !friendRequestList) return;

  friendRequestList.innerHTML = "";

  if (!friend_requests || friend_requests.length === 0) {
    friendRequestBlock.style.display = "none";
    return;
  }

  friend_requests.forEach((request) => {
    const requestElement = createFriendRequestElement(request);
    friendRequestList.appendChild(requestElement);
  });

  friendRequestBlock.style.display = "block";
}

function getUserId(user) {
  return user?.id || user?.uid || user?.localId || user?.user_id || user?.userId || "";
}

function getCurrentUserId() {
  return getUserId(currentUser) || currentUser?.localId || currentUser?.uid || localStorage.getItem("userId") || "";
}

function getClientCacheOwnerKey() {
  return getCurrentUserId() || (currentUser?.email || localStorage.getItem("userEmail") || "guest").toLowerCase();
}

function buildClientCacheKey(name) {
  return `travelwai:${CLIENT_CACHE_VERSION}:${getClientCacheOwnerKey()}:${name}`;
}

function readClientCache(name) {
  try {
    const raw = localStorage.getItem(buildClientCacheKey(name));
    if (!raw) return null;
    const cached = JSON.parse(raw);
    if (!cached || !cached.expiresAt || Date.now() >= cached.expiresAt) {
      localStorage.removeItem(buildClientCacheKey(name));
      return null;
    }
    return cached.value;
  } catch (error) {
    console.warn("Không đọc được cache:", name, error);
    return null;
  }
}

function saveClientCache(name, value, ttlMs) {
  try {
    localStorage.setItem(buildClientCacheKey(name), JSON.stringify({
      value,
      expiresAt: Date.now() + ttlMs
    }));
  } catch (error) {
    console.warn("Không lưu được cache:", name, error);
  }
}

function invalidateClientCache(name) {
  try {
    localStorage.removeItem(buildClientCacheKey(name));
  } catch { }
}

function valuesEqual(a, b) {
  return String(a || "").trim() !== "" && String(a || "").trim() === String(b || "").trim();
}

function isMessageFromCurrentUser(message) {
  const currentId = getCurrentUserId();
  const currentEmail = (currentUser?.email || localStorage.getItem("userEmail") || "").toLowerCase();
  const messageSenderId = message?.sender_id || message?.senderId || message?.user_id || message?.userId || getUserId(message?.sender_info);
  const messageSenderEmail = (message?.sender_email || message?.email || message?.sender_info?.email || "").toLowerCase();

  return valuesEqual(messageSenderId, currentId) || (currentEmail && messageSenderEmail === currentEmail);
}

function resolveAvatarUrl(value) {
  const profilePic = String(value || "").trim();
  if (!profilePic) return "logo/profile-icon-white.webp";
  if (profilePic.startsWith("http") || profilePic.startsWith("data:")) return profilePic;
  if (profilePic.startsWith("/")) return profilePic;
  return `${API_BASE_URL.replace("/api", "")}${profilePic}`;
}

function deriveNameFromEmail(email) {
  const localPart = String(email || "").split("@")[0];
  if (!localPart) return "";
  return localPart
    .replace(/[._-]+/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

function getUserDisplayName(user) {
  return (
    user?.username ||
    user?.name ||
    user?.fullName ||
    deriveNameFromEmail(user?.email) ||
    "Người dùng"
  );
}

function isUserOnline(user) {
  return user?.is_online === true || user?.isOnline === true || String(user?.presence_status || "").toLowerCase() === "online";
}

function getUserPresenceLabel(user) {
  return isUserOnline(user) ? "Đang online" : "Đang offline";
}

function getUserPresenceClass(user) {
  return isUserOnline(user) ? "online" : "offline";
}

function getUserAvatarUrl(user) {
  const profilePic = user?.profilePic || user?.photoURL || user?.avatar || user?.profile_picture_url;
  return profilePic ? resolveAvatarUrl(profilePic) : null;
}

function isGroupConversation(conversation) {
  return Boolean(conversation?.is_group) ||
    conversation?.conversation_type === "group" ||
    (Array.isArray(conversation?.participants) && conversation.participants.length > 2) ||
    (Array.isArray(conversation?.participant_ids) && conversation.participant_ids.length > 2);
}

function isDirectConversation(conversation) {
  return Boolean(conversation) && !isGroupConversation(conversation);
}

function getGroupDisplayName(conversation) {
  if (!conversation) return "Nhóm trò chuyện";
  if (conversation.group_name) return conversation.group_name;

  const currentId = getCurrentUserId();
  const names = (conversation.participants || [])
    .filter((user) => getUserId(user) !== currentId)
    .map((user) => getUserDisplayName(user))
    .filter(Boolean)
    .slice(0, 3);

  return names.length ? `Nhóm ${names.join(", ")}` : "Nhóm trò chuyện";
}

function getGroupMemberText(conversation) {
  const count = Number(conversation?.member_count || conversation?.participants?.length || conversation?.participant_ids?.length || 0);
  return count > 0 ? `${count} thành viên` : "Nhóm trò chuyện";
}

function getConversationNickname(conversation) {
  if (!conversation || isGroupConversation(conversation)) return "";

  const currentUserId = getCurrentUserId();
  const nicknames = conversation.nicknames || conversation.nickname_map || {};
  const nickname = nicknames && typeof nicknames === "object" ? nicknames[currentUserId] : "";
  return typeof nickname === "string" ? nickname.trim() : "";
}

function getDirectConversationDisplayName(conversation) {
  const nickname = getConversationNickname(conversation);
  if (nickname) return nickname;
  return getUserDisplayName(conversation?.other_user_info || getOtherParticipant(conversation));
}

function isAdminSupportChatRequested() {
  const params = new URLSearchParams(window.location.search);
  return params.get("admin") === "1" || /^(admin|support)$/i.test(params.get("chat") || "");
}

function getDirectChatEmailFromQuery() {
  const params = new URLSearchParams(window.location.search);
  const directEmail = params.get("email") || params.get("userEmail") || params.get("adminEmail") || params.get("to") || "";
  return directEmail.trim().toLowerCase();
}

async function ensureAdminSupportConversation() {
  const token = getAuthToken();
  if (!token) throw new Error("Bạn cần đăng nhập để nhắn tin với Admin chính.");

  const response = await fetch(`${API_BASE_URL}/support/admin-conversation`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok || result.success === false) {
    throw new Error(result.detail || result.message || "Không thể mở hội thoại Admin chính.");
  }

  const conversationId = result.conversation_id || result.conversationId || result.data?.conversation_id || result.data?.conversationId;
  if (!conversationId) throw new Error("Không đọc được mã hội thoại Admin chính.");

  invalidateClientCache("conversations");
  await loadConversations(true);
  const conversation = (conversations || []).find((item) => String(item.id || item.conversation_id || item.conversationId || "") === String(conversationId));
  return conversation || {
    id: conversationId,
    conversation_id: conversationId,
    conversation_type: "direct",
    is_group: false,
    group_name: "Nhắn tin Admin chính",
    participants: [currentUser || {}],
    participant_ids: [getCurrentUserId()],
    last_message: "",
    last_message_time: "",
  };
}

function findUserByEmail(email) {
  const targetEmail = String(email || "").trim().toLowerCase();
  if (!targetEmail) return null;
  return (all_users || []).find((user) => String(user?.email || "").trim().toLowerCase() === targetEmail) || null;
}

async function handlePendingDirectChat() {
  const adminRequested = isAdminSupportChatRequested();
  const targetEmail = getDirectChatEmailFromQuery();
  if (!adminRequested && !targetEmail) return false;

  try {
    let conversation = null;
    let openedLabel = "hội thoại Admin chính";

    if (adminRequested) {
      conversation = await ensureAdminSupportConversation();
    } else {
      if (String(currentUser?.email || "").trim().toLowerCase() === targetEmail) {
        try { localStorage.removeItem(ADMIN_PENDING_MESSAGE_KEY); } catch (_) {}
        showMessagingToast("Bạn đang đăng nhập bằng tài khoản này, không thể tự nhắn cho chính mình.", "info");
        window.history.replaceState({}, document.title, window.location.pathname);
        return true;
      }

      let targetUser = findUserByEmail(targetEmail);
      if (!targetUser) {
        await get_all_users(true);
        targetUser = findUserByEmail(targetEmail);
      }

      if (!targetUser) {
        showError(`Không tìm thấy tài khoản ${targetEmail}.`);
        return true;
      }

      openedLabel = getUserDisplayName(targetUser) || targetEmail;
      conversation = await ensureConversationWithUser(targetUser);
    }

    await selectConversation(conversation);

    let pendingMessage = "";
    try {
      pendingMessage = localStorage.getItem(ADMIN_PENDING_MESSAGE_KEY) || "";
      localStorage.removeItem(ADMIN_PENDING_MESSAGE_KEY);
    } catch (_) {}

    const messageInput = document.getElementById("messageInput");
    if (pendingMessage && messageInput) {
      messageInput.value = pendingMessage;
      try {
        await waitForWebSocketOpen(5000);
        await sendMessage();
        showMessagingToast("Đã gửi tin nhắn.", "success");
      } catch (error) {
        showMessagingToast("Đã mở hội thoại Admin.", "info");
        messageInput.focus();
      }
    } else {
      showMessagingToast(`Đã mở ${openedLabel}.`, "success");
    }

    window.history.replaceState({}, document.title, window.location.pathname);
  } catch (error) {
    console.error("Không thể mở hội thoại:", error);
    showError(error.message || "Không thể mở hội thoại.");
  }

  return true;
}

function renderFriendsList() {
  const friendsList = document.getElementById("friendsList");
  const pendingRequests = friend_requests || [];
  const friends = user_friendList || [];

  if (friendsList) {
    friendsList.innerHTML = "";

    if (pendingRequests.length === 0 && friends.length === 0) {
      friendsList.innerHTML = `
        <div class="empty-friends">
          Chưa có bạn bè. Hãy tìm người dùng ở ô <strong>Thêm bạn bè</strong>, gửi lời mời và chờ đối phương chấp nhận.
        </div>`;
    } else {
      pendingRequests.forEach((request) => {
        friendsList.appendChild(createPendingFriendElement(request));
      });

      friends.forEach((friend) => {
        friendsList.appendChild(createFriendElement(friend));
      });
    }
  }

  if (activeSidebarPanelMode === "friends") {
    renderFriendsPanel(activeFriendsSearchQuery);
  }
}

function renderFriendsPanel(searchQuery = activeFriendsSearchQuery) {
  const sidebarList = document.getElementById("conversationList");
  if (!sidebarList) return;

  const normalizedQuery = normalizeForSearch(searchQuery);
  const pendingRequests = friend_requests || [];
  const friends = user_friendList || [];
  const allItems = [
    ...pendingRequests.map((request) => ({ type: "request", data: request })),
    ...friends.map((friend) => ({ type: "friend", data: friend })),
  ];

  sidebarList.innerHTML = "";

  if (allItems.length === 0) {
    sidebarList.innerHTML = `
      <div class="empty-friends sidebar-empty-state">
        Chưa có bạn bè. Hãy tìm người dùng ở ô <strong>Thêm bạn bè</strong>, gửi lời mời và chờ đối phương chấp nhận.
      </div>`;
    return;
  }

  const filteredItems = allItems.filter((item) => {
    const user = item.data || {};
    const text = `${getUserDisplayName(user)} ${user.email || ""} ${item.type === "request" ? "lời mời kết bạn" : "bạn bè nhắn tin"}`;
    return !normalizedQuery || normalizeForSearch(text).includes(normalizedQuery);
  });

  if (filteredItems.length === 0) {
    sidebarList.innerHTML = '<div class="loading-message">Không tìm thấy bạn bè</div>';
    return;
  }

  filteredItems.forEach((item) => {
    const element = item.type === "request"
      ? createPendingFriendElement(item.data, searchQuery)
      : createFriendElement(item.data, searchQuery);
    sidebarList.appendChild(element);
  });
}

function createFriendElement(friend, searchQuery = "") {
  const item = document.createElement("div");
  item.className = "conversation-item friend-card sidebar-friend-item";
  item.title = "Bấm để mở hoặc tạo cuộc trò chuyện";
  item.onclick = () => startChatWithUser(friend);

  const displayName = getUserDisplayName(friend);
  const friendEmail = friend?.email || "";
  const avatarUrl = getUserAvatarUrl(friend) || "logo/profile-icon-white.webp";

  item.innerHTML = `
    <div class="user-avatar friend-avatar">
      <img loading="lazy" decoding="async" src="${escapeHtml(avatarUrl)}" alt="${escapeHtml(displayName)}" onerror="this.src='logo/profile-icon-white.webp'" />
    </div>
    <div class="conversation-item-info friend-info">
      <div class="conversation-item-name friend-name">${highlightSearchTerm(displayName, searchQuery)}</div>
      <div class="conversation-presence ${getUserPresenceClass(friend)}"><span class="presence-dot"></span>${getUserPresenceLabel(friend)}</div>
      <div class="conversation-item-message friend-email">${highlightSearchTerm(friendEmail || "Bạn bè", searchQuery)}</div>
    </div>
    <div class="conversation-item-meta">
      <span class="friend-action-pill">Nhắn tin</span>
    </div>
  `;

  return item;
}

function createPendingFriendElement(request, searchQuery = "") {
  const item = document.createElement("div");
  item.className = "conversation-item friend-card friend-card-request sidebar-friend-item";
  item.title = "Lời mời kết bạn";

  const displayName = getUserDisplayName(request);
  const requestEmail = request.email || "";
  const avatarUrl = getUserAvatarUrl(request) || "logo/profile-icon-white.webp";

  item.innerHTML = `
    <div class="user-avatar friend-avatar">
      <img loading="lazy" decoding="async" src="${escapeHtml(avatarUrl)}" alt="${escapeHtml(displayName)}" onerror="this.src='logo/profile-icon-white.webp'" />
    </div>
    <div class="conversation-item-info friend-info">
      <div class="conversation-item-name friend-name">${highlightSearchTerm(displayName, searchQuery)}</div>
      <div class="conversation-item-message friend-request-label">${highlightSearchTerm("Lời mời kết bạn", searchQuery)}</div>
    </div>
    <div class="conversation-item-meta friend-request-actions">
      <button type="button" class="friend-request-icon-btn accept" aria-label="Đồng ý" title="Đồng ý">
        ${getInlineIcon("check")}
      </button>
      <button type="button" class="friend-request-icon-btn decline" aria-label="Từ chối" title="Từ chối">
        ${getInlineIcon("x")}
      </button>
    </div>
  `;

  const acceptButton = item.querySelector(".friend-request-icon-btn.accept");
  const declineButton = item.querySelector(".friend-request-icon-btn.decline");

  acceptButton.addEventListener("click", (event) => {
    event.stopPropagation();
    handleFriendRequestAction(requestEmail, "accepted", item, { silent: true });
  });
  declineButton.addEventListener("click", (event) => {
    event.stopPropagation();
    handleFriendRequestAction(requestEmail, "declined", item, { silent: true });
  });

  return item;
}

function isFriend(user) {
  const email = (user?.email || "").toLowerCase();
  const id = getUserId(user);
  return (user_friendList || []).some((friend) => {
    return (friend.email || "").toLowerCase() === email || (getUserId(friend) && getUserId(friend) === id);
  });
}

async function getCurrentUser() {
  try {
    const token =
      getAuthToken();

    const response = await fetch(`${API_BASE_URL}/profile`, {
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
    });

    if (response.ok) {
      const data = await response.json();
      return data.user;
    } else {
      const errorData = await response.text();
      console.error("Yêu cầu hồ sơ thất bại:", response.status, errorData);
      throw new Error(`Không lấy được thông tin người dùng: ${response.status}`);
    }
  } catch (error) {
    console.error("Lỗi lấy thông tin người dùng:", error);
    return null;
  }
}

async function loadConversations(forceRefresh = false) {
  try {
    if (!forceRefresh) {
      const cachedConversations = readClientCache("conversations");
      if (Array.isArray(cachedConversations)) {
        conversations = cachedConversations;
        renderConversations();
        return conversations;
      }
    }

    const token =
      getAuthToken();
    const response = await fetch(`${API_BASE_URL}/conversations`, {
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
    });

    if (response.ok) {
      const data = await response.json();
      conversations = data.data || [];
      saveClientCache("conversations", conversations, CONVERSATION_CACHE_TTL_MS);
      renderConversations();
      return conversations;
    } else {
      throw new Error("Không thể tải cuộc trò chuyện");
    }
  } catch (error) {
    console.error("Lỗi tải cuộc trò chuyện:", error);
    document.getElementById("conversationList").innerHTML =
      '<div class="loading-message">Không thể tải cuộc trò chuyện</div>';
    return conversations;
  }
}

function renderConversations(searchQuery = activeConversationSearchQuery) {
  if (activeSidebarPanelMode !== "conversations") return;

  const conversationList = document.getElementById("conversationList");
  if (!conversationList) return;

  conversationList.innerHTML = "";

  const allConversations = [getAiConversation(), ...(conversations || [])];
  const normalizedQuery = normalizeForSearch(searchQuery);
  const filteredConversations = allConversations.filter((conversation) => {
    if (!normalizedQuery) return true;
    const displayName = getConversationDisplayName(conversation);
    const lastMessage = getMessagePreview(conversation.last_message) || "Chưa có tin nhắn";
    const participantText = (conversation.participants || [])
      .map((user) => `${getUserDisplayName(user)} ${user.email || ""}`)
      .join(" ");
    return (
      normalizeForSearch(displayName).includes(normalizedQuery) ||
      normalizeForSearch(lastMessage).includes(normalizedQuery) ||
      normalizeForSearch(participantText).includes(normalizedQuery)
    );
  });

  if (filteredConversations.length === 0) {
    conversationList.innerHTML = '<div class="loading-message">Không tìm thấy cuộc trò chuyện</div>';
    return;
  }

  filteredConversations.forEach((conversation) => {
    const conversationItem = createConversationElement(conversation, searchQuery);
    conversationList.appendChild(conversationItem);
  });
}

function getConversationDisplayName(conversation) {
  if (isAiConversation(conversation)) return AI_DISPLAY_NAME;
  if (isGroupConversation(conversation)) return getGroupDisplayName(conversation);
  return getDirectConversationDisplayName(conversation);
}

function createConversationElement(conversation, searchQuery = "") {
  const div = document.createElement("div");
  const isGroup = isGroupConversation(conversation);
  const isAi = isAiConversation(conversation);
  div.className = `conversation-item${isGroup ? " group-conversation-item" : ""}${isAi ? " ai-conversation-item" : ""}`;
  div.dataset.conversationId = conversation.id || "";
  div.onclick = () => selectConversation(conversation);

  const otherParticipant = isAi
    ? { id: AI_CONVERSATION_ID, username: AI_DISPLAY_NAME, name: AI_DISPLAY_NAME, email: "Trợ lý du lịch", profilePic: AI_AVATAR_URL }
    : isGroup
      ? { username: getGroupDisplayName(conversation), name: getGroupDisplayName(conversation), email: getGroupMemberText(conversation), profilePic: null }
      : getOtherParticipant(conversation);
  const displayName = getConversationDisplayName(conversation);
  const lastMessage = getMessagePreview(conversation.last_message) || (isGroup ? getGroupMemberText(conversation) : "Chưa có tin nhắn");
  const unreadCount = conversation.unread_count?.[getCurrentUserId()] || 0;
  const avatarSrc = isAi ? AI_AVATAR_URL : (getUserAvatarUrl(otherParticipant) || "logo/profile-icon-white.webp");

  div.dataset.conversationName = displayName;
  div.dataset.lastMessage = lastMessage;

  const avatarMarkup = isAi
    ? `<div class="user-avatar waigo-avatar-shell waigo-logo-background" role="img" aria-label="${escapeHtml(displayName)}"></div>`
    : `<div class="user-avatar"><img loading="lazy" decoding="async" src="${escapeHtml(avatarSrc)}" alt="${escapeHtml(displayName)}" onerror="this.src='logo/profile-icon-white.webp'" /></div>`;

  div.innerHTML = `
        ${avatarMarkup}
        <div class="conversation-item-info">
            <div class="conversation-item-name">${highlightSearchTerm(displayName, searchQuery)}</div>
            ${!isAi && !isGroup ? `<div class="conversation-presence ${getUserPresenceClass(otherParticipant)}"><span class="presence-dot"></span>${getUserPresenceLabel(otherParticipant)}</div>` : ""}
            <div class="conversation-item-message">
                <span data-chat-message-preview data-no-translate>${highlightSearchTerm(lastMessage, searchQuery)}</span>
            </div>
        </div>
        <div class="conversation-item-meta">
            <div class="conversation-item-time">
                ${conversation.last_message_time ? formatTime(conversation.last_message_time) : ""}
            </div>
            ${unreadCount > 0 ? `<div class="unread-badge">${unreadCount}</div>` : ""}
        </div>
    `;

  return div;
}

function getOtherParticipant(conversation) {
  if (isGroupConversation(conversation)) {
    return {
      id: conversation?.id || "group",
      name: getGroupDisplayName(conversation),
      email: getGroupMemberText(conversation),
      profilePic: null,
    };
  }

  const currentUserId = getCurrentUserId();

  if (conversation.participants && Array.isArray(conversation.participants)) {
    const other = conversation.participants.find((p) => {
      return (
        p &&
        p.id !== currentUserId &&
        p.localId !== currentUserId &&
        p.uid !== currentUserId
      );
    });

    if (other) {
      return {
        id: other.id || other.localId || other.uid,
        name: getUserDisplayName(other),
        email: other.email,
        profilePic: other.profilePic || other.photoURL || other.avatar,
        is_online: other.is_online === true || other.isOnline === true,
        isOnline: other.is_online === true || other.isOnline === true,
        presence_status: other.presence_status || (other.is_online === true || other.isOnline === true ? "online" : "offline"),
        last_seen_at: other.last_seen_at || other.lastSeenAt || null,
      };
    }
  }

  console.warn(
    "Không tìm thấy người còn lại trong cuộc trò chuyện:",
    conversation.id
  );
  return {
    id: "unknown",
    name: getUserDisplayName(conversation?.other_user_info) || deriveNameFromEmail(conversation?.other_user_info?.email) || "Bạn bè",
    email: conversation?.other_user_info?.email || "",
    profilePic: conversation?.other_user_info?.profilePic || null,
    is_online: conversation?.other_user_info?.is_online === true || conversation?.other_user_info?.isOnline === true,
    isOnline: conversation?.other_user_info?.is_online === true || conversation?.other_user_info?.isOnline === true,
    presence_status: conversation?.other_user_info?.presence_status || "offline",
    last_seen_at: conversation?.other_user_info?.last_seen_at || conversation?.other_user_info?.lastSeenAt || null,
  };
}

async function selectConversation(conversation) {
  if (activeSidebarPanelMode !== "conversations") {
    setSidebarPanelMode("conversations");
  }


  if (isAiConversation(conversation)) {
    if (websocket) {
      websocket.onclose = null;
      websocket.close();
      websocket = null;
    }
    currentConversation = getAiConversation();
    currentMessages = loadAiMessages();
    showConversationInterface();
    updateConversationSelection();
    renderMessages();
    return;
  }
  if (currentConversation?.id === conversation.id && websocket) {
    return;
  }

  currentConversation = conversation;
  showConversationInterface();
  updateConversationSelection();

  if (websocket) {
    websocket.onclose = null;
    websocket.close();
  }

  await loadMessages(conversation.id);

  const token =
    getAuthToken();
  if (!token) {
    showError("Không tìm thấy token đăng nhập.");
    return;
  }

  const wsBaseUrl = API_BASE_URL.replace(/^http/, "ws").replace("/api", "");
  const wsUrl = `${wsBaseUrl}/ws/conversations/${conversation.id}?token=${encodeURIComponent(token)}`;

  websocket = new WebSocket(wsUrl);

  websocket.onopen = () => {};

  websocket.onmessage = (event) => {
    const message = JSON.parse(event.data);

    if (message.type === "status") {
      handlePresenceStatus(message);
    } else if (message.type === "error") {
      console.error("Lỗi WebSocket từ máy chủ:", message.message);
      showError(message.message);
    } else {
      currentMessages.push(message);
      if (currentConversation?.id) saveClientCache(`messages:${currentConversation.id}`, currentMessages, MESSAGE_CACHE_TTL_MS);
      invalidateClientCache("conversations");
      appendMessage(message);
      updateConversationPreview(message);
      const otherParticipant = currentConversation && !isGroupConversation(currentConversation) ? getOtherParticipant(currentConversation) : null;
      if (otherParticipant && message.sender_id === otherParticipant.id) {
        updateConversationUserStatus("online", getConversationDisplayName(currentConversation));
      }
    }
  };

  websocket.onerror = (error) => {
    console.error("Lỗi WebSocket:", error);
    showError("Kết nối tin nhắn bị gián đoạn. Vui lòng tải lại trang.");
  };

  websocket.onclose = (event) => {
    websocket = null;
    if (!event.wasClean) {
      showError("Mất kết nối tin nhắn. Đang cố gắng kết nối lại...");
    }
  };
}

function updateConversationSelection() {
  const conversationItems = document.querySelectorAll(".conversation-item");
  conversationItems.forEach((item) => {
    item.classList.remove("selected", "active");
  });

  if (!currentConversation?.id) return;

  const selectedItem = Array.from(conversationItems).find(
    (item) => item.dataset.conversationId === currentConversation.id
  );

  if (selectedItem) {
    selectedItem.classList.add("selected", "active");
  }
}

function showConversationInterface() {
  setMobileConversationOpenState(true);
  document.getElementById("welcomeScreen").style.display = "none";

  document.getElementById("conversationHeader").style.display = "flex";
  document.getElementById("messagesContainer").style.display = "block";
  document.getElementById("messageInputContainer").style.display = "block";

  const isAiMode = isAiConversation(currentConversation);
  const isGroupMode = !isAiMode && isGroupConversation(currentConversation);
  const styleButton = document.getElementById("messagingWaigoStyleButton");
  const styleStoreButton = document.getElementById("messagingWaigoStyleStoreButton");
  const styleMenu = document.getElementById("messagingWaigoStyleMenu");
  if (styleButton) styleButton.hidden = !isAiMode;
  if (styleStoreButton) styleStoreButton.hidden = !isAiMode;
  if (!isAiMode && styleMenu) styleMenu.hidden = true;
  const otherParticipant = isAiMode
    ? { id: AI_CONVERSATION_ID, name: AI_DISPLAY_NAME, username: AI_DISPLAY_NAME, profilePic: AI_AVATAR_URL }
    : isGroupMode
    ? { name: getGroupDisplayName(currentConversation), username: getGroupDisplayName(currentConversation), profilePic: null }
    : getOtherParticipant(currentConversation);

  document.getElementById("conversationUserName").textContent = isAiMode
    ? AI_DISPLAY_NAME
    : isGroupMode
      ? getGroupDisplayName(currentConversation)
      : getConversationDisplayName(currentConversation);

  const statusElement = document.getElementById("conversationUserStatus");
  if (isAiMode) {
    if (statusElement) {
      statusElement.textContent = "Sẵn sàng";
      statusElement.classList.add("online");
      statusElement.classList.remove("offline");
    }
  } else if (isGroupMode) {
    if (statusElement) {
      statusElement.textContent = getGroupMemberText(currentConversation);
      statusElement.classList.remove("online");
      statusElement.classList.add("offline");
    }
  } else {
    updateConversationUserStatus(isUserOnline(otherParticipant) ? "online" : "offline", getConversationDisplayName(currentConversation));
  }

  syncConversationNameButtonVisibility();
  syncRemoveFriendButtonVisibility(otherParticipant);
  const attachmentButton = document.querySelector(".message-input .attachment-btn");
  const attachmentInput = document.getElementById("chatAttachmentInput");
  const messageInput = document.getElementById("messageInput");
  const aiSuggestions = document.getElementById("messageAiSuggestions");
  if (aiSuggestions) aiSuggestions.hidden = !isAiMode;
  setAiSuggestionButtonsDisabled(isAiMode && isAiSending);
  if (attachmentButton) {
    attachmentButton.hidden = false;
    attachmentButton.disabled = false;
    attachmentButton.classList.toggle("ai-attachment-btn", isAiMode);
    attachmentButton.setAttribute("aria-label", isAiMode ? "Đính kèm ảnh hoặc video" : "Đính kèm tệp");
    attachmentButton.setAttribute("title", isAiMode ? "Đính kèm ảnh hoặc video" : "Đính kèm tệp");
  }
  if (attachmentInput) {
    attachmentInput.disabled = false;
    attachmentInput.accept = isAiMode
      ? "image/png,image/jpeg,image/webp,image/gif"
      : "image/*,video/*,audio/*,.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.zip";
  }
  if (messageInput) {
    messageInput.placeholder = isAiMode
      ? "Nhập câu hỏi..."
      : "Nhập tin nhắn...";
    messageInput.maxLength = isAiMode ? 4000 : MAX_CHAT_MESSAGE_LENGTH;
    messageInput.disabled = isAiMode && isAiSending;
  }
  setMessageSendButtonLoading(isAiMode && isAiSending, "AI đang trả lời");
  const translateButton = document.getElementById("translateConversationBtn");
  if (translateButton) {
    window.TravelwAITranslation?.refreshConversationControl?.(
      translateButton,
      document.getElementById("messagesList")
    );
  }

  const headerAvatar = document.getElementById("conversationUserAvatar");
  const headerAvatarShell = headerAvatar?.parentElement;
  headerAvatarShell?.classList.toggle("waigo-avatar-shell", isAiMode);
  headerAvatarShell?.classList.toggle("waigo-logo-background", isAiMode);
  if (headerAvatar) {
    if (isAiMode) {
      headerAvatar.removeAttribute("src");
      headerAvatar.hidden = true;
      headerAvatar.classList.remove("waigo-brand-avatar");
      headerAvatar.onerror = null;
    } else {
      headerAvatar.hidden = false;
      headerAvatar.src = getUserAvatarUrl(otherParticipant) || "logo/profile-icon-white.webp";
      headerAvatar.onerror = () => (headerAvatar.src = "logo/profile-icon-white.webp");
    }
  }
}

function getConversationNameActionIcon() {
  return `
    <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
      <path d="M12 20h9" />
      <path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z" />
    </svg>
  `;
}

function syncConversationNameButtonVisibility() {
  const nameButton = document.getElementById("conversationNameBtn");
  if (!nameButton) return;

  if (!currentConversation) {
    nameButton.hidden = true;
    nameButton.disabled = true;
    nameButton.onclick = null;
    return;
  }

  if (isAiConversation(currentConversation)) {
    const label = "Đặt biệt danh";
    nameButton.hidden = false;
    nameButton.disabled = true;
    nameButton.removeAttribute("aria-hidden");
    nameButton.removeAttribute("tabindex");
    nameButton.setAttribute("aria-label", label);
    nameButton.setAttribute("title", label);
    nameButton.innerHTML = `${getConversationNameActionIcon()}<span class="sr-only">${label}</span>`;
    nameButton.onclick = null;
    return;
  }

  const label = isGroupConversation(currentConversation) ? "Đổi tên nhóm" : "Đặt biệt danh";
  nameButton.hidden = false;
  nameButton.disabled = false;
  nameButton.setAttribute("aria-label", label);
  nameButton.setAttribute("title", label);
  nameButton.innerHTML = `${getConversationNameActionIcon()}<span class="sr-only">${label}</span>`;
  nameButton.onclick = openConversationNameEditor;
}

async function openConversationNameEditor() {
  if (!currentConversation?.id) return;

  const isGroupMode = isGroupConversation(currentConversation);
  const currentName = isGroupMode ? getGroupDisplayName(currentConversation) : getConversationNickname(currentConversation);
  const defaultName = isGroupMode ? getGroupDisplayName(currentConversation) : getConversationDisplayName(currentConversation);
  const label = isGroupMode ? "Nhập tên nhóm mới:" : "Nhập biệt danh mới:";
  const nextName = await window.TravelwAIPrompt(label, currentName || defaultName || "");

  if (nextName === null) return;

  const cleanName = nextName.trim();
  if (!cleanName) {
    showError("Tên không được để trống.");
    return;
  }

  try {
    showLoading(true);
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/conversations/${currentConversation.id}/name`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(isGroupMode ? { group_name: cleanName } : { nickname: cleanName }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể đổi tên cuộc trò chuyện.");
    }

    await loadConversations(true);
    const refreshed = conversations.find((item) => item.id === currentConversation.id);
    if (refreshed) currentConversation = refreshed;
    showConversationInterface();
    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
    window.TravelwAIToast(isGroupMode ? "Đã đổi tên nhóm." : "Đã cập nhật biệt danh.");
  } catch (error) {
    console.error("Lỗi đổi tên cuộc trò chuyện:", error);
    showError(error.message || "Không thể đổi tên cuộc trò chuyện.");
  } finally {
    showLoading(false);
  }
}

function getFriendActionIcon(mode) {
  if (mode === "add" || mode === "sent") {
    return `
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M19 8v6" />
        <path d="M22 11h-6" />
      </svg>
    `;
  }

  return `
    <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
      <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
      <circle cx="9" cy="7" r="4" />
      <path d="M22 11h-6" />
    </svg>
  `;
}

function getFriendRequestKeys(user) {
  const keys = [];
  const id = getUserId(user);
  const email = (user?.email || "").toLowerCase();
  if (id && id !== "unknown") keys.push(`id:${id}`);
  if (email) keys.push(`email:${email}`);
  return keys;
}

function markOutgoingFriendRequest(user) {
  getFriendRequestKeys(user).forEach((key) => outgoingFriendRequestKeys.add(key));
}

function hasOutgoingFriendRequest(user) {
  return getFriendRequestKeys(user).some((key) => outgoingFriendRequestKeys.has(key));
}

function setFriendActionButtonMode(button, mode, disabled = false) {
  if (!button) return;

  button.removeAttribute("aria-hidden");
  button.removeAttribute("tabindex");

  const label =
    mode === "remove"
      ? "Xóa bạn bè"
      : mode === "sent"
        ? "Đã gửi yêu cầu kết bạn"
        : "Thêm bạn bè";

  button.hidden = false;
  button.disabled = disabled;
  button.dataset.friendAction = mode;
  button.setAttribute("aria-label", label);
  button.setAttribute("title", label);
  button.innerHTML = `${getFriendActionIcon(mode)}<span class="sr-only">${label}</span>`;

  if (disabled) {
    button.onclick = null;
  } else if (mode === "remove") {
    button.onclick = removeFriendFromCurrentConversation;
  } else if (mode === "add") {
    button.onclick = addFriendFromCurrentConversation;
  } else {
    button.onclick = null;
  }
}

function syncRemoveFriendButtonVisibility(otherParticipant = null) {
  const removeFriendButton = document.getElementById("removeFriendBtn");
  if (!removeFriendButton) return;


  if (isAiConversation(currentConversation)) {
    setFriendActionButtonMode(removeFriendButton, "add", true);
    return;
  }

  if (isGroupConversation(currentConversation)) {
    removeFriendButton.hidden = true;
    removeFriendButton.disabled = true;
    return;
  }

  const participant = otherParticipant || (currentConversation ? getOtherParticipant(currentConversation) : null);
  if (!participant || getUserId(participant) === "unknown") {
    removeFriendButton.hidden = true;
    removeFriendButton.disabled = true;
    return;
  }

  if (isFriend(participant)) {
    setFriendActionButtonMode(removeFriendButton, "remove");
  } else if (hasOutgoingFriendRequest(participant)) {
    setFriendActionButtonMode(removeFriendButton, "sent", true);
  } else {
    setFriendActionButtonMode(removeFriendButton, "add");
  }
}

async function loadMessages(conversationId, forceRefresh = false) {
  try {
    showLoading(true);
    const cacheName = `messages:${conversationId}`;
    if (!forceRefresh) {
      const cachedMessages = readClientCache(cacheName);
      if (Array.isArray(cachedMessages)) {
        currentMessages = cachedMessages;
        renderMessages();
        return currentMessages;
      }
    }

    const token =
      getAuthToken();
    const response = await fetch(
      `${API_BASE_URL}/conversations/${conversationId}/messages`,
      {
        headers: { Authorization: `Bearer ${token}` },
      }
    );

    if (response.ok) {
      const data = await response.json();
      currentMessages = data.data || [];
      saveClientCache(cacheName, currentMessages, MESSAGE_CACHE_TTL_MS);
      renderMessages();
      return currentMessages;
    } else {
      throw new Error(`Không thể tải tin nhắn: ${response.statusText}`);
    }
  } catch (error) {
    console.error(`Lỗi tải tin nhắn cho ${conversationId}:`, error);
    document.getElementById("messagesList").innerHTML =
      '<div class="error-message">Không thể tải tin nhắn.</div>';
    return currentMessages;
  } finally {
    showLoading(false);
  }
}

function renderMessages() {
  const messageList = document.getElementById("messagesList");
  messageList.innerHTML = "";
  currentMessages.forEach((message) => {
    const messageElement = createMessageElement(message);
    messageList.appendChild(messageElement);
  });
  scrollToBottom();
  window.TravelwAITranslation?.refreshConversationControl?.(
    document.getElementById("translateConversationBtn"),
    messageList
  );
}

function appendMessage(message) {
  if (document.getElementById(`msg-${message.id}`)) {
    return;
  }
  const messageList = document.getElementById("messagesList");
  const messageElement = createMessageElement(message);
  messageList.appendChild(messageElement);
  scrollToBottom();
  window.TravelwAITranslation?.refreshConversationControl?.(
    document.getElementById("translateConversationBtn"),
    messageElement
  );
}

function appendStatusMessage(statusText) {
  const messageList = document.getElementById("messagesList");
  if (!messageList || !statusText) return;
  const statusElement = document.createElement("div");
  statusElement.className = "status-message";
  statusElement.textContent = statusText;
  messageList.appendChild(statusElement);
  scrollToBottom();
}

function handlePresenceStatus(message) {
  const statusText = message?.message || "";
  const status = message?.status === "online" ? "online" : message?.status === "offline" ? "offline" : "";
  const otherParticipant = currentConversation && !isGroupConversation(currentConversation) ? getOtherParticipant(currentConversation) : null;
  const currentUserId = getCurrentUserId();
  const statusUserId = message?.user_id || message?.userId || "";

  if (otherParticipant && statusUserId && statusUserId === otherParticipant.id) {
    updateConversationUserStatus(status, getConversationDisplayName(currentConversation));
  }

  if (!statusUserId || statusUserId !== currentUserId) {
    appendStatusMessage(statusText);
  }
}

function updateConversationUserStatus(status, displayName) {
  const statusElement = document.getElementById("conversationUserStatus");
  if (!statusElement) return;

  const cleanStatus = status === "online" ? "online" : "offline";
  statusElement.textContent = cleanStatus === "online" ? "Đang online" : "Đang offline";
  statusElement.setAttribute("aria-label", `${displayName || getConversationDisplayName(currentConversation) || "Người dùng"}: ${statusElement.textContent}`);
  statusElement.classList.toggle("online", cleanStatus === "online");
  statusElement.classList.toggle("offline", cleanStatus !== "online");
}

function updateConversationPreview(message) {
  if (!message?.conversation_id) return;
  const index = conversations.findIndex((conversation) => conversation.id === message.conversation_id);
  if (index === -1) return;

  conversations[index].last_message = message.content || "";
  conversations[index].last_message_time = message.timestamp || message.time_sent || new Date().toISOString();

  const updated = conversations.splice(index, 1)[0];
  conversations.unshift(updated);
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
}

function cleanLegacyAiImageText(value) {
  const text = decodeUnicodeEscapes(value || "").trim();
  return /^đã gửi.*ảnh.*ai.*xem[.!]?$/i.test(text) ? "" : text;
}

function normalizeMessageAttachment(item) {
  if (!item) return null;
  const url = String(item.url || item.src || "").trim();
  if (!url) return null;
  const contentType = String(item.contentType || item.content_type || item.mimeType || "application/octet-stream");
  return {
    url,
    name: String(item.name || item.fileName || item.filename || "Tệp đính kèm"),
    contentType,
    size: Number(item.size || 0),
    type: contentType.startsWith("video/") ? "video" : (contentType.startsWith("image/") ? "image" : "file")
  };
}

function parseMessageContent(content) {
  if (!content || typeof content !== "string") return { text: "", attachments: [], attachment: null };
  try {
    const payload = JSON.parse(content);
    if (payload?.type === CHAT_MESSAGE_PAYLOAD_TYPE) {
      const attachments = (Array.isArray(payload.attachments) ? payload.attachments : (payload.attachment ? [payload.attachment] : []))
        .map(normalizeMessageAttachment).filter(Boolean);
      return { text: cleanLegacyAiImageText(payload.text), attachments, attachment: attachments[0] || null };
    }
  } catch (_) {}
  return { text: cleanLegacyAiImageText(content), attachments: [], attachment: null };
}

function buildMessageContent(text, attachments) {
  const list = (Array.isArray(attachments) ? attachments : (attachments ? [attachments] : [])).map(normalizeMessageAttachment).filter(Boolean);
  if (!list.length) return text;
  return JSON.stringify({ type: CHAT_MESSAGE_PAYLOAD_TYPE, version: 2, text, attachments: list, attachment: list[0] || null });
}

function readFileAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result || ""));
    reader.onerror = () => reject(new Error("Không đọc được ảnh."));
    reader.readAsDataURL(file);
  });
}



function getMessagePreview(content) {
  const parsed = parseMessageContent(content);
  if (parsed.text) return parsed.text;
  if (parsed.attachments?.length) return parsed.attachments.length === 1 ? `Tệp: ${parsed.attachments[0].name}` : `${parsed.attachments.length} tệp đính kèm`;
  return "";
}

function getInlineIcon(name) {
  const icons = {
    check: `
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M20 6 9 17l-5-5" />
      </svg>
    `,
    x: `
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M18 6 6 18" />
        <path d="m6 6 12 12" />
      </svg>
    `,
    download: `
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
        <path d="M7 10l5 5 5-5" />
        <path d="M12 15V3" />
      </svg>
    `,
  };

  return icons[name] || "";
}

function createAttachmentElement(attachment) {
  const wrapper = document.createElement("div");
  wrapper.className = "message-attachment-card";

  const contentType = attachment.contentType || "";
  const fileUrl = attachment.url || "#";
  const fileName = attachment.name || "Tệp đính kèm";

  if (contentType.startsWith("image/")) {
    const image = document.createElement("img");
    image.className = "message-attachment-preview image-preview";
    image.src = fileUrl;
    image.alt = fileName;
    wrapper.appendChild(image);
  } else if (contentType.startsWith("video/")) {
    const video = document.createElement("video");
    video.className = "message-attachment-preview video-preview";
    video.src = fileUrl;
    video.controls = true;
    wrapper.appendChild(video);
  } else if (contentType.startsWith("audio/")) {
    const audio = document.createElement("audio");
    audio.className = "message-attachment-preview audio-preview";
    audio.src = fileUrl;
    audio.controls = true;
    wrapper.appendChild(audio);
  }

  const details = document.createElement("div");
  details.className = "message-attachment-details";

  const name = document.createElement("span");
  name.className = "message-attachment-name";
  name.textContent = fileName;
  details.appendChild(name);

  if (attachment.size) {
    const size = document.createElement("span");
    size.className = "message-attachment-size";
    size.textContent = formatFileSize(attachment.size);
    details.appendChild(size);
  }

  const download = document.createElement("a");
  download.className = "message-attachment-download";
  download.href = fileUrl;
  download.download = fileName;
  download.target = "_blank";
  download.rel = "noopener";
  download.title = "Tải về";
  download.setAttribute("aria-label", "Tải về");
  download.innerHTML = `${getInlineIcon("download")}<span class="sr-only">Tải về</span>`;
  details.appendChild(download);

  wrapper.appendChild(details);
  return wrapper;
}

function createMessageElement(message) {
  const isCurrentUser = isMessageFromCurrentUser(message);
  const messageWrapper = document.createElement("div");
  messageWrapper.className = `message-wrapper ${isCurrentUser ? "sent" : "received"}`;
  messageWrapper.id = `msg-${message.id || `temp-${Date.now()}-${Math.random().toString(16).slice(2)}`}`;

  const messageBubble = document.createElement("div");
  messageBubble.className = "message-bubble";

  const otherParticipant = currentConversation && !isGroupConversation(currentConversation) ? getOtherParticipant(currentConversation) : null;
  const senderInfo = message.sender_info || (isCurrentUser ? currentUser : otherParticipant) || {};
  const senderProfilePic = getUserAvatarUrl(senderInfo) || getUserAvatarUrl(isCurrentUser ? currentUser : otherParticipant);
  const senderName = isCurrentUser
    ? getUserDisplayName(currentUser) || "Bạn"
    : isAiMessageSender(message)
      ? AI_DISPLAY_NAME
      : getUserDisplayName(senderInfo) || message.sender_name || getUserDisplayName(otherParticipant) || "Bạn bè";

  const avatarContent = createAvatarContent(
    senderProfilePic,
    senderName?.charAt(0) || "U",
    isCurrentUser,
    !isCurrentUser && isAiConversation(currentConversation)
  );

  const messageTime =
    message.timestamp || message.time_sent || new Date().toISOString();
  const parsedContent = parseMessageContent(message.content || "");
  if (!parsedContent.attachments.length && message.imageDataUrl) {
    parsedContent.attachments = [{ name: message.imageName || "Ảnh đính kèm", contentType: "image/jpeg", url: message.imageDataUrl }];
    parsedContent.attachment = parsedContent.attachments[0];
  }

  messageBubble.innerHTML = `
        <div class="message-sender">${escapeHtml(senderName)}</div>
        <div class="message-content">
        </div>
        <div class="message-meta">
            <span class="timestamp">${formatTime(messageTime)}</span>
        </div>
    `;

  const contentElement = messageBubble.querySelector(".message-content");
  if (parsedContent.attachments.length) {
    messageBubble.classList.add("has-attachment");
    contentElement.classList.add("has-attachment");
  }

  if (parsedContent.text) {
    const text = document.createElement("p");
    const conversationIdentity = String(currentConversation?.id || message.conversation_id || message.conversationId || "conversation");
    const translationIdentity = String(message.id || messageTime);
    text.className = "message-text";
    text.setAttribute("data-chat-message-text", "");
    text.setAttribute("data-no-translate", "");
    text.dataset.aiTranslationTarget = "interface";
    text.dataset.aiTranslationKey = `message:${conversationIdentity}:${translationIdentity}`;
    text.textContent = parsedContent.text;
    contentElement.appendChild(text);
  }

  if (parsedContent.attachments.length) {
    const attachmentList = document.createElement("div");
    attachmentList.className = "message-attachment-list";
    parsedContent.attachments.forEach(attachment => attachmentList.appendChild(createAttachmentElement(attachment)));
    contentElement.appendChild(attachmentList);
  }

  if (isCurrentUser) {
    messageWrapper.appendChild(messageBubble);
    messageWrapper.appendChild(avatarContent);
  } else {
    messageWrapper.appendChild(avatarContent);
    messageWrapper.appendChild(messageBubble);
  }

  return messageWrapper;
}

const createAvatarContent = (profilePic, initial, isCurrentUser = false, isWaigo = false) => {
  const avatar = document.createElement("div");
  avatar.className = `user-avatar message-avatar ${
    isCurrentUser ? "avatar-sent" : "avatar-received"
  }${isWaigo ? " waigo-avatar-shell" : ""}`;

  if (isWaigo) {
    avatar.classList.add("waigo-logo-background");
    avatar.setAttribute("role", "img");
    avatar.setAttribute("aria-label", AI_DISPLAY_NAME);
  } else if (profilePic) {
    const img = document.createElement("img");
    img.src = resolveAvatarUrl(profilePic);
    img.alt = initial;
    img.onerror = () => {
      const initialSpan = document.createElement("span");
      initialSpan.textContent = initial;
      avatar.innerHTML = "";
      avatar.appendChild(initialSpan);
    };
    avatar.appendChild(img);
  } else {
    const initialSpan = document.createElement("span");
    initialSpan.textContent = initial;
    avatar.appendChild(initialSpan);
  }

  return avatar;
};

async function sendMessage() {
  if (isAiConversation() && isAiSending) {
    await cancelActiveAiJob();
    return;
  }

  const messageInput = document.getElementById("messageInput");
  const typedContent = messageInput.value.trim();
  if ((!typedContent && !selectedChatAttachments.length) || !currentConversation) return;

  if (isAiConversation()) {
    await sendAiMessage(typedContent);
    return;
  }
  if (typedContent.length > MAX_CHAT_MESSAGE_LENGTH) {
    showError(`Tin nhắn tối đa ${MAX_CHAT_MESSAGE_LENGTH} ký tự.`);
    return;
  }
  if (!websocket || websocket.readyState !== WebSocket.OPEN) {
    showError("Không có kết nối tin nhắn. Vui lòng thử lại.");
    return;
  }

  try {
    setMessageSendButtonLoading(true);
    showLoading(true);
    const attachments = [];
    for (const file of selectedChatAttachments) attachments.push(await uploadChatAttachment(file));
    websocket.send(buildMessageContent(typedContent, attachments));
    messageInput.value = "";
    clearChatAttachment();
  } catch (error) {
    console.error("Lỗi gửi tin nhắn:", error);
    showError(error.message || "Không thể gửi tin nhắn. Vui lòng thử lại.");
  } finally {
    showLoading(false);
    setMessageSendButtonLoading(false);
  }
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

async function uploadAiAttachments(files) {
  const list = Array.from(files || []).slice(0, MAX_AI_ATTACHMENT_COUNT);
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
    headers: { Authorization: `Bearer ${getAuthToken()}` },
    body: formData
  });
  const result = await response.json().catch(() => ({}));
  if (!response.ok || result.success === false) {
    throw new Error(result.message || result.detail || "Không thể lưu ảnh đính kèm.");
  }
  return (Array.isArray(result.media) ? result.media : []).map(normalizeMessageAttachment).filter(Boolean);
}

function getAiAttachmentPlaceholder(count) {
  return count > 1 ? `Đã gửi ${count} ảnh cho AI.` : "Đã gửi một ảnh cho AI.";
}

function replaceStoredAiMessageAttachments(messageId, text, attachments) {
  const current = loadAiMessages();
  const updated = current.map((message) => String(message?.id || "") === String(messageId)
    ? { ...message, content: buildMessageContent(text, attachments) }
    : message);
  saveAiMessages(updated, true, "updated");
  currentMessages = updated;
}

async function buildDirectAiAttachments(files) {
  const list = Array.from(files || []);
  const results = [];
  for (const file of list) {
    const isVideo = String(file?.type || "").startsWith("video/");
    let preparedFile = file;
    let width = 0;
    let height = 0;
    let originalSize = Number(file?.size || 0);
    let optimizedSize = originalSize;
    let dataUrl = "";

    if (isVideo) {
      dataUrl = await extractVideoFrameAsDataUrl(file);
      const frameBlob = await (await fetch(dataUrl)).blob();
      preparedFile = new File([frameBlob], `${String(file?.name || "khung-hinh-video").replace(/\.[^/.]+$/, "")}.jpg`, {
        type: "image/jpeg",
        lastModified: Date.now()
      });
      optimizedSize = frameBlob.size;
    } else {
      const optimized = window.TravelwAIImageOptimizer?.optimizeImageFileForAi
        ? await window.TravelwAIImageOptimizer.optimizeImageFileForAi(file)
        : { file, width: 0, height: 0, originalSize, optimizedSize };
      preparedFile = optimized.file || file;
      width = Number(optimized.width || 0);
      height = Number(optimized.height || 0);
      originalSize = Number(optimized.originalSize || originalSize);
      optimizedSize = Number(optimized.optimizedSize || preparedFile.size || 0);
      dataUrl = await readFileAsDataUrl(preparedFile);
    }

    const imageData = String(dataUrl || "").replace(/^data:image\/[^;]+;base64,/, "");
    if (!imageData) continue;
    const dimensionText = width > 0 && height > 0 ? `${width}x${height}px` : "khung hình đại diện";
    results.push({
      imageData,
      uploadFile: isVideo ? file : preparedFile,
      attachment: {
        url: dataUrl,
        name: String(preparedFile?.name || file?.name || (isVideo ? "Khung hình video" : "Ảnh đính kèm")),
        contentType: String(preparedFile?.type || "image/jpeg"),
        size: optimizedSize,
        type: "image"
      },
      contextLabel: `${String(file?.name || "Tệp")} (${isVideo ? "video lấy khung hình" : "ảnh"}; ${dimensionText}; ` +
        `${formatFileSize(originalSize)} → ${formatFileSize(optimizedSize)} trước khi gửi AI)`
    });
  }
  return results;
}

async function sendAiMessage(text) {
  const cleanText = String(text || "").trim();
  const files = selectedChatAttachments.slice(0, MAX_AI_ATTACHMENT_COUNT);
  if ((!cleanText && !files.length) || isAiSending) return;
  for (const file of files) {
    const type = String(file.type || "");
    if (!type.startsWith("image/") && !type.startsWith("video/")) {
      showError("AI chỉ hỗ trợ ảnh hoặc video.");
      return;
    }
    if (file.size > MAX_CHAT_ATTACHMENT_SIZE) {
      showError("Mỗi tệp không được vượt quá 10MB.");
      return;
    }
  }

  const input = document.getElementById("messageInput");
  const stored = loadAiMessages();
  aiCancelRequested = false;
  aiStartRequestController?.abort();
  const startController = new AbortController();
  aiStartRequestController = startController;
  setAiJobRunning(true);

  try {
    const mediaPayload = await buildDirectAiAttachments(files);
    if (aiCancelRequested) throw new DOMException("Đã dừng AI", "AbortError");

    // Chỉ upload bản đã tối ưu để không tranh băng thông với request phân tích ảnh.
    const optimizedUploadFiles = mediaPayload.map(item => item.uploadFile).filter(Boolean);
    const uploadPromise = optimizedUploadFiles.length
      ? uploadAiAttachments(optimizedUploadFiles).catch((error) => {
          console.warn("Không thể lưu ảnh AI lên storage:", error);
          return [];
        })
      : Promise.resolve([]);

    const visionImages = mediaPayload.map(item => item.imageData).filter(Boolean);
    const localAttachments = mediaPayload.map(item => item.attachment).filter(Boolean);
    const historyLimit = mediaPayload.length ? 6 : 12;
    const history = stored.filter((item) => !item.is_system_welcome).slice(-historyLimit).map((item) => {
      const parsed = parseMessageContent(item.content || "");
      return { role: isAiMessageSender(item) ? "assistant" : "user", content: parsed.text || "" };
    });
    const referenceContext = mediaPayload.length
      ? `Tệp người dùng đính kèm: ${mediaPayload.map(item => item.contextLabel).join(", ")}.`
      : "";

    const response = await fetch(`${API_BASE_URL}/ai/chat/jobs`, {
      method: "POST",
      headers: { Authorization: `Bearer ${getAuthToken()}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        Message: cleanText || "Hãy phân tích các ảnh hoặc khung hình video đã đính kèm.",
        History: history,
        ReferenceContext: referenceContext,
        Images: visionImages,
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
          headers: { Authorization: `Bearer ${getAuthToken()}`, "Content-Type": "application/json" },
          cache: "no-store"
        }).catch(() => {});
      }
      throw new DOMException("Đã dừng AI", "AbortError");
    }

    if (response.status === 409 && result.jobId) {
      writeActiveAiJobId(result.jobId);
      streamAiJob(result.jobId);
      showError("AI đang trả lời câu trước.");
      return;
    }
    if (!response.ok || !result.jobId) throw new Error(result.message || result.detail || "AI chưa thể trả lời lúc này.");

    const jobId = String(result.jobId);
    const messageId = `ai-user-${jobId}`;
    const persistedText = cleanText || (localAttachments.length ? getAiAttachmentPlaceholder(localAttachments.length) : "");
    const storedUserMessage = {
      id: messageId,
      sender_id: getCurrentUserId() || "current-user",
      sender_info: currentUser || {},
      content: persistedText,
      timestamp: new Date().toISOString()
    };
    const visibleUserMessage = {
      ...storedUserMessage,
      content: buildMessageContent(cleanText, localAttachments)
    };
    const next = [...stored, storedUserMessage];
    saveAiMessages(next, true, "sent");
    currentMessages = next;
    appendMessage(visibleUserMessage);
    input.value = "";
    clearChatAttachment();
    writeActiveAiJobId(jobId);
    streamAiJob(jobId);

    // Cập nhật lịch sử bằng URL storage sau khi upload hoàn tất, không chặn job AI.
    void uploadPromise.then((storedAttachments) => {
      if (storedAttachments.length) replaceStoredAiMessageAttachments(messageId, cleanText, storedAttachments);
    });
  } catch (error) {
    if (error?.name !== "AbortError") {
      setAiJobRunning(false);
      showError(error.message || "Không kết nối được AI.");
      input?.focus();
    }
  } finally {
    if (aiStartRequestController === startController) aiStartRequestController = null;
  }
}

async function uploadChatAttachment(file) {
  if (file.size > MAX_CHAT_ATTACHMENT_SIZE) {
    throw new Error("Tệp đính kèm không được vượt quá 10MB.");
  }

  const token =
    getAuthToken();
  if (!token) {
    throw new Error("Không tìm thấy token đăng nhập.");
  }

  const uploadFile = file.type && file.type.startsWith("image/") && window.TravelwAIImageOptimizer
    ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
    : file;
  const formData = new FormData();
  formData.append("file", uploadFile, uploadFile.name || file.name);

  const response = await fetch(
    `${API_BASE_URL}/conversations/${currentConversation.id}/attachments`,
    {
      method: "POST",
      headers: { Authorization: `Bearer ${token}` },
      body: formData,
    }
  );

  const result = await response.json().catch(() => ({}));
  if (!response.ok || result.success === false) {
    throw new Error(result.detail || result.message || "Không thể tải tệp đính kèm.");
  }

  return result.data;
}

function handleMessageKeyPress(event) {
  if (event.key === "Enter") {
    sendMessage();
  }
}

function openNewChatModal() {
  openChatUserPickerModal("chat");
}

function openGroupChatModal() {
  openChatUserPickerModal("group");
}

function openChatUserPickerModal(mode = "chat") {
  currentChatModalMode = mode === "group" ? "group" : "chat";
  const modal = document.getElementById("newChatModal");
  const modalTitle = modal?.querySelector(".modal-header h3");
  const searchInput = document.getElementById("searchUsers");
  const usersList = document.getElementById("usersList");

  resetGroupSelection();

  if (modalTitle) {
    modalTitle.textContent = currentChatModalMode === "group" ? "Tạo nhóm trò chuyện" : "Tạo cuộc trò chuyện mới";
  }

  if (searchInput) {
    searchInput.value = "";
    searchInput.placeholder = currentChatModalMode === "group"
      ? "Nhập email hoặc tên người muốn thêm vào nhóm..."
      : "Nhập email hoặc tên người dùng...";
  }

  if (usersList) {
    usersList.innerHTML = currentChatModalMode === "group"
      ? '<div class="loading-message">Tìm và chọn ít nhất 2 người để tạo nhóm...</div>'
      : '<div class="loading-message">Nhập email hoặc tên để tìm kiếm người dùng...</div>';
  }

  if (modal) modal.style.display = "block";
  updateGroupSelectionPanel();
  searchInput?.focus();
}

function closeNewChatModal() {
  const modal = document.getElementById("newChatModal");
  const modalTitle = modal?.querySelector(".modal-header h3");
  const searchInput = document.getElementById("searchUsers");
  const usersList = document.getElementById("usersList");

  if (modal) modal.style.display = "none";
  if (modalTitle) modalTitle.textContent = "Tạo cuộc trò chuyện mới";
  if (searchInput) {
    searchInput.value = "";
    searchInput.placeholder = "Nhập email hoặc tên người dùng...";
  }
  if (usersList) {
    usersList.innerHTML = '<div class="loading-message">Nhập email hoặc tên để tìm kiếm người dùng...</div>';
  }
  currentChatModalMode = "chat";
  resetGroupSelection();
}

let selectedUserForSharing = null;

function openShareMemoryModal() {
  document.getElementById("shareMemoryModal").style.display = "block";
  document.getElementById("shareWithUserEmail").focus();
  selectedUserForSharing = null;
  document.getElementById("memoryFile").value = null;
  resetMemoryFileSelection();
}

function closeShareMemoryModal() {
  document.getElementById("shareMemoryModal").style.display = "none";
  document.getElementById("shareWithUserEmail").value = "";
  document.getElementById("shareUserSuggestionList").innerHTML =
    '<div class="loading-message">Nhập email để tìm người dùng.</div>';
  document.getElementById("memoryFile").value = null;
  resetMemoryFileSelection();
  selectedUserForSharing = null;
}

function formatFileSize(bytes) {
  if (!bytes && bytes !== 0) return "";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let size = bytes / 1024;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex += 1;
  }

  return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[unitIndex]}`;
}

function revokePreviewUrls(list) {
  list.splice(0).forEach(url => URL.revokeObjectURL(url));
}

function attachmentPreviewCard(file, index, removeHandlerName, objectUrlList) {
  const objectUrl = URL.createObjectURL(file);
  objectUrlList.push(objectUrl);
  let preview = `<span class="attachment-file-icon" data-interface-icon="paperclip" aria-hidden="true"></span>`;
  if (String(file.type || "").startsWith("image/")) preview = `<img src="${objectUrl}" alt="${escapeHtml(file.name || "Ảnh đính kèm")}" />`;
  else if (String(file.type || "").startsWith("video/")) preview = `<video src="${objectUrl}" preload="metadata" muted playsinline></video>`;
  return `<div class="attachment-preview-item">${preview}<span class="attachment-preview-name">${escapeHtml(file.name || "Tệp")} (${formatFileSize(file.size)})</span><button type="button" class="attachment-preview-remove" onclick="${removeHandlerName}(${index})" title="Xóa tệp" aria-label="Xóa tệp"><span data-interface-icon="trash-2"></span></button></div>`;
}

function renderSelectedMemoryFiles() {
  const box = document.getElementById("selectedMemoryFile");
  if (!box) return;
  revokePreviewUrls(memorySharePreviewObjectUrls);
  box.innerHTML = selectedMemoryShareFiles.map((file, index) => attachmentPreviewCard(file, index, "removeSelectedMemoryFile", memorySharePreviewObjectUrls)).join("");
  box.hidden = selectedMemoryShareFiles.length === 0;
}

function updateSelectedMemoryFile(event) {
  const files = Array.from(event.target.files || []);
  files.forEach(file => {
    if (file.size <= MAX_CHAT_ATTACHMENT_SIZE) selectedMemoryShareFiles.push(file);
    else showError(`${file.name} vượt quá 10MB.`);
  });
  selectedMemoryShareFiles = selectedMemoryShareFiles.slice(0, 12);
  event.target.value = "";
  renderSelectedMemoryFiles();
}

function removeSelectedMemoryFile(index) {
  selectedMemoryShareFiles.splice(index, 1);
  renderSelectedMemoryFiles();
}

function resetMemoryFileSelection() {
  const fileInput = document.getElementById("memoryFile");
  if (fileInput) fileInput.value = "";
  selectedMemoryShareFiles = [];
  renderSelectedMemoryFiles();
}

function searchUsersForSharing() {
  const query = document.getElementById("shareWithUserEmail").value.trim().toLowerCase();
  const usersList = document.getElementById("shareUserSuggestionList");

  selectedUserForSharing = null;
  if (!query || query.length < 2) {
    usersList.innerHTML = '<div class="loading-message">Nhập ít nhất 2 ký tự để tìm người dùng.</div>';
    return;
  }

  usersList.innerHTML = '<div class="loading-message">Đang tìm người dùng...</div>';
  const matchedUsers = (all_users || []).filter((user) => {
    const email = (user.email || "").toLowerCase();
    const username = (user.username || user.name || "").toLowerCase();
    return email.includes(query) || username.includes(query);
  });
  renderShareUserResults(matchedUsers);
}

function renderShareUserResults(users) {
  const usersList = document.getElementById("shareUserSuggestionList");
  usersList.innerHTML = "";

  if (users.length === 0) {
    usersList.innerHTML = '<div class="loading-message">Không tìm thấy người dùng.</div>';
    return;
  }

  users.forEach((user) => {
    const userItem = createShareUserElement(user);
    usersList.appendChild(userItem);
  });
}

function createShareUserElement(user) {
  const div = document.createElement("div");
  div.className = "user-item";
  div.onclick = () => selectUserForSharing(user, div);

  const avatarContent = user.profilePic
    ? `<img loading="lazy" decoding="async" src="${API_BASE_URL.replace("/api", "")}${user.profilePic}" alt="${
        user.name
      }" style="width: 100%; height: 100%; object-fit: cover;" onerror="this.innerHTML='${
        user.name?.charAt(0).toUpperCase() || "U"
      }';" />`
    : user.name?.charAt(0).toUpperCase() || "U";

  div.innerHTML = `
        <div class="user-avatar">${avatarContent}</div>
        <div class="user-info">
            <div class="user-name">${escapeHtml(
              user.username || "Người dùng"
            )}</div>
            <div class="user-email">${escapeHtml(user.email || "")}</div>
        </div>
    `;
  return div;
}

function selectUserForSharing(user, element) {
  selectedUserForSharing = user;
  const allUserItems = document.querySelectorAll(
    "#shareUserSuggestionList .user-item"
  );
  allUserItems.forEach((item) => item.classList.remove("selected"));
  element.classList.add("selected");
  document.getElementById("shareWithUserEmail").value = user.email;
}

async function handleShareMemory() {
  if (!selectedMemoryShareFiles.length) {
    window.TravelwAIToast("Vui lòng chọn tệp kỷ niệm để chia sẻ.");
    return;
  }
  if (!selectedUserForSharing) {
    window.TravelwAIToast("Vui lòng chọn người nhận bằng cách nhập email và bấm vào gợi ý.");
    return;
  }

  try {
    showLoading(true);
    const conversation = await ensureConversationWithUser(selectedUserForSharing);
    await selectConversation(conversation);
    await waitForWebSocketOpen();
    const attachments = [];
    for (const file of selectedMemoryShareFiles) attachments.push(await uploadChatAttachment(file));
    websocket.send(buildMessageContent("Chia sẻ kỷ niệm", attachments));
    closeShareMemoryModal();
  } catch (error) {
    console.error("Lỗi chia sẻ kỷ niệm:", error);
    showError(error.message || "Không thể chia sẻ kỷ niệm. Vui lòng thử lại.");
  } finally {
    showLoading(false);
  }
}

async function searchUsers() {
  const friendInput = document.getElementById("searchFriendInput");
  const modalInput = document.getElementById("searchUsers");
  const newChatModal = document.getElementById("newChatModal");
  const isModalSearch =
    modalInput &&
    (document.activeElement === modalInput || newChatModal?.style.display === "block");

  const query = (isModalSearch ? modalInput?.value : friendInput?.value || "").trim();
  const targetId = isModalSearch ? "usersList" : "friendSearchResultsContainer";
  const target = document.getElementById(targetId);
  if (!target) return;

  if (!query || query.length < 1) {
    if (!isModalSearch) {
      target.innerHTML = "";
      target.classList.remove("is-open");
      return;
    }

    target.innerHTML = currentChatModalMode === "group"
      ? '<div class="loading-message">Tìm và chọn ít nhất 2 người để tạo nhóm...</div>'
      : '<div class="loading-message">Nhập email hoặc tên để tìm kiếm người dùng...</div>';
    return;
  }

  if (!isModalSearch) {
    target.classList.add("is-open");
  }

  target.innerHTML = '<div class="loading-message">Đang tìm kiếm...</div>';
  renderSearchResults(all_users, query, targetId);
}

function renderSearchResults(users, query, targetId = "friendSearchResultsContainer") {
  const usersList = document.getElementById(targetId);
  const normalizedQuery = query.toLowerCase();
  const isModalSearch = targetId === "usersList";
  const matchedUsers = (users || []).filter((user) => {
    const email = (user.email || "").toLowerCase();
    const username = (user.username || user.name || "").toLowerCase();
    return email.includes(normalizedQuery) || username.includes(normalizedQuery);
  });

  if (matchedUsers.length === 0) {
    usersList.innerHTML = '<div class="loading-message">Không tìm thấy người dùng</div>';
    return;
  }

  usersList.innerHTML = "";
  matchedUsers.forEach((user) => {
    const userItem = createUserProfileElement(user, query, isModalSearch ? "new-chat-modal" : "sidebar");
    usersList.appendChild(userItem);
  });
}

function isUserSelectedForGroup(user) {
  const userId = getUserId(user);
  return selectedGroupUsers.some((selected) => getUserId(selected) === userId);
}

function toggleGroupUserSelection(user, element = null) {
  const userId = getUserId(user);
  if (!userId) {
    showError("Không xác định được người dùng này.");
    return;
  }

  const selectedIndex = selectedGroupUsers.findIndex((selected) => getUserId(selected) === userId);
  if (selectedIndex >= 0) {
    selectedGroupUsers.splice(selectedIndex, 1);
    if (element) element.classList.remove("selected");
  } else {
    selectedGroupUsers.push(user);
    if (element) element.classList.add("selected");
  }

  updateGroupSelectionPanel();
}

function resetGroupSelection() {
  selectedGroupUsers = [];
  document.querySelectorAll("#usersList .user-item.selected").forEach((item) => item.classList.remove("selected"));
  updateGroupSelectionPanel();
}

function updateGroupSelectionPanel() {
  const panel = document.getElementById("groupSelectionPanel");
  const summary = document.getElementById("groupSelectionSummary");
  const button = document.getElementById("groupCreateBtn");
  if (!panel || !summary || !button) return;

  if (selectedGroupUsers.length === 0) {
    panel.hidden = true;
    summary.textContent = currentChatModalMode === "group" ? "Chưa chọn thành viên nhóm" : "Chưa chọn người nhận";
    button.textContent = currentChatModalMode === "group" ? "Tạo nhóm" : "Nhắn tin";
    button.disabled = false;
    return;
  }

  const names = selectedGroupUsers.map((user) => getUserDisplayName(user)).join(", ");
  panel.hidden = false;

  if (currentChatModalMode === "group") {
    summary.textContent = selectedGroupUsers.length === 1
      ? `Đã chọn: ${names}. Chọn thêm 1 người để tạo nhóm.`
      : `Đã chọn ${selectedGroupUsers.length} người: ${names}`;
    button.textContent = "Tạo nhóm";
    button.disabled = selectedGroupUsers.length < 2;
    return;
  }

  summary.textContent = selectedGroupUsers.length === 1
    ? `Đã chọn: ${names}`
    : `Đã chọn ${selectedGroupUsers.length} người: ${names}`;
  button.textContent = selectedGroupUsers.length >= 2 ? "Tạo nhóm" : "Nhắn tin";
  button.disabled = false;
}

async function handleSelectedChatUsers() {
  if (selectedGroupUsers.length === 0) {
    showError(currentChatModalMode === "group" ? "Vui lòng chọn thành viên nhóm." : "Vui lòng chọn người muốn nhắn tin.");
    return;
  }

  if (currentChatModalMode === "group") {
    if (selectedGroupUsers.length < 2) {
      showError("Chọn ít nhất 2 người để tạo nhóm.");
      return;
    }
    await createGroupConversation(selectedGroupUsers);
    return;
  }

  if (selectedGroupUsers.length === 1) {
    await startChatWithUser(selectedGroupUsers[0]);
    return;
  }

  await createGroupConversation(selectedGroupUsers);
}

async function createGroupConversation(users) {
  const token = getAuthToken();
  if (!token) {
    showError("Không tìm thấy token đăng nhập.");
    return;
  }

  const participantIds = users
    .map((user) => getUserId(user))
    .filter(Boolean)
    .filter((id, index, arr) => arr.indexOf(id) === index);

  if (participantIds.length < 2) {
    showError("Chọn ít nhất 2 người để tạo nhóm.");
    return;
  }

  const groupName = `Nhóm ${users.map((user) => getUserDisplayName(user)).slice(0, 3).join(", ")}`;

  try {
    showLoading(true);
    const response = await fetch(`${API_BASE_URL}/conversations`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        participant_ids: participantIds,
        group_name: groupName,
      }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể tạo nhóm trò chuyện.");
    }

    closeNewChatModal();
    await loadConversations(true);
    const newConversation = conversations.find((conversation) => conversation.id === result.conversation_id);
    if (newConversation) {
      await selectConversation(newConversation);
    }
  } catch (error) {
    console.error("Lỗi tạo nhóm trò chuyện:", error);
    showError(error.message || "Không thể tạo nhóm trò chuyện. Vui lòng thử lại.");
  } finally {
    showLoading(false);
  }
}

function createUserProfileElement(user, searchQuery = "", context = "sidebar") {
  const div = document.createElement("div");
  const isModalGroupPicker = context === "new-chat-modal";
  const isSelected = isUserSelectedForGroup(user);
  div.className = `friend-search-result-item user-item${isModalGroupPicker ? " group-selectable-user" : ""}${isSelected ? " selected" : ""}`;
  div.title = isModalGroupPicker ? "Bấm để chọn người nhận, chọn nhiều người để tạo nhóm" : "Bấm để xem thông tin người dùng";
  div.onclick = () => {
    if (isModalGroupPicker) {
      toggleGroupUserSelection(user, div);
      return;
    }
    openFriendDetailModal(user);
  };

  const defaultAvatar = "logo/profile-icon-white.webp";
  const avatarUrl = user.profilePic
    ? user.profilePic.startsWith("http")
      ? user.profilePic
      : `${API_BASE_URL.replace("/api", "")}${user.profilePic}`
    : defaultAvatar;

  let avatarHTML;
  if (user.profilePic) {
    avatarHTML = `<img loading="lazy" decoding="async" src="${escapeHtml(avatarUrl)}" alt="${escapeHtml(
      user.name || user.email
    )}" class="user-avatar-img" onerror="this.onerror=null; this.style.display='none'; const initial = (this.alt.charAt(0) || '?').toUpperCase(); const parent = this.parentElement; parent.innerHTML = \`<div class='user-avatar-initial'>\${initial}</div>\`;">`;
  } else {
    const initial = (user.name || user.email)?.charAt(0).toUpperCase() || "?";
    avatarHTML = `<div class="user-avatar-initial">${escapeHtml(
      initial
    )}</div>`;
  }

  div.innerHTML = `
    <div class="user-avatar-container">
      ${avatarHTML}
    </div>
    <div class="user-info">
      <div class="user-name">${highlightSearchTerm(getUserDisplayName(user), searchQuery)}</div>
      <div class="user-presence ${getUserPresenceClass(user)}"><span class="presence-dot"></span>${getUserPresenceLabel(user)}</div>
      <div class="user-email">${highlightSearchTerm(user.email || "N/A", searchQuery)}</div>
    </div>
  `;
  return div;
}

function openFriendDetailModal(user) {
  if (!user) return;

  const modal = document.getElementById("friendDetailModal");
  const avatarImg = document.getElementById("friendDetailAvatar");
  const avatarInitialDiv = document.getElementById("friendDetailAvatarInitial");
  const usernameEl = document.getElementById("friendDetailUsername");
  const emailEl = document.getElementById("friendDetailEmail");
  const addBtn = document.getElementById("friendDetailAddBtn");
  const statusMsg = document.getElementById("friendDetailStatusMsg");

  const defaultAvatarPath = "logo/profile-icon-white.webp";
  avatarInitialDiv.innerHTML = "";
  avatarInitialDiv.style.display = "none";
  avatarImg.style.display = "none";

  if (user.profilePic) {
    const picSrc = user.profilePic.startsWith("http")
      ? user.profilePic
      : `${API_BASE_URL.replace("/api", "")}${user.profilePic}`;
    avatarImg.src = picSrc;
    avatarImg.alt = user.name || user.email;
    avatarImg.style.display = "block";
    avatarImg.onerror = () => {
      avatarImg.style.display = "none";
      const initial = (user.name || user.email)?.charAt(0).toUpperCase() || "?";
      avatarInitialDiv.textContent = initial;
      avatarInitialDiv.style.display = "flex";
    };
  } else {
    const initial = (user.name || user.email)?.charAt(0).toUpperCase() || "?";
    avatarInitialDiv.textContent = initial;
    avatarInitialDiv.style.display = "flex";
  }

  usernameEl.textContent = getUserDisplayName(user);
  emailEl.textContent = user.email || "Chưa có email";

  statusMsg.textContent = "";
  statusMsg.style.display = "none";
  statusMsg.className = "friend-detail-status";

  addBtn.textContent = "Nhắn tin";
  addBtn.disabled = false;
  addBtn.onclick = () => startChatWithUser(user);

  if (!isFriend(user)) {
    statusMsg.innerHTML = "";

    if (user.email) {
      const addFriendButton = document.createElement("button");
      addFriendButton.type = "button";
      addFriendButton.className = "friend-detail-link-btn";
      addFriendButton.textContent = "Thêm bạn bè";
      addFriendButton.onclick = (event) => {
        event.stopPropagation();
        sendFriendRequest(user.email, true, addFriendButton);
      };
      statusMsg.appendChild(addFriendButton);
      statusMsg.style.display = "block";
    }
  }

  modal.style.display = "block";
}

function closeFriendDetailModal() {
  const modal = document.getElementById("friendDetailModal");
  if (modal) {
    modal.style.display = "none";
  }
}

function isDirectConversationWithUser(conversation, user) {
  if (!isDirectConversation(conversation)) return false;

  const targetUserId = getUserId(user);
  const targetEmail = (user?.email || "").toLowerCase();

  return (conversation.participants || []).some((participant) =>
    (targetUserId && getUserId(participant) === targetUserId) ||
    (targetEmail && (participant.email || "").toLowerCase() === targetEmail)
  );
}

async function ensureConversationWithUser(user) {
  const token =
    getAuthToken();
  const targetUserId = getUserId(user);

  const existingConversation = conversations.find((conv) =>
    isDirectConversationWithUser(conv, user)
  );

  if (existingConversation) return existingConversation;

  const response = await fetch(`${API_BASE_URL}/conversations`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ other_user_id: targetUserId }),
  });

  const result = await response.json().catch(() => ({}));
  if (!response.ok || result.success === false) {
    throw new Error(result.detail || result.message || "Không thể tạo cuộc trò chuyện.");
  }

  await loadConversations(true);
  const conversation = conversations.find((c) => c.id === result.conversation_id);
  if (!conversation) {
    throw new Error("Không tìm thấy cuộc trò chuyện vừa tạo.");
  }

  return conversation;
}

function waitForWebSocketOpen(timeout = 5000) {
  if (websocket?.readyState === WebSocket.OPEN) return Promise.resolve();

  return new Promise((resolve, reject) => {
    const startedAt = Date.now();
    const timer = setInterval(() => {
      if (websocket?.readyState === WebSocket.OPEN) {
        clearInterval(timer);
        resolve();
      } else if (Date.now() - startedAt >= timeout) {
        clearInterval(timer);
        reject(new Error("Không thể kết nối chat để gửi tệp."));
      }
    }, 100);
  });
}

async function startChatWithUser(user) {
  closeNewChatModal();
  closeFriendDetailModal();
  showLoading(true);

  try {
    const token =
      getAuthToken();

    const targetUserId = getUserId(user);
    const existingConversation = conversations.find((conv) =>
      isDirectConversationWithUser(conv, user)
    );

    if (existingConversation) {
      selectConversation(existingConversation);
      return;
    }

    const response = await fetch(`${API_BASE_URL}/conversations`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ other_user_id: targetUserId }),
    });

    if (response.ok) {
      const newConversationData = await response.json();

      await loadConversations(true);
      const newConversation = conversations.find(
        (c) => c.id === newConversationData.conversation_id
      );

      if (newConversation) {
        selectConversation(newConversation);
      }
    } else {
      const error = await response.json();
      throw new Error(
        `Không thể tạo cuộc trò chuyện: ${error.detail || response.statusText}`
      );
    }
  } catch (error) {
    console.error("Lỗi bắt đầu cuộc trò chuyện:", error);
    showError("Không thể bắt đầu cuộc trò chuyện. Vui lòng thử lại.");
  } finally {
    showLoading(false);
  }
}

function initializeSidebarPanelMode() {
  setSidebarPanelMode("conversations");
}

function toggleSidebarPanelMode() {
  const nextMode = activeSidebarPanelMode === "conversations" ? "friends" : "conversations";
  setSidebarPanelMode(nextMode);
}

function getSidebarModeToggleButtonMarkup(mode) {
  if (mode === "friends") {
    return `
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
        <path d="M8 9h8" />
        <path d="M8 13h5" />
      </svg>
      <span class="sr-only">Xem cuộc trò chuyện</span>
    `;
  }

  return `
    <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
      <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
      <circle cx="9" cy="7" r="4" />
      <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
      <path d="M16 3.13a4 4 0 0 1 0 7.75" />
    </svg>
    <span class="sr-only">Xem bạn bè</span>
  `;
}

function setSidebarPanelMode(mode, options = {}) {
  const normalizedMode = mode === "friends" ? "friends" : "conversations";
  activeSidebarPanelMode = normalizedMode;

  const toggleButton = document.getElementById("sidebarModeToggleBtn");
  const searchInput = document.getElementById("searchConversations");
  const sidebarList = document.getElementById("conversationList");

  if (searchInput) {
    searchInput.value = normalizedMode === "friends" ? activeFriendsSearchQuery : activeConversationSearchQuery;
  }

  if (toggleButton) {
    const isFriendsMode = normalizedMode === "friends";
    const nextActionLabel = isFriendsMode ? "Xem cuộc trò chuyện" : "Xem bạn bè";

    toggleButton.innerHTML = getSidebarModeToggleButtonMarkup(normalizedMode);
    toggleButton.classList.toggle("is-friends-mode", isFriendsMode);
    toggleButton.setAttribute(
      "aria-label",
      isFriendsMode
        ? "Chuyển danh sách bạn bè sang danh sách cuộc trò chuyện"
        : "Chuyển danh sách cuộc trò chuyện sang danh sách bạn bè"
    );
    toggleButton.setAttribute("title", nextActionLabel);
  }

  if (searchInput) {
    searchInput.placeholder = normalizedMode === "friends"
      ? "Tìm kiếm bạn bè..."
      : "Tìm kiếm cuộc trò chuyện...";
  }

  if (sidebarList) {
    sidebarList.classList.toggle("is-friends-mode", normalizedMode === "friends");
    sidebarList.classList.toggle("is-conversations-mode", normalizedMode === "conversations");
  }

  if (normalizedMode === "friends") {
    renderFriendsPanel(activeFriendsSearchQuery);
  } else {
    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
  }
}

function setupSearchFunctionality() {
  const searchInput = document.getElementById("searchConversations");
  if (!searchInput) return;

  searchInput.addEventListener("input", function () {
    const query = this.value.trim();

    if (activeSidebarPanelMode === "friends") {
      activeFriendsSearchQuery = query;
      renderFriendsPanel(activeFriendsSearchQuery);
      return;
    }

    activeConversationSearchQuery = query;
    filterConversations(activeConversationSearchQuery);
  });
}

function filterConversations(query) {
  activeConversationSearchQuery = query || "";
  renderConversations(activeConversationSearchQuery);
}

function setupFriendSearchAutoHide() {
  const friendInput = document.getElementById("searchFriendInput");
  const resultContainer = document.getElementById("friendSearchResultsContainer");
  if (!friendInput || !resultContainer) return;

  const hideResults = () => {
    resultContainer.innerHTML = "";
    resultContainer.classList.remove("is-open");
  };

  friendInput.addEventListener("focus", () => {
    if (friendInput.value.trim()) {
      searchUsers();
    }
  });

  document.addEventListener("click", (event) => {
    const target = event.target;
    if (target === friendInput || resultContainer.contains(target)) return;
    hideResults();
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      hideResults();
      friendInput.blur();
    }
  });
}

function normalizeForSearch(value) {
  return String(value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/Đ/g, "d")
    .toLowerCase();
}

function buildNormalizedIndexMap(value) {
  const text = String(value || "");
  let normalized = "";
  const map = [];

  Array.from(text).forEach((char, originalIndex) => {
    const normalizedChar = normalizeForSearch(char);
    Array.from(normalizedChar).forEach((outChar) => {
      normalized += outChar;
      map.push(originalIndex);
    });
  });

  return { normalized, map, original: text };
}

function highlightSearchTerm(text, query) {
  const originalText = String(text || "");
  const normalizedQuery = normalizeForSearch(query).trim();

  if (!normalizedQuery) {
    return escapeHtml(originalText);
  }

  const indexed = buildNormalizedIndexMap(originalText);
  const matchIndex = indexed.normalized.indexOf(normalizedQuery);

  if (matchIndex === -1) {
    return escapeHtml(originalText);
  }

  const start = indexed.map[matchIndex];
  const end = indexed.map[matchIndex + normalizedQuery.length - 1] + 1;

  return `${escapeHtml(originalText.slice(0, start))}<mark class="search-highlight">${escapeHtml(
    originalText.slice(start, end)
  )}</mark>${escapeHtml(originalText.slice(end))}`;
}

function formatTime(dateString) {
  if (!dateString) return "";

  try {
    const date = new Date(dateString);

    if (isNaN(date.getTime())) {
      return "Ngày không hợp lệ";
    }

    const now = new Date();
    const diff = now - date;

    if (diff < 60000) {
      return "Vừa xong";
    }

    if (diff < 3600000) {
      const minutes = Math.floor(diff / 60000);
      return `${minutes} phút trước`;
    }

    if (diff < 86400000) {
      const hours = Math.floor(diff / 3600000);
      return `${hours} giờ trước`;
    }

    return date.toLocaleDateString("vi-VN", {
      day: "2-digit",
      month: "2-digit",
      year: date.getFullYear() !== now.getFullYear() ? "numeric" : undefined,
    });
  } catch (error) {
    console.error("Lỗi định dạng thời gian:", error, "Dữ liệu:", dateString);
    return "Không rõ thời gian";
  }
}

function escapeHtml(text) {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function scrollToBottom() {
  const messagesContainer = document.getElementById("messagesContainer");
  messagesContainer.scrollTop = messagesContainer.scrollHeight;
}

function showLoading(show) {
  const loadingOverlay = document.getElementById("loadingOverlay");
  loadingOverlay.style.display = show ? "flex" : "none";
}

function showMessagingToast(message, type = "info") {
  return window.TravelwAIToast(message, type);
}

function showError(message) {
  window.TravelwAIToast(message);
}

function getPastedImageFile(event) {
  const items = Array.from(event?.clipboardData?.items || []);
  const imageItem = items.find((item) => String(item.type || "").startsWith("image/"));
  const file = imageItem?.getAsFile?.();
  if (!file) return null;
  if (file.name) return file;
  const extension = String(file.type || "image/png").split("/")[1] || "png";
  return new File([file], `anh-dan-${Date.now()}.${extension}`, { type: file.type || "image/png" });
}

function handleMessagingImagePaste(event) {
  const file = getPastedImageFile(event);
  if (!file) return;
  if (!currentConversation) {
    showError("Hãy chọn một cuộc trò chuyện trước khi dán ảnh.");
    return;
  }
  event.preventDefault();
  handleChatAttachmentChange({ target: { files: [file] } });
}

function handleAttachment() {
  const attachmentInput = document.getElementById("chatAttachmentInput");
  if (attachmentInput) attachmentInput.click();
}

function renderChatAttachmentPreview() {
  const preview = document.getElementById("chatAttachmentPreview");
  if (!preview) return;
  revokePreviewUrls(chatAttachmentPreviewObjectUrls);
  preview.innerHTML = selectedChatAttachments.map((file, index) => attachmentPreviewCard(file, index, "removeChatAttachment", chatAttachmentPreviewObjectUrls)).join("");
  preview.hidden = selectedChatAttachments.length === 0;
}

function handleChatAttachmentChange(event) {
  const files = Array.from(event.target.files || []);
  for (const file of files) {
    const type = String(file.type || "");
    if (file.size > MAX_CHAT_ATTACHMENT_SIZE) {
      showError(`${file.name} vượt quá 10MB.`);
      continue;
    }
    if (isAiConversation() && !type.startsWith("image/") && !type.startsWith("video/")) {
      showError("AI chỉ hỗ trợ ảnh hoặc video.");
      continue;
    }
    selectedChatAttachments.push(file);
  }
  if (isAiConversation() && selectedChatAttachments.length > MAX_AI_ATTACHMENT_COUNT) {
    showError(`AI chỉ nhận tối đa ${MAX_AI_ATTACHMENT_COUNT} ảnh hoặc video.`);
  }
  selectedChatAttachments = selectedChatAttachments.slice(0, isAiConversation() ? MAX_AI_ATTACHMENT_COUNT : 12);
  event.target.value = "";
  renderChatAttachmentPreview();
}

function removeChatAttachment(index) {
  selectedChatAttachments.splice(index, 1);
  renderChatAttachmentPreview();
}

function clearChatAttachment() {
  const attachmentInput = document.getElementById("chatAttachmentInput");
  if (attachmentInput) attachmentInput.value = "";
  selectedChatAttachments = [];
  renderChatAttachmentPreview();
}

async function addFriendFromCurrentConversation() {
  if (isAiConversation(currentConversation)) return;

  if (!currentConversation?.id) {
    showError("Chưa chọn cuộc trò chuyện để thêm bạn bè.");
    return;
  }

  const otherParticipant = getOtherParticipant(currentConversation);
  const friendEmail = otherParticipant?.email || "";
  const addFriendButton = document.getElementById("removeFriendBtn");

  if (!friendEmail) {
    showError("Không xác định được email người dùng để thêm bạn bè.");
    return;
  }

  if (addFriendButton) {
    addFriendButton.disabled = true;
    addFriendButton.setAttribute("title", "Đang gửi yêu cầu kết bạn...");
    addFriendButton.setAttribute("aria-label", "Đang gửi yêu cầu kết bạn");
  }

  try {
    showLoading(true);
    const token = getAuthToken();
    if (!token) {
      throw new Error("Lỗi xác thực. Vui lòng đăng nhập lại.");
    }

    const response = await fetch(`${API_BASE_URL}/friends/request`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ target_user_email: friendEmail }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể gửi yêu cầu kết bạn.");
    }

    markOutgoingFriendRequest(otherParticipant);
    setFriendActionButtonMode(addFriendButton, "sent", true);
    await refreshFriendsAndRequests(false, true);
    window.TravelwAIToast(result.message || "Đã gửi yêu cầu kết bạn.");
  } catch (error) {
    console.error("Lỗi gửi yêu cầu kết bạn:", error);

    const message = error.message || "Không thể gửi yêu cầu kết bạn. Vui lòng thử lại.";
    if (message.toLowerCase().includes("tồn tại") || message.toLowerCase().includes("đã là bạn")) {
      await refreshFriendsAndRequests(false, true);
      if (isFriend(otherParticipant)) {
        syncRemoveFriendButtonVisibility(otherParticipant);
      } else {
        markOutgoingFriendRequest(otherParticipant);
        setFriendActionButtonMode(addFriendButton, "sent", true);
      }
      window.TravelwAIToast(message);
    } else {
      setFriendActionButtonMode(addFriendButton, "add");
      showError(message);
    }
  } finally {
    showLoading(false);
  }
}

async function removeFriendFromCurrentConversation() {
  if (isAiConversation(currentConversation)) return;

  if (!currentConversation?.id) {
    showError("Chưa chọn cuộc trò chuyện để xóa bạn bè.");
    return;
  }

  const otherParticipant = getOtherParticipant(currentConversation);
  const friendUserId = getUserId(otherParticipant);
  const displayName = getConversationDisplayName(currentConversation);

  if (!friendUserId || friendUserId === "unknown") {
    showError("Không xác định được bạn bè cần xóa.");
    return;
  }

  const confirmed = await window.TravelwAIConfirm(
    `Bạn có chắc chắn muốn xóa ${displayName} khỏi danh sách bạn bè?
Cuộc trò chuyện hiện tại sẽ vẫn được giữ lại.`
  );
  if (!confirmed) return;

  const removeFriendButton = document.getElementById("removeFriendBtn");
  if (removeFriendButton) removeFriendButton.disabled = true;

  try {
    showLoading(true);
    const token = getAuthToken();
    if (!token) {
      throw new Error("Lỗi xác thực. Vui lòng đăng nhập lại.");
    }

    const response = await fetch(`${API_BASE_URL}/friends/${encodeURIComponent(friendUserId)}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể xóa bạn bè.");
    }

    user_friendList = (user_friendList || []).filter((friend) => getUserId(friend) !== friendUserId);
    renderFriendsList();
    syncRemoveFriendButtonVisibility(otherParticipant);
    await refreshFriendsAndRequests(false, true);
    syncRemoveFriendButtonVisibility(otherParticipant);
    window.TravelwAIToast(result.message || "Đã xóa khỏi danh sách bạn bè.");
  } catch (error) {
    console.error("Lỗi xóa bạn bè:", error);
    showError(error.message || "Không thể xóa bạn bè. Vui lòng thử lại.");
    if (removeFriendButton) removeFriendButton.disabled = false;
  } finally {
    showLoading(false);
  }
}

function closeCurrentConversation() {
  if (websocket) {
    websocket.onclose = null;
    websocket.close();
    websocket = null;
  }

  currentConversation = null;
  currentMessages = [];
  clearChatAttachment();

  const messageInput = document.getElementById("messageInput");
  if (messageInput) messageInput.value = "";

  resetConversationInterface();
  updateConversationSelection();
  renderConversations(activeConversationSearchQuery);
}

async function clearConversation() {
  if (isAiConversation()) {
    const confirmed = await window.TravelwAIConfirm(`Xóa toàn bộ lịch sử trò chuyện với ${AI_DISPLAY_NAME}?`);
    if (!confirmed) return;
    try { localStorage.removeItem(getAiHistoryStorageKey()); } catch (_) {}
    currentMessages = [];
    renderMessages();
    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
    window.TravelwAIToast("Đã xóa lịch sử trò chuyện với AI.");
    return;
  }

  if (!currentConversation?.id) {
    showError("Chưa chọn cuộc trò chuyện để xóa.");
    return;
  }

  const displayName = getConversationDisplayName(currentConversation);
  const confirmed = await window.TravelwAIConfirm(
    `Bạn có chắc chắn muốn xoá vĩnh viễn toàn bộ lịch sử cuộc trò chuyện với ${displayName}? Hành động này sẽ xoá tất cả tin nhắn và tệp đính kèm.`
  );
  if (!confirmed) return;

  try {
    showLoading(true);
    const token = getAuthToken();
    const response = await fetch(`${API_BASE_URL}/conversations/${currentConversation.id}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể xóa cuộc trò chuyện.");
    }

    if (websocket) {
      websocket.onclose = null;
      websocket.close();
      websocket = null;
    }

    conversations = conversations.filter((conversation) => conversation.id !== currentConversation.id);
    currentConversation = null;
    currentMessages = [];
    resetConversationInterface();
    renderConversations(activeConversationSearchQuery);
    window.TravelwAIToast(result.message || "Đã xoá toàn bộ lịch sử cuộc trò chuyện.", "success");
  } catch (error) {
    console.error("Lỗi xóa cuộc trò chuyện:", error);
    showError(error.message || "Không thể xóa cuộc trò chuyện. Vui lòng thử lại.");
  } finally {
    showLoading(false);
  }
}

function resetConversationInterface() {
  setMobileConversationOpenState(false);
  const welcomeScreen = document.getElementById("welcomeScreen");
  const conversationHeader = document.getElementById("conversationHeader");
  const messagesContainer = document.getElementById("messagesContainer");
  const messageInputContainer = document.getElementById("messageInputContainer");
  const messagesList = document.getElementById("messagesList");
  const aiSuggestions = document.getElementById("messageAiSuggestions");

  if (welcomeScreen) welcomeScreen.style.display = "flex";
  if (conversationHeader) conversationHeader.style.display = "none";
  if (messagesContainer) messagesContainer.style.display = "none";
  if (messageInputContainer) messageInputContainer.style.display = "none";
  if (messagesList) messagesList.innerHTML = "";
  if (aiSuggestions) aiSuggestions.hidden = true;
  setAiSuggestionButtonsDisabled(false);
}

window.addEventListener("beforeunload", function () {
  if (websocket) {
    websocket.onclose = null;
    websocket.close();
  }
  if (friendRefreshTimer) {
    clearInterval(friendRefreshTimer);
  }
});

window.addEventListener("click", function (event) {
  const newChatModal = document.getElementById("newChatModal");
  const shareMemoryModal = document.getElementById("shareMemoryModal");
  const friendDetailModal = document.getElementById("friendDetailModal");

  if (event.target === newChatModal) {
    closeNewChatModal();
  }
  if (event.target === shareMemoryModal) {
    closeShareMemoryModal();
  }
  if (friendDetailModal && event.target === friendDetailModal) {
    closeFriendDetailModal();
  }
});

function testMessagePositioning() {
  if (!currentConversation) {
    return;
  }

  const testMessages = [
    {
      id: "test1",
      sender_id: getCurrentUserId(),
      content: "This should appear on the RIGHT side (sent by you)",
      time_sent: new Date().toISOString(),
    },
    {
      id: "test2",
      sender_id: "other_user_test",
      content: "Tin nhắn này sẽ nằm bên trái (người khác gửi)",
      time_sent: new Date().toISOString(),
    },
    {
      id: "test3",
      sender_id: getCurrentUserId(),
      content: "Tin nhắn của bạn sẽ nằm bên phải",
      time_sent: new Date().toISOString(),
    },
  ];

  const originalMessages = currentMessages;
  currentMessages = testMessages;

  renderMessages();

  setTimeout(() => {
    currentMessages = originalMessages;
    renderMessages();
  }, 10000);
}

window.testMessagePositioning = testMessagePositioning;

async function sendFriendRequest(targetUserId, isFromModal = false, buttonOverride = null) {

  let buttonToUpdate;
  let statusMessageElement;

  if (isFromModal) {
    buttonToUpdate = buttonOverride || document.getElementById("friendDetailAddBtn");
    statusMessageElement = document.getElementById("friendDetailStatusMsg");
  } else {

    console.warn(
      "Gọi gửi yêu cầu kết bạn ngoài hộp thoại, chưa có UI dự phòng."
    );
  }

  if (buttonToUpdate) {
    buttonToUpdate.textContent = "Đang gửi...";
    buttonToUpdate.disabled = true;
  }
  if (statusMessageElement) {
    statusMessageElement.textContent = "Đang gửi yêu cầu...";
    statusMessageElement.className =
      "friend-detail-status friend-detail-status-processing";
    statusMessageElement.style.display = "block";
  }

  try {
    const token =
      getAuthToken();
    if (!token) {
      const errorMsg = "Lỗi xác thực. Vui lòng đăng nhập lại.";
      showError(errorMsg);
      if (buttonToUpdate) {
        buttonToUpdate.textContent = "Thêm bạn";
        buttonToUpdate.disabled = false;
      }
      if (statusMessageElement) {
        statusMessageElement.textContent = errorMsg;
        statusMessageElement.className =
          "friend-detail-status friend-detail-status-error";
      }
      return;
    }

    const response = await fetch(`${API_BASE_URL}/friends/request`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ target_user_email: targetUserId }),
    });

    const responseData = await response.json();

    if (response.ok && responseData.success) {
      const successMsg =
        responseData.message || "Đã gửi yêu cầu kết bạn.";

      if (statusMessageElement) {
        statusMessageElement.textContent = successMsg;
        statusMessageElement.className =
          "friend-detail-status friend-detail-status-success";
      } else {
        window.TravelwAIToast(successMsg);
      }
      if (buttonToUpdate) {
        buttonToUpdate.textContent = "Đã gửi yêu cầu";
        buttonToUpdate.disabled = true;
      }
      await refreshFriendsAndRequests(false, true);
    } else {
      const errorMessage =
        responseData.detail ||
        responseData.message ||
        "Không thể gửi yêu cầu kết bạn.";

      if (statusMessageElement) {
        statusMessageElement.textContent = errorMessage;
        statusMessageElement.className =
          "friend-detail-status friend-detail-status-error";
      } else {
        showError(errorMessage);
      }
      console.error(
        `Không thể gửi yêu cầu kết bạn (HTTP ${response.status}):`,
        responseData
      );
      if (buttonToUpdate) {
        if (response.status === 409) {

          buttonToUpdate.textContent =
            responseData.message || "Yêu cầu đã có";
        } else {
          buttonToUpdate.textContent = "Thêm bạn";
          buttonToUpdate.disabled = false;
        }
      }
    }
  } catch (error) {
    const networkErrorMsg =
      "Lỗi mạng hoặc lỗi khác khi gửi yêu cầu kết bạn. Vui lòng thử lại.";
    console.error("Lỗi mạng hoặc lỗi khác khi gửi yêu cầu kết bạn:", error);
    if (statusMessageElement) {
      statusMessageElement.textContent = networkErrorMsg;
      statusMessageElement.className =
        "friend-detail-status friend-detail-status-error";
    } else {
      showError(networkErrorMsg);
    }
    if (buttonToUpdate) {
      buttonToUpdate.textContent = "Thêm bạn";
      buttonToUpdate.disabled = false;
    }
  }
}

function createFriendRequestElement(request) {
  const item = document.createElement("div");
  item.className = "friend-request-item";

  let requester;
  if (request.requester_info) {

    requester = request.requester_info;
  } else {

    requester = {
      username: request.username || "Người dùng",
      email: request.email || "Chưa có email",
      profilePic: request.profilePic || null,
    };
  }

  const defaultAvatar = "logo/profile-icon-white.webp";
  let avatarHTML;

  if (requester.profilePic) {
    const avatarUrl = requester.profilePic.startsWith("http")
      ? requester.profilePic
      : `${API_BASE_URL.replace("/api", "")}${requester.profilePic}`;
    avatarHTML = `<img loading="lazy" decoding="async" src="${escapeHtml(avatarUrl)}" alt="${escapeHtml(
      requester.username || "User"
    )}" onerror="this.onerror=null; this.src='${defaultAvatar}';">`;
  } else {
    const initial = (requester.username || requester.name || requester.email || "U").charAt(0).toUpperCase();
    avatarHTML = `<div class="initials">${escapeHtml(initial)}</div>`;
  }

  item.innerHTML = `
    <div class="request-item-avatar">
      ${avatarHTML}
    </div>
    <div class="request-item-info">
      <div class="request-item-name">${escapeHtml(
        requester.username || "Người dùng"
      )}</div>
      <div class="request-item-email">${escapeHtml(
        requester.email || "Chưa có email"
      )}</div>
    </div>
    <div class="request-item-actions">
      <button type="button" class="btn-accept friend-request-icon-btn accept" aria-label="Đồng ý" title="Đồng ý">${getInlineIcon("check")}</button>
      <button type="button" class="btn-decline friend-request-icon-btn decline" aria-label="Từ chối" title="Từ chối">${getInlineIcon("x")}</button>
    </div>
  `;

  const acceptButton = item.querySelector(".btn-accept");
  const declineButton = item.querySelector(".btn-decline");

  const requestEmail = request.email || `temp-${Date.now()}`;
  acceptButton.addEventListener("click", () =>
    handleFriendRequestAction(requestEmail, "accepted", item)
  );
  declineButton.addEventListener("click", () =>
    handleFriendRequestAction(requestEmail, "declined", item)
  );

  return item;
}

async function handleFriendRequestAction(requestEmail, action, listItemElement, options = {}) {

  const buttons = listItemElement.querySelectorAll("button");
  buttons.forEach((btn) => (btn.disabled = true));
  listItemElement.style.opacity = 0.7;

  try {
    const token =
      getAuthToken();
    if (!token) {
      window.TravelwAIToast("Lỗi xác thực. Vui lòng đăng nhập lại.");
      buttons.forEach((btn) => (btn.disabled = false));
      listItemElement.style.opacity = 1;
      return;
    }
    const formData = new FormData();
    formData.append("request_email", requestEmail);
    formData.append("action", action);

    const response = await fetch(`${API_BASE_URL}/friend_requests`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
      },
      body: formData,
    });

    const responseData = await response.json();
    const innerResult = responseData.data || responseData;
    if (response.ok && responseData.success && innerResult.success !== false) {
      const successMessage = innerResult.message || responseData.message || (action === 'accepted' ? 'Đã chấp nhận yêu cầu kết bạn.' : 'Đã từ chối yêu cầu kết bạn.');
      if (!options.silent) window.TravelwAIToast(successMessage);

      await refreshFriendsAndRequests(false, true);
      await loadConversations(true);
    } else {
      const innerResult = responseData.data || responseData;
      window.TravelwAIToast(innerResult.message || responseData.message || "Không thể xử lý yêu cầu. Vui lòng thử lại.");
      buttons.forEach((btn) => (btn.disabled = false));
      listItemElement.style.opacity = 1;
    }
  } catch (error) {
    console.error(`Error ${action} friend request:`, error);
    window.TravelwAIToast("Đã xảy ra lỗi. Vui lòng thử lại.");
    buttons.forEach((btn) => (btn.disabled = false));
    listItemElement.style.opacity = 1;
  }
}

function initializeResizableMessagingLayout() {
  const layout = document.querySelector(".messaging-layout");
  const sidebar = document.querySelector(".chat-sidebar");
  const horizontalHandle = document.getElementById("chatWidthResizer");

  if (!layout || !sidebar || !horizontalHandle) {
    return;
  }

  const STORAGE_KEY = "travelwai.messaging.sidebarWidth";
  const LIMITS = {
    minSidebarWidth: 280,
    maxSidebarWidth: 560,
    minChatWidth: 420,
  };

  const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

  function isCompactLayout() {
    return window.matchMedia("(max-width: 920px)").matches;
  }

  function getClientPoint(event) {
    if (event.touches && event.touches.length) {
      return { x: event.touches[0].clientX, y: event.touches[0].clientY };
    }

    if (event.changedTouches && event.changedTouches.length) {
      return { x: event.changedTouches[0].clientX, y: event.changedTouches[0].clientY };
    }

    return { x: event.clientX, y: event.clientY };
  }

  function getMaxSidebarWidth() {
    const layoutWidth = layout.getBoundingClientRect().width;
    const handleWidth = horizontalHandle.getBoundingClientRect().width || 10;
    return Math.max(
      LIMITS.minSidebarWidth,
      Math.min(LIMITS.maxSidebarWidth, layoutWidth - handleWidth - LIMITS.minChatWidth)
    );
  }

  function setSidebarWidth(width, shouldSave = true) {
    if (isCompactLayout()) return;

    const safeWidth = Math.round(clamp(width, LIMITS.minSidebarWidth, getMaxSidebarWidth()));
    layout.style.setProperty("--chat-sidebar-width", `${safeWidth}px`);
    sidebar.style.setProperty("--chat-sidebar-width", `${safeWidth}px`);

    if (shouldSave) {
      localStorage.setItem(STORAGE_KEY, String(safeWidth));
    }
  }

  function restoreSavedWidth() {
    if (isCompactLayout()) return;

    const savedWidth = Number.parseInt(localStorage.getItem(STORAGE_KEY), 10);
    if (Number.isFinite(savedWidth)) {
      setSidebarWidth(savedWidth, false);
    }
  }

  function stopDragging(moveHandler, endHandler) {
    horizontalHandle.classList.remove("is-dragging");
    document.body.classList.remove("is-resizing-chat-layout");
    window.removeEventListener("mousemove", moveHandler);
    window.removeEventListener("mouseup", endHandler);
    window.removeEventListener("touchmove", moveHandler);
    window.removeEventListener("touchend", endHandler);
    window.removeEventListener("touchcancel", endHandler);
  }

  horizontalHandle.addEventListener("mousedown", startHorizontalDrag);
  horizontalHandle.addEventListener("touchstart", startHorizontalDrag, { passive: false });

  function startHorizontalDrag(event) {
    if (isCompactLayout()) return;
    event.preventDefault();

    horizontalHandle.classList.add("is-dragging");
    document.body.classList.add("is-resizing-chat-layout");

    const moveHandler = (moveEvent) => {
      moveEvent.preventDefault();
      const point = getClientPoint(moveEvent);
      const layoutLeft = layout.getBoundingClientRect().left;
      setSidebarWidth(point.x - layoutLeft);
    };

    const endHandler = () => stopDragging(moveHandler, endHandler);

    window.addEventListener("mousemove", moveHandler);
    window.addEventListener("mouseup", endHandler);
    window.addEventListener("touchmove", moveHandler, { passive: false });
    window.addEventListener("touchend", endHandler);
    window.addEventListener("touchcancel", endHandler);
  }

  let resizeTimer = null;
  window.addEventListener("resize", () => {
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => {
      if (!isCompactLayout()) {
        const currentWidth = sidebar.getBoundingClientRect().width;
        setSidebarWidth(currentWidth, false);
      }
    }, 120);
  });

  restoreSavedWidth();
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) {
    refreshFriendsAndRequests(false).catch(() => {});
    if (activeSidebarPanelMode === "conversations") {
      loadConversations().catch(() => {});
    }
  }
});

window.removeChatAttachment = removeChatAttachment;
window.removeSelectedMemoryFile = removeSelectedMemoryFile;


