(function () {
  "use strict";

  const DEFAULT_LIGHT_BACKGROUND_URL = "/main_site_image/travelwai-bg-light.webp";
  const DEFAULT_DARK_BACKGROUND_URL = "/main_site_image/travelwai-bg-dark.webp";
  const DEFAULT_BACKGROUND_VERSION = "2026-07-26-branding-cache-fix-v3";
  const CACHE_KEY = "travelwai_site_branding_v5";
  const LEGACY_CACHE_KEYS = [
    "travelwai_site_branding_v4",
    "travelwai_site_branding_v3",
    "travelwai_site_branding_v2",
    "travelwai_site_branding_v1"
  ];

  let currentLogoUrl = "";
  let currentLogoVersion = "";
  let currentLightBackgroundUrl = DEFAULT_LIGHT_BACKGROUND_URL;
  let currentLightBackgroundVersion = DEFAULT_BACKGROUND_VERSION;
  let currentDarkBackgroundUrl = DEFAULT_DARK_BACKGROUND_URL;
  let currentDarkBackgroundVersion = DEFAULT_BACKGROUND_VERSION;
  let stateRevision = 0;
  let logoApplyRevision = 0;

  function normalizeUrl(value, fallback = "") {
    const text = String(value || "").trim();
    if (!text || /^javascript:/i.test(text)) return fallback;
    return text;
  }

  function normalizeLogoUrl(value) {
    const text = normalizeUrl(value);
    if (!text) return "";
    // The packaged legacy logo was removed. Never revive it from HTML,
    // localStorage, API data, browser history, or old service-worker caches.
    if (/\/?logo\/travelwai-icon\.webp(?:[?#]|$)/i.test(text)) return "";
    return text;
  }

  function withVersion(url, version, fallback = "") {
    const safeUrl = normalizeUrl(url, fallback);
    if (!safeUrl) return "";
    const safeVersion = String(version || "").trim();
    if (!safeVersion || safeVersion === "default") return safeUrl;
    const separator = safeUrl.includes("?") ? "&" : "?";
    return `${safeUrl}${separator}brand=${encodeURIComponent(safeVersion)}`;
  }

  function toCssUrl(url) {
    if (!url) return "none";
    return `url("${String(url).replace(/\\/g, "\\\\").replace(/"/g, "\\\"")}")`;
  }

  function removeLegacyCaches() {
    try {
      LEGACY_CACHE_KEYS.forEach(key => localStorage.removeItem(key));
      localStorage.removeItem("travelwaiBackgroundVersion");
    } catch (_) { }
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
      removeLegacyCaches();
    } catch (_) { }
  }

  function isTravelwAILogoElement(element) {
    if (!(element instanceof Element)) return false;
    if (element.matches("[data-site-logo]")) return true;
    const raw = element.getAttribute("src") || element.getAttribute("href") || "";
    return /\/?logo\/travelwai-icon\.webp(?:[?#]|$)/i.test(raw);
  }

  function clearLogoElement(element) {
    if (!(element instanceof Element)) return;
    const attributeName = element.tagName === "LINK" ? "href" : "src";
    element.removeAttribute(attributeName);
    element.setAttribute("data-site-logo", "true");
    if (element.tagName === "IMG") {
      element.hidden = true;
      element.classList.remove("site-logo-ready");
    }
  }

  function setImageLogo(image, versionedUrl, revision) {
    if (!(image instanceof HTMLImageElement)) return;
    const current = image.getAttribute("src") || "";
    if (current === versionedUrl && image.complete && image.naturalWidth > 0) {
      image.hidden = false;
      image.classList.add("site-logo-ready");
      return;
    }

    // Keep a previously loaded uploaded logo visible until the replacement is decoded.
    // A new empty element stays hidden instead of flashing a white circle.
    if (/\/?logo\/travelwai-icon\.webp(?:[?#]|$)/i.test(current)) clearLogoElement(image);
    const loader = new Image();
    loader.decoding = "async";
    loader.onload = () => {
      if (revision !== logoApplyRevision || versionedUrl !== getLogoUrl()) return;
      image.setAttribute("src", versionedUrl);
      image.hidden = false;
      image.classList.add("site-logo-ready");
    };
    loader.onerror = () => {
      if (revision !== logoApplyRevision) return;
      if (!(image.complete && image.naturalWidth > 0)) clearLogoElement(image);
    };
    loader.src = versionedUrl;
    loader.decode?.().then(loader.onload).catch(() => { });
  }

  function updateElement(element, versionedUrl) {
    if (!(element instanceof Element) || !isTravelwAILogoElement(element)) return;
    element.setAttribute("data-site-logo", "true");

    if (!versionedUrl) {
      clearLogoElement(element);
      return;
    }

    if (element.tagName === "LINK" && element.relList?.contains("icon")) {
      if (element.getAttribute("href") === versionedUrl) return;
      const replacement = element.cloneNode(true);
      replacement.setAttribute("href", versionedUrl);
      replacement.setAttribute("data-site-logo", "true");
      element.replaceWith(replacement);
      return;
    }

    if (element.tagName === "IMG") setImageLogo(element, versionedUrl, logoApplyRevision);
  }

  function updateTree(root, versionedUrl) {
    if (!root) return;
    if (root instanceof Element) updateElement(root, versionedUrl);
    root.querySelectorAll?.("img[data-site-logo], link[data-site-logo], img[src*='travelwai-icon.webp'], link[rel~='icon'][href*='travelwai-icon.webp']")
      .forEach(element => updateElement(element, versionedUrl));
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
    logoApplyRevision += 1;
    currentLogoUrl = normalizeLogoUrl(url);
    currentLogoVersion = currentLogoUrl ? String(version || "") : "";
    const versionedUrl = getLogoUrl();

    document.documentElement.style.setProperty("--travelwai-site-logo", toCssUrl(versionedUrl));
    document.documentElement.classList.toggle("has-site-logo", Boolean(versionedUrl));
    updateTree(document, versionedUrl);
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
    return withVersion(currentLogoUrl, currentLogoVersion);
  }

  function getBackgroundUrl(theme) {
    return String(theme || "").toLowerCase() === "dark"
      ? withVersion(currentDarkBackgroundUrl, currentDarkBackgroundVersion, DEFAULT_DARK_BACKGROUND_URL)
      : withVersion(currentLightBackgroundUrl, currentLightBackgroundVersion, DEFAULT_LIGHT_BACKGROUND_URL);
  }

  function readCachedBranding() {
    try {
      let cached = JSON.parse(localStorage.getItem(CACHE_KEY) || "null");
      if (!cached) {
        // Migrate only a remotely uploaded logo. The packaged legacy path is rejected.
        for (const key of LEGACY_CACHE_KEYS) {
          const candidate = JSON.parse(localStorage.getItem(key) || "null");
          if (candidate) {
            cached = candidate;
            break;
          }
        }
      }
      if (!cached) {
        removeLegacyCaches();
        return;
      }

      applyLogo(cached.logoUrl, cached.logoVersion || cached.version || "", false);
      applyBackgrounds(
        cached.backgroundLightUrl || DEFAULT_LIGHT_BACKGROUND_URL,
        cached.backgroundLightVersion || DEFAULT_BACKGROUND_VERSION,
        cached.backgroundDarkUrl || DEFAULT_DARK_BACKGROUND_URL,
        cached.backgroundDarkVersion || DEFAULT_BACKGROUND_VERSION,
        false
      );
      persistCache();
    } catch (_) {
      removeLegacyCaches();
    }
  }

  async function refresh() {
    const requestRevision = stateRevision;
    try {
      const response = await fetch(`/api/site-branding?branding=${Date.now()}`, {
        cache: "no-store",
        headers: { Accept: "application/json", "Cache-Control": "no-cache" }
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || result.success === false) return;
      if (requestRevision !== stateRevision) return;
      const data = result.data || result;
      applyLogo(data.logoUrl || data.logo_url || "", data.version || data.logoVersion || "", false);
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
    document.addEventListener("DOMContentLoaded", () => updateTree(document, getLogoUrl()), { once: true });
  } else {
    updateTree(document, getLogoUrl());
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
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ["src", "href"]
  });

  window.TravelwAISiteBranding = {
    applyLogo,
    applyBackground,
    applyBackgrounds,
    refresh,
    getLogoUrl,
    getBackgroundUrl,
    defaultLogoUrl: "",
    defaultLightBackgroundUrl: DEFAULT_LIGHT_BACKGROUND_URL,
    defaultDarkBackgroundUrl: DEFAULT_DARK_BACKGROUND_URL
  };

  refresh();
  window.addEventListener("pageshow", event => {
    if (event.persisted) refresh();
  });
  window.addEventListener("focus", refresh);
})();
