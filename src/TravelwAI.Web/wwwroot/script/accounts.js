
const AUTH_API_BASE = "/api";
const MINIMUM_PASSWORD_LENGTH = 8;
const SUPPORT_ADMIN_PENDING_MESSAGE_KEY = "travelwai-admin-pending-message";

const TRAVELWAI_RETURN_URL_KEY = "travelwai-post-login-return-url";

function readTravelwAICookie(name) {
  const value = `; ${document.cookie || ""}`;
  const parts = value.split(`; ${name}=`);
  if (parts.length === 2) return decodeURIComponent(parts.pop().split(";").shift() || "");
  return "";
}

function normalizeLocalReturnUrl(value, fallback = "/home") {
  const raw = String(value || "").trim();
  if (!raw) return fallback;

  try {
    const url = new URL(raw, window.location.origin);
    if (url.origin !== window.location.origin) return fallback;
    const next = `${url.pathname}${url.search}${url.hash}`;
    if (!next.startsWith("/") || next.startsWith("//")) return fallback;
    if (/^\/(login|signup|forgot-password|reset-password)(\/|\?|#|$)/i.test(next)) return fallback;
    return next;
  } catch (_) {
    if (raw.startsWith("/") && !raw.startsWith("//") && !/^\/(login|signup|forgot-password|reset-password)(\/|\?|#|$)/i.test(raw)) {
      return raw;
    }
    return fallback;
  }
}

function getCurrentReturnUrl() {
  return normalizeLocalReturnUrl(`${window.location.pathname}${window.location.search}${window.location.hash}`, "/home");
}

function getLoginReturnUrl(fallback = "/home") {
  const params = new URLSearchParams(window.location.search);
  const queryReturnUrl = params.get("returnUrl") || params.get("redirect") || "";
  const storedReturnUrl = sessionStorage.getItem(TRAVELWAI_RETURN_URL_KEY) || "";
  return normalizeLocalReturnUrl(queryReturnUrl || storedReturnUrl, fallback);
}

function buildLoginUrl(returnUrl) {
  const next = normalizeLocalReturnUrl(returnUrl || getCurrentReturnUrl(), "/home");
  return `/login?returnUrl=${encodeURIComponent(next)}`;
}

function redirectToLogin(returnUrl) {
  const next = normalizeLocalReturnUrl(returnUrl || getCurrentReturnUrl(), "/home");
  sessionStorage.setItem(TRAVELWAI_RETURN_URL_KEY, next);
  window.location.href = buildLoginUrl(next);
}

function getPostLoginRedirectUrl() {
  const next = getLoginReturnUrl("/home");
  sessionStorage.removeItem(TRAVELWAI_RETURN_URL_KEY);
  return next;
}


function clearSupportAdminLocalConversationCache() {
  try { localStorage.removeItem(SUPPORT_ADMIN_PENDING_MESSAGE_KEY); } catch (_) {}
  try {
    Object.keys(localStorage).forEach((key) => {
      if (/^travelwai:[^:]+:[^:]+:(conversations|messages:)/.test(key)) {
        localStorage.removeItem(key);
      }
    });
  } catch (_) {}
}

async function cleanupSupportAdminConversationBeforeLogout(options = {}) {
  const token = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  clearSupportAdminLocalConversationCache();
  if (!token) return false;

  const request = fetch(`${AUTH_API_BASE}/support/admin-conversation`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
    keepalive: true,
  }).catch(() => false);

  if (options.awaitRequest) {
    await request;
  }
  return true;
}

window.clearTravelwAIAdminSupportOnLogout = cleanupSupportAdminConversationBeforeLogout;

function isTravelwAIFreeRole(role) {
  const value = String(role || "Free").trim().toLowerCase();
  return value === "free" || value === "user" || value === "";
}

function migrateLegacyPersistentAuthSession() {
  const legacyIdToken = localStorage.getItem("idToken") || "";
  const legacyRefreshToken = localStorage.getItem("refreshToken") || "";
  const legacyExpiration = localStorage.getItem("tokenExpiration") || "";

  if (legacyIdToken && !sessionStorage.getItem("idToken")) sessionStorage.setItem("idToken", legacyIdToken);
  if (legacyRefreshToken && !sessionStorage.getItem("refreshToken")) sessionStorage.setItem("refreshToken", legacyRefreshToken);
  if (legacyExpiration && !sessionStorage.getItem("tokenExpiration")) sessionStorage.setItem("tokenExpiration", legacyExpiration);

  localStorage.removeItem("idToken");
  localStorage.removeItem("refreshToken");
  localStorage.removeItem("tokenExpiration");

  const activeIdToken = sessionStorage.getItem("idToken");
  const activeRefreshToken = sessionStorage.getItem("refreshToken");
  if (activeIdToken) document.cookie = `TravelwAIAuth=${encodeURIComponent(activeIdToken)}; path=/; SameSite=Lax`;
  if (activeRefreshToken) document.cookie = `TravelwAIRefresh=${encodeURIComponent(activeRefreshToken)}; path=/; SameSite=Lax`;
}

migrateLegacyPersistentAuthSession();

function saveTokens(idToken, refreshToken, email, expiresIn, username, role, isLocked) {
  localStorage.removeItem("idToken");
  localStorage.removeItem("refreshToken");
  localStorage.removeItem("tokenExpiration");

  if (idToken) sessionStorage.setItem("idToken", idToken);
  if (refreshToken) sessionStorage.setItem("refreshToken", refreshToken);
  if (email) {
    localStorage.setItem("userEmail", email);
    sessionStorage.setItem("userEmail", email);
  }
  if (username) {
    localStorage.setItem("username", username);
    sessionStorage.setItem("username", username);
  }
  if (role) {
    localStorage.setItem("userRole", role);
    sessionStorage.setItem("userRole", role);
  }
  if (typeof isLocked !== "undefined") {
    localStorage.setItem("isLocked", String(!!isLocked));
    sessionStorage.setItem("isLocked", String(!!isLocked));
  }
  updateIdleActivityStamp();

  const expiresInSeconds = parseInt(expiresIn, 10) || 3600;

  if (idToken) {
    document.cookie = `TravelwAIAuth=${encodeURIComponent(idToken)}; path=/; SameSite=Lax`;
  }
  if (refreshToken) {
    document.cookie = `TravelwAIRefresh=${encodeURIComponent(refreshToken)}; path=/; SameSite=Lax`;
  }

  const expirationTime = Date.now() + expiresInSeconds * 1000;
  sessionStorage.setItem("tokenExpiration", expirationTime.toString());
}

function clearLocalAuthData() {
  localStorage.removeItem("idToken");
  localStorage.removeItem("refreshToken");
  localStorage.removeItem("userEmail");
  localStorage.removeItem("tokenExpiration");
  localStorage.removeItem("username");
  localStorage.removeItem("userRole");
  localStorage.removeItem("isLocked");
  localStorage.removeItem("travelwaiLastActivityAt");
  sessionStorage.removeItem("idToken");
  sessionStorage.removeItem("refreshToken");
  sessionStorage.removeItem("userEmail");
  sessionStorage.removeItem("tokenExpiration");
  sessionStorage.removeItem("username");
  sessionStorage.removeItem("userRole");
  sessionStorage.removeItem("isLocked");
  sessionStorage.removeItem("travelwaiIdleLogoutRunning");
  document.cookie = "TravelwAIAuth=; path=/; max-age=0; SameSite=Lax";
  document.cookie = "TravelwAIRefresh=; path=/; max-age=0; SameSite=Lax";
}

function clearAuthData(options = {}) {
  if (!options.skipSupportCleanup) {
    cleanupSupportAdminConversationBeforeLogout({ awaitRequest: false });
  }
  const token = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  clearLocalAuthData();
  try { localStorage.setItem("travelwaiLogoutAt", Date.now().toString()); } catch (_) {}
  fetch(`${AUTH_API_BASE}/logout`, {
    method: "POST",
    credentials: "same-origin",
    headers: token ? { Authorization: `Bearer ${token}` } : {}
  }).catch(() => {});
}

function isAuthenticated() {
  const idToken = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  const expiration = localStorage.getItem("tokenExpiration") || sessionStorage.getItem("tokenExpiration");

  if (!idToken) {
    clearAuthData();
    return false;
  }

  if (!expiration) {
    return true;
  }

  const expirationNumber = parseInt(expiration, 10);
  if (Number.isNaN(expirationNumber)) {
    clearAuthData();
    return false;
  }

  if (Date.now() >= expirationNumber) {

    refreshTokenIfNeeded();
    return false;
  }

  return true;
}

let travelwaiRefreshTokenPromise = null;

async function refreshTokenIfNeeded(options = {}) {
  if (travelwaiRefreshTokenPromise) return travelwaiRefreshTokenPromise;

  travelwaiRefreshTokenPromise = (async () => {
    const refreshToken = sessionStorage.getItem("refreshToken") || localStorage.getItem("refreshToken") || readTravelwAICookie("TravelwAIRefresh");
    if (!refreshToken) {
      clearAuthData();
      return false;
    }

    const controller = new AbortController();
    const timeoutId = window.setTimeout(() => controller.abort("refresh-timeout"), 12000);

    try {
      const response = await fetch(`${AUTH_API_BASE}/refresh-token`, {
        method: "POST",
        credentials: "same-origin",
        cache: "no-store",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ refreshToken }),
        signal: controller.signal,
      });

      const result = await response.json().catch(() => ({}));
      if (!response.ok || !result.success || !result.idToken) {
        clearAuthData();
        return false;
      }

      saveTokens(
        result.idToken,
        result.refreshToken || refreshToken,
        result.email || sessionStorage.getItem("userEmail") || localStorage.getItem("userEmail"),
        result.expiresIn,
        result.username || result.displayName || sessionStorage.getItem("username") || localStorage.getItem("username"),
        result.role || sessionStorage.getItem("userRole") || localStorage.getItem("userRole") || "Free",
        result.is_locked || result.isLocked || false
      );

      if (options.redirectOnSuccess && window.location.pathname === "/login") {
        const postLoginUrl = getPostLoginRedirectUrl();
        if (isTravelwAIFreeRole(result.role || "Free")) {
          sessionStorage.setItem("travelwaiOpenPricingAfterLogin", "1");
        }
        window.location.href = postLoginUrl;
      }
      return true;
    } catch (error) {
      if (controller.signal.aborted) {
        console.error("Làm mới phiên đăng nhập quá thời gian chờ.");
      } else {
        console.error("Lỗi làm mới phiên đăng nhập:", error);
      }
      clearAuthData();
      return false;
    } finally {
      window.clearTimeout(timeoutId);
    }
  })();

  try {
    return await travelwaiRefreshTokenPromise;
  } finally {
    travelwaiRefreshTokenPromise = null;
  }
}

