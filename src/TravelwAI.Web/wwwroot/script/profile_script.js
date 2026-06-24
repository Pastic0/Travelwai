document.addEventListener("DOMContentLoaded", async function () {
  if (!isAuthenticated()) {
    window.location.href = "/login";
    return;
  }

  const profilePicture = document.getElementById("profilePicture");
  let originalProfilePicSrc = profilePicture ? profilePicture.src : "/logo/profile-icon-white.webp";

  const token = localStorage.getItem("idToken");
  if (!token) {
    window.location.href = "/login";
    return;
  }

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
    showProfileToast("Không thể tải đầy đủ hồ sơ người dùng.", "error");
  }

  setupProfilePictureUpload(originalProfilePicSrc);

  if (sessionStorage.getItem("travelwaiOpenProfilePassword") === "1") {
    sessionStorage.removeItem("travelwaiOpenProfilePassword");
    setTimeout(openProfilePasswordModal, 120);
  }
});

function setText(id, value) {
  const element = document.getElementById(id);
  if (element) element.textContent = value || "Chưa có";
}

function showProfileToast(message, type) {
  const toast = document.getElementById("profileToast");
  if (!toast) {
    alert(message);
    return;
  }

  toast.textContent = message;
  toast.classList.toggle("error", type === "error");
  toast.hidden = false;

  clearTimeout(showProfileToast.timer);
  showProfileToast.timer = setTimeout(() => {
    toast.hidden = true;
    toast.classList.remove("error");
  }, 2600);
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

    if (file.size > 5 * 1024 * 1024) {
      showProfileToast("Dung lượng ảnh phải nhỏ hơn 5MB.", "error");
      input.value = "";
      return;
    }

    const originalBtnText = uploadBtn.textContent;
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

      if (!response || !response.ok) {
        throw new Error(`Tải lên thất bại với mã: ${response ? response.status : "unknown"}`);
      }

      const result = await response.json();
      newImageURL = result.profilePic || result.profile_picture_url;
      showProfileToast("Cập nhật ảnh đại diện thành công.");
    } catch (error) {
      console.error("Lỗi tải ảnh đại diện:", error);
      showProfileToast("Không thể tải ảnh đại diện. Vui lòng thử lại.", "error");
      profilePicture.src = originalSrc;
    } finally {
      uploadBtn.textContent = originalBtnText;
      uploadBtn.disabled = false;
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

    if (newPassword.length < 6) {
      showProfileToast("Mật khẩu mới phải có ít nhất 6 ký tự.", "error");
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
      showProfileToast(result.message || "Đổi mật khẩu thành công. Đang chuyển về đăng nhập.");
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
