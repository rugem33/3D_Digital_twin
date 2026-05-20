#!/usr/bin/env python3
"""
Cesium 방식 C 테스트용 로컬 quantized-mesh 지형 타일 서버 (수정판)

수정 내용:
    - 좌표계 수정: Web Mercator → Geographic (등장방형, Cesium 지형 표준)
    - 요청 로그 추가 (터미널에서 Cesium이 실제로 타일을 요청하는지 확인)
    - 가시성 안내 주석 추가

실행:
    pip install flask
    python Tools/terrain_test_server.py

Unity 연결:
    TerrainSourceSwitcher > Mode = CustomUrl
    TerrainSourceSwitcher > Terrain Url = http://localhost:5001/layer.json

확인 방법:
    1. 서버 터미널에서 tile 요청 로그가 찍히는지 확인
       예: [TILE] z=0 x=0 y=0  →  서버가 정상 동작
    2. 지형 자체는 고도 0m 평탄이므로 V-World Overlay를 함께 켜야
       실제로 텍스처가 입혀진 지형을 볼 수 있습니다.
"""

import struct
import math
import json
from flask import Flask, Response, request

app = Flask(__name__)
PORT = 5001


# ────────────────────────────────────────────────────────────
# 좌표계: Cesium 지형 Geographic (등장방형) 투영
# ────────────────────────────────────────────────────────────
#
# Cesium quantized-mesh 지형은 Web Mercator가 아닌
# Geographic (Plate Carrée, EPSG:4326) 투영을 사용합니다.
#
# 줌 레벨 z에서 타일 수:
#   X(경도) 방향: 2^(z+1)  ← 경도 범위가 360°라서 위도(180°)의 두 배
#   Y(위도) 방향: 2^z
#
# z=0:  x=0 → 경도 -180~0,   y=0 → 위도 -90~90
#        x=1 → 경도 0~180,    (y 타일 1개뿐)
# z=1:  4×2 타일 구성
# ────────────────────────────────────────────────────────────

def tile_bounds(z: int, x: int, y_tms: int):
    """
    Geographic TMS 타일 좌표 → (lon_min, lat_min, lon_max, lat_max) 도(degree)

    TMS 스킴: y=0이 남쪽(적도 아래)
    """
    x_count = 2 ** (z + 1)   # 경도 방향 타일 수
    y_count = 2 ** z          # 위도 방향 타일 수

    lon_min = (x       / x_count) * 360.0 - 180.0
    lon_max = ((x + 1) / x_count) * 360.0 - 180.0
    lat_min = (y_tms       / y_count) * 180.0 - 90.0
    lat_max = ((y_tms + 1) / y_count) * 180.0 - 90.0

    return lon_min, lat_min, lon_max, lat_max


def to_ecef(lon_deg: float, lat_deg: float, h: float = 0.0):
    """WGS84 경위도 → ECEF 직교 좌표"""
    a  = 6378137.0
    f  = 1.0 / 298.257223563
    e2 = 2 * f - f * f
    lon = math.radians(lon_deg)
    lat = math.radians(lat_deg)
    N   = a / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    x   = (N + h) * math.cos(lat) * math.cos(lon)
    y   = (N + h) * math.cos(lat) * math.sin(lon)
    z   = (N * (1 - e2) + h) * math.sin(lat)
    return x, y, z


# ────────────────────────────────────────────────────────────
# quantized-mesh 인코딩
# ────────────────────────────────────────────────────────────

def zigzag(delta: int) -> int:
    """부호 있는 delta → 부호 없는 uint16 (양수: 2n, 음수: 2|n|-1)"""
    return delta * 2 if delta >= 0 else -delta * 2 - 1


def delta_zigzag(values: list) -> list:
    """값 목록을 delta→zigzag 인코딩 (prev=0 시작)"""
    out, prev = [], 0
    for v in values:
        out.append(zigzag(v - prev))
        prev = v
    return out


