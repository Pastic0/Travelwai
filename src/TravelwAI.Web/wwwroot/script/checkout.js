let checkoutMode = "";
let checkoutCartId = "";
let checkoutPlan = "";
let checkoutData = null;
let checkoutPlanEligibility = null;
let checkoutPlanOrderId = "";
let checkoutPlanOrderExpiresAt = "";
let checkoutPlanPayment = null;
let checkoutExpireTimer = null;
let checkoutPlanCountdownTimer = null;
let checkoutPaymentPollTimer = null;
let checkoutPaymentSuccessShown = false;
let checkoutPaymentExpiryHandled = false;

const PLAN_BANK_CODE = "BIDV";
const PLAN_ACCOUNT_NUMBER = "96247Q4W8E";
const PLAN_ACCOUNT_NAME = "TravelwAI";

function money(value) {
  return Number(value || 0).toLocaleString("vi-VN") + "đ";
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>\"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;" }[char]));
}

function getValue(item, ...keys) {
  for (const key of keys) {
    if (item && item[key] !== undefined && item[key] !== null && item[key] !== "") return item[key];
  }
  return "";
}

function normalizeExpiryValue(value, unixMs = 0) {
  const numeric = Number(unixMs || 0);
  if (Number.isFinite(numeric) && numeric > 0) return new Date(numeric).toISOString();
  const parsed = new Date(value || "");
  return Number.isNaN(parsed.getTime()) ? "" : parsed.toISOString();
}

function syncPlanExpiryFromResult(result) {
  const normalized = normalizeExpiryValue(
    getValue(result, "expiresAt", "expires_at"),
    getValue(result, "expiresAtUnixMs", "expires_at_unix_ms")
  );
  if (!normalized) return;
  checkoutPlanOrderExpiresAt = normalized;
  if (checkoutPlanPayment) checkoutPlanPayment.expiresAt = normalized;
  const timer = document.getElementById("planPaymentTimer");
  if (timer) timer.textContent = formatCountdown(normalized);
}

function showToast(message, type = "info") {
  return window.TravelwAIToast(message, type);
}

async function readJson(response) {
  if (!response) throw new Error("Không có phản hồi từ máy chủ");
  const data = await response.json().catch(() => ({}));
  if (!response.ok || data.success === false) {
    const error = new Error(data.message || data.detail || "Không thực hiện được");
    error.data = data;
    throw error;
  }
  return data;
}

function setStatus(message, type = "") {
  const el = document.getElementById("checkoutStatus");
  if (!el) return;
  el.textContent = message || "";
  el.className = `checkout-status ${type}`.trim();
}

function setPayDisabled(disabled) {
  const button = document.getElementById("checkoutPayButton");
  if (button) button.disabled = !!disabled;
}

function resetPlanPaymentState() {
  checkoutPlanOrderId = "";
  checkoutPlanOrderExpiresAt = "";
  checkoutPlanPayment = null;
  renderPaymentDetails("");
}

function isPlanPaymentExpiredLocally() {
  if (!checkoutPlanOrderExpiresAt) return false;
  const expires = new Date(checkoutPlanOrderExpiresAt);
  return !Number.isNaN(expires.getTime()) && expires.getTime() <= Date.now();
}

async function expirePlanPaymentLocally(message = "Mã đã hết hạn. Hãy tạo mã mới.") {
  if (checkoutPaymentExpiryHandled) return;
  checkoutPaymentExpiryHandled = true;

  const expiredOrderId = checkoutPlanOrderId;
  stopPlanPaymentPolling();
  clearInterval(checkoutExpireTimer);
  resetPlanPaymentState();

  if (checkoutPlanEligibility) {
    renderPlanCheckout(checkoutPlanEligibility);
    const canBuy = checkoutPlanEligibility.can_buy || checkoutPlanEligibility.canBuy;
    setPayDisabled(!canBuy);
  } else {
    document.querySelector(".checkout-bank-card")?.remove();
    setPayDisabled(false);
  }

  setStatus(message, "error");

  if (expiredOrderId) {
    authenticatedFetch(`/api/commerce/plan-orders/${encodeURIComponent(expiredOrderId)}/status`, {
      cache: "no-store"
    }).catch(() => {});
  }

  try {
    await loadPlanCheckout(checkoutPlan);
  } catch (_) {
    // QR đã được ẩn ở phía trình duyệt; lần tải tiếp theo sẽ đồng bộ lại trạng thái.
  }
}

