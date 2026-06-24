document.addEventListener("DOMContentLoaded", function () {
  if (typeof isAuthenticated !== "function") {
    console.error("Chưa tải được chức năng xác thực. Đang chuyển về trang đăng nhập.");
    window.location.href = "/login";
    return;
  }

  if (!isAuthenticated()) {
    window.location.href = "/login";
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
