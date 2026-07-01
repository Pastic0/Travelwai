let global_memories;
const API_BASE_URL = "/api";

function getTravelwAIAuthToken() {
  const readCookie = (name) => {
    const value = `; ${document.cookie || ""}`;
    const parts = value.split(`; ${name}=`);
    return parts.length === 2 ? decodeURIComponent((parts.pop() || "").split(";").shift() || "") : "";
  };
  return localStorage.getItem("idToken") || sessionStorage.getItem("idToken") || localStorage.getItem("token") || sessionStorage.getItem("token") || readCookie("TravelwAIAuth") || "";
}
let currentUserSelectionFromSuggestion = null;
let emailsToShareList = [];
let selectedMemoryPhotos = [];
let shareSearchTimeoutInPanel;
let user_friendList = [];
const AI_PENDING_PROMPT_KEY = "travelwai-ai-pending-prompt";

function escapeHtml(text) {
  if (text === null || typeof text === "undefined") return "";
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function getMemoryItems() {
  return global_memories && Array.isArray(global_memories.data) ? global_memories.data : [];
}

function getMemoryIdValue(memory) {
  return memory?.id || memory?.memory_id || memory?.memoryId || memory?.Id || memory?.document_id || memory?.documentId || "";
}

function getMemoryCollectionValue(memory) {
  return memory?.memory_collection || memory?.collection || "";
}

function getMemoryTitleValue(memory) {
  return memory?.memory_name || memory?.name || "Kỷ niệm";
}

function getMemoryDescriptionValue(memory) {
  return (memory?.description || "").toString();
}

function normalizeMemoryPhotoUrl(url) {
  const value = (url || "").toString().trim();
  if (!value) return "";
  if (/^(https?:)?\/\//i.test(value) || value.startsWith("data:")) return value;
  return value.startsWith("/") ? value : `/${value}`;
}

function parseMemoryPhotoListValue(value) {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  if (typeof value === "string") {
    const text = value.trim();
    if (!text) return [];
    if (text.startsWith("[") && text.endsWith("]")) {
      try {
        const parsed = JSON.parse(text);
        return Array.isArray(parsed) ? parsed : [];
      } catch (_) {
        return text.split(/[;,]/);
      }
    }
    return text.split(/[;,]/);
  }
  if (typeof value === "object") return Object.values(value);
  return [];
}

function getMemoryPhotoUrlsValue(memory) {
  const urls = [
    ...parseMemoryPhotoListValue(memory?.photo_urls || memory?.photoUrls || memory?.photos),
    memory?.photo_url || memory?.photoUrl || memory?.image || "",
  ]
    .map(normalizeMemoryPhotoUrl)
    .filter(Boolean);

  return Array.from(new Set(urls));
}

function getMemoryPhotoUrlValue(memory) {
  return getMemoryPhotoUrlsValue(memory)[0] || "";
}

function renderMemoryPhotosHtml(memory, title, wrapperClass = "memory-image") {
  const urls = getMemoryPhotoUrlsValue(memory);
  if (urls.length === 0) return "";

  if (urls.length === 1) {
    return `<div class="${wrapperClass}"><img loading="lazy" decoding="async" src="${escapeHtml(urls[0])}" alt="${escapeHtml(title)}" /></div>`;
  }

  const shown = urls.slice(0, 4);
  const remaining = urls.length - shown.length;
  return `
    <div class="${wrapperClass} memory-photo-grid memory-photo-count-${Math.min(shown.length, 4)}">
      ${shown.map((url, index) => `
        <div class="memory-photo-grid-item">
          <img loading="lazy" decoding="async" src="${escapeHtml(url)}" alt="${escapeHtml(title)} ${index + 1}" />
          ${remaining > 0 && index === shown.length - 1 ? `<span class="memory-photo-more">+${remaining}</span>` : ""}
        </div>
      `).join("")}
    </div>
  `;
}

function getMemoryCreatedDateText(memory) {
  const createdAt = memory?.created_at || memory?.createdAt || "";
  if (!createdAt) return "";
  const date = new Date(createdAt);
  return Number.isNaN(date.getTime()) ? createdAt.toString() : date.toLocaleDateString("vi-VN");
}

function normalizeProvinceName(name) {
  return (name || "").toString().trim().toLowerCase();
}

function normalizeProvinceSearchText(value) {
  return (value || "")
    .toString()
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[–—]/g, "-")
    .replace(/\s+/g, " ")
    .trim();
}

function splitProvinceAttr(raw) {
  return (raw || "").split(/[;,]/).map((item) => item.trim()).filter(Boolean);
}

function normalizeProvinceTagKey(value) {
  return (value || "")
    .toString()
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/_+/g, "_");
}

function getProvinceTagAccent(tag) {
  const colors = {
    bien: "#0ea5e9",
    nui: "#22c55e",
    di_tich_lich_su: "#f97316",
    tho_mong: "#ec4899",
    khu_vui_choi: "#eab308",
  };
  return colors[normalizeProvinceTagKey(tag)] || "#6366f1";
}

function getProvinceTravelTags(provinceInfo) {
  const direct = provinceInfo?.travel_tags || provinceInfo?.plan_tags || provinceInfo?.tags;
  if (Array.isArray(direct) && direct.length) return direct.filter(Boolean);
  if (typeof direct === "string" && direct.trim()) {
    return direct.split(/[;,]/).map((item) => item.trim()).filter(Boolean);
  }
  return [];
}

function enrichProvinceInfoWithTravelTags(provinceInfo, requestedName) {
  const info = provinceInfo || {};
  const provinceName = requestedName || info.province_name || info.name || "";
  info.name = info.name || provinceName;
  info.province_name = info.province_name || provinceName;

  const tags = getProvinceTravelTags(info);
  info.travel_tags = tags;
  info.plan_tags = tags;

  return info;
}

function renderProvinceTravelTags(provinceInfo, className = "province-detail-tags") {
  const tags = getProvinceTravelTags(provinceInfo);
  if (!tags.length) return "";
  return `<div class="${className}">${tags.map((tag) => `<span style="--province-tag-accent:${getProvinceTagAccent(tag)}">${escapeHtml(tag)}</span>`).join("")}</div>`;
}

const PROVINCE_CULTURE_LABELS = [
  [["dia_danh_noi_tieng", "cau_chuyen_di_tich"], "Địa danh nổi tiếng và câu chuyện di tích"],
  [["le_hoi_dan_toc", "le_hoi_cac_dan_toc", "le_hoi_dia_phuong", "le_hoi_theo_thang"], "Lễ hội dân tộc và địa phương"],
  [["nganh_nghe_truyen_thong", "nghe_truyen_thong_mai_mot"], "Ngành nghề truyền thống"],
  ["nhan_vat_lich_su", "Nhân vật lịch sử"],
  ["truyen_thuyet_dia_phuong", "Truyền thuyết địa phương"]
];

function getMergedProvinceCultureText(culture, keys) {
  const source = culture || {};
  const list = Array.isArray(keys) ? keys : [keys];
  const seen = new Set();
  return list
    .map((key) => (source[key] || "").toString().trim())
    .filter(Boolean)
    .filter((value) => {
      const normalized = value.toLowerCase().replace(/\s+/g, " ");
      if (seen.has(normalized)) return false;
      seen.add(normalized);
      return true;
    })
    .join("; ");
}

function getProvinceCultureData(provinceInfo) {
  return provinceInfo?.culture && typeof provinceInfo.culture === "object" ? provinceInfo.culture : {};
}

function getProvinceLocalInfo34(provinceName) {
  const name = provinceName || "";
  if (typeof window.getStaticProvinceInfoFromLocal34 === "function") {
    return window.getStaticProvinceInfoFromLocal34(name);
  }
  if (typeof window.getLocalProvinceInfo === "function") {
    return window.getLocalProvinceInfo(name);
  }
  return null;
}

function mergeProvinceInfoWithLocal34(provinceInfo, requestedName) {
  const localInfo = getProvinceLocalInfo34(requestedName || provinceInfo?.province_name || provinceInfo?.name);
  const merged = { ...(localInfo || {}), ...(provinceInfo || {}) };

  if (localInfo?.culture && !provinceInfo?.culture) merged.culture = localInfo.culture;
  if (localInfo?.description && (!provinceInfo?.description || String(provinceInfo.description).trim().length < 20)) merged.description = localInfo.description;
  if (localInfo?.destinations && !provinceInfo?.destinations) merged.destinations = localInfo.destinations;
  if (localInfo?.current_festivals && !provinceInfo?.current_festivals) merged.current_festivals = localInfo.current_festivals;
  if (localInfo?.current_festival_summary && !provinceInfo?.current_festival_summary) merged.current_festival_summary = localInfo.current_festival_summary;

  return typeof window.refreshCurrentFestivalFields34 === "function"
    ? window.refreshCurrentFestivalFields34(merged)
    : merged;
}

function renderProvinceCultureQuickSummary(provinceInfo) {
  const culture = getProvinceCultureData(provinceInfo);
  const rows = [
    ["Địa danh và di tích", getMergedProvinceCultureText(culture, ["dia_danh_noi_tieng", "cau_chuyen_di_tich"])],
    ["Lễ hội", getMergedProvinceCultureText(culture, ["le_hoi_dan_toc", "le_hoi_cac_dan_toc", "le_hoi_dia_phuong", "le_hoi_theo_thang"])],
    ["Nghề truyền thống", getMergedProvinceCultureText(culture, ["nganh_nghe_truyen_thong", "nghe_truyen_thong_mai_mot"])]
  ].filter(([, value]) => value && value.toString().trim());

  if (!rows.length) return "";
  return `
    <div class="province-culture-quick">
      <div class="province-culture-quick-title">Văn hoá - lịch sử</div>
      ${rows.map(([label, value]) => `<p><strong>${escapeHtml(label)}:</strong> ${escapeHtml(value)}</p>`).join("")}
    </div>
  `;
}

function getProvinceCurrentFestivalItems(provinceInfo) {
  const provinceName = provinceInfo?.province_name || provinceInfo?.name || "";
  if (provinceName && typeof window.getCurrentFestivalItems34 === "function") {
    const freshItems = window.getCurrentFestivalItems34(provinceName);
    if (Array.isArray(freshItems)) return freshItems.filter(Boolean);
  }
  if (Array.isArray(provinceInfo?.current_festivals)) return provinceInfo.current_festivals.filter(Boolean);
  return [];
}

function renderProvinceCurrentFestivals(provinceInfo, compact = false) {
  const items = getProvinceCurrentFestivalItems(provinceInfo);
  const summary = items.length
    ? items.map((item) => item?.line || item?.name || item).filter(Boolean).join("; ")
    : (provinceInfo?.current_festival_summary || "Chưa có lễ hội tiêu biểu đang diễn ra");

  return `
    <div class="province-current-festivals${compact ? " province-current-festivals-compact" : ""}">
      <div class="province-current-festivals-title">Lễ hội đang diễn ra</div>
      <p>${escapeHtml(summary)}</p>
    </div>
  `;
}

function renderProvinceCultureSections(provinceInfo) {
  const culture = getProvinceCultureData(provinceInfo);
  const cards = PROVINCE_CULTURE_LABELS
    .map(([key, label]) => {
      const value = getMergedProvinceCultureText(culture, key);
      if (!value) return "";
      return `<article class="province-culture-card"><h5>${escapeHtml(label)}</h5><p>${escapeHtml(value)}</p></article>`;
    })
    .filter(Boolean)
    .join("");

  if (!cards) return `<p>Chưa có dữ liệu văn hoá cho tỉnh/thành này.</p>`;
  return `<div class="province-culture-grid">${cards}</div>`;
}

function getProvinceMergedNames(province) {
  return splitProvinceAttr(province?.getAttribute?.("data-old-provinces") || "");
}

function getProvinceAliases(province) {
  const merged = getProvinceMergedNames(province);
  const searchAliases = splitProvinceAttr(province?.getAttribute?.("data-search-aliases") || "");
  return [...new Set([...merged, ...searchAliases])];
}

function getProvinceSearchText(province) {
  const title = province?.getAttribute?.("title") || "";
  return normalizeProvinceSearchText([title, ...getProvinceAliases(province)].join(" "));
}

function getProvinceDefaultFill(provinceName) {
  return isProvinceInMemories(provinceName) ? (provinceColors[provinceName] || "#0c7489") : "#FFFFFF";
}

function setProvinceFill(province, fill) {
  if (!province) return;
  province.style.fill = fill;
}

function isProvinceInMemories(provinceName) {
  const canonicalName = typeof getCanonicalProvinceName34 === "function" ? getCanonicalProvinceName34(provinceName) : provinceName;
  return getMemoryItems().some((item) => {
    const saved = typeof getCanonicalProvinceName34 === "function" ? getCanonicalProvinceName34(item.province) : item.province;
    return saved === canonicalName;
  });
}

