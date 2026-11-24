"""DEM preparation for Mago, run with the system Python providing GDAL bindings."""
import argparse
import json
import math
from pathlib import Path

from osgeo import gdal, ogr, osr


class DemSampleUnavailable(ValueError):
    """A feature cannot be clamped because the DEM has no valid sample for it."""


def _spatial_reference(value: str) -> osr.SpatialReference:
    spatial_reference = osr.SpatialReference()
    spatial_reference.SetFromUserInput(value if ":" in value else f"EPSG:{value}")
    spatial_reference.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    return spatial_reference


def _bilinear_coverage(dataset) -> tuple[ogr.Geometry, list[float]]:
    """Return the raster area where the current 2x2 bilinear sampler is valid."""
    if dataset.RasterXSize < 2 or dataset.RasterYSize < 2:
        raise ValueError("DEM must be at least 2x2 pixels for bilinear sampling")
    transform = dataset.GetGeoTransform()
    pixel_corners = (
        (0.5, 0.5),
        (dataset.RasterXSize - 0.5, 0.5),
        (dataset.RasterXSize - 0.5, dataset.RasterYSize - 0.5),
        (0.5, dataset.RasterYSize - 0.5),
        (0.5, 0.5),
    )
    ring = ogr.Geometry(ogr.wkbLinearRing)
    coordinates: list[tuple[float, float]] = []
    for pixel, line in pixel_corners:
        x, y = gdal.ApplyGeoTransform(transform, pixel, line)
        ring.AddPoint_2D(x, y)
        coordinates.append((x, y))
    polygon = ogr.Geometry(ogr.wkbPolygon)
    polygon.AddGeometry(ring)
    spatial_reference = dataset.GetSpatialRef()
    if spatial_reference is None:
        raise ValueError("Prepared DEM has no coordinate system")
    spatial_reference = spatial_reference.Clone()
    spatial_reference.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    polygon.AssignSpatialReference(spatial_reference)
    xs = [point[0] for point in coordinates]
    ys = [point[1] for point in coordinates]
    return polygon, [min(xs), min(ys), max(xs), max(ys)]


def _copy_field_definitions(source_layer, target_layer) -> None:
    definition = source_layer.GetLayerDefn()
    for index in range(definition.GetFieldCount()):
        source_field = definition.GetFieldDefn(index)
        target_field = ogr.FieldDefn(source_field.GetNameRef(), source_field.GetType())
        target_field.SetSubType(source_field.GetSubType())
        width = source_field.GetWidth()
        if source_field.GetType() == ogr.OFTInteger:
            width = max(width, 11)
        elif source_field.GetType() == ogr.OFTInteger64:
            width = max(width, 20)
        elif source_field.GetType() == ogr.OFTReal:
            width = max(width, 24)
        target_field.SetWidth(width)
        target_field.SetPrecision(source_field.GetPrecision())
        if target_layer.CreateField(target_field) != ogr.OGRERR_NONE:
            raise ValueError(f"Failed to copy SHP field {source_field.GetName()}")


def _clip_shapefile(shp_path: Path, coordinate_code: str, coverage: ogr.Geometry,
                    clipped_path: Path) -> dict:
    source_dataset = ogr.Open(str(shp_path), 0)
    if source_dataset is None:
        raise ValueError("Cannot read SHP for DEM clipping")
    source_layer = source_dataset.GetLayer(0)
    source_srs = _spatial_reference(coordinate_code)
    coverage_srs = coverage.GetSpatialReference()
    if coverage_srs is None:
        raise ValueError("DEM coverage has no coordinate system")

    clip_geometry = coverage.Clone()
    clip_geometry.Transform(osr.CoordinateTransformation(coverage_srs, source_srs))

    clipped_path.parent.mkdir(parents=True, exist_ok=True)
    driver = ogr.GetDriverByName("ESRI Shapefile")
    if clipped_path.exists():
        driver.DeleteDataSource(str(clipped_path))
    target_dataset = driver.CreateDataSource(str(clipped_path))
    if target_dataset is None:
        raise ValueError("Failed to create clipped SHP")
    target_layer = target_dataset.CreateLayer(
        clipped_path.stem, source_srs, source_layer.GetGeomType(),
        options=["ENCODING=UTF-8"],
    )
    if target_layer is None:
        raise ValueError("Failed to create clipped SHP layer")
    _copy_field_definitions(source_layer, target_layer)

    source_count = 0
    output_count = 0
    clipped_count = 0
    dropped_count = 0
    target_definition = target_layer.GetLayerDefn()
    for feature in source_layer:
        source_count += 1
        geometry = feature.GetGeometryRef()
        if geometry is None or geometry.IsEmpty():
            dropped_count += 1
            continue
        geometry = geometry.Clone()
        try:
            intersection = geometry.Intersection(clip_geometry)
        except RuntimeError as exc:
            raise ValueError(f"Failed to clip SHP feature {feature.GetFID()}: {exc}") from exc
        if intersection is None or intersection.IsEmpty():
            dropped_count += 1
            continue
        if not geometry.Within(clip_geometry):
            clipped_count += 1

        target_feature = ogr.Feature(target_definition)
        if target_feature.SetFrom(feature) != ogr.OGRERR_NONE:
            raise ValueError(f"Failed to copy SHP attributes for feature {feature.GetFID()}")
        target_feature.SetGeometry(intersection)
        if target_layer.CreateFeature(target_feature) != ogr.OGRERR_NONE:
            raise ValueError(f"Failed to write clipped SHP feature {feature.GetFID()}")
        output_count += 1
        target_feature = None

    target_layer.SyncToDisk()
    target_layer = None
    target_dataset = None
    source_layer = None
    source_dataset = None
    if output_count == 0:
        raise ValueError("No SHP features overlap the DEM bilinear sampling coverage")
    return {
        "applied": bool(clipped_count or dropped_count),
        "source_feature_count": source_count,
        "output_feature_count": output_count,
        "clipped_feature_count": clipped_count,
        "dropped_feature_count": dropped_count,
        "output_shp": str(clipped_path),
    }


