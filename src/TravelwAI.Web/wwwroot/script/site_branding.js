(function () {
  "use strict";

  const DEFAULT_LOGO_URL = "/logo/travelwai-icon.webp";
  const DEFAULT_LOGO_VERSION = "2026-07-26-brand-icon-v2";
  const DEFAULT_LIGHT_BACKGROUND_URL = "/main_site_image/travelwai-bg-light.webp";
  const DEFAULT_DARK_BACKGROUND_URL = "/main_site_image/travelwai-bg-dark.webp";
  const DEFAULT_BACKGROUND_VERSION = "2026-07-26-branding-cache-fix-v3";
  const CACHE_KEY = "travelwai_site_branding_v3";

  let currentLogoUrl = DEFAULT_LOGO_URL;
  let currentLogoVersion = DEFAULT_LOGO_VERSION;
  let currentLightBackgroundUrl = DEFAULT_LIGHT_BACKGROUND_URL;
  let currentLightBackgroundVersion = DEFAULT_BACKGROUND_VERSION;
  let currentDarkBackgroundUrl = DEFAULT_DARK_BACKGROUND_URL;
  let currentDarkBackgroundVersion = DEFAULT_BACKGROUND_VERSION;
  let stateRevision = 0;

  function normalizeUrl(value, fallback) {
    const text = String(value || "").trim();
    if (!text || /^javascript:/i.test(text)) return fallback;
    return text;
  }

  function withVersion(url, version, fallback) {
    const safeUrl = normalizeUrl(url, fallback);
    const safeVersion = String(version || "").trim();
    if (!safeVersion || safeVersion === "default") return safeUrl;
    const separator = safeUrl.includes("?") ? "&" : "?";
    return `${safeUrl}${separator}brand=${encodeURIComponent(safeVersion)}`;
  }

  function toCssUrl(url) {
    return `url("${String(url || "").replace(/\\/g, "\\\\").replace(/"/g, "\\\"")}")`;
  }

  function persistCache() {
    try {
      localStorage.setItem(CACHE_KEY, JSON.stringify({
        logoUrl: currentLogoUrl,
        logoVersion: currentLogoVersion,
        backgroundLightUrl: currentLightBackgroundUrl,
        backgroundLightVersion: currentLightBackgroundVersion,
        backgroundDarkUrl: currentDarkBackgroundUrl,
        backgroundDarkVersion: currentDarkBackgroundVersion
      }));
      localStorage.removeItem("travelwai_site_branding_v2");
      localStorage.removeItem("travelwai_site_branding_v1");
      localStorage.removeItem("travelwaiBackgroundVersion");
    } catch (_) { }
  }

  function isTravelwAILogoElement(element) {
    if (!(element instanceof Element)) return false;
    if (element.matches("[data-site-logo]")) return true;
    const raw = element.getAttribute("src") || element.getAttribute("href") || "";
    return raw.includes("/logo/travelwai-icon.webp") || raw.includes("logo/travelwai-icon.webp");
  }

  function updateElement(element, versionedUrl, force = false) {
    if (!(element instanceof Element) || !isTravelwAILogoElement(element)) return;
    const attributeName = element.tagName === "LINK" ? "href" : "src";
    const currentValue = element.getAttribute(attributeName) || "";
    const stillUsesDefaultLogo = currentValue.includes("travelwai-icon.webp");
    if (!force && !stillUsesDefaultLogo) return;
    element.setAttribute("data-site-logo", "true");
    if (currentValue !== versionedUrl) element.setAttribute(attributeName, versionedUrl);
  }

  function updateTree(root, versionedUrl, force = false) {
    if (!root) return;
    if (root instanceof Element) updateElement(root, versionedUrl, force);
    root.querySelectorAll?.("img[data-site-logo], img[src*='travelwai-icon.webp'], link[data-site-logo], link[rel~='icon'][href*='travelwai-icon.webp']")
      .forEach(element => updateElement(element, versionedUrl, force));
  }

  function dispatchBrandingChange() {
    window.dispatchEvent(new CustomEvent("travelwai:brandingchange", {
      detail: {
        logoUrl: getLogoUrl(),
        rawLogoUrl: currentLogoUrl,
        logoVersion: currentLogoVersion,
        backgroundLightUrl: getBackgroundUrl("light"),
        backgroundDarkUrl: getBackgroundUrl("dark")
      }
    }));
  }

  function applyLogo(url, version, persist = true) {
    stateRevision += 1;
    currentLogoUrl = normalizeUrl(url, DEFAULT_LOGO_URL);
    currentLogoVersion = String(version || DEFAULT_LOGO_VERSION);
    const versionedUrl = getLogoUrl();

    document.documentElement.style.setProperty("--travelwai-site-logo", toCssUrl(versionedUrl));
    updateTree(document, versionedUrl, true);
    window.TravelwAISiteLogoUrl = versionedUrl;

    if (persist) persistCache();
    dispatchBrandingChange();
    return versionedUrl;
  }

  function applyBackground(theme, url, version, persist = true) {
    stateRevision += 1;
    const normalizedTheme = String(theme || "").toLowerCase() === "dark" ? "dark" : "light";
    if (normalizedTheme === "dark") {
      currentDarkBackgroundUrl = normalizeUrl(url, DEFAULT_DARK_BACKGROUND_URL);
      currentDarkBackgroundVersion = String(version || DEFAULT_BACKGROUND_VERSION);
    } else {
      currentLightBackgroundUrl = normalizeUrl(url, DEFAULT_LIGHT_BACKGROUND_URL);
      currentLightBackgroundVersion = String(version || DEFAULT_BACKGROUND_VERSION);
    }

    const versionedUrl = getBackgroundUrl(normalizedTheme);
    document.documentElement.style.setProperty(
      normalizedTheme === "dark" ? "--twai-page-bg-dark" : "--twai-page-bg-light",
      toCssUrl(versionedUrl)
    );

    if (persist) persistCache();
    dispatchBrandingChange();
    return versionedUrl;
  }

  function applyBackgrounds(lightUrl, lightVersion, darkUrl, darkVersion, persist = true) {
    applyBackground("light", lightUrl, lightVersion, false);
    applyBackground("dark", darkUrl, darkVersion, false);
    if (persist) persistCache();
    dispatchBrandingChange();
  }

  function getLogoUrl() {
    return withVersion(currentLogoUrl, currentLogoVersion, DEFAULT_LOGO_URL);
  }

  function getBackgroundUrl(theme) {
    return String(theme || "").toLowerCase() === "dark"
      ? withVersion(currentDarkBackgroundUrl, currentDarkBackgroundVersion, DEFAULT_DARK_BACKGROUND_URL)
      : withVersion(currentLightBackgroundUrl, currentLightBackgroundVersion, DEFAULT_LIGHT_BACKGROUND_URL);
  }

  function readCachedBranding() {
    try {
      const cached = JSON.parse(localStorage.getItem(CACHE_KEY) || "null");
      if (!cached) return;
      if (cached.logoUrl) applyLogo(cached.logoUrl, cached.logoVersion || cached.version || DEFAULT_LOGO_VERSION, false);
      applyBackgrounds(
        cached.backgroundLightUrl || DEFAULT_LIGHT_BACKGROUND_URL,
        cached.backgroundLightVersion || DEFAULT_BACKGROUND_VERSION,
        cached.backgroundDarkUrl || DEFAULT_DARK_BACKGROUND_URL,
        cached.backgroundDarkVersion || DEFAULT_BACKGROUND_VERSION,
        false
      );
    } catch (_) { }
  }

  async function refresh() {
    const requestRevision = stateRevision;
    try {
      const response = await fetch("/api/site-branding", { cache: "no-store", headers: { Accept: "application/json" } });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || result.success === false) return;
      // Do not let a slower response containing old settings overwrite a logo
      // or background that the admin changed while this request was in flight.
      if (requestRevision !== stateRevision) return;
      const data = result.data || result;
      applyLogo(data.logoUrl || data.logo_url || DEFAULT_LOGO_URL, data.version || data.logoVersion || DEFAULT_LOGO_VERSION, false);
      applyBackgrounds(
        data.backgroundLightUrl || data.background_light_url || DEFAULT_LIGHT_BACKGROUND_URL,
        data.backgroundLightVersion || data.background_light_version || DEFAULT_BACKGROUND_VERSION,
        data.backgroundDarkUrl || data.background_dark_url || DEFAULT_DARK_BACKGROUND_URL,
        data.backgroundDarkVersion || data.background_dark_version || DEFAULT_BACKGROUND_VERSION,
        false
      );
      persistCache();
    } catch (_) { }
  }

  readCachedBranding();
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => updateTree(document, getLogoUrl(), true), { once: true });
  } else {
    updateTree(document, getLogoUrl(), true);
  }

  const observer = new MutationObserver(mutations => {
    const versionedUrl = getLogoUrl();
    mutations.forEach(mutation => {
      mutation.addedNodes.forEach(node => {
        if (node instanceof Element) updateTree(node, versionedUrl);
      });
      if (mutation.type === "attributes" && mutation.target instanceof Element) {
        updateElement(mutation.target, versionedUrl);
      }
    });
  });
  observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["src", "href"] });

  window.TravelwAISiteBranding = {
    applyLogo,
    applyBackground,
    applyBackgrounds,
    refresh,
    getLogoUrl,
    getBackgroundUrl,
    defaultLogoUrl: DEFAULT_LOGO_URL,
    defaultLightBackgroundUrl: DEFAULT_LIGHT_BACKGROUND_URL,
    defaultDarkBackgroundUrl: DEFAULT_DARK_BACKGROUND_URL
  };

  refresh();
})();
