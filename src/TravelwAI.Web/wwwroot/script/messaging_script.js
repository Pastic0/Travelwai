
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
let selectedChatAttachment = null;
let selectedGroupUsers = [];
let currentChatModalMode = "chat";
let aiMessageSending = false;
let outgoingFriendRequestKeys = new Set();
let pendingAiContextByAssistant = {};
const API_BASE_URL = "/api";
const CLIENT_CACHE_VERSION = "2026-07-01-clean-v1";
const USERS_CACHE_TTL_MS = 5 * 60 * 1000;
const FRIEND_CACHE_TTL_MS = 30 * 1000;
const CONVERSATION_CACHE_TTL_MS = 15 * 1000;
const MESSAGE_CACHE_TTL_MS = 30 * 1000;
const FRIEND_REFRESH_MS = 30 * 1000;
const MAX_CHAT_ATTACHMENT_SIZE = 10 * 1024 * 1024;
const CHAT_MESSAGE_PAYLOAD_TYPE = "travelwai-chat-message";
const AI_CONVERSATION_ID = "travelwai-ai-conversation";
const AI_STORAGE_PREFIX = "travelwai-ai-chat-history";
const AI_PENDING_PROMPT_KEY = "travelwai-ai-pending-prompt";
const AI_AVATAR_VERSION_KEY = "travelwaiAiAvatarVersion";
const SUPPORT_ADMIN_EMAIL = "2324802010387@student.tdmu.edu.vn";
const ADMIN_PENDING_MESSAGE_KEY = "travelwai-admin-pending-message";

function getAiAvatarVersion() {
  try {
    return localStorage.getItem(AI_AVATAR_VERSION_KEY) || "";
  } catch {
    return "";
  }
}

function buildAiAvatarUrl(fileName) {
  const version = getAiAvatarVersion();
  return `/logo/${fileName}${version ? `?v=${encodeURIComponent(version)}` : ""}`;
}

const AI_ASSISTANT_USER = {
  id: "travelwai-ai",
  username: "Quản lý TravelwAI",
  name: "Quản lý TravelwAI",
  email: "manager@travelwai.local",
  profilePic: buildAiAvatarUrl("travelwai-manager-avatar.webp"),
};
const RAG_GUIDE_ASSISTANT_USER = {
  id: "travelwai-rag-guide-ai",
  username: "Hướng dẫn viên RAG AI",
  name: "Hướng dẫn viên RAG AI",
  email: "rag-guide@travelwai.local",
  profilePic: buildAiAvatarUrl("travelwinne-guide-avatar.webp"),
};
const RAG_GUIDE_CONVERSATION_ID = "travelwai-rag-guide-conversation";
const AI_ASSISTANT_CONFIGS = {
  travelwai: {
    key: "travelwai",
    mode: "travelwai",
    conversationId: AI_CONVERSATION_ID,
    displayName: "Quản lý TravelwAI",
    statusText: "Quản lý và điều hướng web",
    defaultLastMessage: "Quản lý và điều hướng web",
    user: AI_ASSISTANT_USER,
    welcome: "Xin chào, mình là Quản lý TravelwAI. Bạn có thể nhắn mình để mở trang, lập lịch trình, tìm tour, nhắn tin với người khác, đổi mật khẩu hoặc đăng xuất.",
    suggestions: [
      "Tôi muốn lập lịch trình",
      "Tôi muốn xem bản đồ",
      "Tôi muốn đổi mật khẩu"
    ]
  },
  guide: {
    key: "guide",
    mode: "guide-rag",
    conversationId: RAG_GUIDE_CONVERSATION_ID,
    displayName: "Hướng dẫn viên RAG AI",
    statusText: "Tra cứu di sản, làng nghề và lộ trình",
    defaultLastMessage: "Hỏi về di tích, làng nghề, nghệ nhân, lịch trình",
    user: RAG_GUIDE_ASSISTANT_USER,
    welcome: "Xin chào, mình là Hướng dẫn viên RAG AI của TravelwAI. Bạn có thể hỏi về di tích, làng nghề, nghệ nhân, văn hoá, tâm linh hoặc nhờ gợi ý lịch trình tham quan.",
    suggestions: [
      "Kể về Hoàng thành Thăng Long",
      "Gợi ý 1 ngày ở Hà Nội",
      "Bát Tràng có gì đặc biệt?"
    ]
  }
};

function refreshAiAvatarUrls() {
  AI_ASSISTANT_USER.profilePic = buildAiAvatarUrl("travelwai-manager-avatar.webp");
  RAG_GUIDE_ASSISTANT_USER.profilePic = buildAiAvatarUrl("travelwinne-guide-avatar.webp");
}

function refreshAiAvatarInMessages() {
  refreshAiAvatarUrls();
  currentMessages = normalizeAiMessageAvatars(currentMessages);
}

function normalizeAiMessageAvatars(messages) {
  refreshAiAvatarUrls();
  return (messages || []).map((message) => {
    if (message?.sender_id === AI_ASSISTANT_USER.id) {
      return { ...message, sender_info: AI_ASSISTANT_USER };
    }
    if (message?.sender_id === RAG_GUIDE_ASSISTANT_USER.id) {
      return { ...message, sender_info: RAG_GUIDE_ASSISTANT_USER };
    }
    return message;
  });
}

function handleAiAvatarVersionChange() {
  refreshAiAvatarInMessages();
  conversations = (conversations || []).map((conversation) => {
    if (!isAiConversation(conversation)) return conversation;
    const config = getAiConfig(conversation);
    return {
      ...conversation,
      other_user_info: config.user,
      participants: [currentUser || {}, config.user]
    };
  });
  if (currentConversation && isAiConversation(currentConversation)) {
    const config = getAiConfig(currentConversation);
    currentConversation = {
      ...currentConversation,
      other_user_info: config.user,
      participants: [currentUser || {}, config.user]
    };
    showConversationInterface();
    renderMessages();
  }
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
}

window.addEventListener("storage", (event) => {
  if (event.key === AI_AVATAR_VERSION_KEY) {
    handleAiAvatarVersionChange();
  }
});

function checkAuth() {
  const token = localStorage.getItem("idToken");
  const expiration = localStorage.getItem("tokenExpiration");

  if (!token) {
    return false;
  }

  if (expiration && Date.now() >= parseInt(expiration)) {
    return false;
  }

  return true;
}

document.addEventListener("DOMContentLoaded", function () {
  syncMessagingMobileViewport();
  initializeResizableMessagingLayout();
  initializeSidebarPanelMode();
  initializeMessaging();
});

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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");

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
    const handledDirectChat = await handlePendingDirectChat();
    if (!handledDirectChat) await handlePendingAiPrompt();
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
    localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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

  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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