def sample_height(dataset, lon: float, lat: float) -> float:
    """Bilinear height at pixel centres; missing neighbours are never treated as 0m."""
    inverse = gdal.InvGeoTransform(dataset.GetGeoTransform())
    if inverse is None:
        raise ValueError("DEM geotransform is not invertible")
    pixel, line = gdal.ApplyGeoTransform(inverse, lon, lat)
    x, y = pixel - 0.5, line - 0.5
    col, row = math.floor(x), math.floor(y)
    if col < 0 or row < 0 or col + 1 >= dataset.RasterXSize or row + 1 >= dataset.RasterYSize:
        raise DemSampleUnavailable("Outside DEM bilinear sampling coverage")
    band = dataset.GetRasterBand(1)
    values = band.ReadAsArray(col, row, 2, 2)
    mask = band.GetMaskBand().ReadAsArray(col, row, 2, 2)
    nodata = band.GetNoDataValue()
    dx, dy = x - col, y - row
    weights = ((1-dx)*(1-dy), dx*(1-dy), (1-dx)*dy, dx*dy)
    height = 0.0
    for (r, c), weight in zip(((0, 0), (0, 1), (1, 0), (1, 1)), weights):
        if weight <= 1e-12:
            continue
        value = float(values[r, c])
        if not mask[r, c] or not math.isfinite(value) or (nodata is not None and value == nodata):
            raise DemSampleUnavailable("DEM sample contains NoData or a non-finite height")
        height += value * weight
    return height


def _delete_features(shp_path: Path, feature_ids: list[int]) -> None:
    if not feature_ids:
        return
    dataset = ogr.Open(str(shp_path), 1)
    if dataset is None:
        raise ValueError("Cannot reopen clipped SHP to remove invalid DEM samples")
    layer = dataset.GetLayer(0)
    for feature_id in feature_ids:
        if layer.DeleteFeature(feature_id) != ogr.OGRERR_NONE:
            raise ValueError(f"Failed to remove SHP feature {feature_id} without a DEM sample")
    layer.SyncToDisk()
    layer = None
    dataset = None


