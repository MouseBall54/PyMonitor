import os
import subprocess
import sys
import types
import unittest
from unittest import mock

from pyruntime_inspector_agent import dataframes
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector
from pyruntime_inspector_agent.server import InspectorAgent, _bound_result

try:
    import pandas as pd
except ImportError:  # pragma: no cover - exercised by the Python version matrix
    pd = None


class DataFrameSafetyTests(unittest.TestCase):
    def test_agent_adapter_does_not_import_pandas(self):
        environment = dict(os.environ)
        environment["PYTHONPATH"] = os.path.abspath("agent")
        completed = subprocess.run(
            [
                sys.executable,
                "-c",
                "import sys; import pyruntime_inspector_agent.dataframes; "
                "assert 'pandas' not in sys.modules",
            ],
            cwd=os.path.abspath("."),
            env=environment,
            capture_output=True,
            text=True,
            timeout=20,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr)

    def test_fake_loaded_pandas_and_heap_dataframe_never_run_properties(self):
        calls = []

        class DataFrame:
            __module__ = "pandas.core.frame"

            @property
            def shape(self):
                calls.append("shape")
                raise AssertionError("fake shape property must not run")

            @property
            def columns(self):
                calls.append("columns")
                raise AssertionError("fake columns property must not run")

            def __repr__(self):
                calls.append("repr")
                raise AssertionError("fake repr must not run")

        fake_pandas = types.ModuleType("pandas")
        fake_frame = types.ModuleType("pandas.core.frame")
        fake_pandas.DataFrame = DataFrame
        fake_frame.DataFrame = DataFrame
        value = DataFrame()

        with mock.patch.dict(
            sys.modules,
            {"pandas": fake_pandas, "pandas.core.frame": fake_frame},
        ):
            self.assertFalse(dataframes.is_exact_dataframe(value))
            summary = SafeObjectInspector(HandleStore()).summarize(value)

        self.assertIsNone(summary["adapterKind"])
        self.assertEqual([], calls)


