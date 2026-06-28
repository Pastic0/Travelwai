let travelwaiPosts = [];
let postSearchQuery = "";
let currentPostUser = null;
let selectedPublicPostImages = [];
let editingPublicPostId = "";
let editingPublicPostExistingImages = [];
try { localStorage.removeItem("travelwaiPostDisplayMode"); } catch (_) {}
let postOwnerFilterMode = localStorage.getItem("travelwaiPostOwnerFilter") === "mine" ? "mine" : "all";
let postTourOfferStatus = { has_offer: false, discount_percent: 0, progress: 0, target: 1, message: "" };

function normalizeAccountRoleForPosts(value) {
  const role = String(value || localStorage.getItem("userRole") || "Free").trim().toLowerCase();
  if (role === "user") return "free";
  if (role === "company") return "business";
  if (role === "tour sales" || role === "toursales") return "sales";
  return role || "free";
}

function currentPostAccountRole() {
  return normalizeAccountRoleForPosts(currentPostUser?.role || currentPostUser?.userRole || localStorage.getItem("userRole"));
}

function canUsePostAi() {
  return currentPostAccountRole() !== "free";
}

function canUsePostOffer() {
  const role = currentPostAccountRole();
  return role !== "free" && role !== "vip";
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

function escapeJsString(value) {
  return String(value ?? "")
    .replace(/\\/g, "\\\\")
    .replace(/'/g, "\\'")
    .replace(/\r/g, "")
    .replace(/\n/g, "\\n");
}

function normalizeSearchText(value) {
  return String(value ?? "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/\s+/g, " ")
    .trim();
}

function currentPostMonth() {
  return new Date().getMonth() + 1;
}

function getValue(item, ...keys) {
  for (const key of keys) {
    if (item && item[key] !== undefined && item[key] !== null && item[key] !== "") return item[key];
  }
  return "";
}

async function readJson(response) {
  const data = await response.json().catch(() => ({}));
  if (!response.ok || data.success === false) throw new Error(data.message || "Tải dữ liệu thất bại");
  return data;
}

function showToast(message) {
  const toast = document.getElementById("tourToast");
  if (!toast) return window.TravelwAIToast(message);
  toast.textContent = message;
  toast.classList.add("show");
  setTimeout(() => toast.classList.remove("show"), 2600);
}

async function loadPostTourOfferStatus(silent = false) {
  if (!canUsePostOffer()) {
    postTourOfferStatus = { has_offer: false, discount_percent: 0, progress: 0, target: 1, message: "Gói Free và VIP chưa dùng được ưu đãi bài viết." };
    renderPostTourOfferStatus();
    return;
  }
  try {
    const result = await readJson(await authenticatedFetch("/api/tour-offers/post-status"));
    postTourOfferStatus = {
      has_offer: Boolean(result.has_offer || result.post_offer_active),
      discount_percent: Number(result.discount_percent || 0),
      progress: Number(result.progress || 0),
      target: Number(result.target || 1),
      message: result.message || ""
    };
    renderPostTourOfferStatus();
  } catch (error) {
    if (!silent) showToast(error.message || "Không tải được ưu đãi.");
  }
}

function renderPostTourOfferStatus() {
  const discount = Math.max(0, Number(postTourOfferStatus.discount_percent || 0));
  const target = Math.max(1, Number(postTourOfferStatus.target || 1));
  const progress = Math.max(0, Math.min(Number(postTourOfferStatus.progress || 0), target));
  const percent = Math.min(100, Math.round(progress * 100 / target));

  const discountText = document.getElementById("postTourOfferDiscountText");
  const progressText = document.getElementById("postTourOfferProgressText");
  const fill = document.getElementById("postTourOfferProgressFill");
  const info = document.getElementById("postTourOfferInfo");

  if (discountText) discountText.textContent = `Giảm ${discount}%`;
  if (progressText) progressText.textContent = `${progress}/${target} bài viết`;
  if (fill) fill.style.width = `${percent}%`;
  if (info) {
    const active = Boolean(postTourOfferStatus.has_offer);
    const blocked = !canUsePostOffer();
    info.innerHTML = `
      <div class="tour-offer-invite-item ${active ? 'accepted' : ''}">
        <span class="tour-offer-invite-main">
          <b>${blocked ? 'Chưa dùng được ưu đãi' : (active ? 'Ưu đãi đang có' : 'Chưa có ưu đãi')}</b>
          <small>${escapeHtml(postTourOfferStatus.message || (active ? 'Đơn tour tiếp theo được giảm 5%.' : 'Tạo bài viết để nhận ưu đãi.'))}</small>
        </span>
        <strong>${active ? '-5%' : '0%'}</strong>
      </div>`;
  }
}

async function openPostTourOfferModal() {
  document.getElementById("postTourOfferModal")?.classList.add("open");
  await loadPostTourOfferStatus(false);
}

function closePostTourOfferModal() {
  document.getElementById("postTourOfferModal")?.classList.remove("open");
}

function setupPostTourOfferUi() {
  document.getElementById("postTourOfferBtn")?.addEventListener("click", openPostTourOfferModal);
  document.getElementById("postTourOfferModal")?.addEventListener("click", (event) => {
    if (event.target?.id === "postTourOfferModal") closePostTourOfferModal();
  });
}

function postSearchText(post) {
  return normalizeSearchText([
    post?.title, post?.summary, post?.content, post?.festival, post?.province,
    post?.holiday_type, post?.holidayType, post?.tour_keywords, post?.tourKeywords,
    post?.author_name, post?.authorName
  ].join(" "));
}

function getPostAuthorId(post) {
  return String(getValue(post, "author_id", "authorId", "owner_id", "ownerId") || "");
}

function cleanAccountDisplayName(value) {
  return String(value ?? "")
    .replace(/^\s*Tài\s*khoản\s+/i, "")
    .trim();
}

function getPostAuthorName(post) {
  const name = cleanAccountDisplayName(getValue(post, "author_name", "authorName"));
  return name || "Cộng đồng TravelwAI";
}

function getCurrentUserId() {
  return String(
    getValue(currentPostUser, "id", "uid", "localId", "user_id", "userId")
    || localStorage.getItem("userId")
    || localStorage.getItem("uid")
    || localStorage.getItem("localId")
    || ""
  );
}

function isOwnPost(post) {
  const currentId = getCurrentUserId();
  const authorId = getPostAuthorId(post);
  return Boolean(currentId && authorId && currentId === authorId);
}

function isCurrentPostAdmin() {
  const role = String(
    getValue(currentPostUser, "role", "userRole")
    || localStorage.getItem("userRole")
    || ""
  ).trim().toLowerCase();
  return role === "admin";
}

function canEditPost(post) {
  return isCurrentPostAdmin() || isOwnPost(post);
}

function canDeletePost(post) {
  return isCurrentPostAdmin() || isOwnPost(post);
}

function normalizeWikiLine(value) {
  return String(value ?? "")
    .trim()
    .replace(/^=+|=+$/g, "")
    .trim()
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/[^a-z0-9 ]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function stripPostSourceLines(value) {
  const blockedHeadings = new Set([
    "xem them",
    "tham khao",
    "lien ket ngoai",
    "chu thich",
    "ghi chu",
    "nguon tham khao",
    "thu muc"
  ]);
  const kept = [];
  for (const line of String(value ?? "").split(/\r?\n/)) {
    const normalized = normalizeWikiLine(line);
    if (blockedHeadings.has(normalized)) break;
    if (/^\s*Nguồn\s+dữ\s+liệu/i.test(line) || /Wikipedia tiếng Việt|vi\.wikipedia\.org/i.test(line)) continue;
    kept.push(line);
  }
  return kept.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

function parsePostImages(post) {
  const raw = getValue(post, "image_urls", "imageUrls", "images", "photos", "photo_urls", "photoUrls", "image", "thumbnail");
  if (!raw) return [];
  if (Array.isArray(raw)) return raw.map(String).filter(Boolean);
  if (typeof raw === "string") {
    const text = raw.trim();
    if (!text) return [];
    if (text.startsWith("[") && text.endsWith("]")) {
      try {
        const parsed = JSON.parse(text);
        if (Array.isArray(parsed)) return parsed.map(String).filter(Boolean);
      } catch (_) {}
    }
    return text.split(/[\n,|]+/).map(x => x.trim()).filter(Boolean);
  }
  return [];
}

function renderPostImages(post, detail = false) {
  const images = parsePostImages(post);
  if (!images.length) return "";
  const visible = detail ? images : images.slice(0, 5);
  const extra = images.length - visible.length;
  return `
    <div class="${detail ? "post-detail-image-grid" : "post-card-image-grid"} ${visible.length > 1 ? "multi" : "single"} image-count-${visible.length}">
      ${visible.map((url, index) => `
        <div class="${detail ? "post-detail-image" : "post-card-image"}">
          <img loading="lazy" decoding="async" src="${escapeHtml(url)}" alt="Ảnh minh họa ${index + 1}" />
          ${!detail && extra > 0 && index === visible.length - 1 ? `<span>+${extra}</span>` : ""}
        </div>`).join("")}
    </div>`;
}

function filteredMonthlyPosts() {
  const query = normalizeSearchText(postSearchQuery);
  const current = currentPostMonth();
  let posts = [...travelwaiPosts].sort((a, b) => {
    const am = Number(getValue(a, "month")) === current ? 0 : 1;
    const bm = Number(getValue(b, "month")) === current ? 0 : 1;
    if (am !== bm) return am - bm;
    return String(getValue(a, "title")).localeCompare(String(getValue(b, "title")), "vi");
  });
  if (postOwnerFilterMode === "mine") posts = posts.filter(isOwnPost);
  return query ? posts.filter(post => postSearchText(post).includes(query)) : posts;
}

function filteredCommunityPosts() {
  const query = normalizeSearchText(postSearchQuery);
  let posts = travelwaiPosts.filter(post => !isOwnPost(post) && String(getValue(post, "source")).toLowerCase() !== "seed");
  posts = posts.sort((a, b) => {
    const current = currentPostMonth();
    const am = Number(getValue(a, "month")) === current ? 0 : 1;
    const bm = Number(getValue(b, "month")) === current ? 0 : 1;
    if (am !== bm) return am - bm;
    return Number(getValue(a, "month")) - Number(getValue(b, "month"));
  });
  if (query) posts = posts.filter(post => postSearchText(post).includes(query));
  return posts;
}

function updatePostViewToggle() {
  document.body.dataset.postView = "all";
}

function updateMyPostsFilterButton() {
  const button = document.getElementById("myPostsFilterButton");
  if (!button) return;
  const active = postOwnerFilterMode === "mine";
  button.classList.toggle("active", active);
  button.title = active ? "Đang xem bài viết của tôi" : "Xem bài viết của tôi";
  button.setAttribute("aria-label", button.title);
}

function toggleMyPostsFilter() {
  postOwnerFilterMode = postOwnerFilterMode === "mine" ? "all" : "mine";
  localStorage.setItem("travelwaiPostOwnerFilter", postOwnerFilterMode);
  updateMyPostsFilterButton();
  renderPosts();
}

function setupPostViewToggle() {
  updatePostViewToggle();
  updateMyPostsFilterButton();
  document.getElementById("myPostsFilterButton")?.addEventListener("click", toggleMyPostsFilter);
}

function postActionIcon(type) {
  if (type === "edit") {
    return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>`;
  }
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/><path d="M10 11v5"/><path d="M14 11v5"/></svg>`;
}

function renderPostActionButton(kind, onClick, text, extraClass = "") {
  const showIconOnly = true;
  const icon = postActionIcon(kind);
  const className = kind === "delete" ? "btn-danger" : "btn-soft";
  const actionClass = kind === "delete" ? "post-delete-action-button" : "post-edit-action-button";
  const classes = [className, actionClass, showIconOnly ? "post-card-icon-button" : "", extraClass].filter(Boolean).join(" ");
  return `<button class="${classes}" type="button" onclick="${onClick}" title="${escapeHtml(text)}" aria-label="${escapeHtml(text)}">${showIconOnly ? icon : escapeHtml(text)}</button>`;
}

function renderPostCard(post, mode = "month") {
  const rawId = getValue(post, "id");
  const id = escapeHtml(rawId);
  const jsId = escapeJsString(rawId);
  const title = getValue(post, "title") || "Bài viết";
  const summary = stripPostSourceLines(getValue(post, "summary") || getValue(post, "content")) || "Đang cập nhật nội dung.";
  const festival = getValue(post, "festival") || getValue(post, "holiday_type", "holidayType") || "Lễ hội";
  const province = getValue(post, "province") || "Việt Nam";
  const author = getPostAuthorName(post);
  const editable = canEditPost(post);
  const deletable = canDeletePost(post);
  return `
    <article class="post-card ${mode === "community" ? "community-post-card" : ""}" data-post-id="${id}">
      ${renderPostImages(post)}
      <div class="post-card-title-row">
        <h3>${escapeHtml(title)}</h3>
        <div class="post-card-author-name">${escapeHtml(author)}</div>
      </div>
      <div class="post-card-meta">
        <span>${escapeHtml(festival)}</span>
        <span>${escapeHtml(province)}</span>
      </div>
      <p>${escapeHtml(summary)}</p>
      <div class="post-card-footer">
        <div class="post-card-actions post-card-owner-actions">
          ${editable ? renderPostActionButton("edit", `openEditPublicPostModal('${jsId}')`, "Sửa bài viết") : ""}
          ${deletable ? renderPostActionButton("delete", `deletePublicPost('${jsId}')`, "Xóa bài viết") : ""}
        </div>
        <div class="post-card-actions post-card-view-actions">
          <button class="btn-soft" type="button" onclick="openPostDetailModal('${jsId}')">Xem bài viết</button>
        </div>
      </div>
    </article>`;
}

function renderPosts() {
  const grid = document.getElementById("postsGrid");
  if (!grid) return;
  const posts = filteredMonthlyPosts();
  if (!posts.length) {
    grid.classList.remove("posts-all-view");
    const emptyText = postOwnerFilterMode === "mine"
      ? "Bạn chưa có bài viết nào."
      : (postSearchQuery ? "Không tìm thấy bài viết." : "Chưa có bài viết nổi bật.");
    grid.innerHTML = `<div class="empty-line">${emptyText}</div>`;
    return;
  }
  grid.classList.add("posts-all-view");
  grid.innerHTML = posts.slice(0, 10).map(post => renderPostCard(post, "month")).join("");
}

async function loadCurrentPostUser() {
  try {
    const result = await readJson(await authenticatedFetch("/api/profile"));
    currentPostUser = result.user || result.data || null;
    if (currentPostUser?.role || currentPostUser?.userRole) localStorage.setItem("userRole", currentPostUser.role || currentPostUser.userRole);
  } catch (_) {
    currentPostUser = null;
  }
  applyPostAccountLimits();
}

function applyPostAccountLimits() {
  const aiButton = document.getElementById("publicPostAiButton");
  if (aiButton) {
    aiButton.disabled = false;
    aiButton.title = canUsePostAi() ? "Tạo tiêu đề, tóm tắt và nội dung từ lễ hội/ngày lễ" : "Tài khoản Free chưa dùng được AI tạo bài viết";
  }
}

async function loadPosts() {
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts`));
    travelwaiPosts = Array.isArray(result.data) ? result.data : [];
    renderPosts();
  } catch (error) {
    const grid = document.getElementById("postsGrid");
    if (grid) grid.innerHTML = `<div class="empty-line">${escapeHtml(error.message)}</div>`;
  }
}

async function fetchFullPublicPost(id) {
  const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(id)}`));
  const post = result.data || result.post || result;
  const index = travelwaiPosts.findIndex(item => String(getValue(item, "id")) === String(id));
  if (index >= 0 && post) travelwaiPosts[index] = { ...travelwaiPosts[index], ...post };
  return post;
}

async function trackPostView(id) {
  try {
    await authenticatedFetch(`/api/posts/${encodeURIComponent(id)}/view`, { method: "POST" });
  } catch (_) {
  }
}

async function openPostDetailModal(id) {
  let post = travelwaiPosts.find(item => String(getValue(item, "id")) === String(id));
  try {
    post = await fetchFullPublicPost(id);
    trackPostView(id);
  } catch (error) {
    showToast(error.message || "Không tải được bài viết.");
    if (!post) return;
  }
  document.getElementById("postDetailTitle").textContent = getValue(post, "title") || "Bài viết";
  document.getElementById("postDetailMeta").textContent = [
    getValue(post, "festival"), getValue(post, "province"), getValue(post, "author_name", "authorName")
  ].filter(Boolean).join(" · ");
  const imageBox = document.getElementById("postDetailImages");
  if (imageBox) imageBox.innerHTML = renderPostImages(post, true);
  document.getElementById("postDetailContent").textContent = stripPostSourceLines(getValue(post, "content") || getValue(post, "summary")) || "Đang cập nhật nội dung.";
  document.getElementById("postDetailModal")?.classList.add("open");
}

function closePostDetailModal() {
  document.getElementById("postDetailModal")?.classList.remove("open");
}

function setupPostSearch() {
  const input = document.getElementById("postSearchInput");
  const clear = document.getElementById("clearPostSearch");
  if (!input) return;
  input.addEventListener("input", () => {
    postSearchQuery = input.value || "";
    renderPosts();
  });
  clear?.addEventListener("click", () => {
    input.value = "";
    postSearchQuery = "";
    input.focus();
    renderPosts();
  });
}

function validatePostImageFile(file) {
  if (!file) return;
  if (!file.type || !file.type.startsWith("image/")) throw new Error("Vui lòng chọn đúng tệp ảnh.");
  if (file.size > 10 * 1024 * 1024) throw new Error("Mỗi ảnh phải nhỏ hơn 10MB.");
}

async function uploadPostImages(files) {
  const list = Array.from(files || []);
  if (!list.length) return [];
  list.forEach(validatePostImageFile);
  const optimizedList = window.TravelwAIImageOptimizer
    ? await window.TravelwAIImageOptimizer.optimizeImageFiles(list)
    : list;
  const formData = new FormData();
  optimizedList.forEach(file => formData.append("images", file, file.name));
  const response = await authenticatedFetch("/api/posts/images", { method: "POST", body: formData });
  const result = await readJson(response);
  return Array.isArray(result.urls) ? result.urls : (Array.isArray(result.images) ? result.images : []);
}

function getPublicPostImageUrlInput() {
  const input = document.getElementById("publicPostImageUrls");
  return (input?.value || "")
    .split(/[\n,|]+/)
    .map(url => url.trim())
    .filter(Boolean);
}

function renderPublicPostPreview() {
  const box = document.getElementById("publicPostImagePreview");
  if (!box) return;
  const files = selectedPublicPostImages;
  const urlImages = getPublicPostImageUrlInput();
  if (!files.length && !urlImages.length) {
    box.innerHTML = "";
    return;
  }
  const currentImages = urlImages;
  const current = currentImages.map((_, index) => `<span>Ảnh hiện có ${index + 1}</span>`);
  const selected = files.map(file => `<span>${escapeHtml(file.name)}</span>`);
  box.innerHTML = current.concat(selected).join("");
}

async function generatePublicPostContentFromFestival() {
  if (!canUsePostAi()) {
    if (window.TravelwAIPricingPopup?.showFreeAiPopup) window.TravelwAIPricingPopup.showFreeAiPopup();
    else showToast("Tài khoản Free chưa dùng được AI tạo bài viết.");
    return;
  }
  const titleInput = document.getElementById("publicPostTitle");
  const festivalInput = document.getElementById("publicPostFestival");
  const festival = festivalInput?.value.trim() || "";
  if (!festival) {
    festivalInput?.focus();
    showToast("Vui lòng nhập Lễ hội/ngày lễ trước khi dùng AI.");
    return;
  }

  const button = document.getElementById("publicPostAiButton");
  const originalText = button?.textContent || "AI";
  try {
    if (button) {
      button.disabled = true;
      button.textContent = "...";
    }
    const response = await authenticatedFetch("/api/posts/ai-content", {
      method: "POST",
      body: JSON.stringify({
        title: titleInput?.value.trim() || "",
        festival,
        province: document.getElementById("publicPostProvince")?.value.trim() || "",
        month: Number(document.getElementById("publicPostMonth")?.value || currentPostMonth())
      })
    });
    const result = await readJson(response);
    const data = result.data || result;
    const content = stripPostSourceLines(data.content || "");
    const summary = stripPostSourceLines(data.summary || "");
    if (!content) throw new Error("Không tìm thấy nội dung phù hợp.");

    if (titleInput && data.title) titleInput.value = data.title;

    document.getElementById("publicPostContent").value = content;

    const summaryInput = document.getElementById("publicPostSummary");
    if (summaryInput && summary) summaryInput.value = summary;

    const festivalInput = document.getElementById("publicPostFestival");
    if (festivalInput && data.festival) festivalInput.value = data.festival;

    const provinceInput = document.getElementById("publicPostProvince");
    if (provinceInput && data.province) provinceInput.value = data.province;

    const monthSelect = document.getElementById("publicPostMonth");
    const month = Number(data.month || 0);
    if (monthSelect && month >= 1 && month <= 12) monthSelect.value = String(month);

    showToast(result.message || "Đã điền nội dung bài viết.");
  } catch (error) {
    showToast(error.message || "Không tạo được nội dung từ Lễ hội/ngày lễ này.");
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalText;
    }
  }
}

function setPublicPostModalMode(isEdit) {
  const title = document.getElementById("publicPostModalTitle");
  const submit = document.getElementById("publicPostSubmitButton");
  if (title) title.textContent = isEdit ? "Sửa bài viết" : "Thêm bài viết";
  if (submit) submit.textContent = isEdit ? "Lưu thay đổi" : "Lưu bài viết";
}

function openPublicPostModal() {
  const month = currentPostMonth();
  editingPublicPostId = "";
  editingPublicPostExistingImages = [];
  document.getElementById("publicPostForm")?.reset();
  const hiddenId = document.getElementById("publicPostId");
  if (hiddenId) hiddenId.value = "";
  const monthSelect = document.getElementById("publicPostMonth");
  if (monthSelect) monthSelect.value = String(month);
  const statusInput = document.getElementById("publicPostStatus");
  if (statusInput) statusInput.value = "Hiển thị";
  const tourKeywordsInput = document.getElementById("publicPostTourKeywords");
  if (tourKeywordsInput) tourKeywordsInput.value = "";
  const imageInput = document.getElementById("publicPostImages");
  if (imageInput) imageInput.value = "";
  const imageUrlInput = document.getElementById("publicPostImageUrls");
  if (imageUrlInput) imageUrlInput.value = "";
  selectedPublicPostImages = [];
  setPublicPostModalMode(false);
  renderPublicPostPreview();
  document.getElementById("publicPostModal")?.classList.add("open");
}

async function openEditPublicPostModal(id) {
  let post = travelwaiPosts.find(item => String(getValue(item, "id")) === String(id));
  try {
    post = await fetchFullPublicPost(id);
  } catch (error) {
    showToast(error.message || "Không tải được bài viết.");
    if (!post) return;
  }
  if (!canEditPost(post)) {
    showToast("Chỉ Admin hoặc người tạo mới được sửa bài viết này.");
    return;
  }
  editingPublicPostId = String(id);
  editingPublicPostExistingImages = parsePostImages(post);
  document.getElementById("publicPostForm")?.reset();
  const hiddenId = document.getElementById("publicPostId");
  if (hiddenId) hiddenId.value = editingPublicPostId;
  const monthSelect = document.getElementById("publicPostMonth");
  if (monthSelect) monthSelect.value = String(Number(getValue(post, "month")) || currentPostMonth());
  const provinceInput = document.getElementById("publicPostProvince");
  if (provinceInput) provinceInput.value = getValue(post, "province") || "";
  const festivalInput = document.getElementById("publicPostFestival");
  if (festivalInput) festivalInput.value = getValue(post, "festival", "holiday_type", "holidayType") || "";
  const titleInput = document.getElementById("publicPostTitle");
  if (titleInput) titleInput.value = getValue(post, "title") || "";
  const statusInput = document.getElementById("publicPostStatus");
  if (statusInput) statusInput.value = getValue(post, "status") || "Hiển thị";
  const tourKeywordsInput = document.getElementById("publicPostTourKeywords");
  if (tourKeywordsInput) tourKeywordsInput.value = getValue(post, "tour_keywords", "tourKeywords") || "";
  const summaryInput = document.getElementById("publicPostSummary");
  if (summaryInput) summaryInput.value = stripPostSourceLines(getValue(post, "summary") || "");
  const contentInput = document.getElementById("publicPostContent");
  if (contentInput) contentInput.value = stripPostSourceLines(getValue(post, "content") || getValue(post, "summary") || "");
  const imageInput = document.getElementById("publicPostImages");
  if (imageInput) imageInput.value = "";
  const imageUrlInput = document.getElementById("publicPostImageUrls");
  if (imageUrlInput) imageUrlInput.value = editingPublicPostExistingImages.join("\n");
  selectedPublicPostImages = [];
  setPublicPostModalMode(true);
  renderPublicPostPreview();
  document.getElementById("publicPostModal")?.classList.add("open");
}

function closePublicPostModal() {
  document.getElementById("publicPostModal")?.classList.remove("open");
}

async function submitPublicPost(event) {
  event.preventDefault();
  const submitButton = event.submitter || document.querySelector("#publicPostForm button[type='submit']");
  const isEdit = Boolean(editingPublicPostId || document.getElementById("publicPostId")?.value);
  const originalText = submitButton?.textContent || (isEdit ? "Lưu thay đổi" : "Lưu bài viết");
  try {
    if (submitButton) {
      submitButton.disabled = true;
      submitButton.textContent = selectedPublicPostImages.length ? "Đang tải ảnh..." : "Đang lưu...";
    }
    const uploadedImages = await uploadPostImages(selectedPublicPostImages);
    const imageUrls = getPublicPostImageUrlInput().concat(uploadedImages);
    if (submitButton) submitButton.textContent = "Đang lưu...";

    const payload = {
      title: document.getElementById("publicPostTitle").value.trim(),
      month: Number(document.getElementById("publicPostMonth").value || currentPostMonth()),
      status: document.getElementById("publicPostStatus")?.value || "Hiển thị",
      festival: document.getElementById("publicPostFestival").value.trim(),
      province: document.getElementById("publicPostProvince").value.trim(),
      tourKeywords: document.getElementById("publicPostTourKeywords")?.value.trim() || "",
      summary: document.getElementById("publicPostSummary").value.trim(),
      content: document.getElementById("publicPostContent").value.trim(),
      imageUrls
    };
    const url = isEdit ? `/api/posts/${encodeURIComponent(editingPublicPostId)}` : "/api/posts";
    const method = isEdit ? "PUT" : "POST";
    const result = await readJson(await authenticatedFetch(url, { method, body: JSON.stringify(payload) }));
    showToast(result.message || (isEdit ? "Đã lưu bài viết" : "Đã thêm bài viết"));
    closePublicPostModal();
    editingPublicPostId = "";
    editingPublicPostExistingImages = [];
    if (!isEdit) await loadPostTourOfferStatus(true);
    await loadPosts();
  } catch (error) {
    showToast(error.message);
  } finally {
    if (submitButton) {
      submitButton.disabled = false;
      submitButton.textContent = originalText;
    }
  }
}

async function deletePublicPost(id) {
  const post = travelwaiPosts.find(item => String(getValue(item, "id")) === String(id));
  if (post && !canDeletePost(post)) {
    showToast("Chỉ Admin hoặc người tạo mới được xóa bài viết này.");
    return;
  }
  if (!await window.TravelwAIConfirm("Xóa bài viết này?")) return;
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(id)}`, { method: "DELETE" }));
    showToast(result.message || "Đã xóa bài viết");
    await loadPosts();
  } catch (error) {
    showToast(error.message || "Không xóa được bài viết.");
  }
}

function setupPublicPostForm() {
  document.getElementById("openPublicPostModalButton")?.addEventListener("click", openPublicPostModal);
  document.getElementById("publicPostForm")?.addEventListener("submit", submitPublicPost);
  document.getElementById("publicPostAiButton")?.addEventListener("click", generatePublicPostContentFromFestival);
  document.getElementById("publicPostImageButton")?.addEventListener("click", () => document.getElementById("publicPostImages")?.click());
  document.getElementById("publicPostImageUrls")?.addEventListener("input", renderPublicPostPreview);
  document.getElementById("publicPostImages")?.addEventListener("change", (event) => {
    selectedPublicPostImages = Array.from(event.target.files || []);
    renderPublicPostPreview();
  });
}

document.addEventListener("DOMContentLoaded", async () => {
  if (document.body.dataset.page !== "posts") return;
  setupPostSearch();
  setupPostViewToggle();
  setupPublicPostForm();
  setupPostTourOfferUi();
  await loadCurrentPostUser();
  await loadPostTourOfferStatus(true);
  await loadPosts();
});

window.closePostDetailModal = closePostDetailModal;
window.openPostDetailModal = openPostDetailModal;
window.openPublicPostModal = openPublicPostModal;
window.closePublicPostModal = closePublicPostModal;
window.openPostTourOfferModal = openPostTourOfferModal;
window.closePostTourOfferModal = closePostTourOfferModal;
window.openEditPublicPostModal = openEditPublicPostModal;
window.deletePublicPost = deletePublicPost;
