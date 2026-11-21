// Optional desktop screenshot harness. Only opens the local design preview.
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { chromium } = require(process.env.PLAYWRIGHT_CORE_PATH || "playwright-core");

async function main() {
  const root = path.resolve(__dirname, "..");
  const output = path.join(root, ".artifacts/classroom-ui-redesign/console");
  const source = path.join(root, "docs/prototypes/classroom-management/index.html");
  await fs.mkdir(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROMIUM_PATH || undefined });
  try {
    const page = await browser.newPage({ viewport: { width: 1160, height: 780 }, reducedMotion: "reduce" });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("console", message => {
      if (message.type() === "error") errors.push(message.text());
    });
    const external = [];
    await page.route(/^https?:/, route => { external.push(route.request().url()); return route.abort(); });
    await page.goto(pathToFileURL(source).href);
    await page.locator("h1").getByText("高二（1）班").waitFor();
    await page.evaluate(() => document.fonts.ready);
    const capture = name => page.screenshot({ path: path.join(output, name + ".png") });
    const drawer = page.getByRole("dialog");
    const close = () => page.getByRole("button", { name: "关闭详情" }).click();
    const choose = index => page.locator('[data-room="' + index + '"]').click();
    const fits = async () => assert.equal(await page.evaluate(() =>
      document.documentElement.scrollWidth <= window.innerWidth &&
      [...document.querySelectorAll(".surface,dialog[open]")].every(el => el.scrollWidth <= el.clientWidth + 1)), true);
    await capture("after-1160x780");
    await page.setViewportSize({ width: 1128, height: 779 });
    await capture("after-reference-size");
    await page.setViewportSize({ width: 1160, height: 780 });
    await page.getByRole("button", { name: /麦克风策略/ }).click();
    assert.match(await drawer.innerText(), /讲台 USB 麦克风/);
    await capture("microphone");
    await page.keyboard.press("Escape");
    assert.equal(await page.locator("#microphone").evaluate(el => document.activeElement === el), true);
    await page.getByRole("button", { name: /软件维护/ }).click();
    assert.match(await drawer.innerText(), /未纳管/);
    await close();

    await choose(1);
    await page.getByRole("button", { name: "后排大屏" }).click();
    assert.match(await page.locator("#device-card").innerText(), /后排显示终端/);
    assert.match(await page.locator("#action-card").innerText(), /当前没有待处理事项/);
    await page.getByRole("button", { name: "主讲大屏" }).click();
    await page.getByRole("button", { name: "核对申请" }).click();
    await capture("replacement-request");
    await drawer.getByRole("button", { name: "批准替换" }).click();
    assert.match(await page.locator("#device-card").innerText(), /讲台一体机/);
    assert.doesNotMatch(await page.locator("#device-card").innerText(), /新讲台一体机/);
    await drawer.getByRole("button", { name: "模拟新设备接入" }).click();
    assert.match(await page.locator("#device-card").innerText(), /新讲台一体机/);
    assert.match(await page.locator("#device-card").innerText(), /等待首次上报/);
    await drawer.getByRole("button", { name: "模拟首次上报" }).click();
    assert.match(await page.locator("#action-card").innerText(), /换机完成/);
    await close();
    await page.getByRole("button", { name: /软件维护/ }).click();
    assert.match(await drawer.innerText(), /未纳管/);
    await close();
    await choose(2);
    assert.equal(await page.locator("#settings").isVisible(), false);
    await page.getByRole("button", { name: "查看接入流程" }).click();
    assert.match(await drawer.innerText(), /班级管理口令/);
    await close();
    await page.locator("#class-search").fill("(1)");
    assert.equal(await page.locator("#class-list button").count(), 1);
    await page.locator("#class-search").fill("不存在");
    assert.equal(await page.locator("#empty-search").isVisible(), true);

    await page.reload();
    await choose(1);
    await page.getByRole("button", { name: "核对申请" }).click();
    await drawer.getByRole("button", { name: "拒绝申请" }).click();
    await close();
    assert.match(await page.locator("#device-card").innerText(), /讲台一体机/);
    assert.match(await page.locator("#action-card").innerText(), /已拒绝/);
    await choose(0);
    for (const [width, height] of [[840, 620], [400, 800]]) {
      await page.setViewportSize({ width, height });
      await fits();
      await capture("compact-" + width);
      await page.getByRole("button", { name: /麦克风策略/ }).click();
      await fits();
      await close();
    }
    assert.deepEqual(errors, []);
    assert.deepEqual(external, []);
    console.log("PASS: classroom and screen selection, search, offline operation, device scope, replacement decisions and first report, focus return and compact layout.");
    console.log(output);
  } finally {
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
