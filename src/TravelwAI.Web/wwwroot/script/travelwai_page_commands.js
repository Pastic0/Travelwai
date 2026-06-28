(function () {
  "use strict";

  const PAGES = [
    { name: "Đăng nhập", url: "/login", aliases: ["dang nhap", "login"], detail: "Đăng nhập tài khoản TravelwAI." },
    { name: "Đăng ký", url: "/signup", aliases: ["dang ky", "tao tai khoan", "register", "signup", "sign up"], detail: "Tạo tài khoản mới." },
    { name: "Quên mật khẩu", url: "/forgot-password", aliases: ["quen mat khau", "lay lai mat khau", "khoi phuc mat khau", "forgot password"], detail: "Lấy lại mật khẩu bằng email." },
    { name: "Đặt lại mật khẩu", url: "/reset-password", aliases: ["dat lai mat khau", "reset password", "reset mat khau"], detail: "Nhập mã khôi phục và đặt mật khẩu mới." },
    { name: "Trang chủ", url: "/home", aliases: ["trang chu", "home", "mainsite", "main site"], detail: "Trang chính sau khi đăng nhập." },
    { name: "Giới thiệu", url: "/landing", aliases: ["gioi thieu", "landing", "trang gioi thieu"], detail: "Trang giới thiệu TravelwAI." },
    { name: "Bản đồ Việt Nam", url: "/provinces", aliases: ["ban do viet nam", "ban do", "tinh thanh", "34 tinh", "34 tinh thanh", "viet nam", "provinces"], detail: "Xem bản đồ 34 tỉnh thành." },
    { name: "Chi tiết tỉnh", url: "/detail", aliases: ["chi tiet tinh", "chi tiet dia phuong", "detail", "xem chi tiet tinh"], detail: "Xem thông tin chi tiết địa phương." },
    { name: "Lịch trình", url: "/schedule", aliases: ["lich trinh", "lap lich trinh", "tao lich trinh", "schedule"], detail: "Tạo và quản lý lịch trình du lịch." },
    { name: "Kế hoạch", url: "/plans", aliases: ["ke hoach", "lap ke hoach", "tao ke hoach", "plans"], detail: "Tạo nhóm kế hoạch và mời người đi chung." },
    { name: "Bảng giá", url: "/pricing", aliases: ["bang gia", "pricing", "gia goi", "goi tai khoan", "mua goi", "nang cap goi"], detail: "Xem các gói tài khoản." },
    { name: "Giỏ hàng", url: "/cart", aliases: ["gio hang", "cart", "xem gio hang"], detail: "Xem sản phẩm chờ thanh toán." },
    { name: "Thanh toán", url: "/checkout", aliases: ["thanh toan", "checkout", "xac nhan thanh toan", "qr thanh toan", "ma qr", "quet qr"], detail: "Thanh toán tour hoặc gói tài khoản." },
    { name: "Hồ sơ", url: "/profile", aliases: ["ho so", "tai khoan", "thong tin ca nhan", "doi ten", "profile"], detail: "Xem hồ sơ, đổi ảnh, đổi tên và đổi mật khẩu." },
    { name: "Nhắn tin", url: "/messaging", aliases: ["nhan tin", "tin nhan", "messaging", "chat", "hop thoai"], detail: "Nhắn tin với bạn bè, nhóm và Admin." },
    { name: "Hỗ trợ Admin", url: "/messaging?admin=1", aliases: ["phan hoi", "lien he admin", "ho tro", "gop y", "nhan tin admin", "chat admin"], detail: "Mở hội thoại với Admin." },
    { name: "Liên hệ", url: "/contact", aliases: ["trang lien he", "contact page", "contact", "lien he travelwai"], detail: "Trang liên hệ TravelwAI." },
    { name: "Thông báo", url: "/notifications", aliases: ["thong bao", "notification", "notifications"], detail: "Xem thông báo và lời mời." },
    { name: "Bài viết", url: "/posts", aliases: ["bai viet", "tin du lich", "kham pha bai", "posts"], detail: "Xem và quản lý bài viết du lịch." },
    { name: "Tour du lịch", url: "/tours", aliases: ["tour du lich", "tour", "dat tour", "xem tour", "tours"], detail: "Xem tour và đặt tour." },
    { name: "Sales", url: "/tour-sales", aliases: ["sales", "trang sales", "ban tour", "don ban tour", "tour sales", "tour-sales"], detail: "Quản lý tour, đơn bán tour và doanh thu." },
    { name: "Business", url: "/business", aliases: ["business", "trang business", "doanh nghiep", "kinh doanh"], detail: "Quản lý tài khoản Business." },
    { name: "Admin", url: "/admin", aliases: ["admin", "quan tri", "quan tri he thong", "quan ly he thong", "trang admin", "admin panel"], detail: "Quản lý hệ thống TravelwAI." },
    { name: "Manage", url: "/manage", aliases: ["manage", "trang manage", "quan ly goi", "quan ly don goi", "don goi", "goi nguoi dung", "tai khoan va goi"], detail: "Quản lý tài khoản, gói và đơn gói." }
  ];

  function normalize(value) {
    return String(value || "")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/đ/g, "d")
      .replace(/Đ/g, "D")
      .toLowerCase()
      .replace(/[^a-z0-9@._+\-\s]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function getPageListText() {
    return PAGES.map(function (page) { return page.name; }).join(", ");
  }

  function getSyntaxReply() {
    return "Nhắn: mở [tên trang], tới trang [tên trang] hoặc chi tiết trang [tên trang].";
  }

  function isExactPage(page, normalized) {
    if (!page || !normalized) return false;
    if (normalize(page.name) === normalized) return true;
    return page.aliases.some(function (alias) { return normalize(alias) === normalized; });
  }

  function cleanPageQuery(value) {
    return normalize(value)
      .replace(/\b(nhe|nha|di|voi|giup toi|giup minh|cho toi|cho minh|a|nhe a|nha a)\b/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function findPage(value) {
    const normalized = cleanPageQuery(value);
    if (!normalized) return null;

    const exact = PAGES.find(function (page) { return isExactPage(page, normalized); });
    if (exact) return exact;

    const scored = PAGES.map(function (page) {
      const names = [page.name].concat(page.aliases).map(normalize).filter(Boolean);
      let score = 0;
      names.forEach(function (name) {
        if (!name) return;
        if (normalized === name) score = Math.max(score, 100 + name.length);
        else if (normalized.includes(name)) score = Math.max(score, 80 + name.length);
        else if (name.includes(normalized)) score = Math.max(score, 50 + normalized.length);
      });
      return { page, score };
    }).filter(function (item) { return item.score > 0; });

    scored.sort(function (a, b) { return b.score - a.score; });
    return scored[0]?.page || null;
  }

  function buildDetailReply(page) {
    return page.detail + " Muốn mở trang này, nhắn: mở " + page.name + ".";
  }

  function asNavigate(page) {
    return { type: "navigate", url: page.url, reply: "Đang mở " + page.name + "." };
  }

  function parseManagerCommand(text) {
    const original = String(text || "").trim();
    const normalized = normalize(original);
    if (!normalized) return null;

    if (/(co\s*)?trang\s*nao|danh\s*sach\s*trang|menu|cac\s*trang|nhung\s*trang|xem\s*trang|tat\s*ca\s*trang|route/.test(normalized)) {
      return { type: "info", reply: "Các trang TravelwAI: " + getPageListText() + ". " + getSyntaxReply() };
    }

    const detailMatch = normalized.match(/^(?:chi\s*tiet|mo\s*ta|gioi\s*thieu)\s+(?:trang\s+)?(.+)$/);
    if (detailMatch) {
      const page = findPage(detailMatch[1]);
      if (!page) return { type: "info", reply: "Chưa tìm thấy trang đó. " + getSyntaxReply() };
      return { type: "info", reply: buildDetailReply(page) };
    }

    const navigatePatterns = [
      /^(?:mo|vao|qua|toi|di|chuyen)(?:\s+(?:toi|den|qua))?(?:\s+trang)?\s+(.+)$/,
      /^(?:cho\s+toi|cho\s+minh)\s+(?:vao|mo|toi|qua)(?:\s+trang)?\s+(.+)$/,
      /^(?:hay\s+)?(?:mo|vao|qua|toi|di|chuyen)(?:\s+(?:toi|den|qua))?(?:\s+trang)?\s+(.+)$/
    ];

    for (const pattern of navigatePatterns) {
      const match = normalized.match(pattern);
      if (!match || !match[1]) continue;
      const page = findPage(match[1]);
      if (!page) return { type: "info", reply: "Chưa tìm thấy trang đó. " + getSyntaxReply() };
      return asNavigate(page);
    }

    const pageByPhrase = normalized.match(/^trang\s+(.+)$/);
    if (pageByPhrase) {
      const page = findPage(pageByPhrase[1]);
      if (page) return asNavigate(page);
    }

    const exactPage = PAGES.find(function (page) { return isExactPage(page, normalized); });
    if (exactPage) return asNavigate(exactPage);

    if (/\btrang\b/.test(normalized)) {
      const page = findPage(normalized.replace(/\btrang\b/g, " "));
      if (page) return asNavigate(page);
      return { type: "info", reply: "Chưa tìm thấy trang đó. " + getSyntaxReply() };
    }

    const loosePage = findPage(normalized);
    if (loosePage && normalized.length >= 5) return asNavigate(loosePage);

    return null;
  }

  window.TravelwAIPageCommands = {
    pages: PAGES,
    normalize,
    findPage,
    parseManagerCommand,
    getPageListText,
    getSyntaxReply,
    buildDetailReply
  };
})();
