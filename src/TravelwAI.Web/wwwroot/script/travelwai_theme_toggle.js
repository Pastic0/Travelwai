(function () {
  const THEME_KEY = "travelwai-global-theme";

  function getSavedTheme() {
    try {
      return localStorage.getItem(THEME_KEY) === "dark" ? "dark" : "light";
    } catch (error) {
      return "light";
    }
  }

  function icon(theme) {
    if (theme === "dark") {
      return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v2.2M12 18.8V21M4.2 4.2l1.6 1.6M18.2 18.2l1.6 1.6M3 12h2.2M18.8 12H21M4.2 19.8l1.6-1.6M18.2 5.8l1.6-1.6"></path><circle cx="12" cy="12" r="4.2"></circle></svg>';
    }
    return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 13.1A8.2 8.2 0 1 1 10.9 3a6.4 6.4 0 0 0 10.1 10.1z"></path></svg>';
  }

  function applyTheme(theme) {
    const normalizedTheme = theme === "dark" ? "dark" : "light";
    document.body.classList.toggle("travelwai-theme-dark", normalizedTheme === "dark");
    document.body.classList.toggle("travelwai-theme-light", normalizedTheme !== "dark");
    document.body.classList.remove("map-theme-light", "map-theme-night");

    const button = document.querySelector(".travelwai-theme-toggle");
    if (button) {
      const nextTheme = normalizedTheme === "dark" ? "sáng" : "tối";
      button.innerHTML = icon(normalizedTheme);
      button.setAttribute("aria-label", "Chuyển nền sang chế độ " + nextTheme);
      button.title = "Chuyển nền sang chế độ " + nextTheme;
    }

    return normalizedTheme;
  }

  function resolveMountPoint() {
    const navLogo = document.querySelector('header .logo');
    if (navLogo) {
      const logoText = navLogo.querySelector('.logo-text') || navLogo.lastElementChild || navLogo;
      return { type: 'after', anchor: logoText };
    }

    const brandLink = document.querySelector('.brand-home-link');
    if (brandLink) {
      return { type: 'after', anchor: brandLink };
    }

    return null;
  }

  function createToggle() {
    document.querySelectorAll('.travelwai-theme-toggle-host, .travelwai-theme-toggle, .travelwai-theme-toggle-zone, .map-theme-toggle').forEach(function (node) {
      node.remove();
    });

    const mount = resolveMountPoint();
    if (!mount || !mount.anchor || !mount.anchor.parentNode) {
      applyTheme(getSavedTheme());
      return;
    }

    const host = document.createElement('span');
    host.className = 'travelwai-theme-toggle-host';

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'travelwai-theme-toggle';
    host.appendChild(button);

    mount.anchor.insertAdjacentElement('afterend', host);

    let currentTheme = applyTheme(getSavedTheme());

    button.addEventListener('click', function () {
      currentTheme = currentTheme === 'dark' ? 'light' : 'dark';
      applyTheme(currentTheme);
      try {
        localStorage.setItem(THEME_KEY, currentTheme);
      } catch (error) {}
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    applyTheme(getSavedTheme());
    createToggle();
  });
})();
