(function () {
  "use strict";

  const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
  let selectedFile = null;
  let selectedImageData = "";
  let selectedPreviewUrl = "";
  let isAnalyzing = false;

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
    elements.content = $("locationResultContent");
    elements.streamingBanner = $("locationStreamingBanner");
    elements.streamingStatus = $("locationStreamingStatus");
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
    elements.message.textContent = text || (type === "error"
      ? localize("Hãy thử lại sau.", "Please try again later.")
      : "");
    elements.message.classList.toggle("is-error", type === "error");
    elements.message.classList.toggle("is-success", type === "success");
  }

  function setResultState(state) {
    const nextState = ["empty", "loading", "content"].includes(state) ? state : "empty";
    if (elements.resultPanel) elements.resultPanel.dataset.resultState = nextState;
    elements.empty.hidden = nextState !== "empty";
    elements.loading.hidden = nextState !== "loading";
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

  function setStreamingText(target, value, fallback) {
    if (!target) return;
    const text = String(value || "").trim();
    target.textContent = text || fallback || "";
    target.classList.toggle("is-streaming-placeholder", !text);
  }

  function beginStreamingAnalysis() {
    resetResultCards();
    elements.resultPanel?.classList.add("is-streaming");
    if (elements.streamingBanner) elements.streamingBanner.hidden = false;
    if (elements.analyzeAgainButton) elements.analyzeAgainButton.hidden = true;

    elements.resultLabel.textContent = localize("Đang nhận diện", "Identifying");
    const badge = $("locationConfidenceBadge");
    badge.className = "confidence-badge is-analyzing";
    badge.textContent = localize("Đang phân tích", "Analyzing");
    setStreamingText(
      $("locationResultTitle"),
      "",
      localize("Đang xác định địa danh hoặc món ăn...", "Identifying the landmark or food...")
    );
    setStreamingText(
      $("locationResultSummary"),
      "",
      localize("Thông tin sẽ được điền ngay khi AI nhận diện được.", "Information will appear as soon as AI identifies it.")
    );

    elements.detailCard.hidden = false;
    renderList(
      $("locationImageDescription"),
      [localize("Đang phân tích các chi tiết trong ảnh...", "Analyzing visual details in the image...")]
    );
    $("locationImageDescription")?.classList.add("is-streaming-placeholder");
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
    const hasType = fields.content_type?.found && contentType;

    if (hasType) {
      if (contentType === "landmark") elements.resultLabel.textContent = localize("Địa danh", "Landmark");
      else if (contentType === "food") elements.resultLabel.textContent = localize("Ẩm thực", "Food");
      else elements.resultLabel.textContent = localize("Kết quả nhận diện", "Recognition result");
    }

    if (fields.title?.found) {
      setStreamingText(
        $("locationResultTitle"),
        data.title,
        localize("Đang xác định tên...", "Identifying the name...")
      );
    }

    if (fields.summary?.found) {
      setStreamingText(
        $("locationResultSummary"),
        data.summary,
        localize("Đang tổng hợp kết quả...", "Summarizing the result...")
      );
    }

    if (fields.confidence_score?.complete || fields.confidence?.complete) {
      renderConfidence(data);
    }

    const locationText = buildLocationText(data);
    if (locationText) {
      $("locationProvinceText").textContent = locationText;
      elements.locationLine.hidden = false;
    }

    if (contentType === "landmark") {
      const landmarks = cleanList(data.landmarks);
      const fallback = String(data.landmark || data.title || "").trim();
      renderList(
        $("locationLandmarkResult"),
        landmarks.length ? landmarks : [fallback || localize("Đang nhận diện địa danh...", "Identifying the landmark...")]
      );
      $("locationLandmarkResult")?.classList.toggle("is-streaming-placeholder", !landmarks.length && !fallback);
      elements.landmarkCard.hidden = false;
      elements.foodCard.hidden = true;
    } else if (contentType === "food") {
      const foods = cleanList(data.foods);
      const fallback = String(data.title || "").trim();
      renderList(
        $("locationFoodResult"),
        foods.length ? foods : [fallback || localize("Đang nhận diện món ăn...", "Identifying the food...")]
      );
      $("locationFoodResult")?.classList.toggle("is-streaming-placeholder", !foods.length && !fallback);
      elements.foodCard.hidden = false;
      elements.landmarkCard.hidden = true;
    }

    if (fields.image_description?.found) {
      renderList(
        $("locationImageDescription"),
        [data.image_description || localize("Đang phân tích mô tả...", "Analyzing the description...")]
      );
      $("locationImageDescription")?.classList.toggle("is-streaming-placeholder", !data.image_description);
    }

    if (fields.observations?.found && data.observations.length) {
      renderList($("locationObservationResult"), data.observations);
      elements.observationBlock.hidden = false;
    }

    if (fields.identification_basis?.found && data.identification_basis.length) {
      renderList($("locationEvidenceResult"), data.identification_basis);
      elements.evidenceBlock.hidden = false;
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
    elements.analyzeButton.disabled = value || !selectedImageData;
    elements.analyzeButton.querySelector("span:last-child").textContent = value
      ? localize("Đang phân tích...", "Analyzing...")
      : localize("Phân tích bằng AI", "Analyze with AI");
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

    setResultState("content");
  }

  async function analyzeImage() {
    if (!selectedImageData || isAnalyzing) return;

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

    setAnalyzing(true);
    setMessage("");
    resetStreamingPreview();
    beginStreamingAnalysis();

    try {
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
        })
      });

      if (!response.ok) {
        const errorPayload = await response.json().catch(() => ({}));
        throw new Error(errorPayload.message || localize("Không thể bắt đầu phân tích ảnh.", "Unable to start image analysis."));
      }

      let streamedReply = "";
      let completedReply = "";
      let streamError = "";

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
          streamError = String(event.message || localize("Phân tích ảnh thất bại.", "Image analysis failed."));
        }
      });

      if (streamError) throw new Error(streamError);

      const reply = (completedReply || streamedReply).trim();
      const parsed = parseAiJson(reply);
      if (!parsed) {
        throw new Error(localize("AI trả về kết quả không đúng định dạng.", "AI returned an invalid result format."));
      }

      renderAnalysis(parsed);
      setMessage(localize("Phân tích hoàn tất.", "Analysis completed."), "success");
    } catch (error) {
      elements.resultPanel?.classList.remove("is-streaming");
      if (elements.streamingBanner) elements.streamingBanner.hidden = true;
      setResultState("empty");
      setMessage(error?.message || "", "error");
    } finally {
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
    window.addEventListener("beforeunload", clearPreviewUrl);
  }

  document.addEventListener("DOMContentLoaded", function () {
    initializeElements();
    bindEvents();
    clearSelection();
  });
})();
