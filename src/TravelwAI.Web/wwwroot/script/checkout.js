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

const PLAN_BANK_CODE = "BIDV";
const PLAN_ACCOUNT_NUMBER = "0343513147";
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

function showToast(message) {
  const toast = document.getElementById("tourToast");
  if (!toast) return window.TravelwAIToast(message);
  toast.textContent = message;
  toast.classList.add("show");
  setTimeout(() => toast.classList.remove("show"), 2600);
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

function setConfirmVisible(visible) {
  const button = document.getElementById("checkoutConfirmPaymentButton");
  if (!button) return;
  button.hidden = !visible;
  button.style.display = visible ? "inline-flex" : "none";
}

function setConfirmDisabled(disabled) {
  const button = document.getElementById("checkoutConfirmPaymentButton");
  if (button) button.disabled = !!disabled;
}

function planMonthlyPrice(role) {
  const normalized = String(role || "").toLowerCase();
  if (normalized === "premium") return 129000;
  if (normalized === "vip") return 59000;
  return 0;
}

function calculatePlanPrice(role, months) {
  const safeMonths = Math.min(12, Math.max(1, Number(months || 1)));
  const monthly = planMonthlyPrice(role);
  const original = monthly * safeMonths;
  const discountPercent = safeMonths >= 12 ? 10 : 0;
  const discountAmount = Math.round(original * discountPercent / 100);
  const total = Math.max(0, original - discountAmount);
  return { months: safeMonths, monthly, original, discountPercent, discountAmount, total };
}

function buildQrUrl(amount, orderId) {
  const info = encodeURIComponent(`TWAI ${orderId || checkoutPlan || "GOI"}`.trim());
  const accountName = encodeURIComponent(PLAN_ACCOUNT_NAME);
  return `https://img.vietqr.io/image/${PLAN_BANK_CODE}-${PLAN_ACCOUNT_NUMBER}-compact2.png?amount=${Math.round(Number(amount || 0))}&addInfo=${info}&accountName=${accountName}`;
}

function formatCountdown(expiresAt) {
  const expires = new Date(expiresAt);
  if (Number.isNaN(expires.getTime())) return "03:00";
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

function startExpireTimer() {
  clearInterval(checkoutExpireTimer);
  if (!checkoutPlanOrderExpiresAt) return;
  checkoutExpireTimer = setInterval(() => {
    const timer = document.getElementById("planPaymentTimer");
    if (timer) timer.textContent = formatCountdown(checkoutPlanOrderExpiresAt);
    const expires = new Date(checkoutPlanOrderExpiresAt);
    if (!Number.isNaN(expires.getTime()) && expires.getTime() <= Date.now()) {
      clearInterval(checkoutExpireTimer);
      confirmPlanPayment(true);
    }
  }, 1000);
}

async function loadCheckout() {
  const params = new URLSearchParams(window.location.search);
  checkoutCartId = params.get("cartId") || "";
  checkoutPlan = params.get("plan") || params.get("role") || "";
  setConfirmVisible(false);
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
  setPayDisabled(true);
}

async function loadCartCheckout(id) {
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
    setPayDisabled(true);
  }
}

async function loadPlanCheckout(plan) {
  const role = String(plan || "").trim();
  checkoutPlan = role;
  try {
    const response = await authenticatedFetch(`/api/commerce/plan-eligibility?plan=${encodeURIComponent(role)}`);
    const result = await readJson(response);
    checkoutPlanEligibility = result;
    renderPlanCheckout(result);
    setStatus(result.message || "", result.can_buy || result.canBuy ? "info" : "error");
    setPayDisabled(!(result.can_buy || result.canBuy));
  } catch (error) {
    renderSummary(`<div class="empty-line">${escapeHtml(error.message)}</div>`);
    setPayDisabled(true);
  }
}

function renderPlanPaymentBox(price, payment) {
  const orderId = getValue(payment, "orderId", "order_id") || checkoutPlanOrderId || checkoutPlan;
  const bank = getValue(payment, "paymentBank", "payment_bank") || PLAN_BANK_CODE;
  const account = getValue(payment, "paymentAccount", "payment_account") || PLAN_ACCOUNT_NUMBER;
  const accountName = getValue(payment, "paymentAccountName", "payment_account_name") || PLAN_ACCOUNT_NAME;
  const content = getValue(payment, "paymentContent", "payment_content") || `TWAI ${orderId}`;
  const qr = getValue(payment, "paymentQrUrl", "payment_qr_url") || buildQrUrl(price.total, orderId);
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
    <div class="checkout-qr-wrap"><img src="${escapeHtml(qr)}" alt="QR thanh toán" /></div>
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
  const selected = Number(currentSelect || 1);
  const price = calculatePlanPrice(role, selected);
  const locked = !!checkoutPlanOrderId;
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
    ${renderPlanPaymentBox(price, checkoutPlanPayment)}
  </div>`);
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
      setStatus(result.message || "Thanh toán thành công", "success");
      showToast(result.message || "Thanh toán thành công");
      return;
    }
    if (checkoutMode === "plan") {
      if (checkoutPlanOrderId) {
        setStatus("Quét QR rồi bấm Xác nhận thanh toán.", "info");
        setPayDisabled(false);
        return;
      }
      response = await authenticatedFetch("/api/commerce/plan-orders", { method: "POST", body: JSON.stringify({ planRole: checkoutPlan, months: getSelectedPlanMonths() }) });
      const result = await readJson(response);
      checkoutPlanOrderId = result.orderId || result.order_id || "";
      checkoutPlanOrderExpiresAt = result.expiresAt || result.expires_at || "";
      checkoutPlanPayment = result;
      renderPlanCheckout(checkoutPlanEligibility || { planRole: checkoutPlan });
      setConfirmVisible(true);
      setConfirmDisabled(false);
      setStatus(result.message || "Quét QR rồi bấm Xác nhận thanh toán.", "info");
      showToast(result.message || "Đã tạo thanh toán");
      setPayDisabled(false);
      startExpireTimer();
      return;
    }
    throw new Error("Chưa có đơn thanh toán.");
  } catch (error) {
    setStatus(error.message, "error");
    showToast(error.message);
    setPayDisabled(false);
  }
}

async function confirmPlanPayment(auto = false) {
  if (!checkoutPlanOrderId) {
    if (!auto) showToast("Chưa có đơn thanh toán.");
    return;
  }
  setConfirmDisabled(true);
  try {
    const response = await authenticatedFetch(`/api/commerce/plan-orders/${encodeURIComponent(checkoutPlanOrderId)}/confirm`, { method: "POST" });
    const result = await readJson(response);
    clearInterval(checkoutExpireTimer);
    await loadPlanCheckout(checkoutPlan);
    setStatus(result.message || "Xác nhận thanh toán thành công", "success");
    showToast(result.message || "Xác nhận thanh toán thành công");
    setPayDisabled(true);
    setConfirmDisabled(true);
  } catch (error) {
    const expired = error.data && error.data.expired;
    setStatus(error.message, "error");
    if (!auto) showToast(error.message);
    if (expired) {
      clearInterval(checkoutExpireTimer);
      checkoutPlanOrderId = "";
      checkoutPlanOrderExpiresAt = "";
      checkoutPlanPayment = null;
      setConfirmVisible(false);
      setPayDisabled(false);
      if (checkoutPlanEligibility) renderPlanCheckout(checkoutPlanEligibility);
    } else {
      setConfirmDisabled(false);
    }
  }
}

document.addEventListener("DOMContentLoaded", () => {
  document.getElementById("checkoutPayButton")?.addEventListener("click", payCheckout);
  document.getElementById("checkoutConfirmPaymentButton")?.addEventListener("click", () => confirmPlanPayment(false));
  loadCheckout();
});
