(function () {
  "use strict";

  function normalizeText(value) {
    return String(value || "")
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/đ/g, "d")
      .replace(/[^a-z0-9\s]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function clampText(value, maxLength = 3400) {
    const text = String(value || "")
      .replace(/[\u0000-\u001F\u007F]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
    return text.length > maxLength ? text.slice(0, maxLength).trim() : text;
  }

  window.TravelwAIGuideChatbot = {
    normalizeText,
    clampText,
    buildContextForMessage: function () {
      return "";
    }
  };
})();
