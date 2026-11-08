@echo off
rem DataAnalyzer 실행기
rem 공유 폴더의 app 폴더를 이 PC(%%LOCALAPPDATA%%\DataAnalyzer\app)로 복사한 뒤 실행합니다.
rem 바뀐 파일만 복사하므로 두 번째부터는 빠르고, 공유 폴더의 파일을 잠그지 않아 언제든 새 버전으로 바꿀 수 있습니다.
chcp 65001 >nul
setlocal
set "SRC=%~dp0app"
set "DST=%LOCALAPPDATA%\DataAnalyzer\app"
if not exist "%SRC%\DataAnalyzer.exe" (
  echo 공유 폴더에서 app\DataAnalyzer.exe 를 찾을 수 없습니다: "%SRC%"
  pause
  exit /b 1
)
robocopy "%SRC%" "%DST%" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo 복사에 실패했습니다. 프로그램이 이미 실행 중이면 닫고 다시 시도하세요.
  pause
  exit /b 1
)
start "" "%DST%\DataAnalyzer.exe" %*