def prepare(dem_path: Path, shp_path: Path, coordinate_code: str,
            prepared_path: Path, report_path: Path,
            clipped_path: Path | None = None) -> dict:
    gdal.UseExceptions()
    ogr.UseExceptions()
    osr.UseExceptions()
    source = gdal.Open(str(dem_path), gdal.GA_ReadOnly)
    if source is None or source.RasterCount != 1:
        raise ValueError("DEM must be a readable single-band elevation raster")
    dem_srs = source.GetSpatialRef()
    if dem_srs is None or not (dem_srs.IsProjected() or dem_srs.IsGeographic()):
        raise ValueError("DEM must contain a geographic or projected coordinate system")
    band = source.GetRasterBand(1)
    unit = band.GetUnitType().lower().strip()
    if unit not in ("", "m", "meter", "meters", "metre", "metres"):
        raise ValueError(f"DEM elevation unit must be metres, got {unit!r}")
    if band.GetScale() not in (None, 1.0) or band.GetOffset() not in (None, 0.0):
        raise ValueError("DEM scale/offset must be applied to its elevation values before upload")

    prepared_path.parent.mkdir(parents=True, exist_ok=True)
    # Mago's terrain sampler receives longitude/latitude. Only transform horizontal CRS;
    # a vertical datum conversion needs an explicitly chosen datum/geoid, not a guess.
    normalized = gdal.Warp(str(prepared_path), source, options=gdal.WarpOptions(
        options=["-novshift"], dstSRS="EPSG:4326", resampleAlg="bilinear",
        outputType=gdal.GDT_Float32, dstNodata=-9999,
        creationOptions=["COMPRESS=DEFLATE"],
    ))
    if normalized is None:
        raise ValueError("Failed to prepare DEM in EPSG:4326")
    normalized.FlushCache()

    coverage, coverage_bounds = _bilinear_coverage(normalized)
    if clipped_path is None:
        clipped_path = prepared_path.parent / "clipped" / shp_path.name
    clipping = _clip_shapefile(shp_path, coordinate_code, coverage, clipped_path)

    shp = ogr.Open(str(clipped_path), 0)
    if shp is None:
        raise ValueError("Cannot read clipped SHP for DEM clamping")
    layer = shp.GetLayer(0)
    source_srs = _spatial_reference(coordinate_code)
    target_srs = _spatial_reference("EPSG:4326")
    transform = osr.CoordinateTransformation(source_srs, target_srs)

    report = {
        "status": "validating", "source_dem": dem_path.name,
        "source_dem_crs": dem_srs.ExportToWkt(), "source_shp_crs": coordinate_code,
        "prepared_dem_crs": "EPSG:4326", "elevation_unit": "metres",
        "source_elevation_unit": unit or "unspecified", "metres_assumed": not bool(unit),
        "vertical_datum_conversion": False,
        "sampling": "bilinear at transformed feature envelope centre (Mago anchor)",
        "clipping": {
            **clipping,
            "nodata_dropped_feature_count": 0,
            "coverage_crs": "EPSG:4326",
            "coverage_bounds": coverage_bounds,
            "coverage_kind": "bilinear-safe raster footprint",
        },
        "feature_count": 0, "valid_count": 0, "invalid_count": 0,
        "min_ground_height": None, "max_ground_height": None,
        "invalid_examples": [], "excluded_nodata_examples": [],
    }
    samples_path = report_path.with_name("dem_clamp_samples.jsonl")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    unavailable_feature_ids: list[int] = []
    with samples_path.open("w", encoding="utf-8") as samples:
        for feature in layer:
            report["feature_count"] += 1
            fid = feature.GetFID()
            try:
                geometry = feature.GetGeometryRef()
                if geometry is None or geometry.IsEmpty():
                    raise ValueError("Empty feature geometry")
                geometry = geometry.Clone()
                geometry.Transform(transform)
                xmin, xmax, ymin, ymax = geometry.GetEnvelope()
                lon, lat = (xmin + xmax) / 2, (ymin + ymax) / 2
                if not math.isfinite(lon) or not math.isfinite(lat):
                    raise ValueError("Invalid transformed feature position")
                height = sample_height(normalized, lon, lat)
            except DemSampleUnavailable as exc:
                unavailable_feature_ids.append(fid)
                if len(report["excluded_nodata_examples"]) < 20:
                    report["excluded_nodata_examples"].append({"fid": fid, "reason": str(exc)})
                continue
            except (ValueError, RuntimeError) as exc:
                report["invalid_count"] += 1
                if len(report["invalid_examples"]) < 20:
                    report["invalid_examples"].append({"fid": fid, "reason": str(exc)})
                continue
            report["valid_count"] += 1
            for key, function in (("min_ground_height", min), ("max_ground_height", max)):
                previous = report[key]
                report[key] = height if previous is None else function(previous, height)
            samples.write(json.dumps({"fid": fid, "lon": lon, "lat": lat, "dem_height": height}) + "\n")
    layer = None
    shp = None
    _delete_features(clipped_path, unavailable_feature_ids)
    report["candidate_feature_count"] = report["feature_count"]
    report["feature_count"] -= len(unavailable_feature_ids)
    report["clipping"]["applied"] = bool(
        report["clipping"]["applied"] or unavailable_feature_ids
    )
    report["clipping"]["nodata_dropped_feature_count"] = len(unavailable_feature_ids)
    report["clipping"]["output_feature_count"] -= len(unavailable_feature_ids)
    report["status"] = "ready" if report["valid_count"] and not report["invalid_count"] else "failed"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    normalized = None
    source = None
    if report["status"] != "ready":
        raise ValueError(
            f"DEM clamping validation failed: {report['invalid_count']} invalid / "
            f"{report['candidate_feature_count']} candidate features. See dem_clamp_report.json."
        )
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dem", type=Path, required=True)
    parser.add_argument("--shp", type=Path, required=True)
    parser.add_argument("--crs", required=True)
    parser.add_argument("--prepared", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--clipped", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = prepare(
            args.dem, args.shp, args.crs, args.prepared, args.report, args.clipped
        )
    except Exception as exc:
        parser.exit(1, f"DEM clamping failed: {exc}\n")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
