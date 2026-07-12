import pathlib
import re
import unittest

import pyruntime_inspector_agent
from pyruntime_inspector_agent import server


EXPECTED_PRODUCT = "PyMonitor"
EXPECTED_DEVELOPER = "박영문"
EXPECTED_VERSION = "26.7.11"
EXPECTED_UPGRADE_CODE = "{2D73C23D-A566-4D8A-889C-F89FCE4A1377}"


def xml_value(source, name):
    return re.search(rf"<{name}>([^<]+)</{name}>", source).group(1)


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
        self.assertIn("PerMonitorV2,PerMonitor", app_manifest)
        self.assertIn('requestedExecutionLevel level="asInvoker"', app_manifest)
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
        self.assertIn("PyMonitor-26.7.11-win-x64.zip", readme)
        self.assertIn("PyMonitor-26.7.11-win-x64.msi", readme)
        self.assertIn("name: PyMonitor-unsigned", ci)
        self.assertIn("name: PyMonitor-signed", release_workflow)
        self.assertIn('title "PyMonitor $env:GITHUB_REF_NAME"', release_workflow)

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

    def test_signed_installer_is_reverified_after_hash_sidecar_update(self):
        release = (self.root / "scripts" / "Build-Release.ps1").read_text(
            encoding="utf-8"
        )

        installer_sign = release.rindex('"Sign-Artifacts.ps1"')
        sidecar_update = release.index("Set-Content", installer_sign)
        final_verification = release.index('"Test-InstallerRelease.ps1"', sidecar_update)

        self.assertLess(installer_sign, sidecar_update)
        self.assertLess(sidecar_update, final_verification)


if __name__ == "__main__":
    unittest.main()
