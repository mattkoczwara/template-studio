# Dependency license provenance

These unmodified license and notice files accompany the redistributed dependencies. SHA-256 values are included in the release's `published-files.json` manifest.

| Component | Source of included files |
|---|---|
| Magick.NET Q8 x64 15.0.0 / Magick.NET.Core 15.0.0 | `Notice.txt` and `Copyright.txt` from the restored signed NuGet packages; Apache license from upstream commit `79bb8700d7fe3a564f62926ab299fc2520cb2269`, the commit declared by the package nuspec |
| ImageMagick and native decoder dependencies | Complete bundled `Magick.NET-15.0.0-ThirdPartyNotices.txt`, including ImageMagick 7.1.2-33 and decoder-specific licenses |
| PDFsharp 6.2.4 | Unmodified `LICENSE` from upstream tag `v6.2.4` |
| .NET runtime 10.0.13 | `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` from `Microsoft.NETCore.App.Runtime.win-x64` 10.0.13 |
| .NET Windows Desktop runtime 10.0.13 | `LICENSE` from `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.13 |
| Microsoft.Extensions.Logging.Abstractions 8.0.3 | License and full notices from the restored package |
| Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2 | License and full notices from the restored package |

AI worker/model/native-runtime licenses and provenance are bundled separately under `ai/`. Test-only packages such as PdfPig are not part of the application payload. Inno Setup is a build tool; its installer/uninstaller carries its own original copyright information. No license to the user's imported artwork is granted.

Upstream URLs:

- https://raw.githubusercontent.com/dlemstra/Magick.NET/79bb8700d7fe3a564f62926ab299fc2520cb2269/License.txt
- https://raw.githubusercontent.com/empira/PDFsharp/v6.2.4/LICENSE
- https://www.nuget.org/packages/Magick.NET-Q8-x64/15.0.0
- https://www.nuget.org/packages/PDFsharp/6.2.4
- https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.13
- https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.13
