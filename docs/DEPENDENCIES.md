# Pinned dependencies and redistribution

| Dependency | Version | License / payload location |
|---|---|---|
| .NET SDK (build only) | 10.0.402 | Microsoft MIT; workspace `.tools` excluded from application |
| .NET and Windows Desktop runtime | 10.0.13 | Full runtime and third-party terms under application `licenses/` |
| Magick.NET Q8 x64 / Core | 15.0.0 | Apache-2.0; full ImageMagick/native dependency notices under `licenses/` |
| PDFsharp | 6.2.4 | MIT; full license under `licenses/` |
| Microsoft Extensions transitive libraries | Exact `packages.lock.json` versions | Microsoft MIT and third-party terms under `licenses/` |
| NCNN Vulkan / CUNet | 20250915 / pinned weights | MIT/BSD and full native notices under `ai/`; see [AI.md](AI.md) |
| CPU NCNN adapter / library | Pinned local build / NCNN20261020 | Source and reproducible build script under `vendor/ai/cpu-src`; BSD3 library terms under `ai/` |
| Visual C++ runtime DLLs | 14.44.35211.0 | Microsoft distributable-code/runtime terms under `ai/`; no separate target install |
| Inno Setup (build only) | 7.1.0 | Upstream terms bundled with compiler; application setup distribution uses Inno license terms |
| PdfPig (tests only) | 0.1.16 | Apache-2.0; not part of application runtime |

NuGet versions are exact and `packages.lock.json` files are retained. The repository config uses the official NuGet v3 package and vulnerability-audit feed. Native binaries and model weights are individually hash-pinned and verified before inference. `vendor/licenses/README.md` records precise license origins; `vendor/ai/assets.json` records every shipped AI file hash/size. No imported artwork is included in the shipping app.

Dependency vulnerability inspection is performed as part of the final validation record. Absence of a current advisory is not a claim of universal decoder or model safety; image/archive size limits, isolated workers, bounded caches and explicit errors remain in force.
