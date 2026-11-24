import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app import config
from app.services.terrain import (
    CTB_IMAGE,
    SELF_NAME,
    _build_ctb_command,
    resolve_terrain_job_assets,
)


class TerrainCommandTests(unittest.TestCase):
    def test_ctb_uses_same_bilinear_sampling_with_high_quality_mesh(self):
        source = Path("/data/input/dem.tif")
        output = Path("/data/output/tiles")

        with patch.object(config, "TERRAIN_RESAMPLING_METHOD", "bilinear"), \
             patch.object(config, "TERRAIN_MESH_QFACTOR", "0.01"):
            command = _build_ctb_command(source, output, 12)

        self.assertEqual(command[:6], [
            "docker", "run", "--rm", f"--volumes-from={SELF_NAME}",
            CTB_IMAGE, "ctb-tile",
        ])
        self.assertEqual(command[command.index("-r") + 1], "bilinear")
        self.assertEqual(command[command.index("-g") + 1], "0.01")
        self.assertEqual(command[command.index("-s") + 1], "12")

    def test_completed_job_resolves_source_dem_and_local_layer(self):
        job_id = "a" * 32
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as temporary:
            root = Path(temporary)
            job_dir = root / job_id
            input_dir = job_dir / "input"
            tiles_dir = job_dir / "tiles"
            input_dir.mkdir(parents=True)
            tiles_dir.mkdir()
            dem_path = input_dir / "dem.tif"
            dem_path.write_bytes(b"dem")
            (job_dir / "status.json").write_text(
                json.dumps({"status": "completed"}), encoding="utf-8",
            )
            layer_path = tiles_dir / "layer.json"
            layer_path.write_text(json.dumps({
                "format": "quantized-mesh-1.0",
                "tiles": ["http://server/terrain/job/{z}/{x}/{y}.terrain"],
            }), encoding="utf-8")

            with patch.object(config, "TERRAIN_DIR", root):
                resolved_dem, resolved_layer = resolve_terrain_job_assets(job_id)

            self.assertEqual(resolved_dem, dem_path)
            self.assertEqual(resolved_layer, layer_path)
            layer = json.loads(layer_path.read_text(encoding="utf-8"))
            self.assertEqual(layer["tiles"], ["{z}/{x}/{y}.terrain"])

    def test_terrain_job_must_be_completed(self):
        job_id = "b" * 32
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as temporary:
            root = Path(temporary)
            job_dir = root / job_id
            job_dir.mkdir()
            (job_dir / "status.json").write_text(
                json.dumps({"status": "running"}), encoding="utf-8",
            )
            with patch.object(config, "TERRAIN_DIR", root):
                with self.assertRaisesRegex(ValueError, "not completed"):
                    resolve_terrain_job_assets(job_id)

    def test_terrain_job_id_rejects_paths(self):
        with self.assertRaisesRegex(ValueError, "32-character hexadecimal"):
            resolve_terrain_job_assets("../terrain/job")


if __name__ == "__main__":
    unittest.main()
