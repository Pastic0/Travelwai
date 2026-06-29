(function () {
  "use strict";

  const MAX_CONTEXT_LENGTH = 3400;

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

  function clampText(value, maxLength = MAX_CONTEXT_LENGTH) {
    const text = String(value || "")
      .replace(/[\u0000-\u001F\u007F]/g, " ")
      .replace(/\s+/g, " ")
      .trim();
    return text.length > maxLength ? text.slice(0, maxLength).trim() : text;
  }

  function questionRequestsDate(text) {
    const normalized = normalizeText(text);
    if (!normalized) return false;
    return [
      "ngay nao", "ngay may", "ngay bao nhieu", "khi nao", "bao gio", "luc nao",
      "thoi gian", "dien ra", "to chuc", "may thang", "thang nao", "nam nao",
      "am lich", "duong lich", "mung", "dang dien ra"
    ].some(function (keyword) { return normalized.includes(keyword); });
  }

  function findProvinceNamesFromText(text) {
    const normalized = normalizeText(text);
    if (!normalized || !Array.isArray(window.VIETNAM_34_PROVINCES)) return [];

    const matches = [];
    window.VIETNAM_34_PROVINCES.forEach(function (province) {
      const names = [province.name].concat(province.aliases || [], province.merged_from || [])
        .filter(Boolean)
        .sort(function (a, b) { return String(b).length - String(a).length; });

      const found = names.some(function (name) {
        const key = normalizeText(name);
        return key && (normalized.includes(key) || key.includes(normalized));
      });

      if (found) matches.push(province.name);
    });

    return Array.from(new Set(matches)).slice(0, 3);
  }

  function getKnownLandmarkReply(text) {
    const normalized = normalizeText(text);
    if (!normalized) return "";

    const knownLandmarks = [
      {
        keys: ["hoang thanh thang long", "thang long", "hoang thanh"],
        reply: "Hoàng thành Thăng Long gắn với trung tâm quyền lực của kinh đô Thăng Long xưa. Đây là nơi lưu dấu nhiều lớp lịch sử của Hà Nội, từ dấu tích cung điện, nền móng kiến trúc đến hiện vật khảo cổ. Câu chuyện của nơi này nên kể theo hướng một kinh thành lâu đời, nơi các triều đại để lại dấu ấn văn hoá và lịch sử giữa lòng Thủ đô."
      },
      {
        keys: ["van mieu quoc tu giam", "quoc tu giam", "van mieu"],
        reply: "Văn Miếu - Quốc Tử Giám gắn với truyền thống hiếu học của Thăng Long - Hà Nội. Nơi này thường được nhắc đến như biểu tượng của giáo dục, khoa bảng và tinh thần trọng chữ nghĩa. Khi kể về Văn Miếu, có thể nhấn vào câu chuyện tôn vinh người học, người thầy và các giá trị văn hoá lâu đời."
      },
      {
        keys: ["co loa", "thanh co loa"],
        reply: "Cổ Loa gắn với câu chuyện kinh đô xưa và truyền thuyết An Dương Vương. Nơi này nổi bật bởi dấu tích thành cổ, không gian lịch sử và các lớp chuyện dân gian quanh nỏ thần, Mỵ Châu - Trọng Thủy. Khi kể về Cổ Loa, có thể nhấn vào sự giao thoa giữa lịch sử và truyền thuyết."
      },
      {
        keys: ["ho guom", "ho hoan kiem", "hoan kiem"],
        reply: "Hồ Gươm là không gian văn hoá tiêu biểu của Hà Nội, gắn với truyền thuyết trả gươm và hình ảnh Tháp Rùa. Câu chuyện nơi đây thường được kể như biểu tượng của ký ức đô thị, lịch sử và nhịp sống thanh bình giữa trung tâm Thủ đô."
      }
    ];

    const match = knownLandmarks.find(function (item) {
      return item.keys.some(function (key) { return normalized.includes(key); });
    });
    return match ? match.reply : "";
  }

  function questionNeedsWikipedia(text) {
    const normalized = normalizeText(text);
    if (!normalized) return false;
    if (findProvinceNamesFromText(text).length) return true;
    return [
      "dia danh", "di tich", "danh lam", "tinh thanh", "thanh pho", "le hoi", "ngay le",
      "lich su", "van hoa", "truyen thuyet", "nguon goc", "y nghia", "nhan vat", "dan toc", "di san",
      "bao tang", "den tho", "ngoi chua", "thap", "hoang thanh", "co do", "pho co", "lang nghe",
      "o dau", "la gi", "khi nao", "ngay nao", "dien ra", "to chuc", "ke chuyen", "gioi thieu", "thuyet minh",
      "hoi lim", "gio to", "tet", "quoc khanh", "trung thu", "thang long", "ha long", "nha trang", "phu quoc", "da lat", "hoi an", "hue", "sa pa", "sapa", "tam coc", "trang an"
    ].some(function (keyword) { return normalized.includes(keyword); });
  }

  function buildNoWikipediaReply() {
    return "Mình chưa tìm được nguồn đủ khớp để nói chắc về câu hỏi này. Bạn gửi lại đúng tên địa danh, lễ hội hoặc kèm thêm tỉnh/thành nhé, mình sẽ tra sát hơn.";
  }

  function buildConversationFallbackReply(text) {
    const normalized = normalizeText(text);
    if (/\b(xin chao|chao|hi|hello|alo|hey)\b/i.test(normalized)) {
      return "Chào bạn, mình là Hướng dẫn viên Travelwinne. Bạn muốn mình gợi ý lịch trình, tư vấn điểm đến hay kể về một địa danh cụ thể?";
    }
    if (normalized.includes("cam on") || normalized.includes("thanks")) {
      return "Không có gì. Bạn cần mình gợi ý thêm điểm đến, lịch trình hay kinh nghiệm đi lại thì cứ nhắn tiếp nhé.";
    }
    if (normalized.includes("ban la ai") || normalized.includes("lam duoc gi") || normalized.includes("giup duoc gi")) {
      return "Mình là Hướng dẫn viên Travelwinne. Mình có thể trò chuyện, gợi ý lịch trình và tra Wikipedia khi bạn hỏi về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá hoặc ngày lễ.";
    }
    if (normalized.includes("toi muon di du lich") || normalized.includes("tu van") || normalized.includes("goi y")) {
      return "Bạn muốn đi kiểu nào: biển, núi, nghỉ dưỡng, khám phá văn hoá hay đi cùng nhóm bạn? Cho mình thêm thời gian đi, số người và ngân sách để gợi ý sát hơn.";
    }
    return "Bạn nói rõ hơn một chút nhé. Nếu hỏi về địa danh, tỉnh thành, lễ hội, lịch sử, văn hoá hoặc ngày lễ, mình sẽ tra Wikipedia để trả lời chính xác.";
  }

  function stripDateText(text) {
    return String(text || "")
      .replace(/\b(?:ngày\s*)?(?:mùng\s*)?\d{1,2}\s*(?:đến|[-–—])\s*\d{1,2}\/\d{1,2}\s*(?:âm\s*lịch|dương\s*lịch|AL|DL)?/gi, "")
      .replace(/\b(?:ngày\s*)?(?:mùng\s*)?\d{1,2}\/\d{1,2}\s*(?:âm\s*lịch|dương\s*lịch|AL|DL)?/gi, "")
      .replace(/\btháng\s*\d{1,2}\s*(?:âm\s*lịch|dương\s*lịch|AL|DL)?/gi, "")
      .replace(/\s+([,.;:])/g, "$1")
      .replace(/\s{2,}/g, " ")
      .trim();
  }

  function getProvinceByName(name) {
    if (!Array.isArray(window.VIETNAM_34_PROVINCES)) return null;
    return window.VIETNAM_34_PROVINCES.find(function (item) { return item.name === name; }) || null;
  }

  function getProvinceInfo(name) {
    return typeof window.getLocalProvinceInfo === "function" ? window.getLocalProvinceInfo(name) : null;
  }

  function getCultureInfo(name) {
    return typeof window.getProvinceCultureInfo34 === "function" ? window.getProvinceCultureInfo34(name) : null;
  }

  function formatProvinceContext(info, includeDates = true) {
    if (!info) return "";
    const culture = info.culture || getCultureInfo(info.province_name || info.name) || {};
    const currentFestivalText = Array.isArray(info.current_festivals) && info.current_festivals.length
      ? info.current_festivals.map(function (item) { return includeDates ? (item && (item.line || item.name) || "") : (item && (item.name || item.line) || ""); }).filter(Boolean).join("; ")
      : (includeDates ? (info.current_festival_summary || "") : "");
    const festivalText = includeDates ? culture.le_hoi_theo_thang : stripDateText(culture.le_hoi_theo_thang);

    return [
      "Tỉnh/thành: " + (info.province_name || info.name || ""),
      info.description ? "Mô tả: " + info.description : "",
      culture.cau_chuyen_di_tich ? "Câu chuyện di tích: " + culture.cau_chuyen_di_tich : "",
      culture.nhan_vat_lich_su ? "Nhân vật lịch sử: " + culture.nhan_vat_lich_su : "",
      culture.truyen_thuyet_dia_phuong ? "Truyền thuyết địa phương: " + culture.truyen_thuyet_dia_phuong : "",
      festivalText ? "Lễ hội theo tháng: " + festivalText : "",
      culture.nghe_truyen_thong_mai_mot ? "Nghề truyền thống đang mai một: " + culture.nghe_truyen_thong_mai_mot : "",
      culture.le_hoi_cac_dan_toc ? "Lễ hội của các dân tộc: " + culture.le_hoi_cac_dan_toc : "",
      currentFestivalText ? "Lễ hội đang diễn ra: " + currentFestivalText : ""
    ].filter(Boolean).join("\n");
  }

  function findFestivalContextFromText(text, provinceNames, includeDates = true) {
    if (typeof window.getFestivalCalendarEvents34 !== "function") return "";
    const normalized = normalizeText(text);
    if (!normalized) return "";

    const provinces = provinceNames && provinceNames.length
      ? provinceNames
      : (Array.isArray(window.VIETNAM_34_PROVINCES) ? window.VIETNAM_34_PROVINCES.map(function (item) { return item.name; }) : []);

    const rows = [];
    const seen = new Set();
    provinces.forEach(function (provinceName) {
      const events = window.getFestivalCalendarEvents34(provinceName) || [];
      events.forEach(function (event) {
        const eventName = String(event && event.name || "").trim();
        const eventKey = normalizeText(eventName);
        if (!eventName || eventKey.length < 3) return;
        if (!normalized.includes(eventKey) && !eventKey.includes(normalized)) return;

        const key = eventKey + "|" + provinceName;
        if (seen.has(key)) return;
        seen.add(key);
        const line = includeDates && typeof window.formatFestivalDate34 === "function" ? window.formatFestivalDate34(event) : eventName;
        rows.push(line + " - " + provinceName);
      });
    });

    return rows.slice(0, 8).join("; ");
  }

  function buildContextForMessage(text) {
    const includeDates = questionRequestsDate(text);
    const knownLandmarkReply = getKnownLandmarkReply(text);
    const provinceNames = findProvinceNamesFromText(text);
    const provinceContexts = provinceNames
      .map(function (provinceName) { return formatProvinceContext(getProvinceInfo(provinceName), includeDates); })
      .filter(Boolean);
    const festivalContext = findFestivalContextFromText(text, provinceNames, includeDates);

    const parts = [];
    if (knownLandmarkReply) parts.push("Thông tin địa danh khớp câu hỏi: " + knownLandmarkReply);
    if (provinceContexts.length) parts.push(provinceContexts.join("\n\n"));
    if (festivalContext) parts.push("Lễ hội khớp với câu hỏi: " + festivalContext);
    if (!parts.length) return "";
    if (!includeDates) parts.unshift("Câu hỏi không hỏi thời gian, không nêu ngày/tháng trong câu trả lời.");

    return clampText(parts.join("\n\n"));
  }

  function buildLocalFallbackReply(text) {
    const knownLandmarkReply = getKnownLandmarkReply(text);
    if (knownLandmarkReply) return knownLandmarkReply;

    const provinceNames = findProvinceNamesFromText(text);
    const includeDates = questionRequestsDate(text);
    const lines = [];

    provinceNames.forEach(function (provinceName) {
      const info = getProvinceInfo(provinceName);
      const province = getProvinceByName(provinceName);
      const name = (info && (info.province_name || info.name)) || (province && province.name) || provinceName;
      const description = String(info && info.description || "").trim();
      const famousFor = Array.isArray(province && province.famous_for) && province.famous_for.length
        ? province.famous_for.slice(0, 3).join(", ")
        : "";
      const culture = getCultureInfo(name);
      const festivalText = includeDates ? String(culture && culture.le_hoi_theo_thang || "").trim() : "";
      const cultureText = String(culture && (culture.cau_chuyen_di_tich || culture.nhan_vat_lich_su || culture.le_hoi_cac_dan_toc) || "").trim();

      const parts = [];
      if (description) parts.push(description);
      if (famousFor) parts.push("Nổi bật về " + famousFor + ".");
      if (cultureText) parts.push(cultureText.split(".").slice(0, 2).join(".").trim() + ".");
      if (festivalText) parts.push("Lễ hội: " + festivalText);
      if (parts.length) lines.push(name + ": " + parts.join(" "));
    });

    if (lines.length) {
      return lines.join("\n\n") + "\n\nBạn có thể hỏi tiếp về lễ hội, lịch sử, địa danh hoặc kinh nghiệm đi lại của tỉnh này.";
    }

    return "Mình chưa lấy được phản hồi AI lúc này. Bạn có thể hỏi ngắn hơn theo tên tỉnh, địa danh hoặc lễ hội, ví dụ: Đà Nẵng có gì nổi bật, Huế có lễ hội gì, Phú Quốc nên đi đâu.";
  }

  function getWikipediaSearchQuery(text) {
    const raw = String(text || "").trim();
    const normalized = normalizeText(raw);
    if (!normalized) return "";

    const knownQueries = [
      { keys: ["hoang thanh thang long", "thang long", "hoang thanh"], title: "Hoàng thành Thăng Long" },
      { keys: ["van mieu quoc tu giam", "quoc tu giam", "van mieu"], title: "Văn Miếu - Quốc Tử Giám" },
      { keys: ["co loa", "thanh co loa"], title: "Cổ Loa" },
      { keys: ["ho guom", "ho hoan kiem", "hoan kiem"], title: "Hồ Hoàn Kiếm" }
    ];
    const known = knownQueries.find(function (item) {
      return item.keys.some(function (key) { return normalized.includes(key); });
    });
    if (known) return known.title;

    return raw
      .replace(/^(hãy|hay|cho\s+tôi|cho\s+toi|giúp\s+tôi|giup\s+toi|kể\s+chuyện|ke\s+chuyen|giới\s+thiệu|gioi\s+thieu|thuyết\s+minh|thuyet\s+minh|tìm\s+hiểu|tim\s+hieu|nói\s+về|noi\s+ve|kể\s+về|ke\s+ve)\s+/i, "")
      .replace(/\b(ở\s+đâu|o\s+dau|là\s+gì|la\s+gi|có\s+gì|co\s+gi|như\s+thế\s+nào|nhu\s+the\s+nao)\b/gi, " ")
      .replace(/[?!.:,;]+/g, " ")
      .replace(/\s+/g, " ")
      .trim()
      .slice(0, 100);
  }

  function isWikipediaResultRelevant(query, title, extract) {
    const q = normalizeText(query);
    const t = normalizeText(title);
    const e = normalizeText(extract).slice(0, 1600);
    if (!q || !t || !e) return false;
    if (t === q) return true;
    if (t.includes(q) || q.includes(t)) return true;

    const stopWords = new Set(["ke", "chuyen", "gioi", "thieu", "thuyet", "minh", "tim", "hieu", "noi", "ve", "cho", "toi", "hay", "la", "gi", "co", "o", "dau", "nhu", "the", "nao", "le", "hoi", "lich", "su", "van", "hoa", "dia", "danh", "di", "tich"]);
    const tokens = q.split(/\s+/).filter(function (token) { return token.length >= 3 && !stopWords.has(token); });
    if (!tokens.length) return false;
    const titleHits = tokens.filter(function (token) { return t.includes(token); }).length;
    const sourceHits = tokens.filter(function (token) { return t.includes(token) || e.includes(token); }).length;
    if (titleHits === tokens.length) return true;
    if (tokens.length <= 2 && titleHits === 0) return false;
    return sourceHits >= Math.ceil(tokens.length * 0.7);
  }

  function cleanWikipediaExtract(value) {
    return String(value || "")
      .replace(/\[[^\]]*\]/g, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function trimWikipediaReply(value) {
    const text = cleanWikipediaExtract(value);
    if (!text) return "";
    const sentences = text.match(/[^.!?。！？]+[.!?。！？]+|[^.!?。！？]+$/g) || [text];
    const picked = [];
    let length = 0;
    for (const sentence of sentences) {
      const part = sentence.trim();
      if (!part) continue;
      if (picked.length >= 5 || length + part.length > 850) break;
      picked.push(part);
      length += part.length;
    }
    return picked.join(" ").trim() || text.slice(0, 850).trim();
  }

  async function fetchWikipediaReply(text) {
    const query = getWikipediaSearchQuery(text);
    if (!query) return "";

    const url = "https://vi.wikipedia.org/w/api.php?action=query&generator=search&gsrlimit=8&prop=extracts|info&exintro=1&explaintext=1&inprop=url&format=json&origin=*&gsrsearch=" + encodeURIComponent(query);
    try {
      const response = await fetch(url, { cache: "no-store" });
      if (!response.ok) return "";
      const data = await response.json().catch(function () { return null; });
      const pages = Object.values(data && data.query && data.query.pages || {})
        .sort(function (a, b) { return Number(a && a.index || 99) - Number(b && b.index || 99); });
      const page = pages.find(function (item) {
        return isWikipediaResultRelevant(query, item && item.title || "", item && item.extract || "");
      });
      if (!page) return "";
      return trimWikipediaReply(page.extract || "");
    } catch (_) {
      return "";
    }
  }

  window.TravelwAIGuideChatbot = {
    normalizeText,
    clampText,
    questionRequestsDate,
    questionNeedsWikipedia,
    findProvinceNamesFromText,
    getKnownLandmarkReply,
    buildNoWikipediaReply,
    buildConversationFallbackReply,
    stripDateText,
    formatProvinceContext,
    findFestivalContextFromText,
    buildContextForMessage,
    buildLocalFallbackReply,
    getWikipediaSearchQuery,
    isWikipediaResultRelevant,
    cleanWikipediaExtract,
    trimWikipediaReply,
    fetchWikipediaReply
  };
})();
