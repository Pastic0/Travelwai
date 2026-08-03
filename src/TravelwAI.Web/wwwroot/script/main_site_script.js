document.addEventListener("DOMContentLoaded", function () {
  if (typeof isAuthenticated !== "function") {
    console.error("Chưa tải được chức năng xác thực. Đang chuyển về trang đăng nhập.");
    window.location.href = typeof buildLoginUrl === "function" ? buildLoginUrl("/home") : "/login?returnUrl=%2Fhome";
    return;
  }

  if (!isAuthenticated()) {
    window.location.href = typeof buildLoginUrl === "function" ? buildLoginUrl("/home") : "/login?returnUrl=%2Fhome";
    return;
  }

  const userEmail = localStorage.getItem("userEmail");
  if (userEmail) {
  }

  const backgroundSlider = document.querySelector(".background-slider");
  if (backgroundSlider) {
    backgroundSlider.style.removeProperty("background-image");
  }

  const navLinks = document.querySelectorAll("nav a");
  navLinks.forEach(function () {
  });

  const detailBtn = document.querySelector(".detail-btn");
  if (detailBtn) {
    detailBtn.addEventListener("click", function () {
    });
  }
});