const provinceColors = {
  "Cao Bằng": "#60A5FA",
  "Điện Biên": "#34D399",
  "Lai Châu": "#FBBF24",
  "Lạng Sơn": "#F472B6",
  "Lào Cai": "#A78BFA",
  "Phú Thọ": "#22D3EE",
  "Sơn La": "#FB7185",
  "Thái Nguyên": "#4ADE80",
  "Tuyên Quang": "#60A5FA",
  "Thành phố Hà Nội": "#34D399",
  "Thành phố Hải Phòng": "#FBBF24",
  "Bắc Ninh": "#F472B6",
  "Hưng Yên": "#A78BFA",
  "Ninh Bình": "#22D3EE",
  "Quảng Ninh": "#FB7185",
  "Thành phố Huế": "#4ADE80",
  "Hà Tĩnh": "#60A5FA",
  "Nghệ An": "#34D399",
  "Quảng Trị": "#FBBF24",
  "Thanh Hóa": "#F472B6",
  "Thành phố Đà Nẵng": "#A78BFA",
  "Đắk Lắk": "#22D3EE",
  "Gia Lai": "#FB7185",
  "Khánh Hòa": "#4ADE80",
  "Lâm Đồng": "#60A5FA",
  "Quảng Ngãi": "#34D399",
  "Thành phố Hồ Chí Minh": "#FBBF24",
  "Đồng Nai": "#F472B6",
  "Tây Ninh": "#A78BFA",
  "Thành phố Cần Thơ": "#22D3EE",
  "An Giang": "#FB7185",
  "Cà Mau": "#4ADE80",
  "Đồng Tháp": "#60A5FA",
  "Vĩnh Long": "#34D399",
};

document.addEventListener("DOMContentLoaded", function () {
  if (typeof isAuthenticated !== "function") {
    console.error("Chưa tải được hàm xác thực, chuyển về trang đăng nhập.");
    window.location.href = "/login";
    return;
  }

  if (!isAuthenticated()) {
    window.location.href = "/login";
    return;
  }

  const profileIcon = document.getElementById("profileIcon");
  const scheduleIcon = document.getElementById("scheduleIcon");

  if (profileIcon) {
    profileIcon.addEventListener("click", function (e) {
      e.preventDefault();
      window.location.href = "/profile";
    });
  }

  if (scheduleIcon) {
    scheduleIcon.addEventListener("click", function (e) {
      e.preventDefault();
      window.location.href = "/schedule";
    });
  }

  const memoryJournalBtn = document.getElementById("memoryJournalBtn");
  if (memoryJournalBtn) {
    memoryJournalBtn.addEventListener("click", function () {
      showMemoryJournalModal();
    });
  }

  const festivalCalendarBtn = document.getElementById("festivalCalendarBtn");
  if (festivalCalendarBtn) {
    festivalCalendarBtn.addEventListener("click", function () {
      showFestivalCalendarModal();
    });
  }
});

document.addEventListener("DOMContentLoaded", async function () {
  try {
    const response = await fetch("vietnam.svg?v=2026-07-01-clean-v1");
    const svgContent = await response.text();

    const mapContainer = document.querySelector(".map-container");
    mapContainer.innerHTML = svgContent;

    const svgElement = mapContainer.querySelector("svg");
    if (svgElement) {
      svgElement.removeAttribute("width");
      svgElement.removeAttribute("height");
      svgElement.setAttribute("preserveAspectRatio", "xMidYMid meet");

      if (!svgElement.getAttribute("viewBox")) {
        const originalWidth = svgElement.width
          ? svgElement.width.baseVal.value
          : 800;
        const originalHeight = svgElement.height
          ? svgElement.height.baseVal.value
          : 600;
        svgElement.setAttribute(
          "viewBox",
          `0 0 ${originalWidth} ${originalHeight}`
        );
      }

      svgElement.style.width = "100%";
      svgElement.style.height = "100%";
      svgElement.style.maxWidth = "90%";
      svgElement.style.maxHeight = "90%";
      svgElement.style.display = "block";
      svgElement.style.margin = "auto";

      global_memories = { data: [] };
      user_friendList = { data: [] };

      try {
        global_memories = await get_user_memories();
      } catch (error) {
        console.warn("Không tải được kỷ niệm, vẫn cho phép bấm bản đồ:", error);
        global_memories = { data: [] };
      }

      try {
        user_friendList = await get_user_friendList();
      } catch (error) {
        console.warn("Không tải được danh sách bạn bè, vẫn cho phép bấm bản đồ:", error);
        user_friendList = { data: [] };
      }

      initializeMap();
    }
  } catch (error) {
    console.error("Lỗi tải bản đồ SVG:", error);
  }
});

function normalizeFriendListResponse(result) {
  if (Array.isArray(result)) return { data: result };
  if (result && Array.isArray(result.data)) return result;
  if (result && Array.isArray(result.friends)) return { ...result, data: result.friends };
  return { data: [] };
}

async function get_user_memories() {
  const token = getTravelwAIAuthToken();
  const get_memories_url = `${API_BASE_URL}/memories`;
  const response = await fetch(get_memories_url, {
    method: "GET",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
  });

  if (!response.ok) {
    throw new Error(`Không tải được kỷ niệm (${response.status})`);
  }

  const data = await response.json();
  return data && Array.isArray(data.data) ? data : { data: [] };
}

async function get_user_friendList() {
  const token = getTravelwAIAuthToken();
  const response = await fetch(`${API_BASE_URL}/friend_requests`, {
    method: "GET",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
  });

  if (!response.ok) {
    throw new Error(`Không tải được danh sách bạn bè (${response.status})`);
  }

  return normalizeFriendListResponse(await response.json());
}

function initializeMap() {
  const svgElement = document.querySelector(".map-container svg");
  if (!svgElement) return;

  const provinces = svgElement.querySelectorAll(".province");
  if (provinces.length === 0) {
    console.error('Không tìm thấy phần tử tỉnh/thành trên bản đồ SVG.');
    return;
  }

  adjustMapViewport();
  const fullMapViewBox = svgElement.getAttribute("viewBox");
  let selectedProvince = null;
  let selectionRequestId = 0;

  const resetButton = createResetButton(fullMapViewBox);
  document.querySelector(".map-container").appendChild(resetButton);
  setupMapZoomControls(fullMapViewBox, resetButton);
  window.resetProvinceMapZoom34 = function () {
    resetButton.click();
    resetButton.style.display = "none";
  };

  const resetSelectedProvinceStyle = () => {
    if (!selectedProvince) return;

    const provinceTitle = selectedProvince.getAttribute("title") || "";
    setProvinceFill(selectedProvince, getProvinceDefaultFill(provinceTitle));
    selectedProvince.classList.remove("selected", "search-highlight");
  };

  const loadProvincePanel = (provinceName) => {
    const currentRequestId = ++selectionRequestId;
    const detailPanelWasOpen = Boolean(document.querySelector(".province-detail-panel"));

    showProvinceLoadingPanel(provinceName);
    trackProvinceInterest34(provinceName, "province-map-open");

    getProvinceInfo(provinceName)
      .then((provinceInfo) => {
        if (currentRequestId !== selectionRequestId) return;

        showInfoPanel(provinceInfo);

        if (detailPanelWasOpen) {
          create_province_detail_Panel(provinceInfo);
        }
      })
      .catch((error) => {
        console.error("Lỗi tải thông tin tỉnh/thành:", error);
        if (currentRequestId !== selectionRequestId) return;

        const errorInfo = buildProvinceErrorInfo(provinceName);
        showInfoPanel(errorInfo);
        if (detailPanelWasOpen) {
          create_province_detail_Panel(errorInfo);
        }
      });
  };

  const selectProvince = (province, options = {}) => {
    if (!province) return;

    if (selectedProvince && selectedProvince !== province) {
      resetSelectedProvinceStyle();
    }

    selectedProvince = province;
    const provinceName = province.getAttribute("title") || "Không rõ tỉnh/thành";
    province.classList.remove("search-highlight");
    setProvinceFill(province, provinceColors[provinceName] || "#0c7489");
    province.classList.add("selected");

    if (options.zoom === true) {
      zoomToProvince(province);
      resetButton.style.display = "block";
    }

    loadProvincePanel(provinceName);
  };

  provinces.forEach((province) => {
    const provinceName = province.getAttribute("title") || "Không rõ tỉnh/thành";
    const isInMemories = isProvinceInMemories(provinceName);

    setProvinceFill(province, isInMemories ? (provinceColors[provinceName] || "#0c7489") : getProvinceDefaultFill(provinceName));
    province.addEventListener("click", function (event) {
      event.stopPropagation();
      selectProvince(this, { zoom: true });
    });
  });

  const openProvinceFromMapOrSearch = (province) => selectProvince(province, { zoom: true });
  const openProvinceFromList = (province) => selectProvince(province, { zoom: true });
  setupSearch(provinces, openProvinceFromMapOrSearch);
  renderProvinceQuickList(provinces, openProvinceFromList);
}

function cleanProvinceRegionTitle(title) {
  return (title || "")
    .toString()
    .replace(/\s*\([^)]*tỉnh[^)]*\)/gi, "")
    .replace(/^\s*(?:[IVXLCDM]+|\d+)\.\s*/i, "")
    .replace(/\s+/g, " ")
    .trim();
}

function getProvinceListMetaFromRegionGroups(provinceName) {
  const regionGroups = Array.isArray(window.VIETNAM_REGION_GROUPS) ? window.VIETNAM_REGION_GROUPS : [];
  for (const areaGroup of regionGroups) {
    for (const region of (areaGroup.regions || [])) {
      if ((region.provinces || []).includes(provinceName)) {
        return {
          area: areaGroup.area || cleanProvinceRegionTitle(areaGroup.title) || "Việt Nam",
          region: cleanProvinceRegionTitle(region.title) || "Chưa phân loại"
        };
      }
    }
  }

  return { area: "Việt Nam", region: "Chưa phân loại" };
}

function renderProvinceQuickList(provinces, onProvinceFound) {
  const listEl = document.getElementById("provinceQuickList");
  if (!listEl) return;

  const regionGroups = Array.isArray(window.VIETNAM_REGION_GROUPS) ? window.VIETNAM_REGION_GROUPS : [];
  const displayNames = window.VIETNAM_REGION_DISPLAY_NAMES instanceof Map ? window.VIETNAM_REGION_DISPLAY_NAMES : new Map();

  const provinceMap = new Map();
  const provinceArray = Array.from(provinces).map((province) => {
    const name = province.getAttribute("title") || province.getAttribute("data-province-name") || "Không rõ";
    const aliases = getProvinceAliases(province);
    const mergedNames = getProvinceMergedNames(province);
    const listMeta = getProvinceListMetaFromRegionGroups(name);
    const belongsToAttr = province.getAttribute("data-belongs-to") || "";
    const isArchipelago = province.classList.contains("province-archipelago") || province.getAttribute("data-is-archipelago") === "true" || Boolean(belongsToAttr);
    const item = {
      name,
      displayName: displayNames.get(name) || name,
      aliases,
      mergedNames,
      searchText: getProvinceSearchText(province),
      area: listMeta.area,
      region: listMeta.region,
      belongsTo: belongsToAttr,
      administrativeNote: "",
      isArchipelago,
      province
    };
    provinceMap.set(name, item);
    return item;
  });

  const renderButton = (item) => {
    const subText = item.isArchipelago
      ? (item.belongsTo ? `Thuộc ${item.belongsTo}` : `${item.area} - ${item.region}`)
      : `${item.area} - ${item.region}`;
    return `<button type="button" class="province-pill${item.isArchipelago ? " province-pill-archipelago" : ""}" data-province="${escapeHtml(item.name)}" data-search="${escapeHtml(item.searchText)} ${escapeHtml(normalizeProvinceSearchText(item.area))} ${escapeHtml(normalizeProvinceSearchText(item.region))} ${escapeHtml(normalizeProvinceSearchText(item.belongsTo))}"><strong>${escapeHtml(item.displayName)}</strong><span>${escapeHtml(subText)}</span></button>`;
  };

  const htmlParts = [];
  const rendered = new Set();

  regionGroups.forEach((areaGroup) => {
    htmlParts.push(`<section class="province-region-block"><h3>${escapeHtml(cleanProvinceRegionTitle(areaGroup.title))}</h3>`);
    (areaGroup.regions || []).forEach((region) => {
      htmlParts.push(`<div class="province-subregion-block"><h4>${escapeHtml(cleanProvinceRegionTitle(region.title))}</h4><div class="province-region-items">`);
      (region.provinces || []).forEach((provinceName) => {
        const item = provinceMap.get(provinceName);
        if (!item) return;
        rendered.add(item.name);
        htmlParts.push(renderButton(item));
      });
      htmlParts.push(`</div></div>`);
    });
    htmlParts.push(`</section>`);
  });

  const remainingAdministrative = provinceArray.filter((item) => !item.isArchipelago && !rendered.has(item.name));
  if (remainingAdministrative.length) {
    htmlParts.push(`<section class="province-region-block"><h3>Khác</h3><div class="province-region-items">`);
    remainingAdministrative.forEach((item) => htmlParts.push(renderButton(item)));
    htmlParts.push(`</div></section>`);
  }

  const archipelagoItems = provinceArray.filter((item) => item.isArchipelago);
  if (archipelagoItems.length) {
    htmlParts.push(`<section class="province-region-block archipelago-list-block"><h3>BIỂN ĐẢO VIỆT NAM</h3><div class="province-region-items">`);
    archipelagoItems.forEach((item) => htmlParts.push(renderButton(item)));
    htmlParts.push(`</div></section>`);
  }

  listEl.innerHTML = htmlParts.join("");

  listEl.querySelectorAll(".province-pill").forEach((button) => {
    button.addEventListener("click", () => {
      const name = button.getAttribute("data-province");
      const item = provinceArray.find((p) => p.name === name);
      if (item) onProvinceFound(item.province);
    });
  });
}
function setupSearch(provinces, onProvinceFound) {
  const searchBar = document.querySelector(".search-bar");
  if (!searchBar) return;

  const applySearch = () => {
    const searchTerm = normalizeProvinceSearchText(searchBar.value);
    const listButtons = document.querySelectorAll(".province-pill");

    listButtons.forEach((btn) => {
      const searchText = btn.getAttribute("data-search") || normalizeProvinceSearchText(btn.getAttribute("data-province"));
      btn.style.display = !searchTerm || searchText.includes(searchTerm) ? "flex" : "none";
    });

    provinces.forEach((province) => {
      if (province.classList.contains("selected")) return;
      const provinceName = province.getAttribute("title") || "";
      const isMatch = searchTerm && getProvinceSearchText(province).includes(searchTerm);
      province.classList.toggle("search-highlight", Boolean(isMatch));
      setProvinceFill(province, isMatch ? "#facc15" : getProvinceDefaultFill(provinceName));
    });
  };

  searchBar.addEventListener("input", applySearch);
  searchBar.addEventListener("keypress", function (e) {
    if (e.key === "Enter") {
      const searchTerm = normalizeProvinceSearchText(this.value);
      if (!searchTerm) return;

      for (const province of provinces) {
        if (getProvinceSearchText(province).includes(searchTerm)) {
          onProvinceFound(province);
          break;
        }
      }
    }
  });
}

