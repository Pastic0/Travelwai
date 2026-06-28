let cartItems = [];

function money(value) {
  return Number(value || 0).toLocaleString("vi-VN") + "đ";
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>\"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;" }[char]));
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
  if (!response.ok || data.success === false) throw new Error(data.message || data.detail || "Không thực hiện được");
  return data;
}

function getValue(item, ...keys) {
  for (const key of keys) {
    if (item && item[key] !== undefined && item[key] !== null && item[key] !== "") return item[key];
  }
  return "";
}

function formatDate(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value).split("T")[0] || "";
  return date.toLocaleDateString("vi-VN");
}

async function loadCart() {
  const body = document.getElementById("cartTableBody");
  if (body) body.innerHTML = '<tr><td colspan="6" class="empty-line">Đang tải giỏ hàng...</td></tr>';
  try {
    const response = await authenticatedFetch("/api/commerce/cart");
    const result = await readJson(response);
    cartItems = Array.isArray(result.data) ? result.data : [];
    renderCart();
  } catch (error) {
    if (body) body.innerHTML = `<tr><td colspan="6" class="empty-line">${escapeHtml(error.message)}</td></tr>`;
  }
}

function renderCart() {
  const body = document.getElementById("cartTableBody");
  if (!body) return;
  if (!cartItems.length) {
    body.innerHTML = '<tr><td colspan="6" class="empty-line">Giỏ hàng đang trống.</td></tr>';
    return;
  }
  body.innerHTML = cartItems.map(item => {
    const id = getValue(item, "id", "Id");
    const type = getValue(item, "item_type", "itemType") || "tour";
    const name = getValue(item, "tour_name", "tourName", "plan_name", "planName") || "Sản phẩm";
    const status = getValue(item, "status") || "Trong giỏ";
    const expired = String(status).toLowerCase() === "hết hạn";
    const statusClass = expired ? "status-canceled" : "status-booked";
    return `<tr>
      <td><strong>${escapeHtml(name)}</strong></td>
      <td>${escapeHtml(type === "tour" ? "Tour" : "Gói")}</td>
      <td>${escapeHtml(getValue(item, "quantity") || 1)}</td>
      <td>${money(getValue(item, "total_price", "totalPrice"))}</td>
      <td><span class="badge tour-status-badge ${statusClass}">${escapeHtml(status)}</span></td>
      <td><div class="inline-actions">${expired ? "" : `<a class="btn-primary cart-action-link" href="/checkout?cartId=${encodeURIComponent(id)}">Thanh toán</a>`}<button class="btn-danger" type="button" onclick="deleteCartItem('${escapeHtml(id)}')">Xóa</button></div></td>
    </tr>`;
  }).join("");
}

async function deleteCartItem(id) {
  try {
    const response = await authenticatedFetch(`/api/commerce/cart/${encodeURIComponent(id)}`, { method: "DELETE" });
    const result = await readJson(response);
    showToast(result.message || "Đã xoá khỏi giỏ hàng");
    await loadCart();
  } catch (error) {
    showToast(error.message);
  }
}

document.addEventListener("DOMContentLoaded", loadCart);
window.deleteCartItem = deleteCartItem;