function getUserAvatarUrl(user) {
  const profilePic = user?.profilePic || user?.photoURL || user?.avatar || user?.profile_picture_url;
  return profilePic ? resolveAvatarUrl(profilePic) : null;
}

function normalizeAiAssistantKey(value) {
  const key = String(value || "").trim().toLowerCase();
  if (["guide", "rag", "guide-rag", "travel-guide", "travel-guide-rag", "huong-dan-vien", "travelwinne"].includes(key)) return "guide";
  return "travelwai";
}

function getAiConfig(value) {
  if (typeof value === "string") return AI_ASSISTANT_CONFIGS[normalizeAiAssistantKey(value)] || AI_ASSISTANT_CONFIGS.travelwai;
  const id = value?.id || value?.conversation_id || value?.conversationId;
  if (id === RAG_GUIDE_CONVERSATION_ID) return AI_ASSISTANT_CONFIGS.guide;
  if (value?.assistant_key) return AI_ASSISTANT_CONFIGS[normalizeAiAssistantKey(value.assistant_key)] || AI_ASSISTANT_CONFIGS.travelwai;
  return AI_ASSISTANT_CONFIGS.travelwai;
}

function getAiWelcomeMessage(configValue) {
  const config = getAiConfig(configValue);
  return {
    id: `${config.key}-welcome`,
    sender_id: config.user.id,
    sender_info: config.user,
    content: config.welcome,
    time_sent: new Date().toISOString(),
    is_system_welcome: true,
  };
}

function isAiConversation(conversation) {
  const id = conversation?.id || conversation?.conversation_id || conversation?.conversationId;
  return Boolean(conversation?.is_ai) || id === AI_CONVERSATION_ID || id === RAG_GUIDE_CONVERSATION_ID;
}

function isGroupConversation(conversation) {
  if (!conversation || isAiConversation(conversation)) return false;
  return Boolean(conversation.is_group) ||
    conversation.conversation_type === "group" ||
    (Array.isArray(conversation.participants) && conversation.participants.length > 2) ||
    (Array.isArray(conversation.participant_ids) && conversation.participant_ids.length > 2);
}