function adjustMapViewport() {
  const svgElement = document.querySelector(".map-container svg");
  if (!svgElement) return;

  const viewBox = svgElement.getAttribute("viewBox") || "0 0 800 800";
  svgElement.setAttribute("viewBox", viewBox);
  svgElement.dataset.defaultViewBox = viewBox;
}

function createResetButton(defaultViewBox) {
  const resetButton = document.createElement("button");
  resetButton.textContent = "Xem toàn bộ bản đồ";
  resetButton.className = "reset-map-button";

  resetButton.addEventListener("click", function () {
    const svgElement = document.querySelector(".map-container svg");
    if (!svgElement) return;

    svgElement.setAttribute("viewBox", defaultViewBox);

    const selectedProvince = document.querySelector(".province.selected");
    if (selectedProvince) {
      const provinceTitle = selectedProvince.getAttribute("title") || "";
      setProvinceFill(selectedProvince, getProvinceDefaultFill(provinceTitle));
      selectedProvince.classList.remove("selected", "search-highlight");
    }

    document.querySelectorAll(".province-info-panel, .province-detail-panel, .memory-registration-panel:not(.province-detail-panel)").forEach((panel) => panel.remove());
    setProvinceSidePanelMode("info");

    this.style.display = "none";
  });

  return resetButton;
}

function parseViewBox(viewBox) {
  if (!viewBox) return null;
  const values = viewBox.trim().split(/[,\s]+/).map(Number);
  if (values.length !== 4 || values.some(Number.isNaN)) return null;
  return { x: values[0], y: values[1], width: values[2], height: values[3] };
}

function formatViewBox(box) {
  return `${box.x} ${box.y} ${box.width} ${box.height}`;
}

function setMapViewBoxSmooth(svgElement, viewBox) {
  svgElement.style.transition = "all 0.22s ease-in-out";
  svgElement.setAttribute("viewBox", formatViewBox(viewBox));
  window.clearTimeout(svgElement._mapZoomTransitionTimer);
  svgElement._mapZoomTransitionTimer = window.setTimeout(() => {
    svgElement.style.transition = "";
  }, 240);
}

function zoomMapByFactor(factor, defaultViewBox, anchorPoint) {
  const svgElement = document.querySelector(".map-container svg");
  if (!svgElement) return;

  const current = parseViewBox(svgElement.getAttribute("viewBox"));
  const defaults = parseViewBox(defaultViewBox || svgElement.dataset.defaultViewBox);
  if (!current || !defaults) return;

  const minWidth = defaults.width * 0.06;
  const maxWidth = defaults.width * 1.15;
  const newWidth = Math.min(maxWidth, Math.max(minWidth, current.width * factor));
  const scale = newWidth / current.width;
  const newHeight = current.height * scale;

  let next;

  if (anchorPoint && Number.isFinite(anchorPoint.x) && Number.isFinite(anchorPoint.y)) {
    const ratioX = Number.isFinite(anchorPoint.ratioX) ? anchorPoint.ratioX : 0.5;
    const ratioY = Number.isFinite(anchorPoint.ratioY) ? anchorPoint.ratioY : 0.5;
    next = {
      x: anchorPoint.x - newWidth * ratioX,
      y: anchorPoint.y - newHeight * ratioY,
      width: newWidth,
      height: newHeight,
    };
  } else {
    const centerX = current.x + current.width / 2;
    const centerY = current.y + current.height / 2;
    next = {
      x: centerX - newWidth / 2,
      y: centerY - newHeight / 2,
      width: newWidth,
      height: newHeight,
    };
  }

  setMapViewBoxSmooth(svgElement, next);
}

function getSvgPointFromMouse(svgElement, event) {
  const viewBox = parseViewBox(svgElement.getAttribute("viewBox"));
  if (!viewBox) return null;
  const rect = svgElement.getBoundingClientRect();
  if (!rect.width || !rect.height) return null;

  const ratioX = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
  const ratioY = Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height));
  const x = viewBox.x + ratioX * viewBox.width;
  const y = viewBox.y + ratioY * viewBox.height;

  return { x, y, ratioX, ratioY };
}

function setupMapZoomControls(defaultViewBox, resetButton) {
  const mapContainer = document.querySelector(".map-container");
  const svgElement = mapContainer?.querySelector("svg");
  if (!mapContainer || !svgElement) return;

  const oldControls = mapContainer.querySelector(".map-zoom-controls");
  if (oldControls) oldControls.remove();

  const controls = document.createElement("div");
  controls.className = "map-zoom-controls";
  controls.innerHTML = `
    <button type="button" class="map-zoom-in" aria-label="Phóng to bản đồ">+</button>
    <button type="button" class="map-zoom-out" aria-label="Thu nhỏ bản đồ">−</button>
    <button type="button" class="map-reset-zoom" aria-label="Đưa bản đồ về mặc định">100%</button>
  `;

  mapContainer.appendChild(controls);

  controls.querySelector(".map-zoom-in").addEventListener("click", () => zoomMapByFactor(0.82, defaultViewBox));
  controls.querySelector(".map-zoom-out").addEventListener("click", () => zoomMapByFactor(1.18, defaultViewBox));
  controls.querySelector(".map-reset-zoom").addEventListener("click", () => {
    if (resetButton) {
      resetButton.click();
      resetButton.style.display = "none";
    } else {
      svgElement.setAttribute("viewBox", defaultViewBox);
    }
  });

  mapContainer.addEventListener("wheel", (event) => {
    event.preventDefault();
    const mousePoint = getSvgPointFromMouse(svgElement, event);
    zoomMapByFactor(event.deltaY < 0 ? 0.88 : 1.12, defaultViewBox, mousePoint);
    if (resetButton) resetButton.style.display = "block";
  }, { passive: false });

  setupMiddleMousePan(mapContainer, svgElement);
}

function setupMiddleMousePan(mapContainer, svgElement) {
  if (mapContainer.dataset.middlePanReady === "true") return;
  mapContainer.dataset.middlePanReady = "true";

  let isPanning = false;
  let startClientX = 0;
  let startClientY = 0;
  let startViewBox = null;
  let panButton = null;

  const canPanWithLeftMouse = (event) => {
    const target = event.target;
    const clickedProvince = target && target.closest && target.closest(".province");
    const clickedControl = target && target.closest && target.closest("button, input, .map-zoom-controls, .province-info-panel, .province-list-panel");
    return event.button === 0 && !clickedProvince && !clickedControl;
  };

  const startPan = (event, button) => {
    const current = parseViewBox(svgElement.getAttribute("viewBox"));
    if (!current) return;

    event.preventDefault();
    isPanning = true;
    panButton = button;
    startClientX = event.clientX;
    startClientY = event.clientY;
    startViewBox = current;
    mapContainer.classList.add("middle-panning");
    document.body.classList.add("map-middle-pan-active");
  };

  const stopPan = () => {
    if (!isPanning) return;
    isPanning = false;
    panButton = null;
    mapContainer.classList.remove("middle-panning");
    document.body.classList.remove("map-middle-pan-active");
  };

  mapContainer.addEventListener("mousedown", (event) => {
    if (event.button === 1) {
      startPan(event, 1);
      return;
    }

    if (canPanWithLeftMouse(event)) {
      startPan(event, 0);
    }
  });

  window.addEventListener("mousemove", (event) => {
    if (!isPanning || !startViewBox) return;

    event.preventDefault();
    const rect = svgElement.getBoundingClientRect();
    if (!rect.width || !rect.height) return;

    const dx = event.clientX - startClientX;
    const dy = event.clientY - startClientY;
    const scaleX = startViewBox.width / rect.width;
    const scaleY = startViewBox.height / rect.height;

    svgElement.style.transition = "";
    svgElement.setAttribute(
      "viewBox",
      formatViewBox({
        x: startViewBox.x - dx * scaleX,
        y: startViewBox.y - dy * scaleY,
        width: startViewBox.width,
        height: startViewBox.height,
      })
    );
  });

  window.addEventListener("mouseup", (event) => {
    if (panButton === null || event.button === panButton) {
      if (panButton === 1) event.preventDefault();
      stopPan();
    }
  });

  mapContainer.addEventListener("mouseleave", stopPan);
  window.addEventListener("blur", stopPan);

  mapContainer.addEventListener("auxclick", (event) => {
    if (event.button === 1) {
      event.preventDefault();
    }
  });
}

function getProvinceSidePanel() {
  return document.getElementById("provinceSidePanel") || document.querySelector(".province-side-panel") || document.querySelector(".map-info-container");
}

function setProvinceSidePanelMode(mode) {
  const sidePanel = getProvinceSidePanel();
  if (!sidePanel) return;
  sidePanel.classList.toggle("province-side-panel-subpage", mode === "subpage");
}

function removeProvinceSubPanels() {
  document.querySelectorAll(".province-detail-panel, .memory-registration-panel:not(.province-detail-panel)").forEach((panel) => panel.remove());
  setProvinceSidePanelMode("info");
}

function zoomToProvince(province) {
  const svgElement = document.querySelector(".map-container svg");
  if (!svgElement || !province) return;

  const customZoom = {
    x: Number(province.getAttribute("data-zoom-x")),
    y: Number(province.getAttribute("data-zoom-y")),
    width: Number(province.getAttribute("data-zoom-width")),
    height: Number(province.getAttribute("data-zoom-height")),
  };

  const bbox = province.getBBox();
  const focusX = bbox.x + bbox.width / 2;
  const focusY = bbox.y + bbox.height / 2;

  let width;
  let height;

  if (Object.values(customZoom).every((value) => Number.isFinite(value) && value > -1)) {
    width = customZoom.width;
    height = customZoom.height;
  } else {
    const minWidth = 120;
    const minHeight = 120;
    width = Math.max(bbox.width * 8, minWidth);
    height = Math.max(bbox.height * 8, minHeight);
  }

  const focusRatioX = 0.5;
  const focusRatioY = 0.5;
  const viewBox = `${focusX - width * focusRatioX} ${focusY - height * focusRatioY} ${width} ${height}`;

  svgElement.style.transition = "all 0.5s ease-in-out";
  svgElement.setAttribute("viewBox", viewBox);

  setTimeout(() => {
    svgElement.style.transition = "";
  }, 500);
}

function buildProvinceErrorInfo(provinceName) {
  return {
    name: provinceName,
    province_name: provinceName,
    description: "Chưa tải được thông tin tỉnh/thành. Vui lòng thử lại sau.",
    area: "Việt Nam",
    region: "Chưa phân loại",
    subregion: "Chưa phân loại",
    travel_tags: [],
    plan_tags: [],
    current_festival_summary: "Chưa tải được dữ liệu lễ hội."
  };
}

function showProvinceLoadingPanel(provinceName) {
  removeProvinceSubPanels();
  const existingPanel = document.querySelector(".province-info-panel");
  if (existingPanel) existingPanel.remove();

  const infoPanel = document.createElement("div");
  infoPanel.className = "province-info-panel province-info-loading";
  infoPanel.setAttribute("data-province-name", provinceName || "");
  infoPanel.innerHTML = `
    <div class="province-info-header">
      <div><h2>${escapeHtml(provinceName || "Tỉnh/thành")}</h2></div>
      <button type="button" class="close-info-panel" aria-label="Đóng bảng thông tin">&times;</button>
    </div>
    <p>Đang tải thông tin tỉnh/thành...</p>
  `;

  const closeInfoBtn = infoPanel.querySelector(".close-info-panel");
  if (closeInfoBtn) {
    closeInfoBtn.addEventListener("click", function () {
      infoPanel.remove();
      if (typeof window.resetProvinceMapZoom34 === "function") {
        window.resetProvinceMapZoom34();
      }
    });
  }

  const sidePanel = getProvinceSidePanel();
  if (sidePanel) sidePanel.appendChild(infoPanel);
}

