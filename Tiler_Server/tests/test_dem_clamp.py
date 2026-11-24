"""Run with GDAL's system Python: python -m unittest discover -s tests -v."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

import numpy as np
from osgeo import gdal, ogr, osr

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.services.dem_clamp_worker import prepare
from app.services.tiler import TilerOptions, prepare_clamp_terrain, prepared_input_dir
from app.services.tiler import build_mago_command, find_tileset
from app import config


class DemClampTests(unittest.TestCase):
    def setUp(self):
        osr.UseExceptions()
        test_dir = Path(__file__).resolve().parent
        self.temporary = tempfile.TemporaryDirectory(prefix="dem_clamp_test_", dir=test_dir)
        self.root = Path(self.temporary.name).resolve()
        assert self.root.is_relative_to(test_dir)
        self.addCleanup(self.temporary.cleanup)
        self.dem = self.root / "terrain.tif"
        self.shp = self.root / "buildings.shp"
        self.prepared = self.root / "prepared" / "terrain.tif"
        self.report = self.root / "output" / "dem_clamp_report.json"

    def make_dem(self, crs=4326, nodata_left=False, scale=None):
        srs = osr.SpatialReference()
        srs.ImportFromEPSG(crs)
        srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        gt = (127, 0.0001, 0, 36.001, 0, -0.0001)
        if crs != 4326:
            geographic = osr.SpatialReference()
            geographic.ImportFromEPSG(4326)
            geographic.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
            x, y, _ = osr.CoordinateTransformation(geographic, srs).TransformPoint(127, 36.001)
            gt = (x, 9, 0, y, 0, -11)
        dataset = gdal.GetDriverByName("GTiff").Create(str(self.dem), 10, 10, 1, gdal.GDT_Float32)
        dataset.SetGeoTransform(gt)
        dataset.SetProjection(srs.ExportToWkt())
        values = np.full((10, 10), 100, dtype=np.float32)
        values[:, 5:] = 200
        if nodata_left:
            values[:, :5] = -9999
        band = dataset.GetRasterBand(1)
        band.SetNoDataValue(-9999)
        if scale is not None:
            band.SetScale(scale)
        band.WriteArray(values)
        dataset = None

    def make_buildings(self, crs=4326, outside=False, include_outside=False, crossing=False):
        geographic = osr.SpatialReference()
        geographic.ImportFromEPSG(4326)
        geographic.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        srs = osr.SpatialReference()
        srs.ImportFromEPSG(crs)
        srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        transform = osr.CoordinateTransformation(geographic, srs)
        dataset = ogr.GetDriverByName("ESRI Shapefile").CreateDataSource(str(self.shp))
        layer = dataset.CreateLayer("buildings", srs, ogr.wkbPolygon)
        layer.CreateField(ogr.FieldDefn("height", ogr.OFTReal))
        locations = [(128, .00002)] if outside else [(127.00025, .00002), (127.00075, .00002)]
        if include_outside:
            locations.append((128, .00002))
        if crossing:
            locations.append((127.00094, .00004))
        for lon, half_size in locations:
            lat = 36.00055
            ring = ogr.Geometry(ogr.wkbLinearRing)
            for dx, dy in [
                (-half_size, -half_size), (half_size, -half_size),
                (half_size, half_size), (-half_size, half_size),
                (-half_size, -half_size),
            ]:
                ring.AddPoint_2D(lon + dx, lat + dy)
            geometry = ogr.Geometry(ogr.wkbPolygon)
            geometry.AddGeometry(ring)
            geometry.Transform(transform)
            feature = ogr.Feature(layer.GetLayerDefn())
            feature.SetField("height", 12)
            feature.SetGeometry(geometry)
            layer.CreateFeature(feature)
        feature = None
        layer = None
        dataset = None

    def run_prepare(self, crs=4326):
        return prepare(self.dem, self.shp, str(crs), self.prepared, self.report)

    def test_buildings_get_different_ground_heights(self):
        self.make_dem()
        self.make_buildings()
        original_dem = self.dem.read_bytes()
        original_shp = self.shp.read_bytes()
        report = self.run_prepare()
        samples = [json.loads(line) for line in self.report.with_name("dem_clamp_samples.jsonl").read_text().splitlines()]
        self.assertEqual(report["valid_count"], 2)
        self.assertAlmostEqual(samples[0]["dem_height"], 100, places=3)
        self.assertAlmostEqual(samples[1]["dem_height"], 200, places=3)
        self.assertEqual(self.dem.read_bytes(), original_dem)
        self.assertEqual(self.shp.read_bytes(), original_shp)

    def test_projected_dem_and_different_shp_crs(self):
        self.make_dem(crs=5179)
        self.make_buildings(crs=5186)
        report = self.run_prepare(crs=5186)
        self.assertEqual(report["status"], "ready")
        self.assertAlmostEqual(report["min_ground_height"], 100, places=2)
        self.assertAlmostEqual(report["max_ground_height"], 200, places=2)

    def test_nodata_features_are_excluded_instead_of_placing_at_zero(self):
        self.make_dem(nodata_left=True)
        self.make_buildings()
        report = self.run_prepare()
        self.assertEqual(report["status"], "ready")
        self.assertEqual(report["invalid_count"], 0)
        self.assertEqual(report["valid_count"], 1)
        self.assertEqual(report["feature_count"], 1)
        self.assertEqual(report["clipping"]["nodata_dropped_feature_count"], 1)
        clipped = self.prepared.parent / "clipped" / self.shp.name
        dataset = ogr.Open(str(clipped), 0)
        self.assertEqual(dataset.GetLayer(0).GetFeatureCount(), 1)
        dataset = None

    def test_outside_coverage_fails(self):
        self.make_dem()
        self.make_buildings(outside=True)
        with self.assertRaisesRegex(ValueError, "No SHP features overlap"):
            self.run_prepare()

    def test_larger_shp_is_clipped_to_dem_coverage(self):
        self.make_dem()
        self.make_buildings(include_outside=True, crossing=True)
        original_shp = self.shp.read_bytes()

        report = self.run_prepare()

        clipping = report["clipping"]
        self.assertEqual(report["status"], "ready")
        self.assertTrue(clipping["applied"])
        self.assertEqual(clipping["source_feature_count"], 4)
        self.assertEqual(clipping["output_feature_count"], 3)
        self.assertEqual(clipping["clipped_feature_count"], 1)
        self.assertEqual(clipping["dropped_feature_count"], 1)
        self.assertEqual(report["feature_count"], 3)
        self.assertEqual(self.shp.read_bytes(), original_shp)

        clipped = self.prepared.parent / "clipped" / self.shp.name
        dataset = ogr.Open(str(clipped), 0)
        layer = dataset.GetLayer(0)
        xmin, xmax, ymin, ymax = layer.GetExtent()
        coverage = clipping["coverage_bounds"]
        self.assertGreaterEqual(xmin, coverage[0] - 1e-9)
        self.assertGreaterEqual(ymin, coverage[1] - 1e-9)
        self.assertLessEqual(xmax, coverage[2] + 1e-9)
        self.assertLessEqual(ymax, coverage[3] + 1e-9)
        dataset = None

    def test_scaled_dem_is_not_used_as_raw_metres(self):
        self.make_dem(scale=.1)
        self.make_buildings()
        with self.assertRaisesRegex(ValueError, "scale/offset"):
            self.run_prepare()

    def test_without_dem_keeps_existing_conversion(self):
        self.assertIsNone(prepare_clamp_terrain(self.root, self.root / "output", TilerOptions(), None))
        self.assertEqual(prepared_input_dir(self.root, None), self.root)

    def test_mago_command_accepts_quantized_mesh_layer(self):
        layer = self.root / "terrain" / "layer.json"
        command = build_mago_command(self.root, self.root / "output", TilerOptions(), layer)
        self.assertEqual(command[command.index("-te") + 1], str(layer))

    @unittest.skipUnless(os.getenv("DEM_CLAMP_TEST_JAR"), "Set DEM_CLAMP_TEST_JAR for actual Mago conversion")
    def test_generated_tiles_reflect_both_ground_heights(self):
        self.make_dem(crs=5179)
        self.make_buildings(crs=5186)
        options = TilerOptions(coordinate_code="5186", height_column="height")
        with patch.object(config, "GDAL_PYTHON", sys.executable), \
             patch.object(config, "MAGO_TILER_JAR", Path(os.environ["DEM_CLAMP_TEST_JAR"])), \
             patch.object(config, "JAVA_XMS", "64m"), patch.object(config, "JAVA_XMX", "512m"):
            prepared = prepare_clamp_terrain(self.root, self.report.parent, options, self.dem)
            clamped_input = prepared_input_dir(self.root, prepared)
            bounds = []
            for name, input_dir, terrain in (
                ("baseline", self.root, None),
                ("clamped", clamped_input, prepared),
            ):
                output = self.root / name
                output.mkdir()
                command = build_mago_command(input_dir, output, options, terrain)
                command[0] = os.getenv("DEM_CLAMP_TEST_JAVA", "java")
                result = subprocess.run(command, capture_output=True, text=True,
                                        encoding="utf-8", errors="replace", timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                tileset = json.loads(find_tileset(output).read_text(encoding="utf-8"))
                self.assertTrue(list(output.rglob("*.b3dm")) or list(output.rglob("*.glb")), result.stdout)
                bounds.append(tileset["root"]["boundingVolume"]["region"][4:6])
            # Two identical-height buildings: DEM must lift the low/high ends by
            # 100m/200m respectively, rather than lifting the entire tile uniformly.
            self.assertAlmostEqual(bounds[1][0] - bounds[0][0], 100, delta=1)
            self.assertAlmostEqual(bounds[1][1] - bounds[0][1], 200, delta=1)


if __name__ == "__main__":
    unittest.main()