async function logout() {
  await cleanupSupportAdminConversationBeforeLogout({ awaitRequest: true });
  const token = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  clearLocalAuthData();
  try { localStorage.setItem("travelwaiLogoutAt", Date.now().toString()); } catch (_) {}
  try {
    await fetch(`${AUTH_API_BASE}/logout`, {
      method: "POST",
      credentials: "same-origin",
      headers: token ? { Authorization: `Bearer ${token}` } : {}
    });
  } catch (_) {}
  window.location.href = "/";
}

async function handleLogin(event) {
  event.preventDefault();

  clearLocalAuthData();

  const email = document.getElementById("email").value.trim();
  const password = document.getElementById("password").value;

  try {
    const response = await fetch(`${AUTH_API_BASE}/login`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, username: "", password }),
    });

    const result = await response.json();

    if (result.success) {
      saveTokens(
        result.idToken,
        result.refreshToken,
        result.email,
        result.expiresIn,
        result.username || result.displayName,
        result.role || "Free",
        result.is_locked || result.isLocked || false
      );

      const postLoginUrl = getPostLoginRedirectUrl();
      if (isTravelwAIFreeRole(result.role || "Free")) {
        sessionStorage.setItem("travelwaiOpenPricingAfterLogin", "1");
      }
      window.location.href = postLoginUrl;
    } else {
      window.TravelwAIToast(result.message || "Đăng nhập thất bại");
    }
  } catch (error) {
    console.error("Lỗi đăng nhập:", error);
    window.TravelwAIToast("Lỗi mạng. Vui lòng thử lại.");
  }
}