function showInfoPanel(provinceInfo) {

  removeProvinceSubPanels();
  const existingPanel = document.querySelector(".province-info-panel");
  if (existingPanel) {
    existingPanel.remove();
  }

  const infoPanel = document.createElement("div");
  infoPanel.className = "province-info-panel";
  infoPanel.setAttribute("data-province-name", provinceInfo.province_name || provinceInfo.name || "");

  const provinceName = provinceInfo.province_name || provinceInfo.name || "Tỉnh/thành";
  const belongsHtml = provinceInfo.belongs_to ? `<span><strong>Thuộc:</strong> ${escapeHtml(provinceInfo.belongs_to)}</span>` : "";
  const noteHtml = "";
  const statsHtml = [
    provinceInfo.natural_area_km2 ? `<span><strong>Diện tích:</strong> ${escapeHtml(provinceInfo.natural_area_km2)} km²</span>` : "",
    provinceInfo.population ? `<span><strong>Dân số:</strong> ${escapeHtml(provinceInfo.population)} người</span>` : ""
  ].filter(Boolean).join("");
  const travelTagsHtml = renderProvinceTravelTags(provinceInfo, "province-tags province-travel-tags");
  const currentFestivalsHtml = renderProvinceCurrentFestivals(provinceInfo, true);
  const cultureQuickHtml = renderProvinceCultureQuickSummary(provinceInfo);

  infoPanel.innerHTML = `
        <div class="province-info-header">
          <div>
            <h2>${escapeHtml(provinceName)}</h2>
          </div>
          <button type="button" class="close-info-panel" aria-label="Đóng bảng thông tin">&times;</button>
        </div>
        <div class="province-classification-row">
          <span><strong>Khu vực:</strong> ${escapeHtml(provinceInfo.area || "Việt Nam")}</span>
          <span><strong>Vùng:</strong> ${escapeHtml(provinceInfo.subregion || provinceInfo.region || "Chưa phân loại")}</span>
          ${belongsHtml}
          ${noteHtml}
          ${statsHtml}
        </div>
        ${travelTagsHtml}
        ${cultureQuickHtml}
        ${currentFestivalsHtml}
        <div class="province-actions-row">
          <button class="read-more-btn">Xem chi tiết</button>
          <button class="add-memory-btn">Thêm kỷ niệm</button>
          <button class="ask-ai-province-btn">Hỏi AI</button>
        </div>
    `;

  const closeInfoBtn = infoPanel.querySelector(".close-info-panel");
  if (closeInfoBtn) {
    closeInfoBtn.addEventListener("click", function () {
      infoPanel.remove();
      if (typeof window.resetProvinceMapZoom34 === "function") {
        window.resetProvinceMapZoom34();
      }
    });
  }

  const readMoreBtn = infoPanel.querySelector(".read-more-btn");
  readMoreBtn.addEventListener("click", function () {
    trackProvinceInterest34(provinceInfo.province_name || provinceInfo.name, "province-detail-panel");
    create_province_detail_Panel(provinceInfo);
  });

  const addMemoryBtn = infoPanel.querySelector(".add-memory-btn");
  addMemoryBtn.addEventListener("click", function () {

    showMemoryPanel(provinceInfo);
  });

  const askAiBtn = infoPanel.querySelector(".ask-ai-province-btn");
  if (askAiBtn) {
    askAiBtn.addEventListener("click", function () {
      trackProvinceInterest34(provinceInfo.province_name || provinceInfo.name, "province-ai-button");
      askAiAboutProvince(provinceInfo);
    });
  }

  const sidePanel = getProvinceSidePanel();
  if (sidePanel) sidePanel.appendChild(infoPanel);

  if (
    global_memories &&
    global_memories.data &&
    Array.isArray(global_memories.data)
  ) {
    const provinceSpecificMemories = global_memories.data.filter(
      (memory) => memoryBelongsToProvince(memory, provinceInfo.name || provinceInfo.province_name)
    );

    if (provinceSpecificMemories.length > 0) {
      displayMemoriesInPanel(provinceSpecificMemories, infoPanel);
    }
  } else {
    console.warn(
      "Không có dữ liệu kỷ niệm để hiển thị."
    );
  }
}

function buildProvinceAiPrompt(provinceInfo) {
  const provinceName = provinceInfo.province_name || provinceInfo.name || "tỉnh/thành này";
  return `Khám phá văn hoá, lịch sử, di tích và lễ hội nổi bật ở ${provinceName}.`;
}

function buildProvinceAiContext34(provinceInfo) { return ""; }

function askAiAboutProvince(provinceInfo) {
  const provinceName = provinceInfo.province_name || provinceInfo.name || "tỉnh/thành này";
  const prompt = buildProvinceAiPrompt(provinceInfo);
  const context = buildProvinceAiAiContext34(provinceInfo);

  try {
    localStorage.setItem(AI_PENDING_PROMPT_KEY, JSON.stringify({
      prompt,
      context,
      province: provinceName,
      assistant: "travelwai",
      source: "province-map",
      createdAt: new Date().toISOString(),
    }));
  } catch (error) {
    console.warn("Không thể lưu câu hỏi AI trước khi chuyển trang:", error);
  }

  window.location.href = "/messaging?ai=travelwai";
}

function create_province_detail_Panel(provinceInfo) {
  document.querySelectorAll(".province-detail-panel, .memory-registration-panel:not(.province-detail-panel)").forEach((panel) => panel.remove());
  setProvinceSidePanelMode("subpage");

  const detailPanel = document.createElement("div");
  detailPanel.className = "province-detail-panel memory-registration-panel";

  const provinceName = provinceInfo.province_name || provinceInfo.name;
  const belongsLine = provinceInfo.belongs_to ? `<p><strong>Thuộc:</strong> ${escapeHtml(provinceInfo.belongs_to)}</p>` : "";
  const noteLine = "";
  const travelTagsHtml = renderProvinceTravelTags(provinceInfo, "province-detail-tags");
  const currentFestivalsHtml = renderProvinceCurrentFestivals(provinceInfo);
  const cultureSectionsHtml = renderProvinceCultureSections(provinceInfo);

  detailPanel.innerHTML = `
    <div class="memory-panel-header">
      <h3>${escapeHtml(provinceName)}</h3>
      <button type="button" class="close-detail-panel close-info-panel" aria-label="Đóng xem chi tiết">&times;</button>
    </div>
    <div class="memory-panel-content">
      <div class="detail-section">
        <h4>Mô tả</h4>
        <p>${escapeHtml(provinceInfo.description || "Chưa có mô tả.")}</p>
      </div>
      <div class="detail-section">
        <h4>Tag du lịch</h4>
        ${travelTagsHtml || '<p>Chưa có tag du lịch.</p>'}
      </div>
      <div class="detail-section">
        <h4>Lễ hội theo thời gian thực</h4>
        ${currentFestivalsHtml}
      </div>
      <div class="detail-section">
        <h4>Văn hoá - lịch sử</h4>
        ${cultureSectionsHtml}
      </div>
      <div class="detail-section">
        <h4>Phân loại hành chính</h4>
        <p><strong>Khu vực:</strong> ${escapeHtml(provinceInfo.area || "Việt Nam")}</p>
        <p><strong>Vùng:</strong> ${escapeHtml(provinceInfo.subregion || provinceInfo.region || "Chưa phân loại")}</p>
        ${belongsLine}
        ${noteLine}
        ${provinceInfo.natural_area_km2 ? `<p><strong>Diện tích:</strong> ${escapeHtml(provinceInfo.natural_area_km2)} km²</p>` : ""}
        ${provinceInfo.population ? `<p><strong>Dân số:</strong> ${escapeHtml(provinceInfo.population)} người</p>` : ""}
      </div>
    </div>
  `;

  const sidePanel = getProvinceSidePanel();
  if (sidePanel) sidePanel.appendChild(detailPanel);

  const closeBtn = detailPanel.querySelector(".close-detail-panel");
  closeBtn.addEventListener("click", () => {
    detailPanel.remove();
    setProvinceSidePanelMode("info");
  });
}

const trackedProvinceInterest34 = new Set();

function trackProvinceInterest34(provinceName, source = "province-map-open") {
  const name = (provinceName || "").toString().trim();
  if (!name) return;
  const key = `${source}|${name.toLowerCase()}`;
  if (trackedProvinceInterest34.has(key)) return;
  trackedProvinceInterest34.add(key);

  fetch("/api/analytics/province-view", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ provinceName: name, source }),
    credentials: "same-origin",
    keepalive: true
  }).catch(() => {});
}

async function getProvinceInfo(provinceName) {
  const localInfo = getProvinceLocalInfo34(provinceName);
  if (localInfo) {
    return enrichProvinceInfoWithTravelTags(localInfo, provinceName);
  }

  if (typeof window.getLocalProvinceInfo === "function") {
    return enrichProvinceInfoWithTravelTags(window.getLocalProvinceInfo(provinceName), provinceName);
  }

  return enrichProvinceInfoWithTravelTags({
    name: provinceName,
    province_name: provinceName,
    area: "Việt Nam",
    region: "Chưa phân loại",
    subregion: "Chưa phân loại",
    description: `Khám phá văn hoá, lịch sử, lễ hội, làng nghề truyền thống, nhân vật lịch sử và địa danh nổi tiếng ở ${provinceName}.`,
    culture: {},
    current_festivals: [],
    current_festival_summary: "Chưa có lễ hội tiêu biểu đang diễn ra",
    destinations: []
  }, provinceName);
}

function showMemoryPanel(provinceInfo) {

  document.querySelectorAll(".province-detail-panel, .memory-registration-panel:not(.province-detail-panel)").forEach((panel) => panel.remove());
  setProvinceSidePanelMode("subpage");

  const memoryPanel = document.createElement("div");
  memoryPanel.className = "province-detail-panel memory-registration-panel memory-add-panel";

  currentUserSelectionFromSuggestion = null;
  emailsToShareList = [];
  selectedMemoryPhotos = [];

  memoryPanel.innerHTML = `
    <div class="memory-panel-header">
      <h3>Thêm kỷ niệm cho ${provinceInfo.name || provinceInfo.province_name}</h3>
      <button type="button" class="close-memory-panel close-detail-panel close-info-panel" aria-label="Đóng thêm kỷ niệm">&times;</button>
    </div>
    <div class="memory-panel-content">
      <form id="memoryForm" class="memory-form memory-detail-form">
        <div class="detail-section memory-form-section">
          <h4>Thông tin kỷ niệm</h4>
          <div class="form-group">
            <label for="memoryName">Tên kỷ niệm *</label>
            <input type="text" id="memoryName" name="memoryName" required placeholder="Đặt tên cho kỷ niệm...">
          </div>
          <div class="form-group">
            <label for="memoryDescription">Mô tả *</label>
            <textarea id="memoryDescription" name="memoryDescription" required placeholder="Mô tả trải nghiệm của bạn..." rows="4"></textarea>
          </div>
        </div>

        <div class="detail-section memory-form-section">
          <h4>Chia sẻ</h4>
          <div class="form-group">
            <label for="shareWithUserEmailInput">Chia sẻ với bạn bè (không bắt buộc)</label>
            <div class="memory-share-row">
              <input type="text" id="shareWithUserEmailInput" name="shareWithUserEmailInput" placeholder="Nhập tên hoặc email bạn bè..." oninput="searchUsersForSharingInPanel()">
              <button type="button" class="add-share-email-btn" onclick="addEmailToSharedList()">Thêm</button>
            </div>
            <div class="users-list" id="shareUserSuggestionListInPanel" ></div>
            <ul id="sharedEmailList"></ul>
          </div>
        </div>

        <div class="detail-section memory-form-section">
          <h4>Hình ảnh</h4>
          <div class="form-group">
            <label for="memoryPhoto">Tải ảnh lên</label>
            <div class="photo-upload-area">
              <input type="file" id="memoryPhoto" name="memoryPhotos" accept="image/*" multiple style="display: none;">
              <div class="upload-placeholder" onclick="document.getElementById('memoryPhoto').click()">
                <div class="upload-icon">📷</div>
                <p>Bấm để chọn ảnh</p>
                <span class="upload-hint">Chọn nhiều ảnh, mỗi ảnh tối đa 10MB</span>
              </div>
              <div class="photo-preview-grid" id="photoPreviewGrid" style="display: none;"></div>
            </div>
          </div>
        </div>

        <div class="detail-section memory-form-section memory-form-actions-section">
          <div class="form-actions">
            <button type="button" class="btn-cancel" onclick="closeMemoryPanel()">Hủy</button>
            <button type="submit" class="btn-save">Lưu kỷ niệm</button>
          </div>
        </div>
      </form>
    </div>
  `;

  const sidePanel = getProvinceSidePanel();
  if (sidePanel) sidePanel.appendChild(memoryPanel);

  setupMemoryPanelEventListeners(provinceInfo);
  renderSharedEmailsList();
}

function setupMemoryPanelEventListeners(provinceInfo) {

  const closeBtn = document.querySelector(".close-memory-panel");
  closeBtn.addEventListener("click", closeMemoryPanel);

  const photoInput = document.getElementById("memoryPhoto");
  photoInput.addEventListener("change", handlePhotoUpload);

  const memoryForm = document.getElementById("memoryForm");
  memoryForm.addEventListener("submit", function (e) {
    e.preventDefault();
    handleMemorySubmission(provinceInfo);
  });
}

function handlePhotoUpload(event) {
  const files = Array.from(event.target.files || []);
  if (files.length === 0) return;

  for (const file of files) {
    if (file.size > 10 * 1024 * 1024) {
      window.TravelwAIToast(`Ảnh ${file.name} phải nhỏ hơn 10MB`);
      continue;
    }

    if (!file.type.startsWith("image/")) {
      window.TravelwAIToast(`File ${file.name} không phải định dạng ảnh`);
      continue;
    }

    selectedMemoryPhotos.push(file);
  }

  event.target.value = "";
  renderMemoryPhotoPreviews();
}

