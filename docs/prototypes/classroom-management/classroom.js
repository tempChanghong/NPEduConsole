"use strict";

// These fixtures live for one page load. This prototype has no server adapter or storage.
const rooms = [
  { name: "高二（1）班", screens: [
    { name: "高二（1）班大屏", device: "高二（1）班一体机", online: true, lastReport: "2026/11/21 09:54:11",
      version: null, microphone: "讲台 USB 麦克风", locked: true, policy: 5, receipt: "2026/11/21 09:54:11", managed: false }
  ] },
  { name: "高二（2）班", screens: [
    { name: "主讲大屏", device: "讲台一体机", online: false, lastReport: "2026/11/20 17:36:12",
      version: "1.1.10", microphone: "内置麦克风", locked: true, policy: 2, receipt: "2026/11/20 17:35:58",
      managed: true, replacement: "requested", candidate: "新讲台一体机" },
    { name: "后排大屏", device: "后排显示终端", online: true, lastReport: "2026/11/20 20:08:09",
      version: "1.1.10", microphone: null, locked: false, managed: false }
  ] },
  { name: "高二（3）班", screens: [] }
];

const $ = id => document.getElementById(id);
const glyph = code => '<span class="glyph" aria-hidden="true">&#x' + code + ';</span>';
const details = rows => '<dl class="detail-list">' + rows.map(([label, value]) =>
  '<div><dt>' + label + '</dt><dd>' + value + '</dd></div>').join('') + '</dl>';
const titles = { microphone: "麦克风策略", software: "软件维护", replacement: "设备替换", admission: "准备教室大屏" };
const replacementTitles = { requested: "有一台新设备申请替换", approved: "已批准，等待新设备接入",
  connected: "新设备已接入，等待首次上报", completed: "换机完成", declined: "本次换机申请已拒绝" };
let selectedRoom = 0;
let selectedScreen = 0;
let detail = null;

function renderClassList() {
  const search = $("class-search").value.trim().normalize("NFKC").toLowerCase();
  const classList = $("class-list");
  classList.replaceChildren();
  rooms.forEach((room, index) => {
    if (!room.name.normalize("NFKC").toLowerCase().includes(search)) return;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "class-row";
    button.dataset.room = index;
    button.setAttribute("aria-current", String(index === selectedRoom));
    button.innerHTML = glyph("e80a") + "<span>" + room.name + "</span>";
    if (room.screens.some(screen => screen.replacement === "requested")) {
      button.innerHTML += '<span class="pending-dot" aria-hidden="true"></span><span class="visually-hidden">有申请待核对</span>';
    }
    classList.append(button);
  });
  $("empty-search").hidden = !!classList.childElementCount;
}

function renderScreenPicker(room) {
  const picker = $("screen-switch");
  picker.replaceChildren();
  picker.hidden = room.screens.length < 2;
  room.screens.forEach((screen, index) => {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = screen.name;
    button.dataset.screen = index;
    button.setAttribute("aria-pressed", String(index === selectedScreen));
    picker.append(button);
  });
}

function actionCard(title, message, action, label) {
  return '<div class="action-card' + (action ? ' attention' : '') + '">' + glyph(action ? 'e946' : 'e930') +
    '<div class="row-text"><strong>' + title + '</strong>' + (message ? '<span>' + message + '</span>' : '') + '</div>' +
    (action ? '<button class="button" type="button" id="review" data-detail="' + action + '">' + label + '</button>' : '') + '</div>';
}

function render() {
  const room = rooms[selectedRoom];
  const screen = room.screens[selectedScreen];
  $("class-name").textContent = room.name;
  $("screen-count").textContent = room.screens.length + " 台大屏";
  renderClassList();
  renderScreenPicker(room);
  $("settings").hidden = !screen;
  if (!screen) {
    $("device-card").innerHTML = '<div class="device-main"><span class="glyph monitor" aria-hidden="true">&#xe7f4;</span>' +
      '<div><h3>尚未登记大屏</h3><p class="muted">本班目前没有设备可上报。</p></div></div>';
    $("action-card").innerHTML = actionCard("为本班准备首台大屏", "", "admission", "查看接入流程");
    return;
  }
  const awaitingReport = screen.replacement === "connected";
  const connectivity = awaitingReport ? "待上报" : screen.online ? "在线" : "离线";
  $("device-card").innerHTML = '<div class="device-main"><span class="glyph monitor" aria-hidden="true">&#xe7f4;</span>' +
    '<div><h3>' + screen.device + '</h3><p class="muted">' + screen.name + '</p></div>' +
    '<span class="pill' + (screen.online ? '' : ' off') + '">' + connectivity + '</span></div>' +
    '<dl class="device-facts"><div><dt>最近上报</dt><dd>' + (screen.lastReport || "等待首次上报") + '</dd></div>' +
    '<div><dt>终端运行版本</dt><dd>' + (screen.version ? "NPEduTools " + screen.version : "待设备报告") + '</dd></div></dl>';
  if (screen.replacement) {
    const next = {requested: "核对新旧终端后，可批准本次替换。", approved: "旧设备仍保留互联授权。",
      connected: "首次收到状态后才能确认课堂已恢复。", completed: "", declined: ""};
    const completed = ["completed", "declined"].includes(screen.replacement);
    $("action-card").innerHTML = actionCard(replacementTitles[screen.replacement], next[screen.replacement],
      completed ? null : "replacement", screen.replacement === "requested" ? "核对申请" : "查看进度");
  } else {
    $("action-card").innerHTML = actionCard("当前没有待处理事项", "", null);
  }
  $("mic-summary").textContent = screen.microphone || "尚未指定学校麦克风";
  $("mic-status").textContent = screen.receipt ? "策略已应用" : "暂无策略回执";
  $("software-summary").textContent = "查看本机纳管与课后升级任务";
  $("software-status").textContent = screen.managed ? "已纳管" : "未纳管";
}