def make_flat_tile(z: int, x: int, y: int) -> bytes:
    """
    고도 0m 평탄 지형 타일을 quantized-mesh 바이너리로 생성.

    헤더 구조 (88 bytes):
        center ECEF    3 × double  (24)
        min/max height 2 × float   ( 8)
        bounding sphere center+r  4 × double  (32)
        horizon occlusion  3 × double  (24)
        계                          88

    정점 4개 (SW·SE·NW·NE 코너), 삼각형 2개, 경계 정점 각 2개
    """
    lon_min, lat_min, lon_max, lat_max = tile_bounds(z, x, y)
    lon_c = (lon_min + lon_max) / 2
    lat_c = (lat_min + lat_max) / 2

    cx, cy, cz = to_ecef(lon_c, lat_c)
    kx, ky, kz = to_ecef(lon_min, lat_min)
    radius = max(math.dist((cx, cy, cz), (kx, ky, kz)), 1.0)

    a = 6378137.0

    # 헤더 (88 bytes)
    hdr  = struct.pack('<ddd',  cx, cy, cz)
    hdr += struct.pack('<ff',   0.0, 0.0)
    hdr += struct.pack('<dddd', cx, cy, cz, radius)
    hdr += struct.pack('<ddd',  cx / a, cy / a, cz / a)
    assert len(hdr) == 88

    # 정점 데이터 (SW=0, SE=1, NW=2, NE=3)
    u = [0,     32767, 0,     32767]
    v = [0,     0,     32767, 32767]
    h = [0,     0,     0,     0    ]

    vdata  = struct.pack('<I',  4)
    vdata += struct.pack('<4H', *delta_zigzag(u))
    vdata += struct.pack('<4H', *delta_zigzag(v))
    vdata += struct.pack('<4H', *delta_zigzag(h))

    # 인덱스 (uint16, 2 삼각형)
    idata  = struct.pack('<I',  2)
    idata += struct.pack('<6H', 0, 1, 2, 1, 3, 2)

    # 경계 정점 인덱스 (uint32)
    def edge(a, b):
        return struct.pack('<I', 2) + struct.pack('<2I', a, b)

    edata  = edge(0, 2)   # 서  SW→NW
    edata += edge(0, 1)   # 남  SW→SE
    edata += edge(1, 3)   # 동  SE→NE
    edata += edge(2, 3)   # 북  NW→NE

    return hdr + vdata + idata + edata


# ────────────────────────────────────────────────────────────
# Flask 엔드포인트
# ────────────────────────────────────────────────────────────

@app.after_request
def cors(resp):
    resp.headers['Access-Control-Allow-Origin']  = '*'
    resp.headers['Access-Control-Allow-Headers'] = '*'
    return resp


@app.route('/layer.json')
def layer_json():
    host = request.host_url.rstrip('/')
    data = {
        "tilejson":    "2.1.0",
        "format":      "quantized-mesh-1.0",
        "version":     "1.0.0",
        "scheme":      "tms",
        "tiles":       [f"{host}/{{z}}/{{x}}/{{y}}.terrain"],
        "bounds":      [-180.0, -90.0, 180.0, 90.0],
        "minzoom":     0,
        "maxzoom":     12,
        "description": "RoadTools 방식 C 테스트 서버 (평탄 지형)",
        "attribution": "RoadTools Test"
    }
    print(f"[META] layer.json 요청")
    return Response(json.dumps(data, indent=2), content_type='application/json')


@app.route('/<int:z>/<int:x>/<int:y>.terrain')
def tile(z, x, y):
    lon_min, lat_min, lon_max, lat_max = tile_bounds(z, x, y)
    print(f"[TILE] z={z} x={x} y={y}  "
          f"lon[{lon_min:.1f}~{lon_max:.1f}] lat[{lat_min:.1f}~{lat_max:.1f}]")
    try:
        data = make_flat_tile(z, x, y)
        return Response(data,
                        content_type='application/vnd.quantized-mesh')
    except Exception as e:
        print(f"[ERROR] {e}")
        return Response(status=500)


@app.route('/health')
def health():
    return Response('{"status":"ok"}', content_type='application/json')


if __name__ == '__main__':
    print("=" * 55)
    print("  RoadTools — 방식 C 로컬 지형 서버 (수정판)")
    print("=" * 55)
    print(f"  layer.json : http://localhost:{PORT}/layer.json")
    print(f"  상태 확인  : http://localhost:{PORT}/health")
    print()
    print("  지형이 안 보이면 확인 사항:")
    print("  1. 터미널에 [TILE] 로그가 찍히는지 확인")
    print("     → 로그 있음: 타일 로드 성공, V-World Overlay 필요")
    print("     → 로그 없음: layer.json URL 연결 문제")
    print()
    print("  2. V-World Overlay 함께 적용해야 지형이 눈에 보임")
    print("     (평탄 고도 0m는 텍스처 없이 타원체와 구분 불가)")
    print("=" * 55)
    app.run(host='0.0.0.0', port=PORT, debug=False)
