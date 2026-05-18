using CesiumForUnity;
using UnityEngine;

namespace Rugem.RoadTools
{
    public enum MagoOutputType { b3dm, i3dm, pnts }

    /// <summary>
    /// SHP → mago-3D-tiler 변환 요청 및 Cesium 3D Tiles 자동 배치 컴포넌트
    ///
    /// ── mago-3D-tiler CLI 파라미터 매핑 ────────────────────────────
    ///   -it shp          : 입력 타입 고정
    ///   -ot              : OutputType
    ///   -cc              : CurvatureCorrection (지구 곡률 보정)
    ///   -c               : CoordinateSystem (EPSG 코드)
    ///   -hc              : HeightColumn (높이 속성 컬럼명)
    ///   -sh              : ScaleHeight (높이 스케일)
    ///
    /// ── 서버 API 계약 (Docker 서버팀 참고) ─────────────────────────
    ///
    ///   [변환 요청]
    ///   POST {ServerUrl}/api/convert
    ///   Content-Type: multipart/form-data
    ///   Body:
    ///     files[]              : .shp / .dbf / .shx / .prj
    ///     demFile              : .tif (GeoTIFF, optional — 지형 DEM)
    ///     outputType           : string  (b3dm | i3dm | pnts)
    ///     curvatureCorrection  : bool
    ///     coordinateSystem     : int     (EPSG, 예: 5186)
    ///     heightColumn         : string
    ///     scaleHeight          : float
    ///
    ///   Response 200:
    ///   {
    ///     "jobId"      : "uuid",
    ///     "status"     : "processing" | "complete" | "error",
    ///     "tilesetUrl" : "http://host/tiles/{jobId}/tileset.json",
    ///     "message"    : "에러 시 메시지"
    ///   }
    ///
    ///   [상태 폴링]
    ///   GET {ServerUrl}/api/jobs/{jobId}
    ///   Response: 위와 동일
    ///
    /// ────────────────────────────────────────────────────────────────
    /// </summary>
    [AddComponentMenu("RoadTools/SHP to Tile Converter")]
    public class ShpToTileConverter : MonoBehaviour
    {
        // ── 서버 ──────────────────────────────────────────────────────
        [Header("서버 설정")]
        [Tooltip("mago-3D-tiler Docker 서버 주소")]
        [SerializeField] private string _serverUrl = "http://localhost:8080";

        // ── 입력 파일 ─────────────────────────────────────────────────
        [Header("입력 파일")]
        [Tooltip("SHP 파일이 담긴 ZIP")]
        [HideInInspector]
        [SerializeField] private string _shpZipPath = "";

        [Tooltip(".tif GeoTIFF — 건물 높이 기준 지형 DEM (선택 사항)")]
        [HideInInspector]
        [SerializeField] private string _demTifPath = "";

        // ── mago-3D-tiler 옵션 ────────────────────────────────────────
        [Header("mago-3D-tiler 옵션")]
        [Tooltip("-ot : 출력 타일 포맷")]
        [SerializeField] private MagoOutputType _outputType = MagoOutputType.b3dm;

        [Tooltip("-cc / --curvatureCorrection : 지구 곡률 보정")]
        [SerializeField] private bool _curvatureCorrection = true;

        [Tooltip("-c : 입력 좌표계 EPSG 코드 (한국 평면직각 5186 권장)")]
        [SerializeField] private int _coordinateSystem = 5186;

        [Tooltip("-hc : 건물 높이로 사용할 SHP 속성 컬럼명")]
        [SerializeField] private string _heightColumn = "height";

        [Tooltip("-sh : 높이 스케일 배율")]
        [SerializeField] private float _scaleHeight = 10.0f;

        // ── 배치 설정 ─────────────────────────────────────────────────
        [Header("배치 설정")]
        [Tooltip("타일셋을 배치할 씬의 GameObject — Cesium3DTileset 컴포넌트가 자동으로 추가됨")]
        [SerializeField] private GameObject _tilesetObject;

        [Tooltip("배치된 Tileset의 Maximum Screen Space Error (모바일 권장 32)")]
        [SerializeField, Range(1, 128)] private float _maximumScreenSpaceError = 32f;

        // ── 로컬 테스트 ───────────────────────────────────────────────
        [Header("로컬 테스트 (서버 없이 직접 배치)")]
        [Tooltip("변환 서버 없이 로컬 tileset.json을 직접 배치합니다.")]
        [HideInInspector]
        [SerializeField] private string _localTilesetPath = "";

        public string LocalTilesetPath => _localTilesetPath;

        // ── 공개 접근자 ───────────────────────────────────────────────
        public string             ServerUrl            => _serverUrl;
        public string             ShpZipPath           => _shpZipPath;
        public string             DemTifPath           => _demTifPath;
        public MagoOutputType     OutputType           => _outputType;
        public bool               CurvatureCorrection  => _curvatureCorrection;
        public int                CoordinateSystem     => _coordinateSystem;
        public string             HeightColumn         => _heightColumn;
        public float              ScaleHeight          => _scaleHeight;
        public GameObject         TilesetObject        => _tilesetObject;
        public float              MaxScreenSpaceError  => _maximumScreenSpaceError;
    }
}