async function syncPlanRoleAfterPayment(role) {
  const normalizedRole = String(role || "").trim();
  if (normalizedRole) {
    localStorage.setItem("userRole", normalizedRole);
    sessionStorage.setItem("userRole", normalizedRole);
  }
}

function completePlanPayment(message) {
  stopPlanPaymentPolling();
  clearInterval(checkoutExpireTimer);
  resetPlanPaymentState();
  setPayDisabled(true);
  setStatus(message, "success");

  if (!checkoutPaymentSuccessShown) {
    checkoutPaymentSuccessShown = true;
    showToast(message, "success");
  }

  window.setTimeout(() => {
    window.location.assign("/profile?payment=plan-success");
  }, 700);
}

function planMonthlyPrice(role, source) {
  const rawConfigured = getValue(source, "monthlyPriceAmount", "monthly_price_amount");
  if (rawConfigured !== "") {
    const configured = Number(rawConfigured);
    if (Number.isFinite(configured) && configured >= 0) return configured;
  }
  return 0;
}

function calculatePlanPrice(role, months, source) {
  const safeMonths = Math.min(12, Math.max(1, Number(months || 1)));
  const monthly = planMonthlyPrice(role, source);
  const original = monthly * safeMonths;
  const configuredDiscount = Number(getValue(source, "yearDiscountPercent", "year_discount_percent") || 10);
  const discountPercent = safeMonths >= 12 ? Math.max(0, configuredDiscount) : 0;
  const discountAmount = Math.round(original * discountPercent / 100);
  const total = Math.max(0, original - discountAmount);
  return { months: safeMonths, monthly, original, discountPercent, discountAmount, total };
}

function buildQrUrl(amount, paymentContent) {
  const info = encodeURIComponent(String(paymentContent || "TWAI").trim());
  const accountName = encodeURIComponent(PLAN_ACCOUNT_NAME);
  return `https://img.vietqr.io/image/${PLAN_BANK_CODE}-${PLAN_ACCOUNT_NUMBER}-compact2.png?amount=${Math.round(Number(amount || 0))}&addInfo=${info}&accountName=${accountName}`;
}

function formatCountdown(expiresAt) {
  const expires = new Date(expiresAt);
  if (Number.isNaN(expires.getTime())) return "05:00";
  const ms = Math.max(0, expires.getTime() - Date.now());
  const totalSeconds = Math.ceil(ms / 1000);
  const minutes = String(Math.floor(totalSeconds / 60)).padStart(2, "0");
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${minutes}:${seconds}`;
}

function formatLongCountdown(expiresAt) {
  const expires = new Date(expiresAt);
  if (Number.isNaN(expires.getTime())) return "0 ngày 00:00:00";
  const ms = Math.max(0, expires.getTime() - Date.now());
  const totalSeconds = Math.floor(ms / 1000);
  const days = Math.floor(totalSeconds / 86400);
  const hours = String(Math.floor((totalSeconds % 86400) / 3600)).padStart(2, "0");
  const minutes = String(Math.floor((totalSeconds % 3600) / 60)).padStart(2, "0");
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${days} ngày ${hours}:${minutes}:${seconds}`;
}