async function handleSignUp(event) {
  event.preventDefault();

  const username = document.getElementById("username").value.trim();
  const email = document.getElementById("email").value.trim();
  const password = document.getElementById("password").value;
  const signupParams = new URLSearchParams(window.location.search);
  const offerInviteInput = document.getElementById("offerInvite");
  const offerInvite = (offerInviteInput?.value || signupParams.get("offerInvite") || signupParams.get("inviteCode") || sessionStorage.getItem("offerInvite") || "").trim();
  const confirmPassword = document.getElementById("confirm-password").value;

  if (password.length < MINIMUM_PASSWORD_LENGTH) {
    window.TravelwAIToast(`Mật khẩu phải có ít nhất ${MINIMUM_PASSWORD_LENGTH} ký tự`);
    return;
  }

  if (password !== confirmPassword) {
    window.TravelwAIToast("Mật khẩu xác nhận không khớp");
    return;
  }

  try {
    const response = await fetch(`${AUTH_API_BASE}/signup`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, username, password, offerInvite }),
    });

    const result = await response.json();

    if (result.success) {
      sessionStorage.removeItem("offerInvite");
      clearLocalAuthData();
      window.location.replace("/login");
    } else {
      window.TravelwAIToast(result.message || "Đăng ký thất bại");
    }
  } catch (error) {
    console.error("Lỗi đăng ký:", error);
    window.TravelwAIToast("Lỗi mạng. Vui lòng thử lại.");
  }
}

