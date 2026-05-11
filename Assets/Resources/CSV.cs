using System.Collections;
using UnityEngine;
using CesiumForUnity; // 세슘 필수
using Unity.Mathematics; // double3 필수
using System.Globalization; // 숫자 변환 필수

public class CSVManager : MonoBehaviour
{
    [Header("1. 필수 에셋")]
    public TextAsset csvFile;       // CSV 파일
    public GameObject prefab;       // 배치할 프리팹 (가로등 등)

    [Header("2. CSV 열 설정 (A=0, B=1...)")]
    public int latColumnIndex = 0;  // 위도 열 번호
    public int lonColumnIndex = 1;  // 경도 열 번호

    [Header("3. 생성 옵션")]
    public int maxSpawnCount = 10;  // 일단 10개만 테스트

    void Start()
    {
        if (csvFile == null || prefab == null)
        {
            Debug.LogError("CSV 파일이나 프리팹이 비어있습니다!");
            return;
        }

        StartCoroutine(SpawnObjectsCoroutine());
    }

    IEnumerator SpawnObjectsCoroutine()
    {
        // 1. 씬에서 세슘 지오레퍼런스를 찾습니다. (이게 있어야 좌표가 안 겹칩니다)
        CesiumGeoreference geoReference = FindFirstObjectByType<CesiumGeoreference>();

        if (geoReference == null)
        {
            Debug.LogError("씬에 CesiumGeoreference가 없습니다! 세슘 설정을 먼저 확인하세요.");
            yield break;
        }

        // 2. CSV 줄 단위 쪼개기
        string[] lines = csvFile.text.Split(new char[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
        int spawnedCount = 0;

        // i=1부터 시작 (첫 줄 제목 건너뛰기)
        for (int i = 1; i < lines.Length; i++)
        {
            if (spawnedCount >= maxSpawnCount) break;

            string[] columns = lines[i].Split(',');

            if (columns.Length > Mathf.Max(latColumnIndex, lonColumnIndex))
            {
                // 글자 양 끝 공백 제거
                string latStr = columns[latColumnIndex].Trim();
                string lonStr = columns[lonColumnIndex].Trim();

                // 3. 숫자로 정확히 변환 시도
                if (double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(lonStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double lon))
                {
                    // 4. ★중요: Georeference의 자식으로 생성★
                    GameObject obj = Instantiate(prefab, geoReference.transform);
                    obj.name = $"{prefab.name}_{spawnedCount}";

                    // 5. 세슘 앵커 컴포넌트 추가 및 좌표 설정
                    CesiumGlobeAnchor anchor = obj.GetComponent<CesiumGlobeAnchor>();
                    if (anchor == null)
                    {
                        anchor = obj.AddComponent<CesiumGlobeAnchor>();
                    }

                    // 6. 위경도 좌표 꽂아넣기 (경도, 위도, 높이 순서)
                    anchor.longitudeLatitudeHeight = new double3(lon, lat, 0);

                    spawnedCount++;

                    // 7. 렉 방지용 (5개마다 한 번씩 쉬기)
                    if (spawnedCount % 5 == 0) yield return null;
                }
            }
        }

        Debug.Log($"<color=cyan><b>배치 완료:</b> 총 {spawnedCount}개의 객체가 세슘 좌표계에 정상 배치되었습니다.</color>");
    }
}