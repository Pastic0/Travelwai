const trackedProvinceDetailViews = new Set();

function trackProvinceDetailView(provinceName, source = 'province-detail-page') {
    const name = (provinceName || '').toString().trim();
    if (!name) return;
    const key = `${source}|${name.toLowerCase()}`;
    if (trackedProvinceDetailViews.has(key)) return;
    trackedProvinceDetailViews.add(key);

    fetch('/api/analytics/province-view', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ provinceName: name, source }),
        credentials: 'same-origin',
        keepalive: true
    }).catch(() => {});
}

document.addEventListener('DOMContentLoaded', function() {

    document.querySelectorAll('a[href^="#"]').forEach(anchor => {
        anchor.addEventListener('click', function(e) {
            e.preventDefault();
            document.querySelector(this.getAttribute('href')).scrollIntoView({
                behavior: 'smooth'
            });
        });
    });

    const provinceDetail = document.querySelector('.province-detail');
    const serverRendered = provinceDetail && provinceDetail.dataset.serverRendered === 'true';

    const urlParams = new URLSearchParams(window.location.search);
    const provinceId = urlParams.get('id') || urlParams.get('province') || urlParams.get('name');
    const renderedProvinceName = document.querySelector('.province-name')?.textContent?.trim();

    if (serverRendered && renderedProvinceName && typeof window.getStaticProvinceInfoFromLocal34 === 'function') {
        trackProvinceDetailView(renderedProvinceName, 'province-detail-page');
        const localInfo = window.getStaticProvinceInfoFromLocal34(renderedProvinceName);
        renderProvinceStaticDetailSections(localInfo);
    }

    if (!serverRendered && provinceId) {
        fetchProvinceData(provinceId);
    } else if (!serverRendered) {
        console.error('Không có mã tỉnh/thành trong URL');
        document.querySelector('.province-name').textContent = 'Không tìm thấy tỉnh/thành';
        (document.querySelector('.province-detail-lead') || document.querySelector('.province-description')).textContent = 'Vui lòng chọn tỉnh/thành hợp lệ';
    }

    document.getElementById("backButton").addEventListener("click", function () {
        window.location.href = "/provinces";
    });
const thumbnails = document.querySelectorAll('.thumbnail');
    thumbnails.forEach(thumb => {
        thumb.addEventListener('mouseenter', function() {
            this.style.zIndex = '10';
        });

        thumb.addEventListener('mouseleave', function() {
            this.style.zIndex = '1';
        });
    });
});

function stripDateText(text) { return text || ""; }

function getMergedProvinceDetailText(culture, keys, stripDates = false) {
    const source = culture || {};
    const list = Array.isArray(keys) ? keys : [keys];
    const seen = new Set();
    return list
        .map((key) => {
            const raw = (source[key] || '').toString().trim();
            return stripDates ? stripDateText(raw) : raw;
        })
        .filter(Boolean)
        .filter((value) => {
            const normalized = value.toLowerCase().replace(/\s+/g, ' ');
            if (seen.has(normalized)) return false;
            seen.add(normalized);
            return true;
        })
        .join('; ');
}

function fetchProvinceData(provinceId) {
    const localInfo = typeof window.getStaticProvinceInfoFromLocal34 === 'function'
        ? window.getStaticProvinceInfoFromLocal34(provinceId)
        : (typeof window.getLocalProvinceInfo === 'function' ? window.getLocalProvinceInfo(provinceId) : null);

    showLoading(false);
    displayProvinceData(localInfo || {
        name: provinceId,
        province_name: provinceId,
        area: 'Việt Nam',
        region: 'Chưa phân loại',
        description: `Khám phá văn hoá, lịch sử, lễ hội, làng nghề truyền thống, nhân vật lịch sử và địa danh nổi tiếng ở ${provinceId}.`,
        culture: {},
        current_festivals: [],
        current_festival_summary: 'Chưa có lễ hội tiêu biểu đang diễn ra',
        destinations: []
    });
}

function showLoading(isLoading) {
    const elements = [
        document.querySelector('.province-name'),
        document.querySelector('.province-detail-lead') || document.querySelector('.province-description'),
        document.querySelector('.main-image .image-placeholder'),
        document.querySelector('.main-image .image-name')
    ];

    if (isLoading) {
        elements.forEach(el => {
            if (el) {
                el.classList.add('loading');
                el.dataset.originalText = el.textContent;
                el.textContent = 'Đang tải...';
            }
        });
    } else {
        elements.forEach(el => {
            if (el && el.classList.contains('loading')) {
                el.classList.remove('loading');

            }
        });
    }
}

function escapeDetailHtml(text) {
    const div = document.createElement('div');
    div.textContent = text === null || typeof text === 'undefined' ? '' : String(text);
    return div.innerHTML;
}

