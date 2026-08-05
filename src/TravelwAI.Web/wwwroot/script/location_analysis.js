(function () {
  "use strict";

  const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
  let selectedFile = null;
  let selectedImageData = "";
  let selectedPreviewUrl = "";
  let isAnalyzing = false;
  let analysisAbortController = null;
  let analysisStreamReader = null;
  let analysisStoppedByUser = false;
  let streamedAnalysisText = "";

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
    elements.loadingTitle = $("locationLoadingTitle");
    elements.loadingText = $("locationLoadingText");
    elements.stopButton = $("locationAnalysisStopButton");
    elements.stopButtonLabel = $("locationAnalysisStopLabel");
    elements.content = $("locationResultContent");
    elements.resultLabel = $("locationResultLabel");
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
    elements.message.textContent = type === "error"
      ? (text || localize("Hãy thử lại sau.", "Please try again later."))
      : (text || "");
    elements.message.classList.toggle("is-error", type === "error");
    elements.message.classList.toggle("is-success", type === "success");
  }

  function setResultState(state) {
    const nextState = ["empty", "loading", "content"].includes(state) ? state : "empty";
    const previousState = elements.resultPanel?.dataset.resultState || "";
    if (elements.resultPanel) elements.resultPanel.dataset.resultState = nextState;
    elements.empty.hidden = nextState !== "empty";
    elements.loading.hidden = nextState !== "loading";
    elements.content.hidden = nextState !== "content";
    if (nextState === "content" && previousState !== "content") elements.content.scrollTop = 0;
  }

  function setAnalyzing(value) {
    isAnalyzing = value;
    elements.analyzeButton.disabled = !selectedImageData;
    elements.analyzeButton.classList.toggle("is-analyzing", value);
    elements.analyzeButton.setAttribute("aria-busy", value ? "true" : "false");
    elements.analyzeButton.setAttribute(
      "aria-label",
      value ? localize("Dừng AI phân tích ảnh", "Stop AI image analysis") : localize("Phân tích ảnh bằng AI", "Analyze image with AI")
    );
    elements.analyzeButton.title = value
      ? localize("Bấm để dừng AI", "Click to stop AI")
      : localize("Phân tích bằng AI", "Analyze with AI");
    elements.analyzeButton.querySelector("span:last-child").textContent = value
      ? localize("Đang phân tích...", "Analyzing...")
      : localize("Phân tích bằng AI", "Analyze with AI");

    if (elements.stopButton) {
      elements.stopButton.hidden = !value;
      elements.stopButton.classList.remove("is-stopping");
      elements.stopButton.setAttribute("aria-busy", value ? "true" : "false");
    }
    if (elements.stopButtonLabel) {
      elements.stopButtonLabel.textContent = localize("AI đang phân tích", "AI is analyzing");
    }
    if (elements.analyzeAgainButton) elements.analyzeAgainButton.hidden = value;
  }

  function clearPreviewUrl() {
    if (selectedPreviewUrl) URL.revokeObjectURL(selectedPreviewUrl);
    selectedPreviewUrl = "";
  }

  function resetResultCards() {
    elements.landmarkCard.hidden = true;
    elements.foodCard.hidden = true;
    elements.detailCard.hidden = true;
    elements.observationBlock.hidden = true;
    elements.evidenceBlock.hidden = true;
    elements.locationLine.hidden = true;
  }

  function resetStreamingResult() {
    streamedAnalysisText = "";
    resetResultCards();
    elements.content?.classList.remove("is-streaming");
    elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");
    $("locationConfidenceBadge").className = "confidence-badge is-pending";
    $("locationConfidenceBadge").textContent = localize("Đang đánh giá", "Evaluating");
    $("locationResultTitle").textContent = localize("Đang nhận diện...", "Identifying...");
    $("locationResultSummary").textContent = "";
    $("locationProvinceText").textContent = "";
    $("locationLandmarkResult").innerHTML = "";
    $("locationFoodResult").innerHTML = "";
    $("locationImageDescription").innerHTML = "";
    $("locationObservationResult").innerHTML = "";
    $("locationEvidenceResult").innerHTML = "";
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
    resetStreamingResult();
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

  function cleanAnalysisText(value) {
    const text = String(value ?? "").trim();
    return /^(exact|probable)$/i.test(text) ? "" : text;
  }

  function cleanList(value) {
    const values = Array.isArray(value) ? value : (value ? [value] : []);
    return values
      .map((item) => cleanAnalysisText(item))
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

  function decodePartialJsonString(rawValue) {
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

  function findJsonValueStart(rawJson, fieldName) {
    const source = String(rawJson || "");
    const marker = `"${fieldName}"`;
    const markerIndex = source.indexOf(marker);
    if (markerIndex < 0) return -1;
    const colonIndex = source.indexOf(":", markerIndex + marker.length);
    if (colonIndex < 0) return -1;
    let index = colonIndex + 1;
    while (index < source.length && /\s/.test(source[index])) index += 1;
    return index;
  }

  function extractPartialJsonString(rawJson, fieldName) {
    const source = String(rawJson || "");
    const start = findJsonValueStart(source, fieldName);
    if (start < 0 || source[start] !== '"') return { found: false, value: "", complete: false };

    let encoded = "";
    let escaped = false;
    for (let index = start + 1; index < source.length; index += 1) {
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
      if (character === '"') {
        return { found: true, value: decodePartialJsonString(encoded), complete: true };
      }
      encoded += character;
    }
    if (escaped) encoded += "\\";
    return { found: true, value: decodePartialJsonString(encoded), complete: false };
  }

  function extractPartialJsonNumber(rawJson, fieldName) {
    const source = String(rawJson || "");
    const start = findJsonValueStart(source, fieldName);
    if (start < 0) return { found: false, value: null };
    const match = source.slice(start).match(/^-?\d+(?:\.\d+)?/);
    if (!match) return { found: true, value: null };
    const value = Number(match[0]);
    return { found: true, value: Number.isFinite(value) ? value : null };
  }

  function extractPartialJsonStringArray(rawJson, fieldName) {
    const source = String(rawJson || "");
    const start = findJsonValueStart(source, fieldName);
    if (start < 0 || source[start] !== "[") return { found: false, values: [], complete: false };

    const values = [];
    let index = start + 1;
    while (index < source.length) {
      while (index < source.length && /[\s,]/.test(source[index])) index += 1;
      if (index >= source.length) break;
      if (source[index] === "]") return { found: true, values: cleanList(values), complete: true };
      if (source[index] !== '"') {
        index += 1;
        continue;
      }

      let encoded = "";
      let escaped = false;
      let closed = false;
      index += 1;
      for (; index < source.length; index += 1) {
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
        if (character === '"') {
          closed = true;
          index += 1;
          break;
        }
        encoded += character;
      }
      const decoded = decodePartialJsonString(encoded).trim();
      if (decoded) values.push(decoded);
      if (!closed) break;
    }

    return { found: true, values: cleanList(values), complete: false };
  }

  function revealStreamingBlock(target) {
    if (!target || !target.hidden) return;
    target.hidden = false;
    target.classList.remove("stream-reveal");
    void target.offsetWidth;
    target.classList.add("stream-reveal");
  }

  function renderStreamingList(target, values, fallbackText = "") {
    const list = cleanList(values);
    if (list.length > 0) {
      target.innerHTML = list.length === 1
        ? `<p>${escapeHtml(list[0])}</p>`
        : `<ul>${list.map((item) => `<li>${escapeHtml(item)}</li>`).join("")}</ul>`;
      return;
    }
    target.innerHTML = fallbackText ? `<p class="stream-placeholder">${escapeHtml(fallbackText)}</p>` : "";
  }

  function renderStreamingAnalysis(rawJson) {
    const contentType = extractPartialJsonString(rawJson, "content_type");
    const confidence = extractPartialJsonString(rawJson, "confidence");
    const confidenceScore = extractPartialJsonNumber(rawJson, "confidence_score");
    const title = extractPartialJsonString(rawJson, "title");
    const landmark = extractPartialJsonString(rawJson, "landmark");
    const district = extractPartialJsonString(rawJson, "district");
    const province = extractPartialJsonString(rawJson, "province");
    const country = extractPartialJsonString(rawJson, "country");
    const summary = extractPartialJsonString(rawJson, "summary");
    const imageDescription = extractPartialJsonString(rawJson, "image_description");
    const landmarks = extractPartialJsonStringArray(rawJson, "landmarks");
    const foods = extractPartialJsonStringArray(rawJson, "foods");
    const observations = extractPartialJsonStringArray(rawJson, "observations");
    const evidence = extractPartialJsonStringArray(rawJson, "identification_basis");

    const hasVisibleData = contentType.found || confidenceScore.found || title.found || summary.found || imageDescription.found;
    if (!hasVisibleData) return;

    setResultState("content");
    elements.content.classList.add("is-streaming");

    const partialData = {
      content_type: cleanAnalysisText(contentType.value),
      confidence: cleanAnalysisText(confidence.value),
      confidence_score: confidenceScore.value,
      title: cleanAnalysisText(title.value),
      landmark: cleanAnalysisText(landmark.value),
      district: cleanAnalysisText(district.value),
      province: cleanAnalysisText(province.value),
      country: cleanAnalysisText(country.value),
      summary: cleanAnalysisText(summary.value),
      image_description: cleanAnalysisText(imageDescription.value),
      landmarks: landmarks.values,
      foods: foods.values,
      observations: observations.values,
      identification_basis: evidence.values
    };

    const type = contentType.value ? resolveContentType(partialData) : "";
    if (type === "landmark") elements.resultLabel.textContent = localize("Địa danh", "Landmark");
    else if (type === "food") elements.resultLabel.textContent = localize("Ẩm thực", "Food");
    else if (type === "unknown") elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");
    else elements.resultLabel.textContent = localize("Đang nhận diện", "Identifying");

    if (confidenceScore.value !== null || confidence.value) {
      renderConfidence(partialData);
    }

    if (title.found) {
      $("locationResultTitle").textContent = partialData.title || localize("Đang nhận diện...", "Identifying...");
    }
    if (summary.found) {
      $("locationResultSummary").textContent = partialData.summary || localize("Đang hoàn thiện kết luận...", "Finalizing the conclusion...");
    }

    const locationText = buildLocationText(partialData);
    if (locationText) {
      $("locationProvinceText").textContent = locationText;
      revealStreamingBlock(elements.locationLine);
    }

    if (type === "landmark" && (landmark.found || landmarks.found || title.found)) {
      const values = landmarks.values.length ? landmarks.values : cleanList([partialData.landmark || partialData.title]);
      renderStreamingList($("locationLandmarkResult"), values, localize("Đang xác định địa danh...", "Identifying the landmark..."));
      revealStreamingBlock(elements.landmarkCard);
      elements.foodCard.hidden = true;
    } else if (type === "food" && (foods.found || title.found)) {
      const values = foods.values.length ? foods.values : cleanList([partialData.title]);
      renderStreamingList($("locationFoodResult"), values, localize("Đang xác định món ăn...", "Identifying the food..."));
      revealStreamingBlock(elements.foodCard);
      elements.landmarkCard.hidden = true;
    }

    if (imageDescription.found) {
      renderStreamingList(
        $("locationImageDescription"),
        partialData.image_description ? [partialData.image_description] : [],
        localize("AI đang mô tả ảnh...", "AI is describing the image...")
      );
      revealStreamingBlock(elements.detailCard);
    }

    if (observations.found) {
      renderStreamingList(
        $("locationObservationResult"),
        observations.values,
        localize("AI đang bổ sung chi tiết quan sát...", "AI is adding visual observations...")
      );
      revealStreamingBlock(elements.observationBlock);
      revealStreamingBlock(elements.detailCard);
    }

    if (evidence.found) {
      renderStreamingList(
        $("locationEvidenceResult"),
        evidence.values,
        localize("AI đang đối chiếu căn cứ nhận diện...", "AI is checking identification evidence...")
      );
      revealStreamingBlock(elements.evidenceBlock);
      revealStreamingBlock(elements.detailCard);
    }
  }

  function renderList(target, values) {
    const list = cleanList(values);
    target.innerHTML = list.length <= 1
      ? `<p>${escapeHtml(list[0] || localize("Chưa xác định", "Not identified"))}</p>`
      : `<ul>${list.map((item) => `<li>${escapeHtml(item)}</li>`).join("")}</ul>`;
  }

  function buildLocationText(data) {
    const values = [data?.district, data?.province, data?.country]
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
    const normalizedData = { ...(data || {}) };
    delete normalizedData.location_status;
    delete normalizedData.locationStatus;
    delete normalizedData.address;
    normalizedData.title = cleanAnalysisText(normalizedData.title);
    normalizedData.landmark = cleanAnalysisText(normalizedData.landmark);
    normalizedData.summary = cleanAnalysisText(normalizedData.summary);
    normalizedData.image_description = cleanAnalysisText(normalizedData.image_description);
    normalizedData.district = cleanAnalysisText(normalizedData.district);
    normalizedData.province = cleanAnalysisText(normalizedData.province);
    normalizedData.country = cleanAnalysisText(normalizedData.country);
    data = normalizedData;

    const type = resolveContentType(data);
    resetResultCards();
    renderConfidence(data);

    const summary = String(data?.summary || "").trim();
    const unknownText = localize("Chưa xác định", "Not identified");
    const title = String(data?.title || data?.landmark || unknownText).trim();
    $("locationResultTitle").textContent = title || unknownText;
    $("locationResultSummary").textContent = summary;

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
    elements.content.classList.remove("is-streaming");

    setResultState("content");
  }

  function updateLoadingStatus(message) {
    if (elements.loadingTitle) {
      elements.loadingTitle.textContent = localize("AI đang quan sát ảnh", "AI is examining the image");
    }
    if (elements.loadingText && message) elements.loadingText.textContent = message;
  }

  async function stopAnalysis() {
    if (!isAnalyzing) return;
    analysisStoppedByUser = true;
    elements.stopButton?.classList.add("is-stopping");
    if (elements.stopButtonLabel) {
      elements.stopButtonLabel.textContent = localize("Đang dừng AI...", "Stopping AI...");
    }
    setMessage(localize("Đang dừng phân tích...", "Stopping analysis..."));
    analysisAbortController?.abort("user-cancelled");
    try {
      await analysisStreamReader?.cancel?.("user-cancelled");
    } catch (_) { }
  }

  async function analyzeImage() {
    if (!selectedImageData) return;
    if (isAnalyzing) {
      await stopAnalysis();
      return;
    }

    let token = getToken();
    if (!token && typeof window.refreshTokenIfNeeded === "function") {
      const refreshed = await window.refreshTokenIfNeeded();
      if (refreshed) token = getToken();
    }
    if (!token) {
      setMessage("", "error");
      if (typeof window.redirectToLogin === "function") {
        window.setTimeout(() => window.redirectToLogin("/location-analysis"), 500);
      }
      return;
    }

    const abortController = new AbortController();
    analysisAbortController = abortController;
    analysisStoppedByUser = false;
    resetStreamingResult();
    setAnalyzing(true);
    setMessage("");
    updateLoadingStatus(localize("Đang nhận diện nội dung trong ảnh...", "Recognizing the image content..."));
    setResultState("loading");

    let completedAnalysis = "";
    try {
      const response = await fetch("/api/ai/location-analysis-stream", {
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
        signal: abortController.signal
      });

      if (!response.ok || !response.body) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.message || localize("Không thể phân tích ảnh.", "Unable to analyze the image."));
      }

      const reader = response.body.getReader();
      analysisStreamReader = reader;
      const decoder = new TextDecoder();
      let buffer = "";
      let streamCompleted = false;

      const processLine = (line) => {
        if (!line) return;
        const event = JSON.parse(line);
        const type = String(event.type || "").toLowerCase();
        if (type === "status") {
          updateLoadingStatus(event.message || localize("AI đang phân tích ảnh...", "AI is analyzing the image..."));
          return;
        }
        if (type === "delta") {
          streamedAnalysisText += String(event.delta || "");
          renderStreamingAnalysis(streamedAnalysisText);
          return;
        }
        if (type === "reset") {
          resetStreamingResult();
          setResultState("loading");
          const retryMessage = event.message || localize("Máy chủ AI gặp lỗi, đang thử lại...", "The AI server failed, retrying...");
          updateLoadingStatus(retryMessage);
          setMessage(retryMessage);
          return;
        }
        if (type === "completed") {
          completedAnalysis = String(event.analysis || streamedAnalysisText || "").trim();
          streamCompleted = true;
          return;
        }
        if (type === "error") {
          throw new Error(event.message || localize("Không thể phân tích ảnh.", "Unable to analyze the image."));
        }
      };

      while (!streamCompleted) {
        const { value, done } = await reader.read();
        buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
        let newlineIndex;
        while ((newlineIndex = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, newlineIndex).trim();
          buffer = buffer.slice(newlineIndex + 1);
          processLine(line);
          if (streamCompleted) break;
        }
        if (done) {
          const tail = buffer.trim();
          if (tail) processLine(tail);
          break;
        }
      }

      if (!streamCompleted || !completedAnalysis) {
        throw new Error(localize("Luồng AI kết thúc trước khi có kết quả hoàn chỉnh.", "The AI stream ended before returning a complete result."));
      }

      const parsed = parseAiJson(completedAnalysis);
      if (!parsed) throw new Error(localize("Kết quả AI không đúng định dạng.", "The AI result has an invalid format."));

      renderAnalysis(parsed);
      setMessage(localize("Phân tích hoàn tất.", "Analysis completed."), "success");
    } catch (error) {
      const wasStopped = analysisStoppedByUser || error?.name === "AbortError";
      resetStreamingResult();
      setResultState("empty");
      if (wasStopped) {
        setMessage(localize("Đã dừng phân tích.", "Analysis stopped."));
      } else {
        setMessage(error?.message || "", "error");
      }
    } finally {
      if (analysisStreamReader) {
        try { analysisStreamReader.releaseLock?.(); } catch (_) { }
      }
      if (analysisAbortController === abortController) analysisAbortController = null;
      analysisStreamReader = null;
      analysisStoppedByUser = false;
      setAnalyzing(false);
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
    elements.analyzeButton.addEventListener("click", () => void analyzeImage());
    elements.stopButton?.addEventListener("click", () => void stopAnalysis());
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
      try { void analysisStreamReader?.cancel?.("page-unload"); } catch (_) { }
      clearPreviewUrl();
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initializeElements();
    bindEvents();
    clearSelection();
  });
})();