function setAuthMessage(message, type = "info") {
  const messageEl = document.getElementById("authMessage");
  if (!messageEl) {
    if (type === "error") window.TravelwAIToast(message);
    return;
  }

  messageEl.hidden = false;
  messageEl.textContent = message;
  messageEl.className = `auth-message ${type}`;
}

function setAuthButtonLoading(button, isLoading, loadingText) {
  if (!button) return;
  if (isLoading) {
    button.dataset.originalText = button.textContent;
    button.textContent = loadingText;
    button.disabled = true;
  } else {
    button.textContent = button.dataset.originalText || button.textContent;
    button.disabled = false;
  }
}

async function handleForgotPassword(event) {
  event.preventDefault();

  const emailInput = document.getElementById("email");
  const email = emailInput?.value.trim() || "";
  const submitBtn = event.submitter || document.querySelector("#requestResetOtpForm .login-btn");

  if (!email) {
    setAuthMessage("Vui lòng nhập email.", "error");
    return;
  }

  try {
    setAuthButtonLoading(submitBtn, true, "Đang gửi OTP...");
    const response = await fetch(`${AUTH_API_BASE}/forgot-password`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email }),
    });

    const result = await response.json();
    if (!response.ok || !result.success) {
      setAuthMessage(result.message || "Không gửi được OTP. Vui lòng thử lại.", "error");
      return;
    }

    sessionStorage.setItem("passwordResetEmail", email);
    document.getElementById("verifyResetOtpForm")?.removeAttribute("hidden");
    document.getElementById("resetOtp")?.focus();

    const message = result.message || "Đã gửi mã OTP.";
    setAuthMessage(message, result.emailSent === false ? "warning" : "success");
  } catch (error) {
    console.error("Lỗi gửi OTP đổi mật khẩu:", error);
    setAuthMessage("Lỗi mạng. Vui lòng thử lại.", "error");
  } finally {
    setAuthButtonLoading(submitBtn, false);
  }
}

async function handleVerifyPasswordResetOtp(event) {
  event.preventDefault();

  const email = (sessionStorage.getItem("passwordResetEmail") || document.getElementById("email")?.value || "").trim();
  const otp = document.getElementById("resetOtp")?.value.trim() || "";
  const submitBtn = event.submitter || document.querySelector("#verifyResetOtpForm .login-btn");

  if (!email || !otp) {
    setAuthMessage("Vui lòng nhập đầy đủ email và mã OTP.", "error");
    return;
  }

  try {
    setAuthButtonLoading(submitBtn, true, "Đang xác nhận...");
    const response = await fetch(`${AUTH_API_BASE}/password-reset/verify-otp`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, otp }),
    });

    const result = await response.json();
    if (!response.ok || !result.success || !result.resetToken) {
      setAuthMessage(result.message || "OTP không đúng hoặc đã hết hạn.", "error");
      return;
    }

    sessionStorage.setItem("passwordResetEmail", email);
    sessionStorage.setItem("passwordResetToken", result.resetToken);
    setAuthMessage("Xác nhận OTP thành công.", "success");
    setTimeout(() => {
      window.location.href = "/reset-password";
    }, 650);
  } catch (error) {
    console.error("Lỗi xác nhận OTP:", error);
    setAuthMessage("Lỗi mạng. Vui lòng thử lại.", "error");
  } finally {
    setAuthButtonLoading(submitBtn, false);
  }
}