function renderMemoryPhotoPreviews() {
  const previewGrid = document.getElementById("photoPreviewGrid");
  if (!previewGrid) return;

  if (!selectedMemoryPhotos.length) {
    previewGrid.innerHTML = "";
    previewGrid.style.display = "none";
    return;
  }

  previewGrid.style.display = "grid";
  previewGrid.innerHTML = selectedMemoryPhotos
    .map((file, index) => {
      const url = URL.createObjectURL(file);
      return `
        <div class="photo-preview-item">
          <img loading="lazy" decoding="async" src="${url}" alt="Ảnh kỷ niệm ${index + 1}">
          <button type="button" class="remove-photo" onclick="removeMemoryPhoto(${index})" aria-label="Xóa ảnh ${index + 1}">&times;</button>
        </div>
      `;
    })
    .join("");
}

function removeMemoryPhoto(index) {
  selectedMemoryPhotos.splice(index, 1);
  renderMemoryPhotoPreviews();
}

function removePhoto() {
  selectedMemoryPhotos = [];
  const photoInput = document.getElementById("memoryPhoto");
  if (photoInput) photoInput.value = "";
  renderMemoryPhotoPreviews();
}

async function handleMemorySubmission(provinceInfo) {
  const memoryName = document.getElementById("memoryName").value.trim();
  const memoryDescription = document
    .getElementById("memoryDescription")
    .value.trim();

  let memoryPhotos = selectedMemoryPhotos.slice();

  if (!memoryName) {
    window.TravelwAIToast("Vui lòng nhập tên kỷ niệm");
    document.getElementById("memoryName").focus();
    return;
  }

  if (!memoryDescription) {
    window.TravelwAIToast("Vui lòng nhập mô tả");
    document.getElementById("memoryDescription").focus();
    return;
  }

  if (window.TravelwAIImageOptimizer && memoryPhotos.length) {
    memoryPhotos = await window.TravelwAIImageOptimizer.optimizeImageFiles(memoryPhotos);
  }

  const formData = new FormData();
  formData.append("memory_name", memoryName);
  formData.append("description", memoryDescription);
  formData.append("province", provinceInfo.name || provinceInfo.province_name);

  formData.append("created_at", new Date().toISOString());
  memoryPhotos.forEach((photo) => formData.append("photos", photo, photo.name));

  const sharedEmails = (Array.isArray(emailsToShareList) ? emailsToShareList : [])
    .map((item) => {
      if (typeof item === "string") return item;
      return item?.email || item?.Email || item?.mail || "";
    })
    .map((email) => email.toString().trim())
    .filter(Boolean);
  sharedEmails.forEach((email) => formData.append("shared_emails", email));

  const saveBtn = document.querySelector(".btn-save");
  const originalText = saveBtn.textContent;
  saveBtn.textContent = "Đang lưu...";
  saveBtn.disabled = true;

  const token = getTravelwAIAuthToken();
  if (!token) {
    window.TravelwAIToast("Vui lòng đăng nhập để lưu kỷ niệm");
    window.location.href = "/login";

    saveBtn.textContent = originalText;
    saveBtn.disabled = false;
    return;
  }

  fetch(`${API_BASE_URL}/memories`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
    },
    body: formData,
  })
    .then(async (response) => {
      const result = await response.json();
      if (result.success) {

        global_memories.data.push(result.data);
        showNotification("Đã lưu kỷ niệm!", "success");

        closeMemoryPanel();

        const currentInfoPanel = document.querySelector(".province-info-panel");
        if (currentInfoPanel) {

          const existingMemoriesSection = currentInfoPanel.querySelector(
            ".user-memories-section"
          );
          if (existingMemoriesSection) {
            existingMemoriesSection.remove();
          }

          if (
            global_memories &&
            global_memories.data &&
            Array.isArray(global_memories.data)
          ) {
            const provinceSpecificMemories = global_memories.data.filter(
              (memory) => memoryBelongsToProvince(memory, provinceInfo.name || provinceInfo.province_name)
            );

            if (provinceSpecificMemories.length > 0) {
              displayMemoriesInPanel(
                provinceSpecificMemories,
                currentInfoPanel
              );
            }
          } else {
            console.warn(
              "global_memories not available for refreshing memories in panel."
            );
          }
        }
      } else {
        throw new Error(result.message || "Không thể lưu kỷ niệm");
      }
    })
    .catch((error) => {
      console.error("Lỗi lưu kỷ niệm:", error);
      showNotification(`Lỗi lưu kỷ niệm: ${error.message}`, "error");
    })
    .finally(() => {

      saveBtn.textContent = originalText;
      saveBtn.disabled = false;
    });
}

function closeMemoryPanel() {
  selectedMemoryPhotos = [];
  const memoryPanel = document.querySelector(".memory-add-panel, .memory-registration-panel:not(.province-detail-panel)");
  if (memoryPanel) {
    memoryPanel.style.animation = "fadeOut 0.3s ease-in-out";
    setTimeout(() => {
      memoryPanel.remove();
      setProvinceSidePanelMode("info");
    }, 300);
  } else {
    setProvinceSidePanelMode("info");
  }
}

function showNotification(message, type = "info") {

  const existingNotification = document.querySelector(".notification");
  if (existingNotification) {
    existingNotification.remove();
  }

  const notification = document.createElement("div");
  notification.className = `notification notification-${type}`;
  notification.textContent = message;

  document.body.appendChild(notification);

  setTimeout(() => {
    if (notification.parentNode) {
      notification.style.animation = "fadeOut 0.3s ease-in-out";
      setTimeout(() => {
        if (notification.parentNode) {
          notification.remove();
        }
      }, 300);
    }
  }, 3000);
}

async function showMemoryJournalModal() {
  const existingModal = document.querySelector(".memory-journal-modal");
  if (existingModal) existingModal.remove();

  const modal = document.createElement("div");
  modal.className = "memory-viewing-modal memory-journal-modal";
  modal.innerHTML = `
    <div class="modal-content">
      <div class="modal-header">
        <div>
          <h3>Nhật ký kỷ niệm</h3>
        </div>
        <button type="button" class="close-modal-btn" aria-label="Đóng nhật ký">&times;</button>
      </div>
      <div class="modal-body">
        <div class="memory-journal-loading">Đang tải kỷ niệm...</div>
      </div>
      <div class="modal-footer">
        <button type="button" class="btn-close-modal">Đóng</button>
      </div>
    </div>
  `;

  document.body.appendChild(modal);

  const closeModal = () => closeMemoryJournalModal(modal);
  modal.querySelector(".close-modal-btn")?.addEventListener("click", closeModal);
  modal.querySelector(".btn-close-modal")?.addEventListener("click", closeModal);
  modal.addEventListener("click", (event) => {
    if (event.target === modal) closeModal();
  });

  const cachedMemories = getMemoryItems();
  if (cachedMemories.length > 0) {
    renderMemoryJournalModal(modal, cachedMemories);
  }

  try {
    global_memories = await get_user_memories();
    renderMemoryJournalModal(modal, getMemoryItems());
  } catch (error) {
    console.error("Không tải được nhật ký kỷ niệm:", error);
    renderMemoryJournalModal(modal, getMemoryItems(), error.message || "Không tải được kỷ niệm");
  }
}

function formatProvinceCalendarMonthTitle(date) {
  const month = date.getMonth() + 1;
  const year = date.getFullYear();
  return `Lịch tháng ${String(month).padStart(2, "0")}/${year}`;
}

const PROVINCE_CALENDAR_SOLAR_HOLIDAYS_34 = Object.freeze([
  {
    "day": 1,
    "month": 1,
    "name": "Tết Dương lịch"
  },
  {
    "day": 9,
    "month": 1,
    "name": "Ngày truyền thống học sinh, sinh viên Việt Nam"
  },
  {
    "day": 3,
    "month": 2,
    "name": "Ngày thành lập Đảng Cộng sản Việt Nam"
  },
  {
    "day": 14,
    "month": 2,
    "name": "Lễ Tình nhân Valentine"
  },
  {
    "day": 27,
    "month": 2,
    "name": "Ngày Thầy thuốc Việt Nam"
  },
  {
    "day": 8,
    "month": 3,
    "name": "Ngày Quốc tế Phụ nữ"
  },
  {
    "day": 20,
    "month": 3,
    "name": "Ngày Quốc tế Hạnh phúc"
  },
  {
    "day": 22,
    "month": 3,
    "name": "Ngày Nước thế giới"
  },
  {
    "day": 26,
    "month": 3,
    "name": "Ngày thành lập Đoàn Thanh niên Cộng sản Hồ Chí Minh"
  },
  {
    "day": 1,
    "month": 4,
    "name": "Ngày Cá tháng Tư"
  },
  {
    "day": 21,
    "month": 4,
    "name": "Ngày Sách và Văn hóa đọc Việt Nam"
  },
  {
    "day": 22,
    "month": 4,
    "name": "Ngày Trái Đất"
  },
  {
    "day": 30,
    "month": 4,
    "name": "Ngày Giải phóng miền Nam, thống nhất đất nước"
  },
  {
    "day": 1,
    "month": 5,
    "name": "Ngày Quốc tế Lao động"
  },
  {
    "day": 7,
    "month": 5,
    "name": "Ngày Chiến thắng Điện Biên Phủ"
  },
  {
    "day": 15,
    "month": 5,
    "name": "Ngày thành lập Đội Thiếu niên Tiền phong Hồ Chí Minh"
  },
  {
    "day": 19,
    "month": 5,
    "name": "Ngày sinh Chủ tịch Hồ Chí Minh"
  },
  {
    "day": 1,
    "month": 6,
    "name": "Ngày Quốc tế Thiếu nhi"
  },
  {
    "day": 5,
    "month": 6,
    "name": "Ngày Môi trường thế giới"
  },
  {
    "day": 21,
    "month": 6,
    "name": "Ngày Báo chí Cách mạng Việt Nam"
  },
  {
    "day": 28,
    "month": 6,
    "name": "Ngày Gia đình Việt Nam"
  },
  {
    "day": 11,
    "month": 7,
    "name": "Ngày Dân số thế giới"
  },
  {
    "day": 27,
    "month": 7,
    "name": "Ngày Thương binh - Liệt sĩ"
  },
  {
    "day": 10,
    "month": 8,
    "name": "Ngày Vì nạn nhân chất độc da cam Việt Nam"
  },
  {
    "day": 19,
    "month": 8,
    "name": "Ngày Cách mạng Tháng Tám thành công"
  },
  {
    "day": 2,
    "month": 9,
    "name": "Quốc khánh Việt Nam"
  },
  {
    "day": 5,
    "month": 9,
    "name": "Ngày khai giảng năm học mới"
  },
  {
    "day": 10,
    "month": 9,
    "name": "Ngày thành lập Mặt trận Tổ quốc Việt Nam"
  },
  {
    "day": 10,
    "month": 10,
    "name": "Ngày Giải phóng Thủ đô"
  },
  {
    "day": 13,
    "month": 10,
    "name": "Ngày Doanh nhân Việt Nam"
  },
  {
    "day": 20,
    "month": 10,
    "name": "Ngày Phụ nữ Việt Nam"
  },
  {
    "day": 31,
    "month": 10,
    "name": "Halloween"
  },
  {
    "day": 9,
    "month": 11,
    "name": "Ngày Pháp luật Việt Nam"
  },
  {
    "day": 18,
    "month": 11,
    "name": "Ngày truyền thống Mặt trận Tổ quốc Việt Nam"
  },
  {
    "day": 20,
    "month": 11,
    "name": "Ngày Nhà giáo Việt Nam"
  },
  {
    "day": 23,
    "month": 11,
    "name": "Ngày Di sản Văn hóa Việt Nam"
  },
  {
    "day": 1,
    "month": 12,
    "name": "Ngày Thế giới phòng chống AIDS"
  },
  {
    "day": 22,
    "month": 12,
    "name": "Ngày thành lập Quân đội nhân dân Việt Nam"
  },
  {
    "day": 24,
    "month": 12,
    "name": "Đêm Giáng sinh"
  },
  {
    "day": 25,
    "month": 12,
    "name": "Lễ Giáng sinh"
  }
]);

const PROVINCE_CALENDAR_LUNAR_HOLIDAYS_34 = Object.freeze([
  {
    "day": 23,
    "month": 12,
    "name": "Tết ông Công ông Táo",
    "sourceSolarDate2026": "10/02/2026"
  },
  {
    "day": 29,
    "month": 12,
    "name": "Giao thừa Tết Nguyên đán",
    "sourceSolarDate2026": "16/02/2026"
  },
  {
    "day": 1,
    "month": 1,
    "name": "Mùng 1 Tết Nguyên đán",
    "sourceSolarDate2026": "17/02/2026"
  },
  {
    "day": 2,
    "month": 1,
    "name": "Mùng 2 Tết",
    "sourceSolarDate2026": "18/02/2026"
  },
  {
    "day": 3,
    "month": 1,
    "name": "Mùng 3 Tết",
    "sourceSolarDate2026": "19/02/2026"
  },
  {
    "day": 15,
    "month": 1,
    "name": "Rằm tháng Giêng, Tết Nguyên tiêu",
    "sourceSolarDate2026": "03/03/2026"
  },
  {
    "day": 3,
    "month": 3,
    "name": "Tết Hàn thực",
    "sourceSolarDate2026": "19/04/2026"
  },
  {
    "day": 10,
    "month": 3,
    "name": "Giỗ Tổ Hùng Vương",
    "sourceSolarDate2026": "26/04/2026"
  },
  {
    "day": 15,
    "month": 4,
    "name": "Lễ Phật Đản",
    "sourceSolarDate2026": "31/05/2026"
  },
  {
    "day": 5,
    "month": 5,
    "name": "Tết Đoan Ngọ",
    "sourceSolarDate2026": "19/06/2026"
  },
  {
    "day": 15,
    "month": 7,
    "name": "Lễ Vu Lan, Rằm tháng Bảy",
    "sourceSolarDate2026": "26/08/2026"
  },
  {
    "day": 15,
    "month": 8,
    "name": "Tết Trung thu",
    "sourceSolarDate2026": "25/09/2026"
  },
  {
    "day": 9,
    "month": 9,
    "name": "Tết Trùng Cửu",
    "sourceSolarDate2026": "18/10/2026"
  },
  {
    "day": 10,
    "month": 10,
    "name": "Tết Trùng Thập",
    "sourceSolarDate2026": "18/11/2026"
  },
  {
    "day": 15,
    "month": 10,
    "name": "Tết Hạ Nguyên",
    "sourceSolarDate2026": "23/11/2026"
  }
]);

