(function () {
  "use strict";

  var UTF8_FLAG = 0x0800;
  var ZIP_STORE_METHOD = 0;
  var CRC_TABLE = createCrcTable();

  function createCrcTable() {
    var table = new Uint32Array(256);
    for (var index = 0; index < 256; index += 1) {
      var value = index;
      for (var bit = 0; bit < 8; bit += 1) {
        value = (value & 1) ? (0xedb88320 ^ (value >>> 1)) : (value >>> 1);
      }
      table[index] = value >>> 0;
    }
    return table;
  }

  function crc32(bytes) {
    var crc = 0xffffffff;
    for (var index = 0; index < bytes.length; index += 1) {
      crc = CRC_TABLE[(crc ^ bytes[index]) & 0xff] ^ (crc >>> 8);
    }
    return (crc ^ 0xffffffff) >>> 0;
  }

  function encodeUtf8(value) {
    return new TextEncoder().encode(String(value == null ? "" : value));
  }

  function concatByteArrays(parts) {
    var total = parts.reduce(function (sum, part) { return sum + part.length; }, 0);
    var output = new Uint8Array(total);
    var offset = 0;
    parts.forEach(function (part) {
      output.set(part, offset);
      offset += part.length;
    });
    return output;
  }

  function writeUint16(view, offset, value) {
    view.setUint16(offset, value & 0xffff, true);
  }

  function writeUint32(view, offset, value) {
    view.setUint32(offset, value >>> 0, true);
  }

  function getDosDateTime(date) {
    var year = Math.max(1980, date.getFullYear());
    return {
      time: ((date.getHours() & 0x1f) << 11) | ((date.getMinutes() & 0x3f) << 5) | (Math.floor(date.getSeconds() / 2) & 0x1f),
      date: (((year - 1980) & 0x7f) << 9) | (((date.getMonth() + 1) & 0x0f) << 5) | (date.getDate() & 0x1f)
    };
  }

  function createZip(files) {
    var localParts = [];
    var centralParts = [];
    var localOffset = 0;
    var now = getDosDateTime(new Date());

    files.forEach(function (file) {
      var nameBytes = encodeUtf8(file.name);
      var dataBytes = file.data instanceof Uint8Array ? file.data : encodeUtf8(file.data);
      var checksum = crc32(dataBytes);

      var localHeader = new Uint8Array(30);
      var localView = new DataView(localHeader.buffer);
      writeUint32(localView, 0, 0x04034b50);
      writeUint16(localView, 4, 20);
      writeUint16(localView, 6, UTF8_FLAG);
      writeUint16(localView, 8, ZIP_STORE_METHOD);
      writeUint16(localView, 10, now.time);
      writeUint16(localView, 12, now.date);
      writeUint32(localView, 14, checksum);
      writeUint32(localView, 18, dataBytes.length);
      writeUint32(localView, 22, dataBytes.length);
      writeUint16(localView, 26, nameBytes.length);
      writeUint16(localView, 28, 0);
      localParts.push(localHeader, nameBytes, dataBytes);

      var centralHeader = new Uint8Array(46);
      var centralView = new DataView(centralHeader.buffer);
      writeUint32(centralView, 0, 0x02014b50);
      writeUint16(centralView, 4, 20);
      writeUint16(centralView, 6, 20);
      writeUint16(centralView, 8, UTF8_FLAG);
      writeUint16(centralView, 10, ZIP_STORE_METHOD);
      writeUint16(centralView, 12, now.time);
      writeUint16(centralView, 14, now.date);
      writeUint32(centralView, 16, checksum);
      writeUint32(centralView, 20, dataBytes.length);
      writeUint32(centralView, 24, dataBytes.length);
      writeUint16(centralView, 28, nameBytes.length);
      writeUint16(centralView, 30, 0);
      writeUint16(centralView, 32, 0);
      writeUint16(centralView, 34, 0);
      writeUint16(centralView, 36, 0);
      writeUint32(centralView, 38, 0);
      writeUint32(centralView, 42, localOffset);
      centralParts.push(centralHeader, nameBytes);

      localOffset += localHeader.length + nameBytes.length + dataBytes.length;
    });

    var localData = concatByteArrays(localParts);
    var centralData = concatByteArrays(centralParts);
    var end = new Uint8Array(22);
    var endView = new DataView(end.buffer);
    writeUint32(endView, 0, 0x06054b50);
    writeUint16(endView, 4, 0);
    writeUint16(endView, 6, 0);
    writeUint16(endView, 8, files.length);
    writeUint16(endView, 10, files.length);
    writeUint32(endView, 12, centralData.length);
    writeUint32(endView, 16, localData.length);
    writeUint16(endView, 20, 0);

    return concatByteArrays([localData, centralData, end]);
  }

  function xmlEscape(value) {
    return String(value == null ? "" : value)
      .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&apos;");
  }

  function normalizeCellValue(value) {
    var text = String(value == null ? "" : value).replace(/\r\n?/g, "\n").trim();
    return text.length > 32000 ? text.slice(0, 31997) + "..." : text;
  }

  function columnName(index) {
    var value = index + 1;
    var output = "";
    while (value > 0) {
      var remainder = (value - 1) % 26;
      output = String.fromCharCode(65 + remainder) + output;
      value = Math.floor((value - 1) / 26);
    }
    return output;
  }

  function sanitizeSheetName(name, usedNames) {
    var base = String(name || "Sheet").replace(/[\\/*?:\[\]]/g, " ").trim() || "Sheet";
    base = base.slice(0, 31);
    var candidate = base;
    var suffix = 2;
    while (usedNames.has(candidate.toLowerCase())) {
      var ending = " (" + suffix + ")";
      candidate = base.slice(0, Math.max(1, 31 - ending.length)) + ending;
      suffix += 1;
    }
    usedNames.add(candidate.toLowerCase());
    return candidate;
  }

  function buildWorksheetXml(sheet) {
    var rows = Array.isArray(sheet.rows) ? sheet.rows : [];
    var headerRows = new Set(Array.isArray(sheet.headerRows) ? sheet.headerRows : [1]);
    var maxColumns = rows.reduce(function (max, row) { return Math.max(max, Array.isArray(row) ? row.length : 0); }, 0);
    var widths = new Array(maxColumns).fill(10);

    rows.forEach(function (row) {
      (Array.isArray(row) ? row : []).forEach(function (value, columnIndex) {
        var longestLine = normalizeCellValue(value).split("\n").reduce(function (max, line) { return Math.max(max, line.length); }, 0);
        widths[columnIndex] = Math.max(widths[columnIndex], Math.min(50, longestLine + 2));
      });
    });

    var colsXml = maxColumns ? "<cols>" + widths.map(function (width, index) {
      return '<col min="' + (index + 1) + '" max="' + (index + 1) + '" width="' + Math.max(10, width) + '" customWidth="1"/>';
    }).join("") + "</cols>" : "";

    var rowsXml = rows.map(function (row, rowIndex) {
      var rowNumber = rowIndex + 1;
      var cells = (Array.isArray(row) ? row : []).map(function (value, columnIndex) {
        var text = normalizeCellValue(value);
        var reference = columnName(columnIndex) + rowNumber;
        var styleId = headerRows.has(rowNumber) ? 1 : 2;
        return '<c r="' + reference + '" s="' + styleId + '" t="inlineStr"><is><t xml:space="preserve">' + xmlEscape(text) + '</t></is></c>';
      }).join("");
      var height = (Array.isArray(row) ? row : []).some(function (value) { return normalizeCellValue(value).includes("\n"); }) ? ' ht="36" customHeight="1"' : "";
      return '<row r="' + rowNumber + '"' + height + '>' + cells + '</row>';
    }).join("");

    var freezeRow = Math.max(1, Number(sheet.freezeRow) || 1);
    var paneXml = rows.length ? '<sheetViews><sheetView workbookViewId="0"><pane ySplit="' + freezeRow + '" topLeftCell="A' + (freezeRow + 1) + '" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>' : '<sheetViews><sheetView workbookViewId="0"/></sheetViews>';
    var filterXml = "";
    if (sheet.filterRow && maxColumns && rows.length >= sheet.filterRow) {
      filterXml = '<autoFilter ref="A' + sheet.filterRow + ':' + columnName(maxColumns - 1) + rows.length + '"/>';
    }

    return '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
      paneXml +
      '<sheetFormatPr defaultRowHeight="18"/>' +
      colsXml +
      '<sheetData>' + rowsXml + '</sheetData>' +
      filterXml +
      '<pageMargins left="0.4" right="0.4" top="0.6" bottom="0.6" header="0.2" footer="0.2"/>' +
      '</worksheet>';
  }

  function buildWorkbookFiles(sheets) {
    var usedNames = new Set();
    var normalizedSheets = sheets.map(function (sheet) {
      return Object.assign({}, sheet, { name: sanitizeSheetName(sheet.name, usedNames) });
    });

    var sheetOverrides = normalizedSheets.map(function (_, index) {
      return '<Override PartName="/xl/worksheets/sheet' + (index + 1) + '.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>';
    }).join("");

    var contentTypes = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' +
      '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
      '<Default Extension="xml" ContentType="application/xml"/>' +
      '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' +
      '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>' +
      '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>' +
      '<Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>' +
      sheetOverrides + '</Types>';

    var rootRels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
      '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>' +
      '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>' +
      '<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>' +
      '</Relationships>';

    var workbookSheets = normalizedSheets.map(function (sheet, index) {
      return '<sheet name="' + xmlEscape(sheet.name) + '" sheetId="' + (index + 1) + '" r:id="rId' + (index + 1) + '"/>';
    }).join("");
    var workbook = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">' +
      '<bookViews><workbookView xWindow="0" yWindow="0" windowWidth="24000" windowHeight="12000"/></bookViews>' +
      '<sheets>' + workbookSheets + '</sheets>' +
      '<calcPr calcId="0"/>' +
      '</workbook>';

    var workbookRelationships = normalizedSheets.map(function (_, index) {
      return '<Relationship Id="rId' + (index + 1) + '" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet' + (index + 1) + '.xml"/>';
    }).join("") + '<Relationship Id="rId' + (normalizedSheets.length + 1) + '" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>';
    var workbookRels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + workbookRelationships + '</Relationships>';

    var styles = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
      '<fonts count="2"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/><family val="2"/></font></fonts>' +
      '<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF2563EB"/><bgColor indexed="64"/></patternFill></fill></fills>' +
      '<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FFD1D5DB"/></left><right style="thin"><color rgb="FFD1D5DB"/></right><top style="thin"><color rgb="FFD1D5DB"/></top><bottom style="thin"><color rgb="FFD1D5DB"/></bottom><diagonal/></border></borders>' +
      '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>' +
      '<cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf></cellXfs>' +
      '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>' +
      '</styleSheet>';

    var isoNow = new Date().toISOString();
    var core = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">' +
      '<dc:title>Dữ liệu Admin TravelwAI</dc:title><dc:creator>TravelwAI</dc:creator><cp:lastModifiedBy>TravelwAI</cp:lastModifiedBy>' +
      '<dcterms:created xsi:type="dcterms:W3CDTF">' + isoNow + '</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">' + isoNow + '</dcterms:modified>' +
      '</cp:coreProperties>';

    var titles = normalizedSheets.map(function (sheet) { return '<vt:lpstr>' + xmlEscape(sheet.name) + '</vt:lpstr>'; }).join("");
    var app = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
      '<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">' +
      '<Application>TravelwAI</Application><DocSecurity>0</DocSecurity><ScaleCrop>false</ScaleCrop>' +
      '<HeadingPairs><vt:vector size="2" baseType="variant"><vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant><vt:variant><vt:i4>' + normalizedSheets.length + '</vt:i4></vt:variant></vt:vector></HeadingPairs>' +
      '<TitlesOfParts><vt:vector size="' + normalizedSheets.length + '" baseType="lpstr">' + titles + '</vt:vector></TitlesOfParts>' +
      '<Company>TravelwAI</Company><LinksUpToDate>false</LinksUpToDate><SharedDoc>false</SharedDoc><HyperlinksChanged>false</HyperlinksChanged><AppVersion>1.0</AppVersion>' +
      '</Properties>';

    var files = [
      { name: "[Content_Types].xml", data: contentTypes },
      { name: "_rels/.rels", data: rootRels },
      { name: "xl/workbook.xml", data: workbook },
      { name: "xl/_rels/workbook.xml.rels", data: workbookRels },
      { name: "xl/styles.xml", data: styles },
      { name: "docProps/core.xml", data: core },
      { name: "docProps/app.xml", data: app }
    ];

    normalizedSheets.forEach(function (sheet, index) {
      files.push({ name: "xl/worksheets/sheet" + (index + 1) + ".xml", data: buildWorksheetXml(sheet) });
    });
    return files;
  }

  function normalizedText(value) {
    return String(value == null ? "" : value)
      .replace(/\u00a0/g, " ")
      .replace(/[ \t]+\n/g, "\n")
      .replace(/\n[ \t]+/g, "\n")
      .replace(/[ \t]{2,}/g, " ")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  function getElementExportText(element) {
    if (!element) return "";
    var clone = element.cloneNode(true);
    var originals = Array.from(element.querySelectorAll("input, select, textarea, button, a, img"));
    var copies = Array.from(clone.querySelectorAll("input, select, textarea, button, a, img"));

    copies.forEach(function (copy, index) {
      var original = originals[index];
      if (!original) return;
      var tag = original.tagName.toLowerCase();
      var value = "";
      if (tag === "input") {
        var type = String(original.type || "text").toLowerCase();
        if (type === "hidden" || type === "file") {
          copy.remove();
          return;
        }
        value = type === "checkbox" || type === "radio" ? (original.checked ? "Có" : "Không") : original.value;
      } else if (tag === "select") {
        value = Array.from(original.selectedOptions || []).map(function (option) { return option.textContent || option.value; }).join(", ");
      } else if (tag === "textarea") {
        value = original.value;
      } else if (tag === "button") {
        value = normalizedText(original.textContent) || original.getAttribute("aria-label") || original.getAttribute("title") || "";
      } else if (tag === "a") {
        value = normalizedText(original.textContent) || original.getAttribute("href") || "";
      } else if (tag === "img") {
        value = original.getAttribute("alt") || "";
      }
      copy.replaceWith(document.createTextNode(value ? " " + value + " " : ""));
    });

    clone.querySelectorAll("script, style, svg, [hidden], .sr-only").forEach(function (node) { node.remove(); });
    clone.querySelectorAll("br").forEach(function (node) { node.replaceWith(document.createTextNode("\n")); });
    clone.querySelectorAll("p, div, small").forEach(function (node) { node.appendChild(document.createTextNode("\n")); });
    return normalizedText(clone.textContent);
  }

  function normalizeHeaderKey(value) {
    return normalizedText(value)
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLowerCase();
  }

  function isIgnoredExportColumn(headerElement, headerText, index, totalColumns, options) {
    var settings = options || {};
    if (headerElement && headerElement.hasAttribute("data-excel-ignore")) return true;

    var key = normalizeHeaderKey(headerText);
    if (key === "thao tac" || key === "action" || key === "actions") return true;

    // Các bảng quản trị có cột thao tác ở cuối. Loại theo vị trí để cấu trúc
    // Excel luôn giống nhau dù tiêu đề đang là tiếng Việt hay tiếng Anh.
    return Boolean(settings.excludeLastColumn && index === totalColumns - 1);
  }

  function extractTable(table, options) {
    if (!table) return { headers: [], rows: [] };
    var headerRow = table.querySelector("thead tr");
    var headerCells = headerRow ? Array.from(headerRow.cells) : [];
    var allHeaders = headerCells.map(getElementExportText);
    var includedColumnIndexes = allHeaders.map(function (header, index) {
      return isIgnoredExportColumn(headerCells[index], header, index, allHeaders.length, options) ? -1 : index;
    }).filter(function (index) { return index >= 0; });
    var headers = includedColumnIndexes.map(function (index) { return allHeaders[index]; });
    var rows = Array.from(table.querySelectorAll("tbody tr")).filter(function (row) {
      return !row.querySelector(".empty-line");
    }).map(function (row) {
      var cells = Array.from(row.cells);
      return includedColumnIndexes.map(function (index) { return getElementExportText(cells[index]); });
    });
    return { headers: headers, rows: rows };
  }

  function tableSheet(name, panelId, options) {
    var table = document.querySelector("#" + panelId + " table");
    var data = extractTable(table, options);
    return {
      name: name,
      rows: [data.headers].concat(data.rows),
      headerRows: [1],
      filterRow: 1,
      freezeRow: 1
    };
  }

  function feedbackSheet(name) {
    var data = window.TravelwAIAdminFeedback && typeof window.TravelwAIAdminFeedback.getExportRows === "function"
      ? window.TravelwAIAdminFeedback.getExportRows()
      : { headers: ["Tài khoản", "Nội dung", "Trạng thái", "Thời gian"], rows: [] };
    return {
      name: name,
      rows: [data.headers].concat(data.rows || []),
      headerRows: [1],
      filterRow: 1,
      freezeRow: 1
    };
  }

  function storageSheet(name) {
    var data = extractTable(document.querySelector("#tab-storage table"));
    var rows = [
      ["Tổng dung lượng", "Số tài khoản", "Số ảnh"],
      [
        document.getElementById("adminStorageTotalText")?.textContent || "",
        document.getElementById("adminStorageAccountCount")?.textContent || "",
        document.getElementById("adminStorageImageCount")?.textContent || ""
      ],
      [],
      data.headers
    ].concat(data.rows);
    return {
      name: name,
      rows: rows,
      headerRows: [1, 4],
      filterRow: 4,
      freezeRow: 4
    };
  }

  function tabLabel(tabId, fallback) {
    return normalizedText(document.querySelector('[data-tab="' + tabId + '"]')?.textContent) || fallback;
  }

  function isPendingBody(bodyId) {
    var body = document.getElementById(bodyId);
    if (!body) return false;
    var text = normalizedText(body.textContent).toLowerCase();
    return text.includes("đang tải") || text.includes("chọn tab để tải");
  }

  async function ensureExportDataLoaded() {
    var jobs = [];
    var loaders = [
      ["accountTableBody", "loadAccounts"],
      ["tourTableBody", "loadTours"],
      ["scheduleTableBody", "loadSchedules"],
      ["postTableBody", "loadPosts"],
      ["adminRevenueTableBody", "loadAdminRevenue"]
    ];

    loaders.forEach(function (entry) {
      var bodyId = entry[0];
      var loader = window[entry[1]];
      if (isPendingBody(bodyId) && typeof loader === "function") jobs.push(Promise.resolve().then(loader));
    });

    if (jobs.length) await Promise.allSettled(jobs);
  }

  function buildAdminSheets() {
    var withoutActionColumn = { excludeLastColumn: true };
    return [
      tableSheet(tabLabel("accounts", "Tài khoản"), "tab-accounts", withoutActionColumn),
      tableSheet(tabLabel("tours", "Tour"), "tab-tours", withoutActionColumn),
      tableSheet(tabLabel("schedules", "Lịch trình"), "tab-schedules", withoutActionColumn),
      tableSheet(tabLabel("posts", "Bài viết"), "tab-posts", withoutActionColumn),
      tableSheet(tabLabel("revenue", "Doanh thu"), "tab-revenue")
    ];
  }

  function filenameTimestamp(date) {
    function pad(value) { return String(value).padStart(2, "0"); }
    return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate()) + "_" + pad(date.getHours()) + "-" + pad(date.getMinutes());
  }

  function showExportToast(message, type) {
    if (typeof window.showToast === "function") window.showToast(message, type);
    else if (typeof window.TravelwAIToast === "function") window.TravelwAIToast(message, type || "info");
  }

  async function exportAdminExcel() {
    var button = document.getElementById("exportAdminExcelButton");
    if (!button || button.disabled) return;
    button.disabled = true;
    button.classList.add("is-exporting");
    button.setAttribute("aria-busy", "true");

    try {
      await ensureExportDataLoaded();
      var sheets = buildAdminSheets();
      var workbookBytes = createZip(buildWorkbookFiles(sheets));
      var blob = new Blob([workbookBytes], { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
      var url = URL.createObjectURL(blob);
      var download = document.createElement("a");
      download.href = url;
      download.download = "travelwai-admin-" + filenameTimestamp(new Date()) + ".xlsx";
      document.body.appendChild(download);
      download.click();
      download.remove();
      window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
      showExportToast("Đã xuất dữ liệu admin ra Excel.", "success");
    } catch (error) {
      console.error("Không xuất được Excel:", error);
      showExportToast(error?.message || "Không xuất được dữ liệu Excel.", "error");
    } finally {
      button.disabled = false;
      button.classList.remove("is-exporting");
      button.removeAttribute("aria-busy");
    }
  }

  function bind() {
    document.getElementById("exportAdminExcelButton")?.addEventListener("click", exportAdminExcel);
  }

  window.TravelwAIAdminExcel = {
    export: exportAdminExcel,
    buildWorkbook: function () { return createZip(buildWorkbookFiles(buildAdminSheets())); }
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind, { once: true });
  else bind();
})();