function isDirectConversation(conversation) {
  return !isAiConversation(conversation) && !isGroupConversation(conversation);
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
  if (!conversation || isAiConversation(conversation) || isGroupConversation(conversation)) return "";

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

function getAiStorageKey(configValue) {
  const config = getAiConfig(configValue || currentConversation);
  return `${AI_STORAGE_PREFIX}:${config.key}:${getCurrentUserId() || currentUser?.email || "guest"}`;
}

function loadStoredAiMessages(configValue) {
  try {
    const raw = localStorage.getItem(getAiStorageKey(configValue));
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch (error) {
    console.warn("Không đọc được lịch sử AI:", error);
    return [];
  }
}

function saveStoredAiMessages(messages, configValue) {
  try {
    const cleanMessages = (messages || [])
      .filter((message) => !message.is_system_welcome)
      .slice(-100);
    localStorage.setItem(getAiStorageKey(configValue), JSON.stringify(cleanMessages));
  } catch (error) {
    console.warn("Không lưu được lịch sử AI:", error);
  }
}

function getAiConversation(configValue = "travelwai") {
  const config = getAiConfig(configValue);
  const storedMessages = loadStoredAiMessages(config.key);
  const lastStoredMessage = storedMessages[storedMessages.length - 1];
  return {
    id: config.conversationId,
    is_ai: true,
    assistant_key: config.key,
    other_user_info: config.user,
    participants: [currentUser || {}, config.user],
    last_message: lastStoredMessage?.content || config.defaultLastMessage,
    last_message_time: lastStoredMessage?.time_sent || lastStoredMessage?.timestamp || "",
    unread_count: {},
  };
}

function getAiConversations() {
  return [getAiConversation("travelwai"), getAiConversation("guide")];
}

function getAiVisibleMessages(configValue = currentConversation || "travelwai") {
  const config = getAiConfig(configValue);
  const storedMessages = loadStoredAiMessages(config.key);
  return storedMessages.length > 0 ? normalizeAiMessageAvatars(storedMessages) : [getAiWelcomeMessage(config.key)];
}

function buildAiHistoryForRequest(messages) {
  return (messages || [])
    .filter((message) => !message.is_system_welcome && message.content)
    .slice(-12)
    .map((message) => ({
      role: isMessageFromCurrentUser(message) ? "user" : "assistant",
      content: message.content,
    }));
}

function clampAiContextText(value, maxLength = 3400) {
  const text = String(value || "")
    .replace(/[\u0000-\u001F\u007F]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  return text.length > maxLength ? text.slice(0, maxLength).trim() : text;
}

function setPendingAiContext(assistantKey, context) {
  const key = normalizeAiAssistantKey(assistantKey || "travelwai");
  const cleaned = clampAiContextText(context);
  if (cleaned) pendingAiContextByAssistant[key] = cleaned;
}

function consumePendingAiContext(assistantKey) {
  const key = normalizeAiAssistantKey(assistantKey || "travelwai");
  const value = pendingAiContextByAssistant[key] || "";
  delete pendingAiContextByAssistant[key];
  return value;
}

function buildAiContextForRequest(aiConfig, text) {
  const pendingContext = consumePendingAiContext(aiConfig.key);
  if (aiConfig.key !== "guide") return pendingContext || "";

  const guideContext = window.TravelwAIGuideChatbot && typeof window.TravelwAIGuideChatbot.buildContextForMessage === "function"
    ? window.TravelwAIGuideChatbot.buildContextForMessage(text)
    : "";

  return [pendingContext, guideContext].filter(Boolean).join("\n\n");
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
  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
        showMessagingToast("Đã gửi tin nhắn trong hội thoại Admin chính.", "success");
      } catch (error) {
        showMessagingToast("Đã mở hội thoại Admin chính. Bạn bấm Gửi để gửi nội dung đang nhập.", "info");
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

async function handlePendingAiPrompt() {
  const params = new URLSearchParams(window.location.search);
  const aiParam = params.get("ai") || params.get("assistant") || "";
  const shouldOpenAi = aiParam === "1" || aiParam === "travelwai" || normalizeAiAssistantKey(aiParam) === "guide";
  let pending = null;

  try {
    const raw = localStorage.getItem(AI_PENDING_PROMPT_KEY);
    if (raw) pending = JSON.parse(raw);
  } catch (error) {
    console.warn("Không đọc được câu hỏi AI chờ sẵn:", error);
  }

  if (!shouldOpenAi && !pending?.prompt) return;

  const targetAssistant = normalizeAiAssistantKey(pending?.assistant || pending?.target || aiParam || "travelwai");
  await selectAiConversation(targetAssistant);

  if (pending?.prompt) {
    if (pending.context) {
      setPendingAiContext(targetAssistant, pending.context);
    }
    localStorage.removeItem(AI_PENDING_PROMPT_KEY);
    const messageInput = document.getElementById("messageInput");
    if (messageInput) {
      messageInput.value = pending.prompt;
      showMessagingToast(`Đang hỏi ${getAiConfig(targetAssistant).displayName}. Bạn có thể tiếp tục thao tác.`, "info");
      setTimeout(() => {
        sendAiMessage({ background: true });
      }, 0);
    }
  }
}

function getAiLoadingIconHtml() {
  return `
    <span class="ai-send-spinner" aria-hidden="true"></span>`;
}

function setAiSendButtonLoading(isLoading) {
  aiMessageSending = Boolean(isLoading);
  const sendButton = document.querySelector(".message-input .send-btn");
  if (!sendButton || !isAiConversation(currentConversation)) return;

  sendButton.classList.remove("ai-send-btn");
  sendButton.classList.toggle("ai-send-loading", aiMessageSending);
  sendButton.disabled = aiMessageSending;
  sendButton.setAttribute("aria-label", aiMessageSending ? "AI đang trả lời" : "Gửi câu hỏi cho AI");
  sendButton.title = aiMessageSending ? "AI đang trả lời" : "Gửi câu hỏi cho AI";
  sendButton.innerHTML = aiMessageSending ? getAiLoadingIconHtml() : "Gửi";
}

function applyAiInputMode(isAiMode) {
  const messageInputContainer = document.getElementById("messageInputContainer");
  const messageInput = document.getElementById("messageInput");
  const attachmentInput = document.getElementById("chatAttachmentInput");
  const attachmentButton = document.querySelector(".message-input .attachment-btn");
  const shareMemoryButton = document.querySelector(".message-input .share-memory-btn");
  const attachmentPreview = document.getElementById("chatAttachmentPreview");
  const sendButton = document.querySelector(".message-input .send-btn");

  if (messageInputContainer) {
    messageInputContainer.classList.toggle("ai-input-mode", isAiMode);
  }

  const aiConfig = isAiMode ? getAiConfig(currentConversation) : null;

  if (messageInput) {
    messageInput.placeholder = isAiMode ? `Nhập câu hỏi cho ${aiConfig.displayName}...` : "Nhập tin nhắn...";
  }

  if (attachmentInput) {
    attachmentInput.disabled = isAiMode;
    attachmentInput.hidden = isAiMode;
  }

  if (attachmentButton) {
    attachmentButton.hidden = isAiMode;
    attachmentButton.disabled = isAiMode;
    attachmentButton.style.display = isAiMode ? "none" : "";
  }

  if (shareMemoryButton) {
    shareMemoryButton.hidden = isAiMode;
    shareMemoryButton.disabled = isAiMode;
    shareMemoryButton.style.display = isAiMode ? "none" : "";
  }

  if (attachmentPreview) {
    attachmentPreview.hidden = isAiMode || !selectedChatAttachment;
    attachmentPreview.style.display = isAiMode ? "none" : "";
  }

  if (sendButton) {
    sendButton.classList.remove("ai-send-btn");
    sendButton.classList.toggle("ai-send-loading", isAiMode && aiMessageSending);
    sendButton.disabled = isAiMode && aiMessageSending;
    sendButton.setAttribute("aria-label", isAiMode ? "Gửi câu hỏi cho AI" : "Gửi tin nhắn");
    sendButton.title = isAiMode ? "Gửi câu hỏi cho AI" : "Gửi tin nhắn";
    sendButton.innerHTML = isAiMode && aiMessageSending ? getAiLoadingIconHtml() : "Gửi";
  }

  renderAiSuggestionRow(isAiMode);

  if (isAiMode) {
    clearChatAttachment();
  } else {
    aiMessageSending = false;
  }
}

function renderAiSuggestionRow(isAiMode) {
  const container = document.getElementById("messageInputContainer");
  if (!container) return;

  container.querySelector(".ai-suggestion-row")?.remove();
  if (!isAiMode) return;

  const aiConfig = getAiConfig(currentConversation);
  const row = document.createElement("div");
  row.className = "ai-suggestion-row";

  (aiConfig.suggestions || []).slice(0, 3).forEach((question) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "ai-suggestion-chip";
    button.textContent = question;
    button.addEventListener("click", async () => {
      const input = document.getElementById("messageInput");
      if (input) input.value = question;
      await sendAiMessage();
    });
    row.appendChild(button);
  });

  const inputBox = container.querySelector(".message-input");
  if (inputBox) container.insertBefore(row, inputBox);
  else container.prepend(row);
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");

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
      console.error("🔐 Yêu cầu hồ sơ thất bại:", response.status, errorData);
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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

  const allConversations = [...getAiConversations(), ...(conversations || [])];
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
      normalizeForSearch(participantText).includes(normalizedQuery) ||
      (isAiConversation(conversation) && normalizeForSearch("ai hoi tri tue nhan tao quan ly travelwai dieu huong web lap lich trinh doi mat khau dang xuat huong dan vien rag di tich lang nghe nghe nhan van hoa tam linh du lich").includes(normalizedQuery))
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
  if (isAiConversation(conversation)) return getAiConfig(conversation).displayName;
  if (isGroupConversation(conversation)) return getGroupDisplayName(conversation);
  return getDirectConversationDisplayName(conversation);
}

function createConversationElement(conversation, searchQuery = "") {
  const div = document.createElement("div");
  const isAi = isAiConversation(conversation);
  const isGroup = isGroupConversation(conversation);
  div.className = `conversation-item${isAi ? " ai-conversation-item pinned-conversation" : ""}${isGroup ? " group-conversation-item" : ""}`;
  div.dataset.conversationId = conversation.id || "";
  div.onclick = () => selectConversation(conversation);

  const aiConfig = isAi ? getAiConfig(conversation) : null;
  const otherParticipant = isAi
    ? aiConfig.user
    : isGroup
      ? { username: getGroupDisplayName(conversation), name: getGroupDisplayName(conversation), email: getGroupMemberText(conversation), profilePic: null }
      : getOtherParticipant(conversation);
  const displayName = getConversationDisplayName(conversation);
  const lastMessage = getMessagePreview(conversation.last_message) || (isAi ? aiConfig.defaultLastMessage : (isGroup ? getGroupMemberText(conversation) : "Chưa có tin nhắn"));
  const unreadCount = isAi ? 0 : (conversation.unread_count?.[getCurrentUserId()] || 0);
  const avatarSrc = isAi ? aiConfig.user.profilePic : (getUserAvatarUrl(otherParticipant) || "logo/profile-icon-white.webp");

  div.dataset.conversationName = displayName;
  div.dataset.lastMessage = lastMessage;

  div.innerHTML = `
        <div class="user-avatar">
            <img loading="lazy" decoding="async" src="${escapeHtml(avatarSrc)}" alt="${escapeHtml(displayName)}" onerror="this.src='logo/profile-icon-white.webp'" />
        </div>
        <div class="conversation-item-info">
            <div class="conversation-item-name">${highlightSearchTerm(displayName, searchQuery)}</div>
            <div class="conversation-item-message">
                ${highlightSearchTerm(lastMessage, searchQuery)}
            </div>
        </div>
        <div class="conversation-item-meta">
            <div class="conversation-item-time">
                ${isAi ? "" : (conversation.last_message_time ? formatTime(conversation.last_message_time) : "")}
            </div>
            ${isAi ? '<div class="ai-pill">AI</div>' : (unreadCount > 0 ? `<div class="unread-badge">${unreadCount}</div>` : "")}
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
  };
}

async function selectConversation(conversation) {
  if (isAiConversation(conversation)) {
    await selectAiConversation(conversation);
    return;
  }

  if (activeSidebarPanelMode !== "conversations") {
    setSidebarPanelMode("conversations");
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
    localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
  if (!token) {
    showError("Không tìm thấy token đăng nhập.");
    return;
  }

  const wsBaseUrl = API_BASE_URL.replace(/^http/, "ws").replace("/api", "");
  const wsUrl = `${wsBaseUrl}/ws/conversations/${conversation.id}?token=${token}`;

  websocket = new WebSocket(wsUrl);

  websocket.onopen = () => {
  };

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

async function selectAiConversation(configValue = "travelwai") {
  if (activeSidebarPanelMode !== "conversations") {
    setSidebarPanelMode("conversations");
  }

  if (websocket) {
    websocket.onclose = null;
    websocket.close();
    websocket = null;
  }

  const aiConfig = getAiConfig(configValue);
  currentConversation = getAiConversation(aiConfig.key);
  showConversationInterface();
  currentMessages = getAiVisibleMessages(aiConfig.key);
  renderMessages();
  updateConversationSelection();
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
  const isGroupMode = isGroupConversation(currentConversation);
  const aiConfig = isAiMode ? getAiConfig(currentConversation) : null;
  const otherParticipant = isAiMode
    ? aiConfig.user
    : isGroupMode
      ? { name: getGroupDisplayName(currentConversation), username: getGroupDisplayName(currentConversation), profilePic: null }
      : getOtherParticipant(currentConversation);

  document.getElementById("conversationUserName").textContent = isAiMode
    ? aiConfig.displayName
    : isGroupMode
      ? getGroupDisplayName(currentConversation)
      : getConversationDisplayName(currentConversation);

  const statusElement = document.getElementById("conversationUserStatus");
  if (isAiMode) {
    if (statusElement) {
      statusElement.textContent = aiConfig.statusText;
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
    updateConversationUserStatus("offline", getConversationDisplayName(currentConversation));
  }

  syncConversationNameButtonVisibility();
  syncRemoveFriendButtonVisibility(otherParticipant);
  applyAiInputMode(isAiMode);

  const headerAvatar = document.getElementById("conversationUserAvatar");
  headerAvatar.src = isAiMode ? aiConfig.user.profilePic : (getUserAvatarUrl(otherParticipant) || "logo/profile-icon-white.webp");
  headerAvatar.onerror = () =>
    (headerAvatar.src = "logo/profile-icon-white.webp");
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

  if (!currentConversation || isAiConversation(currentConversation)) {
    nameButton.hidden = true;
    nameButton.disabled = true;
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
  if (!currentConversation?.id || isAiConversation(currentConversation)) return;

  const isGroupMode = isGroupConversation(currentConversation);
  const currentName = isGroupMode ? getGroupDisplayName(currentConversation) : getConversationNickname(currentConversation);
  const defaultName = isGroupMode ? getGroupDisplayName(currentConversation) : getConversationDisplayName(currentConversation);
  const label = isGroupMode ? "Nhập tên nhóm mới:" : "Nhập biệt danh mới:";
  const nextName = await window.TravelwAIPrompt(label, currentName || defaultName || "");

  if (nextName === null) return;

  const cleanName = nextName.trim();
  if (!cleanName) {
    showError(isGroupMode ? "Tên nhóm không được để trống." : "Biệt danh không được để trống.");
    return;
  }

  if (cleanName.length > 60) {
    showError(isGroupMode ? "Tên nhóm tối đa 60 ký tự." : "Biệt danh tối đa 60 ký tự.");
    return;
  }

  await saveConversationDisplayName(cleanName);
}

async function saveConversationDisplayName(displayName) {
  const nameButton = document.getElementById("conversationNameBtn");
  try {
    if (nameButton) nameButton.disabled = true;
    showLoading(true);

    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
    if (!token) throw new Error("Không tìm thấy token đăng nhập.");

    const response = await fetch(`${API_BASE_URL}/conversations/${encodeURIComponent(currentConversation.id)}/name`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ display_name: displayName }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể lưu tên cuộc trò chuyện.");
    }

    const updatedConversation = result.data || {};
    currentConversation = { ...currentConversation, ...updatedConversation };
    const index = conversations.findIndex((conversation) => conversation.id === currentConversation.id);
    if (index >= 0) {
      conversations[index] = { ...conversations[index], ...updatedConversation };
    }

    invalidateClientCache("conversations");
    showConversationInterface();
    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
  } catch (error) {
    console.error("Lỗi đổi tên cuộc trò chuyện:", error);
    showError(error.message || "Không thể lưu tên cuộc trò chuyện. Vui lòng thử lại.");
  } finally {
    showLoading(false);
    syncConversationNameButtonVisibility();
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

  if (mode === "remove") {
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

  if (isAiConversation(currentConversation) || isGroupConversation(currentConversation)) {
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
}

function appendMessage(message) {
  if (document.getElementById(`msg-${message.id}`)) {
    return;
  }
  const messageList = document.getElementById("messagesList");
  const messageElement = createMessageElement(message);
  messageList.appendChild(messageElement);
  scrollToBottom();
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

  const cleanName = displayName || getConversationDisplayName(currentConversation) || "Người dùng";
  const cleanStatus = status === "online" ? "online" : "offline";
  statusElement.textContent = `${cleanName} ${cleanStatus}`;
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

function parseMessageContent(content) {
  if (!content || typeof content !== "string") {
    return { text: "", attachment: null };
  }

  try {
    const payload = JSON.parse(content);
    if (payload?.type === CHAT_MESSAGE_PAYLOAD_TYPE) {
      return {
        text: payload.text || "",
        attachment: payload.attachment || null,
      };
    }
  } catch {
  }

  return { text: content, attachment: null };
}

function buildMessageContent(text, attachment) {
  if (!attachment) return text;

  return JSON.stringify({
    type: CHAT_MESSAGE_PAYLOAD_TYPE,
    version: 1,
    text,
    attachment,
  });
}

function getMessagePreview(content) {
  const parsed = parseMessageContent(content);
  if (parsed.text) return parsed.text;
  if (parsed.attachment?.name) return `Tệp: ${parsed.attachment.name}`;
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
    : getUserDisplayName(senderInfo) || message.sender_name || getUserDisplayName(otherParticipant) || "Bạn bè";

  const avatarContent = createAvatarContent(
    senderProfilePic,
    senderName?.charAt(0) || "U",
    isCurrentUser
  );

  const messageTime =
    message.timestamp || message.time_sent || new Date().toISOString();
  const parsedContent = parseMessageContent(message.content || "");

  messageBubble.innerHTML = `
        <div class="message-sender">${escapeHtml(senderName)}</div>
        <div class="message-content">
        </div>
        <div class="message-meta">
            <span class="timestamp">${formatTime(messageTime)}</span>
        </div>
    `;

  const contentElement = messageBubble.querySelector(".message-content");
  if (parsedContent.attachment) {
    messageBubble.classList.add("has-attachment");
    contentElement.classList.add("has-attachment");
  }

  if (parsedContent.text) {
    const text = document.createElement("p");
    text.className = "message-text";
    text.textContent = parsedContent.text;
    contentElement.appendChild(text);
  }

  if (parsedContent.attachment) {
    contentElement.appendChild(createAttachmentElement(parsedContent.attachment));
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

const createAvatarContent = (profilePic, initial, isCurrentUser = false) => {
  const avatar = document.createElement("div");
  avatar.className = `user-avatar message-avatar ${
    isCurrentUser ? "avatar-sent" : "avatar-received"
  }`;

  if (profilePic) {
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
  if (isAiConversation(currentConversation)) {
    await sendAiMessage();
    return;
  }

  const messageInput = document.getElementById("messageInput");
  const typedContent = messageInput.value.trim();

  if ((!typedContent && !selectedChatAttachment) || !currentConversation) {
    return;
  }

  if (websocket && websocket.readyState === WebSocket.OPEN) {
    try {
      showLoading(true);
      const attachment = selectedChatAttachment
        ? await uploadChatAttachment(selectedChatAttachment)
        : null;
      const content = buildMessageContent(typedContent, attachment);

      websocket.send(content);

      messageInput.value = "";
      clearChatAttachment();
    } catch (error) {
      console.error("Lỗi gửi tin nhắn:", error);
      showError(error.message || "Không thể gửi tin nhắn. Vui lòng thử lại.");
    } finally {
      showLoading(false);
    }
  } else {
    showError("Không có kết nối tin nhắn. Vui lòng thử lại.");
    console.error("WebSocket chưa kết nối hoặc chưa sẵn sàng.");
  }
}

function buildLocalAiAssistantMessage(text, configValue = currentConversation || "travelwai") {
  const config = getAiConfig(configValue);
  return {
    id: `ai-local-${config.key}-${Date.now()}`,
    sender_id: config.user.id,
    sender_info: config.user,
    content: text,
    time_sent: new Date().toISOString(),
  };
}

function appendLocalAiAssistantReply(text, configValue = currentConversation || "travelwai") {
  const config = getAiConfig(configValue);
  const aiMessage = buildLocalAiAssistantMessage(text, config.key);
  currentMessages.push(aiMessage);
  appendMessage(aiMessage);
  saveStoredAiMessages(currentMessages, config.key);
  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
  return aiMessage;
}


function getTravelwaiManagerFallbackReply() {
  return "Chưa nhận diện được lệnh. Bạn có thể nhắn: đăng nhập, đăng ký, bản đồ, lịch trình, kế hoạch, bảng giá, giỏ hàng, thanh toán, tour du lịch, bài viết, nhắn tin, đổi mật khẩu hoặc đăng xuất.";
}

function getLastTravelwaiManagerReplyText() {
  const messages = loadStoredAiMessages("travelwai");
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index];
    if (message?.sender_id === AI_ASSISTANT_USER.id && message.content && !message.is_system_welcome) {
      return String(message.content || "");
    }
  }
  return "";
}

function isManagerConfirmText(text) {
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
    { url: "/messaging", reply: "Đang mở trang Nhắn tin.", patterns: [/tin\s*nhan/, /nhan\s*tin/, /messaging/, /chat/] },
    { url: "/profile", reply: "Đang mở trang Hồ sơ.", patterns: [/ho\s*so/, /thong\s*tin\s*ca\s*nhan/, /tai\s*khoan/, /doi\s*ten/] },
    { url: "/notifications", reply: "Đang mở trang Thông báo.", patterns: [/thong\s*bao/, /notification/] },
    { url: "/messaging?admin=1", reply: "Đang mở hội thoại với Admin.", patterns: [/phan\s*hoi/, /lien\s*he/, /gop\s*y/, /ho\s*tro/] },
    { url: "/home", reply: "Đang mở trang chủ.", patterns: [/trang\s*chu/, /home/] },
    { url: "/landing", reply: "Đang mở giới thiệu TravelwAI.", patterns: [/landing/, /gioi\s*thieu/, /trang\s*gioi\s*thieu/] },
  ];

  return rules.find((rule) => rule.patterns.some((pattern) => pattern.test(normalized))) || null;
}

function getConfirmedNavigationTargetFromLastManagerReply() {
  const lastReply = getLastTravelwaiManagerReplyText();
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

function extractDirectChatTarget(text) {
  const original = String(text || "").trim();
  const normalized = normalizeForSearch(original);
  const patterns = [
    /(?:nhan\s*tin|nhan\s*toi|chat|noi\s*chuyen|tro\s*chuyen)\s*(?:voi|cung)\s+(.+)/i,
    /(?:mo\s*)?(?:cuoc\s*)?(?:tro\s*chuyen|tin\s*nhan)\s*(?:voi|cung)\s+(.+)/i,
  ];

  for (const pattern of patterns) {
    const match = normalized.match(pattern);
    if (!match || !match[1]) continue;
    let normalizedTarget = match[1]
      .replace(/\b(di|di\s*cho|minh|toi|nhe|nha|voi|a|giup\s*toi|giup\s*minh)\b/g, " ")
      .replace(/\s+/g, " ")
      .trim();

    if (!normalizedTarget) continue;

    const originalWords = original.split(/\s+/);
    const normalizedWords = normalized.split(/\s+/);
    const targetWords = normalizedTarget.split(/\s+/);
    const targetStart = normalizedWords.findIndex((_, index) => targetWords.every((word, offset) => normalizedWords[index + offset] === word));
    if (targetStart >= 0) {
      return originalWords.slice(targetStart, targetStart + targetWords.length).join(" ").replace(/[.,!?;:]+$/g, "").trim();
    }

    return normalizedTarget.replace(/[.,!?;:]+$/g, "").trim();
  }

  return "";
}

function findUserByNameOrEmail(query) {
  const normalizedQuery = normalizeForSearch(query).trim();
  if (!normalizedQuery) return null;
  const currentId = getCurrentUserId();

  const candidates = (all_users || []).filter((user) => getUserId(user) !== currentId);
  const scored = candidates.map((user) => {
    const name = normalizeForSearch(getUserDisplayName(user));
    const email = normalizeForSearch(user.email || "");
    const local = email.split("@")[0] || "";
    let score = 0;
    if (name === normalizedQuery || local === normalizedQuery || email === normalizedQuery) score = 100;
    else if (name.startsWith(normalizedQuery) || local.startsWith(normalizedQuery)) score = 80;
    else if (name.includes(normalizedQuery) || email.includes(normalizedQuery)) score = 60;
    return { user, score };
  }).filter((item) => item.score > 0);

  scored.sort((a, b) => b.score - a.score);
  return scored[0]?.user || null;
}

async function handleManagerDirectChatCommand(text) {
  const targetName = extractDirectChatTarget(text);
  if (!targetName) return false;

  try {
    await get_all_users(true);
    const targetUser = findUserByNameOrEmail(targetName);
    if (!targetUser) {
      appendLocalAiAssistantReply(`Chưa tìm thấy người dùng tên ${targetName}. Bạn kiểm tra lại tên hoặc email rồi nhắn lại nhé.`, "travelwai");
      return true;
    }

    appendLocalAiAssistantReply(`Đang mở cuộc trò chuyện với ${getUserDisplayName(targetUser)}.`, "travelwai");
    await startChatWithUser(targetUser);
    return true;
  } catch (error) {
    console.error("Lỗi mở cuộc trò chuyện theo yêu cầu AI quản lý:", error);
    appendLocalAiAssistantReply("Chưa mở được cuộc trò chuyện. Vui lòng thử lại sau.", "travelwai");
    return true;
  }
}

function extractFriendRequestTarget(text) {
  const original = String(text || "").trim();
  const directEmail = original.match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/i)?.[0] || "";
  const normalized = normalizeForSearch(original);
  const patterns = [
    /(?:gui|goi)\s*(?:loi\s*moi\s*)?(?:ket\s*ban|ban\s*be)\s*(?:den|toi|cho|voi)\s+(.+)/i,
    /(?:gui|goi)\s*(?:yeu\s*cau\s*)?(?:ket\s*ban|ban\s*be)\s*(?:den|toi|cho|voi)\s+(.+)/i,
    /(?:ket\s*ban)\s*(?:voi|cung|cho)\s+(.+)/i,
    /(?:them\s*ban|them\s*ban\s*be)\s*(?:voi|cho)?\s+(.+)/i,
  ];

  for (const pattern of patterns) {
    const match = normalized.match(pattern);
    if (!match || !match[1]) continue;

    let normalizedTarget = match[1]
      .replace(/\b(di|nhe|nha|voi|a|giup\s*toi|giup\s*minh|cho\s*toi|cho\s*minh|minh|toi)\b/g, " ")
      .replace(/\s+/g, " ")
      .trim();

    if (!normalizedTarget) continue;

    if (directEmail && normalizeForSearch(directEmail) === normalizedTarget.replace(/\s+/g, "")) {
      return directEmail;
    }

    const originalWords = original.split(/\s+/);
    const normalizedWords = normalized.split(/\s+/);
    const targetWords = normalizedTarget.split(/\s+/);
    const targetStart = normalizedWords.findIndex((_, index) => targetWords.every((word, offset) => normalizedWords[index + offset] === word));
    if (targetStart >= 0) {
      return originalWords.slice(targetStart, targetStart + targetWords.length).join(" ").replace(/[.,!?;:]+$/g, "").trim();
    }

    return normalizedTarget.replace(/[.,!?;:]+$/g, "").trim();
  }

  return "";
}

async function sendManagerFriendRequestToUser(targetUser, fallbackTarget = "") {
  const targetEmail = targetUser?.email || fallbackTarget;
  if (!targetEmail) {
    throw new Error("Không xác định được email người dùng để gửi kết bạn.");
  }

  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
  if (!token) {
    throw new Error("Lỗi xác thực. Vui lòng đăng nhập lại.");
  }

  const response = await fetch(`${API_BASE_URL}/friends/request`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({ target_user_email: targetEmail }),
  });

  const result = await response.json().catch(() => ({}));
  if (!response.ok || result.success === false) {
    throw new Error(result.detail || result.message || "Không thể gửi yêu cầu kết bạn.");
  }

  if (targetUser) markOutgoingFriendRequest(targetUser);
  await refreshFriendsAndRequests(false, true);
  return result.message || "Đã gửi yêu cầu kết bạn.";
}

async function handleManagerFriendRequestCommand(text) {
  const targetName = extractFriendRequestTarget(text);
  if (!targetName) return false;

  try {
    await get_all_users(true);
    const targetUser = findUserByNameOrEmail(targetName);
    const targetEmail = /@/.test(targetName) ? targetName : "";

    if (!targetUser && !targetEmail) {
      appendLocalAiAssistantReply(`Chưa tìm thấy người dùng tên ${targetName}. Bạn nhập lại tên hoặc email chính xác hơn nhé.`, "travelwai");
      return true;
    }

    const displayName = targetUser ? getUserDisplayName(targetUser) : targetEmail;
    const resultMessage = await sendManagerFriendRequestToUser(targetUser, targetEmail);
    appendLocalAiAssistantReply(`${resultMessage} Người nhận: ${displayName}.`, "travelwai");
    return true;
  } catch (error) {
    console.error("Lỗi gửi kết bạn theo yêu cầu AI quản lý:", error);
    appendLocalAiAssistantReply(error.message || "Chưa gửi được yêu cầu kết bạn. Vui lòng thử lại sau.", "travelwai");
    return true;
  }
}

async function tryHandleTravelwaiManagerCommand(text) {
  if (getAiConfig(currentConversation).key !== "travelwai") return false;

  if (isManagerConfirmText(text)) {
    const confirmedTarget = getConfirmedNavigationTargetFromLastManagerReply();
    if (confirmedTarget) {
      appendLocalAiAssistantReply(confirmedTarget.reply, "travelwai");
      if (confirmedTarget.password) {
        sessionStorage.setItem("travelwaiOpenProfilePassword", "1");
      }
      if (confirmedTarget.url) {
        setTimeout(() => { window.location.href = confirmedTarget.url; }, 650);
      }
      return true;
    }
    appendLocalAiAssistantReply("Dùng cú pháp: tới trang [tên trang], qua trang [tên trang] hoặc chi tiết trang [tên trang].", "travelwai");
    return true;
  }

  if (await handleManagerFriendRequestCommand(text)) {
    return true;
  }

  if (await handleManagerDirectChatCommand(text)) {
    return true;
  }

  const target = getManagerNavigationTarget(text);
  if (!target) return false;

  appendLocalAiAssistantReply(target.reply, "travelwai");

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
    setTimeout(() => {
      window.location.href = target.url;
    }, 650);
  }
  return true;
}

async function sendAiMessage(options = {}) {
  if (aiMessageSending) return;

  if (!isAiConversation(currentConversation)) {
    await selectAiConversation("travelwai");
  }

  const aiConfig = getAiConfig(currentConversation);
  const aiKey = aiConfig.key;
  const activeConversationIdAtSend = currentConversation?.id || getAiConversation(aiKey).id;

  clearChatAttachment();

  const messageInput = document.getElementById("messageInput");
  const typedContent = messageInput.value.trim();

  if (!typedContent) {
    showError("Nhập câu hỏi trước khi gửi cho AI.");
    return;
  }

  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
  if (!token) {
    showError("Không tìm thấy token đăng nhập.");
    return;
  }

  const storedBeforeSend = loadStoredAiMessages(aiKey);
  const userMessage = {
    id: `ai-user-${Date.now()}`,
    sender_id: getCurrentUserId(),
    sender_info: currentUser,
    content: typedContent,
    time_sent: new Date().toISOString(),
  };

  const messagesAfterUser = [...storedBeforeSend, userMessage];
  saveStoredAiMessages(messagesAfterUser, aiKey);

  if (currentConversation?.id === activeConversationIdAtSend) {
    currentMessages = messagesAfterUser;
    appendMessage(userMessage);
  }

  renderConversations(activeConversationSearchQuery);
  updateConversationSelection();
  messageInput.value = "";

  if (await tryHandleTravelwaiManagerCommand(typedContent)) {
    return;
  }

  if (aiKey === "travelwai") {
    appendLocalAiAssistantReply(getTravelwaiManagerFallbackReply(), "travelwai");
    return;
  }

  try {
    setAiSendButtonLoading(true);
    const aiContext = buildAiContextForRequest(aiConfig, typedContent);
    const response = await fetch(`${API_BASE_URL}/ai/chat`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({
        message: typedContent,
        history: buildAiHistoryForRequest(storedBeforeSend),
        assistant: aiConfig.mode,
        context: aiContext,
      }),
    });

    const result = await response.json().catch(() => ({}));
    if (!response.ok || result.success === false) {
      throw new Error(result.detail || result.message || "Không thể gọi AI.");
    }

    const replyText = String(result.data?.reply || "").trim();
    if (!replyText || /openrouter|kh[oô]ng\s*tr[aả]\s*v[eề]\s*n[oộ]i\s*dung|kh[oô]ng\s*c[oó]\s*ph[aả]n\s*h[oồ]i|qu[aá]\s*t[aả]i|gi[oớ]i\s*h[aạ]n\s*l[uư][oợ]t\s*g[oọ]i|đ[oổ]i\s*model/i.test(replyText)) {
      throw new Error("AI không có phản hồi rõ ràng.");
    }

    const aiMessage = {
      id: `ai-reply-${Date.now()}`,
      sender_id: aiConfig.user.id,
      sender_info: aiConfig.user,
      content: replyText,
      time_sent: new Date().toISOString(),
    };

    const latestMessages = loadStoredAiMessages(aiKey);
    const messagesAfterReply = [...latestMessages, aiMessage];
    saveStoredAiMessages(messagesAfterReply, aiKey);

    if (currentConversation?.id === activeConversationIdAtSend) {
      currentMessages = messagesAfterReply;
      appendMessage(aiMessage);
    } else {
      showMessagingToast(`${aiConfig.displayName} đã trả lời.`, "success");
    }

    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
  } catch (error) {
    console.error("Lỗi hỏi AI:", error);
    const errorMessage = String(error.message || "Không thể gọi AI. Vui lòng thử lại.");
    const friendlyMessage = /429|quá tải|qua tai|giới hạn|gioi han|rate|limit|openrouter|model/i.test(errorMessage)
      ? "Hiện chưa trả lời được. Bạn thử lại sau."
      : errorMessage;
    if (/free|nâng cấp|nang cap|upgrade_required|free_ai_quota_exceeded/i.test(errorMessage) && window.TravelwAIPricingPopup?.showFreeAiPopup) {
      window.TravelwAIPricingPopup.showFreeAiPopup(errorMessage);
      return;
    }
    if (aiKey === "travelwai") {
      appendLocalAiAssistantReply(getTravelwaiManagerFallbackReply(), "travelwai");
      renderConversations(activeConversationSearchQuery);
      updateConversationSelection();
    } else if (options.background) {
      showMessagingToast(friendlyMessage, "error");
    } else {
      showError(friendlyMessage);
    }
  } finally {
    setAiSendButtonLoading(false);
  }
}

async function uploadChatAttachment(file) {
  if (file.size > MAX_CHAT_ATTACHMENT_SIZE) {
    throw new Error("Tệp đính kèm không được vượt quá 10MB.");
  }

  const token =
    localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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

function updateSelectedMemoryFile(event) {
  const file = event.target.files && event.target.files[0];
  const selectedFileCard = document.getElementById("selectedMemoryFile");
  const selectedFileName = document.getElementById("selectedMemoryFileName");

  if (!selectedFileCard || !selectedFileName) return;

  if (!file) {
    resetMemoryFileSelection();
    return;
  }

  selectedFileName.textContent = `${file.name} (${formatFileSize(file.size)})`;
  selectedFileCard.hidden = false;
}

function resetMemoryFileSelection() {
  const fileInput = document.getElementById("memoryFile");
  const selectedFileCard = document.getElementById("selectedMemoryFile");
  const selectedFileName = document.getElementById("selectedMemoryFileName");

  if (fileInput) fileInput.value = "";
  if (selectedFileName) selectedFileName.textContent = "Chưa chọn tệp";
  if (selectedFileCard) selectedFileCard.hidden = true;
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
  const memoryFile = document.getElementById("memoryFile").files[0];

  if (!memoryFile) {
    window.TravelwAIToast("Vui lòng chọn tệp kỷ niệm để chia sẻ.");
    return;
  }

  if (!selectedUserForSharing) {
    window.TravelwAIToast(
      "Vui lòng chọn người nhận bằng cách nhập email và bấm vào gợi ý."
    );
    return;
  }

  try {
    showLoading(true);
    const conversation = await ensureConversationWithUser(selectedUserForSharing);
    await selectConversation(conversation);
    await waitForWebSocketOpen();

    selectedChatAttachment = memoryFile;
    const attachment = await uploadChatAttachment(memoryFile);
    websocket.send(buildMessageContent("Chia sẻ kỷ niệm", attachment));

    closeShareMemoryModal();
    clearChatAttachment();
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
  const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
    const hintText = document.createElement("span");
    hintText.textContent = "Người lạ vẫn có thể nhắn tin. ";
    statusMsg.appendChild(hintText);

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
    }

    statusMsg.style.display = "block";
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
    localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");

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
  const oldToast = document.querySelector(".messaging-toast");
  if (oldToast) oldToast.remove();

  const toast = document.createElement("div");
  toast.className = `messaging-toast ${type}`;
  toast.textContent = message;
  document.body.appendChild(toast);

  requestAnimationFrame(() => toast.classList.add("show"));
  setTimeout(() => {
    toast.classList.remove("show");
    setTimeout(() => toast.remove(), 220);
  }, 2800);
}

function showError(message) {
  window.TravelwAIToast(message);
}

function handleAttachment() {
  if (isAiConversation(currentConversation)) {
    clearChatAttachment();
    return;
  }

  const attachmentInput = document.getElementById("chatAttachmentInput");
  if (attachmentInput) attachmentInput.click();
}

function handleChatAttachmentChange(event) {
  if (isAiConversation(currentConversation)) {
    clearChatAttachment();
    return;
  }

  const file = event.target.files && event.target.files[0];
  const preview = document.getElementById("chatAttachmentPreview");
  const fileName = document.getElementById("chatAttachmentName");

  selectedChatAttachment = file || null;

  if (!preview || !fileName) return;

  if (!file) {
    clearChatAttachment();
    return;
  }

  if (file.size > MAX_CHAT_ATTACHMENT_SIZE) {
    showError("Tệp đính kèm không được vượt quá 10MB.");
    clearChatAttachment();
    return;
  }

  fileName.textContent = `${file.name} (${formatFileSize(file.size)})`;
  preview.hidden = false;
}

function clearChatAttachment() {
  const attachmentInput = document.getElementById("chatAttachmentInput");
  const preview = document.getElementById("chatAttachmentPreview");
  const fileName = document.getElementById("chatAttachmentName");

  selectedChatAttachment = null;
  if (attachmentInput) attachmentInput.value = "";
  if (fileName) fileName.textContent = "Chưa chọn tệp";
  if (preview) preview.hidden = true;
}

async function addFriendFromCurrentConversation() {
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
    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
    window.TravelwAIToast(result.message || "Yêu cầu kết bạn đã được gửi thành công.");
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
    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
  aiMessageSending = false;
  clearChatAttachment();

  const messageInput = document.getElementById("messageInput");
  if (messageInput) messageInput.value = "";

  resetConversationInterface();
  updateConversationSelection();
  renderConversations(activeConversationSearchQuery);
}

async function clearConversation() {
  if (!currentConversation?.id) {
    showError("Chưa chọn cuộc trò chuyện để xóa.");
    return;
  }

  if (isAiConversation(currentConversation)) {
    const confirmed = await window.TravelwAIConfirm("Bạn có chắc chắn muốn xóa lịch sử cuộc trò chuyện AI không?");
    if (!confirmed) return;

    localStorage.removeItem(getAiStorageKey(currentConversation));
    currentMessages = getAiVisibleMessages(currentConversation);
    renderMessages();
    renderConversations(activeConversationSearchQuery);
    updateConversationSelection();
    return;
  }

  const displayName = getConversationDisplayName(currentConversation);
  const confirmed = await window.TravelwAIConfirm(
    `Bạn có chắc chắn muốn xóa cuộc trò chuyện với ${displayName}?
Tin nhắn trong cuộc trò chuyện này sẽ bị xóa và không thể khôi phục.`
  );
  if (!confirmed) return;

  try {
    showLoading(true);
    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
    window.TravelwAIToast("Đã xóa cuộc trò chuyện.");
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

  if (welcomeScreen) welcomeScreen.style.display = "flex";
  if (conversationHeader) conversationHeader.style.display = "none";
  if (messagesContainer) messagesContainer.style.display = "none";
  if (messageInputContainer) messageInputContainer.style.display = "none";
  if (messagesList) messagesList.innerHTML = "";
  applyAiInputMode(false);
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
        responseData.message || "Yêu cầu kết bạn đã được gửi thành công!";

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

function renderFriendRequests(requests) {
  const listContainer = document.getElementById("friendRequestList") || document.getElementById("friendRequestListContainer");
  if (!listContainer) return;
  listContainer.innerHTML = "";

  if (requests.length === 0) {
    listContainer.innerHTML =
      '<div class="no-requests-message">Không có yêu cầu kết bạn nào đang chờ.</div>';
    return;
  }

  requests.forEach((request) => {
    const requestElement = createFriendRequestElement(request);
    listContainer.appendChild(requestElement);
  });
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
      localStorage.getItem("idToken") || sessionStorage.getItem("idToken");
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