let festivalCalendarState34 = null;

function padProvinceCalendarNumber34(value) {
  return String(value).padStart(2, "0");
}

function getProvinceCalendarDateKey34(date) {
  return `${date.getFullYear()}-${padProvinceCalendarNumber34(date.getMonth() + 1)}-${padProvinceCalendarNumber34(date.getDate())}`;
}

function formatProvinceCalendarSolarDate34(date) {
  return `${padProvinceCalendarNumber34(date.getDate())}/${padProvinceCalendarNumber34(date.getMonth() + 1)}/${date.getFullYear()}`;
}

function formatProvinceCalendarLunarDate34(date) {
  const lunar = typeof getVietnamLunarDate34 === "function" ? getVietnamLunarDate34(date) : null;
  return lunar ? `${padProvinceCalendarNumber34(lunar.day)}/${padProvinceCalendarNumber34(lunar.month)}` : "";
}

function getProvinceCalendarYearRange34() {
  const currentYear = new Date().getFullYear();
  const years = [];
  for (let year = currentYear - 3; year <= currentYear + 3; year += 1) years.push(year);
  return years;
}

function clampProvinceCalendarYear34(year) {
  const years = getProvinceCalendarYearRange34();
  return Math.min(Math.max(Number(year) || new Date().getFullYear(), years[0]), years[years.length - 1]);
}

function getProvinceCalendarHolidaysForDay(date = new Date()) {
  const solarDay = date.getDate();
  const solarMonth = date.getMonth() + 1;
  const lunarDate = typeof getVietnamLunarDate34 === "function" ? getVietnamLunarDate34(date) : null;
  const rows = [];

  PROVINCE_CALENDAR_SOLAR_HOLIDAYS_34.forEach((item) => {
    if (Number(item.day) === solarDay && Number(item.month) === solarMonth) {
      rows.push({
        type: "holiday",
        calendar: "solar",
        name: item.name,
        line: `${padProvinceCalendarNumber34(item.day)}/${padProvinceCalendarNumber34(item.month)} dương lịch: ${item.name}`
      });
    }
  });

  if (lunarDate) {
    PROVINCE_CALENDAR_LUNAR_HOLIDAYS_34.forEach((item) => {
      if (Number(item.day) === lunarDate.day && Number(item.month) === lunarDate.month) {
        rows.push({
          type: "holiday",
          calendar: "lunar",
          name: item.name,
          line: `${padProvinceCalendarNumber34(lunarDate.day)}/${padProvinceCalendarNumber34(lunarDate.month)} âm lịch: ${item.name}`
        });
      }
    });
  }

  return rows;
}

function normalizeProvinceCalendarText34(value) {
  return String(value || "")
    .normalize("NFC")
    .replace(/\s+/g, " ")
    .trim()
    .toLowerCase();
}

function sortProvinceCalendarNames34(values) {
  return Array.from(values || [])
    .filter(Boolean)
    .sort((a, b) => String(a).localeCompare(String(b), "vi"));
}

function getProvinceCalendarFestivalBaseLine34(event) {
  const dateText = typeof formatFestivalDate34 === "function" ? formatFestivalDate34(event).split(":")[0] : "";
  const ethnicText = typeof formatFestivalEthnic34 === "function" ? formatFestivalEthnic34(event) : "";
  return `${dateText}: ${event.name || "Lễ hội"}${ethnicText}`;
}

function groupProvinceCalendarFestivalEvents34(events) {
  const groups = new Map();

  (events || []).forEach((event) => {
    if (!event) return;
    const line = getProvinceCalendarFestivalBaseLine34(event);
    const key = [
      normalizeProvinceCalendarText34(event.name),
      normalizeProvinceCalendarText34(line),
      normalizeProvinceCalendarText34(event.calendar),
      normalizeProvinceCalendarText34(event.day),
      normalizeProvinceCalendarText34(event.month),
      normalizeProvinceCalendarText34(event.ethnic)
    ].join("|");

    if (!groups.has(key)) {
      groups.set(key, {
        type: "festival",
        name: event.name || "Lễ hội",
        line,
        provinces: new Set()
      });
    }

    if (event.province) groups.get(key).provinces.add(event.province);
  });

  return Array.from(groups.values()).map((item) => ({
    ...item,
    provinces: sortProvinceCalendarNames34(item.provinces)
  }));
}

function getProvinceCalendarFestivalEventsForDayRaw34(date = new Date()) {
  if (typeof getAllFestivalCalendarEvents34 !== "function" || typeof festivalMonthMatches34 !== "function" || typeof festivalDayMatches34 !== "function") return [];

  const solarMonth = date.getMonth() + 1;
  const solarDay = date.getDate();
  const lunarDate = typeof getVietnamLunarDate34 === "function" ? getVietnamLunarDate34(date) : null;

  return getAllFestivalCalendarEvents34()
    .filter((event) => {
      if (!event) return false;

      if (typeof festivalEventMatchesDate34 === "function") {
        return festivalEventMatchesDate34(event, date);
      }

      const calendarKey = typeof normalizeFestivalCalendarKey34 === "function" ? normalizeFestivalCalendarKey34(event.calendar) : event.calendar;
      const isSolar = calendarKey === "solar";
      const activeMonth = isSolar ? solarMonth : lunarDate?.month;
      const activeDay = isSolar ? solarDay : lunarDate?.day;

      if (!activeMonth || !activeDay) return false;
      return festivalMonthMatches34(event.month, activeMonth) && festivalDayMatches34(event.day, activeDay);
    });
}

function getProvinceCalendarFestivalsForDay(date = new Date()) {
  return groupProvinceCalendarFestivalEvents34(getProvinceCalendarFestivalEventsForDayRaw34(date)).slice(0, 60);
}

function getProvinceCalendarDayItems34(date = new Date()) {
  return [
    ...getProvinceCalendarHolidaysForDay(date),
    ...getProvinceCalendarFestivalsForDay(date)
  ];
}

function getProvinceCalendarHolidays(date = new Date()) {
  const year = date.getFullYear();
  const month = date.getMonth();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const rows = [];
  const seenKeys = new Set();

  for (let day = 1; day <= daysInMonth; day += 1) {
    const current = new Date(year, month, day);
    const solarText = formatProvinceCalendarSolarDate34(current);
    const lunarText = formatProvinceCalendarLunarDate34(current);
    getProvinceCalendarHolidaysForDay(current).forEach((item) => {
      const line = item.calendar === "lunar"
        ? `${solarText} - ${lunarText} âm lịch: ${item.name}`
        : `${solarText}: ${item.name}`;
      const key = line.toLowerCase();
      if (!seenKeys.has(key)) {
        seenKeys.add(key);
        rows.push(line);
      }
    });
  }

  return rows;
}

function buildProvinceMonthCalendarGrid(date = new Date(), selectedDate = date) {
  const year = date.getFullYear();
  const month = date.getMonth();
  const today = new Date();
  const firstDay = new Date(year, month, 1);
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const mondayOffset = (firstDay.getDay() + 6) % 7;
  const weekLabels = ["T2", "T3", "T4", "T5", "T6", "T7", "CN"];
  const selectedKey = selectedDate ? getProvinceCalendarDateKey34(selectedDate) : "";
  const cells = [];

  for (let i = 0; i < mondayOffset; i += 1) {
    cells.push('<div class="festival-calendar-cell festival-calendar-cell-empty"></div>');
  }

  for (let day = 1; day <= daysInMonth; day += 1) {
    const current = new Date(year, month, day);
    const lunar = typeof getVietnamLunarDate34 === "function" ? getVietnamLunarDate34(current) : null;
    const currentKey = getProvinceCalendarDateKey34(current);
    const isToday = today.getFullYear() === year && today.getMonth() === month && today.getDate() === day;
    const isSelected = selectedKey === currentKey;
    const dayItems = getProvinceCalendarDayItems34(current);
    const hasItems = dayItems.length > 0;
    const lunarText = lunar ? `${lunar.day}/${padProvinceCalendarNumber34(lunar.month)}` : "";
    cells.push(`
      <div class="festival-calendar-cell festival-calendar-cell-date${isToday ? " festival-calendar-cell-today" : ""}${isSelected ? " festival-calendar-cell-selected" : ""}${hasItems ? " festival-calendar-cell-has-items" : ""}" data-calendar-date="${currentKey}" role="button" tabindex="0" aria-label="Xem ngày ${day} tháng ${month + 1} năm ${year}">
        <strong>${day}</strong>
        <span>${escapeHtml(lunarText)}</span>
        ${hasItems ? '<em></em>' : ''}
      </div>
    `);
  }

  return `
    <div class="festival-calendar-weekdays">
      ${weekLabels.map((label) => `<span>${label}</span>`).join("")}
    </div>
    <div class="festival-calendar-grid">
      ${cells.join("")}
    </div>
  `;
}

function getProvinceCurrentMonthFestivalLines(date = new Date()) {
  const year = date.getFullYear();
  const month = date.getMonth();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const events = [];
  const seenKeys = new Set();

  for (let day = 1; day <= daysInMonth; day += 1) {
    const current = new Date(year, month, day);
    getProvinceCalendarFestivalEventsForDayRaw34(current).forEach((event) => {
      const key = [
        event.province || "",
        event.name || "",
        event.calendar || "lunar",
        event.day || "",
        event.month || "",
        event.ethnic || event.dan_toc || ""
      ]
        .map((value) => String(value).trim().toLowerCase())
        .join("|");

      if (seenKeys.has(key)) return;
      seenKeys.add(key);
      events.push(event);
    });
  }

  return groupProvinceCalendarFestivalEvents34(events).slice(0, 80);
}

function buildFestivalAiContext34(item) { return ""; }

function renderProvinceCalendarItem34(item) {
  if (typeof item === "string") return `<li>${escapeHtml(item)}</li>`;

  const line = escapeHtml(item?.line || "");
  const provinces = Array.isArray(item?.provinces) ? item.provinces.filter(Boolean) : [];
  const festivalContext = buildFestivalAiAiContext34(item);
  const festivalContextAttr = escapeHtml(festivalContext).replace(/\n/g, "&#10;");
  const askAiButton = item?.type === "festival"
    ? `<button type="button" class="ask-ai-province-btn festival-calendar-ask-ai-btn" data-festival-name="${escapeHtml(item?.name || "lễ hội này")}" data-festival-line="${escapeHtml(item?.line || "")}" data-festival-provinces="${escapeHtml(provinces.join(", "))}" data-festival-context="${festivalContextAttr}">Hỏi AI</button>`
    : "";

  if (!provinces.length) {
    return `<li><span class="festival-calendar-item-main">${line}</span>${askAiButton}</li>`;
  }

  return `
    <li>
      <span class="festival-calendar-item-main">${line}</span>
      <span class="festival-calendar-item-provinces">Tỉnh: ${escapeHtml(provinces.join(", "))}</span>
      ${askAiButton}
    </li>
  `;
}

function askAiAboutFestivalFromCalendar34(button) {
  const festivalName = button?.dataset?.festivalName || "lễ hội này";
  const provinces = button?.dataset?.festivalProvinces || "";
  const context = button?.dataset?.festivalContext || "";
  const provinceText = provinces ? ` ở ${provinces}` : "";
  const prompt = `Khám phá ${festivalName}${provinceText}: nguồn gốc, ý nghĩa văn hoá, lịch sử và điều thú vị cần biết.`;

  try {
    localStorage.setItem(AI_PENDING_PROMPT_KEY, JSON.stringify({
      prompt,
      context,
      assistant: "travelwai",
      festival: festivalName,
      source: "festival-calendar",
      createdAt: new Date().toISOString(),
    }));
  } catch (error) {
    console.warn("Không thể lưu câu hỏi AI lễ hội:", error);
  }

  window.location.href = "/messaging?ai=travelwai";
}

function renderProvinceCalendarList34(items, emptyText) {
  return items.length
    ? `<ul class="festival-calendar-scroll-list">${items.map(renderProvinceCalendarItem34).join("")}</ul>`
    : `<p>${escapeHtml(emptyText)}</p>`;
}

