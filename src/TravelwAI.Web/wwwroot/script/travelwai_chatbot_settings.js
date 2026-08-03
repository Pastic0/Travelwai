(function () {
  const FALLBACK = {
    chatbotName: "WaiGo",
    selectedStyleId: "default",
    defaultStyleId: "default",
    role: "Free",
    canChangeStyle: false,
    hasAllStyles: false,
    styles: [
      { id: "default", name: "Mặc định", price: 0, isFree: true, owned: true, locked: false, canSelect: false, canPurchase: false },
      { id: "gentle", name: "Dịu dàng", price: 0, isFree: true, owned: true, locked: false, canSelect: false, canPurchase: false },
      { id: "formal", name: "Trang nghiêm", price: 0, isFree: true, owned: true, locked: false, canSelect: false, canPurchase: false }
    ]
  };

  let cached = null;
  let loadingPromise = null;
  let purchasePollTimer = null;
  let purchaseExpireTimer = null;
  let purchaseExpiryHandled = false;
  let currentPurchaseOrderId = "";
  let currentPurchaseExpiresAt = "";

  function readCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    return parts.length === 2 ? decodeURIComponent(parts.pop().split(";").shift() || "") : "";
  }

  function getToken() {
    return readCookie("TravelwAIAuth") || localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || "";
  }

  function requestHeaders(includeJson = false) {
    const headers = {};
    const token = getToken();
    if (token) headers.Authorization = `Bearer ${token}`;
    if (includeJson) headers["Content-Type"] = "application/json";
    return headers;
  }

  function request(url, options = {}) {
    const config = { ...options, credentials: "same-origin", cache: options.cache || "no-store" };
    config.headers = { ...requestHeaders(false), ...(options.headers || {}) };
    return fetch(url, config);
  }

  function normalize(data) {
    const styles = Array.isArray(data?.styles)
      ? data.styles.map(item => {
          const id = String(item?.id || "").trim();
          const name = String(item?.name || "").trim();
          const isFree = item?.isFree === true || item?.is_free === true || ["default", "gentle", "formal"].includes(id.toLowerCase());
          const owned = item?.owned === true || isFree;
          return {
            id,
            name,
            price: isFree ? 0 : Math.max(0, Number(item?.price || 0)),
            isFree,
            owned,
            locked: item?.locked === true || !owned,
            canSelect: item?.canSelect === true,
            canPurchase: item?.canPurchase === true
          };
        }).filter(item => item.id && item.name)
      : [];
    const chatbotName = String(data?.chatbotName || data?.chatbot_name || FALLBACK.chatbotName).trim() || FALLBACK.chatbotName;
    const defaultStyleId = String(data?.defaultStyleId || data?.default_style_id || styles[0]?.id || FALLBACK.defaultStyleId).trim();
    const selectedCandidate = String(data?.selectedStyleId || data?.selected_style_id || defaultStyleId).trim();
    const selectedStyleId = styles.some(item => item.id === selectedCandidate) ? selectedCandidate : (styles[0]?.id || FALLBACK.selectedStyleId);
    return {
      chatbotName,
      defaultStyleId,
      selectedStyleId,
      role: String(data?.role || FALLBACK.role),
      canChangeStyle: data?.canChangeStyle === true || data?.can_change_style === true,
      hasAllStyles: data?.hasAllStyles === true || data?.has_all_styles === true,
      styles: styles.length ? styles : FALLBACK.styles.slice()
    };
  }

  function applyName(settings) {
    document.querySelectorAll("[data-chatbot-name]").forEach(element => { element.textContent = settings.chatbotName; });
    document.querySelectorAll("[data-chatbot-name-alt]").forEach(element => { element.setAttribute("alt", settings.chatbotName); });
    document.querySelectorAll("[data-chatbot-name-title]").forEach(element => { element.setAttribute("title", settings.chatbotName); });
  }

  function applyPermissions(settings) {
    document.querySelectorAll(".waigo-style-picker-button").forEach(button => {
      button.disabled = !settings.canChangeStyle;
      button.classList.toggle("is-locked", !settings.canChangeStyle);
      button.title = settings.canChangeStyle ? "Chọn phong cách nói chuyện" : "Đăng nhập để chọn phong cách";
      button.setAttribute("aria-label", button.title);
    });
  }

  function emit(settings) {
    applyName(settings);
    applyPermissions(settings);
    window.dispatchEvent(new CustomEvent("travelwai:chatbot-settings-changed", { detail: settings }));
  }

  async function load(force = false) {
    if (cached && !force) return cached;
    if (loadingPromise && !force) return loadingPromise;
    loadingPromise = (async () => {
      try {
        let response = await request("/api/chatbot/settings", { headers: requestHeaders(true) });
        let result = await response.json().catch(() => ({}));
        if (response.status === 401 || response.status === 403) {
          response = await fetch("/api/chatbot/public-settings", {
            credentials: "same-origin",
            cache: "no-store",
            headers: { "Content-Type": "application/json" }
          });
          result = await response.json().catch(() => ({}));
        }
        if (!response.ok || result?.success === false) throw new Error(result?.message || "Không tải được cấu hình chatbot.");
        cached = normalize(result?.data || result);
      } catch (_) {
        cached = normalize(cached || FALLBACK);
      }
      emit(cached);
      return cached;
    })();
    try { return await loadingPromise; } finally { loadingPromise = null; }
  }

  async function selectStyle(styleId) {
    const cleanId = String(styleId || "").trim();
    if (!cleanId) throw new Error("Vui lòng chọn phong cách.");
    const response = await request("/api/chatbot/style", {
      method: "PUT",
      headers: requestHeaders(true),
      body: JSON.stringify({ styleId: cleanId })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || result?.success === false) throw new Error(result?.message || "Không đổi được phong cách.");
    cached = normalize(result?.data || { ...(await load()), selectedStyleId: cleanId });
    emit(cached);
    return { settings: cached, message: result?.message || "Đã đổi phong cách." };
  }

  function renderMenu(menu, settings, onSelect) {
    if (!menu) return;
    menu.innerHTML = "";
    settings.styles.forEach(style => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "waigo-style-option";
      button.setAttribute("data-no-translate", "");
      button.dataset.styleId = style.id;
      button.setAttribute("role", "menuitemradio");
      button.setAttribute("aria-checked", style.id === settings.selectedStyleId ? "true" : "false");
      button.classList.toggle("is-selected", style.id === settings.selectedStyleId);
      button.classList.toggle("is-locked", !style.canSelect);
      button.disabled = !style.canSelect;
      button.innerHTML = `<span class="waigo-style-option-check" aria-hidden="true">${style.id === settings.selectedStyleId ? "✓" : (style.locked ? "🔒" : "")}</span><span>${escapeHtml(style.name)}</span>`;
      button.addEventListener("click", async event => {
        event.stopPropagation();
        if (style.id === settings.selectedStyleId) { menu.hidden = true; return; }
        button.disabled = true;
        try {
          const result = await selectStyle(style.id);
          onSelect?.(result.settings, result.message);
          renderMenu(menu, result.settings, onSelect);
          menu.hidden = true;
        } catch (error) {
          toast(error.message || "Không đổi được phong cách.", "error");
        }
      });
      menu.appendChild(button);
    });
  }

  function bindPicker(button, menu, onSelect) {
    if (!button || !menu || button.dataset.chatbotStyleBound === "true") return;
    button.dataset.chatbotStyleBound = "true";
    button.addEventListener("click", async event => {
      event.preventDefault();
      event.stopPropagation();
      const settings = await load();
      if (!settings.canChangeStyle) return toast("Bạn cần đăng nhập để chọn phong cách nói chuyện.", "error");
      renderMenu(menu, settings, onSelect);
      menu.hidden = !menu.hidden;
      button.setAttribute("aria-expanded", menu.hidden ? "false" : "true");
    });
    document.addEventListener("click", event => {
      if (menu.hidden || menu.contains(event.target) || button.contains(event.target)) return;
      menu.hidden = true;
      button.setAttribute("aria-expanded", "false");
    });
    document.addEventListener("keydown", event => {
      if (event.key !== "Escape") return;
      menu.hidden = true;
      button.setAttribute("aria-expanded", "false");
    });
  }

  function ensureStoreModal() {
    let modal = document.getElementById("waigoStyleStoreModal");
    if (modal) return modal;
    modal = document.createElement("div");
    modal.id = "waigoStyleStoreModal";
    modal.className = "waigo-style-store-modal";
    modal.hidden = true;
    modal.innerHTML = `<div class="waigo-style-store-card" role="dialog" aria-modal="true" aria-label="Cửa hàng phong cách">
      <div class="waigo-style-store-head"><strong>Cửa hàng phong cách</strong><button type="button" data-style-store-close aria-label="Đóng">×</button></div>
      <div class="waigo-style-store-list" data-style-store-list></div>
      <div class="waigo-style-payment" data-style-payment hidden></div>
    </div>`;
    document.body.appendChild(modal);
    modal.addEventListener("click", event => { if (event.target === modal || event.target.closest("[data-style-store-close]")) closeStore(); });
    return modal;
  }

  async function openStore() {
    const modal = ensureStoreModal();
    modal.hidden = false;
    document.body.classList.add("waigo-style-store-open");
    const settings = await load(true);
    renderStore(settings);
  }

  function clearPurchaseTimers() {
    clearInterval(purchasePollTimer);
    clearInterval(purchaseExpireTimer);
    purchasePollTimer = null;
    purchaseExpireTimer = null;
  }

  function closeStore() {
    const modal = document.getElementById("waigoStyleStoreModal");
    if (modal) modal.hidden = true;
    document.body.classList.remove("waigo-style-store-open");
    clearPurchaseTimers();
    currentPurchaseOrderId = "";
    currentPurchaseExpiresAt = "";
  }

  function normalizePaymentExpiry(value, unixMs = 0) {
    const numeric = Number(unixMs || 0);
    if (Number.isFinite(numeric) && numeric > 0) return new Date(numeric).toISOString();
    const parsed = new Date(value || "");
    return Number.isNaN(parsed.getTime()) ? "" : parsed.toISOString();
  }

  function formatPaymentCountdown(value) {
    const expires = new Date(value);
    if (Number.isNaN(expires.getTime())) return "05:00";
    const totalSeconds = Math.max(0, Math.ceil((expires.getTime() - Date.now()) / 1000));
    const minutes = String(Math.floor(totalSeconds / 60)).padStart(2, "0");
    const seconds = String(totalSeconds % 60).padStart(2, "0");
    return `${minutes}:${seconds}`;
  }

  function isCurrentStylePaymentExpired() {
    if (!currentPurchaseExpiresAt) return false;
    const expires = new Date(currentPurchaseExpiresAt);
    return !Number.isNaN(expires.getTime()) && expires.getTime() <= Date.now();
  }

  async function expireStylePaymentLocally(message = "Mã đã hết hạn. Hãy tạo mã mới.") {
    if (purchaseExpiryHandled) return;
    purchaseExpiryHandled = true;

    const expiredOrderId = currentPurchaseOrderId;
    clearPurchaseTimers();
    currentPurchaseOrderId = "";
    currentPurchaseExpiresAt = "";
    cached = null;

    try {
      renderStore(await load(true));
    } catch (_) {
      const modal = ensureStoreModal();
      const list = modal.querySelector("[data-style-store-list]");
      const payment = modal.querySelector("[data-style-payment]");
      if (payment) payment.hidden = true;
      if (list) list.hidden = false;
    }

    toast(message, "error");

    if (expiredOrderId) {
      request(`/api/chatbot/style-orders/${encodeURIComponent(expiredOrderId)}`).catch(() => {});
    }
  }

  function startStylePaymentExpiry(orderId, expiresAt) {
    clearInterval(purchaseExpireTimer);
    currentPurchaseOrderId = String(orderId || "");
    currentPurchaseExpiresAt = String(expiresAt || "");
    purchaseExpiryHandled = false;
    if (!currentPurchaseExpiresAt) return;

    const update = () => {
      const timer = document.querySelector("[data-style-payment-timer]");
      if (timer) timer.textContent = formatPaymentCountdown(currentPurchaseExpiresAt);
      if (isCurrentStylePaymentExpired()) {
        clearInterval(purchaseExpireTimer);
        expireStylePaymentLocally().catch(() => {});
      }
    };

    update();
    purchaseExpireTimer = setInterval(update, 1000);
  }

  function completeStylePayment(message) {
    clearPurchaseTimers();
    cached = null;
    toast(message || "Thanh toán thành công. Phong cách đã mở khóa.", "success");
    window.setTimeout(() => {
      window.location.assign("/messaging?ai=1&styleStore=1&payment=style-success");
    }, 700);
  }

  function renderStore(settings) {
    const modal = ensureStoreModal();
    const list = modal.querySelector("[data-style-store-list]");
    const payment = modal.querySelector("[data-style-payment]");
    payment.hidden = true;
    list.hidden = false;
    list.innerHTML = settings.styles.map(style => {
      let action;
      if (style.owned) action = `<span class="waigo-style-access-state">Đã mở</span>`;
      else if (style.canPurchase) action = `<button type="button" data-buy-style="${escapeHtml(style.id)}">Mua</button>`;
      else action = `<button type="button" disabled>Cần VIP</button>`;
      return `<article class="waigo-style-store-item ${style.owned ? "is-owned" : "is-locked"}">
        <div><strong>${escapeHtml(style.name)}</strong><span>${style.isFree ? "Miễn phí" : money(style.price)}</span></div>${action}
      </article>`;
    }).join("");
    list.querySelectorAll("[data-buy-style]").forEach(button => button.addEventListener("click", () => purchaseStyle(button.dataset.buyStyle, button)));
  }

  async function purchaseStyle(styleId, button) {
    button.disabled = true;
    try {
      const response = await request(`/api/chatbot/styles/${encodeURIComponent(styleId)}/purchase`, {
        method: "POST",
        headers: requestHeaders(true)
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || result?.success === false) throw new Error(result?.message || "Không tạo được đơn mua.");
      if (result?.purchased === true) {
        completeStylePayment(result.message || "Đã mở khóa phong cách.");
        return;
      }
      renderPayment(result?.data || {});
    } catch (error) {
      toast(error.message || "Không tạo được đơn mua.", "error");
    } finally {
      button.disabled = false;
    }
  }

  function renderPayment(data) {
    const modal = ensureStoreModal();
    const list = modal.querySelector("[data-style-store-list]");
    const payment = modal.querySelector("[data-style-payment]");
    list.hidden = true;
    payment.hidden = false;
    payment.innerHTML = `<button class="waigo-style-payment-back" type="button" data-style-payment-back>←</button>
      <strong>${escapeHtml(data.styleName || "Phong cách")}</strong>
      ${data.paymentQrUrl ? `<img src="${escapeHtml(data.paymentQrUrl)}" alt="QR thanh toán" />` : ""}
      <div><span>${money(data.amount || 0)}</span><span>${escapeHtml(data.paymentContent || "")}</span></div>
      <small>Hết hạn sau <b data-style-payment-timer>${formatPaymentCountdown(normalizePaymentExpiry(data.expiresAt || "", data.expiresAtUnixMs || 0))}</b></small>`;
    payment.querySelector("[data-style-payment-back]")?.addEventListener("click", async () => {
      clearPurchaseTimers();
      currentPurchaseOrderId = "";
      currentPurchaseExpiresAt = "";
      renderStore(await load(true));
    });
    const check = () => checkStyleOrder(data.orderId);
    clearInterval(purchasePollTimer);
    startStylePaymentExpiry(data.orderId, normalizePaymentExpiry(data.expiresAt, data.expiresAtUnixMs));
    check();
    purchasePollTimer = setInterval(check, 2500);
  }

  async function checkStyleOrder(orderId) {
    if (!orderId) return;
    try {
      const response = await request(`/api/chatbot/style-orders/${encodeURIComponent(orderId)}`, {
        method: "GET"
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || result?.success === false) throw new Error(result?.message || "Không thể kiểm tra giao dịch.");
      const serverExpiry = normalizePaymentExpiry(result?.data?.expiresAt, result?.data?.expiresAtUnixMs);
      if (serverExpiry) {
        currentPurchaseExpiresAt = serverExpiry;
        const timer = document.querySelector("[data-style-payment-timer]");
        if (timer) timer.textContent = formatPaymentCountdown(serverExpiry);
      }
      if (result?.data?.purchased) {
        completeStylePayment(result?.message || "Thanh toán thành công. Phong cách đã mở khóa.");
      } else if (result?.data?.expired && isCurrentStylePaymentExpired()) {
        await expireStylePaymentLocally(result?.message || "Mã đã hết hạn. Hãy tạo mã mới.");
      }
    } catch (_) {
      if (isCurrentStylePaymentExpired()) await expireStylePaymentLocally();
    }
  }

  function bindStore(button) {
    if (!button || button.dataset.styleStoreBound === "true") return;
    button.dataset.styleStoreBound = "true";
    button.addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); openStore().catch(error => toast(error?.message || "Không mở được cửa hàng phong cách.", "error")); });
  }

  function money(value) {
    return `${Math.round(Number(value || 0)).toLocaleString("vi-VN")}đ`;
  }

  function toast(message, type) {
    if (typeof window.TravelwAIToast === "function") window.TravelwAIToast(message, type || "info");
  }

  function escapeHtml(value) {
    return String(value || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function refreshFromAdminUpdate() {
    cached = null;
    load(true).catch(() => {});
  }

  window.addEventListener("travelwai:chatbot-admin-settings-updated", refreshFromAdminUpdate);
  window.addEventListener("storage", event => {
    if (event.key === "travelwai-chatbot-settings-updated-at") refreshFromAdminUpdate();
  });

  window.TravelwAIChatbotSettings = { load, selectStyle, bindPicker, bindStore, renderMenu, openStore, get: () => cached || normalize(FALLBACK) };

  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("[data-waigo-style-store-button]").forEach(bindStore);
    load().catch(() => {});
  });
})();
