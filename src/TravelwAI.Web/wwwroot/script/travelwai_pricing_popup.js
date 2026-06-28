(function () {
  const DEFAULT_PLANS = [
    { role: "Free", name: "Free", price: "0đ", subtitle: "Dùng thử cơ bản", note: "Miễn phí", cta: "Bắt đầu miễn phí", requiresPayment: false, benefits: ["Xem bản đồ Việt Nam, bài viết và tour du lịch", "Nhắn tin thường và xem thông báo", "Không dùng AI tạo bài viết", "Không lập lịch trình", "Không dùng ưu đãi bài viết", "Chatbot AI 3 câu hỏi trong 5 phút"] },
    { role: "VIP", name: "VIP", price: "59.000đ", subtitle: "Có AI và lịch trình", note: "Theo tháng", cta: "Nâng cấp VIP", requiresPayment: true, benefits: ["Xem bản đồ, bài viết và tour", "AI tạo bài viết", "Lập lịch trình", "Không dùng ưu đãi bài viết", "Chatbot AI 10 câu hỏi trong 5 phút"] },
    { role: "Premium", name: "Premium", price: "129.000đ", subtitle: "Không giới hạn", note: "Đầy đủ", cta: "Nâng cấp Premium", requiresPayment: true, benefits: ["Đầy đủ tính năng của VIP", "Ưu đãi bài viết", "Chatbot AI không giới hạn", "Không giới hạn AI tạo bài viết và lập lịch trình"] },
    { role: "Sales", name: "Sales", price: "Đăng ký", subtitle: "Bán tour và nhận hoa hồng", note: "Thu phí đăng ký", cta: "Đăng ký Sales", requiresPayment: true, benefits: ["Tài khoản kinh doanh Sales", "Quản lý tour đã tạo", "Xem đơn bán tour", "Nhận hoa hồng theo cấp"] },
    { role: "Business", name: "Business", price: "Đăng ký", subtitle: "Đối tác tour và dịch vụ", note: "Thu phí đăng ký", cta: "Đăng ký Business", requiresPayment: true, benefits: ["Tài khoản kinh doanh Business", "Quản lý tour của doanh nghiệp", "Xem doanh thu Business", "Tính phí dịch vụ theo cấp"] }
  ];

  let planCache = null;
  let loadingPlans = null;

  const RETURN_URL_KEY = "travelwai-post-login-return-url";

  function readCookie(name) {
    const value = `; ${document.cookie || ""}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) return decodeURIComponent(parts.pop().split(";").shift() || "");
    return "";
  }

  function normalizeLocalReturnUrl(value, fallback = "/home") {
    const raw = String(value || "").trim();
    if (!raw) return fallback;

    try {
      const url = new URL(raw, window.location.origin);
      if (url.origin !== window.location.origin) return fallback;
      const next = `${url.pathname}${url.search}${url.hash}`;
      if (!next.startsWith("/") || next.startsWith("//")) return fallback;
      if (/^\/(login|signup|forgot-password|reset-password)(\/|\?|#|$)/i.test(next)) return fallback;
      return next;
    } catch (_) {
      if (raw.startsWith("/") && !raw.startsWith("//") && !/^\/(login|signup|forgot-password|reset-password)(\/|\?|#|$)/i.test(raw)) return raw;
      return fallback;
    }
  }

  function buildLoginUrl(returnUrl) {
    if (typeof window.buildLoginUrl === "function") return window.buildLoginUrl(returnUrl);
    const next = normalizeLocalReturnUrl(returnUrl || `${window.location.pathname}${window.location.search}${window.location.hash}`, "/home");
    return `/login?returnUrl=${encodeURIComponent(next)}`;
  }

  function redirectToLogin(returnUrl) {
    const next = normalizeLocalReturnUrl(returnUrl || `${window.location.pathname}${window.location.search}${window.location.hash}`, "/home");
    try { sessionStorage.setItem(RETURN_URL_KEY, next); } catch (_) { }
    if (typeof window.redirectToLogin === "function") {
      window.redirectToLogin(next);
      return;
    }
    window.location.href = buildLoginUrl(next);
  }

  function hasLoggedInAccount() {
    if (typeof window.isAuthenticated === "function") {
      try { if (window.isAuthenticated()) return true; } catch (_) { }
    }

    const token = localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || readCookie("TravelwAIAuth");
    if (!token) return false;

    const expiration = localStorage.getItem("tokenExpiration") || sessionStorage.getItem("tokenExpiration");
    if (!expiration) return true;

    const expirationNumber = parseInt(expiration, 10);
    return !Number.isNaN(expirationNumber) && Date.now() < expirationNumber;
  }


  function escapeHtml(value) {
    return String(value || "").replace(/[&<>"']/g, function (char) {
      return ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char] || char;
    });
  }

  function normalizeRole(value) {
    const role = String(value || "").trim().toLowerCase().replace(/[_-]+/g, " ");
    if (role === "user") return "free";
    if (role === "company") return "business";
    if (role === "tour sales" || role === "toursales") return "sales";
    return role || "free";
  }

  function getCurrentRole() {
    const localRole = localStorage.getItem("userRole") || sessionStorage.getItem("userRole") || "";
    if (localRole) return normalizeRole(localRole);
    try {
      const raw = localStorage.getItem("currentUser") || sessionStorage.getItem("currentUser") || "";
      if (raw) {
        const user = JSON.parse(raw);
        if (user?.role) return normalizeRole(user.role);
      }
    } catch (_) { }
    return "free";
  }

  function isFreeAccount() {
    return getCurrentRole() === "free";
  }

  function normalizePlan(plan, fallback) {
    const benefits = Array.isArray(plan?.benefits) ? plan.benefits : fallback.benefits;
    return {
      role: plan?.role || fallback.role,
      name: plan?.name || fallback.name,
      price: plan?.price || fallback.price,
      subtitle: plan?.subtitle || fallback.subtitle,
      note: plan?.note || fallback.note,
      cta: plan?.cta || fallback.cta,
      requiresPayment: Boolean(plan?.requiresPayment ?? plan?.requires_payment ?? fallback.requiresPayment),
      benefits: benefits.map(item => String(item || "").trim()).filter(Boolean)
    };
  }

  function normalizePlans(rows) {
    const source = Array.isArray(rows) ? rows : [];
    return DEFAULT_PLANS.map(fallback => {
      const row = source.find(item => normalizeRole(item?.role) === normalizeRole(fallback.role));
      return normalizePlan(row, fallback);
    });
  }

  async function loadPlans() {
    if (planCache) return planCache;
    if (loadingPlans) return loadingPlans;
    loadingPlans = fetch("/api/account-plans", { cache: "no-store" })
      .then(response => response.json().catch(() => ({})))
      .then(result => {
        planCache = normalizePlans(result.data || result.plans || []);
        return planCache;
      })
      .catch(() => {
        planCache = normalizePlans([]);
        return planCache;
      })
      .finally(() => { loadingPlans = null; });
    return loadingPlans;
  }

  function ensureModal() {
    let modal = document.getElementById("travelwaiPricingPopup");
    if (modal) return modal;
    modal = document.createElement("div");
    modal.id = "travelwaiPricingPopup";
    modal.className = "twai-pricing-popup";
    modal.setAttribute("aria-hidden", "true");
    modal.innerHTML = `
      <div class="twai-pricing-popup-card" role="dialog" aria-modal="true" aria-labelledby="twaiPricingPopupTitle">
        <div class="twai-pricing-heading" hidden>
          <span class="eyebrow"></span>
          <h2 id="twaiPricingPopupTitle"></h2>
          <p id="twaiPricingPopupReason"></p>
        </div>
        <div class="twai-pricing-grid" id="twaiPricingPopupGrid"></div>
        <div class="twai-pricing-footer">
          <button class="twai-pricing-bottom-close" type="button">Đóng</button>
        </div>
      </div>`;
    document.body.appendChild(modal);
    modal.addEventListener("click", function (event) {
      if (event.target === modal || event.target.closest(".twai-pricing-bottom-close")) closePricingPopup();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && modal.classList.contains("open")) closePricingPopup();
    });
    return modal;
  }

  function planCtaHref(plan) {
    const role = normalizeRole(plan.role);
    if (role === "free") return "/home?plan=free";
    if (role === "vip") return "/checkout?plan=VIP";
    if (role === "premium") return "/checkout?plan=Premium";
    return `${window.location.pathname}${window.location.search}${window.location.hash}` || "/home";
  }

  function planDisplayRole(plan) {
    const role = normalizeRole(plan?.role);
    if (role === "vip") return "VIP";
    if (role === "premium") return "Premium";
    if (role === "sales") return "Sales";
    if (role === "business") return "Business";
    return "Free";
  }

  function buildBusinessApplicationReturn(planRole) {
    const url = new URL(window.location.href);
    url.searchParams.set("businessApplication", planDisplayRole({ role: planRole }));
    return `${url.pathname}${url.search}${url.hash}`;
  }

  function renderPlans(plans) {
    const grid = document.getElementById("twaiPricingPopupGrid");
    if (!grid) return;
    grid.innerHTML = plans.map((plan) => {
      const role = normalizeRole(plan.role);
      const selected = role === "premium";
      const targetHref = planCtaHref(plan);
      const businessReturn = buildBusinessApplicationReturn(planDisplayRole(plan));
      const ctaText = escapeHtml(plan.cta || "Đăng ký");
      const actionHtml = role === "sales" || role === "business"
        ? `<button class="twai-price-cta ${selected ? "primary" : ""}" type="button" data-auth-required="true" data-auth-return="${escapeHtml(businessReturn)}" data-business-application-plan="${escapeHtml(planDisplayRole(plan))}">${ctaText} →</button>`
        : `<a class="twai-price-cta ${selected ? "primary" : ""}" href="${escapeHtml(targetHref)}" data-auth-required="true" data-auth-return="${escapeHtml(targetHref)}">${ctaText} →</a>`;
      return `
        <article class="twai-price-card twai-price-card-${escapeHtml(role)} ${selected ? "is-featured" : ""}">
          ${selected ? `<span class="twai-price-ribbon">AI CŨNG CHỌN</span>` : ""}
          <h3>${escapeHtml(plan.name)}</h3>
          <strong>${escapeHtml(plan.price)}</strong>
          <p class="twai-price-subtitle">${escapeHtml(plan.subtitle)}</p>
          ${plan.note ? `<em>${escapeHtml(plan.note)}</em>` : ""}
          <ul>${plan.benefits.map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul>
          ${actionHtml}
        </article>`;
    }).join("");
  }

  async function openPricingPopup(reason) {
    const modal = ensureModal();
    const reasonBox = document.getElementById("twaiPricingPopupReason");
    if (reasonBox) reasonBox.textContent = reason || "";
    renderPlans(planCache || DEFAULT_PLANS);
    modal.classList.add("open");
    modal.setAttribute("aria-hidden", "false");
    const plans = await loadPlans();
    renderPlans(plans);
  }

  function closePricingPopup() {
    const modal = document.getElementById("travelwaiPricingPopup");
    if (!modal) return;
    modal.classList.remove("open");
    modal.setAttribute("aria-hidden", "true");
  }


  function ensureBusinessApplicationModal() {
    let modal = document.getElementById("businessApplicationModal");
    if (modal) return modal;
    modal = document.createElement("div");
    modal.id = "businessApplicationModal";
    modal.className = "form-modal business-application-modal";
    modal.innerHTML = `
      <form class="form-card business-application-card" id="businessApplicationForm">
        <div class="tour-offer-modal-header business-application-header">
          <div>
            <span class="eyebrow">Đăng ký đối tác</span>
            <h2 id="businessApplicationTitle">Biểu mẫu đăng ký</h2>
          </div>
          <button type="button" class="tour-offer-close" data-business-application-close aria-label="Đóng">&times;</button>
        </div>
        <input type="hidden" id="businessApplicationPlan" />
        <h3>Thông tin doanh nghiệp</h3>
        <div class="form-grid">
          <div class="form-group full"><label>Tên công ty / cá nhân kinh doanh *</label><input id="businessCompanyName" required autocomplete="organization" /></div>
          <div class="form-group"><label>Loại hình *</label><input id="businessType" required /></div>
          <div class="form-group"><label>Mã số thuế / CMND</label><input id="businessTaxCode" /></div>
          <div class="form-group full"><label>Địa chỉ văn phòng</label><input id="businessOfficeAddress" /></div>
          <div class="form-group"><label>Tỉnh / Thành phố *</label><input id="businessProvince" required autocomplete="address-level1" /></div>
          <div class="form-group"><label>Website / Fanpage</label><input id="businessWebsite" /></div>
        </div>
        <h3>Người phụ trách</h3>
        <div class="form-grid">
          <div class="form-group"><label>Họ và tên *</label><input id="businessContactName" required autocomplete="name" /></div>
          <div class="form-group"><label>Chức vụ</label><input id="businessPosition" /></div>
          <div class="form-group"><label>Số điện thoại *</label><input id="businessPhone" type="tel" required autocomplete="tel" /></div>
          <div class="form-group"><label>Email *</label><input id="businessEmail" type="email" required autocomplete="email" /></div>
        </div>
        <p class="checkout-status" id="businessApplicationStatus"></p>
        <div class="modal-actions">
          <button class="btn-soft" type="button" data-business-application-close>Đóng</button>
          <button class="btn-primary" type="submit">Gửi Admin</button>
        </div>
      </form>`;
    document.body.appendChild(modal);
    modal.addEventListener("click", function (event) {
      if (event.target === modal || event.target.closest("[data-business-application-close]")) closeBusinessApplicationModal();
    });
    modal.querySelector("#businessApplicationForm")?.addEventListener("submit", submitBusinessApplication);
    return modal;
  }

  function closeBusinessApplicationModal() {
    const modal = document.getElementById("businessApplicationModal");
    if (modal) modal.classList.remove("open");
  }

  function setBusinessApplicationStatus(message, type) {
    const status = document.getElementById("businessApplicationStatus");
    if (!status) return;
    status.textContent = message || "";
    status.className = `checkout-status ${type || ""}`.trim();
  }

  function setBusinessApplicationBusy(busy) {
    const form = document.getElementById("businessApplicationForm");
    const button = form?.querySelector('button[type="submit"]');
    if (button) button.disabled = !!busy;
  }

  function validateBusinessApplicationForm() {
    const requiredFields = [
      ["businessCompanyName", "Vui lòng nhập Tên công ty / cá nhân kinh doanh."],
      ["businessType", "Vui lòng nhập Loại hình."],
      ["businessProvince", "Vui lòng nhập Tỉnh / Thành phố."],
      ["businessContactName", "Vui lòng nhập Họ và tên người phụ trách."],
      ["businessPhone", "Vui lòng nhập Số điện thoại."],
      ["businessEmail", "Vui lòng nhập Email."]
    ];
    for (const [id, message] of requiredFields) {
      const input = document.getElementById(id);
      if (!input) continue;
      input.setCustomValidity("");
      if (!String(input.value || "").trim()) {
        input.setCustomValidity(message);
        input.reportValidity();
        input.focus();
        setBusinessApplicationStatus(message, "error");
        return false;
      }
      if (!input.checkValidity()) {
        input.reportValidity();
        input.focus();
        setBusinessApplicationStatus(input.validationMessage || message, "error");
        return false;
      }
    }
    return true;
  }

  function openBusinessApplicationModal(planRole) {
    const plan = planDisplayRole({ role: planRole });
    const modal = ensureBusinessApplicationModal();
    const title = document.getElementById("businessApplicationTitle");
    const planInput = document.getElementById("businessApplicationPlan");
    if (title) title.textContent = `Biểu mẫu đăng ký ${plan}`;
    if (planInput) planInput.value = plan;
    setBusinessApplicationStatus("", "");
    const email = localStorage.getItem("userEmail") || sessionStorage.getItem("userEmail") || "";
    const name = localStorage.getItem("username") || sessionStorage.getItem("username") || "";
    const emailInput = document.getElementById("businessEmail");
    const nameInput = document.getElementById("businessContactName");
    if (emailInput && !emailInput.value) emailInput.value = email;
    if (nameInput && !nameInput.value) nameInput.value = name;
    closePricingPopup();
    modal.classList.add("open");
  }

  async function submitBusinessApplication(event) {
    event.preventDefault();
    if (!validateBusinessApplicationForm()) return;
    setBusinessApplicationBusy(true);
    setBusinessApplicationStatus("Đang gửi biểu mẫu cho Admin...", "info");
    const payload = {
      planRole: document.getElementById("businessApplicationPlan")?.value || "Sales",
      companyName: document.getElementById("businessCompanyName")?.value.trim() || "",
      businessType: document.getElementById("businessType")?.value.trim() || "",
      taxCode: document.getElementById("businessTaxCode")?.value.trim() || "",
      officeAddress: document.getElementById("businessOfficeAddress")?.value.trim() || "",
      province: document.getElementById("businessProvince")?.value.trim() || "",
      website: document.getElementById("businessWebsite")?.value.trim() || "",
      contactName: document.getElementById("businessContactName")?.value.trim() || "",
      position: document.getElementById("businessPosition")?.value.trim() || "",
      phone: document.getElementById("businessPhone")?.value.trim() || "",
      email: document.getElementById("businessEmail")?.value.trim() || ""
    };
    try {
      const apiFetch = typeof window.authenticatedFetch === "function" ? window.authenticatedFetch : fetch;
      const response = await apiFetch("/api/commerce/business-application", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || result.success === false) throw new Error(result.message || "Không gửi được biểu mẫu.");
      setBusinessApplicationStatus(result.message || "Đã gửi biểu mẫu cho Admin.", "success");
      document.getElementById("businessApplicationForm")?.reset();
      setTimeout(closeBusinessApplicationModal, 900);
    } catch (error) {
      setBusinessApplicationStatus(error.message || "Không gửi được biểu mẫu.", "error");
    } finally {
      setBusinessApplicationBusy(false);
    }
  }

  function showFreeAiPopup(reason) {
    openPricingPopup(reason || "Tài khoản Free chưa dùng được tính năng này. Vui lòng nâng cấp gói.");
  }

  function shouldBlockAiTarget(target) {
    if (!target) return false;
    return Boolean(target.closest([
      "#postAiButton",
      "#publicPostAiButton",
      "#askScheduleAiBtn",
      "#aiCreateScheduleBtn",
      "#askAiProvinceDetailBtn",
      ".ask-ai-detail-btn",
      ".ai-content-button",
      ".ai-suggestion-chip",
    ].join(",")));
  }

  document.addEventListener("click", function (event) {
    const pricingTrigger = event.target.closest("[data-pricing-trigger]");
    if (pricingTrigger) {
      event.preventDefault();
      openPricingPopup(pricingTrigger.getAttribute("data-pricing-reason") || "");
      return;
    }

    const authRequiredTrigger = event.target.closest("[data-auth-required]");
    if (authRequiredTrigger && !hasLoggedInAccount()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      redirectToLogin(authRequiredTrigger.getAttribute("data-auth-return") || authRequiredTrigger.getAttribute("href") || "/home");
      return;
    }

    const businessApplicationTrigger = event.target.closest("[data-business-application-plan]");
    if (businessApplicationTrigger) {
      event.preventDefault();
      event.stopImmediatePropagation();
      openBusinessApplicationModal(businessApplicationTrigger.getAttribute("data-business-application-plan"));
      return;
    }

    const aiButtonTarget = shouldBlockAiTarget(event.target);
    if (aiButtonTarget && !hasLoggedInAccount()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      redirectToLogin(`${window.location.pathname}${window.location.search}${window.location.hash}` || "/home");
      return;
    }

    if (aiButtonTarget && isFreeAccount()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      showFreeAiPopup();
    }
  }, true);

  document.addEventListener("DOMContentLoaded", function () {
    const params = new URLSearchParams(window.location.search);
    const requestedBusinessPlan = params.get("businessApplication") || params.get("businessPlan") || "";
    if (requestedBusinessPlan && hasLoggedInAccount()) {
      const cleaned = new URL(window.location.href);
      cleaned.searchParams.delete("businessApplication");
      cleaned.searchParams.delete("businessPlan");
      window.history.replaceState({}, document.title, `${cleaned.pathname}${cleaned.search}${cleaned.hash}`);
      openBusinessApplicationModal(requestedBusinessPlan);
    }
    if (sessionStorage.getItem("travelwaiOpenPricingAfterLogin") === "1" && hasLoggedInAccount()) {
      sessionStorage.removeItem("travelwaiOpenPricingAfterLogin");
      setTimeout(() => openPricingPopup(""), 180);
    }
  });

  window.TravelwAIPricingPopup = {
    open: openPricingPopup,
    close: closePricingPopup,
    isFreeAccount,
    showFreeAiPopup,
    hasLoggedInAccount,
    redirectToLogin,
    openBusinessApplicationModal,
    closeBusinessApplicationModal,
    reload: function () { planCache = null; return loadPlans(); }
  };
})();