function syncProvinceCalendarListPanelHeight34(modal) {
  if (!modal) return;

  const board = modal.querySelector(".festival-calendar-board");
  const panel = modal.querySelector(".festival-calendar-list-panel");
  if (!board || !panel) return;

  panel.style.removeProperty("height");
  panel.style.removeProperty("max-height");

  if (window.matchMedia("(max-width: 900px)").matches) return;

  const boardHeight = Math.ceil(board.getBoundingClientRect().height);
  if (boardHeight > 0) {
    panel.style.setProperty("height", `${boardHeight}px`, "important");
    panel.style.setProperty("max-height", `${boardHeight}px`, "important");
  }
}

function limitProvinceCalendarListHeight34(modal) {
  if (!modal) return;

  syncProvinceCalendarListPanelHeight34(modal);

  modal.querySelectorAll(".festival-calendar-scroll-list").forEach((list) => {
    const rows = Array.from(list.children || []);
    list.classList.toggle("festival-calendar-scroll-list-active", rows.length > 2);
    list.style.removeProperty("max-height");

    if (rows.length <= 2) return;

    const computed = window.getComputedStyle(list);
    const gap = parseFloat(computed.rowGap || computed.gap || "8") || 8;
    const visibleHeight = rows
      .slice(0, 2)
      .reduce((total, row) => total + row.getBoundingClientRect().height, 0) + gap + 2;

    list.style.setProperty("max-height", `${Math.ceil(visibleHeight)}px`, "important");
  });
}

function buildProvinceCalendarControls34(date) {
  const month = date.getMonth() + 1;
  const year = date.getFullYear();
  const monthOptions = Array.from({ length: 12 }, (_, index) => index + 1)
    .map((value) => `<option value="${value}"${value === month ? " selected" : ""}>Tháng ${padProvinceCalendarNumber34(value)}</option>`)
    .join("");
  const yearOptions = getProvinceCalendarYearRange34()
    .map((value) => `<option value="${value}"${value === year ? " selected" : ""}>${value}</option>`)
    .join("");

  return `
    <div class="festival-calendar-controls">
      <select id="festivalCalendarMonthSelect" aria-label="Chọn tháng">${monthOptions}</select>
      <select id="festivalCalendarYearSelect" aria-label="Chọn năm">${yearOptions}</select>
    </div>
  `;
}

function normalizeProvinceCalendarSelectedDate34(state) {
  const now = new Date();
  const year = clampProvinceCalendarYear34(state?.year ?? now.getFullYear());
  const month = Math.min(Math.max(Number(state?.month ?? now.getMonth()) || 0, 0), 11);
  const maxDay = new Date(year, month + 1, 0).getDate();
  const selectedDay = Math.min(Math.max(Number(state?.selectedDay ?? now.getDate()) || 1, 1), maxDay);
  return { year, month, selectedDay };
}

function renderFestivalCalendarModalContent34(modal) {
  festivalCalendarState34 = normalizeProvinceCalendarSelectedDate34(festivalCalendarState34);
  const monthDate = new Date(festivalCalendarState34.year, festivalCalendarState34.month, 1);
  const selectedDate = new Date(festivalCalendarState34.year, festivalCalendarState34.month, festivalCalendarState34.selectedDay);
  const holidays = getProvinceCalendarHolidays(monthDate);
  const festivals = getProvinceCurrentMonthFestivalLines(monthDate);
  const dayHolidays = getProvinceCalendarHolidaysForDay(selectedDate);
  const dayFestivals = getProvinceCalendarFestivalsForDay(selectedDate);
  const selectedLunar = formatProvinceCalendarLunarDate34(selectedDate);

  modal.innerHTML = `
    <div class="modal-content">
      <div class="modal-header">
        <div>
          <h3>${escapeHtml(formatProvinceCalendarMonthTitle(monthDate))}</h3>
          ${buildProvinceCalendarControls34(monthDate)}
        </div>
        <button type="button" class="close-modal-btn" aria-label="Đóng lịch">&times;</button>
      </div>
      <div class="modal-body">
        <div class="festival-calendar-shell">
          <section class="festival-calendar-board">
            ${buildProvinceMonthCalendarGrid(monthDate, selectedDate)}
          </section>
          <section class="festival-calendar-list-panel">
            <div class="festival-calendar-list-block festival-calendar-selected-day-block">
              <h4>${escapeHtml(formatProvinceCalendarSolarDate34(selectedDate))}${selectedLunar ? ` · ${selectedLunar} âm lịch` : ""}</h4>
              <div class="festival-calendar-subblock">
                <h5>Ngày lễ</h5>
                ${renderProvinceCalendarList34(dayHolidays, "Không có ngày lễ tiêu biểu trong ngày này")}
              </div>
              <div class="festival-calendar-subblock">
                <h5>Lễ hội</h5>
                ${renderProvinceCalendarList34(dayFestivals, "Không có lễ hội tiêu biểu trong ngày này")}
              </div>
            </div>
            <div class="festival-calendar-list-block">
              <h4>Ngày lễ tháng này</h4>
              ${renderProvinceCalendarList34(holidays, "Chưa có ngày lễ tiêu biểu trong tháng này")}
            </div>
            <div class="festival-calendar-list-block">
              <h4>Lễ hội tháng này</h4>
              ${renderProvinceCalendarList34(festivals, "Chưa có lễ hội tiêu biểu đang diễn ra")}
            </div>
          </section>
        </div>
      </div>
      <div class="modal-footer">
        <button type="button" class="btn-close-modal">Đóng</button>
      </div>
    </div>
  `;

  limitProvinceCalendarListHeight34(modal);
  requestAnimationFrame(() => limitProvinceCalendarListHeight34(modal));
  bindFestivalCalendarModalEvents34(modal);
}

function bindFestivalCalendarModalEvents34(modal) {
  const closeModal = () => closeMemoryJournalModal(modal);
  modal.querySelector(".close-modal-btn")?.addEventListener("click", closeModal);
  modal.querySelector(".btn-close-modal")?.addEventListener("click", closeModal);

  modal.querySelector("#festivalCalendarMonthSelect")?.addEventListener("change", (event) => {
    festivalCalendarState34.month = Math.min(Math.max(Number(event.target.value) || 1, 1), 12) - 1;
    festivalCalendarState34.selectedDay = 1;
    renderFestivalCalendarModalContent34(modal);
  });

  modal.querySelector("#festivalCalendarYearSelect")?.addEventListener("change", (event) => {
    festivalCalendarState34.year = clampProvinceCalendarYear34(event.target.value);
    festivalCalendarState34.selectedDay = 1;
    renderFestivalCalendarModalContent34(modal);
  });

  modal.querySelectorAll(".festival-calendar-cell-date").forEach((cell) => {
    const selectDay = () => {
      const raw = cell.getAttribute("data-calendar-date") || "";
      const match = raw.match(/^(\d{4})-(\d{2})-(\d{2})$/);
      if (!match) return;
      festivalCalendarState34.year = Number(match[1]);
      festivalCalendarState34.month = Number(match[2]) - 1;
      festivalCalendarState34.selectedDay = Number(match[3]);
      renderFestivalCalendarModalContent34(modal);
    };
    cell.addEventListener("click", selectDay);
    cell.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectDay();
      }
    });
  });

  modal.querySelectorAll(".festival-calendar-ask-ai-btn").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      askAboutFestivalFromCalendar34(button);
    });
  });
}

function showFestivalCalendarModal() {
  const existingModal = document.querySelector(".festival-calendar-modal");
  if (existingModal) existingModal.remove();

  const now = new Date();
  festivalCalendarState34 = {
    year: clampProvinceCalendarYear34(now.getFullYear()),
    month: now.getMonth(),
    selectedDay: now.getDate()
  };

  const modal = document.createElement("div");
  modal.className = "memory-viewing-modal memory-journal-modal festival-calendar-modal";
  document.body.appendChild(modal);
  renderFestivalCalendarModalContent34(modal);

  modal.addEventListener("click", (event) => {
    if (event.target === modal) closeMemoryJournalModal(modal);
  });
}

function closeMemoryJournalModal(modal) {
  const target = modal || document.querySelector(".memory-journal-modal");
  if (!target) return;
  target.style.animation = "fadeOutModal 0.2s ease-in forwards";
  setTimeout(() => target.remove(), 200);
}

function renderMemoryJournalModal(modal, memories, errorMessage = "") {
  const body = modal?.querySelector?.(".modal-body");
  const title = modal?.querySelector?.(".modal-header h3");
  if (!body) return;

  const memoryList = Array.isArray(memories) ? memories : [];
  if (title) title.textContent = `Nhật ký kỷ niệm (${memoryList.length})`;

  if (errorMessage && memoryList.length === 0) {
    body.innerHTML = `
      <div class="memory-journal-empty">
        <strong>Không tải được kỷ niệm</strong>
        <p>${escapeHtml(errorMessage)}</p>
      </div>
    `;
    return;
  }

  if (memoryList.length === 0) {
    body.innerHTML = `
      <div class="memory-journal-empty">
        <strong>Chưa có kỷ niệm nào</strong>
      </div>
    `;
    return;
  }

  const cards = memoryList
    .map((memory) => {
      const memoryId = getMemoryIdValue(memory).toString();
      const memoryCollection = getMemoryCollectionValue(memory).toString();
      const title = getMemoryTitleValue(memory).toString();
      const description = getMemoryDescriptionValue(memory);
      const province = (memory?.province || "Chưa chọn tỉnh/thành").toString();
      const photoHtml = renderMemoryPhotosHtml(memory, title, "memory-image");
      const createdDate = getMemoryCreatedDateText(memory);

      return `
        <article class="memory-card memory-journal-card memory-item" data-memory-id="${escapeHtml(memoryId)}" data-memory-province="${escapeHtml(province)}" data-memory-collection="${escapeHtml(memoryCollection)}">
          ${photoHtml || `<div class="memory-image-placeholder"><span class="placeholder-icon">•</span><span>Không có ảnh</span></div>`}
          <div class="memory-details">
            <div class="memory-card-header">
              <h4>${escapeHtml(title)}</h4>
            </div>
            <div class="memory-province-pill">${escapeHtml(province)}</div>
            <div class="memory-journal-card-footer">
              ${createdDate ? `<small>Ngày tạo: ${escapeHtml(createdDate)}</small>` : ""}
              ${memoryId ? `<button type="button" class="delete-memory-btn memory-journal-delete-btn" data-memory-id="${escapeHtml(memoryId)}" data-memory-province="${escapeHtml(province)}" data-memory-collection="${escapeHtml(memoryCollection)}" aria-label="Xóa kỷ niệm ${escapeHtml(title)}">Xóa kỷ niệm</button>` : ""}
            </div>
          </div>
        </article>
      `;
    })
    .join("");

  body.innerHTML = `
    ${errorMessage ? `<div class="memory-journal-warning">${escapeHtml(errorMessage)}</div>` : ""}
    <div class="memory-journal-summary">Hiển thị tất cả ${memoryList.length} kỷ niệm của bạn</div>
    <div class="memories-grid memory-journal-grid">${cards}</div>
  `;

  body.querySelectorAll(".memory-journal-delete-btn").forEach((button) => {
    button.addEventListener("click", async (event) => {
      const deleted = await deleteMemoryFromMap(button.dataset.memoryId, button.dataset.memoryProvince, button, event);
      if (deleted) refreshOpenMemoryJournal();
    });
  });
}

function refreshOpenMemoryJournal() {
  const modal = document.querySelector(".memory-journal-modal");
  if (!modal) return;
  renderMemoryJournalModal(modal, getMemoryItems());
}

window.showMemoryJournalModal = showMemoryJournalModal;
window.showFestivalCalendarModal = showFestivalCalendarModal;

