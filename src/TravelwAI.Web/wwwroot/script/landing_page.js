document.addEventListener("DOMContentLoaded", function () {
  const backgroundSlider = document.querySelector(".landing-background-slider");
  if (backgroundSlider) {
    backgroundSlider.style.removeProperty("background-image");
  }

  const navLinks = Array.from(document.querySelectorAll('.landing-nav a[href^="#"], .landing-section-rail a[href^="#"], .landing-footer a[href^="#"]'));
  const sections = Array.from(new Set(navLinks
    .map(function (link) {
      return document.querySelector(link.getAttribute("href"));
    })
    .filter(Boolean)));
  let activeSectionIndex = 0;
  let activeSectionId = sections[0]?.id || "";
  let sectionSlideTimer = null;

  function getLandingHeaderHeight() {
    const header = document.querySelector(".landing-header");
    return header ? header.offsetHeight : 0;
  }

  function syncLandingHeaderHeight() {
    document.documentElement.style.setProperty("--landing-header-h", getLandingHeaderHeight() + "px");
  }
  syncLandingHeaderHeight();
  window.addEventListener("resize", syncLandingHeaderHeight, { passive: true });

  // Cuộn sao cho card của phần được chọn nằm giữa vùng nhìn thấy (dưới header), cả dọc lẫn ngang
  function scrollTargetToSection(target) {
    const header = getLandingHeaderHeight();
    const rect = target.getBoundingClientRect();
    const visibleHeight = window.innerHeight - header;
    const targetCenter = rect.top + window.scrollY + rect.height / 2;
    let nextTop = targetCenter - header - visibleHeight / 2;
    // card cao hơn vùng nhìn thấy thì đưa đầu card lên ngay dưới header
    if (rect.height >= visibleHeight) nextTop = rect.top + window.scrollY - header;
    const maxTop = document.documentElement.scrollHeight - window.innerHeight;
    nextTop = Math.min(Math.max(0, nextTop), Math.max(0, maxTop));
    const left = rect.left + window.scrollX + rect.width / 2 - window.innerWidth / 2;
    window.scrollTo({ top: nextTop, left: Math.max(0, left), behavior: "smooth" });
  }

  function getSectionDirection(target) {
    const nextIndex = Math.max(0, sections.indexOf(target));
    if (nextIndex === activeSectionIndex) return "next";
    return nextIndex > activeSectionIndex ? "next" : "prev";
  }

  function playLandingSectionSlide(target, direction) {
    if (!target) return;

    const targetIndex = sections.indexOf(target);
    if (targetIndex >= 0) {
      activeSectionIndex = targetIndex;
      activeSectionId = target.id;
    }

    const slideClass = direction === "prev" ? "section-slide-from-left" : "section-slide-from-right";
    document.body.classList.remove("landing-slide-next", "landing-slide-prev");
    document.body.classList.add(direction === "prev" ? "landing-slide-prev" : "landing-slide-next");

    target.classList.add("is-visible");
    target.classList.remove("section-slide-run", "section-slide-from-left", "section-slide-from-right", "section-nav-focus");
    void target.offsetWidth;
    target.classList.add("section-slide-run", slideClass, "section-nav-focus");

    window.clearTimeout(sectionSlideTimer);
    sectionSlideTimer = window.setTimeout(function () {
      target.classList.remove("section-slide-run", "section-slide-from-left", "section-slide-from-right", "section-nav-focus");
    }, 920);
  }

  document.querySelectorAll('a[href^="#"]').forEach(function (link) {
    link.addEventListener("click", function (event) {
      const target = document.querySelector(link.getAttribute("href"));
      if (!target) return;

      event.preventDefault();
      playLandingSectionSlide(target, getSectionDirection(target));
      scrollTargetToSection(target);
    });
  });

  function setActiveTab() {
    if (!navLinks.length || !sections.length) return;

    let activeId = sections[0].id;
    const point = window.scrollY + window.innerHeight * 0.5;

    sections.forEach(function (section) {
      const top = section.offsetTop;
      const bottom = top + section.offsetHeight;
      if (point >= top && point <= bottom) activeId = section.id;
      if (top <= point) activeId = section.id;
    });

    const nextSection = sections.find(function (section) {
      return section.id === activeId;
    });

    if (nextSection && activeId !== activeSectionId) {
      playLandingSectionSlide(nextSection, getSectionDirection(nextSection));
    }

    navLinks.forEach(function (link) {
      link.classList.toggle("active", link.getAttribute("href") === `#${activeId}`);
    });
  }

  function initLandingVietnamMap() {
    const mapContainer = document.getElementById("landingVietnamMap");
    const loading = mapContainer?.querySelector(".culture-map-loading");
    if (!mapContainer) return;

    function parseViewBox(svg) {
      const parts = (svg.getAttribute("viewBox") || "0 0 800 800")
        .trim()
        .split(/[\s,]+/)
        .map(Number);
      if (parts.length !== 4 || parts.some(Number.isNaN)) return [0, 0, 800, 800];
      return parts;
    }

    function clamp(value, min, max) {
      return Math.min(Math.max(value, min), max);
    }

    function clampViewBox(viewBox, original) {
      const minW = original[2] / 2;
      const minH = original[3] / 2;
      const width = clamp(viewBox[2], minW, original[2]);
      const height = clamp(viewBox[3], minH, original[3]);
      const maxX = original[0] + original[2] - width;
      const maxY = original[1] + original[3] - height;
      const x = clamp(viewBox[0], original[0], maxX);
      const y = clamp(viewBox[1], original[1], maxY);
      return [x, y, width, height];
    }

    function setSvgViewBox(svg, viewBox) {
      const original = svg.__landingOriginalViewBox || parseViewBox(svg);
      const next = clampViewBox(viewBox, original);
      svg.__landingCurrentViewBox = next;
      svg.setAttribute("viewBox", next.join(" "));
      const isZoomed = Math.abs(next[2] - original[2]) > 0.5 || Math.abs(next[3] - original[3]) > 0.5;
      svg.classList.toggle("is-zoomed", isZoomed);
      return next;
    }

    function resetSvgViewBox(svg) {
      const original = svg.__landingOriginalViewBox || parseViewBox(svg);
      setSvgViewBox(svg, original);
      const label = svg.querySelector("#landingMapProvinceLabel");
      if (label) label.remove();
      svg.querySelectorAll(".province.selected").forEach(function (item) {
        item.classList.remove("selected");
      });
    }

    function getProvinceName(province) {
      return province?.getAttribute("data-province-name") || province?.getAttribute("title") || "Tỉnh/thành Việt Nam";
    }

    function getProvinceCenter(province) {
      const zoomX = Number(province.getAttribute("data-zoom-x"));
      const zoomY = Number(province.getAttribute("data-zoom-y"));
      const zoomW = Number(province.getAttribute("data-zoom-width"));
      const zoomH = Number(province.getAttribute("data-zoom-height"));

      if (![zoomX, zoomY, zoomW, zoomH].some(Number.isNaN) && zoomW > 0 && zoomH > 0) {
        return { x: zoomX + zoomW / 2, y: zoomY + zoomH / 2, width: zoomW, height: zoomH };
      }

      const box = province.getBBox();
      return { x: box.x + box.width / 2, y: box.y + box.height / 2, width: box.width, height: box.height };
    }

    function zoomProvinceTo2x(svg, province) {
      const original = svg.__landingOriginalViewBox || parseViewBox(svg);
      if (!province || typeof province.getBBox !== "function") return;

      try {
        const center = getProvinceCenter(province);
        const nextW = original[2] / 2;
        const nextH = original[3] / 2;
        setSvgViewBox(svg, [center.x - nextW / 2, center.y - nextH / 2, nextW, nextH]);
      } catch {
        const current = svg.__landingCurrentViewBox || parseViewBox(svg);
        setSvgViewBox(svg, [current[0], current[1], original[2] / 2, original[3] / 2]);
      }
    }

    function showProvinceLabel(svg, province) {
      const provinceName = getProvinceName(province);
      let label = svg.querySelector("#landingMapProvinceLabel");
      if (!label) {
        label = document.createElementNS("http://www.w3.org/2000/svg", "text");
        label.setAttribute("id", "landingMapProvinceLabel");
        label.setAttribute("class", "landing-map-province-label");
        label.setAttribute("text-anchor", "middle");
        label.setAttribute("dominant-baseline", "middle");
        label.setAttribute("pointer-events", "none");
        svg.appendChild(label);
      }

      try {
        const center = getProvinceCenter(province);
        label.textContent = provinceName;
        label.setAttribute("x", String(center.x));
        label.setAttribute("y", String(center.y - Math.max(18, Math.min(34, center.height * 0.55))));
        label.style.display = "block";
      } catch {
        label.textContent = provinceName;
        label.setAttribute("x", "400");
        label.setAttribute("y", "400");
        label.style.display = "block";
      }
    }

    function getPointerRatio(svg, event) {
      const rect = svg.getBoundingClientRect();
      const px = clamp((event.clientX - rect.left) / rect.width, 0, 1);
      const py = clamp((event.clientY - rect.top) / rect.height, 0, 1);
      return { px, py, rect };
    }

    function zoomSvgAtPointer(svg, event) {
      event.preventDefault();
      const original = svg.__landingOriginalViewBox || parseViewBox(svg);
      const current = svg.__landingCurrentViewBox || parseViewBox(svg);
      const { px, py } = getPointerRatio(svg, event);
      const cursorX = current[0] + px * current[2];
      const cursorY = current[1] + py * current[3];
      const zoomStep = event.deltaY < 0 ? 1 / 1.18 : 1.18;
      const minW = original[2] / 2;
      const maxW = original[2];
      const nextW = clamp(current[2] * zoomStep, minW, maxW);
      const nextH = nextW * (original[3] / original[2]);
      const nextX = cursorX - px * nextW;
      const nextY = cursorY - py * nextH;
      setSvgViewBox(svg, [nextX, nextY, nextW, nextH]);
    }

    function selectProvince(svg, province) {
      if (!province) return;
      svg.querySelectorAll(".province.selected").forEach(function (item) {
        item.classList.remove("selected");
      });
      province.classList.add("selected");
      showProvinceLabel(svg, province);
      zoomProvinceTo2x(svg, province);
    }

    function enablePanZoom(svg) {
      let isPanning = false;
      let startX = 0;
      let startY = 0;
      let startViewBox = null;
      let moved = false;
      let pressedProvince = null;

      // Cuộn thường khi bản đồ ở trạng thái gốc -> trang vẫn cuộn được.
      // Ctrl/⌘ + lăn (hoặc chụm 2 ngón trên touchpad) luôn zoom bản đồ.
      svg.addEventListener("wheel", function (event) {
        const canZoom = event.ctrlKey || event.metaKey || svg.classList.contains("is-zoomed");
        if (!canZoom) return;
        zoomSvgAtPointer(svg, event);
      }, { passive: false });

      // ---- Cảm ứng: 1 ngón kéo, 2 ngón chụm để zoom ----
      const touchPoints = new Map();
      let touchPan = null;
      let pinch = null;

      function touchDistance() {
        const pts = Array.from(touchPoints.values());
        return Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y) || 1;
      }

      function touchCenter() {
        const pts = Array.from(touchPoints.values());
        return { x: (pts[0].x + pts[1].x) / 2, y: (pts[0].y + pts[1].y) / 2 };
      }

      function beginTouchGesture() {
        const viewBox = (svg.__landingCurrentViewBox || parseViewBox(svg)).slice();
        if (touchPoints.size === 2) {
          const rect = svg.getBoundingClientRect();
          const c = touchCenter();
          pinch = {
            dist: touchDistance(),
            viewBox: viewBox,
            px: clamp((c.x - rect.left) / rect.width, 0, 1),
            py: clamp((c.y - rect.top) / rect.height, 0, 1)
          };
          touchPan = null;
          moved = true;
        } else if (touchPoints.size === 1) {
          const pt = Array.from(touchPoints.values())[0];
          touchPan = { x: pt.x, y: pt.y, viewBox: viewBox };
          pinch = null;
          moved = false;
        } else {
          touchPan = null;
          pinch = null;
        }
      }

      function handleTouchDown(event) {
        touchPoints.set(event.pointerId, { x: event.clientX, y: event.clientY });
        beginTouchGesture();
      }

      function handleTouchMove(event) {
        if (!touchPoints.has(event.pointerId)) return;
        touchPoints.set(event.pointerId, { x: event.clientX, y: event.clientY });
        const original = svg.__landingOriginalViewBox || parseViewBox(svg);
        const rect = svg.getBoundingClientRect();

        if (pinch && touchPoints.size === 2) {
          const start = pinch.viewBox;
          const c = touchCenter();
          const px = clamp((c.x - rect.left) / rect.width, 0, 1);
          const py = clamp((c.y - rect.top) / rect.height, 0, 1);
          const nextW = clamp(start[2] * (pinch.dist / touchDistance()), original[2] / 2, original[2]);
          const nextH = nextW * (original[3] / original[2]);
          const anchorX = start[0] + pinch.px * start[2];
          const anchorY = start[1] + pinch.py * start[3];
          setSvgViewBox(svg, [anchorX - px * nextW, anchorY - py * nextH, nextW, nextH]);
          moved = true;
          return;
        }

        if (touchPan && touchPoints.size === 1) {
          const dx = event.clientX - touchPan.x;
          const dy = event.clientY - touchPan.y;
          if (Math.abs(dx) > 8 || Math.abs(dy) > 8) moved = true;
          if (!moved) return;
          const start = touchPan.viewBox;
          setSvgViewBox(svg, [
            start[0] - (dx / rect.width) * start[2],
            start[1] - (dy / rect.height) * start[3],
            start[2],
            start[3]
          ]);
        }
      }

      function handleTouchEnd(event) {
        if (!touchPoints.has(event.pointerId)) return;
        touchPoints.delete(event.pointerId);
        beginTouchGesture();
        window.setTimeout(function () {
          if (!touchPoints.size) moved = false;
        }, 80);
      }

      svg.addEventListener("pointermove", function (event) {
        if (event.pointerType === "touch") handleTouchMove(event);
      });
      svg.addEventListener("pointerup", function (event) {
        if (event.pointerType === "touch") handleTouchEnd(event);
      });
      svg.addEventListener("pointercancel", function (event) {
        if (event.pointerType === "touch") handleTouchEnd(event);
      });

      svg.addEventListener("pointerdown", function (event) {
        if (event.pointerType === "touch") {
          handleTouchDown(event);
          return;
        }
        if (event.button !== 0) return;
        isPanning = true;
        moved = false;
        startX = event.clientX;
        startY = event.clientY;
        pressedProvince = event.target.closest?.(".province, [data-province-name]") || null;
        startViewBox = (svg.__landingCurrentViewBox || parseViewBox(svg)).slice();
        mapContainer.classList.add("is-panning");
        svg.setPointerCapture?.(event.pointerId);
      });

      svg.addEventListener("pointermove", function (event) {
        if (!isPanning || !startViewBox) return;
        const rect = svg.getBoundingClientRect();
        const dx = event.clientX - startX;
        const dy = event.clientY - startY;
        if (Math.abs(dx) > 5 || Math.abs(dy) > 5) moved = true;
        const moveX = -(dx / rect.width) * startViewBox[2];
        const moveY = -(dy / rect.height) * startViewBox[3];
        setSvgViewBox(svg, [startViewBox[0] + moveX, startViewBox[1] + moveY, startViewBox[2], startViewBox[3]]);
      });

      function stopPan(event) {
        if (!isPanning) return;
        isPanning = false;
        startViewBox = null;
        mapContainer.classList.remove("is-panning");
        svg.releasePointerCapture?.(event.pointerId);
        window.setTimeout(function () {
          moved = false;
        }, 60);
      }

      svg.addEventListener("pointerup", stopPan);
      svg.addEventListener("pointercancel", stopPan);
      svg.addEventListener("pointerleave", function (event) {
        if (isPanning) stopPan(event);
      });

      svg.addEventListener("click", function (event) {
        const province = event.target.closest?.(".province, [data-province-name]") || pressedProvince;
        pressedProvince = null;
        if (!province || !svg.contains(province) || province.classList.contains("province-islet")) return;
        event.preventDefault();
        event.stopPropagation();
        if (moved) return;
        selectProvince(svg, province);
      }, true);

      svg.addEventListener("dblclick", function (event) {
        event.preventDefault();
        resetSvgViewBox(svg);
      });
    }

    fetch("/vietnam.svg?v=2026-07-01-clean-v1")
      .then(function (response) {
        if (!response.ok) throw new Error("Không tải được bản đồ Việt Nam");
        return response.text();
      })
      .then(function (svgText) {
        const wrapper = document.createElement("div");
        wrapper.innerHTML = svgText.trim();
        const svg = wrapper.querySelector("svg");
        if (!svg) throw new Error("File bản đồ không đúng định dạng SVG");

        svg.removeAttribute("width");
        svg.removeAttribute("height");
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
        svg.classList.add("landing-vietnam-svg");
        svg.setAttribute("aria-label", "Bản đồ Việt Nam, kéo để di chuyển, Ctrl + lăn chuột hoặc chụm hai ngón để phóng to thu nhỏ");
        svg.__landingOriginalViewBox = parseViewBox(svg);
        svg.__landingCurrentViewBox = svg.__landingOriginalViewBox.slice();

        if (loading) loading.remove();
        mapContainer.appendChild(svg);

        const provinces = Array.from(svg.querySelectorAll(".province, [data-province-name]")).filter(function (province) {
          return !province.classList.contains("province-islet");
        });

        provinces.forEach(function (province) {
          const provinceName = getProvinceName(province);
          province.setAttribute("tabindex", "0");
          province.setAttribute("role", "button");
          province.setAttribute("aria-label", provinceName);

          province.addEventListener("keydown", function (event) {
            if (event.key !== "Enter" && event.key !== " ") return;
            event.preventDefault();
            selectProvince(svg, province);
          });
        });

        enablePanZoom(svg);
      })
      .catch(function () {
        if (loading) loading.textContent = "Chưa tải được bản đồ";
      });
  }

  function initLandingSectionAnimations() {
    const landingSections = Array.from(document.querySelectorAll('.landing-culture-page main > section[id]'));
    if (!landingSections.length) return;

    landingSections[0].classList.add('is-visible');

    if (!('IntersectionObserver' in window)) {
      landingSections.forEach(function (section) {
        section.classList.add('is-visible');
      });
      return;
    }

    const observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-visible');
        if (entry.target.id && entry.target.id !== activeSectionId) {
          playLandingSectionSlide(entry.target, getSectionDirection(entry.target));
        }
      });
    }, {
      threshold: 0.28,
      rootMargin: '-12% 0px -18% 0px'
    });

    landingSections.forEach(function (section) {
      observer.observe(section);
    });
  }

  function initLandingNewsletter() {
    const form = document.getElementById("landingNewsletterForm");
    if (!form) return;

    const input = document.getElementById("landingNewsletterEmail") || form.querySelector('input[type="email"]');
    const button = form.querySelector('button[type="submit"]');
    const status = document.getElementById("landingNewsletterStatus");

    function showStatus(message, isError) {
      if (!status) return;
      status.textContent = message || "";
      status.classList.toggle("is-error", Boolean(isError));
      status.classList.toggle("is-success", Boolean(message && !isError));
    }

    form.addEventListener("submit", async function (event) {
      event.preventDefault();

      const email = (input?.value || "").trim();
      if (!email) {
        showStatus("Vui lòng nhập email nhận tin.", true);
        input?.focus();
        return;
      }

      if (button) {
        button.disabled = true;
        button.dataset.originalText = button.dataset.originalText || button.textContent || "ĐĂNG KÝ NHẬN TIN";
        button.textContent = "ĐANG GỬI...";
      }
      showStatus("Đang gửi email xác nhận...", false);

      try {
        const response = await fetch("/api/newsletter/subscribe", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ email })
        });

        const result = await response.json().catch(function () { return {}; });
        if (!response.ok || result.success === false) {
          throw new Error(result.message || "Chưa gửi được email xác nhận.");
        }

        showStatus(result.message || "Đã gửi email xác nhận đăng ký nhận tin.", false);
        if (input) input.value = "";
      } catch (error) {
        showStatus(error?.message || "Chưa gửi được email xác nhận.", true);
      } finally {
        if (button) {
          button.disabled = false;
          button.textContent = button.dataset.originalText || "ĐĂNG KÝ NHẬN TIN";
        }
      }
    });
  }

  initLandingSectionAnimations();
  initLandingVietnamMap();
  initLandingNewsletter();
  setActiveTab();
  window.addEventListener("scroll", setActiveTab, { passive: true });
});
