(function () {
  "use strict";

  const CACHE_KEY = "travelwaiAiTranslationCacheV4";
  const MAX_CACHE_ITEMS = 300;
  let translationCache = loadTranslationCache();
  const conversationControlState = new WeakMap();
  const translationRequests = new Map();


  function readCookie(name) {
    try {
      const value = `; ${document.cookie || ""}`;
      const parts = value.split(`; ${name}=`);
      return parts.length === 2 ? decodeURIComponent(parts.pop().split(";").shift() || "") : "";
    } catch (_) {
      return "";
    }
  }

  function getToken() {
    return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || readCookie("TravelwAIAuth");
  }

  function normalize(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function getInterfaceLanguage() {
    const language = window.TravelwAILanguage?.get?.()
      || document.documentElement.getAttribute("data-travelwai-language")
      || document.documentElement.lang
      || "vi";
    return String(language).toLowerCase().startsWith("en") ? "en" : "vi";
  }

  function resolveTargetLanguage(element, button) {
    const requested = String(
      button?.dataset?.aiTranslationTarget
      || element?.dataset?.aiTranslationTarget
      || "vi"
    ).toLowerCase();
    if (requested === "interface" || requested === "current") return getInterfaceLanguage();
    return requested === "en" ? "en" : "vi";
  }

  function getLabels(targetLanguage) {
    const target = targetLanguage === "en" ? "en" : "vi";
    if (getInterfaceLanguage() === "en") {
      return {
        visible: "Translate",
        loading: "AI is translating",
        translated: "Show original content",
        original: target === "en" ? "Translate into English" : "Translate into Vietnamese",
        noContent: "There is no content to translate.",
        loginRequired: "Sign in to use AI translation.",
        failed: "This content cannot be translated right now.",
        empty: "AI did not return a translation.",
        alreadyTarget: target === "en" ? "This content is already in English." : "This content is already in Vietnamese."
      };
    }
    return {
      visible: "Dịch",
      loading: "AI đang dịch",
      translated: "Hiện nội dung gốc",
      original: target === "en" ? "Dịch sang tiếng Anh" : "Dịch sang tiếng Việt",
      noContent: "Không có nội dung để dịch.",
      loginRequired: "Bạn cần đăng nhập để dùng dịch AI.",
      failed: "Không thể dịch nội dung lúc này.",
      empty: "AI không trả về bản dịch.",
      alreadyTarget: target === "en" ? "Nội dung này đã là tiếng Anh." : "Nội dung này đã là tiếng Việt."
    };
  }

  function loadTranslationCache() {
    try {
      const parsed = JSON.parse(sessionStorage.getItem(CACHE_KEY) || "{}");
      return parsed && typeof parsed === "object" ? parsed : {};
    } catch (_) {
      return {};
    }
  }

  function saveTranslationCache() {
    try {
      const entries = Object.entries(translationCache);
      if (entries.length > MAX_CACHE_ITEMS) {
        translationCache = Object.fromEntries(entries.slice(-MAX_CACHE_ITEMS));
      }
      sessionStorage.setItem(CACHE_KEY, JSON.stringify(translationCache));
    } catch (_) { }
  }

  function hashText(value) {
    let hash = 2166136261;
    const source = String(value || "");
    for (let index = 0; index < source.length; index += 1) {
      hash ^= source.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16);
  }

  function stripTargetSuffix(value) {
    return String(value || "").replace(/\|target:(?:vi|en)$/i, "");
  }

  function getBaseTranslationKey(element, button, original) {
    const explicit = String(
      element?.dataset?.aiTranslationBaseKey
      || element?.dataset?.aiTranslationKey
      || button?.dataset?.aiTranslationBaseKey
      || button?.dataset?.aiTranslationKey
      || ""
    ).trim();
    return stripTargetSuffix(explicit) || `text:${hashText(normalize(original).toLocaleLowerCase("vi"))}`;
  }

  function getTranslationKey(element, button, original, targetLanguage) {
    return `${getBaseTranslationKey(element, button, original)}|target:${targetLanguage === "en" ? "en" : "vi"}`;
  }

  function readCachedTranslation(key, original, targetLanguage) {
    const item = translationCache[key];
    if (!item || typeof item !== "object") return null;
    if (String(item.source || "") !== String(original || "")) return null;
    if (String(item.targetLanguage || targetLanguage) !== String(targetLanguage)) return null;
    const translation = String(item.translation || "").trim();
    return translation ? {
      source: String(item.source || original),
      translation,
      targetLanguage,
      visible: item.visible === true
    } : null;
  }

  function writeCachedTranslation(key, original, translation, targetLanguage, visible) {
    if (!key || !translation) return;
    translationCache[key] = {
      source: String(original || ""),
      translation: String(translation || ""),
      targetLanguage: targetLanguage === "en" ? "en" : "vi",
      visible: visible === true,
      updatedAt: Date.now()
    };
    saveTranslationCache();
  }

  function updateCachedVisibility(key, visible) {
    if (!key || !translationCache[key]) return;
    translationCache[key].visible = visible === true;
    translationCache[key].updatedAt = Date.now();
    saveTranslationCache();
  }

  function showMessage(message, type) {
    if (typeof window.TravelwAIToast === "function") {
      window.TravelwAIToast(message, type || "info");
      return;
    }
    if (message) window.alert(message);
  }

  async function translateText(text, targetLanguage) {
    const target = targetLanguage === "en" ? "en" : "vi";
    const labels = getLabels(target);
    const source = String(text || "").trim();
    if (!source) throw new Error(labels.noContent);

    const token = getToken();
    if (!token) throw new Error(labels.loginRequired);

    const requestKey = `${target}:${hashText(source)}`;
    if (translationRequests.has(requestKey)) return translationRequests.get(requestKey);

    const request = (async function () {
      const response = await fetch("/api/ai/translate", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${token}`,
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ text: source, targetLanguage: target })
      });
      const result = await response.json().catch(function () { return {}; });
      if (!response.ok || result.success === false) {
        const serverMessage = String(result.message || result.detail || "").trim();
        throw new Error(getInterfaceLanguage() === "vi" && serverMessage ? serverMessage : labels.failed);
      }

      const translated = String(result.translation || "").trim();
      if (!translated) throw new Error(labels.empty);
      return translated;
    })();

    translationRequests.set(requestKey, request);
    try {
      return await request;
    } finally {
      translationRequests.delete(requestKey);
    }
  }

  function isConversationControl(button) {
    return button?.dataset?.aiTranslateMode === "conversation";
  }

  function getConversationControlLabels(targetLanguage) {
    const target = targetLanguage === "en" ? "en" : "vi";
    if (getInterfaceLanguage() === "en") {
      return {
        visible: "Translate",
        loading: `Translating all messages into ${target === "en" ? "English" : "Vietnamese"}`,
        enabled: "Turn off message translation",
        disabled: `Translate all messages into ${target === "en" ? "English" : "Vietnamese"}`
      };
    }
    return {
      visible: "Dịch",
      loading: `Đang dịch toàn bộ tin nhắn sang ${target === "en" ? "tiếng Anh" : "tiếng Việt"}`,
      enabled: "Tắt dịch tin nhắn",
      disabled: `Dịch toàn bộ tin nhắn sang ${target === "en" ? "tiếng Anh" : "tiếng Việt"}`
    };
  }

  function setButtonState(button, state, targetLanguage) {
    if (!button) return;
    const target = targetLanguage || resolveTargetLanguage(null, button);
    const loading = state === "loading";
    const translated = state === "translated";
    const unavailable = button.dataset.aiTranslationAvailable === "false";

    if (isConversationControl(button)) {
      const labels = getConversationControlLabels(target);


      button.disabled = unavailable;
      button.classList.toggle("is-loading", loading);
      button.classList.toggle("is-translated", translated || loading);
      button.setAttribute("aria-busy", loading ? "true" : "false");
      button.setAttribute("aria-pressed", translated || loading ? "true" : "false");
      button.dataset.aiTranslationResolvedTarget = target;
      button.dataset.aiTranslationEnabled = translated || loading ? "true" : "false";
      button.title = loading ? labels.loading : translated ? labels.enabled : labels.disabled;
      button.setAttribute("aria-label", button.title);
      const label = button.querySelector("[data-ai-translate-label]");
      if (label) label.textContent = labels.visible;
      return;
    }

    const labels = getLabels(target);
    button.disabled = loading || unavailable;
    button.classList.toggle("is-loading", loading);
    button.classList.toggle("is-translated", translated);
    button.setAttribute("aria-busy", loading ? "true" : "false");
    button.setAttribute("aria-pressed", translated ? "true" : "false");
    button.dataset.aiTranslationResolvedTarget = target;
    button.title = loading ? labels.loading : translated ? labels.translated : labels.original;
    button.setAttribute("aria-label", button.title);
    const label = button.querySelector("[data-ai-translate-label]");
    if (label) label.textContent = labels.visible;
  }

  function findAssociatedButton(element, baseKey) {
    const container = element?.closest?.(".post-comment-item, .message-wrapper, .admin-support-message-row") || element?.parentElement;
    const localButton = container?.querySelector?.("[data-ai-translate-control]");
    if (localButton) return localButton;

    return Array.from(document.querySelectorAll("[data-ai-translate-control]")).find(function (button) {
      return stripTargetSuffix(button.dataset.aiTranslationKey || "") === baseKey;
    }) || null;
  }

  function restoreOriginalElement(element, button) {
    if (!element || !element.dataset.aiOriginalText) return false;
    const original = element.dataset.aiOriginalText;
    const key = element.dataset.aiTranslationCacheKey || "";
    element.textContent = original;
    element.dataset.aiTranslationVisible = "false";
    if (key) updateCachedVisibility(key, false);
    const control = button || findAssociatedButton(element, getBaseTranslationKey(element, button, original));
    if (control) setButtonState(control, "original");
    return true;
  }

  function restoreOriginalTree(root) {
    if (!root) return 0;
    const elements = [];
    if (root.nodeType === 1 && root.dataset?.aiTranslationVisible === "true") elements.push(root);
    if (root.querySelectorAll) elements.push(...root.querySelectorAll('[data-ai-translation-visible="true"]'));
    let restored = 0;
    elements.forEach(function (element) {
      if (restoreOriginalElement(element)) restored += 1;
    });
    if (root.querySelectorAll) {
      root.querySelectorAll("[data-ai-translate-control]").forEach(function (button) {
        setButtonState(button, "original");
      });
    }
    return restored;
  }

  function hydrateTextElement(element, button) {
    if (!element || element.nodeType !== 1 || /^(BUTTON|INPUT|TEXTAREA|SELECT)$/i.test(element.tagName)) return false;


    if (element.hasAttribute("data-chat-message-text")) return false;
    const control = button || findAssociatedButton(element, getBaseTranslationKey(element, button, element.textContent || ""));
    const target = resolveTargetLanguage(element, control);
    const original = String(element.dataset.aiOriginalText || element.textContent || "");
    const baseKey = getBaseTranslationKey(element, control, original);
    const key = `${baseKey}|target:${target}`;
    const cached = readCachedTranslation(key, original, target);
    if (!cached) return false;

    element.dataset.aiOriginalText = cached.source;
    element.dataset.aiTranslatedText = cached.translation;
    if (target === "vi") element.dataset.aiVietnameseText = cached.translation;
    element.dataset.aiTranslationBaseKey = baseKey;
    element.dataset.aiTranslationKey = key;
    element.dataset.aiTranslationCacheKey = key;
    element.dataset.aiTranslationResolvedTarget = target;
    element.dataset.aiTranslationVisible = cached.visible ? "true" : "false";
    element.textContent = cached.visible ? cached.translation : cached.source;

    if (control) {
      control.dataset.aiTranslationBaseKey = baseKey;
      control.dataset.aiTranslationKey = key;
      setButtonState(control, cached.visible ? "translated" : "original", target);
    }
    return true;
  }

  function hydrateTree(root) {
    if (!root) return;
    const candidates = [];
    if (root.nodeType === 1 && root.hasAttribute?.("data-ai-translation-key")) candidates.push(root);
    if (root.querySelectorAll) candidates.push(...root.querySelectorAll("[data-ai-translation-key]"));
    candidates.forEach(function (element) {
      if (!element.hasAttribute("data-ai-translate-control")) hydrateTextElement(element);
    });
  }

  async function toggleTextElement(element, button) {
    if (!element) {
      showMessage(getLabels(resolveTargetLanguage(null, button)).noContent, "info");
      return false;
    }

    const target = resolveTargetLanguage(element, button);
    const previousTarget = element.dataset.aiTranslationResolvedTarget || "";
    if (element.dataset.aiTranslationVisible === "true" && previousTarget && previousTarget !== target) {
      restoreOriginalElement(element, button);
    }

    hydrateTextElement(element, button);

    if (!element.dataset.aiOriginalText) {
      element.dataset.aiOriginalText = element.textContent || "";
    }

    const original = element.dataset.aiOriginalText || "";
    const baseKey = getBaseTranslationKey(element, button, original);
    const key = `${baseKey}|target:${target}`;
    element.dataset.aiTranslationBaseKey = baseKey;
    element.dataset.aiTranslationKey = key;
    element.dataset.aiTranslationCacheKey = key;
    element.dataset.aiTranslationResolvedTarget = target;
    if (button) {
      button.dataset.aiTranslationBaseKey = baseKey;
      button.dataset.aiTranslationKey = key;
    }

    const isTranslated = element.dataset.aiTranslationVisible === "true"
      && element.dataset.aiTranslationResolvedTarget === target;
    if (isTranslated) {
      restoreOriginalElement(element, button);
      return false;
    }

    const cached = readCachedTranslation(key, original, target);
    const cachedTranslation = previousTarget === target
      ? (element.dataset.aiTranslatedText || (target === "vi" ? element.dataset.aiVietnameseText : "") || cached?.translation || "")
      : (cached?.translation || "");
    if (cachedTranslation) {
      element.dataset.aiTranslatedText = cachedTranslation;
      if (target === "vi") element.dataset.aiVietnameseText = cachedTranslation;
      element.dataset.aiTranslationResolvedTarget = target;
      element.textContent = cachedTranslation;
      element.dataset.aiTranslationVisible = "true";
      writeCachedTranslation(key, original, cachedTranslation, target, true);
      setButtonState(button, "translated", target);
      return true;
    }

    setButtonState(button, "loading", target);
    try {
      const translated = await translateText(original, target);
      if (normalize(translated).toLocaleLowerCase(target) === normalize(original).toLocaleLowerCase(target)) {
        setButtonState(button, "original", target);
        showMessage(getLabels(target).alreadyTarget, "info");
        return false;
      }
      element.dataset.aiTranslatedText = translated;
      if (target === "vi") element.dataset.aiVietnameseText = translated;
      element.dataset.aiTranslationResolvedTarget = target;
      element.dataset.aiTranslationVisible = "true";
      element.textContent = translated;
      writeCachedTranslation(key, original, translated, target, true);
      setButtonState(button, "translated", target);
      return true;
    } catch (error) {
      setButtonState(button, "original", target);
      showMessage(error?.message || getLabels(target).failed, "error");
      return false;
    }
  }

  function getConversationRoot(button) {
    const selector = String(button?.dataset?.aiTranslationList || "").trim();
    if (selector) {
      try { return document.querySelector(selector); } catch (_) { return null; }
    }
    return button?.closest?.("form, .message-input, .twai-chatbot-form")
      ?.parentElement?.querySelector?.("[data-chat-message-list]") || null;
  }

  function getConversationTextElements(root) {
    if (!root) return [];
    const elements = [];
    if (root.nodeType === 1 && root.hasAttribute?.("data-chat-message-text")) elements.push(root);
    if (root.querySelectorAll) elements.push(...root.querySelectorAll("[data-chat-message-text]"));
    return elements.filter(function (element) {
      return String(element.dataset.aiOriginalText || element.textContent || "").trim();
    });
  }

  function getMessageSourceText(element) {
    if (!element) return "";

    if (element.dataset.aiTranslationVisible === "true") restoreOriginalElement(element);
    const source = String(element.dataset.aiOriginalText || element.textContent || "");
    element.dataset.aiOriginalText = source;
    element.dataset.aiTranslationVisible = "false";
    return source;
  }

  function getMessageTranslationLine(element) {
    const next = element?.nextElementSibling;
    return next?.classList?.contains("ai-message-translation") ? next : null;
  }

  function removeMessageTranslationLine(element) {
    getMessageTranslationLine(element)?.remove();
    if (element) {
      delete element.dataset.aiMessageTranslationTarget;
      delete element.dataset.aiMessageTranslationVisible;
    }
  }

  function renderMessageTranslationLine(element, translated, targetLanguage) {
    if (!element) return;
    const clean = String(translated || "").trim();
    if (!clean) {
      removeMessageTranslationLine(element);
      return;
    }
    let line = getMessageTranslationLine(element);
    if (!line) {
      line = document.createElement("div");
      line.className = "ai-message-translation";
      line.setAttribute("data-no-translate", "");
      line.setAttribute("aria-label", getInterfaceLanguage() === "en" ? "Translated message" : "Bản dịch tin nhắn");
      element.insertAdjacentElement("afterend", line);
    }
    line.textContent = clean;
    line.dataset.aiTranslationTarget = targetLanguage;
    element.dataset.aiMessageTranslationTarget = targetLanguage;
    element.dataset.aiMessageTranslationVisible = "true";
  }

  function clearConversationTranslations(root) {
    if (!root) return;
    root.querySelectorAll?.(".ai-message-translation").forEach(function (line) { line.remove(); });
    getConversationTextElements(root).forEach(function (element) {
      delete element.dataset.aiMessageTranslationTarget;
      delete element.dataset.aiMessageTranslationVisible;

    });
  }

  function getConversationState(button) {
    let state = conversationControlState.get(button);
    if (!state) {
      state = { enabled: false, generation: 0, observer: null };
      conversationControlState.set(button, state);
    }
    return state;
  }

  async function translateMessageToLine(element, button, generation) {
    const state = getConversationState(button);
    if (!state.enabled || state.generation !== generation || !element?.isConnected) return false;

    const target = resolveTargetLanguage(element, button);
    const original = getMessageSourceText(element);
    if (!original.trim()) return false;

    const baseKey = getBaseTranslationKey(element, button, original);
    const key = `${baseKey}|target:${target}`;
    const cached = readCachedTranslation(key, original, target);
    let translated = cached?.translation || "";

    try {
      if (!translated) {
        translated = await translateText(original, target);
        writeCachedTranslation(key, original, translated, target, false);
      }
    } catch (error) {

      console.warn("Không thể dịch tin nhắn:", error);
      return false;
    }

    if (!state.enabled || state.generation !== generation || !element.isConnected) return false;
    if (normalize(translated).toLocaleLowerCase(target) === normalize(original).toLocaleLowerCase(target)) {
      removeMessageTranslationLine(element);
      element.dataset.aiMessageTranslationTarget = target;
      return false;
    }

    renderMessageTranslationLine(element, translated, target);
    return true;
  }

  async function translateConversationElements(button, elements) {
    const state = getConversationState(button);
    if (!state.enabled) return;
    const generation = state.generation;
    const unique = Array.from(new Set(elements || [])).filter(Boolean);
    let cursor = 0;
    const workerCount = Math.min(3, unique.length);

    async function worker() {
      while (cursor < unique.length && state.enabled && state.generation === generation) {
        const element = unique[cursor++];
        const target = resolveTargetLanguage(element, button);
        const hasCurrentLine = element.dataset.aiMessageTranslationVisible === "true"
          && element.dataset.aiMessageTranslationTarget === target
          && getMessageTranslationLine(element);
        if (!hasCurrentLine) await translateMessageToLine(element, button, generation);
      }
    }

    await Promise.all(Array.from({ length: workerCount }, worker));
    if (state.enabled && state.generation === generation) setButtonState(button, "translated");
  }

  async function refreshConversationControl(button, rootOrNode) {
    if (!button || !isConversationControl(button)) return;
    const root = getConversationRoot(button);
    const allElements = getConversationTextElements(root);
    button.dataset.aiTranslationAvailable = allElements.length ? "true" : "false";

    const state = getConversationState(button);
    if (!state.enabled) {
      setButtonState(button, "original");
      return;
    }

    const target = resolveTargetLanguage(null, button);
    if (button.dataset.aiTranslationResolvedTarget && button.dataset.aiTranslationResolvedTarget !== target) {
      state.generation += 1;
      clearConversationTranslations(root);
    }

    const scoped = rootOrNode ? getConversationTextElements(rootOrNode) : allElements;
    const elements = scoped.length ? scoped : allElements;
    if (!elements.length) {
      setButtonState(button, "translated", target);
      return;
    }
    setButtonState(button, "loading", target);
    await translateConversationElements(button, elements);
  }

  async function toggleConversationTranslation(button) {
    if (!button || !isConversationControl(button)) return false;
    const root = getConversationRoot(button);
    const elements = getConversationTextElements(root);
    button.dataset.aiTranslationAvailable = elements.length ? "true" : "false";
    if (!elements.length) {
      setButtonState(button, "original");
      showMessage(getLabels(resolveTargetLanguage(null, button)).noContent, "info");
      return false;
    }

    const state = getConversationState(button);
    if (state.enabled) {
      state.enabled = false;
      state.generation += 1;
      clearConversationTranslations(root);
      setButtonState(button, "original");
      return false;
    }

    state.enabled = true;
    state.generation += 1;
    setButtonState(button, "loading");
    await translateConversationElements(button, elements);
    return state.enabled;
  }

  function initializeConversationControl(button) {
    if (!button || !isConversationControl(button) || button.dataset.aiTranslationInitialized === "true") return;
    button.dataset.aiTranslationInitialized = "true";
    const state = getConversationState(button);
    const root = getConversationRoot(button);
    button.addEventListener("click", function () { toggleConversationTranslation(button); });

    if (root && typeof MutationObserver === "function") {
      state.observer = new MutationObserver(function (mutations) {
        const added = [];
        mutations.forEach(function (mutation) {
          mutation.addedNodes.forEach(function (node) {
            if (node.nodeType === 1) added.push(...getConversationTextElements(node));
          });
        });
        const all = getConversationTextElements(root);
        button.dataset.aiTranslationAvailable = all.length ? "true" : "false";
        if (state.enabled && added.length) refreshConversationControl(button, root);
        else if (!state.enabled) setButtonState(button, "original");
      });
      state.observer.observe(root, { childList: true, subtree: true });
    }
    refreshConversationControl(button);
  }

  function refreshControlLabels() {
    document.querySelectorAll("[data-ai-translate-control]").forEach(function (button) {
      const target = resolveTargetLanguage(null, button);
      const state = button.classList.contains("is-loading")
        ? "loading"
        : button.getAttribute("aria-pressed") === "true"
          ? "translated"
          : "original";
      setButtonState(button, state, target);
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    hydrateTree(document);
    document.querySelectorAll('[data-ai-translate-mode="conversation"]').forEach(initializeConversationControl);
    refreshControlLabels();

    const observer = new MutationObserver(function (mutations) {
      mutations.forEach(function (mutation) {
        mutation.addedNodes.forEach(function (node) {
          hydrateTree(node);
          if (node.nodeType === 1) {
            if (node.matches?.('[data-ai-translate-mode="conversation"]')) initializeConversationControl(node);
            node.querySelectorAll?.('[data-ai-translate-mode="conversation"]').forEach(initializeConversationControl);
          }
        });
      });
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });

  window.addEventListener("travelwai:languagechange", function () {


    restoreOriginalTree(document);
    document.querySelectorAll('[data-ai-translate-mode="conversation"]').forEach(function (button) {
      const state = getConversationState(button);
      if (state.enabled) {
        state.generation += 1;
        clearConversationTranslations(getConversationRoot(button));
        refreshConversationControl(button);
      } else {
        setButtonState(button, "original");
      }
    });
    refreshControlLabels();
  });

  window.TravelwAITranslation = Object.freeze({
    translateText: translateText,
    translateToVietnamese: function (text) { return translateText(text, "vi"); },
    toggleTextElement: toggleTextElement,
    toggleConversationTranslation: toggleConversationTranslation,
    refreshConversationControl: refreshConversationControl,
    clearConversationTranslations: clearConversationTranslations,
    hydrateTextElement: hydrateTextElement,
    restoreOriginalElement: restoreOriginalElement,
    restoreOriginalTree: restoreOriginalTree,
    setButtonState: setButtonState
  });
})();
