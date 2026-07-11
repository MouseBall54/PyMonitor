import pathlib
import re
import unittest

import pyruntime_inspector_agent
from pyruntime_inspector_agent import server


class ReleaseMetadataTests(unittest.TestCase):
    def test_all_product_versions_match(self):
        root = pathlib.Path(__file__).resolve().parents[2]
        props = (root / "Directory.Build.props").read_text(encoding="utf-8")
        pyproject = (root / "agent" / "pyproject.toml").read_text(encoding="utf-8")

        product_version = re.search(r"<Version>([^<]+)</Version>", props).group(1)
        package_version = re.search(r'^version = "([^"]+)"$', pyproject, re.MULTILINE).group(1)

        self.assertEqual(product_version, package_version)
        self.assertEqual(product_version, pyruntime_inspector_agent.__version__)
        self.assertEqual(product_version, server.AGENT_VERSION)


if __name__ == "__main__":
    unittest.main()
