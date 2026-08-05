(function () {
  "use strict";

  const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
  const MAX_INTERNAL_SERVER_RETRIES = 5;
  const INTERNAL_SERVER_RETRY_DELAY_MS = 400;
  let selectedFile = null;
  let selectedImageData = "";
  let selectedPreviewUrl = "";
  let isAnalyzing = false;
  let analysisAbortController = null;
  let streamingRevealStage = 0;

  const elements = {};

  function $(id) {
    return document.getElementById(id);
  }

  function getCurrentLanguage() {
    const language = window.TravelwAILanguage?.get?.()
      || document.documentElement.getAttribute("data-travelwai-language")
      || document.documentElement.lang
      || "vi";
    return String(language).toLowerCase().startsWith("en") ? "en" : "vi";
  }

  function localize(vi, en) {
    return getCurrentLanguage() === "en" ? en : vi;
  }

  function initializeElements() {
    elements.dropzone = $("locationDropzone");
    elements.input = $("locationImageInput");
    elements.chooseButton = $("locationChooseImageButton");
    elements.placeholder = $("locationUploadPlaceholder");
    elements.preview = $("locationImagePreview");
    elements.previewImage = $("locationPreviewImage");
    elements.imageName = $("locationImageName");
    elements.imageMeta = $("locationImageMeta");
    elements.removeButton = $("locationRemoveImageButton");
    elements.analyzeButton = $("locationAnalyzeButton");
    elements.analyzeAgainButton = $("locationAnalyzeAgainButton");
    elements.message = $("locationAnalysisMessage");
    elements.resultPanel = $("locationResultPanel");
    elements.empty = $("locationResultEmpty");
    elements.loading = $("locationResultLoading");
    elements.error = $("locationResultError");
    elements.content = $("locationResultContent");
    elements.streamingBanner = $("locationStreamingBanner");
    elements.streamingStatus = $("locationStreamingStatus");
    elements.summaryCard = $("locationSummaryCard");
    elements.resultLabel = $("locationResultLabel");
    elements.confidenceBadge = $("locationConfidenceBadge");
    elements.resultTitle = $("locationResultTitle");
    elements.resultSummary = $("locationResultSummary");
    elements.locationLine = $("locationResultLocationLine");
    elements.landmarkCard = $("locationLandmarkCard");
    elements.foodCard = $("locationFoodCard");
    elements.detailCard = $("locationDetailCard");
    elements.observationBlock = $("locationObservationBlock");
    elements.evidenceBlock = $("locationEvidenceBlock");
  }

  function getToken() {
    const cookieValue = String(document.cookie || "")
      .split(";")
      .map((item) => item.trim())
      .find((item) => item.startsWith("TravelwAIAuth="));
    return sessionStorage.getItem("idToken")
      || localStorage.getItem("idToken")
      || (cookieValue ? decodeURIComponent(cookieValue.slice("TravelwAIAuth=".length)) : "");
  }

  function formatBytes(bytes) {
    const value = Number(bytes || 0);
    if (value < 1024) return `${value} B`;
    if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
    return `${(value / (1024 * 1024)).toFixed(1)} MB`;
  }

  function escapeHtml(value) {
    return String(value ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function setMessage(text, type) {
    if (!elements.message) return;
    elements.message.textContent = text || (type === "error"
      ? localize("Vui lòng thử lại", "Please try again")
      : "");
    elements.message.classList.toggle("is-error", type === "error");
    elements.message.classList.toggle("is-success", type === "success");
  }

  function setResultState(state) {
    const nextState = ["empty", "loading", "error", "content"].includes(state) ? state : "empty";
    if (elements.resultPanel) elements.resultPanel.dataset.resultState = nextState;
    elements.empty.hidden = nextState !== "empty";
    elements.loading.hidden = nextState !== "loading";
    elements.error.hidden = nextState !== "error";
    elements.content.hidden = nextState !== "content";
    if (nextState === "content") elements.content.scrollTop = 0;
  }

  function resetStreamingPreview() {
    elements.resultPanel?.classList.remove("is-streaming");
    if (elements.streamingBanner) elements.streamingBanner.hidden = true;
    if (elements.streamingStatus) {
      elements.streamingStatus.textContent = localize(
        "Đang quan sát ảnh và nhận diện nội dung...",
        "Examining the image and identifying its content..."
      );
    }
  }

  function setLoadingStatus(message) {
    if (!elements.streamingStatus || !message) return;
    elements.streamingStatus.textContent = String(message);
  }

  function revealStreamingElement(target) {
    if (!target || !target.hidden) return false;
    target.hidden = false;
    target.classList.remove("stream-reveal");
    void target.offsetWidth;
    target.classList.add("stream-reveal");
    window.setTimeout(() => target.classList.remove("stream-reveal"), 360);
    target.scrollIntoView?.({ behavior: "smooth", block: "nearest" });
    return true;
  }

  function beginStreamingAnalysis() {
    streamingRevealStage = 0;
    resetResultCards();
    elements.resultPanel?.classList.add("is-streaming");
    if (elements.streamingBanner) elements.streamingBanner.hidden = false;
    if (elements.analyzeAgainButton) elements.analyzeAgainButton.hidden = true;

    elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");
    elements.confidenceBadge.className = "confidence-badge is-analyzing";
    elements.confidenceBadge.textContent = localize("Đang phân tích", "Analyzing");
    elements.confidenceBadge.hidden = true;
    elements.resultTitle.textContent = "";
    elements.resultSummary.textContent = "";
    elements.resultSummary.hidden = true;
    setResultState("content");
  }

  function decodePartialJsonString(raw, startIndex) {
    let value = "";
    let index = startIndex;

    while (index < raw.length) {
      const character = raw[index];
      if (character === '"') return { value, complete: true, end: index + 1 };
      if (character !== "\\") {
        value += character;
        index += 1;
        continue;
      }

      if (index + 1 >= raw.length) return { value, complete: false, end: raw.length };
      const escaped = raw[index + 1];
      const escapes = {
        '"': '"',
        "\\": "\\",
        "/": "/",
        "b": "\b",
        "f": "\f",
        "n": "\n",
        "r": "\r",
        "t": "\t"
      };

      if (escaped === "u") {
        const code = raw.slice(index + 2, index + 6);
        if (code.length < 4 || !/^[0-9a-f]{4}$/i.test(code)) {
          return { value, complete: false, end: raw.length };
        }
        value += String.fromCharCode(parseInt(code, 16));
        index += 6;
        continue;
      }

      value += Object.prototype.hasOwnProperty.call(escapes, escaped) ? escapes[escaped] : escaped;
      index += 2;
    }

    return { value, complete: false, end: raw.length };
  }

  function findJsonPropertyValue(raw, key) {
    const escapedKey = String(key).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const match = new RegExp(`"${escapedKey}"\\s*:\\s*`).exec(raw);
    return match ? match.index + match[0].length : -1;
  }

  function extractStreamingString(raw, key) {
    const valueStart = findJsonPropertyValue(raw, key);
    if (valueStart < 0) return { found: false, complete: false, value: "" };
    const quoteIndex = raw.indexOf('"', valueStart);
    if (quoteIndex < 0 || raw.slice(valueStart, quoteIndex).trim()) {
      return { found: true, complete: false, value: "" };
    }
    const parsed = decodePartialJsonString(raw, quoteIndex + 1);
    return { found: true, complete: parsed.complete, value: parsed.value };
  }

  function extractStreamingNumber(raw, key) {
    const valueStart = findJsonPropertyValue(raw, key);
    if (valueStart < 0) return { found: false, complete: false, value: null };
    const fragment = raw.slice(valueStart);
    const match = fragment.match(/^-?\d+(?:\.\d+)?/);
    if (!match) return { found: true, complete: false, value: null };
    const next = fragment[match[0].length] || "";
    return {
      found: true,
      complete: Boolean(next) && /[},\s]/.test(next),
      value: Number(match[0])
    };
  }

  function extractStreamingArray(raw, key) {
    const valueStart = findJsonPropertyValue(raw, key);
    if (valueStart < 0) return { found: false, complete: false, values: [] };
    const bracketIndex = raw.indexOf("[", valueStart);
    if (bracketIndex < 0 || raw.slice(valueStart, bracketIndex).trim()) {
      return { found: true, complete: false, values: [] };
    }

    const values = [];
    let index = bracketIndex + 1;
    let complete = false;
    while (index < raw.length) {
      while (index < raw.length && /[\s,]/.test(raw[index])) index += 1;
      if (index >= raw.length) break;
      if (raw[index] === "]") {
        complete = true;
        break;
      }
      if (raw[index] !== '"') break;

      const parsed = decodePartialJsonString(raw, index + 1);
      if (parsed.value.trim()) values.push(parsed.value.trim());
      index = parsed.end;
      if (!parsed.complete) break;
    }

    return { found: true, complete, values };
  }

  function readStreamingAnalysis(raw) {
    const data = {};
    const fields = {};
    const stringKeys = [
      "content_type", "location_status", "confidence", "title", "landmark",
      "address", "district", "province", "country", "summary", "image_description"
    ];
    const arrayKeys = ["landmarks", "foods", "observations", "identification_basis"];

    for (const key of stringKeys) {
      const result = extractStreamingString(raw, key);
      fields[key] = result;
      if (result.found) data[key] = result.value;
    }

    const score = extractStreamingNumber(raw, "confidence_score");
    fields.confidence_score = score;
    if (score.found && score.value !== null) data.confidence_score = score.value;

    for (const key of arrayKeys) {
      const result = extractStreamingArray(raw, key);
      fields[key] = result;
      if (result.found) data[key] = result.values;
    }

    return { data, fields };
  }

  function renderStreamingAnalysis(raw) {
    const { data, fields } = readStreamingAnalysis(raw);
    const contentType = String(data.content_type || "").trim().toLowerCase();
    const title = String(data.title || "").trim();
    const summary = String(data.summary || "").trim();
    const locationText = buildLocationText(data);
    const imageDescription = String(data.image_description || "").trim();
    const confidenceReady = Boolean(fields.confidence_score?.complete || fields.confidence?.complete);

    // Luôn tiếp tục điền trường đang hiển thị, nhưng mỗi tầng mới chỉ mở sau tầng phía trên.
    if (!elements.summaryCard.hidden) {
      if (contentType === "landmark") elements.resultLabel.textContent = localize("Địa danh", "Landmark");
      else if (contentType === "food") elements.resultLabel.textContent = localize("Ẩm thực", "Food");
      else elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");

      if (confidenceReady) {
        renderConfidence(data);
        elements.confidenceBadge.hidden = false;
      }
      if (title) elements.resultTitle.textContent = title;
    }

    if (!elements.resultSummary.hidden && summary) {
      elements.resultSummary.textContent = summary;
    }

    if (!elements.locationLine.hidden && locationText) {
      $("locationProvinceText").textContent = locationText;
    }

    if (!elements.landmarkCard.hidden) {
      const landmarks = cleanList(data.landmarks);
      const fallback = String(data.landmark || data.title || "").trim();
      if (landmarks.length || fallback) {
        renderList($("locationLandmarkResult"), landmarks.length ? landmarks : [fallback]);
      }
    }

    if (!elements.foodCard.hidden) {
      const foods = cleanList(data.foods);
      const fallback = String(data.title || "").trim();
      if (foods.length || fallback) {
        renderList($("locationFoodResult"), foods.length ? foods : [fallback]);
      }
    }

    if (!elements.detailCard.hidden && imageDescription) {
      renderList($("locationImageDescription"), [imageDescription]);
    }

    if (!elements.observationBlock.hidden && fields.observations?.found && data.observations.length) {
      renderList($("locationObservationResult"), data.observations);
    }

    if (!elements.evidenceBlock.hidden
      && fields.identification_basis?.found && data.identification_basis.length) {
      renderList($("locationEvidenceResult"), data.identification_basis);
    }

    // Tầng 1: tiêu đề nhận diện.
    if (streamingRevealStage === 0 && title) {
      elements.resultTitle.textContent = title;
      if (contentType === "landmark") elements.resultLabel.textContent = localize("Địa danh", "Landmark");
      else if (contentType === "food") elements.resultLabel.textContent = localize("Ẩm thực", "Food");
      if (confidenceReady) {
        renderConfidence(data);
        elements.confidenceBadge.hidden = false;
      }
      revealStreamingElement(elements.summaryCard);
      streamingRevealStage = 1;
      return;
    }

    // Tầng 2: tóm tắt chỉ mở sau khi tiêu đề đã trả xong.
    if (streamingRevealStage === 1 && fields.title?.complete && summary) {
      elements.resultSummary.textContent = summary;
      revealStreamingElement(elements.resultSummary);
      streamingRevealStage = 2;
      return;
    }

    // Tầng 3: địa chỉ chỉ mở sau khi tóm tắt đã trả xong.
    if (streamingRevealStage === 2 && fields.summary?.complete) {
      if (locationText) {
        $("locationProvinceText").textContent = locationText;
        revealStreamingElement(elements.locationLine);
        streamingRevealStage = 3;
        return;
      }
      streamingRevealStage = 3;
    }

    // Tầng 4: địa danh/ẩm thực.
    if (streamingRevealStage === 3) {
      if (contentType === "landmark") {
        const landmarks = cleanList(data.landmarks);
        const fallback = String(data.landmark || data.title || "").trim();
        if (landmarks.length || fallback) {
          renderList($("locationLandmarkResult"), landmarks.length ? landmarks : [fallback]);
          revealStreamingElement(elements.landmarkCard);
          elements.foodCard.hidden = true;
          streamingRevealStage = 4;
          return;
        }
      } else if (contentType === "food") {
        const foods = cleanList(data.foods);
        const fallback = String(data.title || "").trim();
        if (foods.length || fallback) {
          renderList($("locationFoodResult"), foods.length ? foods : [fallback]);
          revealStreamingElement(elements.foodCard);
          elements.landmarkCard.hidden = true;
          streamingRevealStage = 4;
          return;
        }
      } else if (contentType === "unknown" && fields.content_type?.complete) {
        streamingRevealStage = 4;
      }
    }

    // Tầng 5: mô tả tổng thể ảnh.
    if (streamingRevealStage === 4 && imageDescription) {
      renderList($("locationImageDescription"), [imageDescription]);
      revealStreamingElement(elements.detailCard);
      streamingRevealStage = 5;
      return;
    }

    // Tầng 6: chi tiết quan sát.
    if (streamingRevealStage === 5 && fields.observations?.found) {
      if (data.observations.length) {
        renderList($("locationObservationResult"), data.observations);
        revealStreamingElement(elements.observationBlock);
        streamingRevealStage = 6;
        return;
      }
      if (fields.observations.complete || fields.identification_basis?.found) {
        streamingRevealStage = 6;
      }
    }

    // Tầng 7: căn cứ nhận diện.
    if (streamingRevealStage === 6
      && fields.identification_basis?.found && data.identification_basis.length) {
      renderList($("locationEvidenceResult"), data.identification_basis);
      revealStreamingElement(elements.evidenceBlock);
      streamingRevealStage = 7;
    }
  }

  async function revealRemainingStreamingFields(raw, signal) {
    const finalRaw = String(raw || "");
    for (let index = 0; index < 8; index += 1) {
      if (signal?.aborted) throw createAbortError();

      const previousStage = streamingRevealStage;
      renderStreamingAnalysis(finalRaw);
      if (streamingRevealStage === previousStage) break;
      await waitForDelay(90, signal);
    }
  }

  async function readNdjsonStream(response, onEvent) {
    if (!response.body || typeof response.body.getReader !== "function") {
      const body = await response.text();
      for (const line of body.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        onEvent(JSON.parse(trimmed));
      }
      return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder("utf-8");
    let buffer = "";

    while (true) {
      const { value, done } = await reader.read();
      buffer += decoder.decode(value || new Uint8Array(), { stream: !done });

      let newlineIndex;
      while ((newlineIndex = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newlineIndex).trim();
        buffer = buffer.slice(newlineIndex + 1);
        if (!line) continue;
        onEvent(JSON.parse(line));
      }

      if (done) break;
    }

    const remaining = buffer.trim();
    if (remaining) onEvent(JSON.parse(remaining));
  }

  function setAnalyzing(value) {
    isAnalyzing = value;
    elements.analyzeButton.disabled = !selectedImageData;
    elements.analyzeButton.classList.toggle("is-cancel-analysis", value);
    elements.analyzeButton.setAttribute(
      "aria-label",
      value
        ? localize("Dừng AI phân tích địa danh", "Stop AI landmark analysis")
        : localize("Phân tích bằng AI", "Analyze with AI")
    );
    elements.analyzeButton.title = value
      ? localize("Bấm để dừng phân tích", "Click to stop analysis")
      : "";
    elements.analyzeButton.querySelector("span:last-child").textContent = value
      ? localize("AI đang phân tích...", "AI is analyzing...")
      : localize("Phân tích bằng AI", "Analyze with AI");
  }

  function cancelLocationAnalysis() {
    const controller = analysisAbortController;
    if (!controller || controller.signal.aborted) return;

    controller.abort("user-cancelled");
    if (analysisAbortController !== controller) return;
    analysisAbortController = null;

    setAnalyzing(false);
    resetStreamingPreview();
    resetResultCards();
    setResultState("empty");
    setMessage("");
  }

  function clearPreviewUrl() {
    if (selectedPreviewUrl) URL.revokeObjectURL(selectedPreviewUrl);
    selectedPreviewUrl = "";
  }

  function resetResultCards() {
    elements.summaryCard.hidden = true;
    elements.confidenceBadge.hidden = true;
    elements.resultSummary.hidden = true;
    elements.landmarkCard.hidden = true;
    elements.foodCard.hidden = true;
    elements.detailCard.hidden = true;
    elements.observationBlock.hidden = true;
    elements.evidenceBlock.hidden = true;
    elements.locationLine.hidden = true;
    [
      "locationResultTitle", "locationResultSummary", "locationLandmarkResult",
      "locationFoodResult", "locationImageDescription", "locationObservationResult",
      "locationEvidenceResult"
    ].forEach((id) => $(id)?.classList.remove("is-streaming-placeholder"));
  }

  function clearSelection(options = {}) {
    selectedFile = null;
    selectedImageData = "";
    clearPreviewUrl();
    if (elements.input) elements.input.value = "";
    if (elements.previewImage) elements.previewImage.removeAttribute("src");
    elements.preview.hidden = true;
    elements.placeholder.hidden = false;
    elements.analyzeButton.disabled = true;
    resetResultCards();
    resetStreamingPreview();
    setMessage("");
    if (options.resetResult !== false) setResultState("empty");
  }

  function readFileAsDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(new Error("read-failed"));
      reader.readAsDataURL(file);
    });
  }

  async function prepareImage(file) {
    if (!file || !String(file.type || "").startsWith("image/")) {
      throw new Error("invalid-image");
    }
    if (file.size > MAX_IMAGE_BYTES) {
      throw new Error("image-too-large");
    }

    const optimizer = window.TravelwAIImageOptimizer;
    const optimized = optimizer?.optimizeImageFileForAi
      ? await optimizer.optimizeImageFileForAi(file)
      : { file, width: 0, height: 0, originalSize: file.size, optimizedSize: file.size };
    const preparedFile = optimized.file || file;
    const dataUrl = await readFileAsDataUrl(preparedFile);
    const base64 = dataUrl.replace(/^data:image\/[^;]+;base64,/, "");
    if (!base64 || base64.length > 3_500_000) throw new Error("image-unavailable");

    return {
      file: preparedFile,
      base64,
      width: Number(optimized.width || 0),
      height: Number(optimized.height || 0),
      originalSize: Number(optimized.originalSize || file.size),
      optimizedSize: Number(optimized.optimizedSize || preparedFile.size)
    };
  }

  async function selectFile(file) {
    if (isAnalyzing) return;
    setMessage(localize("Đang chuẩn bị ảnh...", "Preparing image..."));
    elements.analyzeButton.disabled = true;
    try {
      const prepared = await prepareImage(file);
      selectedFile = file;
      selectedImageData = prepared.base64;
      clearPreviewUrl();
      selectedPreviewUrl = URL.createObjectURL(prepared.file);
      elements.previewImage.src = selectedPreviewUrl;
      elements.imageName.textContent = file.name || localize("Ảnh đã sao chép", "Pasted image");
      const dimensions = prepared.width && prepared.height ? `${prepared.width} × ${prepared.height}px · ` : "";
      const optimizedText = prepared.optimizedSize < prepared.originalSize
        ? `${formatBytes(prepared.originalSize)} → ${formatBytes(prepared.optimizedSize)}`
        : formatBytes(prepared.optimizedSize);
      elements.imageMeta.textContent = `${dimensions}${optimizedText}`;
      elements.placeholder.hidden = true;
      elements.preview.hidden = false;
      elements.analyzeButton.disabled = false;
      setMessage(localize("Ảnh đã sẵn sàng để phân tích.", "The image is ready for analysis."), "success");
      setResultState("empty");
    } catch (_) {
      clearSelection();
      setMessage("", "error");
    }
  }

  function cleanList(value) {
    const values = Array.isArray(value) ? value : (value ? [value] : []);
    return values
      .map((item) => String(item || "").trim())
      .filter(Boolean)
      .filter((item, index, array) => array.indexOf(item) === index)
      .slice(0, 6);
  }

  function parseAiJson(reply) {
    const raw = String(reply || "").trim();
    const candidates = [raw];
    const fenced = raw.match(/```(?:json)?\s*([\s\S]*?)```/i);
    if (fenced?.[1]) candidates.push(fenced[1].trim());
    const start = raw.indexOf("{");
    const end = raw.lastIndexOf("}");
    if (start >= 0 && end > start) candidates.push(raw.slice(start, end + 1));

    for (const candidate of candidates) {
      try {
        const parsed = JSON.parse(candidate);
        if (parsed && typeof parsed === "object") return parsed;
      } catch (_) { }
    }
    return null;
  }

  function renderList(target, values) {
    const list = cleanList(values);
    target.innerHTML = list.length <= 1
      ? `<p>${escapeHtml(list[0] || localize("Chưa xác định", "Not identified"))}</p>`
      : `<ul>${list.map((item) => `<li>${escapeHtml(item)}</li>`).join("")}</ul>`;
  }

  function buildLocationText(data) {
    const values = [data?.address, data?.district, data?.province, data?.country]
      .map((value) => String(value || "").trim())
      .filter(Boolean)
      .filter((value, index, array) => array.indexOf(value) === index);
    return values.join(", ");
  }

  function normalizeConfidence(data) {
    const rawScore = Number(data?.confidence_score ?? data?.confidenceScore);
    const score = Number.isFinite(rawScore) ? Math.max(0, Math.min(100, Math.round(rawScore))) : null;
    const confidence = String(data?.confidence || "").trim().toLowerCase();
    if ((score !== null && score >= 80) || confidence.includes("cao") || confidence.includes("high")) {
      return { level: "high", score };
    }
    if ((score !== null && score >= 55) || confidence.includes("trung") || confidence.includes("medium")) {
      return { level: "medium", score };
    }
    return { level: "low", score };
  }

  function resolveContentType(data) {
    const explicit = String(data?.content_type || data?.contentType || data?.type || "").trim().toLowerCase();
    if (["food", "am-thuc", "ẩm thực", "cuisine"].includes(explicit)) return "food";
    if (["landmark", "location", "place", "dia-danh", "địa danh"].includes(explicit)) return "landmark";
    if (["unknown", "khong-xac-dinh", "không xác định"].includes(explicit)) return "unknown";

    const foods = cleanList(data?.foods);
    const landmarks = cleanList(data?.landmarks || data?.landmark);
    if (foods.length && !landmarks.length) return "food";
    if (landmarks.length) return "landmark";
    return "unknown";
  }

  function renderConfidence(data) {
    const confidence = normalizeConfidence(data);
    const badge = $("locationConfidenceBadge");
    const scoreText = confidence.score !== null ? ` · ${confidence.score}%` : "";
    badge.className = "confidence-badge";
    if (confidence.level === "high") {
      badge.textContent = `${localize("Tin cậy cao", "High confidence")}${scoreText}`;
      badge.classList.add("is-high");
    } else if (confidence.level === "medium") {
      badge.textContent = `${localize("Tin cậy vừa", "Medium confidence")}${scoreText}`;
      badge.classList.add("is-medium");
    } else {
      badge.textContent = `${localize("Tin cậy thấp", "Low confidence")}${scoreText}`;
      badge.classList.add("is-low");
    }
  }

  function renderAnalysis(data) {
    const type = resolveContentType(data);
    resetResultCards();
    elements.resultPanel?.classList.remove("is-streaming");
    if (elements.streamingBanner) elements.streamingBanner.hidden = true;
    if (elements.analyzeAgainButton) elements.analyzeAgainButton.hidden = false;
    elements.summaryCard.hidden = false;
    elements.confidenceBadge.hidden = false;
    renderConfidence(data);

    const summary = String(data?.summary || "").trim();
    const unknownText = localize("Chưa xác định", "Not identified");
    const title = String(data?.title || data?.landmark || unknownText).trim();
    $("locationResultTitle").textContent = title || unknownText;
    $("locationResultSummary").textContent = summary;
    elements.resultSummary.hidden = !summary;

    if (type === "landmark") {
      elements.resultLabel.textContent = localize("Địa danh", "Landmark");
      const landmarkValues = cleanList(data?.landmarks || data?.landmark);
      renderList($("locationLandmarkResult"), landmarkValues.length ? landmarkValues : [title]);
      elements.landmarkCard.hidden = false;

      const locationText = buildLocationText(data);
      if (locationText) {
        $("locationProvinceText").textContent = locationText;
        elements.locationLine.hidden = false;
      }
    } else if (type === "food") {
      elements.resultLabel.textContent = localize("Ẩm thực", "Food");
      const foodValues = cleanList(data?.foods);
      renderList($("locationFoodResult"), foodValues.length ? foodValues : [title]);
      elements.foodCard.hidden = false;
    } else {
      elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");
      $("locationResultTitle").textContent = title || unknownText;
    }

    const imageDescription = String(
      data?.image_description || data?.imageDescription || data?.detailed_analysis || data?.detailedAnalysis || summary || localize("Chưa có mô tả chi tiết.", "No detailed description is available.")
    ).trim();
    renderList($("locationImageDescription"), [imageDescription]);

    const observations = cleanList(data?.observations || data?.visual_details || data?.visualDetails);
    if (observations.length) {
      renderList($("locationObservationResult"), observations);
      elements.observationBlock.hidden = false;
    }

    const evidence = cleanList(data?.identification_basis || data?.identificationBasis || data?.evidence);
    if (evidence.length) {
      renderList($("locationEvidenceResult"), evidence);
      elements.evidenceBlock.hidden = false;
    }
    elements.detailCard.hidden = false;

    setResultState("content");
  }

  function isInternalServerError(value) {
    const status = Number(value?.status ?? value?.statusCode ?? value?.httpStatus ?? 0);
    if (status === 500) return true;

    let text = "";
    if (typeof value === "string") {
      text = value;
    } else if (value) {
      try {
        text = JSON.stringify(value);
      } catch (_) {
        text = String(value?.message || value?.error || value?.title || value?.statusText || "");
      }
    }

    return /internal\s*server\s*error/i.test(text)
      || /internalservererror/i.test(text)
      || /\bhttp\s*500\b/i.test(text);
  }

  function createAbortError() {
    try {
      return new DOMException("The operation was aborted.", "AbortError");
    } catch (_) {
      const error = new Error("The operation was aborted.");
      error.name = "AbortError";
      return error;
    }
  }

  function isAbortError(error) {
    return error?.name === "AbortError" || error?.code === 20;
  }

  function waitForDelay(delay, signal) {
    if (signal?.aborted) return Promise.reject(createAbortError());

    return new Promise((resolve, reject) => {
      const timeoutId = window.setTimeout(() => {
        signal?.removeEventListener("abort", handleAbort);
        resolve();
      }, Math.max(0, Number(delay) || 0));

      function handleAbort() {
        window.clearTimeout(timeoutId);
        signal?.removeEventListener("abort", handleAbort);
        reject(createAbortError());
      }

      signal?.addEventListener("abort", handleAbort, { once: true });
    });
  }

  function waitForRetry(attempt, signal) {
    const delay = Math.min(INTERNAL_SERVER_RETRY_DELAY_MS * Math.max(1, attempt), 1600);
    return waitForDelay(delay, signal);
  }

  async function performLocationAnalysisAttempt(token, signal) {
    if (signal?.aborted) throw createAbortError();

    const response = await fetch("/api/ai/location-analysis/stream", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/x-ndjson",
        Authorization: `Bearer ${token}`
      },
      body: JSON.stringify({
        Image: selectedImageData,
        Language: getCurrentLanguage()
      }),
      signal
    });

    if (!response.ok) {
      const rawError = await response.text().catch(() => "");
      let errorPayload = rawError;
      try {
        errorPayload = rawError ? JSON.parse(rawError) : {};
      } catch (_) {
      }

      const requestError = new Error("analysis-failed");
      requestError.status = response.status;
      requestError.isInternalServerError = response.status === 500
        || isInternalServerError(response.statusText)
        || isInternalServerError(errorPayload);
      throw requestError;
    }

    let streamedReply = "";
    let completedReply = "";
    let streamError = null;

    await readNdjsonStream(response, (event) => {
      const type = String(event?.type || "").toLowerCase();
      if (type === "status") {
        setLoadingStatus(event.message);
        return;
      }
      if (type === "delta") {
        const delta = String(event.delta || "");
        streamedReply += delta;
        renderStreamingAnalysis(streamedReply);
        setLoadingStatus(localize("AI đang điền thông tin vào kết quả...", "AI is filling in the result..."));
        return;
      }
      if (type === "completed") {
        completedReply = String(event.analysis || streamedReply).trim();
        return;
      }
      if (type === "error") {
        streamError = event || { message: "analysis-failed" };
      }
    });

    if (streamError) {
      const requestError = new Error(String(streamError.message || "analysis-failed"));
      requestError.isInternalServerError = isInternalServerError(streamError);
      throw requestError;
    }

    const reply = (completedReply || streamedReply).trim();
    const parsed = parseAiJson(reply);
    if (!parsed) throw new Error("analysis-failed");

    return { reply, parsed };
  }

  async function analyzeImage() {
    if (isAnalyzing) {
      cancelLocationAnalysis();
      return;
    }
    if (!selectedImageData) return;

    let token = getToken();
    if (!token && typeof window.refreshTokenIfNeeded === "function") {
      const refreshed = await window.refreshTokenIfNeeded();
      if (refreshed) token = getToken();
    }
    if (!token) {
      resetStreamingPreview();
      resetResultCards();
      setResultState("error");
      setMessage(localize("Vui lòng thử lại", "Please try again"), "error");
      if (typeof window.redirectToLogin === "function") {
        window.setTimeout(() => window.redirectToLogin("/location-analysis"), 500);
      }
      return;
    }

    const abortController = new AbortController();
    analysisAbortController = abortController;
    const { signal } = abortController;

    setAnalyzing(true);
    setMessage("");
    resetStreamingPreview();
    beginStreamingAnalysis();

    try {
      let result = null;

      for (let retryCount = 0; retryCount <= MAX_INTERNAL_SERVER_RETRIES; retryCount += 1) {
        if (signal.aborted) throw createAbortError();

        if (retryCount > 0) {
          resetStreamingPreview();
          beginStreamingAnalysis();
          await waitForRetry(retryCount, signal);
        }

        try {
          result = await performLocationAnalysisAttempt(token, signal);
          break;
        } catch (error) {
          const canRetry = error?.isInternalServerError === true
            && retryCount < MAX_INTERNAL_SERVER_RETRIES;
          if (canRetry) continue;
          throw error;
        }
      }

      if (!result) throw new Error("analysis-failed");
      if (signal.aborted) throw createAbortError();

      await revealRemainingStreamingFields(result.reply, signal);
      if (signal.aborted) throw createAbortError();

      renderAnalysis(result.parsed);
      setMessage(localize("Phân tích hoàn tất.", "Analysis completed."), "success");
    } catch (error) {
      if (isAbortError(error) || signal.aborted) {
        if (analysisAbortController === abortController) {
          resetStreamingPreview();
          resetResultCards();
          setResultState("empty");
          setMessage("");
        }
        return;
      }

      if (analysisAbortController === abortController) {
        resetStreamingPreview();
        resetResultCards();
        setResultState("error");
        setMessage(localize("Vui lòng thử lại", "Please try again"), "error");
      }
    } finally {
      if (analysisAbortController === abortController) {
        analysisAbortController = null;
        setAnalyzing(false);
      }
    }
  }

  function openFilePicker(event) {
    event?.preventDefault();
    event?.stopPropagation();
    if (isAnalyzing || !elements.input) return;

    elements.input.value = "";
    try {
      if (typeof elements.input.showPicker === "function") {
        elements.input.showPicker();
      } else {
        elements.input.click();
      }
    } catch (_) {
      elements.input.click();
    }
  }

  function extensionFromMime(type) {
    const normalized = String(type || "").toLowerCase();
    if (normalized.includes("jpeg")) return "jpg";
    if (normalized.includes("webp")) return "webp";
    if (normalized.includes("heic")) return "heic";
    if (normalized.includes("heif")) return "heif";
    return "png";
  }

  function handlePaste(event) {
    if (isAnalyzing || selectedImageData) return;
    const items = Array.from(event.clipboardData?.items || []);
    const imageItem = items.find((item) => String(item.type || "").startsWith("image/"));
    if (!imageItem) return;

    const pastedBlob = imageItem.getAsFile();
    if (!pastedBlob) return;

    event.preventDefault();
    const extension = extensionFromMime(pastedBlob.type);
    const pastedFile = new File([pastedBlob], `${localize("Ảnh đã sao chép", "Pasted image")}.${extension}`, {
      type: pastedBlob.type || `image/${extension}`,
      lastModified: Date.now()
    });
    void selectFile(pastedFile);
  }

  function bindEvents() {
    elements.chooseButton.addEventListener("click", openFilePicker);
    elements.dropzone.addEventListener("click", (event) => {
      if (event.target.closest("#locationRemoveImageButton")) return;
      openFilePicker(event);
    });
    elements.dropzone.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") openFilePicker(event);
    });
    elements.input.addEventListener("change", () => {
      const file = elements.input.files?.[0];
      if (file) void selectFile(file);
    });
    elements.removeButton.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (!isAnalyzing) clearSelection();
    });
    elements.analyzeButton.addEventListener("click", () => {
      if (isAnalyzing) {
        cancelLocationAnalysis();
        return;
      }
      void analyzeImage();
    });
    elements.analyzeAgainButton.addEventListener("click", (event) => {
      clearSelection();
      openFilePicker(event);
    });

    ["dragenter", "dragover"].forEach((eventName) => {
      elements.dropzone.addEventListener(eventName, (event) => {
        event.preventDefault();
        if (!isAnalyzing) elements.dropzone.classList.add("is-dragover");
      });
    });
    ["dragleave", "drop"].forEach((eventName) => {
      elements.dropzone.addEventListener(eventName, (event) => {
        event.preventDefault();
        elements.dropzone.classList.remove("is-dragover");
      });
    });
    elements.dropzone.addEventListener("drop", (event) => {
      const file = event.dataTransfer?.files?.[0];
      if (file && !isAnalyzing) void selectFile(file);
    });

    document.addEventListener("paste", handlePaste);
    window.addEventListener("beforeunload", () => {
      analysisAbortController?.abort("page-unload");
      clearPreviewUrl();
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initializeElements();
    bindEvents();
    clearSelection();
  });
})();
