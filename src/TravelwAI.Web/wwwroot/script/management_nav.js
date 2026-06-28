(function () {
  function readCookie(name) {
    const value = `; ${document.cookie || ""}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) return decodeURIComponent(parts.pop().split(";").shift() || "");
    return "";
  }

  function normalizeRole(value) {
    return String(value || "").trim().toLowerCase().replace(/[_-]+/g, " ");
  }

  function normalizePath(value) {
    const path = String(value || "").split("?")[0].replace(/\/+$/, "") || "/";
    return path;
  }

  function setManagementLinkState(role) {
    const isAdmin = normalizeRole(role) === "admin";
    const currentPath = normalizePath(window.location.pathname);
    document.querySelectorAll(".admin-role-page-link").forEach(link => {
      link.style.display = isAdmin ? "inline-flex" : "none";
      link.hidden = !isAdmin;
      const linkPath = normalizePath(link.getAttribute("href") || "");
      const active = linkPath === currentPath;
      link.classList.toggle("is-current", active);
      if (active) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    });
  }

  async function fetchRoleFromServer(token) {
    if (!token) return "";
    try {
      const response = await fetch("/api/verify-token", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ idToken: token })
      });
      const result = await response.json().catch(() => ({}));
      return result && result.user ? (result.user.role || "") : "";
    } catch (_) {
      return "";
    }
  }

  async function syncManagementNav() {
    const localRole = localStorage.getItem("userRole") || "";
    setManagementLinkState(localRole);

    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || readCookie("TravelwAIAuth");
    const serverRole = await fetchRoleFromServer(token);
    if (serverRole) {
      localStorage.setItem("userRole", serverRole);
      setManagementLinkState(serverRole);
    }
  }

  document.addEventListener("DOMContentLoaded", syncManagementNav);
  window.addEventListener("storage", syncManagementNav);
  window.refreshTravelwAIManagementNav = syncManagementNav;
})();
