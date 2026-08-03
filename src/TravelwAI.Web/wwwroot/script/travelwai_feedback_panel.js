(function () {
  "use strict";

  var MAX_FILES = 3;
  var MAX_FILE_BYTES = 10 * 1024 * 1024;
  var selectedFiles = [];
  var loadingHistory = false;

  function readCookie(name) {
    var prefix = name + "=";
    var item = document.cookie.split(";").map(function (part) { return part.trim(); }).find(function (part) { return part.indexOf(prefix) === 0; });
    return item ? decodeURIComponent(item.slice(prefix.length)) : "";
  }

  function getToken() {
    return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || readCookie("TravelwAIAuth") || "";
  }

  function authHeaders() {
    var token = getToken();
    return token ? { Authorization: "Bearer " + token } : {};
  }

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function formatBytes(value) {
    var bytes = Math.max(0, Number(value) || 0);
    if (bytes < 1024) return Math.round(bytes) + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(bytes >= 100 * 1024 ? 0 : 1) + " KB";
    return (bytes / (1024 * 1024)).toFixed(bytes >= 100 * 1024 * 1024 ? 0 : 1) + " MB";
  }

  function formatDate(value) {
    var date = value ? new Date(value) : null;
    if (!date || Number.isNaN(date.getTime())) return "";
    return date.toLocaleString("vi-VN", { hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit", year: "numeric" });
  }

  function statusLabel(status) {
    var labels = { new: "Mới", processing: "Đang xử lý", resolved: "Đã xử lý", closed: "Đã đóng" };
    return labels[String(status || "new").toLowerCase()] || "Mới";
  }

  function getPanel() {
    return document.getElementById("feedback-panel");
  }

  function showMessage(message, type, persist) {
    if (typeof window.TravelwAIToast === "function") {
      window.TravelwAIToast(message, type || "info", undefined, { persist: persist !== false });
      return;
    }
    window.alert(message);
  }

  function renderSelectedFiles() {
    var host = document.getElementById("travelwaiFeedbackFiles");
    if (!host) return;
    if (!selectedFiles.length) {
      host.innerHTML = "";
      host.hidden = true;
      return;
    }
    host.hidden = false;
    host.innerHTML = selectedFiles.map(function (file, index) {
      return '<div class="feedback-file-chip" data-feedback-file-index="' + index + '">' +
        '<span data-no-translate>' + escapeHtml(file.name) + '</span>' +
        '<small>' + escapeHtml(formatBytes(file.size)) + '</small>' +
        '<button type="button" data-remove-feedback-file="' + index + '" aria-label="Xóa tệp" title="Xóa"><span data-interface-icon="x"></span></button>' +
        '</div>';
    }).join("");
    window.TravelwAIInterfaceIcons?.refresh?.(host);
    window.TravelwAIIcons?.render?.(host);
  }

  function addFiles(files) {
    var incoming = Array.from(files || []);
    for (var i = 0; i < incoming.length; i += 1) {
      var file = incoming[i];
      if (file.size <= 0 || file.size > MAX_FILE_BYTES) {
        showMessage("Mỗi tệp đính kèm tối đa 10 MB.", "error");
        continue;
      }
      if (selectedFiles.length >= MAX_FILES) {
        showMessage("Mỗi phản hồi tối đa 3 tệp.", "error");
        break;
      }
      var duplicate = selectedFiles.some(function (item) {
        return item.name === file.name && item.size === file.size && item.lastModified === file.lastModified;
      });
      if (!duplicate) selectedFiles.push(file);
    }
    renderSelectedFiles();
  }

  function renderHistory(items) {
    var host = document.getElementById("travelwaiFeedbackHistory");
    if (!host) return;
    if (!Array.isArray(items) || !items.length) {
      host.innerHTML = '<div class="notification-panel-state">Chưa có phản hồi.</div>';
      return;
    }
    host.innerHTML = items.map(function (item) {
      var attachmentCount = Array.isArray(item.attachments) ? item.attachments.length : 0;
      var note = String(item.adminNote || "").trim();
      return '<article class="feedback-history-item">' +
        '<div class="feedback-history-head"><span class="feedback-status feedback-status-' + escapeHtml(item.status || "new") + '">' + escapeHtml(statusLabel(item.status)) + '</span><time>' + escapeHtml(formatDate(item.createdAt)) + '</time></div>' +
        '<p data-no-translate>' + escapeHtml(item.message || "") + '</p>' +
        (attachmentCount ? '<small><span data-interface-icon="paperclip"></span> ' + attachmentCount + '</small>' : '') +
        (note ? '<div class="feedback-admin-note"><strong>Admin</strong><span data-no-translate>' + escapeHtml(note) + '</span></div>' : '') +
        '</article>';
    }).join("");
    window.TravelwAIInterfaceIcons?.refresh?.(host);
    window.TravelwAIIcons?.render?.(host);
    window.TravelwAILanguage?.translate?.(host);
  }

  async function loadHistory() {
    var host = document.getElementById("travelwaiFeedbackHistory");
    if (!host || loadingHistory) return;
    loadingHistory = true;
    try {
      var response = await fetch("/api/feedback/mine?limit=20", {
        credentials: "same-origin",
        headers: authHeaders()
      });
      var result = await response.json().catch(function () { return {}; });
      if (!response.ok) throw new Error(result.message || "Không tải được phản hồi.");
      renderHistory(result.data || []);
    } catch (error) {
      host.innerHTML = '<div class="notification-panel-state error">' + escapeHtml(error.message || "Không tải được phản hồi.") + '</div>';
    } finally {
      loadingHistory = false;
    }
  }

  async function submitFeedback(event) {
    event.preventDefault();
    var messageInput = document.getElementById("travelwaiFeedbackMessage");
    var submitButton = document.getElementById("travelwaiFeedbackSubmitButton");
    var message = String(messageInput?.value || "").trim();
    if (!message) return showMessage("Vui lòng nhập nội dung phản hồi.", "error");

    var form = new FormData();
    form.append("message", message);
    selectedFiles.forEach(function (file) { form.append("attachments", file, file.name); });

    if (submitButton) {
      submitButton.disabled = true;
      submitButton.classList.add("is-loading");
    }
    try {
      var response = await fetch("/api/feedback", {
        method: "POST",
        credentials: "same-origin",
        headers: authHeaders(),
        body: form
      });
      var result = await response.json().catch(function () { return {}; });
      if (!response.ok) throw new Error(result.message || "Không gửi được phản hồi.");
      if (messageInput) messageInput.value = "";
      selectedFiles = [];
      renderSelectedFiles();
      var fileInput = document.getElementById("travelwaiFeedbackFileInput");
      if (fileInput) fileInput.value = "";
      showMessage(result.message || "Đã gửi phản hồi.", "success", false);
      await loadHistory();
      window.invalidateTravelwAINotificationCache?.();
      window.refreshTravelwAINotificationBadge?.(true);
      window.dispatchEvent(new CustomEvent("travelwai:feedback-created"));
      window.dispatchEvent(new CustomEvent("travelwai:notification-created", { detail: { source: "feedback" } }));
    } catch (error) {
      showMessage(error.message || "Không gửi được phản hồi.", "error");
    } finally {
      if (submitButton) {
        submitButton.disabled = false;
        submitButton.classList.remove("is-loading");
      }
    }
  }

  function openFeedbackPanel(event) {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    var panel = getPanel();
    if (!panel) return false;
    window.closeNotificationPanel?.();
    panel.classList.add("open");
    panel.setAttribute("aria-hidden", "false");
    document.body.classList.add("feedback-panel-open");
    document.getElementById("travelwaiFeedbackMessage")?.focus();
    loadHistory();
    return false;
  }

  function closeFeedbackPanel() {
    var panel = getPanel();
    if (!panel) return;
    panel.classList.remove("open");
    panel.setAttribute("aria-hidden", "true");
    document.body.classList.remove("feedback-panel-open");
  }

  function bind() {
    document.querySelectorAll("[data-feedback-panel-trigger], #feedbackIconContainer").forEach(function (trigger) {
      trigger.onclick = openFeedbackPanel;
    });
    document.querySelectorAll("[data-close-feedback-panel]").forEach(function (button) {
      button.onclick = function (event) {
        event.preventDefault();
        closeFeedbackPanel();
      };
    });
    document.getElementById("travelwaiFeedbackForm")?.addEventListener("submit", submitFeedback);
    document.getElementById("travelwaiFeedbackAttachButton")?.addEventListener("click", function () {
      document.getElementById("travelwaiFeedbackFileInput")?.click();
    });
    document.getElementById("travelwaiFeedbackFileInput")?.addEventListener("change", function (event) {
      addFiles(event.target.files);
      event.target.value = "";
    });
    document.getElementById("travelwaiFeedbackFiles")?.addEventListener("click", function (event) {
      var button = event.target.closest?.("[data-remove-feedback-file]");
      if (!button) return;
      var index = Number(button.getAttribute("data-remove-feedback-file"));
      if (Number.isInteger(index) && index >= 0 && index < selectedFiles.length) selectedFiles.splice(index, 1);
      renderSelectedFiles();
    });

    document.addEventListener("click", function (event) {
      var panel = getPanel();
      if (!panel || !panel.classList.contains("open")) return;
      if (panel.contains(event.target) || event.target.closest?.("[data-feedback-panel-trigger]")) return;
      closeFeedbackPanel();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") closeFeedbackPanel();
    });
  }

  window.openFeedbackPanel = openFeedbackPanel;
  window.closeFeedbackPanel = closeFeedbackPanel;

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind, { once: true });
  else bind();
})();
