"""
DEM (.tif / .img) → quantized-mesh 지형 타일 변환 및 서빙 서비스

흐름:
  1. POST /api/terrain/upload  →  DEM 파일 저장 + 비동기 작업 시작
  2. gdalwarp 로 WGS84 변환
  3. ctb-tile 로 quantized-mesh 타일 생성 (tiles/{z}/{x}/{y}.terrain)
  4. 저줌 타일 수정 (ctb-tile의 잘못된 ECEF 중심 교정)
  5. GET /terrain/<job_id>/layer.json   →  TileJSON 메타데이터 서빙
  6. GET /terrain/<job_id>/<z>/<x>/<y>.terrain  →  타일 서빙 (없으면 평탄 폴백)
"""

import json
import math
import os
import struct
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from typing import Any

from app import config

NODATA_VAL   = -9999.0
CTB_IMAGE    = "tumgis/ctb-quantized-mesh"
SELF_NAME    = os.getenv("SELF_CONTAINER_NAME", "tiler-server")

# ctb-tile가 ECEF 중심을 잘못 계산하는 줌 레벨 임계값
# z <= LOW_ZOOM_FIX_THRESHOLD 인 타일은 make_flat_tile 로 교체
LOW_ZOOM_FIX_THRESHOLD = 4
# z=7 도 4정점 평탄 타일이므로 교체 대상
FLAT_ZOOM_LEVELS = {7}


# ────────────────────────────────────────────────────────────
# 작업 관리
# ────────────────────────────────────────────────────────────

def start_terrain_job(job_id: str, dem_path: Path, max_zoom: int, public_base_url: str = "") -> None:
    config.TERRAIN_DIR.mkdir(parents=True, exist_ok=True)
    _write_status(job_id, {
        "status": "running",
        "stage": "queued",
        "progress": 0,
        "progressText": "작업 대기 중",
        "maxZoom": max_zoom,
        "statusUrl": _pub(f"/api/terrain/jobs/{job_id}/status", public_base_url),
        "layerUrl": _pub(f"/terrain/{job_id}/layer.json", public_base_url),
    })
    threading.Thread(
        target=_run_job,
        args=(job_id, dem_path, max_zoom, public_base_url),
        daemon=True,
    ).start()