function initResetPasswordPage() {
  const email = sessionStorage.getItem("passwordResetEmail");
  const resetToken = sessionStorage.getItem("passwordResetToken");
  if (!email || !resetToken) {
    setAuthMessage("Bạn cần xác nhận OTP trước khi đổi mật khẩu.", "error");
    const form = document.getElementById("resetPasswordForm");
    if (form) form.style.display = "none";
    setTimeout(() => {
      window.location.href = "/forgot-password";
    }, 1200);
  }
}

async function handleResetPassword(event) {
  event.preventDefault();

  const email = sessionStorage.getItem("passwordResetEmail") || "";
  const resetToken = sessionStorage.getItem("passwordResetToken") || "";
  const password = document.getElementById("newPassword")?.value || "";
  const confirmPassword = document.getElementById("confirmNewPassword")?.value || "";
  const submitBtn = event.submitter || document.querySelector("#resetPasswordForm .login-btn");

  if (!email || !resetToken) {
    setAuthMessage("Phiên đổi mật khẩu đã hết hạn. Vui lòng xác nhận OTP lại.", "error");
    return;
  }

  if (password.length < MINIMUM_PASSWORD_LENGTH) {
    setAuthMessage(`Mật khẩu mới phải có ít nhất ${MINIMUM_PASSWORD_LENGTH} ký tự.`, "error");
    return;
  }

  if (password !== confirmPassword) {
    setAuthMessage("Mật khẩu xác nhận không khớp.", "error");
    return;
  }

  try {
    setAuthButtonLoading(submitBtn, true, "Đang đổi mật khẩu...");
    const response = await fetch(`${AUTH_API_BASE}/password-reset/confirm`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, resetToken, password }),
    });

    const result = await response.json();
    if (!response.ok || !result.success) {
      setAuthMessage(result.message || "Không đổi được mật khẩu. Vui lòng thử lại.", "error");
      return;
    }

    sessionStorage.removeItem("passwordResetEmail");
    sessionStorage.removeItem("passwordResetToken");
    clearLocalAuthData();
    setAuthMessage(result.message || "Đổi mật khẩu thành công.", "success");
    setTimeout(() => {
      window.location.href = "/login";
    }, 900);
  } catch (error) {
    console.error("Lỗi đổi mật khẩu:", error);
    setAuthMessage("Lỗi mạng. Vui lòng thử lại.", "error");
  } finally {
    setAuthButtonLoading(submitBtn, false);
  }
}

