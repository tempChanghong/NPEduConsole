# NPEduConsole 蓝色图标

沿用 NPEduTools 青绿版和 NPClassworks 暖橙版的抽象 n 和书页曲线。四种 SVG 的画布与贝塞尔路径逐项一致。标准版主蓝为 `#2668B2`，深折面为 `#1B4878`，浅书页为 `#70ADE6`。深浅单色用 `#202E35` 和 `#FFFFFF`。

| 文件 | 内容 |
| --- | --- |
| `npeduconsole-logo.ai` | Illustrator 2026 原生 RGB 母版。四个 1024 × 1024 画板，11 条闭合路径，90 个可编辑锚点。 |
| `npeduconsole-logo.svg` | 标准版，含三种蓝色填充与透明留白。 |
| `npeduconsole-logo-small.svg` | 小尺寸简化版。沿用系列中加宽的开口，并省去深色折面。 |
| `npeduconsole-logo-mono.svg`、`npeduconsole-logo-mono-light.svg` | 深色和白色单色版，分别适合浅底和深底。深色版本支持 SVG 内联时通过 `color` 调色。 |
| `png/` | 透明 PNG。彩色 16、24、32、48、64 px 用简化版，128、256、512、1024 px 用标准版。另有 256 px 简化对照和两套单色 PNG。 |
| `npeduconsole.ico` | Windows 多帧图标。16、24、32、48、64 px 为简化版 32 位 DIB，256 px 为标准版 PNG。 |
| `npep-family-preview.png` | 使用三款产品的实际 PNG 合成。下方小图按原始像素显示。 |

母版已在 Illustrator 中重新打开，并用透明网格检查了外侧和内部开口。四个 SVG 重新导入后，其 256 px 渲染与交付 PNG 的 RGBA 像素一致。ICO 六帧均已解码并与对应 PNG 逐像素比对。深浅背景和小尺寸结果见预览与 `source/validation.json`。

需要重新导出时，执行 `source/build_vectors.py`，在 Illustrator 中运行生成的 `source/build-illustrator.jsx`。单独重新打开保存的 AI 后，运行 `source/verify-illustrator.jsx`，再用 Pillow 运行 `source/finish_assets.py`。比较图和几何核对使用同级的两个产品仓库。

本次交付范围为设计素材和导出文件。桌面程序接入另行处理。
