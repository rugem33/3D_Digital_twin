import json
import subprocess
from dataclasses import dataclass
from pathlib import Path

from app import config


@dataclass(frozen=True)
class TilerOptions:
    coordinate_code: str = config.DEFAULT_COORDINATE_CODE
    height_column: str = config.DEFAULT_HEIGHT_COLUMN
    skirt_height: str = config.DEFAULT_SKIRT_HEIGHT
    input_type: str = config.DEFAULT_INPUT_TYPE
    output_type: str = config.DEFAULT_OUTPUT_TYPE
    curvature_correction: bool = True


@dataclass(frozen=True)
class TilerResult:
    command: list[str]
    stdout: str
    stderr: str
    tileset_path: Path


def prepare_clamp_terrain(input_dir: Path, output_dir: Path, options: TilerOptions,
                          terrain_path: Path | None) -> Path | None:
    if terrain_path is None:
        return None
    shapefiles = [
        path for path in input_dir.rglob("*.shp")
        if "_dem_clamp" not in path.relative_to(input_dir).parts
    ]
    if len(shapefiles) != 1:
        raise ValueError("DEM clamping requires exactly one input SHP")
    prepared = input_dir / "_dem_clamp" / "terrain_wgs84.tif"
    clipped = input_dir / "_dem_clamp" / "clipped" / shapefiles[0].name
    report = output_dir / "dem_clamp_report.json"
    output_dir.mkdir(parents=True, exist_ok=True)
    command = [config.GDAL_PYTHON, str(Path(__file__).with_name("dem_clamp_worker.py")),
               "--dem", str(terrain_path), "--shp", str(shapefiles[0]),
               "--crs", options.coordinate_code, "--prepared", str(prepared),
               "--report", str(report), "--clipped", str(clipped)]
    result = subprocess.run(command, capture_output=True, text=True,
                            timeout=config.CONVERT_TIMEOUT_SECONDS, check=False)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or "DEM clamping preparation failed")
    summary = json.loads(report.read_text(encoding="utf-8"))
    if summary.get("status") != "ready" or not prepared.is_file() or not clipped.is_file():
        raise RuntimeError("DEM clamping preparation did not produce valid clipped inputs")
    return prepared


def prepared_input_dir(input_dir: Path, terrain_path: Path | None) -> Path:
    if terrain_path is None:
        return input_dir
    clipped_dir = input_dir / "_dem_clamp" / "clipped"
    if len(list(clipped_dir.glob("*.shp"))) != 1:
        raise RuntimeError("DEM clamping did not produce exactly one clipped SHP")
    return clipped_dir


def build_mago_command(
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None = None,
) -> list[str]:
    command = [
        "java",
        f"-Xms{config.JAVA_XMS}",           # 초기 힙 — 작은 값에서 시작해 GC 반복 방지
        f"-Xmx{config.JAVA_XMX}",           # 최대 힙 — 부족하면 전처리 GC 폭발
        "-XX:+UseG1GC",                      # G1GC: 대용량 힙에서 멈춤 시간 최소화
        "-XX:MaxGCPauseMillis=200",          # GC 목표 멈춤 시간 200ms
        "-XX:+UseStringDeduplication",       # 문자열 중복 제거로 힙 절약
        "-jar",
        str(config.MAGO_TILER_JAR),
        "-i",
        str(input_dir),
        "-o",
        str(output_dir),
        "-it",
        options.input_type,
        "-ot",
        options.output_type,
        "-c",
        options.coordinate_code,
        "-hc",
        options.height_column,
        "-sh",
        options.skirt_height,
    ]

    if options.curvature_correction:
        command.append("-cc")

    if terrain_path:
        command.extend(["-te", str(terrain_path)])

    return command


def run_mago_tiler(
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None = None,
    mago_terrain_path: Path | None = None,
) -> TilerResult:
    if not config.MAGO_TILER_JAR.exists():
        raise FileNotFoundError(
            f"mago3d tiler jar not found: {config.MAGO_TILER_JAR}. "
            "Mount or copy mago-3d-tiler-1.16.3.jar into vendor/."
        )

    output_dir.mkdir(parents=True, exist_ok=True)
    prepared_dem_path = prepare_clamp_terrain(input_dir, output_dir, options, terrain_path)
    input_dir = prepared_input_dir(input_dir, prepared_dem_path)
    terrain_for_mago = mago_terrain_path or prepared_dem_path
    command = build_mago_command(input_dir, output_dir, options, terrain_for_mago)

    completed = subprocess.run(
        command,
        capture_output=True,
        text=True,
        timeout=config.CONVERT_TIMEOUT_SECONDS,
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            "mago3d tiler failed\n"
            f"command: {' '.join(command)}\n"
            f"stdout:\n{completed.stdout}\n"
            f"stderr:\n{completed.stderr}"
        )

    tileset_path = find_tileset(output_dir)
    return TilerResult(command, completed.stdout, completed.stderr, tileset_path)


def find_tileset(output_dir: Path) -> Path:
    candidates = sorted(output_dir.rglob("tileset.json"))
    if not candidates:
        raise FileNotFoundError(f"tileset.json was not generated under {output_dir}")
    return candidates[0]
