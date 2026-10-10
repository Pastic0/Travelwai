/* Thanh điều hướng nửa vòng tròn (Landing)
   - Chỉ hiện 3 mục: trước / hiện tại / kế tiếp
   - Chuyển sang mục kế tiếp thì bánh xoay THEO CHIỀU KIM ĐỒNG HỒ
   - Trạng thái "đang ở mục nào" lấy từ class .active mà landing_page.js đã gắn */
(function () {
  "use strict";
  const rail = document.querySelector(".landing-section-rail");
  if (!rail) return;
  const links = Array.from(rail.querySelectorAll("a[href^='#']"));
  const n = links.length;
  if (n < 2) return;

  const STEP = 34; // độ lệch góc giữa 2 mục liền kề
  let lastKey = "";

  function radius() {
    const v = parseFloat(getComputedStyle(rail).getPropertyValue("--tw-rail-r"));
    return Number.isFinite(v) && v > 0 ? v : 120;
  }

  function layout() {
    let active = links.findIndex(function (l) { return l.classList.contains("active"); });
    if (active < 0) active = 0;
    const R = radius();
    const key = active + ":" + R;
    if (key === lastKey) return;
    lastKey = key;

    // cửa sổ 3 mục, không quay vòng: đầu/cuối danh sách thì mục active nằm ở đầu/cuối cung
    const c = Math.min(Math.max(active, 1), n - 2);

    links.forEach(function (link, i) {
      const theta = (c - i) * STEP; // dương = phía trên, âm = phía dưới
      const visible = Math.abs(c - i) <= 1;
      link.style.transform =
        "rotate(" + theta + "deg) translateX(" + (-R) + "px) rotate(" + (-theta) + "deg) translate(-50%, -50%)";
      link.classList.toggle("is-visible", visible);
      link.classList.toggle("is-current", i === active);
      link.setAttribute("aria-hidden", visible ? "false" : "true");
      link.tabIndex = visible ? 0 : -1;
    });
  }

  layout();
  requestAnimationFrame(function () { rail.classList.add("is-ready"); });

  new MutationObserver(layout).observe(rail, {
    subtree: true,
    attributes: true,
    attributeFilter: ["class"]
  });
  window.addEventListener("resize", layout, { passive: true });
})();
