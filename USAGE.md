# Windget 사용법

Current public version: `v0.2.0`

Windget은 Windows 데스크톱 위에서 메모, 시스템 상태, Sound Mixer, 캘린더, 타이머/스톱워치, Quick Launcher를 배치해 사용하는 WPF 위젯 앱입니다.

## 설치

### 권장: MSI 설치

1. GitHub Releases에서 `Windget-v0.2.0-win-x64.msi`를 다운로드합니다.
2. MSI를 실행해 설치합니다.
3. 설치 후 시작 메뉴 또는 설치 폴더의 `Windget.exe`를 실행합니다.

MSI 설치 방식은 현재 사용자 영역의 `%LOCALAPPDATA%\Programs\Windget`에 설치합니다. 이전 버전 또는 같은 버전의 이전 MSI 빌드를 자동으로 제거한 뒤 새 빌드를 설치하며, Windows 작업관리자 시작프로그램 화면에서도 앱 이름과 아이콘이 `Windget`으로 인식되도록 실행 파일 메타데이터를 포함합니다.

### 대안: ZIP 수동 설치

1. GitHub Releases에서 `Windget-v0.2.0-win-x64.zip`을 다운로드합니다.
2. 원하는 폴더에 압축을 풉니다.
3. `Windget.exe`를 실행합니다.

ZIP 방식은 설치 프로그램을 쓰지 않는 휴대용 배포입니다. 폴더를 옮겼다면 `Start With Windows`를 껐다가 다시 켜서 시작프로그램 경로를 갱신하세요.

## 시작프로그램

`Control Center`의 `Start With Windows`를 켜면 Windget이 Windows 로그인 시 자동 실행됩니다.

- 우선적으로 Windows 로그온 예약 작업으로 등록해 시작 우선순위를 높입니다.
- 예약 작업 등록이 실패하면 기존 Run 등록 방식으로 대체합니다.
- 예전 `WindgetApp.exe` 기반 시작프로그램 항목이 남아 있으면 자동으로 정리합니다.

## 기본 조작

- 위젯 제목 영역을 드래그하면 위치를 옮길 수 있습니다.
- 위젯 오른쪽 아래 모서리를 드래그하면 크기를 조절할 수 있습니다.
- `Control Center`의 `Auto`는 현재 해상도에 맞춰 위젯을 자동 배치합니다.
- `Save`는 현재 위젯 위치, 크기, 투명도, 표시 상태를 저장합니다.
- 트레이 아이콘으로 Control Center를 열거나 앱을 다시 표시할 수 있습니다.

## Memo

- 메모는 제목과 내용을 바로 편집할 수 있습니다.
- `Open / Done` 상태를 바꿀 수 있습니다.
- 메모별 자동 초기화 조건을 설정할 수 있습니다.
- 초기화 모드: `After Done`, `At Time`, `Weekly`, `Monthly`

## System

- CPU, Memory, GPU, Network, App Memory를 표시합니다.
- Network Down/Up 속도는 `KB/s`, `MB/s`, `GB/s`로 자동 변환됩니다.
- CPU / Memory / GPU 그래프를 제공합니다.

## Sound Mixer

- 전체 볼륨과 음소거를 조절합니다.
- 앱별 오디오 세션 볼륨과 음소거를 조절합니다.
- 기본 재생 장치와 녹음 장치를 변경합니다.
- 앱별 출력 장치 라우팅은 안정성을 위해 Windows App Volume Settings 화면에서 설정합니다.
- 시스템 오디오 세션 이름과 게임/보호된 프로세스 아이콘 인식이 개선되어 있습니다.

## Calendar

- 날짜별 일정을 등록하고 확인할 수 있습니다.
- 일정이 있는 날짜는 옅은 강조 색으로 표시됩니다.
- 앱이 실행 중인 상태에서 날짜가 바뀌면 선택 날짜가 자동으로 오늘로 이동합니다.

## Timer / Stopwatch

- Timer와 Stopwatch 모드를 지원합니다.
- Timer 종료 시 Windows 알림을 표시할 수 있습니다.
- 시간 선택은 스크롤형 선택 UI를 사용합니다.

## Quick Launcher

- 파일, 폴더, 바로가기를 드래그 앤 드롭으로 등록합니다.
- 카테고리를 직접 만들 수 있습니다.
- `Icon Only` 모드로 아이콘 중심 표시가 가능합니다.
- 삭제 아이콘은 Quick Launcher 설정이 열려 있을 때만 표시됩니다.

## 소스에서 실행

```powershell
cd WindgetApp
dotnet build
dotnet run
```

배포 파일 생성:

```powershell
dotnet publish WindgetApp\WindgetApp.csproj -c Release -r win-x64 --self-contained false -o release\Windget-v0.2.0-win-x64
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Msi.ps1
```

## AI 사용 명시

이 프로젝트는 기획, 구현, 문서화 과정에서 AI 도구의 도움을 받아 제작되었습니다. 최종 기능 구성과 개선 방향은 사용자 피드백을 바탕으로 조정되었습니다.