@unittest.skipIf(pd is None, "pandas is not installed for this Python runtime")
class DataFrameAdapterTests(unittest.TestCase):
    def setUp(self):
        self.inspector = SafeObjectInspector(HandleStore())

    def test_summary_describe_and_bounded_preview(self):
        frame = pd.DataFrame(
            {
                "date": pd.date_range("2026-01-01", periods=3),
                "product": ["A", "B", "C"],
                "quantity": [12, 8, 15],
            }
        )

        summary = self.inspector.summarize(frame)
        description = dataframes.describe(frame)
        result = dataframes.preview(frame, row_count=2, column_count=2)

        self.assertEqual("pandas.DataFrame", summary["adapterKind"])
        self.assertEqual([3, 3], summary["shape"])
        self.assertEqual("DataFrame(rows=3, columns=3)", summary["safePreview"])
        self.assertEqual((3, 3), (description["totalRows"], description["totalColumns"]))
        self.assertEqual(["date", "product"], [column["name"] for column in result["columns"]])
        self.assertEqual(["0", "1"], result["indexLabels"])
        self.assertTrue(result["hasMoreRows"])
        self.assertTrue(result["hasMoreColumns"])
        self.assertEqual(2, len(result["rows"]))
        self.assertTrue(result["rows"][0][0].startswith("2026-01-01"))
        self.assertEqual("A", result["rows"][0][1])

    def test_public_properties_callables_and_mutable_module_functions_are_not_used(self):
        frame = pd.DataFrame({"name": ["alpha", "beta"], "value": [1, 2]})
        calls = []

        def hostile(*args, **kwargs):
            calls.append((args, kwargs))
            raise AssertionError("mutable pandas API must not run")

        property_names = ("shape", "columns", "index", "dtypes", "iloc", "values")
        method_names = ("head", "to_numpy", "to_dict", "to_json", "iterrows", "itertuples")
        patches = [
            mock.patch.object(pd.DataFrame, name, property(hostile))
            for name in property_names
        ] + [
            mock.patch.object(pd.DataFrame, name, hostile)
            for name in method_names
        ] + [
            mock.patch.object(pd, name, hostile)
            for name in ("isna", "notna", "concat")
        ]

        for patcher in patches:
            patcher.start()
        try:
            self.assertTrue(dataframes.is_exact_dataframe(frame))
            result = dataframes.preview(frame)
        finally:
            for patcher in reversed(patches):
                patcher.stop()

        self.assertEqual([["alpha", "1"], ["beta", "2"]], result["rows"])
        self.assertEqual([], calls)

    def test_exact_dataframe_required_and_heap_subclass_properties_do_not_run(self):
        calls = []

        class HostileFrame(pd.DataFrame):
            @property
            def shape(self):
                calls.append("shape")
                raise AssertionError("subclass shape must not run")

            def __repr__(self):
                calls.append("repr")
                raise AssertionError("subclass repr must not run")

        value = HostileFrame({"a": [1]})

        self.assertFalse(dataframes.is_exact_dataframe(value))
        with self.assertRaisesRegex(ValueError, "exact loaded pandas DataFrame"):
            dataframes.preview(value)
        self.assertEqual([], calls)

    def test_multiindex_non_string_columns_and_unsafe_labels_are_bounded(self):
        class HostileLabel:
            def __str__(self):
                raise AssertionError("label __str__ must not run")

            def __repr__(self):
                raise AssertionError("label __repr__ must not run")

        columns = pd.MultiIndex.from_tuples(
            [("sales", 2026), (("nested", "name"), 7), (HostileLabel(), 9)]
        )
        index = pd.MultiIndex.from_tuples([("north", 1), ("south", 2)])
        frame = pd.DataFrame([[1, 2, 3], [4, 5, 6]], columns=columns, index=index)

        result = dataframes.preview(frame)

        self.assertEqual("(sales, 2026)", result["columns"][0]["name"])
        self.assertEqual("((nested, name), 7)", result["columns"][1]["name"])
        self.assertIn("HostileLabel", result["columns"][2]["name"])
        self.assertEqual(["(north, 1)", "(south, 2)"], result["indexLabels"])
        self.assertTrue(all(len(column["name"]) <= 256 for column in result["columns"]))

    def test_large_frame_preview_is_paged_without_full_serialization(self):
        import numpy as np

        row_total = 250_000
        frame = pd.DataFrame(
            {
                "sequence": np.arange(row_total, dtype=np.int64),
                "doubled": np.arange(row_total, dtype=np.int64) * 2,
                "constant": "bounded",
            }
        )

        result = dataframes.preview(
            frame,
            row_offset=row_total - 5,
            row_count=3,
            column_offset=1,
            column_count=1,
        )

        self.assertEqual(row_total, result["totalRows"])
        self.assertEqual(3, result["rowCount"])
        self.assertEqual(1, result["columnCount"])
        self.assertEqual(str((row_total - 5) * 2), result["rows"][0][0])
        self.assertTrue(result["rowsTruncated"])
        self.assertTrue(result["columnsTruncated"])
        self.assertLessEqual(len(result["rows"]) * len(result["columns"]), 3)

    def test_maximum_page_is_cell_bounded_and_fits_the_protocol_result_budget(self):
        long_value = "x" * 500
        frame = pd.DataFrame(
            {f"column-{index}": [long_value] * 200 for index in range(100)}
        )

        result = dataframes.preview(
            frame,
            row_count=dataframes.MAX_PREVIEW_ROWS,
            column_count=dataframes.MAX_PREVIEW_COLUMNS,
        )
        _, truncated = _bound_result(result)

        self.assertTrue(result["cellLimitApplied"])
        self.assertLessEqual(
            result["rowCount"] * result["columnCount"],
            dataframes.MAX_PREVIEW_CELLS,
        )
        self.assertFalse(truncated)

    def test_nullable_values_are_safe_and_column_dtypes_are_reported(self):
        frame = pd.DataFrame(
            {
                "integer": pd.array([1, None, 3], dtype="Int64"),
                "boolean": pd.array([True, None, False], dtype="boolean"),
                "text": pd.array(["a", None, "c"], dtype="string"),
            }
        )

        result = dataframes.preview(frame)

        self.assertEqual("1", result["rows"][0][0])
        self.assertEqual("<NA>", result["rows"][1][0])
        self.assertEqual("<NA>", result["rows"][1][1])
        self.assertEqual("<NA>", result["rows"][1][2])
        self.assertNotIn("<unavailable>", [column["dtype"] for column in result["columns"]])

    def test_manager_replacement_is_reported_as_a_volatile_snapshot(self):
        frame = pd.DataFrame({"a": [1, 2]})
        original_snapshot = dataframes._frame_snapshot

        def snapshot_then_mutate(value):
            snapshot = original_snapshot(value)
            value["late"] = [3, 4]
            return snapshot

        with mock.patch.object(dataframes, "_frame_snapshot", side_effect=snapshot_then_mutate):
            result = dataframes.preview(frame)

        self.assertTrue(result["mutationDetected"])
        self.assertFalse(result["snapshotConsistent"])
        self.assertEqual(1, result["totalColumns"])
        self.assertEqual(1, result["columnCount"])

    def test_bounded_sample_fingerprint_detects_in_place_cell_change(self):
        frame = pd.DataFrame({"a": [1, 2], "b": [3, 4]})

        before = self.inspector.summarize(frame)
        identity_before = before["identityToken"]
        frame.iat[0, 0] = 9
        after = self.inspector.summarize(frame)

        self.assertEqual(identity_before, after["identityToken"])
        self.assertNotEqual(before["metadataToken"], after["metadataToken"])
        self.assertNotEqual(before["changeToken"], after["changeToken"])

    def test_protocol_dispatch_exposes_describe_and_preview(self):
        frame = pd.DataFrame({"a": [1, 2], "b": [3, 4]})
        agent = InspectorAgent("127.0.0.1", 12345, "a" * 64, "cooperative")
        handle = agent._handles.put(frame)

        description, binary, detach = agent._dispatch(
            "dataframes.describe", {"handleId": handle})
        result, preview_binary, preview_detach = agent._dispatch(
            "dataframes.preview",
            {"handleId": handle, "rowCount": 1, "columnCount": 1},
        )

        self.assertEqual((2, 2), (description["totalRows"], description["totalColumns"]))
        self.assertEqual([["1"]], result["rows"])
        self.assertEqual(b"", binary)
        self.assertEqual(b"", preview_binary)
        self.assertFalse(detach)
        self.assertFalse(preview_detach)

    def test_preview_limits_are_validated_before_reading_cells(self):
        frame = pd.DataFrame({"a": [1]})

        with self.assertRaisesRegex(ValueError, "rowCount"):
            dataframes.preview(frame, row_count=dataframes.MAX_PREVIEW_ROWS + 1)
        with self.assertRaisesRegex(ValueError, "columnCount"):
            dataframes.preview(frame, column_count=dataframes.MAX_PREVIEW_COLUMNS + 1)
        with self.assertRaisesRegex(ValueError, "rowOffset"):
            dataframes.preview(frame, row_offset=-1)
