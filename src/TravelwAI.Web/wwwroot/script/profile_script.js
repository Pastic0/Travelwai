let profilePlanCountdownTimer = null;
document.addEventListener("DOMContentLoaded", async function () {
  if (!isAuthenticated()) {
    window.location.href = "/login";
    return;
  }

  const profilePicture = document.getElementById("profilePicture");
  let originalProfilePicSrc = profilePicture ? profilePicture.src : "/logo/profile-icon-white.webp";

  try {
    const response = await authenticatedFetch("/api/profile", { method: "GET" });
    if (response && response.ok) {
      const result = await response.json();
      if (result.success) {
        const user = result.user || {};
        const email = user.email || localStorage.getItem("userEmail") || "Chưa có";
        const username =
          user.username ||
          user.displayName ||
          user.name ||
          localStorage.getItem("username") ||
          (email.includes("@") ? email.split("@")[0] : "Chưa có");
        const createdRaw = user.created_at || user.createdAt || user.registeredAt || user.registrationDate;
        const createdAt = formatProfileDate(createdRaw);

        setText("userEmail", email);
        setText("username", username);
        setText("createdAt", createdAt);
        setText("profileDisplayName", username);
        setText("profileHeroName", username);
        setText("profileEmailText", email);
        renderProfileImageStorage(user.imageStorage || user.image_storage || {});
        const planRole = user.plan_role || user.planRole || user.role || user.userRole || "Free";
        localStorage.setItem("userRole", planRole || "Free");
        sessionStorage.setItem("userRole", planRole || "Free");
        const planExpiresAt = user.plan_expires_at || user.planExpiresAt || "";
        const nextPlanRole = user.next_plan_role || user.nextPlanRole || "";
        const nextPlanStart = user.next_plan_started_at || user.nextPlanStartedAt || "";
        setText("profilePlanRole", planRole || "Free");
        startProfilePlanCountdown(planExpiresAt, nextPlanRole, nextPlanStart);

        if (user.profilePic && profilePicture) {
          originalProfilePicSrc = `${user.profilePic}`;
          profilePicture.src = originalProfilePicSrc;
          profilePicture.style.objectFit = "cover";
        }
      }
    }
  } catch (error) {
    console.error("Lỗi tải hồ sơ:", error);
    const fallbackEmail = localStorage.getItem("userEmail") || "Lỗi tải dữ liệu";
    setText("userEmail", fallbackEmail);
    setText("username", "Lỗi tải dữ liệu");
    setText("createdAt", "Lỗi tải dữ liệu");
    setText("profileDisplayName", "Lỗi tải dữ liệu");
    setText("profileHeroName", "TravelwAI");
    setText("profileEmailText", fallbackEmail);
    setText("profilePlanRole", "Không tải được");
    setText("profilePlanExpiresAt", "Không tải được");
    showProfileToast("Không thể tải đầy đủ hồ sơ người dùng.", "error");
  }

  setupProfilePictureUpload(originalProfilePicSrc);
  setupProfileImageStorageDelete();

  if (sessionStorage.getItem("travelwaiOpenProfilePassword") === "1") {
    sessionStorage.removeItem("travelwaiOpenProfilePassword");
    setTimeout(openProfilePasswordModal, 120);
  }
});

function setText(id, value) {
  const element = document.getElementById(id);
  if (element) element.textContent = value || "Chưa có";
}

function showProfileToast(message, type = "info") {
  return window.TravelwAIToast(message, type);
}

function normalizeProfilePlanRole(value) {
  const role = String(value || "Free").trim();
  return role || "Free";
}