def read_terrain_status(job_id: str) -> dict[str, Any] | None:
    try:
        return json.loads(_status_path(job_id).read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return None


def read_terrain_log(job_id: str, max_chars: int = 32000) -> str | None:
    log_path = config.TERRAIN_DIR / job_id / "process.log"
    if not log_path.exists():
        return None
    text = log_path.read_text(encoding="utf-8", errors="replace")
    if len(text) > max_chars:
        text = "...(앞부분 생략)...\n" + text[-max_chars:]
    return text


def serve_layer_json(job_id: str, host_url: str) -> dict | None:
    """layer.json 읽어서 tiles URL을 현재 호스트로 교체 후 반환."""
    layer_path = _tiles_dir(job_id) / "layer.json"
    if not layer_path.exists():
        return None
    data = json.loads(layer_path.read_text(encoding="utf-8"))
    host = host_url.rstrip("/")
    version = int(layer_path.stat().st_mtime)
    data["tiles"] = [f"{host}/terrain/{job_id}/{{z}}/{{x}}/{{y}}.terrain?v={version}"]
    return data


def get_tile_bytes(job_id: str, z: int, x: int, y: int) -> bytes | None:
    tile_path = _tiles_dir(job_id) / str(z) / str(x) / f"{y}.terrain"
    if not tile_path.exists():
        return None

    if z <= LOW_ZOOM_FIX_THRESHOLD or z in FLAT_ZOOM_LEVELS:
        return make_flat_tile(z, x, y, h_max=_read_dem_max_height(job_id))

    return tile_path.read_bytes()


def _read_dem_max_height(job_id: str) -> float:
    status = read_terrain_status(job_id)
    if status is None:
        return 9000.0

    try:
        return float(status.get("demMaxHeight", 9000.0))
    except (TypeError, ValueError):
        return 9000.0


# ────────────────────────────────────────────────────────────
# 비동기 변환 실행
# ────────────────────────────────────────────────────────────

def _run_job(job_id: str, dem_path: Path, max_zoom: int, public_base_url: str = "") -> None:
    job_dir = config.TERRAIN_DIR / job_id
    wgs84_dir = job_dir / "wgs84"
    tiles_dir = _tiles_dir(job_id)
    wgs84_dir.mkdir(parents=True, exist_ok=True)
    tiles_dir.mkdir(parents=True, exist_ok=True)
    log_path = job_dir / "process.log"

    try:
        with log_path.open("w", encoding="utf-8") as log:

            # 1단계: WGS84 변환
            _write_status(job_id, {"stage": "reprojecting", "progress": 10,
                                   "progressText": "WGS84 좌표계 변환 중"}, merge=True)
            log.write("[1단계] WGS84 변환\n")
            wgs84_tif = _step_reproject(dem_path, wgs84_dir / "dem_wgs84.tif", log)

            # DEM 최대 고도 추출 (저줌 타일 교정에 사용)
            dem_max_height = _get_dem_max_height(wgs84_tif, log)
            log.write(f"  DEM 최대 고도: {dem_max_height:.1f}m\n")

            # 2단계: ctb-tile 실행
            _write_status(job_id, {"stage": "tiling", "progress": 20,
                                   "progressText": "quantized-mesh 타일 생성 중"}, merge=True)
            log.write("\n[2단계] ctb-tile 실행\n")
            _step_ctb_tile(wgs84_tif, tiles_dir, max_zoom, log)

            # 3단계: 저줌 타일 교정
            # ctb-tile 은 z=0~2 에서 ECEF 중심을 지구 바운딩 박스 중심으로 잘못 계산함
            # → Cesium 이 SSE 를 올바로 계산하지 못해 드릴다운이 멈춤
            # z=7 도 4정점 평탄 타일로 geometric error=0 판정을 받아 드릴다운이 멈춤
            # 해결: 해당 타일을 올바른 ECEF + dem_max_height 로 교체
            _write_status(job_id, {"stage": "fixing_tiles", "progress": 80,
                                   "progressText": "저줌 타일 ECEF 교정 중"}, merge=True)
            log.write("\n[3단계] 저줌 타일 교정\n")
            _fix_low_zoom_tiles(tiles_dir, dem_max_height, log)

            # 4단계: layer.json
            _write_status(job_id, {"stage": "finalizing", "progress": 90,
                                   "progressText": "layer.json 생성 중"}, merge=True)
            log.write("\n[4단계] layer.json 생성\n")
            bounds = _get_bounds(wgs84_tif, log)
            _write_layer_json(job_id, tiles_dir, max_zoom, bounds)

            tile_count = sum(1 for _ in tiles_dir.rglob("*.terrain"))
            log.write(f"\n완료: 타일 {tile_count}개 생성\n")

            if tile_count == 0:
                raise RuntimeError(
                    "ctb-tile 완료 후 .terrain 파일이 없습니다.\n"
                    "원인 A) --volumes-from 볼륨 공유 실패\n"
                    "원인 B) DEM에 유효 고도 데이터 없음"
                )

        _write_status(job_id, {
            "status": "completed",
            "stage": "completed",
            "progress": 100,
            "progressText": f"완료 — 타일 {tile_count}개",
            "tileCount": tile_count,
            "maxZoom": max_zoom,
            "demMaxHeight": dem_max_height,
            "layerUrl": _pub(f"/terrain/{job_id}/layer.json", public_base_url),
        }, merge=True)

    except Exception as exc:
        _write_status(job_id, {
            "status": "failed",
            "stage": "failed",
            "progressText": str(exc),
            "error": str(exc),
        }, merge=True)


def _step_reproject(dem_path: Path, out_path: Path, log) -> Path:
    """gdalinfo 로 WGS84 여부 + nodata 값 확인 후 필요 시 gdalwarp 실행."""
    info_r = subprocess.run(
        ["gdalinfo", "-json", str(dem_path)],
        capture_output=True, text=True,
    )
    is_wgs84 = False
    nodata = _detect_nodata(info_r.stdout, log)

    if info_r.returncode == 0:
        txt = info_r.stdout
        is_wgs84 = ("WGS 84" in txt or "EPSG:4326" in txt or
                    '"EPSG","4326"' in txt or '"4326"' in txt)

    if is_wgs84:
        log.write(f"  이미 WGS84입니다. 변환 생략. (nodata={nodata})\n")
        return dem_path

    cmd = [
        "gdalwarp",
        "-t_srs", "EPSG:4326",
        "-r", "bilinear",
        "-of", "GTiff",
        "-co", "COMPRESS=DEFLATE",
    ]
    if nodata is not None:
        cmd += ["-srcnodata", str(nodata), "-dstnodata", str(nodata)]

    cmd += [str(dem_path), str(out_path)]

    r = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    log.write(r.stdout)
    log.write(r.stderr)
    if r.returncode != 0:
        raise RuntimeError(f"gdalwarp 실패 (코드 {r.returncode}): {r.stderr[:400]}")
    return out_path


def _detect_nodata(gdalinfo_json: str, log) -> float | None:
    """gdalinfo -json 출력에서 nodata 값 추출. 없으면 None 반환."""
    try:
        info = json.loads(gdalinfo_json)
        bands = info.get("bands", [])
        if bands:
            nd = bands[0].get("noDataValue")
            if nd is not None:
                log.write(f"  nodata 자동 감지: {nd}\n")
                return float(nd)
    except Exception:
        pass
    log.write("  nodata 값 없음 — gdalwarp에서 nodata 옵션 생략\n")
    return None


def _get_dem_max_height(tif_path: Path, log) -> float:
    """gdalinfo -stats 로 DEM 최대 고도를 반환. 실패 시 보수적 기본값 반환."""
    r = subprocess.run(
        ["gdalinfo", "-stats", "-json", str(tif_path)],
        capture_output=True, text=True,
    )
    if r.returncode == 0:
        try:
            info = json.loads(r.stdout)
            bands = info.get("bands", [])
            if bands:
                max_val = bands[0].get("maximum")
                if max_val is not None:
                    return float(max_val)
        except Exception:
            pass
    log.write("  경고: DEM 최대 고도 감지 실패 → 기본값 9000m 사용\n")
    return 9000.0


def _fix_low_zoom_tiles(tiles_dir: Path, dem_max_height: float, log) -> None:
    """
    ctb-tile 이 잘못 생성한 저줌 타일을 올바른 ECEF 와 h_max 로 교체한다.

    문제:
      - z=0~2: ECEF 중심이 지구 내부에 위치 (바운딩 박스 기하 중심 버그)
      - z=3~4: ECEF 는 정상이지만 고도 편차=0 → Cesium geometric error=0
      - z=7  : 4정점 평탄 타일 → geometric error=0

    해결: make_flat_tile(h_max=dem_max_height) 로 교체
      → 올바른 ECEF + 비-0 h_max → Cesium 이 드릴다운 결정 가능
    교체 대상에서 z=5, z=6 은 실제 고도 데이터가 있으므로 제외.
    """
    replaced = 0
    for z_dir in sorted(tiles_dir.iterdir(), key=lambda d: int(d.name) if d.name.isdigit() else 999):
        if not z_dir.is_dir() or not z_dir.name.isdigit():
            continue
        z = int(z_dir.name)
        if z not in FLAT_ZOOM_LEVELS and z > LOW_ZOOM_FIX_THRESHOLD:
            continue  # z=5, z=6, z=8+ 는 건드리지 않음

        for x_dir in z_dir.iterdir():
            if not x_dir.is_dir():
                continue
            x = int(x_dir.name)
            for tile_file in x_dir.glob("*.terrain"):
                y = int(tile_file.stem)
                tile_file.write_bytes(make_flat_tile(z, x, y, h_max=dem_max_height))
                replaced += 1

    log.write(f"  교체된 저줌 타일: {replaced}개 (z≤{LOW_ZOOM_FIX_THRESHOLD} 및 z∈{FLAT_ZOOM_LEVELS})\n")


def _build_availability(tiles_dir: Path, max_zoom: int) -> list:
    """tiles 디렉토리를 스캔해 layer.json 용 available 배열을 생성한다."""
    available = []
    for z in range(max_zoom + 1):
        z_dir = tiles_dir / str(z)
        ranges: list[dict] = []
        if z_dir.exists():
            for x_dir in sorted(z_dir.iterdir(), key=lambda d: int(d.name)):
                if not x_dir.is_dir():
                    continue
                x = int(x_dir.name)
                ys = sorted(int(f.stem) for f in x_dir.glob("*.terrain"))
                if not ys:
                    continue
                start = end = ys[0]
                for y in ys[1:]:
                    if y == end + 1:
                        end = y
                    else:
                        ranges.append({"startX": x, "startY": start, "endX": x, "endY": end})
                        start = end = y
                ranges.append({"startX": x, "startY": start, "endX": x, "endY": end})
        available.append(ranges)
    return available


def _step_ctb_tile(wgs84_tif: Path, tiles_dir: Path, max_zoom: int, log) -> None:
    """
    tumgis/ctb-quantized-mesh 컨테이너로 ctb-tile 실행.

    --volumes-from=<self> 를 사용하면 이 컨테이너가 마운트한 named volume(/data)을
    ctb-tile 컨테이너도 동일 경로로 공유하므로 Windows Docker Desktop에서도 경로 문제 없음.
    """
    _ensure_ctb_image(log)

    r = subprocess.run(
        [
            "docker", "run", "--rm",
            f"--volumes-from={SELF_NAME}",
            CTB_IMAGE,
            "ctb-tile",
            "-f", "Mesh",
            "-C",
            "-s", str(max_zoom),
            "-e", "0",
            "-o", str(tiles_dir),
            str(wgs84_tif),
        ],
        capture_output=True, text=True, timeout=config.CONVERT_TIMEOUT_SECONDS,
    )
    log.write(r.stdout)
    log.write(r.stderr)
    if r.returncode != 0:
        raise RuntimeError(f"ctb-tile 실패 (코드 {r.returncode}): {r.stderr[:400]}")


def _ensure_ctb_image(log) -> None:
    """로컬에 tumgis/ctb-quantized-mesh 이미지가 없으면 pull."""
    check = subprocess.run(
        ["docker", "image", "inspect", CTB_IMAGE],
        capture_output=True,
    )
    if check.returncode != 0:
        log.write(f"[pull] {CTB_IMAGE} 이미지 다운로드 중...\n")
        pull = subprocess.run(
            ["docker", "pull", CTB_IMAGE],
            capture_output=True, text=True, timeout=300,
        )
        log.write(pull.stdout)
        log.write(pull.stderr)
        if pull.returncode != 0:
            raise RuntimeError(f"docker pull 실패: {pull.stderr[:300]}")


def _get_bounds(tif_path: Path, log) -> list[float]:
    r = subprocess.run(
        ["gdalinfo", "-json", str(tif_path)],
        capture_output=True, text=True,
    )
    if r.returncode == 0:
        try:
            info = json.loads(r.stdout)
            coords = info.get("wgs84Extent", {}).get("coordinates", [[]])
            flat = [pt for ring in coords for pt in ring]
            lons = [p[0] for p in flat]
            lats = [p[1] for p in flat]
            b = [min(lons), min(lats), max(lons), max(lats)]
            log.write(f"  bounds: {[round(v, 4) for v in b]}\n")
            return b
        except Exception:
            pass
    log.write("  bounds 감지 실패 → 전 세계 기본값 사용\n")
    return [-180.0, -90.0, 180.0, 90.0]


def _write_layer_json(job_id: str, tiles_dir: Path, max_zoom: int, bounds: list[float]) -> None:
    available = _build_availability(tiles_dir, max_zoom)
    data = {
        "tilejson": "2.1.0",
        "format": "quantized-mesh-1.0",
        "version": "1.0.0",
        "scheme": "tms",
        "tiles": [f"{config.PUBLIC_BASE_URL}/terrain/{job_id}/{{z}}/{{x}}/{{y}}.terrain"],
        "bounds": bounds,
        "minzoom": 0,
        "maxzoom": max_zoom,
        "available": available,
        "description": "RoadTools DEM 지형 서버",
        "attribution": "RoadTools",
    }
    (tiles_dir / "layer.json").write_text(
        json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8"
    )


# ────────────────────────────────────────────────────────────
# 평탄 타일 폴백 (quantized-mesh 4-vertex 최소 타일)
# ────────────────────────────────────────────────────────────

def _tile_bounds(z: int, x: int, y_tms: int):
    n = 2 ** (z + 1)
    m = 2 ** z
    lon_min = x / n * 360.0 - 180.0
    lon_max = (x + 1) / n * 360.0 - 180.0
    lat_min = y_tms / m * 180.0 - 90.0
    lat_max = (y_tms + 1) / m * 180.0 - 90.0
    return lon_min, lat_min, lon_max, lat_max


def _to_ecef(lon_deg: float, lat_deg: float, h: float = 0.0):
    a = 6378137.0
    e2 = 0.00669437999014
    lo = math.radians(lon_deg)
    la = math.radians(lat_deg)
    N = a / math.sqrt(1 - e2 * math.sin(la) ** 2)
    return (N + h) * math.cos(la) * math.cos(lo), \
           (N + h) * math.cos(la) * math.sin(lo), \
           (N * (1 - e2) + h) * math.sin(la)


def _zz(d: int) -> int:
    return d * 2 if d >= 0 else -d * 2 - 1


def _delta_zz(vs: list) -> list:
    out, prev = [], 0
    for v in vs:
        out.append(_zz(v - prev))
        prev = v
    return out


def _encode_high_water_mark(indices: list[int]) -> list[int]:
    encoded = []
    highest = 0
    for index in indices:
        encoded.append(highest - index)
        if index == highest:
            highest += 1
    return encoded


def make_flat_tile(z: int, x: int, y: int, h_max: float = 0.0) -> bytes:
    """
    올바른 ECEF 중심과 지정된 h_max 를 가진 최소 quantized-mesh 타일 생성.

    h_max 를 DEM 최대 고도로 설정하면 Cesium 이 geometric error > 0 으로
    판단해 드릴다운을 수행한다.
    """
    lon_min, lat_min, lon_max, lat_max = _tile_bounds(z, x, y)
    cx, cy, cz = _to_ecef((lon_min + lon_max) / 2, (lat_min + lat_max) / 2)
    kx, ky, kz = _to_ecef(lon_min, lat_min)
    a = 6378137.0
    r = max(math.dist((cx, cy, cz), (kx, ky, kz)), 1.0)

    hdr  = struct.pack('<ddd', cx, cy, cz)
    hdr += struct.pack('<ff', 0.0, float(h_max))
    hdr += struct.pack('<dddd', cx, cy, cz, r)
    hdr += struct.pack('<ddd', cx / a, cy / a, cz / a)

    u = [0, 32767, 0, 32767]
    v = [0, 0, 32767, 32767]
    h = [0, 0, 0, 0]
    vd  = struct.pack('<I', 4)
    vd += struct.pack('<4H', *_delta_zz(u))
    vd += struct.pack('<4H', *_delta_zz(v))
    vd += struct.pack('<4H', *_delta_zz(h))

    triangle_indices = _encode_high_water_mark([0, 1, 2, 1, 3, 2])
    id_ = struct.pack('<I', 2) + struct.pack('<6H', *triangle_indices)

    def edge(a_, b_):
        return struct.pack('<I', 2) + struct.pack('<2H', a_, b_)

    ed = edge(0, 2) + edge(0, 1) + edge(1, 3) + edge(2, 3)
    return hdr + vd + id_ + ed


# ────────────────────────────────────────────────────────────
# 내부 유틸리티
# ────────────────────────────────────────────────────────────

def _tiles_dir(job_id: str) -> Path:
    return config.TERRAIN_DIR / job_id / "tiles"


def _status_path(job_id: str) -> Path:
    return config.TERRAIN_DIR / job_id / "status.json"


def _write_status(job_id: str, payload: dict[str, Any], merge: bool = False) -> None:
    job_dir = config.TERRAIN_DIR / job_id
    job_dir.mkdir(parents=True, exist_ok=True)
    sp = _status_path(job_id)
    data: dict[str, Any] = {}
    if merge:
        try:
            data = json.loads(sp.read_text(encoding="utf-8"))
        except Exception:
            pass
    data.update({"jobId": job_id, "updatedAt": time.time(), **payload})

    fd, tmp = tempfile.mkstemp(dir=job_dir, suffix=".tmp")
    try:
        with open(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        Path(tmp).replace(sp)
    except Exception:
        Path(tmp).unlink(missing_ok=True)
        raise


def _pub(path: str, public_base_url: str = "") -> str:
    clean = "/" + str(path).lstrip("/")
    if config.PUBLIC_BASE_URL:
        return f"{config.PUBLIC_BASE_URL}{clean}"
    if public_base_url:
        return f"{public_base_url.rstrip('/')}{clean}"
    return clean
