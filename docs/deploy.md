# DataAnalyzer 배포 (공유 폴더)

## 한 번만: 공유 폴더에 올리기

1. GitHub 저장소의 **Actions** 탭 → 왼쪽 **release** → **Run workflow** → 버전(예: `0.3.0`)을 적고 실행합니다.
2. 몇 분 뒤 실행이 끝나면, 그 실행 화면 아래 **Artifacts**의 `DataAnalyzer-0.3.0`을 내려받습니다. (zip 안에 zip이 들어 있으면 한 번 더 풉니다.)
3. 압축을 풀면 `DataAnalyzer` 폴더가 나옵니다. 이 폴더를 통째로 공유 폴더에 복사합니다.

```
\\서버\공유\DataAnalyzer\
    DataAnalyzer.cmd   ← 사용자는 이것을 실행
    app\               ← 프로그램 (런타임 포함, .NET 설치 불필요)
    version.txt
    README.md
```

## 사용자: 실행하기

- 공유 폴더의 `DataAnalyzer.cmd`를 더블클릭합니다. 바탕 화면에 바로 가기를 만들어 두면 편합니다.
- 처음 한 번은 프로그램을 이 PC(`%LOCALAPPDATA%\DataAnalyzer\app`)로 복사하느라 조금 걸리고, 그다음부터는 바뀐 파일만 복사해서 바로 열립니다.
- CSV 파일을 `DataAnalyzer.cmd` 위로 끌어다 놓으면 그 파일을 열면서 시작합니다.
- 파라미터·차트 구성 프리셋은 사용자마다 `%AppData%\DataAnalyzer`에 저장됩니다.

## 새 버전으로 바꾸기

1. 위 1~2번으로 새 zip을 받습니다.
2. 공유 폴더의 `app` 폴더를 새 `app` 폴더로 바꿉니다. 사용자가 프로그램을 쓰고 있어도 바꿀 수 있습니다. (실행은 각자 PC의 복사본으로 하기 때문)
3. 사용자는 다음에 `DataAnalyzer.cmd`로 실행할 때 자동으로 새 버전을 받습니다. 창 제목에 버전이 표시됩니다.

## 참고

- 개발 PC에서 직접 만들 때: `dotnet publish src/DataAnalyzer.App -c Release -r win-x64 --self-contained true -p:Version=0.3.0 -o out/DataAnalyzer/app`
  (런타임 패키지를 nuget.org에서 받아야 하므로 회사 네트워크에서 막히면 위의 GitHub Actions 방법을 쓰세요.)
- `v0.3.0` 같은 태그를 만들면 GitHub **Releases**에도 zip이 자동으로 올라갑니다.