function formatProfileDateTime(value) {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString("vi-VN", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

function formatProfileCountdown(value) {
  if (!value) return "Không có thời hạn";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Không có thời hạn";
  const ms = date.getTime() - Date.now();
  if (ms <= 0) return "Đã hết hạn";
  const totalSeconds = Math.floor(ms / 1000);
  const days = Math.floor(totalSeconds / 86400);
  const hours = String(Math.floor((totalSeconds % 86400) / 3600)).padStart(2, "0");
  const minutes = String(Math.floor((totalSeconds % 3600) / 60)).padStart(2, "0");
  const seconds = String(totalSeconds % 60).padStart(2, "0");
  return `${days} ngày ${hours}:${minutes}:${seconds}`;
}

function startProfilePlanCountdown(expiresAt, nextRole, nextStart) {
  clearInterval(profilePlanCountdownTimer);
  const render = () => {
    let text = "Không có thời hạn";
    if (expiresAt) {
      const endText = formatProfileDateTime(expiresAt);
      text = `${formatProfileCountdown(expiresAt)}${endText ? ` · Hết hạn ${endText}` : ""}`;
    }
    if (nextRole && nextStart) {
      const nextText = formatProfileDateTime(nextStart);
      text += `${text ? " · " : ""}Tiếp theo: ${normalizeProfilePlanRole(nextRole)}${nextText ? ` từ ${nextText}` : ""}`;
    }
    setText("profilePlanExpiresAt", text);
  };
  render();
  if (expiresAt) profilePlanCountdownTimer = setInterval(render, 1000);
}

function formatProfileDate(value) {
  if (!value) return "Chưa có";

  let date;
  if (typeof value === "number" || /^\d+$/.test(String(value))) {
    const numberValue = Number(value);
    date = new Date(numberValue > 100000000000 ? numberValue : numberValue * 1000);
  } else {
    date = new Date(value);
  }

  if (Number.isNaN(date.getTime())) return "Chưa có";
  return date.toLocaleDateString("vi-VN");
}

function formatProfileStorageBytes(value) {
  const bytes = Math.max(0, Number(value) || 0);
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes > 0 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(bytes >= 10 * 1024 * 1024 ? 1 : 2)} MB`;
}

function renderProfileImageStorage(storage) {
  const usedBytes = Math.max(0, Number(storage?.usedBytes ?? storage?.used_bytes) || 0);
  const limitBytes = Math.max(1, Number(storage?.limitBytes ?? storage?.limit_bytes) || 200 * 1024 * 1024);
  const imageCount = Math.max(0, Number(storage?.imageCount ?? storage?.image_count) || 0);
  const messageImageCount = Math.max(0, Number(storage?.messageImageCount ?? storage?.message_image_count) || 0);
  const percent = Math.min(100, Math.max(0, Number(storage?.usedPercent ?? storage?.used_percent) || (usedBytes / limitBytes) * 100));

  const text = document.getElementById("profileImageStorageText");
  const count = document.getElementById("profileImageStorageCount");
  const bar = document.getElementById("profileImageStorageBar");
  const track = document.getElementById("profileImageStorageTrack");
  const deleteBtn = document.getElementById("deleteUploadedImagesBtn");

  if (text) text.textContent = `${formatProfileStorageBytes(usedBytes)} / ${formatProfileStorageBytes(limitBytes)}`;
  if (count) count.textContent = `${imageCount} ảnh · ${messageImageCount} ảnh tin nhắn`;
  if (bar) bar.style.width = `${percent}%`;
  if (track) {
    track.setAttribute("aria-valuenow", String(Math.round(percent)));
    track.setAttribute("aria-valuemax", "100");
    track.classList.toggle("is-warning", percent >= 80 && percent < 100);
    track.classList.toggle("is-full", percent >= 100);
  }
  if (deleteBtn) {
    deleteBtn.disabled = messageImageCount <= 0;
    deleteBtn.dataset.messageImageCount = String(messageImageCount);
  }
}

function setupProfileImageStorageDelete() {
  const button = document.getElementById("deleteUploadedImagesBtn");
  if (!button) return;

  button.addEventListener("click", async () => {
    if (button.disabled) return;
    const confirmed = window.confirm("Xóa toàn bộ ảnh đã tải lên trong tin nhắn?");
    if (!confirmed) return;

    const originalHtml = button.innerHTML;
    try {
      button.disabled = true;
      button.textContent = "Đang xóa...";
      const response = await authenticatedFetch("/api/profile/image-storage", { method: "DELETE" });
      const result = response ? await response.json().catch(() => ({})) : {};
      if (!response || !response.ok || !result.success) {
        throw new Error(result.message || "Không thể xóa ảnh.");
      }

      renderProfileImageStorage(result.imageStorage || {});
      showProfileToast(result.message || "Đã xóa ảnh tin nhắn.");
    } catch (error) {
      console.error("Lỗi xóa ảnh tài khoản:", error);
      showProfileToast(error.message || "Không thể xóa ảnh.", "error");
    } finally {
      button.innerHTML = originalHtml;
      button.disabled = Number(button.dataset.messageImageCount || 0) <= 0;
      if (window.TravelwAIInterfaceIcons?.refresh) window.TravelwAIInterfaceIcons.refresh(button);
    }
  });
}

function setupProfilePictureUpload(originalSrc) {
  const input = document.getElementById("profilePictureInput");
  const profilePicture = document.getElementById("profilePicture");
  const uploadBtn = document.querySelector(".upload-btn");
  if (!input || !profilePicture || !uploadBtn) return;

  input.addEventListener("change", async function (event) {
    const file = event.target.files && event.target.files[0];
    if (!file) return;

    if (!file.type.startsWith("image/")) {
      showProfileToast("Vui lòng chọn tệp ảnh.", "error");
      input.value = "";
      return;
    }

    if (file.size > 10 * 1024 * 1024) {
      showProfileToast("Dung lượng ảnh phải nhỏ hơn 10MB.", "error");
      input.value = "";
      return;
    }

    const originalBtnHtml = uploadBtn.innerHTML;
    let newImageURL = null;

    try {
      uploadBtn.textContent = "Đang tải lên...";
      uploadBtn.disabled = true;

      const reader = new FileReader();
      reader.onload = (e) => {
        profilePicture.src = e.target.result;
        profilePicture.style.objectFit = "cover";
      };
      reader.readAsDataURL(file);

      const uploadFile = window.TravelwAIImageOptimizer
        ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
        : file;
      const formData = new FormData();
      formData.append("profilePic", uploadFile, uploadFile.name || file.name);

      const response = await authenticatedFetch("/api/profile", {
        method: "POST",
        body: formData,
      });

      const result = response ? await response.json().catch(() => ({})) : {};
      if (!response || !response.ok) {
        if (result.imageStorage) renderProfileImageStorage(result.imageStorage);
        throw new Error(result.message || result.detail || `Tải lên thất bại với mã: ${response ? response.status : "unknown"}`);
      }

      newImageURL = result.profilePic || result.profile_picture_url;
      if (result.imageStorage) renderProfileImageStorage(result.imageStorage);
      showProfileToast(result.message || "Đã cập nhật ảnh đại diện.");
    } catch (error) {
      console.error("Lỗi tải ảnh đại diện:", error);
      showProfileToast(error.message || "Không thể tải ảnh đại diện. Vui lòng thử lại.", "error");
      profilePicture.src = originalSrc;
    } finally {
      uploadBtn.innerHTML = originalBtnHtml;
      uploadBtn.disabled = false;
      if (window.TravelwAIInterfaceIcons?.refresh) window.TravelwAIInterfaceIcons.refresh(uploadBtn);
      input.value = "";
      if (newImageURL) {
        originalSrc = `${newImageURL}`;
        profilePicture.src = originalSrc;
      }
    }
  });
}
function openProfilePasswordModal() {
  const modal = document.getElementById("profilePasswordModal");
  if (!modal) return;
  modal.hidden = false;
  document.body.classList.add("profile-modal-open");
  setTimeout(() => document.getElementById("profileNewPassword")?.focus(), 40);
}

function closeProfilePasswordModal() {
  const modal = document.getElementById("profilePasswordModal");
  if (!modal) return;
  modal.hidden = true;
  document.body.classList.remove("profile-modal-open");
  const form = document.getElementById("profilePasswordForm");
  if (form) form.reset();
}

function setupProfilePasswordForm() {
  const form = document.getElementById("profilePasswordForm");
  if (!form) return;

  form.addEventListener("submit", async (event) => {
    event.preventDefault();

    const newPassword = document.getElementById("profileNewPassword")?.value || "";
    const confirmPassword = document.getElementById("profileConfirmPassword")?.value || "";
    const submitBtn = form.querySelector(".profile-password-submit");
    const cancelBtn = form.querySelector(".profile-password-cancel");
    const closeBtn = form.querySelector(".profile-password-close");

    if (newPassword.length < 8) {
      showProfileToast("Mật khẩu mới phải có ít nhất 8 ký tự.", "error");
      return;
    }

    if (newPassword !== confirmPassword) {
      showProfileToast("Mật khẩu nhập lại không khớp.", "error");
      return;
    }

    const originalText = submitBtn ? submitBtn.textContent : "";
    try {
      if (submitBtn) {
        submitBtn.textContent = "Đang lưu...";
        submitBtn.disabled = true;
      }
      if (cancelBtn) cancelBtn.disabled = true;
      if (closeBtn) closeBtn.disabled = true;

      const response = await authenticatedFetch("/api/profile/change-password", {
        method: "POST",
        body: JSON.stringify({ password: newPassword }),
      });

      const result = response ? await response.json() : { success: false };
      if (!response || !response.ok || !result.success) {
        throw new Error(result.message || "Không đổi được mật khẩu.");
      }

      closeProfilePasswordModal();
      localStorage.removeItem("idToken");
      localStorage.removeItem("refreshToken");
      localStorage.removeItem("userEmail");
      localStorage.removeItem("tokenExpiration");
      localStorage.removeItem("username");
      localStorage.removeItem("userRole");
      localStorage.removeItem("isLocked");
      localStorage.removeItem("travelwaiLastActivityAt");
      sessionStorage.removeItem("travelwaiIdleLogoutRunning");
      showProfileToast(result.message || "Đổi mật khẩu thành công.");
      setTimeout(() => {
        window.location.href = "/login";
      }, 900);
    } catch (error) {
      console.error("Lỗi đổi mật khẩu:", error);
      showProfileToast(error.message || "Không đổi được mật khẩu. Vui lòng thử lại.", "error");
    } finally {
      if (submitBtn) {
        submitBtn.textContent = originalText || "Lưu mật khẩu";
        submitBtn.disabled = false;
      }
      if (cancelBtn) cancelBtn.disabled = false;
      if (closeBtn) closeBtn.disabled = false;
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeProfilePasswordModal();
  });
}

document.addEventListener("DOMContentLoaded", setupProfilePasswordForm);


document.addEventListener("DOMContentLoaded", function () {
  const url = new URL(window.location.href);
  if (url.searchParams.get("payment") !== "plan-success") return;

  window.setTimeout(() => {
    showProfileToast("Thanh toán thành công. Gói tài khoản đã được kích hoạt.", "success");
  }, 250);

  url.searchParams.delete("payment");
  window.history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
});
