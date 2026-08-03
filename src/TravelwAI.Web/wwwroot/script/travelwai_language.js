(function () {
  "use strict";

  const STORAGE_KEY = "travelwaiLanguage";
  const CACHE_KEY = "travelwaiUiTranslationCacheV6";
  const WORKER_CLIENT_KEY = "travelwaiTranslationWorkerClientId";
  const WORKER_HEARTBEAT_MS = 45000;
  const STANDARD_TARGET_ATTRS = [
    "placeholder", "title", "aria-label", "aria-description", "alt"
  ];
  const STANDARD_TARGET_ATTR_SET = new Set(STANDARD_TARGET_ATTRS);
  const TRANSLATE_ATTRS_DECLARATION = "data-translate-attrs";
  const LETTER_CHARACTERS = /\p{L}/u;
  const SKIP_SELECTOR = [
    "script", "style", "noscript", "code", "pre", "canvas",
    "[data-no-translate]", "[data-chat-message-text]", "[data-chat-message-preview]",
    "[contenteditable='true']"
  ].join(",");
  const MAX_TRANSLATION_UNIT = 950;

  const LANGUAGE_FILE_URL = "/i18n/travelwai.languages.json?v=2026-07-13-storage-total-limit-v2";
  const LANGUAGE_COOKIE_MAX_AGE = 60 * 60 * 24 * 400;

  let exact = Object.freeze({});
  let languageCatalog = null;

  const originalText = new WeakMap();
  const lastAppliedText = new WeakMap();
  const originalAttrs = new WeakMap();
  const lastAppliedAttrs = new WeakMap();
  const pending = new Map();
  const unchangedForSession = new Set();
  let cache = loadCache();
  let currentLanguage = readLanguage();
  let processing = false;
  let flushTimer = 0;
  let observer = null;
  let applying = false;
  let workerHeartbeatTimer = 0;
  const translationWorkerClientId = getTranslationWorkerClientId();
  const dictionaryReady = loadLanguageCatalog();

  function readCookie(name) {
    try {
      const prefix = `${encodeURIComponent(name)}=`;
      const item = String(document.cookie || "")
        .split(";")
        .map(function (value) { return value.trim(); })
        .find(function (value) { return value.startsWith(prefix); });
      return item ? decodeURIComponent(item.slice(prefix.length)) : "";
    } catch (_) {
      return "";
    }
  }

  function saveCookie(name, value) {
    try {
      document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; path=/; max-age=${LANGUAGE_COOKIE_MAX_AGE}; SameSite=Lax`;
    } catch (_) { }
  }

  async function loadLanguageCatalog() {
    try {
      const response = await fetch(LANGUAGE_FILE_URL, { cache: "force-cache" });
      if (!response.ok) throw new Error(`Không thể tải file ngôn ngữ (${response.status}).`);
      const catalog = await response.json();
      const english = catalog?.translations?.en;
      languageCatalog = catalog && typeof catalog === "object" ? catalog : null;
      exact = Object.freeze(english && typeof english === "object" ? english : {});
      return languageCatalog;
    } catch (error) {
      console.warn("TravelwAI: không thể tải file ngôn ngữ riêng.", error);
      languageCatalog = null;
      exact = Object.freeze({});
      return null;
    }
  }

  function readLanguage() {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored === "en" || stored === "vi") return stored;
    } catch (_) { }

    const cookieValue = readCookie(STORAGE_KEY);
    return cookieValue === "en" ? "en" : "vi";
  }

  function saveLanguage(language) {
    try { localStorage.setItem(STORAGE_KEY, language); } catch (_) { }
    saveCookie(STORAGE_KEY, language);
  }

  function getTranslationWorkerClientId() {
    try {
      let value = sessionStorage.getItem(WORKER_CLIENT_KEY);
      if (!value) {
        value = globalThis.crypto?.randomUUID?.() || `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
        sessionStorage.setItem(WORKER_CLIENT_KEY, value);
      }
      return value;
    } catch (_) {
      return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    }
  }

  function reportTranslationWorkerState(active, useBeacon) {
    const payload = JSON.stringify({
      clientId: translationWorkerClientId,
      language: active ? "en" : "vi",
      active: Boolean(active)
    });

    if (useBeacon && navigator.sendBeacon) {
      try {
        navigator.sendBeacon(
          "/api/ui-language/worker-state",
          new Blob([payload], { type: "application/json" })
        );
        return;
      } catch (_) { }
    }

    fetch("/api/ui-language/worker-state", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: payload,
      cache: "no-store",
      keepalive: true
    }).catch(function () { });
  }

  function syncTranslationWorkerState() {
    window.clearInterval(workerHeartbeatTimer);
    workerHeartbeatTimer = 0;

    const englishActive = currentLanguage === "en";
    reportTranslationWorkerState(englishActive, false);
    if (englishActive) {
      workerHeartbeatTimer = window.setInterval(function () {
        if (currentLanguage === "en") reportTranslationWorkerState(true, false);
      }, WORKER_HEARTBEAT_MS);
    }
  }

  function loadCache() {
    try {
      const parsed = JSON.parse(localStorage.getItem(CACHE_KEY) || "{}");
      return parsed && typeof parsed === "object" ? parsed : {};
    } catch (_) { return {}; }
  }

  function saveCache() {
    try {
      const keys = Object.keys(cache);
      if (keys.length > 3000) {
        const reduced = {};
        keys.slice(-2400).forEach(function (key) { reduced[key] = cache[key]; });
        cache = reduced;
      }
      localStorage.setItem(CACHE_KEY, JSON.stringify(cache));
    } catch (_) { }
  }

  function normalize(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function shouldTranslate(value) {
    const normalized = normalize(value);
    if (!normalized) return false;
    if (Object.prototype.hasOwnProperty.call(exact, normalized)) return true;

    // Translate every textual value. Content that must remain unchanged is
    // controlled explicitly with data-no-translate / SKIP_SELECTOR instead of
    // relying on an incomplete Vietnamese word detector.
    return LETTER_CHARACTERS.test(normalized);
  }

  function getCustomTranslatableAttributes(element) {
    if (!element || typeof element.getAttribute !== "function") return [];
    return String(element.getAttribute(TRANSLATE_ATTRS_DECLARATION) || "")
      .split(/[\s,]+/)
      .map(function (name) { return name.trim().toLowerCase(); })
      .filter(function (name, index, values) {
        return name.startsWith("data-")
          && name !== TRANSLATE_ATTRS_DECLARATION
          && values.indexOf(name) === index;
      });
  }

  function getTranslatableAttributes(element) {
    return STANDARD_TARGET_ATTRS.concat(getCustomTranslatableAttributes(element));
  }

  function isTranslatableAttribute(element, name) {
    const normalizedName = String(name || "").toLowerCase();
    return STANDARD_TARGET_ATTR_SET.has(normalizedName)
      || getCustomTranslatableAttributes(element).includes(normalizedName);
  }

  function isSkippableElement(element) {
    return !element || element.nodeType !== 1 || element.matches(SKIP_SELECTOR) || Boolean(element.closest(SKIP_SELECTOR));
  }

  function splitWhitespace(value) {
    const match = String(value || "").match(/^(\s*)([\s\S]*?)(\s*)$/);
    return { leading: match ? match[1] : "", core: match ? match[2] : value, trailing: match ? match[3] : "" };
  }

  function catalogTranslation(value) {
    const source = String(value || "");
    const normalized = normalize(source);


    return source.trim() === normalized ? (exact[normalized] || "") : "";
  }

  function immediateTranslation(value) {
    const source = String(value || "");
    return catalogTranslation(source) || cache[source] || "";
  }

  function splitForTranslation(value) {
    const source = String(value || "");
    if (source.length <= MAX_TRANSLATION_UNIT) return [source];
    const units = [];
    let remaining = source;
    while (remaining.length > MAX_TRANSLATION_UNIT) {
      let cut = remaining.lastIndexOf("\n", MAX_TRANSLATION_UNIT);
      if (cut < MAX_TRANSLATION_UNIT * 0.45) cut = remaining.lastIndexOf(". ", MAX_TRANSLATION_UNIT);
      if (cut < MAX_TRANSLATION_UNIT * 0.45) cut = remaining.lastIndexOf("; ", MAX_TRANSLATION_UNIT);
      if (cut < MAX_TRANSLATION_UNIT * 0.45) cut = remaining.lastIndexOf(", ", MAX_TRANSLATION_UNIT);
      if (cut < MAX_TRANSLATION_UNIT * 0.45) cut = remaining.lastIndexOf(" ", MAX_TRANSLATION_UNIT);
      if (cut < 1) cut = MAX_TRANSLATION_UNIT;
      else if (remaining.slice(cut, cut + 2) === ". " || remaining.slice(cut, cut + 2) === "; " || remaining.slice(cut, cut + 2) === ", ") cut += 1;
      units.push(remaining.slice(0, cut));
      remaining = remaining.slice(cut);
    }
    if (remaining) units.push(remaining);
    return units;
  }

  function queueSingleTranslation(source, apply) {
    const whitespace = splitWhitespace(source);
    const originalCore = String(whitespace.core || "");
    if (!normalize(originalCore) || !shouldTranslate(originalCore)) {
      apply(source);
      return;
    }
    if (unchangedForSession.has(originalCore)) {
      apply(source);
      return;
    }
    const applyWithWhitespace = function (translated) {
      apply(whitespace.leading + translated + whitespace.trailing);
    };
    const cached = immediateTranslation(originalCore);
    if (cached) {
      applyWithWhitespace(cached);
      return;
    }
    if (!pending.has(originalCore)) pending.set(originalCore, []);
    pending.get(originalCore).push(applyWithWhitespace);
    window.clearTimeout(flushTimer);
    flushTimer = window.setTimeout(flushQueue, 80);
  }

  function queueTranslation(source, apply) {
    const parts = splitForTranslation(source);
    if (parts.length === 1) {
      queueSingleTranslation(parts[0], apply);
      return;
    }
    const results = new Array(parts.length);
    let remaining = parts.length;
    parts.forEach(function (part, index) {
      queueSingleTranslation(part, function (translated) {
        results[index] = translated;
        remaining -= 1;
        if (remaining === 0) apply(results.join(""));
      });
    });
  }

  async function flushQueue() {
    if (processing || currentLanguage !== "en" || pending.size === 0) return;
    processing = true;
    document.documentElement.classList.add("twai-language-translating");
    try {
      while (pending.size && currentLanguage === "en") {
        const batch = [];
        let batchCharacters = 0;
        for (const key of pending.keys()) {
          if (batch.length >= 50 || (batch.length > 0 && batchCharacters + key.length > 22000)) break;
          batch.push(key);
          batchCharacters += key.length;
        }
        const callbacks = {};
        batch.forEach(function (key) {
          callbacks[key] = pending.get(key) || [];
          pending.delete(key);
        });

        let results = {};
        try {
          const response = await fetch("/api/ui-language/translate", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ texts: batch, clientId: translationWorkerClientId })
          });
          const data = await response.json().catch(function () { return {}; });
          results = data && data.translations ? data.translations : {};
        } catch (_) { }

        batch.forEach(function (source) {
          const translated = String(results[source] || catalogTranslation(source) || source).trim();
          if (translated && normalize(translated).toLocaleLowerCase("en") !== normalize(source).toLocaleLowerCase("vi")) {
            cache[source] = translated;
            unchangedForSession.delete(source);
          } else {
            // Avoid sending already-English text repeatedly during the same page
            // session, but do not persist unchanged responses as translations.
            unchangedForSession.add(source);
          }
          (callbacks[source] || []).forEach(function (callback) {
            if (currentLanguage === "en") callback(translated || source);
          });
        });
        saveCache();
      }
    } finally {
      processing = false;
      document.documentElement.classList.remove("twai-language-translating");
      if (pending.size && currentLanguage === "en") flushQueue();
    }
  }

  function translateTextNode(node) {
    if (!node || node.nodeType !== Node.TEXT_NODE || !node.parentElement || isSkippableElement(node.parentElement)) return;
    const current = node.nodeValue || "";
    const lastApplied = lastAppliedText.get(node);

    if (currentLanguage === "vi") {
      if (lastApplied !== undefined && current === lastApplied && originalText.has(node)) {
        const source = originalText.get(node);
        lastAppliedText.delete(node);
        if (node.nodeValue !== source) node.nodeValue = source;
      } else {
        originalText.set(node, current);
        lastAppliedText.delete(node);
      }
      return;
    }

    if (!originalText.has(node) || (lastApplied !== undefined && current !== lastApplied)) originalText.set(node, current);
    const source = originalText.get(node) || "";
    const whitespace = splitWhitespace(source);
    if (!shouldTranslate(whitespace.core)) return;

    queueTranslation(whitespace.core, function (translated) {
      if (!node.isConnected || currentLanguage !== "en") return;
      const next = whitespace.leading + translated + whitespace.trailing;
      lastAppliedText.set(node, next);
      if (node.nodeValue !== next) node.nodeValue = next;
    });
  }

  function getOriginalAttributeMap(element) {
    let values = originalAttrs.get(element);
    if (!values) {
      values = new Map();
      originalAttrs.set(element, values);
    }
    return values;
  }

  function getAppliedAttributeMap(element) {
    let values = lastAppliedAttrs.get(element);
    if (!values) {
      values = new Map();
      lastAppliedAttrs.set(element, values);
    }
    return values;
  }

  function shouldTranslateValueAttribute(element) {
    if (!element || !/^(INPUT|BUTTON)$/i.test(element.tagName)) return false;
    return /^(button|submit|reset)$/i.test(element.type || "");
  }

  function translateAttribute(element, name) {
    if (!element || isSkippableElement(element) || !isTranslatableAttribute(element, name) || !element.hasAttribute(name)) return;

    if (name === "title" && (element.matches?.(".province, .province-islet") || element.hasAttribute("data-province-name"))) return;
    if (name === "value" && !shouldTranslateValueAttribute(element)) return;
    const current = element.getAttribute(name) || "";
    const originals = getOriginalAttributeMap(element);
    const applied = getAppliedAttributeMap(element);
    const lastApplied = applied.get(name);

    if (currentLanguage === "vi") {
      if (lastApplied !== undefined && current === lastApplied && originals.has(name)) {
        const source = originals.get(name);
        applied.delete(name);
        if (element.getAttribute(name) !== source) element.setAttribute(name, source);
      } else {
        originals.set(name, current);
        applied.delete(name);
      }
      return;
    }

    if (!originals.has(name) || (lastApplied !== undefined && current !== lastApplied)) originals.set(name, current);
    const source = originals.get(name) || "";
    if (!shouldTranslate(source)) return;
    queueTranslation(source, function (translated) {
      if (!element.isConnected || currentLanguage !== "en") return;
      applied.set(name, translated);
      if (element.getAttribute(name) !== translated) element.setAttribute(name, translated);
    });
  }

  function translateElement(element) {
    if (!element || element.nodeType !== 1 || isSkippableElement(element)) return;


    if (!/^(INPUT|TEXTAREA)$/i.test(element.tagName)) {
      Array.from(element.childNodes).forEach(function (node) {
        if (node.nodeType === Node.TEXT_NODE) translateTextNode(node);
      });
    }
    getTranslatableAttributes(element).forEach(function (name) { translateAttribute(element, name); });
  }

  function translateTree(root) {
    if (!root) return;
    applying = true;
    try {
      if (root.nodeType === Node.TEXT_NODE) translateTextNode(root);
      else if (root.nodeType === 1) {
        translateElement(root);
        root.querySelectorAll("*").forEach(translateElement);
      } else if (root === document) {
        document.querySelectorAll("*").forEach(translateElement);
      }
    } finally {
      applying = false;
    }
  }

  function restoreTree(root) {
    applying = true;
    try {
      const elements = root === document
        ? Array.from(document.querySelectorAll("*"))
        : [root].concat(Array.from(root.querySelectorAll?.("*") || []));
      elements.forEach(function (element) {
        if (!element || element.nodeType !== 1 || isSkippableElement(element)) return;
        Array.from(element.childNodes).forEach(function (node) {
          if (node.nodeType !== Node.TEXT_NODE || !originalText.has(node)) return;
          const source = originalText.get(node);
          lastAppliedText.delete(node);
          if (node.nodeValue !== source) node.nodeValue = source;
        });
        const originals = originalAttrs.get(element);
        if (originals) originals.forEach(function (value, name) {
          if (element.hasAttribute(name) && element.getAttribute(name) !== value) element.setAttribute(name, value);
        });
        lastAppliedAttrs.delete(element);
      });
    } finally {
      applying = false;
    }
  }

  function updateButton() {
    const button = document.getElementById("travelwaiLanguageToggle");
    if (!button) return;

    const targetLanguage = currentLanguage === "vi" ? "en" : "vi";
    const targetLabel = targetLanguage === "en" ? "En" : "Vi";
    const accessibleLabel = targetLanguage === "en"
      ? "Chuyển sang tiếng Anh"
      : "Switch to Vietnamese";

    button.dataset.languageTarget = targetLanguage;
    button.textContent = targetLabel;
    button.lang = targetLanguage;
    button.classList.add("active");
    button.setAttribute("aria-label", accessibleLabel);
    button.title = accessibleLabel;
  }

  function ensureButton() {
    const host = document.getElementById("travelwaiFloatingTools");
    if (!host) return;

    ["travelwaiLanguageViButton", "travelwaiLanguageEnButton"].forEach(function (id) {
      document.getElementById(id)?.remove();
    });

    if (!document.getElementById("travelwaiLanguageToggle")) {
      const button = document.createElement("button");
      button.id = "travelwaiLanguageToggle";
      button.className = "twai-theme-toggle twai-tool-action twai-language-choice active";
      button.type = "button";
      button.setAttribute("data-no-translate", "");
      host.insertBefore(button, document.getElementById("travelwaiThemeToggle") || null);
    }

    const button = document.getElementById("travelwaiLanguageToggle");
    button?.style.setProperty("--twai-tool-index", "2");
    updateButton();
  }

  function setLanguage(language) {
    const requested = language === "en" ? "en" : "vi";
    currentLanguage = requested;
    saveLanguage(currentLanguage);
    document.documentElement.lang = currentLanguage;
    document.documentElement.setAttribute("data-travelwai-language", currentLanguage);
    pending.clear();

    if (currentLanguage === "vi") {
      restoreTree(document);
    } else {
      dictionaryReady.finally(function () {
        if (currentLanguage === "en") translateTree(document);
      });
    }

    updateButton();
    syncTranslationWorkerState();
    window.dispatchEvent(new CustomEvent("travelwai:languagechange", { detail: { language: currentLanguage } }));
  }

  function bindButton() {
    if (window.__travelwaiLanguageDelegatedClickBound) return;
    window.__travelwaiLanguageDelegatedClickBound = true;
    document.addEventListener("click", function (event) {
      const button = event.target?.closest?.("#travelwaiLanguageToggle");
      if (!button) return;
      event.preventDefault();
      event.stopPropagation();
      const target = button.dataset.languageTarget || (currentLanguage === "vi" ? "en" : "vi");
      setLanguage(target);
    }, true);
  }

  function startObserver() {
    if (observer) observer.disconnect();
    observer = new MutationObserver(function (mutations) {
      if (applying) return;
      mutations.forEach(function (mutation) {
        if (mutation.type === "childList") mutation.addedNodes.forEach(translateTree);
        else if (mutation.type === "characterData") translateTextNode(mutation.target);
        else if (mutation.type === "attributes") {
          if (mutation.attributeName === TRANSLATE_ATTRS_DECLARATION) translateElement(mutation.target);
          else if (isTranslatableAttribute(mutation.target, mutation.attributeName)) {
            translateAttribute(mutation.target, mutation.attributeName);
          }
        }
      });
    });
    observer.observe(document.documentElement, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true
    });
  }

  function translateImmediate(value) {
    const source = String(value || "");
    if (currentLanguage !== "en" || !shouldTranslate(source)) return source;
    return immediateTranslation(source) || source;
  }

  function wrapNativeDialogs() {
    if (window.__travelwaiLanguageDialogsWrapped) return;
    window.__travelwaiLanguageDialogsWrapped = true;
    const nativeAlert = window.alert.bind(window);
    const nativeConfirm = window.confirm.bind(window);
    const nativePrompt = window.prompt.bind(window);
    window.alert = function (message) { return nativeAlert(translateImmediate(message)); };
    window.confirm = function (message) { return nativeConfirm(translateImmediate(message)); };
    window.prompt = function (message, defaultValue) { return nativePrompt(translateImmediate(message), defaultValue); };
  }

  document.documentElement.lang = currentLanguage;
  document.documentElement.setAttribute("data-travelwai-language", currentLanguage);
  saveLanguage(currentLanguage);

  function initializeLanguage() {
    ensureButton();
    bindButton();
    updateButton();
    startObserver();
    wrapNativeDialogs();
    syncTranslationWorkerState();

    dictionaryReady.finally(function () {
      if (currentLanguage === "en") translateTree(document);
      updateButton();
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initializeLanguage, { once: true });
  } else {
    initializeLanguage();
  }
  window.addEventListener("pageshow", function () {
    ensureButton();
    updateButton();
    syncTranslationWorkerState();
  });
  window.addEventListener("pagehide", function () {
    window.clearInterval(workerHeartbeatTimer);
    workerHeartbeatTimer = 0;
    reportTranslationWorkerState(false, true);
  });

  window.TravelwAILanguage = {
    get: function () { return currentLanguage; },
    set: setLanguage,
    ready: dictionaryReady,
    getCatalog: function () { return languageCatalog; },
    translate: function (root) {
      dictionaryReady.finally(function () { translateTree(root || document); });
    },
    translateText: function (value, callback) {
      if (currentLanguage !== "en" || !shouldTranslate(value)) {
        if (typeof callback === "function") callback(value);
        return;
      }
      queueTranslation(value, function (translated) {
        if (typeof callback === "function") callback(translated);
      });
    }
  };
})();
