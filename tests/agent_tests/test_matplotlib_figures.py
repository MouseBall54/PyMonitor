import os
import subprocess
import sys
import types
import unittest
from unittest import mock

from pyruntime_inspector_agent import matplotlib_figures
from pyruntime_inspector_agent.handles import HandleStore
from pyruntime_inspector_agent.safe_objects import SafeObjectInspector
from pyruntime_inspector_agent.server import InspectorAgent


class MatplotlibFigureTests(unittest.TestCase):
    def setUp(self):
        self.handles = HandleStore(max_entries=64)
        self.inspector = SafeObjectInspector(self.handles)

    def test_importing_agent_does_not_import_matplotlib_or_numpy(self):
        environment = dict(os.environ)
        environment["PYTHONPATH"] = os.path.abspath("agent")
        output = subprocess.check_output(
            [
                sys.executable,
                "-B",
                "-c",
                "import sys; import pyruntime_inspector_agent; "
                "print('matplotlib' in sys.modules, 'numpy' in sys.modules)",
            ],
            cwd=os.path.abspath("."),
            env=environment,
            text=True,
        )
        self.assertEqual("False False", output.strip())

    def test_fake_modules_and_subclasses_are_rejected_without_property_reads(self):
        calls = []

        class FakeFigure:
            __module__ = "matplotlib.figure"

            @property
            def canvas(self):
                calls.append("canvas")
                raise AssertionError("fake Figure property must not run")

        FakeFigure.__name__ = FakeFigure.__qualname__ = "Figure"
        fake_figure_module = types.ModuleType("matplotlib.figure")
        fake_figure_module.Figure = FakeFigure
        fake_axes_package = types.ModuleType("matplotlib.axes")
        fake_axes_module = types.ModuleType("matplotlib.axes._axes")
        fake_axes_module.Axes = type("Axes", (), {"__module__": "matplotlib.axes._axes"})
        fake_axes_package.Axes = fake_axes_module.Axes

        with mock.patch.dict(sys.modules, {
            "matplotlib.figure": fake_figure_module,
            "matplotlib.axes": fake_axes_package,
            "matplotlib.axes._axes": fake_axes_module,
        }):
            self.assertIsNone(matplotlib_figures.adapter_kind(FakeFigure()))

        Figure, _, FigureCanvasAgg, _, _ = self._matplotlib_types()

        class FigureSubclass(Figure):
            pass

        self.assertIsNone(matplotlib_figures.adapter_kind(FigureSubclass()))
        self.assertEqual([], calls)

    def test_hostile_module_metadata_and_canvas_metaclass_equality_never_run(self):
        calls = []

        class HostileText:
            def __eq__(self, other):
                calls.append(("module-name-equality", other))
                raise AssertionError("target module metadata equality must not run")

        fake_figure_module = types.ModuleType("matplotlib.figure")
        vars(fake_figure_module)["__name__"] = HostileText()
        with mock.patch.dict(sys.modules, {"matplotlib.figure": fake_figure_module}):
            self.assertIsNone(matplotlib_figures.adapter_kind(object()))

        class HostileMeta(type):
            def __eq__(self, other):
                calls.append(("canvas-type-equality", other))
                raise AssertionError("target metaclass equality must not run")

        class HostileCanvas(metaclass=HostileMeta):
            pass

        figure, _ = self._new_agg_figure()
        vars(figure)["canvas"] = HostileCanvas()
        description = matplotlib_figures.describe(figure)

        self.assertEqual("non-agg", description["availability"]["reason"])
        self.assertEqual([], calls)

    def test_fake_backend_core_cannot_trigger_python_buffer_callback(self):
        calls = []

        class FakeCoreRenderer:
            __module__ = "matplotlib.backends._backend_agg"

            def __buffer__(self, flags):
                calls.append(flags)
                raise AssertionError("an unverified target buffer callback must not run")

        FakeCoreRenderer.__name__ = FakeCoreRenderer.__qualname__ = "RendererAgg"
        fake_core_module = types.ModuleType("matplotlib.backends._backend_agg")
        fake_core_module.RendererAgg = FakeCoreRenderer

        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [0, 1])
        figure.canvas.draw()
        renderer = vars(figure.canvas)["renderer"]
        vars(renderer)["_renderer"] = FakeCoreRenderer()

        with mock.patch.dict(sys.modules, {
            "matplotlib.backends._backend_agg": fake_core_module,
        }):
            description = matplotlib_figures.describe(figure)

        self.assertFalse(description["previewAvailable"])
        self.assertEqual([], calls)

        _, _, _, _, real_core_type = self._matplotlib_types()
        real_metaclass = type(real_core_type)
        real_pybind_base = real_core_type.__mro__[1]
        spoofed_core_type = real_metaclass(
            "RendererAgg",
            (real_pybind_base,),
            {
                "__module__": "matplotlib.backends._backend_agg",
                "__init__": list.append,
                "draw_path": list.append,
                "__buffer__": lambda self, flags: calls.append(flags),
            },
        )
        self.assertFalse(matplotlib_figures._valid_core_renderer_type(spoofed_core_type))
        self.assertEqual([], calls)

    def test_tight_layout_is_stale_and_not_a_completed_render(self):
        figure, axes = self._new_agg_figure()
        not_rendered = matplotlib_figures.describe(figure)
        self.assertEqual("not-rendered", not_rendered["availability"]["reason"])

        axes.plot([0, 1], [1, 0])
        figure.tight_layout()

        description = matplotlib_figures.describe(figure)
        preview, binary = matplotlib_figures.preview(figure)
        self.assertTrue(description["stale"])
        self.assertFalse(description["previewAvailable"])
        self.assertEqual("stale", description["availability"]["reason"])
        self.assertEqual("stale", preview["availability"]["reason"])
        self.assertEqual(b"", binary)

    def test_drawn_figure_and_axes_return_same_bounded_owning_figure_bgra(self):
        figure, axes = self._new_agg_figure(figsize=(6, 4))
        axes.plot([0, 1], [1, 0])
        figure.canvas.draw()

        figure_metadata, figure_binary = matplotlib_figures.preview(figure, 64, 64)
        axes_metadata, axes_binary = matplotlib_figures.preview(axes, 64, 64)

        self.assertEqual("ready", figure_metadata["availability"]["state"])
        self.assertEqual("Figure", figure_metadata["sourceKind"])
        self.assertEqual("Axes", axes_metadata["sourceKind"])
        self.assertTrue(axes_metadata["axesUsesOwningFigure"])
        self.assertEqual("Figure", axes_metadata["renderedKind"])
        self.assertEqual(figure_metadata["figureAddressHex"], axes_metadata["figureAddressHex"])
        self.assertEqual(figure_binary, axes_binary)
        self.assertEqual(figure_metadata["width"] * figure_metadata["height"] * 4, len(figure_binary))
        self.assertLessEqual(figure_metadata["width"], 64)
        self.assertLessEqual(figure_metadata["height"], 64)
        self.assertEqual("BGRA32", figure_metadata["pixelFormat"])

        renderer = vars(figure.canvas)["renderer"]
        raw = memoryview(vars(renderer)["_renderer"]).cast("B")
        self.assertEqual(bytes((raw[2], raw[1], raw[0], raw[3])), figure_binary[:4])

    def test_adapter_never_calls_draw_buffer_methods_axes_getter_or_properties(self):
        Figure, Axes, FigureCanvasAgg, RendererAgg, _ = self._matplotlib_types()
        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [0, 1])
        figure.canvas.draw()

        def hostile(*args, **kwargs):
            raise AssertionError("Matplotlib draw, getter, or callback code must not run")

        with (
            mock.patch.dict(vars(figure.canvas), {"draw": hostile}),
            mock.patch.object(FigureCanvasAgg, "buffer_rgba", hostile),
            mock.patch.object(RendererAgg, "buffer_rgba", hostile),
            mock.patch.object(Axes, "get_figure", hostile),
            mock.patch.object(Figure, "draw", hostile),
        ):
            figure_metadata, figure_binary = matplotlib_figures.preview(figure, 32, 32)
            axes_metadata, axes_binary = matplotlib_figures.preview(axes, 32, 32)

        self.assertTrue(figure_metadata["previewAvailable"])
        self.assertTrue(axes_metadata["previewAvailable"])
        self.assertEqual(figure_binary, axes_binary)

    def test_non_agg_and_detached_axes_report_structured_unavailable_states(self):
        Figure, _, _, _, _ = self._matplotlib_types()
        non_agg = Figure()
        self.assertEqual("non-agg", matplotlib_figures.describe(non_agg)["availability"]["reason"])

        figure, axes = self._new_agg_figure()
        vars(axes)["_parent_figure"] = None
        detached = matplotlib_figures.describe(axes)
        self.assertEqual("unavailable", detached["availability"]["state"])
        self.assertEqual("detached-axes", detached["availability"]["reason"])
        self.assertTrue(detached["axesUsesOwningFigure"])

    def test_preview_is_bounded_to_1024_and_four_mebibytes(self):
        figure, _ = self._new_agg_figure(figsize=(20.48, 20.48), dpi=100)
        figure.canvas.draw()

        for invalid in (True, 0, 1025, "1024"):
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValueError):
                    matplotlib_figures.preview(figure, max_width=invalid)

        metadata, binary = matplotlib_figures.preview(figure)

        self.assertEqual((2048, 2048), (metadata["sourceWidth"], metadata["sourceHeight"]))
        self.assertEqual((1024, 1024), (metadata["width"], metadata["height"]))
        self.assertEqual((2, 2), (metadata["rowStep"], metadata["columnStep"]))
        self.assertEqual(matplotlib_figures.MAX_PREVIEW_BYTES, len(binary))

    def test_stale_render_never_returns_old_pixels(self):
        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [0, 1])
        figure.canvas.draw()
        before_metadata, before_binary = matplotlib_figures.preview(figure)
        self.assertTrue(before_metadata["previewAvailable"])
        self.assertTrue(before_binary)

        axes.set_title("pending change")
        stale_metadata, stale_binary = matplotlib_figures.preview(figure)

        self.assertEqual("stale", stale_metadata["availability"]["reason"])
        self.assertEqual(b"", stale_binary)

    def test_buffer_mutation_during_copy_is_rejected(self):
        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [0, 1])
        figure.canvas.draw()
        original_copy = matplotlib_figures._copy_bgra

        def copy_then_mutate(snapshot, *args):
            output = original_copy(snapshot, *args)
            raw = snapshot["view"].cast("B")
            raw[0] = (raw[0] + 1) % 256
            return output

        with mock.patch.object(matplotlib_figures, "_copy_bgra", copy_then_mutate):
            metadata, binary = matplotlib_figures.preview(figure)

        self.assertEqual("buffer-changed", metadata["availability"]["reason"])
        self.assertFalse(metadata["snapshotConsistent"])
        self.assertEqual(b"", binary)

    def test_safe_summary_and_change_token_follow_render_state_and_pixels(self):
        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [0, 1])
        before = self.inspector.summarize(figure)
        self.assertEqual("matplotlib.Figure", before["adapterKind"])
        self.assertEqual("unavailable", before["renderState"])
        self.assertIn("preview unavailable", before["safePreview"])

        figure.canvas.draw()
        rendered = self.inspector.summarize(figure)
        self.assertEqual(before["handleId"], rendered["handleId"])
        self.assertEqual([400, 600, 4], rendered["shape"])
        self.assertEqual("uint8", rendered["dtype"])
        self.assertEqual(400 * 600 * 4, rendered["payloadSizeBytes"])
        self.assertNotEqual(before["metadataToken"], rendered["metadataToken"])

        axes.set_title("changed")
        stale = self.inspector.summarize(figure)
        self.assertEqual("stale", stale["renderUnavailableReason"])
        self.assertNotEqual(rendered["metadataToken"], stale["metadataToken"])

        figure.canvas.draw()
        redrawn = self.inspector.summarize(figure)
        self.assertEqual("ready", redrawn["renderState"])
        self.assertNotEqual(stale["metadataToken"], redrawn["metadataToken"])
        self.assertNotEqual(rendered["metadataToken"], redrawn["metadataToken"])

    def test_server_dispatches_describe_and_binary_preview(self):
        figure, axes = self._new_agg_figure()
        axes.plot([0, 1], [1, 0])
        figure.canvas.draw()
        agent = InspectorAgent("127.0.0.1", 49152, "a" * 64, "cooperative")
        handle = agent._objects.summarize(figure)["handleId"]

        description, description_binary, detach = agent._dispatch(
            "figures.describe", {"handleId": handle})
        preview, preview_binary, preview_detach = agent._dispatch(
            "figures.preview", {"handleId": handle, "maxWidth": 48, "maxHeight": 48})

        self.assertTrue(description["previewAvailable"])
        self.assertEqual(b"", description_binary)
        self.assertFalse(detach)
        self.assertTrue(preview["previewAvailable"])
        self.assertEqual(preview["width"] * preview["height"] * 4, len(preview_binary))
        self.assertFalse(preview_detach)

        axes.set_xlabel("pending")
        unavailable, unavailable_binary, _ = agent._dispatch(
            "figures.preview", {"handleId": handle})
        self.assertEqual("stale", unavailable["availability"]["reason"])
        self.assertEqual(b"", unavailable_binary)

    def _new_agg_figure(self, figsize=(6, 4), dpi=100):
        Figure, _, FigureCanvasAgg, _, _ = self._matplotlib_types()
        figure = Figure(figsize=figsize, dpi=dpi)
        FigureCanvasAgg(figure)
        return figure, figure.subplots()

    def _matplotlib_types(self):
        try:
            from matplotlib.axes._axes import Axes
            from matplotlib.backends._backend_agg import RendererAgg as CoreRendererAgg
            from matplotlib.backends.backend_agg import FigureCanvasAgg, RendererAgg
            from matplotlib.figure import Figure
        except ImportError:
            self.skipTest("Matplotlib with the Agg backend is not installed.")
        return Figure, Axes, FigureCanvasAgg, RendererAgg, CoreRendererAgg


if __name__ == "__main__":
    unittest.main()
