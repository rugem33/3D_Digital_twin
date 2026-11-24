# Tiler Server

Unity에서 SHP 파일과 속성값을 보내면 서버가 `mago-3d-tiler`를 실행해 3D Tiles 산출물(`b3dm`, `tileset.json`)을 만들고 URL을 반환하는 Python/Flask 서버입니다. Docker 환경을 기준으로 구성했습니다.

## 구조

```text
.
├── app/                    # Flask API 서버
│   ├── main.py             # /convert, /outputs, /health
│   └── services/           # 업로드, 압축, mago tiler 실행 로직
├── tools/                  # DEM/terrain 관련 기존 도구
│   ├── download_dem.py
│   ├── process_dem.py
│   ├── terrain_server.py
│   └── terrain_test_server.py
├── data/
│   ├── uploads/            # 요청별 업로드 저장
│   ├── outputs/            # 요청별 b3dm/tileset 결과 저장
│   └── terrain_tiles/      # DEM terrain tile 확장용
├── vendor/                 # mago-3d-tiler jar 배치
├── Dockerfile
└── docker-compose.yml
```

## 준비

`mago-3d-tiler-1.16.3.jar` 파일은 프로젝트에 포함되어 있으며 아래 위치에 둡니다.

```bash
vendor/mago-3d-tiler-1.16.3.jar
```

Docker 이미지 빌드 시 이 jar가 `/opt/mago/mago-3d-tiler-1.16.3.jar`로 복사됩니다.

## 실행

```bash
docker compose up --build
```

상태 확인:

```bash
curl http://localhost:8000/health
curl http://localhost:8000/status
```

## 변환 API

`POST /api/convert`

기존 호환용으로 `POST /convert`도 동일하게 동작합니다.

요청은 `multipart/form-data`입니다.

| 필드 | 설명 |
| --- | --- |
| `files[]` | `.shp/.dbf/.shx/.prj` 개별 업로드 또는 shapefile zip |
| `demFile` | 호환용 선택 사항. GeoTIFF를 전처리·검사한 뒤 mago `-te`에 전달. `terrainJobId`와 동시에 사용 불가 |
| `terrainJobId` | 선택 사항. 완료된 DEM terrain job ID. 원본 DEM으로 검사하고 동일 job의 quantized-mesh `layer.json`으로 건물을 클램핑 |
| `outputType` | `b3dm`, `i3dm`, `pnts` 중 하나 |
| `curvatureCorrection` | `true`이면 mago `-cc` 적용 |
| `coordinateSystem` | EPSG 코드, 예: `5186` |
| `heightColumn` | mago 옵션 `-hc`, 기본 `height` |
| `scaleHeight` | mago 옵션 `-sh`, 기본 `0.0` |
| `shp_zip` | 기존 호환 필드. `.shp/.shx/.dbf/.prj`가 들어있는 zip |
| `files` | 기존 호환 필드. zip 대신 여러 shapefile 구성 파일을 반복 업로드할 때 사용 |
| `terrain_tif` | 기존 호환 필드. GeoTIFF terrain 파일 |
| `attributes` | Unity에서 넘기는 속성 JSON 문자열 |
| `attributes_file` | 속성 JSON 파일 |

SHP와 DEM을 함께 업로드하면 각 건물의 변환 후 도형 경계 중심에서 DEM 높이를
조회하고 유효성을 검사한 뒤, Mago의 지형 클램프 기능으로 건물마다 다른 지면
높이를 적용합니다. 건물 자체 높이(`heightColumn`)와 지면 높이는 별개입니다.
SHP가 DEM보다 넓으면 서버가 작업용 SHP를 DEM의 bilinear 샘플링 가능 범위로
자동 클리핑합니다. 경계에 걸친 도형은 잘리고 완전히 바깥인 도형은 제외됩니다.
보간에 필요한 셀이 NoData인 피처도 작업용 SHP에서 제외하며 0m로 대체하지
않습니다. 클리핑·NoData 제외 후 피처가 하나도 없으면 변환은 실패합니다.
업로드한 원본 SHP/DEM은 변경하지 않습니다.