function displayMemoriesInPanel(memories, infoPanel) {
  if (!memories || memories.length === 0) {
    return;
  }

  const normalizePhotoUrl = (url) => {
    const value = (url || "").toString().trim();
    if (!value) return "";
    if (/^(https?:)?\/\//i.test(value) || value.startsWith("data:")) return value;
    return value.startsWith("/") ? value : `/${value}`;
  };

  const memoriesSection = document.createElement("div");
  memoriesSection.className = "user-memories-section";
  memoriesSection.innerHTML = `
    <h3>Kỷ niệm của bạn (${memories.length})</h3>
    <div class="memories-list">
      ${memories
        .map((memory) => {
          const memoryId = memory.id || memory.memory_id || memory.memoryId || memory.Id || memory.document_id || memory.documentId || "";
          const memoryCollection = memory.memory_collection || memory.collection || "";
          const title = memory.memory_name || memory.name || "Kỷ niệm";
          const description = memory.description || "";
          const shortDescription = description.length > 100 ? description.substring(0, 100) + "..." : description;
          const photoHtml = renderMemoryPhotosHtml(memory, title, "memory-photo");
          const createdAt = memory.created_at || memory.createdAt || new Date().toISOString();
          const createdDate = createdAt ? new Date(createdAt).toLocaleDateString("vi-VN") : "";
          return `
        <div class="memory-item" data-memory-id="${escapeHtml(memoryId)}" data-memory-province="${escapeHtml(memory.province || "")}" data-memory-collection="${escapeHtml(memoryCollection)}">
          <div class="memory-content">
            <div class="memory-card-header">
              <h4>${escapeHtml(title)}</h4>
              ${memoryId ? `<button type="button" class="delete-memory-btn" data-memory-id="${escapeHtml(memoryId)}" data-memory-province="${escapeHtml(memory.province || "")}" data-memory-collection="${escapeHtml(memoryCollection)}">Xóa</button>` : ""}
            </div>
            <p>${escapeHtml(shortDescription)}</p>
            <small>Ngày tạo: ${escapeHtml(createdDate)}</small>
          </div>
          ${photoHtml}
        </div>
      `;
        })
        .join("")}
    </div>
  `;

  const buttonsContainer = infoPanel.querySelector(".province-actions-row");
  if (buttonsContainer && buttonsContainer.parentNode) {
    buttonsContainer.parentNode.insertBefore(memoriesSection, buttonsContainer);
  }

  memoriesSection.querySelectorAll(".delete-memory-btn").forEach((button) => {
    button.addEventListener("click", (event) => deleteMemoryFromMap(button.dataset.memoryId, button.dataset.memoryProvince, button, event));
  });
}

function memoryBelongsToProvince(memory, provinceName) {
  const canonicalMemoryProvince = typeof getCanonicalProvinceName34 === "function" ? getCanonicalProvinceName34(memory?.province) : memory?.province;
  const canonicalProvince = typeof getCanonicalProvinceName34 === "function" ? getCanonicalProvinceName34(provinceName) : provinceName;
  return canonicalMemoryProvince === canonicalProvince;
}

function refreshProvinceMemoryColors() {
  const provinces = document.querySelectorAll(".map-container svg .province");
  provinces.forEach((province) => {
    if (province.classList.contains("selected")) return;
    const provinceName = province.getAttribute("title") || "";
    setProvinceFill(province, getProvinceDefaultFill(provinceName));
  });
}

async function deleteMemoryFromMap(memoryId, provinceName, button, event) {
  if (event) {
    event.preventDefault();
    event.stopPropagation();
  }

  memoryId = (memoryId || "").toString().trim();
  if (!memoryId) {
    showNotification("Không tìm thấy mã kỷ niệm", "error");
    return false;
  }
  if (!await window.TravelwAIConfirm("Xóa kỷ niệm này?")) return false;

  const token = getTravelwAIAuthToken();
  if (!token) {
    window.TravelwAIToast("Vui lòng đăng nhập để xóa kỷ niệm");
    window.location.href = "/login";
    return false;
  }

  const originalText = button?.textContent || "Xóa";
  if (button) {
    button.textContent = "Đang xóa...";
    button.disabled = true;
  }

  const headers = { Authorization: `Bearer ${token}` };
  const deleteUrl = `${API_BASE_URL}/memories/${encodeURIComponent(memoryId)}`;

  async function parseResult(response) {
    const text = await response.text().catch(() => "");
    if (!text) return {};
    try {
      return JSON.parse(text);
    } catch (_) {
      return { detail: text };
    }
  }

  try {
    let response = await fetch(deleteUrl, {
      method: "DELETE",
      headers,
      credentials: "same-origin",
    });

    if (response.status === 405 || response.status === 404) {
      response = await fetch(`${deleteUrl}/delete`, {
        method: "POST",
        headers,
        credentials: "same-origin",
      });
    }

    const result = await parseResult(response);
    if (!response.ok || result.success === false) {
      throw new Error(result.message || result.detail || "Không thể xóa kỷ niệm");
    }

    if (global_memories && Array.isArray(global_memories.data)) {
      global_memories.data = global_memories.data.filter((memory) => String(memory.id || memory.memory_id || memory.memoryId || memory.Id || memory.document_id || memory.documentId || "") !== String(memoryId));
    }

    const memoryItem = button?.closest?.(".memory-item");
    if (memoryItem) memoryItem.remove();

    const infoPanel = document.querySelector(".province-info-panel");
    if (infoPanel) {
      infoPanel.querySelector(".user-memories-section")?.remove();
      const panelProvinceName = provinceName || infoPanel.getAttribute("data-province-name") || "";
      const remainingMemories = getMemoryItems().filter((memory) => memoryBelongsToProvince(memory, panelProvinceName));
      if (remainingMemories.length > 0) {
        displayMemoriesInPanel(remainingMemories, infoPanel);
      }
    }

    try {
      global_memories = await get_user_memories();
    } catch (_) { }

    refreshProvinceMemoryColors();
    refreshOpenMemoryJournal();
    showNotification(result.message || "Đã xóa kỷ niệm", "success");
    return true;
  } catch (error) {
    console.error("Lỗi xóa kỷ niệm:", error);
    showNotification(error.message || "Không thể xóa kỷ niệm", "error");
    if (button) {
      button.textContent = originalText;
      button.disabled = false;
    }
    return false;
  }
}

window.deleteMemoryFromMap = deleteMemoryFromMap;
window.removeMemoryPhoto = removeMemoryPhoto;

async function searchUsersForSharingInPanel() {
  const query = document.getElementById("shareWithUserEmailInput").value.trim();
  const usersListContainer = document.getElementById(
    "shareUserSuggestionListInPanel"
  );

  if (
    query !==
    (currentUserSelectionFromSuggestion
      ? currentUserSelectionFromSuggestion.email
      : "")
  ) {
    currentUserSelectionFromSuggestion = null;
  }

  if (!query || query.length < 2) {
    usersListContainer.innerHTML = "";
    usersListContainer.style.display = "none";
    return;
  }

  usersListContainer.innerHTML =
    '<div style="padding: 8px; text-align: center; color: #555;">Đang tìm...</div>';
  usersListContainer.style.display = "block";

  clearTimeout(shareSearchTimeoutInPanel);
  const friends = Array.isArray(user_friendList?.data) ? user_friendList.data : [];
  const normalizedQuery = normalizeShareText(query);
  const filteredFriends = friends.filter((user) => {
    const fields = [
      user.email,
      user.username,
      user.name,
      user.fullName,
      user.displayName,
    ].map(normalizeShareText).filter(Boolean);
    return fields.some((field) => field.includes(normalizedQuery));
  });
  renderShareUserResultsInPanel(filteredFriends);
}

function renderShareUserResultsInPanel(users) {
  const usersListContainer = document.getElementById(
    "shareUserSuggestionListInPanel"
  );
  users = Array.isArray(users) ? users : [];
  usersListContainer.innerHTML = "";

  if (users.length === 0) {
    usersListContainer.innerHTML =
      '<div style="padding: 8px; text-align: center; color: #555;">Không tìm thấy người dùng.</div>';
    usersListContainer.style.display = "block";
    return;
  }

  users.forEach((user) => {
    if (!isShareTargetAlreadyAdded(getShareUserEmail(user))) {
      const userItem = createShareUserElementInPanel(user);
      usersListContainer.appendChild(userItem);
    }
  });
  if (usersListContainer.children.length === 0 && users.length > 0) {
    usersListContainer.innerHTML =
      '<div style="padding: 8px; text-align: center; color: #555;">Tất cả người dùng đã được thêm hoặc chọn.</div>';
  }
  usersListContainer.style.display =
    usersListContainer.children.length > 0 ? "block" : "none";
}

function createShareUserElementInPanel(user) {
  const div = document.createElement("div");
  div.className = "share-user-item";
  div.onclick = () => selectUserFromSuggestions(user);

  const avatarSrc = user.profilePic
    ? `${API_BASE_URL.replace("/api", "")}${user.profilePic}`
    : null;
  const avatarHTML = avatarSrc
    ? `<img loading="lazy" decoding="async" src="${avatarSrc}" alt="${escapeHtml(
        user.username || "U"
      )}" style="width: 30px; height: 30px; border-radius: 50%; object-fit: cover; margin-right: 8px; vertical-align: middle;" onerror="this.style.display='none'; this.nextSibling.style.display='inline-block';" />`
    : "";
  const initialHTML = `<span style="display: ${
    avatarSrc ? "none" : "inline-block"
  }; width: 30px; height: 30px; border-radius: 50%; background-color: #ccc; color: white; text-align: center; line-height: 30px; font-weight: bold; margin-right: 8px; vertical-align: middle;">${(
    escapeHtml(user.username) || "U"
  )
    .charAt(0)
    .toUpperCase()}</span>`;

  div.innerHTML = `
    ${avatarHTML}
    ${initialHTML}
    <span style="vertical-align: middle;">
        <strong style="display: block; font-size: 0.9em;">${escapeHtml(
          user.username || "Unknown User"
        )}</strong>
        <small style="color: #bfdbfe; font-size: 0.8em;">${escapeHtml(
          user.email || ""
        )}</small>
    </span>`;
  return div;
}

function selectUserFromSuggestions(user) {
  currentUserSelectionFromSuggestion = user;
  const displayName = getShareUserDisplayName(user);
  document.getElementById("shareWithUserEmailInput").value = displayName && user.email ? `${displayName} (${user.email})` : (user.email || displayName || "");
  document.getElementById("shareUserSuggestionListInPanel").innerHTML = "";
  document.getElementById("shareUserSuggestionListInPanel").style.display = "none";
}

function getShareUserDisplayName(user) {
  return (user?.username || user?.name || user?.fullName || user?.displayName || user?.email || "").toString().trim();
}

function getShareUserEmail(user) {
  return (user?.email || user?.Email || user?.mail || "").toString().trim();
}

function normalizeShareText(value) {
  return (value || "")
    .toString()
    .trim()
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/\s+/g, " ");
}

function getShareTargetEmail(item) {
  if (typeof item === "string") return item.toString().trim();
  return getShareUserEmail(item);
}

function isShareTargetAlreadyAdded(targetEmail) {
  const normalizedEmail = normalizeShareText(targetEmail);
  return emailsToShareList.some((item) => normalizeShareText(getShareTargetEmail(item)) === normalizedEmail);
}

function findFriendForShareInput(rawValue) {
  const friends = Array.isArray(user_friendList?.data) ? user_friendList.data : [];
  const cleanedValue = rawValue.replace(/\(([^)]+@[^)]+)\)/, "$1").trim();
  const normalizedValue = normalizeShareText(cleanedValue);
  if (!normalizedValue) return null;

  const exactMatches = friends.filter((user) => {
    const fields = [
      getShareUserEmail(user),
      user?.username,
      user?.name,
      user?.fullName,
      user?.displayName,
    ].map(normalizeShareText).filter(Boolean);
    return fields.includes(normalizedValue);
  });
  if (exactMatches.length === 1) return exactMatches[0];

  const containsMatches = friends.filter((user) => {
    const fields = [
      getShareUserEmail(user),
      user?.username,
      user?.name,
      user?.fullName,
      user?.displayName,
    ].map(normalizeShareText).filter(Boolean);
    return fields.some((field) => field.includes(normalizedValue));
  });
  return containsMatches.length === 1 ? containsMatches[0] : null;
}

function addEmailToSharedList() {
  const emailInput = document.getElementById("shareWithUserEmailInput");
  const rawValue = emailInput.value.trim();
  const suggestionListContainer = document.getElementById("shareUserSuggestionListInPanel");

  if (!rawValue) return;

  const selectedUser = currentUserSelectionFromSuggestion || findFriendForShareInput(rawValue);
  if (selectedUser) {
    const selectedEmail = getShareUserEmail(selectedUser);
    if (!selectedEmail) {
      window.TravelwAIToast("Tài khoản này chưa có email để chia sẻ.");
      return;
    }
    if (!isShareTargetAlreadyAdded(selectedEmail)) {
      emailsToShareList.push({
        id: selectedUser.id || selectedUser.userId || selectedUser.user_id || selectedEmail,
        name: getShareUserDisplayName(selectedUser),
        email: selectedEmail,
      });
    }
  } else {
    const emailValue = rawValue.replace(/.*\(([^)]+@[^)]+)\).*/, "$1").trim();
    if (!emailValue.includes("@") || !emailValue.includes(".")) {
      window.TravelwAIToast("Vui lòng nhập đúng email hoặc tên bạn bè trong danh sách gợi ý.");
      return;
    }
    if (!isShareTargetAlreadyAdded(emailValue)) {
      emailsToShareList.push(emailValue);
    }
  }

  renderSharedEmailsList();
  emailInput.value = "";
  currentUserSelectionFromSuggestion = null;
  if (suggestionListContainer) {
    suggestionListContainer.innerHTML = "";
    suggestionListContainer.style.display = "none";
  }
  emailInput.focus();
}

function renderSharedEmailsList() {
  const listElement = document.getElementById("sharedEmailList");
  if (!listElement) return;
  listElement.innerHTML = "";

  if (emailsToShareList.length === 0) {
    listElement.style.display = "none";
    return;
  }
  listElement.style.display = "block";

  emailsToShareList.forEach((item, index) => {
    const listItem = document.createElement("li");
    listItem.style.display = "flex";
    listItem.style.justifyContent = "space-between";
    listItem.style.alignItems = "center";
    listItem.style.padding = "5px 0";
    listItem.style.borderBottom = "1px solid #eee";

    const textSpan = document.createElement("span");
    if (typeof item === "object" && item.email) {
      textSpan.textContent = `${item.name || item.email} (${item.email})`;
    } else {
      textSpan.textContent = item;
    }

    const removeButton = document.createElement("button");
    removeButton.innerHTML = "&times;";
    removeButton.style.marginLeft = "10px";
    removeButton.style.cursor = "pointer";
    removeButton.style.border = "none";
    removeButton.style.background = "transparent";
    removeButton.style.color = "red";
    removeButton.style.fontSize = "1.2em";
    removeButton.onclick = () => removeEmailFromSharedList(index);

    listItem.appendChild(textSpan);
    listItem.appendChild(removeButton);
    listElement.appendChild(listItem);
  });
}

function removeEmailFromSharedList(index) {
  if (index >= 0 && index < emailsToShareList.length) {
    emailsToShareList.splice(index, 1);
    renderSharedEmailsList();
  }
}
