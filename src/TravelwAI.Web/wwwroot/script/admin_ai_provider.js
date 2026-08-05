(() => {
  "use strict";

  let currentProvider = "ollama";
  let switching = false;

  function toast(message) {
    if (typeof window.showToast === "function") window.showToast(message);
    else console.info(message);
  }

  async function readResponse(response) {
    if (typeof window.readAdminJson === "function") return window.readAdminJson(response);
    const result = await response.json().catch(() => ({}));
    if (!response.ok || result?.success === false) throw new Error(result?.message || "Không cập nhật được nhà cung cấp AI.");
    return result;
  }

  function updateButton(data) {
    const button = document.getElementById("switchAiProviderButton");
    if (!button) return;

    currentProvider = String(data?.provider || "ollama").toLowerCase() === "openrouter" ? "openrouter" : "ollama";
    const isOpenRouter = currentProvider === "openrouter";
    const model = String(data?.model || (isOpenRouter ? data?.openRouterModel : data?.ollamaModel) || "").trim();
    const openRouterConfigured = data?.openRouterConfigured !== false;
    const nextName = isOpenRouter ? "Ollama" : "OpenRouter";
    const currentName = isOpenRouter ? "OpenRouter" : "Ollama";

    button.classList.toggle("is-openrouter", isOpenRouter);
    button.classList.toggle("is-ollama", !isOpenRouter);
    button.setAttribute("aria-pressed", isOpenRouter ? "true" : "false");
    button.dataset.provider = currentProvider;
    button.dataset.model = model;

    const configurationNote = !isOpenRouter && !openRouterConfigured
      ? " OpenRouter chưa có OPENROUTER_API_KEY trên Render."
      : "";
    const title = `AI đang dùng ${currentName}${model ? ` (${model})` : ""}. Bấm để chuyển sang ${nextName}.${configurationNote}`;
    button.title = title;
    button.setAttribute("aria-label", title);
  }

  async function loadAiProvider() {
    const button = document.getElementById("switchAiProviderButton");
    if (!button) return;
    try {
      const fetcher = window.authenticatedFetch || window.fetch.bind(window);
      const response = await fetcher("/api/admin/ai-provider", { cache: "no-store" });
      const result = await readResponse(response);
      updateButton(result?.data || {});
    } catch (error) {
      button.title = error?.message || "Không đọc được nhà cung cấp AI.";
      button.setAttribute("aria-label", button.title);
      console.error("Không đọc được nhà cung cấp AI:", error);
    }
  }

  async function switchAiProvider() {
    if (switching) return;
    const button = document.getElementById("switchAiProviderButton");
    if (!button) return;

    switching = true;
    button.disabled = true;
    button.classList.add("is-switching");
    button.setAttribute("aria-busy", "true");

    try {
      const targetProvider = currentProvider === "openrouter" ? "ollama" : "openrouter";
      const fetcher = window.authenticatedFetch || window.fetch.bind(window);
      const response = await fetcher("/api/admin/ai-provider", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ provider: targetProvider })
      });
      const result = await readResponse(response);
      updateButton(result?.data || { provider: targetProvider });
      toast(result?.message || "Đã chuyển nhà cung cấp AI.");
      window.dispatchEvent(new CustomEvent("travelwai:ai-provider-changed", { detail: result?.data || {} }));
    } catch (error) {
      toast(error?.message || "Không chuyển được nhà cung cấp AI.");
    } finally {
      switching = false;
      button.disabled = false;
      button.classList.remove("is-switching");
      button.removeAttribute("aria-busy");
    }
  }

  function bind() {
    const button = document.getElementById("switchAiProviderButton");
    if (!button || button.dataset.aiProviderBound === "true") return;
    button.dataset.aiProviderBound = "true";
    button.addEventListener("click", switchAiProvider);
    loadAiProvider();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind, { once: true });
  else bind();
})();
