(function () {
  const storageKey = "travelwaiTheme";


  function setButtonIcon(button, name) {
    if (!button) return;
    button.innerHTML = window.TravelwAIIcons?.html ? window.TravelwAIIcons.html(name) : `<span data-interface-icon="${name}"></span>`;
  }

  function readTheme() {
    try { return localStorage.getItem(storageKey) === "dark" ? "dark" : "light"; }
    catch { return "light"; }
  }

  function saveTheme(theme) {
    try { localStorage.setItem(storageKey, theme); } catch { }
  }

  function setThemeButtonState(button, theme) {
    if (!button) return;
    const isDark = theme === "dark";
    setButtonIcon(button, isDark ? "sun" : "moon");
    button.setAttribute("aria-label", isDark ? "Chuyển sang nền sáng" : "Chuyển sang nền tối");
    button.setAttribute("title", isDark ? "Chuyển sang nền sáng" : "Chuyển sang nền tối");
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-travelwai-theme", theme);
    if (document.body) {
      document.body.classList.toggle("travelwai-theme-dark", theme === "dark");
      document.body.classList.toggle("travelwai-theme-light", theme !== "dark");
    }
    saveTheme(theme);
    setThemeButtonState(document.getElementById("travelwaiThemeToggle"), theme);
  }

  function readCookie(name) {
    const prefix = `${name}=`;
    const item = document.cookie.split(";").map(value => value.trim()).find(value => value.startsWith(prefix));
    return item ? decodeURIComponent(item.slice(prefix.length)) : "";
  }

  function ensureFloatingToolsHost() {
    let host = document.getElementById("travelwaiFloatingTools");
    if (!host) {
      host = document.createElement("div");
      host.id = "travelwaiFloatingTools";
      host.className = "twai-floating-tools-zone";
      host.setAttribute("aria-label", "Công cụ nhanh TravelwAI");
      document.body.appendChild(host);
    }
    return host;
  }

  function ensureReloadButton(host) {
    let button = document.getElementById("travelwaiReloadButton");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiReloadButton";
      button.className = "twai-theme-toggle twai-tool-action twai-reload-toggle";
      button.type = "button";
    }
    if (button.parentElement !== host) host.appendChild(button);
    button.style.setProperty("--twai-tool-index", "4");
    setButtonIcon(button, "refresh-cw");
    button.setAttribute("aria-label", "Tải lại");
    button.setAttribute("title", "Tải lại");
    button.onclick = function () { window.location.reload(); };
  }

  function isSafeCacheKeyToRemove(key) {
    if (!key) return false;
    const normalized = String(key).toLowerCase();
    return normalized === "twai_cache_conversations"
      || normalized.startsWith("travelwai:notifications:cache:")
      || normalized.startsWith("travelwai:notifications:read:")
      || normalized.startsWith("travelwai:notifications:deleted:")
      || /^travelwai:[^:]*cache[^:]*:/i.test(key)
      || /^travelwai_static_province_info_34(?:_v\d+)?$/i.test(key);
  }

  function clearRecommendedLocalCache() {
    try {
      for (let i = localStorage.length - 1; i >= 0; i -= 1) {
        const key = localStorage.key(i);
        if (isSafeCacheKeyToRemove(key)) localStorage.removeItem(key);
      }
    } catch { }
  }

  function setCacheButtonState(button, done) {
    if (!button) return;
    setButtonIcon(button, done ? "check" : "trash-2");
    button.setAttribute("aria-label", done ? "Đã dọn dẹp" : "Dọn dẹp");
    button.setAttribute("title", done ? "Đã dọn dẹp" : "Dọn dẹp");
  }

  function ensureCacheButton(host) {
    let button = document.getElementById("travelwaiCacheClearButton");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiCacheClearButton";
      button.className = "twai-theme-toggle twai-tool-action twai-cache-clear-toggle";
      button.type = "button";
    }
    if (button.parentElement !== host) host.appendChild(button);
    button.style.setProperty("--twai-tool-index", "3");
    setCacheButtonState(button, false);
    button.onclick = async function () {
      button.disabled = true;
      try {
        const token = localStorage.getItem("idToken")
          || sessionStorage.getItem("idToken")
          || localStorage.getItem("token")
          || sessionStorage.getItem("token")
          || readCookie("TravelwAIAuth");

        if (token) {
          if (typeof window.clearTravelwAINotifications === "function") {
            await window.clearTravelwAINotifications();
          } else {
            const response = await fetch("/api/notifications/clear", {
              method: "POST",
              credentials: "same-origin",
              headers: {
                "Content-Type": "application/json",
                Authorization: `Bearer ${token}`
              },
              body: JSON.stringify({ ids: [] })
            });
            let result = null;
            try { result = await response.json(); } catch { }
            if (!response.ok || result?.success === false) {
              throw new Error(result?.message || "Không dọn được thông báo.");
            }
            window.dispatchEvent(new CustomEvent("travelwai:notifications-cleared", { detail: result }));
          }
        }

        clearRecommendedLocalCache();
        if (window.invalidateTravelwAINotificationCache) {
          try { window.invalidateTravelwAINotificationCache(); } catch { }
        }
        setCacheButtonState(button, true);
      } catch (error) {
        setCacheButtonState(button, false);
        window.TravelwAINotify?.error?.(error.message || "Không dọn được thông báo.", { persist: false });
      } finally {
        window.setTimeout(function () {
          setCacheButtonState(button, false);
          button.disabled = false;
        }, 1200);
      }
    };
  }

  function ensureThemeButton(host) {
    let button = document.getElementById("travelwaiThemeToggle");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiThemeToggle";
      button.className = "twai-theme-toggle twai-tool-action";
      button.type = "button";
    }
    if (button.parentElement !== host) host.appendChild(button);
    button.style.setProperty("--twai-tool-index", "1");
    setThemeButtonState(button, readTheme());
    button.onclick = function () {
      const current = document.documentElement.getAttribute("data-travelwai-theme") === "dark" ? "dark" : "light";
      applyTheme(current === "dark" ? "light" : "dark");
    };
  }

  function ensureRevealButton(host) {
    let button = document.getElementById("travelwaiToolsRevealButton");
    if (!button) {
      button = document.createElement("button");
      button.id = "travelwaiToolsRevealButton";
      button.className = "twai-theme-toggle twai-tools-reveal-toggle";
      button.type = "button";
      button.setAttribute("data-no-translate", "");
      host.appendChild(button);
    }
    if (button.parentElement !== host) host.appendChild(button);
    setButtonIcon(button, "chevron-up");
    button.setAttribute("aria-label", "Mở công cụ nhanh");
    button.setAttribute("title", "Công cụ nhanh");
    button.setAttribute("aria-expanded", host.classList.contains("is-open") ? "true" : "false");
    button.onclick = function (event) {
      event.preventDefault();
      event.stopPropagation();
      const open = !host.classList.contains("is-open");
      host.classList.toggle("is-open", open);
      button.setAttribute("aria-expanded", open ? "true" : "false");
    };
  }

  function bindFloatingToolsDismiss(host) {
    if (host.dataset.dismissBound === "true") return;
    host.dataset.dismissBound = "true";
    document.addEventListener("click", function (event) {
      if (host.contains(event.target)) return;
      host.classList.remove("is-open");
      document.getElementById("travelwaiToolsRevealButton")?.setAttribute("aria-expanded", "false");
    });
    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      host.classList.remove("is-open");
      document.getElementById("travelwaiToolsRevealButton")?.setAttribute("aria-expanded", "false");
    });
  }

  applyTheme(readTheme());

  function initializeFloatingTools() {
    const host = ensureFloatingToolsHost();
    ensureReloadButton(host);
    ensureCacheButton(host);
    ensureThemeButton(host);
    ensureRevealButton(host);
    bindFloatingToolsDismiss(host);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initializeFloatingTools, { once: true });
  } else {
    initializeFloatingTools();
  }
  window.addEventListener("pageshow", initializeFloatingTools);
})();