작업 출력에 `dem_clamp_report.json`(좌표계·경계/NoData 제외 수·높이 범위)과
`dem_clamp_samples.jsonl`(SHP FID·경위도·DEM 조회값)이 생성됩니다. 이는 전처리
조회 기록이며 생성된 메시의 높이를 독립적으로 측정한 결과는 아닙니다.
DEM의 높이 단위는 m, scale/offset은 적용된 상태여야 합니다. 수직 기준·지오이드
변환은 자동 추정하지 않습니다. DEM을 생략하면 기존 변환 방식을 유지합니다.
단위 메타데이터가 없으면 m로 해석하며, 보고서에 `metres_assumed: true`를 기록합니다.
건물 전체를 중심점의 지면 높이만큼 이동하는 방식으로, 경사진 바닥의 각 정점을
지형에 맞춰 변형하지 않습니다. `scaleHeight`는 배율이 아닌 Mago `-sh`의 지중
여유 높이이며, 침하를 피하기 위해 기본값은 `0.0`입니다.

화면에 표시되는 지형과 건물 높이를 맞추려면 `demFile` 대신 완료된
`terrainJobId`를 전달합니다. 서버는 해당 job의 원본 DEM으로 범위를 검사하지만,
Mago에는 같은 job의 quantized-mesh `layer.json`을 전달합니다.

전처리는 Docker의 `/usr/bin/python3`와 `python3-gdal`을 사용합니다.
다른 환경에서는 `GDAL_PYTHON`을 GDAL Python 바인딩이 있는 실행 파일로 지정합니다.

예시:

```bash
curl -X POST "http://localhost:8000/api/convert" \
  -F "files[]=@building.zip" \
  -F "terrainJobId=<completed_terrain_job_id>" \
  -F "outputType=b3dm" \
  -F "curvatureCorrection=true" \
  -F "coordinateSystem=5186" \
  -F "heightColumn=height" \
  -F "scaleHeight=0.0" \
  -F 'attributes={"project":"demo","height":12.5}' \
```

응답:

```json
{
  "job_id": "요청 ID",
  "jobId": "요청 ID",
  "status": "running",
  "statusUrl": "http://localhost:8000/api/jobs/<job_id>/status",
  "logsUrl": "http://localhost:8000/api/jobs/<job_id>/logs",
  "stdoutLogUrl": "http://localhost:8000/outputs/<job_id>/mago_stdout.log",
  "stderrLogUrl": "http://localhost:8000/outputs/<job_id>/mago_stderr.log"
}
```

Unity는 `statusUrl`을 1초 간격으로 polling 하다가 `status=completed`가 되면
응답의 `tilesetUrl` 또는 `zipUrl`을 받아 사용하면 됩니다.

진행 상태 조회:

```bash
curl "http://localhost:8000/api/jobs/<job_id>/status"
```

응답 예시:

```json
{
  "jobId": "요청 ID",
  "status": "running",
  "stage": "postprocess",
  "progress": 83,
  "progressText": "[Post][1200/1956] post-process in progress : ..."
}
```

로그 조회:

```bash
curl "http://localhost:8000/api/jobs/<job_id>/logs?maxChars=12000"
```

웹 클라이언트에서는 SSE 스트림도 사용할 수 있습니다.

```bash
curl "http://localhost:8000/api/jobs/<job_id>/events"
```

호환용 동기 API `POST /convert`는 기존처럼 변환 완료 후 한 번에 결과를 반환합니다.

## mago3d-tiler 실행 명령

서버는 내부적으로 아래 형태의 명령을 실행합니다.

```bash
java -jar /opt/mago/mago-3d-tiler-1.16.3.jar \
  -i /data/uploads/<job_id>/_dem_clamp/clipped \
  -o /data/outputs/<job_id> \
  -it shp \
  -ot b3dm \
  -cc \
  -c 5186 \
  -te /data/terrain_tiles/<terrain_job_id>/tiles/layer.json \
  -hc height \
  -sh 0.0
```

## DEM 클램프 검증

GDAL Python 바인딩과 NumPy가 있는 Python에서 실행합니다.

```bash
python -m unittest discover -s tests -v
```

`DEM_CLAMP_TEST_JAR`에 실제 Mago JAR 경로를 지정하면 DEM 적용 전후의
3D Tiles 생성 비교도 실행합니다. 필요하면 `DEM_CLAMP_TEST_JAVA`에 Java 실행 파일을
지정합니다. 합성 DEM의 두 지면 높이(100m·200m)가 산출물의 최소·최대 높이에
각각 반영되는지 검사하며, 실제 현장의 위치·고도 정확도 평가는 별도로 필요합니다.

## DEM/Terrain 도구

제공된 DEM 관련 파일은 `tools/`에 포함했습니다.

```bash
python tools/download_dem.py --process --max-zoom 12
python tools/process_dem.py --input path/to/dem.tif --max-zoom 12
python tools/terrain_server.py
```

이 도구들은 quantized-mesh terrain 서버가 필요할 때 별도 확장 파트로 사용할 수 있습니다.
