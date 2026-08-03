(function () {
  "use strict";

  if (window.__travelwAIPointerScrollReady) return;
  window.__travelwAIPointerScrollReady = true;

  const nativeWheelSelector = [
    "select",
    "option",
    "input[type='number']",
    "iframe",
    "video",
    "audio",
    "canvas",
    "[data-wheel-native]",
    "[data-wheel-zoom]",
    ".map-container svg",
    "#landingVietnamMap svg",
    ".culture-board-map svg"
  ].join(",");

  function normalizeDelta(value, mode, viewportSize) {
    if (!Number.isFinite(value) || value === 0) return 0;
    if (mode === 1) return value * 34;
    if (mode === 2) return value * Math.max(120, viewportSize * 0.82);
    return value;
  }

  function allowsScrolling(element, axis) {
    const style = window.getComputedStyle(element);
    const overflow = axis === "x" ? style.overflowX : style.overflowY;
    return overflow === "auto" || overflow === "scroll" || overflow === "overlay";
  }

  function hasScrollableContent(element, axis) {
    if (axis === "x") return element.scrollWidth > element.clientWidth + 1;
    return element.scrollHeight > element.clientHeight + 1;
  }

  function findPointerScroller(target, axis) {
    let element = target instanceof Element ? target : target?.parentElement;

    while (element && element !== document.body && element !== document.documentElement) {
      if (element.hasAttribute("data-wheel-scroll-page")) return null;
      if (allowsScrolling(element, axis) && hasScrollableContent(element, axis)) return element;
      element = element.parentElement;
    }

    return null;
  }

  function handleWheel(event) {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey) return;

    const target = event.target instanceof Element ? event.target : event.target?.parentElement;
    if (!target || target.closest(nativeWheelSelector)) return;

    const deltaX = normalizeDelta(event.deltaX, event.deltaMode, window.innerWidth);
    const deltaY = normalizeDelta(event.deltaY, event.deltaMode, window.innerHeight);
    if (!deltaX && !deltaY) return;

    const useHorizontal = Math.abs(deltaX) > Math.abs(deltaY);
    const axis = useHorizontal ? "x" : "y";
    const delta = useHorizontal ? deltaX : deltaY;
    const scroller = findPointerScroller(target, axis);
    if (!scroller) return;


    event.preventDefault();
    event.stopPropagation();

    if (axis === "x") {
      scroller.scrollLeft += delta;
    } else {
      scroller.scrollTop += delta;
    }
  }

  document.addEventListener("wheel", handleWheel, {
    capture: true,
    passive: false
  });
})();