function formatDateTime(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value).split("T")[0] || "";
  return date.toLocaleString("vi-VN", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

function updatePlanCountdowns() {
  document.querySelectorAll("[data-current-plan-countdown]").forEach(el => {
    el.textContent = formatLongCountdown(el.dataset.currentPlanCountdown || "");
  });
}

function startPlanCountdowns() {
  clearInterval(checkoutPlanCountdownTimer);
  updatePlanCountdowns();
  checkoutPlanCountdownTimer = setInterval(updatePlanCountdowns, 1000);
}

function renderSummary(html) {
  const box = document.getElementById("checkoutSummary");
  if (box) box.innerHTML = html;
}

function renderPaymentDetails(html) {
  const box = document.getElementById("checkoutPaymentDetails");
  if (!box) return;
  box.innerHTML = html || "";
  box.hidden = !html;
}

function startExpireTimer() {
  clearInterval(checkoutExpireTimer);
  if (!checkoutPlanOrderExpiresAt) return;

  const update = () => {
    const timer = document.getElementById("planPaymentTimer");
    if (timer) timer.textContent = formatCountdown(checkoutPlanOrderExpiresAt);
    if (isPlanPaymentExpiredLocally()) {
      clearInterval(checkoutExpireTimer);
      expirePlanPaymentLocally().catch(() => {});
    }
  };

  update();
  checkoutExpireTimer = setInterval(update, 1000);
}

async function loadCheckout() {
  const params = new URLSearchParams(window.location.search);
  checkoutCartId = params.get("cartId") || "";
  checkoutPlan = params.get("plan") || params.get("role") || "";
  if (checkoutCartId) {
    checkoutMode = "cart";
    await loadCartCheckout(checkoutCartId);
    return;
  }
  if (checkoutPlan) {
    checkoutMode = "plan";
    await loadPlanCheckout(checkoutPlan);
    return;
  }
  renderSummary('<div class="empty-line">Chưa có sản phẩm thanh toán.</div>');
  renderPaymentDetails("");
  setPayDisabled(true);
}

async function loadCartCheckout(id) {
  renderPaymentDetails("");
  try {
    const response = await authenticatedFetch(`/api/commerce/cart/${encodeURIComponent(id)}`);
    const result = await readJson(response);
    checkoutData = result.data || {};
    const name = getValue(checkoutData, "tour_name", "tourName") || "Tour du lịch";
    const quantity = Number(getValue(checkoutData, "quantity") || 1);
    const total = Number(getValue(checkoutData, "total_price", "totalPrice") || 0);
    const status = getValue(checkoutData, "status") || "Trong giỏ";
    const expired = String(status).toLowerCase() === "hết hạn";
    renderSummary(`<div class="checkout-product-card">
      <span class="eyebrow">Tour du lịch</span>
      <h3>${escapeHtml(name)}</h3>
      ${getValue(checkoutData, "tour_duration") ? `<div class="checkout-line"><span>Thời gian</span><strong>${escapeHtml(getValue(checkoutData, "tour_duration"))}</strong></div>` : ""}
      <div class="checkout-line"><span>Số lượng</span><strong>${quantity}</strong></div>
      <div class="checkout-line"><span>Tạm tính</span><strong>${money(total)}</strong></div>
      <div class="checkout-line"><span>Người mua</span><strong>${escapeHtml(getValue(checkoutData, "buyer_name", "customer_name") || "")}</strong></div>
      ${expired ? `<div class="checkout-line"><span>Trạng thái</span><strong>Hết hạn</strong></div>` : ""}
    </div>`);
    setStatus(expired ? "Tour đã bán hết. Đơn trong giỏ đã hết hạn." : "", expired ? "error" : "");
    setPayDisabled(expired);
  } catch (error) {
    renderSummary(`<div class="empty-line">${escapeHtml(error.message)}</div>`);
    renderPaymentDetails("");
    setPayDisabled(true);
  }
}

async function loadPlanCheckout(plan) {
  const role = String(plan || "").trim();
  checkoutPlan = role;
  try {
    const response = await authenticatedFetch(`/api/commerce/plan-eligibility?plan=${encodeURIComponent(role)}`, { cache: "no-store" });
    const result = await readJson(response);
    checkoutPlanEligibility = result;
    const pendingOrderId = getValue(result, "pendingOrderId", "pending_order_id");
    if (pendingOrderId) {
      const pendingRole = getValue(result, "pendingPlanRole", "pending_plan_role") || role;
      checkoutPlan = pendingRole;
      checkoutPlanOrderId = pendingOrderId;
      checkoutPlanOrderExpiresAt = normalizeExpiryValue(
        getValue(result, "pendingOrderExpiresAt", "pending_order_expires_at"),
        getValue(result, "pendingOrderExpiresAtUnixMs", "pending_order_expires_at_unix_ms")
      );
      checkoutPaymentExpiryHandled = false;
      checkoutPlanPayment = {
        orderId: pendingOrderId,
        expiresAt: checkoutPlanOrderExpiresAt,
        durationMonths: Number(getValue(result, "pendingDurationMonths", "pending_duration_months") || 1),
        paymentBank: getValue(result, "pendingPaymentBank", "pending_payment_bank"),
        paymentAccount: getValue(result, "pendingPaymentAccount", "pending_payment_account"),
        paymentAccountName: getValue(result, "pendingPaymentAccountName", "pending_payment_account_name"),
        paymentContent: getValue(result, "pendingPaymentContent", "pending_payment_content"),
        paymentQrUrl: getValue(result, "pendingPaymentQrUrl", "pending_payment_qr_url"),
        monthly: Number(getValue(result, "pendingMonthlyPrice", "pending_monthly_price") || 0),
        original: Number(getValue(result, "pendingOriginalAmount", "pending_original_amount") || 0),
        discountPercent: Number(getValue(result, "pendingDiscountPercent", "pending_discount_percent") || 0),
        discountAmount: Number(getValue(result, "pendingDiscountAmount", "pending_discount_amount") || 0),
        amount: Number(getValue(result, "pendingAmount", "pending_amount") || 0)
      };
      renderPlanCheckout({ ...result, planRole: pendingRole, plan_role: pendingRole });
      setPayDisabled(true);
      setStatus("");
      startPlanPaymentPolling();
      return;
    }

    resetPlanPaymentState();
    checkoutPaymentExpiryHandled = false;
    renderPlanCheckout(result);
    const canBuy = result.can_buy || result.canBuy;
    setStatus(result.message || "", canBuy ? "info" : "error");
    setPayDisabled(!canBuy);
  } catch (error) {
    renderSummary(`<div class="empty-line">${escapeHtml(error.message)}</div>`);
    renderPaymentDetails("");
    setPayDisabled(true);
  }
}

function renderPlanPaymentBox(price, payment) {
  const orderId = getValue(payment, "orderId", "order_id") || checkoutPlanOrderId || checkoutPlan;
  // Always render the current receiving account. Pending orders created before the
  // account change may still contain the old destination in their saved metadata.
  const bank = PLAN_BANK_CODE;
  const account = PLAN_ACCOUNT_NUMBER;
  const accountName = PLAN_ACCOUNT_NAME;
  const content = getValue(payment, "paymentContent", "payment_content", "paymentCode", "payment_code") || `TWAI${String(orderId).replace(/[^a-z0-9]/gi, "").slice(0, 20).toUpperCase()}`;
  const qr = buildQrUrl(price.total, content);
  const expiresAt = getValue(payment, "expiresAt", "expires_at") || checkoutPlanOrderExpiresAt;
  return `<div class="checkout-bank-card">
    <div class="checkout-bank-info">
      <div class="checkout-line"><span>Ngân hàng</span><strong>${escapeHtml(bank)}</strong></div>
      <div class="checkout-line"><span>Số tài khoản</span><strong>${escapeHtml(account)}</strong></div>
      <div class="checkout-line"><span>Chủ tài khoản</span><strong>${escapeHtml(accountName)}</strong></div>
      <div class="checkout-line"><span>Số tiền</span><strong>${money(price.total)}</strong></div>
      <div class="checkout-line"><span>Nội dung</span><strong>${escapeHtml(content)}</strong></div>
      ${expiresAt ? `<div class="checkout-line"><span>Hết hạn</span><strong id="planPaymentTimer">${formatCountdown(expiresAt)}</strong></div>` : ""}
    </div>
    <div class="checkout-qr-wrap"><img src="${escapeHtml(qr)}" alt="QR thanh toán tự động" /></div>
  </div>`;
}

function renderCurrentPlanBox(result) {
  const currentRole = result.currentRole || result.current_role || "Free";
  const currentExpires = result.currentPlanExpiresAt || result.current_plan_expires_at || "";
  const nextRole = result.nextPlanRole || result.next_plan_role || "";
  const nextStart = result.nextPlanStartedAt || result.next_plan_started_at || "";
  let html = `<div class="checkout-line"><span>Gói hiện tại</span><strong>${escapeHtml(currentRole)}</strong></div>`;
  if (currentExpires) {
    html += `<div class="checkout-line"><span>Thời gian còn lại</span><strong data-current-plan-countdown="${escapeHtml(currentExpires)}">${formatLongCountdown(currentExpires)}</strong></div>`;
    html += `<div class="checkout-line"><span>Hết hạn</span><strong>${formatDateTime(currentExpires)}</strong></div>`;
  }
  if (nextRole && nextStart) {
    html += `<div class="checkout-line"><span>Gói tiếp theo</span><strong>${escapeHtml(nextRole)} · ${formatDateTime(nextStart)}</strong></div>`;
  }
  return html;
}

function renderPlanCheckout(result) {
  const role = result.planRole || result.plan_role || checkoutPlan;
  const currentSelect = document.getElementById("planMonthsSelect")?.value;
  const locked = !!checkoutPlanOrderId;
  const lockedMonths = Number(getValue(checkoutPlanPayment, "durationMonths", "duration_months") || 1);
  const selected = locked ? lockedMonths : Number(currentSelect || 1);
  const price = calculatePlanPrice(role, selected, result);
  if (locked && checkoutPlanPayment) {
    const monthly = Number(getValue(checkoutPlanPayment, "monthly") || 0);
    const original = Number(getValue(checkoutPlanPayment, "original") || 0);
    const discountPercent = Number(getValue(checkoutPlanPayment, "discountPercent", "discount_percent") || 0);
    const discountAmount = Number(getValue(checkoutPlanPayment, "discountAmount", "discount_amount") || 0);
    const rawAmount = getValue(checkoutPlanPayment, "amount");
    const amount = Number(rawAmount);
    if (monthly > 0) price.monthly = monthly;
    if (original > 0) price.original = original;
    price.discountPercent = discountPercent;
    price.discountAmount = discountAmount;
    if (rawAmount !== "" && Number.isFinite(amount) && amount >= 0) price.total = amount;
  }
  renderSummary(`<div class="checkout-product-card">
    <span class="eyebrow">Gói tài khoản</span>
    <h3>${escapeHtml(role)}</h3>
    ${renderCurrentPlanBox(result)}
    <label class="checkout-plan-select-label" for="planMonthsSelect">Thời hạn gói</label>
    <select class="checkout-plan-select" id="planMonthsSelect" ${locked ? "disabled" : ""}>
      ${Array.from({ length: 12 }, (_, index) => index + 1).map(month => `<option value="${month}" ${month === price.months ? "selected" : ""}>${month} tháng</option>`).join("")}
    </select>
    <div class="checkout-line"><span>Đơn giá</span><strong>${money(price.monthly)} / tháng</strong></div>
    <div class="checkout-line"><span>Tạm tính</span><strong>${money(price.original)}</strong></div>
    <div class="checkout-line"><span>Giảm giá</span><strong>${price.discountPercent ? `${price.discountPercent}% (-${money(price.discountAmount)})` : "0%"}</strong></div>
    <div class="checkout-line"><span>Tổng tiền</span><strong>${money(price.total)}</strong></div>
  </div>`);
  renderPaymentDetails(locked ? renderPlanPaymentBox(price, checkoutPlanPayment) : "");
  document.getElementById("planMonthsSelect")?.addEventListener("change", () => renderPlanCheckout(result));
  startPlanCountdowns();
  if (checkoutPlanOrderExpiresAt) startExpireTimer();
}

function getSelectedPlanMonths() {
  return Math.min(12, Math.max(1, Number(document.getElementById("planMonthsSelect")?.value || 1)));
}

async function payCheckout() {
  setPayDisabled(true);
  setStatus("Đang thanh toán...", "info");
  try {
    let response;
    if (checkoutMode === "cart") {
      response = await authenticatedFetch(`/api/commerce/checkout/cart/${encodeURIComponent(checkoutCartId)}/pay`, { method: "POST" });
      const result = await readJson(response);
      setStatus(result.message || "Thanh toán thành công.", "success");
      showToast(result.message || "Thanh toán thành công.");
      return;
    }
    if (checkoutMode === "plan") {
      if (checkoutPlanOrderId) {
        setStatus("");
        setPayDisabled(true);
        startPlanPaymentPolling();
        return;
      }
      response = await authenticatedFetch("/api/commerce/plan-orders", { method: "POST", body: JSON.stringify({ planRole: checkoutPlan, months: getSelectedPlanMonths() }) });
      const result = await readJson(response);
      checkoutPlanOrderId = result.orderId || result.order_id || "";
      checkoutPlanOrderExpiresAt = normalizeExpiryValue(
        result.expiresAt || result.expires_at || "",
        result.expiresAtUnixMs || result.expires_at_unix_ms || 0
      );
      checkoutPlanPayment = result;
      checkoutPaymentExpiryHandled = false;
      renderPlanCheckout(checkoutPlanEligibility || { planRole: checkoutPlan });
      checkoutPaymentSuccessShown = false;
      setStatus("");
      showToast(result.message || "Đã tạo mã thanh toán");
      setPayDisabled(true);
      startExpireTimer();
      startPlanPaymentPolling();
      return;
    }
    throw new Error("Chưa có đơn thanh toán.");
  } catch (error) {
    setStatus(error.message, "error");
    showToast(error.message);
    if (checkoutMode === "plan") {
      await loadPlanCheckout(checkoutPlan);
    } else {
      setPayDisabled(false);
    }
  }
}

function stopPlanPaymentPolling() {
  clearInterval(checkoutPaymentPollTimer);
  checkoutPaymentPollTimer = null;
}

function startPlanPaymentPolling() {
  stopPlanPaymentPolling();
  if (!checkoutPlanOrderId) return;
  checkPlanPaymentStatus(true);
  checkoutPaymentPollTimer = setInterval(() => checkPlanPaymentStatus(true), 2500);
}

async function checkPlanPaymentStatus(auto = true) {
  if (!checkoutPlanOrderId) return;
  try {
    const response = await authenticatedFetch(`/api/commerce/plan-orders/${encodeURIComponent(checkoutPlanOrderId)}/status`, { cache: "no-store" });
    const result = await readJson(response);
    syncPlanExpiryFromResult(result);
    if (result.paid) {
      await syncPlanRoleAfterPayment(result.role);
      completePlanPayment(result.message || "Thanh toán thành công. Gói đã kích hoạt.");
      return;
    }

    if (result.expired) {
      // Keep the QR visible until the exact countdown shown on this page reaches zero.
      if (!isPlanPaymentExpiredLocally()) {
        setStatus("");
        return;
      }
      await expirePlanPaymentLocally(result.message || "Mã đã hết hạn. Hãy tạo mã mới.");
      return;
    }

    setStatus("");
  } catch (error) {
    if (isPlanPaymentExpiredLocally()) {
      await expirePlanPaymentLocally();
      return;
    }
    if (!auto) showToast(error.message, "error");
  }
}

document.addEventListener("DOMContentLoaded", () => {
  document.getElementById("checkoutPayButton")?.addEventListener("click", payCheckout);
  loadCheckout();
});
