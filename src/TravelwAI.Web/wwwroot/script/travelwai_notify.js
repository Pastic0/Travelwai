(function () {
  const STACK_ID = "travelwaiNotifyStack";
  const DIALOG_ID = "travelwaiNotifyDialog";

  function ready(callback) {
    if (document.body) {
      callback();
      return;
    }
    document.addEventListener("DOMContentLoaded", callback, { once: true });
  }

  function ensureStack() {
    let stack = document.getElementById(STACK_ID);
    if (stack) return stack;
    stack = document.createElement("div");
    stack.id = STACK_ID;
    stack.className = "twai-notify-stack";
    stack.setAttribute("aria-live", "polite");
    stack.setAttribute("aria-label", "Thông báo TravelwAI");
    document.body.appendChild(stack);
    return stack;
  }

  function normalizeType(type) {
    const value = String(type || "info").toLowerCase();
    if (["success", "error", "warning", "info"].includes(value)) return value;
    return "info";
  }

  function notify(message, type, timeout) {
    const text = String(message || "").trim();
    if (!text) return;

    ready(() => {
      const stack = ensureStack();
      const item = document.createElement("div");
      item.className = `twai-notify-item ${normalizeType(type)}`;
      item.innerHTML = `
        <div class="twai-notify-dot" aria-hidden="true"></div>
        <div class="twai-notify-message"></div>
        <button class="twai-notify-close" type="button" aria-label="Đóng">×</button>
      `;
      item.querySelector(".twai-notify-message").textContent = text;
      const close = () => {
        item.classList.remove("show");
        window.setTimeout(() => item.remove(), 180);
      };
      item.querySelector(".twai-notify-close").addEventListener("click", close);
      stack.appendChild(item);
      requestAnimationFrame(() => item.classList.add("show"));
      window.setTimeout(close, Number(timeout) > 0 ? Number(timeout) : 3200);
    });
  }

  function closeDialog(resolve, value) {
    const dialog = document.getElementById(DIALOG_ID);
    if (dialog) {
      dialog.classList.remove("open");
      window.setTimeout(() => dialog.remove(), 140);
    }
    resolve(value);
  }

  function showDialog(options) {
    const config = options || {};
    return new Promise((resolve) => {
      ready(() => {
        const existed = document.getElementById(DIALOG_ID);
        if (existed) existed.remove();

        const dialog = document.createElement("div");
        dialog.id = DIALOG_ID;
        dialog.className = "twai-notify-dialog";
        const isPrompt = config.mode === "prompt";
        dialog.innerHTML = `
          <div class="twai-notify-card" role="dialog" aria-modal="true" aria-labelledby="twaiNotifyTitle">
            <button class="twai-notify-x" type="button" aria-label="Đóng">×</button>
            <div class="twai-notify-title" id="twaiNotifyTitle">${isPrompt ? "Nhập thông tin" : "Xác nhận"}</div>
            <div class="twai-notify-text"></div>
            ${isPrompt ? '<input class="twai-notify-input" type="text" autocomplete="off" />' : ""}
            <div class="twai-notify-actions">
              <button class="twai-notify-btn soft" type="button" data-action="cancel">Hủy</button>
              <button class="twai-notify-btn primary" type="button" data-action="ok">${isPrompt ? "Lưu" : "Đồng ý"}</button>
            </div>
          </div>
        `;
        dialog.querySelector(".twai-notify-text").textContent = String(config.message || "").trim();
        document.body.appendChild(dialog);

        const input = dialog.querySelector(".twai-notify-input");
        if (input) {
          input.value = config.defaultValue || "";
          input.addEventListener("keydown", (event) => {
            if (event.key === "Enter") closeDialog(resolve, input.value);
            if (event.key === "Escape") closeDialog(resolve, null);
          });
        }

        dialog.querySelector("[data-action='cancel']").addEventListener("click", () => closeDialog(resolve, isPrompt ? null : false));
        dialog.querySelector("[data-action='ok']").addEventListener("click", () => closeDialog(resolve, isPrompt ? (input?.value || "") : true));
        dialog.querySelector(".twai-notify-x").addEventListener("click", () => closeDialog(resolve, isPrompt ? null : false));
        dialog.addEventListener("click", (event) => {
          if (event.target === dialog) closeDialog(resolve, isPrompt ? null : false);
        });
        document.addEventListener("keydown", function onKeyDown(event) {
          if (event.key !== "Escape") return;
          document.removeEventListener("keydown", onKeyDown);
          closeDialog(resolve, isPrompt ? null : false);
        });

        requestAnimationFrame(() => dialog.classList.add("open"));
        if (input) window.setTimeout(() => input.focus(), 80);
      });
    });
  }

  function confirmBox(message) {
    return showDialog({ mode: "confirm", message });
  }

  function promptBox(message, defaultValue) {
    return showDialog({ mode: "prompt", message, defaultValue });
  }

  window.TravelwAIToast = notify;
  window.TravelwAIAlert = notify;
  window.TravelwAIConfirm = confirmBox;
  window.TravelwAIPrompt = promptBox;
  window.showTravelwAINotification = notify;
  window.alert = function (message) {
    notify(message, "info");
  };
})();
