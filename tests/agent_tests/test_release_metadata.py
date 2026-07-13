import pathlib
import re
import struct
import unittest

import pyruntime_inspector_agent
from pyruntime_inspector_agent import server


EXPECTED_PRODUCT = "PyMonitor"
EXPECTED_DEVELOPER = "박영문"
EXPECTED_VERSION = "26.7.12"
EXPECTED_UPGRADE_CODE = "{2D73C23D-A566-4D8A-889C-F89FCE4A1377}"
EXPECTED_ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 256]
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def xml_value(source, name):
    return re.search(rf"<{name}>([^<]+)</{name}>", source).group(1)


def ico_frames(data):
    reserved, image_type, count = struct.unpack_from("<HHH", data)
    if reserved != 0 or image_type != 1:
        raise ValueError("Invalid ICO directory header")

    frames = []
    for index in range(count):
        offset = 6 + index * 16
        width, height, _, _, planes, bit_count, length, image_offset = struct.unpack_from(
            "<BBBBHHII", data, offset
        )
        payload = data[image_offset:image_offset + length]
        if len(payload) != length:
            raise ValueError("ICO frame payload is truncated")
        frames.append(
            {
                "width": width or 256,
                "height": height or 256,
                "planes": planes,
                "bit_count": bit_count,
                "payload": payload,
            }
        )
    return frames


def png_dimensions(data):
    if not data.startswith(PNG_SIGNATURE) or data[12:16] != b"IHDR":
        raise ValueError("Expected a PNG image")
    return struct.unpack_from(">II", data, 16)


def markdown_section(source, heading):
    marker = f"## {heading}"
    start = source.index(marker)
    next_heading = source.find("\n## ", start + len(marker))
    return source[start:] if next_heading == -1 else source[start:next_heading]


class ReleaseMetadataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = pathlib.Path(__file__).resolve().parents[2]

    def test_all_product_versions_match(self):
        props = (self.root / "Directory.Build.props").read_text(encoding="utf-8")
        pyproject = (self.root / "agent" / "pyproject.toml").read_text(encoding="utf-8")
        repl_bootstrap = (
            self.root / "src" / "PyRuntimeInspector.App" / "Services" / "ReplBootstrap.cs"
        ).read_text(encoding="utf-8")
        live_attach = (
            self.root / "src" / "PyRuntimeInspector.App" / "Services" / "LiveAttachService.cs"
        ).read_text(encoding="utf-8")

        product_version = xml_value(props, "Version")
        package_version = re.search(r'^version = "([^"]+)"$', pyproject, re.MULTILINE).group(1)

        self.assertEqual(EXPECTED_VERSION, product_version)
        self.assertEqual(product_version, package_version)
        self.assertEqual(product_version, pyruntime_inspector_agent.__version__)
        self.assertEqual(product_version, server.AGENT_VERSION)
        self.assertEqual(2, pyruntime_inspector_agent.__bootstrap_abi__)
        self.assertEqual(pyruntime_inspector_agent.__bootstrap_abi__, server.BOOTSTRAP_ABI)
        self.assertIn(
            f"public const int ExpectedBootstrapAbi = {server.BOOTSTRAP_ABI};",
            repl_bootstrap,
        )
        self.assertIn("runpy.run_path", live_attach)
        self.assertIn("expected_bootstrap_abi=", live_attach)
        self.assertEqual(f"{product_version}.0", xml_value(props, "AssemblyVersion"))
        self.assertEqual(f"{product_version}.0", xml_value(props, "FileVersion"))
        self.assertEqual(product_version, xml_value(props, "InformationalVersion"))
        self.assertEqual("false", xml_value(props, "IncludeSourceRevisionInInformationalVersion"))

    def test_product_identity_is_consistent_across_release_metadata(self):
        props = (self.root / "Directory.Build.props").read_text(encoding="utf-8")
        pyproject = (self.root / "agent" / "pyproject.toml").read_text(encoding="utf-8")
        app_project = (
            self.root / "src" / "PyRuntimeInspector.App" / "PyRuntimeInspector.App.csproj"
        ).read_text(encoding="utf-8")
        app_manifest = (
            self.root / "src" / "PyRuntimeInspector.App" / "app.manifest"
        ).read_text(encoding="utf-8")
        main_window = (
            self.root / "src" / "PyRuntimeInspector.App" / "MainWindow.xaml"
        ).read_text(encoding="utf-8")
        about_window = (
            self.root / "src" / "PyRuntimeInspector.App" / "AboutWindow.xaml"
        ).read_text(encoding="utf-8")
        icon_path = (
            self.root / "src" / "PyRuntimeInspector.App" / "Assets" / "app-icon.ico"
        )
        icon_master_path = (
            self.root / "src" / "PyRuntimeInspector.App" / "Assets" / "app-icon.png"
        )
        package = (
            self.root / "installer" / "PyRuntimeInspector.Installer" / "Package.wxs"
        ).read_text(encoding="utf-8")
        installer_project = (
            self.root
            / "installer"
            / "PyRuntimeInspector.Installer"
            / "PyRuntimeInspector.Installer.wixproj"
        ).read_text(encoding="utf-8")

        self.assertEqual(EXPECTED_PRODUCT, xml_value(props, "Product"))
        self.assertEqual(EXPECTED_PRODUCT, xml_value(props, "Title"))
        self.assertEqual(EXPECTED_DEVELOPER, xml_value(props, "Authors"))
        self.assertEqual(EXPECTED_DEVELOPER, xml_value(props, "Company"))
        self.assertIn('name = "pymonitor-agent"', pyproject)
        self.assertIn(f'authors = [{{ name = "{EXPECTED_DEVELOPER}" }}]', pyproject)
        self.assertEqual(EXPECTED_PRODUCT, xml_value(app_project, "AssemblyName"))
        self.assertEqual("app.manifest", xml_value(app_project, "ApplicationManifest"))
        self.assertIn("<ApplicationIcon>Assets\\app-icon.ico</ApplicationIcon>", app_project)
        self.assertIn('<Resource Include="Assets\\app-icon.ico" />', app_project)
        self.assertIn('<Resource Include="Assets\\app-icon.png" />', app_project)
        self.assertIn("PerMonitorV2,PerMonitor", app_manifest)
        self.assertIn('requestedExecutionLevel level="asInvoker"', app_manifest)
        for window in (main_window, about_window):
            self.assertNotIn('Icon="Assets/app-icon.ico"', window)
            self.assertIn('Image Source="Assets/app-icon.png"', window)
            self.assertIn('RenderOptions.BitmapScalingMode="HighQuality"', window)

        frames = ico_frames(icon_path.read_bytes())
        self.assertEqual(EXPECTED_ICON_SIZES, [frame["width"] for frame in frames])
        for expected_size, frame in zip(EXPECTED_ICON_SIZES, frames, strict=True):
            self.assertEqual(expected_size, frame["height"])
            self.assertEqual(1, frame["planes"])
            self.assertEqual(32, frame["bit_count"])
            self.assertEqual((expected_size, expected_size), png_dimensions(frame["payload"]))
            self.assertEqual(8, frame["payload"][24])
            self.assertEqual(6, frame["payload"][25])

        icon_master = icon_master_path.read_bytes()
        master_width, master_height = png_dimensions(icon_master)
        self.assertEqual(master_width, master_height)
        self.assertGreaterEqual(master_width, 1024)
        self.assertEqual(8, icon_master[24])
        self.assertEqual(6, icon_master[25])
        self.assertIn(f'Name="{EXPECTED_PRODUCT}"', package)
        self.assertIn(f'Manufacturer="{EXPECTED_DEVELOPER}"', package)
        self.assertIn(f'UpgradeCode="{EXPECTED_UPGRADE_CODE}"', package)
        self.assertIn(f'Target="[INSTALLFOLDER]{EXPECTED_PRODUCT}.exe"', package)
        self.assertIn('<StandardDirectory Id="ProgramMenuFolder">', package)
        self.assertIn('Root="HKCU"', package)
        self.assertIn(f"<OutputName>{EXPECTED_PRODUCT}-$(Version)-win-x64</OutputName>", installer_project)

    def test_release_scripts_use_exact_public_artifact_names(self):
        portable = (self.root / "scripts" / "Build-PortableRelease.ps1").read_text(encoding="utf-8")
        release = (self.root / "scripts" / "Build-Release.ps1").read_text(encoding="utf-8")
        installer = (self.root / "scripts" / "Build-Installer.ps1").read_text(encoding="utf-8")
        portable_test = (self.root / "scripts" / "Test-PortableRelease.ps1").read_text(
            encoding="utf-8"
        )
        readme = (self.root / "README.md").read_text(encoding="utf-8")
        ci = (self.root / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        release_workflow = (self.root / ".github" / "workflows" / "release.yml").read_text(
            encoding="utf-8"
        )

        self.assertIn('"PyMonitor-$version-$RuntimeIdentifier"', portable)
        self.assertIn('"PyMonitor.exe"', release)
        self.assertIn('"artifacts\\PyMonitor-$($portable.Version)-win-x64.zip"', release)
        self.assertIn('"PyMonitor-$version-win-x64.msi"', installer)
        self.assertIn('"$ExpectedProductName.exe"', portable_test)
        self.assertIn('"$ExpectedProductName.dll"', portable_test)
        self.assertIn("PyMonitor-26.7.12-win-x64.zip", readme)
        self.assertIn("PyMonitor-26.7.12-win-x64.msi", readme)
        self.assertIn("name: PyMonitor-unsigned", ci)
        self.assertIn("name: PyMonitor-signed", release_workflow)
        self.assertIn('title "PyMonitor $env:GITHUB_REF_NAME"', release_workflow)

    def test_readme_has_end_user_install_quick_start_and_help_contract(self):
        readme = (self.root / "README.md").read_text(encoding="utf-8")

        features = markdown_section(readme, "주요 기능 한눈에 보기")
        for expected in (
            "변수 찾기",
            "Object Tree",
            "DataFrame",
            "실행 중 변화 추적",
            "Managed Launch",
            "Quick Attach",
            "읽기 전용",
        ):
            self.assertIn(expected, features)

        install = markdown_section(readme, "설치 및 제거")
        for expected in (
            "PyMonitor-26.7.12-win-x64.msi",
            "PyMonitor-26.7.12-win-x64.zip",
            "Get-FileHash",
            '"$file.sha256"',
            "별도 .NET 10 runtime을 설치할 필요가 없",
            "대상 CPython 3.10~3.14",
            "대상 Python 또는 대상 venv/Conda 환경",
            "최신 안정 Release",
            "24시간이 지났을 때",
            "About > Check for updates",
            "GitHubRepository",
            "repository metadata가 없는 로컬 개발 빌드",
            "Release 저장소는 공개",
            "private GitHub Release",
            "Windows Authenticode",
            "Windows UAC",
            "MSI major upgrade",
            "Windows MSI 설치 방식으로 전환",
            "설정 > 앱 > 설치된 앱 > PyMonitor > 제거",
            "Portable 제거",
            "%LOCALAPPDATA%\\PyMonitor\\settings.json",
        ):
            self.assertIn(expected, install)
        self.assertNotIn("자동 업데이트 기능은 없", install)
        self.assertNotIn("- MSI 업데이트:", install)

        quick_start = markdown_section(readme, "5분 빠른 시작")
        for expected in (
            "os.getpid()",
            "Rescan",
            "Quick Attach",
            "CPython 3.14+",
            "CPython 3.10~3.13",
            "Modules > `__main__`",
            "`F5`",
            "**Launch**",
        ):
            self.assertIn(expected, quick_start)

        help_section = markdown_section(readme, "F1 검색 도움말")
        for expected in (
            "`F1`",
            "**Help**",
            "PyMonitor Help",
            "modeless",
            "제목, 키워드, 요약, 상세 설명, 따라하기 단계, 예제 전체",
            "대소문자를 구분하지 않",
            "공백으로 나눈 여러 단어를 모두 포함",
            "`Ctrl+F`",
            "Variables",
            "도움말 검색란",
        ):
            self.assertIn(expected, help_section)

    def test_supported_runtime_agent_tests_provision_render_dependencies(self):
        matrix = (self.root / "scripts" / "Test-PythonMatrix.ps1").read_text(
            encoding="utf-8"
        )
        ci = (self.root / ".github" / "workflows" / "ci.yml").read_text(
            encoding="utf-8"
        )
        release_workflow = (
            self.root / ".github" / "workflows" / "release.yml"
        ).read_text(encoding="utf-8")

        dependency_arguments = "--with numpy --with pandas --with matplotlib --"
        self.assertEqual(2, matrix.count(dependency_arguments))
        self.assertIn("import matplotlib, numpy, pandas, platform", matrix)
        for workflow in (ci, release_workflow):
            self.assertIn("pip install --disable-pip-version-check numpy pandas matplotlib", workflow)
            self.assertIn('import matplotlib, numpy, pandas', workflow)

    def test_release_docs_define_update_and_github_publication_contracts(self):
        release_doc = (self.root / "docs" / "release.md").read_text(encoding="utf-8")

        update_contract = markdown_section(release_doc, "In-app update contract")
        for expected in (
            "GitHubRepository",
            "latest stable Release",
            "at most once",
            "per 24 hours",
            "About > Check for updates",
            "local build with",
            "empty repository metadata",
            "public Releases",
            "no embedded GitHub token",
            "PyMonitor-<version>-win-x64.msi",
            "PyMonitor-<version>-win-x64.msi.sha256",
            "SHA-256",
            "Authenticode",
            "UAC",
            "major upgrade",
            "changes the installation model to a machine-wide MSI",
        ):
            self.assertIn(expected, update_contract)

        runbook = markdown_section(release_doc, "GitHub release operator runbook")
        for expected in (
            "permissions: contents: write",
            "WINDOWS_CERTIFICATE_BASE64",
            "WINDOWS_CERTIFICATE_PASSWORD",
            "Directory.Build.props",
            "agent/pyproject.toml",
            "agent/pyruntime_inspector_agent/__init__.py",
            "agent/pyruntime_inspector_agent/server.py",
            "rg -n --fixed-strings '<previous-version>'",
            'git commit -m "release: PyMonitor $version"',
            'git tag -a "v$version"',
            'git push origin "v$version"',
            "GITHUB_REPOSITORY",
            "PyMonitor-X.Y.Z-win-x64.zip",
            "PyMonitor-X.Y.Z-win-x64.zip.sha256",
            "PyMonitor-X.Y.Z-win-x64.msi",
            "PyMonitor-X.Y.Z-win-x64.msi.sha256",
            "workflow_dispatch",
            "Publication failure conditions",
            "contents: write",
            "gh release create",
        ):
            self.assertIn(expected, runbook)

    def test_signed_installer_is_reverified_after_hash_sidecar_update(self):
        release = (self.root / "scripts" / "Build-Release.ps1").read_text(
            encoding="utf-8"
        )

        installer_sign = release.rindex('"Sign-Artifacts.ps1"')
        sidecar_update = release.index("Set-Content", installer_sign)
        final_verification = release.index('"Test-InstallerRelease.ps1"', sidecar_update)

        self.assertLess(installer_sign, sidecar_update)
        self.assertLess(sidecar_update, final_verification)

    def test_github_release_injects_repository_metadata_and_uploads_exact_assets(self):
        app_project = (
            self.root / "src" / "PyRuntimeInspector.App" / "PyRuntimeInspector.App.csproj"
        ).read_text(encoding="utf-8")
        portable = (self.root / "scripts" / "Build-PortableRelease.ps1").read_text(
            encoding="utf-8"
        )
        release = (self.root / "scripts" / "Build-Release.ps1").read_text(
            encoding="utf-8"
        )
        workflow = (self.root / ".github" / "workflows" / "release.yml").read_text(
            encoding="utf-8"
        )

        self.assertIn('<ItemGroup Condition="\'$(GitHubRepository)\' != \'\'">', app_project)
        self.assertIn(
            '<AssemblyMetadata Include="GitHubRepository" Value="$(GitHubRepository)" />',
            app_project,
        )
        self.assertIn("[string]$GitHubRepository", portable)
        self.assertIn("GitHubRepository must use owner/repository format.", portable)
        self.assertIn('"-p:GitHubRepository=$GitHubRepository"', portable)
        self.assertIn("-GitHubRepository $GitHubRepository", release)
        self.assertIn("-GitHubRepository $env:GITHUB_REPOSITORY", workflow)

        executable_sign = release.index('"Sign-Artifacts.ps1"')
        portable_archive = release.index('"New-PortableArchive.ps1"')
        installer_build = release.index('"Build-Installer.ps1"')
        installer_sign = release.rindex('"Sign-Artifacts.ps1"')
        self.assertLess(executable_sign, portable_archive)
        self.assertLess(portable_archive, installer_build)
        self.assertLess(installer_build, installer_sign)

        self.assertIn('tags: ["v*"]', workflow)
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("contents: write", workflow)
        self.assertIn("id: release_metadata", workflow)
        self.assertIn('Add-Content $env:GITHUB_OUTPUT "version=$version"', workflow)
        self.assertIn("Release tag $env:GITHUB_REF_NAME does not match product version", workflow)
        self.assertIn("WINDOWS_CERTIFICATE_BASE64", workflow)
        self.assertIn("WINDOWS_CERTIFICATE_PASSWORD", workflow)
        self.assertIn("X509KeyStorageFlags]::EphemeralKeySet", workflow)
        self.assertIn("Import-Certificate", workflow)
        self.assertIn("Cert:\\LocalMachine\\Root", workflow)
        self.assertIn('Add-Content $env:GITHUB_OUTPUT "self_signed=', workflow)
        self.assertIn('Remove-Item -LiteralPath "Cert:\\LocalMachine\\Root\\$temporaryRootThumbprint"', workflow)
        self.assertIn("- name: Remove temporary signing material", workflow)
        self.assertIn("if: always()", workflow)
        self.assertIn("timeout-minutes: 2", workflow)
        self.assertIn("Test-Path -LiteralPath $asset -PathType Leaf", workflow)
        self.assertIn("gh release create $env:GITHUB_REF_NAME @assets", workflow)
        self.assertIn("--verify-tag --generate-notes", workflow)
        self.assertIn("--prerelease", workflow)
        self.assertNotIn("artifacts\\*.zip", workflow)

        require_secrets = workflow.index("- name: Require signing secrets")
        build_release = workflow.index("- name: Build, test, and sign release")
        create_release = workflow.index("- name: Create GitHub release")
        upload_artifact = workflow.index("- uses: actions/upload-artifact@v4")
        self.assertLess(require_secrets, build_release)
        self.assertLess(build_release, create_release)
        self.assertLess(create_release, upload_artifact)
        create_step = workflow[create_release:upload_artifact]
        self.assertIn("if: startsWith(github.ref, 'refs/tags/')", create_step)
        self.assertIn("GH_TOKEN: ${{ github.token }}", create_step)
        for asset in (
            '"artifacts\\$baseName.zip"',
            '"artifacts\\$baseName.zip.sha256"',
            '"artifacts\\installer\\$baseName.msi"',
            '"artifacts\\installer\\$baseName.msi.sha256"',
        ):
            self.assertIn(asset, create_step)

        version_expression = "${{ steps.release_metadata.outputs.version }}"
        expected_assets = (
            f"artifacts/PyMonitor-{version_expression}-win-x64.zip",
            f"artifacts/PyMonitor-{version_expression}-win-x64.zip.sha256",
            f"artifacts/installer/PyMonitor-{version_expression}-win-x64.msi",
            f"artifacts/installer/PyMonitor-{version_expression}-win-x64.msi.sha256",
        )
        workflow_lines = [line.strip() for line in workflow.splitlines()]
        for asset in expected_assets:
            self.assertEqual(1, workflow_lines.count(asset))
        self.assertIn("if-no-files-found: error", workflow)


if __name__ == "__main__":
    unittest.main()