async function authenticatedFetch(url, options = {}) {
  const getToken = () => sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  let idToken = getToken();

  if (!idToken) {
    redirectToLogin(getCurrentReturnUrl());
    throw new Error("Phiên đăng nhập không còn hiệu lực.");
  }

  const requestTimeoutMs = Number(options.timeoutMs || 20000);
  const streamResponse = options.streamResponse === true;
  const buildOptions = (token, signal) => {
    const headers = {
      ...(options.headers || {}),
      Authorization: `Bearer ${token}`,
    };
    if (!(options.body instanceof FormData) && !headers["Content-Type"]) {
      headers["Content-Type"] = "application/json";
    }
    if (!headers.Accept) headers.Accept = "application/json";
    const { timeoutMs, streamResponse: _streamResponse, ...fetchOptions } = options;
    return {
      cache: "no-store",
      credentials: "same-origin",
      ...fetchOptions,
      headers,
      signal,
    };
  };

  const fetchWithTimeout = async (token) => {
    const controller = new AbortController();
    const externalSignal = options.signal;
    const abortFromExternal = () => controller.abort(externalSignal?.reason);
    if (externalSignal) {
      if (externalSignal.aborted) abortFromExternal();
      else externalSignal.addEventListener("abort", abortFromExternal, { once: true });
    }
    const timeoutId = window.setTimeout(() => controller.abort("timeout"), requestTimeoutMs);
    try {
      return await fetch(url, buildOptions(token, controller.signal));
    } catch (error) {
      if (controller.signal.aborted && !externalSignal?.aborted) {
        throw new Error("Máy chủ phản hồi quá lâu. Vui lòng tải lại trang.");
      }
      throw error;
    } finally {
      // Với response streaming, fetch() trả về ngay sau khi nhận header.
      // Phải giữ liên kết với signal bên ngoài cho tới lúc body stream kết thúc,
      // nếu tháo listener tại đây thì nút Dừng AI chỉ đổi giao diện nhưng không ngắt luồng.
      if (!streamResponse) {
        window.clearTimeout(timeoutId);
        externalSignal?.removeEventListener?.("abort", abortFromExternal);
      }
    }
  };

  try {
    let response = await fetchWithTimeout(idToken);
    if (response.status !== 401) return response;

    const refreshed = await refreshTokenIfNeeded();
    if (!refreshed) {
      redirectToLogin(getCurrentReturnUrl());
      throw new Error("Phiên đăng nhập đã hết hạn.");
    }

    idToken = getToken();
    if (!idToken) {
      redirectToLogin(getCurrentReturnUrl());
      throw new Error("Phiên đăng nhập đã hết hạn.");
    }
    return await fetchWithTimeout(idToken);
  } catch (error) {
    console.error("Lỗi gọi API xác thực:", error);
    throw error;
  }
}

const TRAVELWAI_IDLE_TIMEOUT_MS = 3 * 60 * 1000;
const TRAVELWAI_IDLE_CHECK_MS = 10 * 1000;
const TRAVELWAI_IDLE_STORAGE_KEY = "travelwaiLastActivityAt";
const TRAVELWAI_IDLE_EVENTS = ["click", "keydown", "mousemove", "mousedown", "scroll", "touchstart", "pointerdown"];

function updateIdleActivityStamp() {
  if (!hasActiveLoginSession()) return;
  localStorage.setItem(TRAVELWAI_IDLE_STORAGE_KEY, Date.now().toString());
}

function getIdleActivityStamp() {
  const value = parseInt(localStorage.getItem(TRAVELWAI_IDLE_STORAGE_KEY) || "0", 10);
  return Number.isNaN(value) ? 0 : value;
}

function hasActiveLoginSession() {
  return !!(
    localStorage.getItem("idToken") ||
    sessionStorage.getItem("idToken") ||
    readTravelwAICookie("TravelwAIAuth")
  );
}

async function logoutByIdleTimeout() {
  if (sessionStorage.getItem("travelwaiIdleLogoutRunning") === "1") return;
  sessionStorage.setItem("travelwaiIdleLogoutRunning", "1");
  clearAuthData();
  window.location.href = "/";
}

function checkIdleTimeout() {
  if (!hasActiveLoginSession()) return;

  let lastActivity = getIdleActivityStamp();
  if (!lastActivity) {
    updateIdleActivityStamp();
    lastActivity = getIdleActivityStamp();
  }

  if (Date.now() - lastActivity >= TRAVELWAI_IDLE_TIMEOUT_MS) {
    logoutByIdleTimeout();
  }
}

function initAutoIdleLogout() {
  if (!hasActiveLoginSession()) return;

  updateIdleActivityStamp();

  TRAVELWAI_IDLE_EVENTS.forEach((eventName) => {
    window.addEventListener(eventName, updateIdleActivityStamp, { passive: true });
  });

  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") {
      checkIdleTimeout();
      updateIdleActivityStamp();
    }
  });

  window.addEventListener("storage", (event) => {
    if (event.key === "idToken" && !event.newValue) {
      clearLocalAuthData();
      window.location.href = "/";
    }
  });

  setInterval(checkIdleTimeout, TRAVELWAI_IDLE_CHECK_MS);
}

