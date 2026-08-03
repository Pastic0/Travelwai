(function () {
  "use strict";

  var items = [];
  var selectedId = "";
  var loaded = false;
  var loading = false;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function escapeAttr(value) {
    return escapeHtml(value).replace(/`/g, "&#96;");
  }

  function statusLabel(status) {
    var labels = { new: "Mới", processing: "Đang xử lý", resolved: "Đã xử lý", closed: "Đã đóng" };
    return labels[String(status || "new").toLowerCase()] || "Mới";
  }

  function formatDate(value) {
    var date = value ? new Date(value) : null;
    if (!date || Number.isNaN(date.getTime())) return "";
    return date.toLocaleString("vi-VN", { hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit", year: "numeric" });
  }

  function formatBytes(value) {
    var bytes = Math.max(0, Number(value) || 0);
    if (bytes < 1024) return Math.round(bytes) + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(bytes >= 100 * 1024 ? 0 : 1) + " KB";
    return (bytes / (1024 * 1024)).toFixed(bytes >= 100 * 1024 * 1024 ? 0 : 1) + " MB";
  }

  function isImage(attachment) {
    return String(attachment?.contentType || "").toLowerCase().startsWith("image/") || /\.(jpe?g|png|gif|webp)(\?|$)/i.test(String(attachment?.url || ""));
  }

  function selectedItem() {
    return items.find(function (item) { return String(item.id) === String(selectedId); }) || null;
  }

  function showToastMessage(message, type) {
    if (typeof window.showToast === "function") window.showToast(message, type);
    else if (typeof window.TravelwAIToast === "function") window.TravelwAIToast(message, type || "info");
    else window.alert(message);
  }

  function renderList() {
    var host = document.getElementById("adminFeedbackList");
    if (!host) return;
    if (!items.length) {
      host.innerHTML = '<div class="empty-line">Không có phản hồi.</div>';
      renderDetail();
      return;
    }
    host.innerHTML = items.map(function (item) {
      var active = String(item.id) === String(selectedId) ? " is-selected" : "";
      return '<button type="button" class="admin-feedback-list-item' + active + '" data-feedback-id="' + escapeAttr(item.id) + '">' +
        '<div class="admin-feedback-list-head"><strong data-no-translate>' + escapeHtml(item.userName || item.userEmail || "Tài khoản") + '</strong><span class="admin-feedback-status status-' + escapeHtml(item.status || "new") + '">' + escapeHtml(statusLabel(item.status)) + '</span></div>' +
        '<p data-no-translate>' + escapeHtml(item.message || "") + '</p>' +
        '<time>' + escapeHtml(formatDate(item.createdAt)) + '</time>' +
        '</button>';
    }).join("");
  }

  function renderDetail() {
    var host = document.getElementById("adminFeedbackDetail");
    if (!host) return;
    var item = selectedItem();
    if (!item) {
      host.innerHTML = '<div class="admin-feedback-empty">Chọn phản hồi</div>';
      return;
    }
    var attachments = Array.isArray(item.attachments) ? item.attachments : [];
    var attachmentsHtml = attachments.length ? '<div class="admin-feedback-attachments">' + attachments.map(function (attachment) {
      var preview = isImage(attachment)
        ? '<img src="' + escapeAttr(attachment.url) + '" alt="" loading="lazy" />'
        : '<span data-interface-icon="file"></span>';
      return '<a class="admin-feedback-attachment" href="' + escapeAttr(attachment.url) + '" target="_blank" rel="noopener">' +
        preview + '<span data-no-translate>' + escapeHtml(attachment.name || "Tệp đính kèm") + '</span><small>' + escapeHtml(formatBytes(attachment.size)) + '</small></a>';
    }).join("") + '</div>' : '';

    host.innerHTML = '<div class="admin-feedback-detail-head">' +
      '<div><strong data-no-translate>' + escapeHtml(item.userName || "Tài khoản") + '</strong><span data-no-translate>' + escapeHtml(item.userEmail || "") + '</span></div>' +
      '<time>' + escapeHtml(formatDate(item.createdAt)) + '</time></div>' +
      '<div class="admin-feedback-message" data-no-translate>' + escapeHtml(item.message || "") + '</div>' +
      attachmentsHtml +
      '<div class="admin-feedback-fields">' +
        '<label>Trạng thái<select id="adminFeedbackDetailStatus">' +
          ['new','processing','resolved','closed'].map(function (status) { return '<option value="' + status + '"' + (status === item.status ? ' selected' : '') + '>' + statusLabel(status) + '</option>'; }).join("") +
        '</select></label>' +
        '<label>Xử lý<textarea id="adminFeedbackAdminNote" rows="5" maxlength="4000" data-no-translate>' + escapeHtml(item.adminNote || "") + '</textarea></label>' +
      '</div>' +
      '<div class="admin-feedback-detail-actions">' +
        '<button type="button" class="btn-soft admin-feedback-delete" id="deleteAdminFeedback"><span data-interface-icon="trash-2"></span><span>Xóa</span></button>' +
        '<button type="button" class="btn-primary" id="saveAdminFeedback"><span data-interface-icon="check"></span><span>Lưu</span></button>' +
      '</div>';
    window.TravelwAIInterfaceIcons?.refresh?.(host);
    window.TravelwAIIcons?.render?.(host);
  }

  async function load(force) {
    if (loading || (loaded && !force)) return;
    var host = document.getElementById("adminFeedbackList");
    if (!host) return;
    loading = true;
    host.innerHTML = '<div class="empty-line">Đang tải...</div>';
    try {
      var status = document.getElementById("adminFeedbackStatusFilter")?.value || "";
      var search = document.getElementById("adminFeedbackSearchInput")?.value || "";
      var url = "/api/feedback/admin?status=" + encodeURIComponent(status) + "&search=" + encodeURIComponent(search);
      var response = await authenticatedFetch(url);
      var result = await readAdminJson(response);
      items = Array.isArray(result.data) ? result.data : [];
      if (!items.some(function (item) { return String(item.id) === String(selectedId); })) selectedId = items[0]?.id || "";
      loaded = true;
      renderList();
      renderDetail();
    } catch (error) {
      host.innerHTML = '<div class="empty-line">' + escapeHtml(error.message || "Không tải được phản hồi.") + '</div>';
    } finally {
      loading = false;
    }
  }

  async function saveSelected() {
    var item = selectedItem();
    if (!item) return;
    var button = document.getElementById("saveAdminFeedback");
    if (button) button.disabled = true;
    try {
      var response = await authenticatedFetch("/api/feedback/admin/" + encodeURIComponent(item.id), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          status: document.getElementById("adminFeedbackDetailStatus")?.value || "new",
          adminNote: document.getElementById("adminFeedbackAdminNote")?.value || ""
        })
      });
      var result = await readAdminJson(response);
      var updated = result.data;
      items = items.map(function (entry) { return String(entry.id) === String(item.id) ? updated : entry; });
      renderList();
      renderDetail();
      showToastMessage(result.message || "Đã cập nhật phản hồi.", "success");
    } catch (error) {
      showToastMessage(error.message || "Không cập nhật được phản hồi.", "error");
    } finally {
      if (button) button.disabled = false;
    }
  }

  async function deleteSelected() {
    var item = selectedItem();
    if (!item) return;
    var confirmed = window.TravelwAIConfirm ? await window.TravelwAIConfirm("Xóa phản hồi này?") : window.confirm("Xóa phản hồi này?");
    if (!confirmed) return;
    try {
      var response = await authenticatedFetch("/api/feedback/admin/" + encodeURIComponent(item.id), { method: "DELETE" });
      var result = await readAdminJson(response);
      items = items.filter(function (entry) { return String(entry.id) !== String(item.id); });
      selectedId = items[0]?.id || "";
      renderList();
      renderDetail();
      showToastMessage(result.message || "Đã xóa phản hồi.", "success");
    } catch (error) {
      showToastMessage(error.message || "Không xóa được phản hồi.", "error");
    }
  }

  function debounce(callback, wait) {
    var timeout;
    return function () {
      clearTimeout(timeout);
      timeout = setTimeout(callback, wait);
    };
  }

  function getExportRows() {
    return {
      headers: ["Tài khoản", "Email", "Nội dung", "Trạng thái", "Thời gian", "Xử lý", "Tệp đính kèm"],
      rows: items.map(function (item) {
        var attachments = Array.isArray(item.attachments) ? item.attachments : [];
        return [
          item.userName || item.userEmail || "Tài khoản",
          item.userEmail || "",
          item.message || "",
          statusLabel(item.status),
          formatDate(item.createdAt),
          item.adminNote || "",
          attachments.map(function (attachment) {
            var name = attachment.name || "Tệp đính kèm";
            var url = attachment.url || "";
            return url ? name + ": " + url : name;
          }).join("\n")
        ];
      })
    };
  }

  window.TravelwAIAdminFeedback = {
    load: load,
    getExportRows: getExportRows
  };

  function bind() {
    document.querySelector('[data-tab="feedback"]')?.addEventListener("click", function () { load(false); });
    document.getElementById("refreshAdminFeedback")?.addEventListener("click", function () { load(true); });
    document.getElementById("adminFeedbackStatusFilter")?.addEventListener("change", function () { load(true); });
    document.getElementById("adminFeedbackSearchInput")?.addEventListener("input", debounce(function () { load(true); }, 260));
    document.getElementById("clearAdminFeedbackSearch")?.addEventListener("click", function () {
      var input = document.getElementById("adminFeedbackSearchInput");
      if (input) input.value = "";
      load(true);
    });
    document.getElementById("adminFeedbackList")?.addEventListener("click", function (event) {
      var button = event.target.closest?.("[data-feedback-id]");
      if (!button) return;
      selectedId = button.getAttribute("data-feedback-id") || "";
      renderList();
      renderDetail();
    });
    document.getElementById("adminFeedbackDetail")?.addEventListener("click", function (event) {
      if (event.target.closest?.("#saveAdminFeedback")) saveSelected();
      if (event.target.closest?.("#deleteAdminFeedback")) deleteSelected();
    });
    window.addEventListener("travelwai:feedback-created", function () { loaded = false; });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind, { once: true });
  else bind();
})();