function getProvinceFestivalNamesForDetail(province) {
    const provinceName = province?.province_name || province?.name || '';
    if (provinceName && typeof window.getFestivalNamesText34 === 'function') {
        return window.getFestivalNamesText34(provinceName);
    }
    return '';
}

function renderProvinceStaticDetailSections(province) {
    if (!province) return;

    const contentBox = document.querySelector('.province-detail .content-box');
    if (!contentBox) return;

    const oldBlock = contentBox.querySelector('.province-static-detail-sections');
    if (oldBlock) oldBlock.remove();

    const culture = province.culture || {};
    const provinceName = province?.province_name || province?.name || '';
    const currentFestivalText = provinceName && typeof window.getCurrentFestivalNamesText34 === 'function'
        ? window.getCurrentFestivalNamesText34(provinceName)
        : '';
    const festivalNamesText = getProvinceFestivalNamesForDetail(province);
    const rows = [
        ['Lễ hội đang diễn ra', currentFestivalText],
        ['Địa danh nổi tiếng và câu chuyện di tích', getMergedProvinceDetailText(culture, ['dia_danh_noi_tieng', 'cau_chuyen_di_tich'])],
        ['Lễ hội dân tộc và địa phương', festivalNamesText],
        ['Ngành nghề truyền thống', getMergedProvinceDetailText(culture, ['nganh_nghe_truyen_thong', 'nghe_truyen_thong_mai_mot'])],
        ['Nhân vật lịch sử', culture.nhan_vat_lich_su],
        ['Truyền thuyết địa phương', culture.truyen_thuyet_dia_phuong]
    ].filter(([, value]) => value && String(value).trim());

    if (!rows.length) return;

    const block = document.createElement('div');
    block.className = 'province-static-detail-sections';
    block.innerHTML = rows.map(([label, value]) => `
        <div class="province-static-detail-card">
            <strong>${escapeDetailHtml(label)}</strong>
            <p>${escapeDetailHtml(value)}</p>
        </div>
    `).join('');

    const actions = contentBox.querySelector('.province-detail-actions');
    if (actions) {
        contentBox.insertBefore(block, actions);
    } else {
        contentBox.appendChild(block);
    }
}

function displayProvinceData(province) {
    const provinceName = province.province_name || province.name || 'Tỉnh/thành';
    trackProvinceDetailView(provinceName, 'province-detail-page');
    fadeInElement(document.querySelector('.province-name'), provinceName);
    fadeInElement(document.querySelector('.province-detail-lead') || document.querySelector('.province-description'), province.description || 'Thông tin đang được cập nhật.');
    renderProvinceStaticDetailSections(province);

    const mainImage = document.querySelector('.main-image');

    if (province.mainImage && province.mainImage.url) {
        const img = new Image();
        img.src = province.mainImage.url;
        img.alt = province.mainImage.name;
        img.onload = function() {
            fadeInElement(mainImage.querySelector('.image-placeholder'), '');
            mainImage.querySelector('.image-placeholder').innerHTML = '';
            mainImage.querySelector('.image-placeholder').appendChild(img);
            fadeInElement(mainImage.querySelector('.image-name'), province.mainImage.name);
        };
    } else {
        fadeInElement(mainImage.querySelector('.image-placeholder'), 'Chưa có hình ảnh');
        fadeInElement(mainImage.querySelector('.image-name'), 'Mặc định');
    }

    const thumbnailContainers = document.querySelectorAll('.thumbnail');

    const provinceImages = Array.isArray(province.images) ? province.images : [];
    provinceImages.forEach((image, index) => {
        if (index < thumbnailContainers.length) {
            const thumb = thumbnailContainers[index];
            const placeholder = thumb.querySelector('.image-placeholder');
            const nameEl = thumb.querySelector('.image-name');

            setTimeout(() => {
                const img = new Image();
                img.src = image.url;
                img.alt = image.name;
                img.onload = function() {
                    fadeInElement(placeholder, '');
                    placeholder.innerHTML = '';
                    placeholder.appendChild(img);
                    fadeInElement(nameEl, image.name);
                };

                thumb.addEventListener('click', function() {
                    updateMainImage(image);
                });
            }, index * 200);
        }
    });
}

function updateMainImage(image) {
    const mainImage = document.querySelector('.main-image');
    const placeholder = mainImage.querySelector('.image-placeholder');
    const nameEl = mainImage.querySelector('.image-name');

    placeholder.style.opacity = 0;
    nameEl.style.opacity = 0;

    setTimeout(() => {

        placeholder.innerHTML = '';
        const img = new Image();
        img.src = image.url;
        img.alt = image.name;
        placeholder.appendChild(img);
        nameEl.textContent = image.name;

        placeholder.style.opacity = 1;
        nameEl.style.opacity = 1;
    }, 300);
}

function fadeInElement(element, text) {
    if (!element) return;

    element.style.opacity = 0;

    setTimeout(() => {
        if (text !== undefined) {
            element.textContent = text;
        }
        element.style.opacity = 1;
    }, 300);
}
