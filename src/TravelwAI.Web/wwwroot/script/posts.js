let travelwaiPosts = [];
let postSearchQuery = "";
let currentPostUser = null;
let selectedPublicPostMediaFiles = [];
let publicPostPreviewObjectUrls = [];
let editingPublicPostId = "";
let editingPublicPostExistingMedia = [];
let publicPostAiGenerationSessionId = "";
let publicPostAiGenerationId = "";
let publicPostAiAbortController = null;
let publicPostAiStreamReader = null;
let currentPostCommentsId = "";
let currentPostCommentsTitle = "";
try { localStorage.removeItem("travelwaiPostDisplayMode"); } catch (_) {}
let postOwnerFilterMode = localStorage.getItem("travelwaiPostOwnerFilter") === "mine" ? "mine" : "all";
let postTourOfferStatus = { has_offer: false, discount_percent: 0, progress: 0, target: 1, message: "" };

function normalizeAccountRoleForPosts(value) {
  const role = String(value || localStorage.getItem("userRole") || "Free").trim().toLowerCase();
  if (role === "user") return "free";
  if (role === "business") return "company";
  if (role === "tour sales" || role === "toursales") return "sales";
  return role || "free";
}

function currentPostAccountRole() {
  return normalizeAccountRoleForPosts(currentPostUser?.role || currentPostUser?.userRole || localStorage.getItem("userRole"));
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

function interfaceIcon(name, extraClass = "") {
  if (window.TravelwAIIcons?.html) return window.TravelwAIIcons.html(name, extraClass);
  return `<span class="interface-icon-fallback ${escapeHtml(extraClass)}" data-interface-icon="${escapeHtml(name)}"></span>`;
}

function readPostNumber(post, ...keys) {
  const value = getValue(post, ...keys);
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function readPostBoolean(post, ...keys) {
  const value = getValue(post, ...keys);
  if (typeof value === "boolean") return value;
  return ["true", "1", "yes"].includes(String(value || "").trim().toLowerCase());
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

function showToast(message, type = "info") {
  return window.TravelwAIToast(message, type);
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

function isVideoMediaUrl(url) {
  return /\.(mp4|webm|mov)(?:$|[?#])/i.test(String(url || ""));
}

function normalizePostMediaItem(item) {
  if (!item) return null;
  if (typeof item === "string") {
    const url = item.trim();
    if (!url) return null;
    const video = isVideoMediaUrl(url);
    return { url, name: url.split(/[/?#]/).pop() || "Tệp", contentType: video ? "video/mp4" : "image/jpeg", size: 0, type: video ? "video" : "image" };
  }
  const url = String(item.url || item.src || item.path || "").trim();
  if (!url) return null;
  const contentType = String(item.contentType || item.content_type || item.mimeType || item.mime_type || "").trim().toLowerCase();
  const video = String(item.type || "").toLowerCase() === "video" || contentType.startsWith("video/") || isVideoMediaUrl(url);
  return {
    url,
    name: String(item.name || item.fileName || item.filename || url.split(/[/?#]/).pop() || "Tệp"),
    contentType: contentType || (video ? "video/mp4" : "image/jpeg"),
    size: Number(item.size || 0),
    type: video ? "video" : "image"
  };
}

function parsePostMedia(post) {
  const rawMedia = getValue(post, "media", "media_items", "mediaItems");
  let media = [];
  if (Array.isArray(rawMedia)) media = rawMedia.map(normalizePostMediaItem).filter(Boolean);
  else if (typeof rawMedia === "string" && rawMedia.trim()) {
    try {
      const parsed = JSON.parse(rawMedia);
      if (Array.isArray(parsed)) media = parsed.map(normalizePostMediaItem).filter(Boolean);
    } catch (_) {}
  }

  const rawUrls = getValue(post, "media_urls", "mediaUrls", "image_urls", "imageUrls", "images", "video_urls", "videoUrls", "photos", "photo_urls", "photoUrls", "image", "thumbnail");
  let urls = [];
  if (Array.isArray(rawUrls)) urls = rawUrls;
  else if (typeof rawUrls === "string") {
    const text = rawUrls.trim();
    if (text.startsWith("[") && text.endsWith("]")) {
      try { const parsed = JSON.parse(text); if (Array.isArray(parsed)) urls = parsed; } catch (_) {}
    } else urls = text.split(/[\n,|]+/);
  }
  urls.map(normalizePostMediaItem).filter(Boolean).forEach(item => {
    if (!media.some(current => current.url === item.url)) media.push(item);
  });
  return media.slice(0, 12);
}

function renderPostMedia(post, detail = false) {
  const media = parsePostMedia(post);
  if (!media.length) return "";
  const visible = detail ? media : media.slice(0, 5);
  const extra = media.length - visible.length;
  return `
    <div class="${detail ? "post-detail-image-grid" : "post-card-image-grid"} ${visible.length > 1 ? "multi" : "single"} image-count-${visible.length}">
      ${visible.map((item, index) => `
        <div class="${detail ? "post-detail-image" : "post-card-image"}">
          ${item.type === "video"
            ? `<video preload="metadata" playsinline controls src="${escapeHtml(item.url)}" aria-label="Video minh họa ${index + 1}"></video>`
            : `<img loading="lazy" decoding="async" src="${escapeHtml(item.url)}" alt="Ảnh minh họa ${index + 1}" />`}
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
  if (type === "ai") return interfaceIcon("sparkles");
  const names = { edit: "edit-3", delete: "trash-2", view: "eye" };
  return interfaceIcon(names[type] || "eye");
}

function renderPostActionButton(kind, onClick, text, extraClass = "") {
  const icon = postActionIcon(kind);
  const className = kind === "delete" ? "btn-danger" : "btn-soft";
  const actionClass = kind === "delete"
    ? "post-delete-action-button"
    : (kind === "edit" ? "post-edit-action-button" : (kind === "ai" ? "post-ai-action-button" : "post-view-action-button"));
  const classes = [className, actionClass, "post-card-icon-button", kind === "ai" ? "twai-ai-icon-button" : "", extraClass].filter(Boolean).join(" ");
  return `<button class="${classes}" type="button" onclick="${onClick}" title="${escapeHtml(text)}" aria-label="${escapeHtml(text)}">${icon}</button>`;
}

function renderPostEngagement(post, jsId) {
  const likeCount = Math.max(0, readPostNumber(post, "like_count", "likeCount"));
  const commentCount = Math.max(0, readPostNumber(post, "comment_count", "commentCount"));
  const ratingCount = Math.max(0, readPostNumber(post, "rating_count", "ratingCount"));
  const userRating = Math.max(0, Math.min(5, readPostNumber(post, "user_rating", "userRating")));
  const liked = readPostBoolean(post, "user_liked", "userLiked");
  const ratingSummary = String(ratingCount);
  const hearts = Array.from({ length: 5 }, (_, index) => {
    const value = index + 1;
    const active = value <= userRating;
    const selected = value === userRating;
    const label = selected ? `Hủy đánh giá ${value} tim` : `Đánh giá ${value} tim`;
    return `<button class="post-rating-heart ${active ? "active" : ""}" type="button" onclick="ratePost('${jsId}', ${value})" title="${label}" aria-label="${label}" aria-pressed="${selected ? "true" : "false"}">${interfaceIcon("heart")}</button>`;
  }).join("");

  return `
    <div class="post-card-engagement">
      <div class="post-engagement-actions">
        <div class="post-engagement-left">
          <button class="post-social-button ${liked ? "active" : ""}" type="button" onclick="togglePostLike('${jsId}')" title="${liked ? "Bỏ thích" : "Thích bài viết"}" aria-label="${liked ? "Bỏ thích" : "Thích bài viết"}" aria-pressed="${liked ? "true" : "false"}">
            ${interfaceIcon("thumbs-up")}<span>${likeCount}</span>
          </button>
          <button class="post-social-button" type="button" onclick="openPostComments('${jsId}')" title="Xem bình luận" aria-label="Xem bình luận">
            ${interfaceIcon("message-circle")}<span>${commentCount}</span>
          </button>
        </div>
        <button class="post-social-button post-share-button" type="button" onclick="sharePost('${jsId}')" title="Sao chép liên kết" aria-label="Sao chép liên kết">
          ${interfaceIcon("share-2")}
        </button>
      </div>
      <div class="post-rating-row">
        <span class="post-rating-label">Đánh giá</span>
        <div class="post-rating-hearts" role="group" aria-label="Đánh giá bài viết từ 1 đến 5 tim">${hearts}</div>
        <span class="post-rating-summary">${escapeHtml(ratingSummary)}</span>
      </div>
    </div>`;
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
    <article class="post-card ${mode === "community" ? "community-post-card" : ""}" data-post-id="${id}" data-original-post-title="${escapeHtml(title)}">
      ${renderPostMedia(post)}
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
          ${renderPostActionButton("ai", `analyzePostWithAI(this, '${jsId}')`, `Phân tích ${title}`)}
          ${renderPostActionButton("view", `openPostDetailModal('${jsId}')`, "Xem bài viết")}
        </div>
      </div>
      ${renderPostEngagement(post, jsId)}
    </article>`;
}

function mergePostEngagement(id, data) {
  const index = travelwaiPosts.findIndex(item => String(getValue(item, "id")) === String(id));
  if (index < 0 || !data) return;
  travelwaiPosts[index] = { ...travelwaiPosts[index], ...data };
  renderPosts();
}

function getPostTitleFromClickedCard(source, id) {
  const article = source?.closest?.(".post-card[data-post-id]")
    || Array.from(document.querySelectorAll(".post-card[data-post-id]")).find((item) =>
      String(item.dataset.postId || "") === String(id)
    );
  const post = travelwaiPosts.find(item => String(getValue(item, "id")) === String(id));
  const storedTitle = String(getValue(post, "title") || "").trim();
  const originalTitle = String(article?.dataset.originalPostTitle || storedTitle).trim();
  const visibleTitle = String(article?.querySelector(".post-card-title-row h3")?.textContent || originalTitle).trim();
  return {
    originalTitle: originalTitle || visibleTitle || "Bài viết",
    visibleTitle: visibleTitle || originalTitle || "Post"
  };
}

function analyzePostWithAI(source, id) {
  const { originalTitle, visibleTitle } = getPostTitleFromClickedCard(source, id);
  const question = {
    vi: `Phân tích bài viết "${originalTitle}" và cho tôi các điểm chính, giá trị nổi bật và gợi ý liên quan.`,
    en: `Analyze the article "${visibleTitle}" and give me its key points, notable value, and relevant suggestions.`
  };

  // TravelwAIAskAI tự mở chatbot nổi ngay trước khi xử lý câu hỏi.
  if (typeof window.TravelwAIAskAI === "function") {
    void window.TravelwAIAskAI(question);
    return;
  }

  // Dự phòng khi script chatbot chưa khởi tạo xong.
  window.dispatchEvent(new CustomEvent("travelwai:open-chatbot"));
  window.dispatchEvent(new CustomEvent("travelwai:ask-ai", { detail: { question } }));
}

async function togglePostLike(id) {
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(id)}/like`, { method: "POST" }));
    mergePostEngagement(id, result.data || {});
  } catch (error) {
    showToast(error.message || "Không thể cập nhật lượt thích.");
  }
}

async function ratePost(id, rating) {
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(id)}/rating`, {
      method: "PUT",
      body: JSON.stringify({ rating: Number(rating) })
    }));
    mergePostEngagement(id, result.data || {});
    showToast(result.message || `Đã đánh giá ${rating} tim`);
  } catch (error) {
    showToast(error.message || "Không thể gửi đánh giá.");
  }
}

async function sharePost(id) {
  const url = new URL(window.location.href);
  url.pathname = "/posts";
  url.search = "";
  url.hash = "";
  url.searchParams.set("post", id);
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(url.toString());
    } else {
      const input = document.createElement("textarea");
      input.value = url.toString();
      input.style.position = "fixed";
      input.style.opacity = "0";
      document.body.appendChild(input);
      input.select();
      document.execCommand("copy");
      input.remove();
    }
    showToast("Đã sao chép liên kết bài viết.");
  } catch (_) {
    showToast("Không thể sao chép liên kết.");
  }
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

function applyPostAccountLimits() {}

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
  if (imageBox) imageBox.innerHTML = renderPostMedia(post, true);
  document.getElementById("postDetailContent").textContent = stripPostSourceLines(getValue(post, "content") || getValue(post, "summary")) || "Đang cập nhật nội dung.";
  document.getElementById("postDetailModal")?.classList.add("open");
}

function closePostDetailModal() {
  document.getElementById("postDetailModal")?.classList.remove("open");
}

function formatPostCommentDate(value) {
  const date = new Date(value || "");
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString("vi-VN", { dateStyle: "short", timeStyle: "short" });
}

function canDeletePostComment(comment) {
  const ownerId = String(getValue(comment, "user_id", "userId") || "");
  return isCurrentPostAdmin() || Boolean(ownerId && ownerId === getCurrentUserId());
}

function renderPostComments(comments) {
  const list = document.getElementById("postCommentsList");
  if (!list) return;
  if (!Array.isArray(comments) || !comments.length) {
    list.innerHTML = '<div class="empty-line">Chưa có bình luận.</div>';
    return;
  }
  list.innerHTML = comments.map((comment, commentIndex) => {
    const rawCommentId = String(getValue(comment, "id") || `${currentPostCommentsId}:${commentIndex}`);
    const commentId = escapeJsString(rawCommentId);
    const translationKey = escapeHtml(`post-comment:${currentPostCommentsId}:${rawCommentId}`);
    const name = getValue(comment, "user_name", "userName") || "Người dùng";
    const content = getValue(comment, "content") || "";
    const date = formatPostCommentDate(getValue(comment, "created_at", "createdAt"));
    return `<article class="post-comment-item">
      <div class="post-comment-main">
        <div class="post-comment-author">${escapeHtml(name)}</div>
        <div class="post-comment-content" data-no-translate data-ai-translation-key="${translationKey}">${escapeHtml(content)}</div>
        ${date ? `<time>${escapeHtml(date)}</time>` : ""}
      </div>
      <div class="post-comment-actions">
        <button class="post-comment-ai-translate" type="button" data-ai-translate-control data-ai-translation-target="interface" data-ai-translation-available="true" data-ai-translation-key="${translationKey}" data-no-translate onclick="togglePostCommentTranslation(this)" title="Dịch bình luận" aria-label="Dịch bình luận" aria-pressed="false"><span data-ai-translate-label>Dịch</span></button>
        ${canDeletePostComment(comment) ? `<button class="post-comment-delete interface-icon-button" type="button" onclick="deletePostComment('${commentId}')" title="Xóa bình luận" aria-label="Xóa bình luận">${interfaceIcon("trash-2")}</button>` : ""}
      </div>
    </article>`;
  }).join("");
}

async function togglePostCommentTranslation(button) {
  const content = button?.closest?.(".post-comment-item")?.querySelector?.(".post-comment-content");
  if (!window.TravelwAITranslation?.toggleTextElement) {
    showToast("Chức năng dịch AI chưa sẵn sàng.", "error");
    return;
  }
  await window.TravelwAITranslation.toggleTextElement(content, button);
}

async function loadPostComments() {
  const list = document.getElementById("postCommentsList");
  if (!currentPostCommentsId) return;
  if (list) list.innerHTML = '<div class="empty-line">Đang tải bình luận...</div>';
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(currentPostCommentsId)}/comments`));
    renderPostComments(result.data || []);
  } catch (error) {
    if (list) list.innerHTML = `<div class="empty-line">${escapeHtml(error.message || "Không tải được bình luận.")}</div>`;
  }
}

async function openPostComments(id) {
  const post = travelwaiPosts.find(item => String(getValue(item, "id")) === String(id));
  currentPostCommentsId = String(id || "");
  currentPostCommentsTitle = getValue(post, "title") || "Bài viết";
  const title = document.getElementById("postCommentsTitle");
  if (title) title.textContent = currentPostCommentsTitle;
  const input = document.getElementById("postCommentInput");
  if (input) input.value = "";
  const modal = document.getElementById("postCommentsModal");
  modal?.classList.add("open");
  modal?.setAttribute("aria-hidden", "false");
  await loadPostComments();
  input?.focus();
}

function closePostComments() {
  const modal = document.getElementById("postCommentsModal");
  modal?.classList.remove("open");
  modal?.setAttribute("aria-hidden", "true");
  currentPostCommentsId = "";
  currentPostCommentsTitle = "";
}

async function submitPostComment(event) {
  event.preventDefault();
  if (!currentPostCommentsId) return;
  const input = document.getElementById("postCommentInput");
  const button = document.getElementById("postCommentSubmitButton");
  const content = input?.value.trim() || "";
  if (!content) {
    input?.focus();
    return;
  }
  try {
    if (button) button.disabled = true;
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(currentPostCommentsId)}/comments`, {
      method: "POST",
      body: JSON.stringify({ content })
    }));
    if (input) input.value = "";
    mergePostEngagement(currentPostCommentsId, result.engagement || {});
    await loadPostComments();
  } catch (error) {
    showToast(error.message || "Không thể gửi bình luận.");
  } finally {
    if (button) button.disabled = false;
  }
}

async function deletePostComment(commentId) {
  if (!currentPostCommentsId || !commentId) return;
  if (!await window.TravelwAIConfirm("Xóa bình luận này?")) return;
  try {
    const result = await readJson(await authenticatedFetch(`/api/posts/${encodeURIComponent(currentPostCommentsId)}/comments/${encodeURIComponent(commentId)}`, { method: "DELETE" }));
    mergePostEngagement(currentPostCommentsId, result.data || {});
    await loadPostComments();
  } catch (error) {
    showToast(error.message || "Không thể xóa bình luận.");
  }
}

function setupPostEngagementUi() {
  document.getElementById("postCommentForm")?.addEventListener("submit", submitPostComment);
  document.getElementById("closePostCommentsButton")?.addEventListener("click", closePostComments);
  document.getElementById("postCommentsModal")?.addEventListener("click", event => {
    if (event.target?.id === "postCommentsModal") closePostComments();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && document.getElementById("postCommentsModal")?.classList.contains("open")) closePostComments();
  });
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

function validatePostMediaFile(file) {
  if (!file) return;
  const type = String(file.type || "");
  if (!type.startsWith("image/") && !type.startsWith("video/")) throw new Error("Chỉ hỗ trợ ảnh hoặc video.");
  if (file.size > 10 * 1024 * 1024) throw new Error("Mỗi tệp phải nhỏ hơn 10MB.");
}

async function uploadPostMedia(files) {
  const list = Array.from(files || []);
  if (!list.length) return [];
  list.forEach(validatePostMediaFile);
  const optimizedList = [];
  for (const file of list) {
    optimizedList.push(file.type?.startsWith("image/") && window.TravelwAIImageOptimizer
      ? await window.TravelwAIImageOptimizer.optimizeImageFile(file)
      : file);
  }
  const formData = new FormData();
  optimizedList.forEach(file => formData.append("files", file, file.name));
  const response = await authenticatedFetch("/api/posts/images", { method: "POST", body: formData });
  const result = await readJson(response);
  if (Array.isArray(result.media)) return result.media.map(normalizePostMediaItem).filter(Boolean);
  return (Array.isArray(result.urls) ? result.urls : []).map(normalizePostMediaItem).filter(Boolean);
}

function getPublicPostMediaInput() {
  const input = document.getElementById("publicPostImageUrls");
  const value = String(input?.value || "").trim();
  if (!value) return [];
  try {
    const parsed = JSON.parse(value);
    if (Array.isArray(parsed)) return parsed.map(normalizePostMediaItem).filter(Boolean);
  } catch (_) {}
  return value.split(/[\n,|]+/).map(normalizePostMediaItem).filter(Boolean);
}

function revokePublicPostPreviewObjectUrls() {
  publicPostPreviewObjectUrls.forEach(url => URL.revokeObjectURL(url));
  publicPostPreviewObjectUrls = [];
}

function setPublicPostExistingMedia(items) {
  const media = (items || []).map(normalizePostMediaItem).filter(Boolean);
  const input = document.getElementById("publicPostImageUrls");
  if (input) input.value = JSON.stringify(media);
  editingPublicPostExistingMedia = [...media];
}

function mediaPreviewMarkup(item, index, source) {
  const preview = item.type === "video"
    ? `<video preload="metadata" muted playsinline src="${escapeHtml(item.url)}"></video>`
    : `<img src="${escapeHtml(item.url)}" alt="${escapeHtml(item.name || `Tệp ${index + 1}`)}" />`;
  return `<div class="image-attachment-preview-item">${preview}<button class="image-attachment-remove" type="button" data-media-source="${source}" data-media-index="${index}" title="Xóa tệp" aria-label="Xóa tệp"><span data-interface-icon="trash-2"></span></button></div>`;
}

function renderPublicPostPreview() {
  const box = document.getElementById("publicPostImagePreview");
  if (!box) return;
  revokePublicPostPreviewObjectUrls();

  const existing = getPublicPostMediaInput();
  const selected = selectedPublicPostMediaFiles.map((file, index) => {
    const url = URL.createObjectURL(file);
    publicPostPreviewObjectUrls.push(url);
    return normalizePostMediaItem({ url, name: file.name, contentType: file.type, size: file.size });
  });
  box.innerHTML = existing.map((item, index) => mediaPreviewMarkup(item, index, "existing"))
    .concat(selected.map((item, index) => mediaPreviewMarkup(item, index, "selected"))).join("");
  box.querySelectorAll(".image-attachment-remove").forEach(button => {
    button.addEventListener("click", () => {
      const index = Number(button.dataset.mediaIndex);
      if (button.dataset.mediaSource === "existing") {
        const next = getPublicPostMediaInput();
        next.splice(index, 1);
        setPublicPostExistingMedia(next);
      } else selectedPublicPostMediaFiles.splice(index, 1);
      renderPublicPostPreview();
    });
  });
}

function createPublicPostAiSessionId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`;
}

function setPublicPostAiLoading(isLoading, message = "") {
  const button = document.getElementById("publicPostAiGenerateButton");
  const status = document.getElementById("publicPostAiGenerateStatus");
  if (button) {
    button.disabled = false;
    button.classList.toggle("is-loading", Boolean(isLoading));
    button.classList.toggle("is-cancellable", Boolean(isLoading));
    button.setAttribute("aria-busy", isLoading ? "true" : "false");
    button.setAttribute("aria-label", isLoading ? "Dừng AI tạo bài viết" : "Tự tạo nội dung bài viết bằng AI");
    button.title = isLoading ? "Bấm để dừng AI" : "Tự tạo nội dung bằng AI";
  }
  if (status) status.textContent = message;
}

function decodePublicPostPartialJsonString(rawValue) {
  let value = String(rawValue || "");
  if (!value) return "";
  if (value.endsWith("\\")) value = value.slice(0, -1);
  try {
    return JSON.parse(`"${value}"`);
  } catch (_) {
    return value
      .replace(/\\n/g, "\n")
      .replace(/\\r/g, "")
      .replace(/\\t/g, "\t")
      .replace(/\\"/g, '"')
      .replace(/\\\\/g, "\\");
  }
}

function extractPublicPostAiField(rawJson, fieldName) {
  const source = String(rawJson || "");
  const marker = `"${fieldName}"`;
  const markerIndex = source.indexOf(marker);
  if (markerIndex < 0) return "";
  const colonIndex = source.indexOf(":", markerIndex + marker.length);
  if (colonIndex < 0) return "";
  const quoteIndex = source.indexOf('"', colonIndex + 1);
  if (quoteIndex < 0) return "";
  let escaped = false;
  let encoded = "";
  for (let index = quoteIndex + 1; index < source.length; index += 1) {
    const character = source[index];
    if (escaped) {
      encoded += `\\${character}`;
      escaped = false;
      continue;
    }
    if (character === "\\") {
      escaped = true;
      continue;
    }
    if (character === '"') return decodePublicPostPartialJsonString(encoded);
    encoded += character;
  }
  if (escaped) encoded += "\\";
  return decodePublicPostPartialJsonString(encoded);
}

function updatePublicPostAiStreamingFields(rawJson) {
  const fields = [
    ["title", "publicPostTitle"],
    ["province", "publicPostProvince"],
    ["tourKeywords", "publicPostTourKeywords"],
    ["summary", "publicPostSummary"],
    ["content", "publicPostContent"]
  ];
  fields.forEach(([jsonKey, inputId]) => {
    const value = extractPublicPostAiField(rawJson, jsonKey);
    const input = document.getElementById(inputId);
    if (input && value) input.value = value;
  });
}

function clearPublicPostAiGeneratedFields() {
  ["publicPostTitle", "publicPostProvince", "publicPostTourKeywords", "publicPostSummary", "publicPostContent"].forEach(id => {
    const input = document.getElementById(id);
    if (input) input.value = "";
  });
}

async function generatePublicPostContentFromFestival() {
  const festivalInput = document.getElementById("publicPostFestival");
  const keyword = festivalInput?.value?.trim() || "";
  if (!keyword) {
    showToast("Vui lòng nhập lễ hội hoặc ngày lễ trước khi tạo nội dung.");
    festivalInput?.focus();
    return;
  }

  if (publicPostAiAbortController) {
    setPublicPostAiLoading(true, "Đang dừng AI...");
    publicPostAiAbortController.abort("user-cancelled");
    try { await publicPostAiStreamReader?.cancel?.("user-cancelled"); } catch (_) {}
    return;
  }

  const abortController = new AbortController();
  publicPostAiAbortController = abortController;
  setPublicPostAiLoading(true, "Đang chuẩn bị dữ liệu cho AI...");
  let rawAiOutput = "";
  let completedPayload = null;
  try {
    const response = await authenticatedFetch("/api/post-content-ai/generate-stream", {
      method: "POST",
      headers: { Accept: "application/x-ndjson" },
      body: JSON.stringify({
        keyword,
        language: window.TravelwAILanguage?.get?.() || "vi",
        sessionId: publicPostAiGenerationSessionId || (publicPostAiGenerationSessionId = createPublicPostAiSessionId())
      }),
      timeoutMs: 240000,
      streamResponse: true,
      signal: abortController.signal
    });

    if (!response.ok || !response.body) {
      const result = await readJson(response);
      throw new Error(result.message || "Không thể tạo nội dung bằng AI.");
    }

    const reader = response.body.getReader();
    publicPostAiStreamReader = reader;
    const decoder = new TextDecoder();
    let streamBuffer = "";
    let streamDone = false;
    while (!streamDone) {
      const { value, done } = await reader.read();
      streamBuffer += decoder.decode(value || new Uint8Array(), { stream: !done });
      let newlineIndex;
      while ((newlineIndex = streamBuffer.indexOf("\n")) >= 0) {
        const line = streamBuffer.slice(0, newlineIndex).trim();
        streamBuffer = streamBuffer.slice(newlineIndex + 1);
        if (!line) continue;
        const event = JSON.parse(line);
        const type = String(event.type || "").toLowerCase();
        if (type === "status") {
          setPublicPostAiLoading(true, event.message || "AI đang chuẩn bị nội dung...");
        } else if (type === "delta") {
          rawAiOutput += String(event.delta || "");
          updatePublicPostAiStreamingFields(rawAiOutput);
          const currentContent = document.getElementById("publicPostContent")?.value || "";
          setPublicPostAiLoading(true, currentContent
            ? `AI đang viết nội dung... ${currentContent.length.toLocaleString("vi-VN")} ký tự`
            : "AI đang sinh tiêu đề và nội dung...");
        } else if (type === "reset") {
          rawAiOutput = "";
          clearPublicPostAiGeneratedFields();
          setPublicPostAiLoading(true, event.message || "AI đang tạo lại nội dung...");
        } else if (type === "completed") {
          completedPayload = event;
          streamDone = true;
          break;
        } else if (type === "error") {
          throw new Error(event.message || "Không thể tạo nội dung bằng AI.");
        }
      }
      if (done) break;
    }

    if (!completedPayload) throw new Error("Luồng AI kết thúc nhưng chưa trả về nội dung hoàn chỉnh.");
    const data = completedPayload.data || completedPayload;
    publicPostAiGenerationSessionId = data.aiGenerationSessionId || data.ai_generation_session_id || publicPostAiGenerationSessionId;
    publicPostAiGenerationId = data.aiGenerationId || data.ai_generation_id || "";

    const titleInput = document.getElementById("publicPostTitle");
    const provinceInput = document.getElementById("publicPostProvince");
    const keywordsInput = document.getElementById("publicPostTourKeywords");
    const summaryInput = document.getElementById("publicPostSummary");
    const contentInput = document.getElementById("publicPostContent");
    if (titleInput) titleInput.value = data.title || "";
    if (provinceInput) provinceInput.value = data.province || "";
    if (keywordsInput) keywordsInput.value = data.tourKeywords || data.tour_keywords || "";
    if (summaryInput) summaryInput.value = data.summary || "";
    if (contentInput) contentInput.value = data.content || "";

    setPublicPostAiLoading(false, "Đã tạo nội dung.");
    showToast(completedPayload.message || "Đã tạo nội dung.");
    titleInput?.focus();
  } catch (error) {
    if (abortController.signal.aborted) {
      setPublicPostAiLoading(false, "Đã dừng tạo nội dung.");
      showToast("Đã dừng AI tạo bài viết.");
    } else {
      console.error("Không thể tạo nội dung bài viết bằng AI:", error);
      setPublicPostAiLoading(false, "");
      showToast(error?.message || "Không thể tạo nội dung bằng AI.");
    }
  } finally {
    if (publicPostAiStreamReader) {
      try { publicPostAiStreamReader.releaseLock?.(); } catch (_) {}
      publicPostAiStreamReader = null;
    }
    if (publicPostAiAbortController === abortController) publicPostAiAbortController = null;
    const button = document.getElementById("publicPostAiGenerateButton");
    button?.classList.remove("is-cancellable");
    if (button?.classList.contains("is-loading")) {
      setPublicPostAiLoading(false, document.getElementById("publicPostAiGenerateStatus")?.textContent || "");
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
  editingPublicPostExistingMedia = [];
  document.getElementById("publicPostForm")?.reset();
  setPublicPostAiLoading(false, "");
  publicPostAiGenerationSessionId = createPublicPostAiSessionId();
  publicPostAiGenerationId = "";
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
  selectedPublicPostMediaFiles = [];
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
  editingPublicPostExistingMedia = parsePostMedia(post);
  document.getElementById("publicPostForm")?.reset();
  setPublicPostAiLoading(false, "");
  publicPostAiGenerationSessionId = createPublicPostAiSessionId();
  publicPostAiGenerationId = "";
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
  if (imageUrlInput) imageUrlInput.value = JSON.stringify(editingPublicPostExistingMedia);
  selectedPublicPostMediaFiles = [];
  setPublicPostModalMode(true);
  renderPublicPostPreview();
  document.getElementById("publicPostModal")?.classList.add("open");
}

function closePublicPostModal() {
  publicPostAiAbortController?.abort("modal-closed");
  try { void publicPostAiStreamReader?.cancel?.("modal-closed"); } catch (_) {}
  publicPostAiStreamReader = null;
  publicPostAiAbortController = null;
  setPublicPostAiLoading(false, "");
  document.getElementById("publicPostModal")?.classList.remove("open");
  revokePublicPostPreviewObjectUrls();
  publicPostAiGenerationSessionId = "";
  publicPostAiGenerationId = "";
}

async function submitPublicPost(event) {
  event.preventDefault();
  const submitButton = event.submitter || document.querySelector("#publicPostForm button[type='submit']");
  const isEdit = Boolean(editingPublicPostId || document.getElementById("publicPostId")?.value);
  const originalText = submitButton?.textContent || (isEdit ? "Lưu thay đổi" : "Lưu bài viết");
  try {
    if (submitButton) {
      submitButton.disabled = true;
      submitButton.textContent = selectedPublicPostMediaFiles.length ? "Đang tải tệp..." : "Đang lưu...";
    }
    const uploadedMedia = await uploadPostMedia(selectedPublicPostMediaFiles);
    const media = getPublicPostMediaInput().concat(uploadedMedia);
    const imageUrls = media.filter(item => item.type === "image").map(item => item.url);
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
      imageUrls,
      media,
      aiGenerationSessionId: publicPostAiGenerationSessionId,
      aiGenerationId: publicPostAiGenerationId
    };
    const url = isEdit ? `/api/posts/${encodeURIComponent(editingPublicPostId)}` : "/api/posts";
    const method = isEdit ? "PUT" : "POST";
    const result = await readJson(await authenticatedFetch(url, { method, body: JSON.stringify(payload) }));
    showToast(result.message || (isEdit ? "Đã lưu bài viết" : "Đã thêm bài viết"));
    closePublicPostModal();
    editingPublicPostId = "";
    editingPublicPostExistingMedia = [];
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
  document.getElementById("publicPostAiGenerateButton")?.addEventListener("click", generatePublicPostContentFromFestival);
  document.getElementById("publicPostImageButton")?.addEventListener("click", () => document.getElementById("publicPostImages")?.click());
  document.getElementById("publicPostImages")?.addEventListener("change", (event) => {
    const addedFiles = Array.from(event.target.files || []);
    try {
      addedFiles.forEach(validatePostMediaFile);
      const remaining = Math.max(0, 12 - getPublicPostMediaInput().length - selectedPublicPostMediaFiles.length);
      selectedPublicPostMediaFiles = selectedPublicPostMediaFiles.concat(addedFiles.slice(0, remaining));
      if (addedFiles.length > remaining) showToast("Mỗi bài viết tối đa 12 ảnh hoặc video.");
      event.target.value = "";
      renderPublicPostPreview();
    } catch (error) {
      event.target.value = "";
      showToast(error.message || "Tệp không hợp lệ.");
    }
  });
}

document.addEventListener("DOMContentLoaded", async () => {
  if (document.body.dataset.page !== "posts") return;
  setupPostSearch();
  setupPostViewToggle();
  setupPublicPostForm();
  setupPostTourOfferUi();
  setupPostEngagementUi();
  await loadCurrentPostUser();
  await loadPostTourOfferStatus(true);
  await loadPosts();
  const sharedPostId = new URLSearchParams(window.location.search).get("post");
  if (sharedPostId) openPostDetailModal(sharedPostId);
});

window.closePostDetailModal = closePostDetailModal;
window.openPostDetailModal = openPostDetailModal;
window.analyzePostWithAI = analyzePostWithAI;
window.togglePostCommentTranslation = togglePostCommentTranslation;
window.openPublicPostModal = openPublicPostModal;
window.closePublicPostModal = closePublicPostModal;
window.generatePublicPostContentFromFestival = generatePublicPostContentFromFestival;
window.openPostTourOfferModal = openPostTourOfferModal;
window.closePostTourOfferModal = closePostTourOfferModal;
window.openEditPublicPostModal = openEditPublicPostModal;
window.deletePublicPost = deletePublicPost;
window.togglePostLike = togglePostLike;
window.ratePost = ratePost;
window.sharePost = sharePost;
window.openPostComments = openPostComments;
window.closePostComments = closePostComments;
window.deletePostComment = deletePostComment;