function renderDrawer() {
  const room = rooms[selectedRoom];
  const screen = room.screens[selectedScreen];
  $("drawer-title").textContent = titles[detail];
  $("drawer-scope").textContent = room.name + (screen ? " / " + screen.name : "");
  let body;
  if (detail === "microphone") {
    body = '<h3 class="drawer-subtitle">学校策略</h3>' + details([
      ["当前设备", screen.device], ["学校指定麦克风", screen.microphone || "尚未指定"],
      ["锁定策略", screen.locked ? "要求锁定" : "未要求锁定"]
    ]) + '<h3 class="drawer-subtitle">设备回执</h3>' + details([
      ["实际选用麦克风", screen.receipt ? screen.microphone : "暂无上报"],
      ["已应用策略版本", screen.receipt ? String(screen.policy) : "暂无回执"], ["回执时间", screen.receipt || "暂无回执"]
    ]);
  } else if (detail === "software") {
    body = '<h3 class="drawer-subtitle">本机维护状态</h3>' + details([
      ["当前设备", screen.device], ["NPDownloader 纳管", screen.managed ? "已由本机确认" : "未纳管"],
      ["最近上报的运行版本", screen.version || "等待上报"], ["课后升级任务", "暂无已批准任务"]
    ]);
  } else if (detail === "admission") {
    body = '<p class="drawer-copy">在 Console 登记本班首台大屏并开放班级口令接入。现场在 NPEduTools 选择本校、本班和大屏，输入已设置的班级管理口令。待新设备首次上报后，即可查看运行状态。</p>';
  } else {
    body = '<h3 class="drawer-subtitle">' + replacementTitles[screen.replacement] + '</h3>' +
      details([["原设备", "讲台一体机"], ["申请设备", screen.candidate], ["申请时间", "2026/11/20 20:06:18"]]);
    const steps = {
      requested: ['学校批准后，原设备的互联授权保留到新设备完成接入。', "批准替换"],
      approved: ['新设备再次连接后才会接替原设备。', "模拟新设备接入"],
      connected: ['已完成授权交接。还需收到新设备状态上报，才能确认换机完成。', "模拟首次上报"],
      completed: ['已收到新设备的首次状态上报。', null],
      declined: ['原设备的互联授权继续有效。', null]
    };
    const [copy, action] = steps[screen.replacement];
    body += '<p class="drawer-copy">' + copy + '</p><p class="eyebrow">本页演示换机流程，操作仅在当前样稿中生效。</p>';
    if (action) body += '<div class="drawer-action"><button class="button primary" type="button" id="advance">' + action + '</button>' +
      (screen.replacement === "requested" ? '<button class="button" type="button" id="decline">拒绝申请</button>' : '') + '</div>';
  }
  $("drawer-body").innerHTML = body;
}

function openDetail(name) {
  detail = name;
  renderDrawer();
  $("drawer").showModal();
  $("close").focus();
}

function advanceReplacement(decline) {
  const screen = rooms[selectedRoom].screens[selectedScreen];
  const before = screen.replacement;
  if (decline && before === "requested") screen.replacement = "declined";
  else if (!decline && before === "requested") screen.replacement = "approved";
  else if (!decline && before === "approved") {
    // The new terminal owns its own credentials, microphone report and software maintenance consent.
    Object.assign(screen, { replacement: "connected", device: screen.candidate, online: false, lastReport: null,
      version: null, microphone: null, locked: false, policy: null, receipt: null, managed: false });
  } else if (!decline && before === "connected") {
    Object.assign(screen, { replacement: "completed", online: true, lastReport: "2026/11/20 20:12:18", version: "1.1.10" });
  } else return;
  render();
  renderDrawer();
  ($("advance") || $("close")).focus();
  $("announcement").textContent = replacementTitles[screen.replacement];
}

$("class-search").addEventListener("input", renderClassList);
$("class-list").addEventListener("click", event => {
  const button = event.target.closest("[data-room]");
  if (!button) return;
  selectedRoom = Number(button.dataset.room);
  selectedScreen = 0;
  render();
  $("class-list").querySelector('[aria-current="true"]').focus();
  $("workspace").scrollTop = 0;
});
$("screen-switch").addEventListener("click", event => {
  const button = event.target.closest("[data-screen]");
  if (!button) return;
  selectedScreen = Number(button.dataset.screen);
  render();
  $("screen-switch").querySelector('[aria-pressed="true"]').focus();
});
$("action-card").addEventListener("click", event => {
  const button = event.target.closest("[data-detail]");
  if (button) openDetail(button.dataset.detail);
});
for (const name of ["microphone", "software"]) $(name).addEventListener("click", () => openDetail(name));
$("drawer-body").addEventListener("click", event => {
  if (event.target.id === "advance" || event.target.id === "decline") advanceReplacement(event.target.id === "decline");
});
$("close").addEventListener("click", () => $("drawer").close());
$("drawer").addEventListener("close", () => {
  const trigger = detail === "microphone" || detail === "software" ? $(detail) : $("review") || $("microphone");
  if (trigger && !trigger.closest("[hidden]")) trigger.focus();
  else $("class-list").querySelector('[aria-current="true"]')?.focus();
  detail = null;
});
render();
