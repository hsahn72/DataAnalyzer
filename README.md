# DataAnalyzer

굴착기·휠로더 시계열 데이터(CSV)를 차트로 보고 동작·에너지를 분석하는 Windows 데스크톱 앱입니다.
HTML로 만든 'CSV 시계열 차트 뷰어'를 WPF(.NET 8, C#)로 옮기는 중입니다.

## 실행

- 필요: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Visual Studio 2022 또는 VS Code
- 명령줄: `dotnet run --project src/DataAnalyzer.App` (CSV 경로를 인수로 주면 바로 열림)
- 테스트: `dotnet test`

## 구조

| 프로젝트 | 내용 |
| --- | --- |
| `src/DataAnalyzer.Core` | 화면과 무관한 계산부 (CSV 파서, 시계열 보간, 기준 데이터, 차트 구성 JSON). Windows가 아니어도 빌드·테스트 가능 |
| `src/DataAnalyzer.App` | WPF 화면. 차트는 [ScottPlot 5](https://scottplot.net) (MIT) |
| `tests/DataAnalyzer.Core.Tests` | Core 단위 테스트 (xUnit) |

## 진행 단계

1. **뼈대와 차트 화면** (현재): CSV 열기(단위 행, Action 문자열), 시계열 차트 여러 개, 커서 A/B, 확대/이동, XY 차트, 기준 CSV 비교, 구성 저장/불러오기(HTML 뷰어 JSON 호환), PNG 저장
2. 계산 기능 이전: Filter, Cycle/Action 분석, 굴착기 기구학·동력·동역학·관성 추정, 휠로더 Z-bar
3. 분석 화면과 DB 연결: 2D 애니메이션, 파라미터 편집, Excel 가져오기, SQL Server 직접 조회
4. 배포: 공유폴더 설치 파일

## 차트 조작

| 동작 | 결과 |
| --- | --- |
| 클릭·드래그 | 커서 이동 (커서 B가 켜져 있으면 가까운 커서) |
| 휠 | X 확대/축소 (모든 시계열 차트 공통) |
| Shift+드래그, 오른쪽 드래그 | X 이동 |
| 가운데 드래그 | X·Y 이동 |
| Y축 위 휠 / Y축 더블클릭 | Y 확대/축소 / Y 자동 |
| ←/→ (Shift) | 커서를 1(10) 샘플 이동 |
| 신호 칩 클릭 / 더블클릭 / 끌기 | 강조 / 선 스타일 / 다른 차트로 이동 (Ctrl = 복사) |