const TRAVELWAI_PRESENCE_HEARTBEAT_MS = 45 * 1000;
const TRAVELWAI_PRESENCE_LEADER_TTL_MS = 75 * 1000;
const TRAVELWAI_PRESENCE_LEADER_KEY = "travelwaiPresenceLeader";
const TRAVELWAI_PRESENCE_TAB_KEY = "travelwaiPresenceTabId";
let travelwaiPresenceHeartbeatRunning = false;

function getPresenceTabId() {
  let id = sessionStorage.getItem(TRAVELWAI_PRESENCE_TAB_KEY);
  if (!id) {
    id = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    sessionStorage.setItem(TRAVELWAI_PRESENCE_TAB_KEY, id);
  }
  return id;
}

function claimPresenceLeader() {
  if (!hasActiveLoginSession()) return false;
  const tabId = getPresenceTabId();
  const now = Date.now();
  let current = null;
  try { current = JSON.parse(localStorage.getItem(TRAVELWAI_PRESENCE_LEADER_KEY) || "null"); } catch (_) {}

  if (!current || current.id === tabId || Number(current.expiresAt || 0) <= now) {
    const next = { id: tabId, expiresAt: now + TRAVELWAI_PRESENCE_LEADER_TTL_MS };
    try { localStorage.setItem(TRAVELWAI_PRESENCE_LEADER_KEY, JSON.stringify(next)); } catch (_) { return true; }
  }

  try {
    const verified = JSON.parse(localStorage.getItem(TRAVELWAI_PRESENCE_LEADER_KEY) || "null");
    return verified?.id === tabId;
  } catch (_) {
    return true;
  }
}

async function sendPresenceHeartbeat() {
  if (travelwaiPresenceHeartbeatRunning || !claimPresenceLeader()) return;
  const token = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  if (!token) return;

  travelwaiPresenceHeartbeatRunning = true;
  try {
    await fetch(`${AUTH_API_BASE}/presence/heartbeat`, {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: { Authorization: `Bearer ${token}` }
    });
  } catch (_) {
    // Heartbeat sẽ thử lại ở chu kỳ sau; không làm gián đoạn thao tác của người dùng.
  } finally {
    travelwaiPresenceHeartbeatRunning = false;
  }
}

function initAccountPresenceHeartbeat() {
  if (!hasActiveLoginSession()) return;
  sendPresenceHeartbeat();
  window.setInterval(sendPresenceHeartbeat, TRAVELWAI_PRESENCE_HEARTBEAT_MS);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") sendPresenceHeartbeat();
  });
  window.addEventListener("storage", (event) => {
    if (event.key === "travelwaiLogoutAt" && event.newValue) {
      clearLocalAuthData();
      window.location.href = "/";
    }
  });
}

function applyOfferInviteFromUrl() {
  const params = new URLSearchParams(window.location.search);
  const invite = params.get("offerInvite") || params.get("inviteCode") || "";
  const email = params.get("email") || "";

  if (invite) {
    sessionStorage.setItem("offerInvite", invite);
    const inviteInput = document.getElementById("offerInvite");
    if (inviteInput) inviteInput.value = invite;
  }
  if (email && document.getElementById("email")) {
    document.getElementById("email").value = email;
  }
}

document.addEventListener("DOMContentLoaded", applyOfferInviteFromUrl);
document.addEventListener("DOMContentLoaded", initAutoIdleLogout);
document.addEventListener("DOMContentLoaded", initAccountPresenceHeartbeat);

document.addEventListener("DOMContentLoaded", function () {
  const idToken = sessionStorage.getItem("idToken") || localStorage.getItem("idToken") || readTravelwAICookie("TravelwAIAuth");
  const refreshToken = sessionStorage.getItem("refreshToken") || localStorage.getItem("refreshToken") || readTravelwAICookie("TravelwAIRefresh");
  const expiration = parseInt(sessionStorage.getItem("tokenExpiration") || localStorage.getItem("tokenExpiration") || "0", 10);

  if (idToken && refreshToken && (!expiration || Date.now() >= expiration - 5 * 60 * 1000)) {
    refreshTokenIfNeeded({ redirectOnSuccess: true });
  }
});
